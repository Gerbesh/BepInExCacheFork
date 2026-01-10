using System;
using System.Collections;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class ValheimCraftingDiagnosticsPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Valheim.CraftingDiag";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _patched;
		private static AssemblyLoadEventHandler _assemblyHandler;
		private static bool _assemblyHooked;
		private static ManualLogSource _log;
		private static int _logged;
		private static bool _suppressedLogged;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;

				_initialized = true;
				_log = log ?? _log;

				TryPatchNowOrDefer();
			}
		}

		private static void TryPatchNowOrDefer()
		{
			TryPatch();

			if (_patched)
				return;

			if (_assemblyHooked)
				return;

			_assemblyHandler = (sender, args) =>
			{
				try
				{
					TryPatch();
					if (_patched)
					{
						AppDomain.CurrentDomain.AssemblyLoad -= _assemblyHandler;
						_assemblyHooked = false;
					}
				}
				catch
				{
				}
			};
			AppDomain.CurrentDomain.AssemblyLoad += _assemblyHandler;
			_assemblyHooked = true;
			_log?.LogMessage("CacheFork: DIAG крафта отложен до загрузки Player.");
		}

		private static void TryPatch()
		{
			try
			{
				var playerType = AccessTools.TypeByName("Player");
				if (playerType == null)
					return;

				var recipeType = AccessTools.TypeByName("Recipe");
				MethodInfo method = null;
				if (recipeType != null)
					method = AccessTools.Method(playerType, "HaveRequirements", new[] { recipeType, typeof(bool), typeof(int), typeof(int) });

				if (method == null)
					method = AccessTools.Method(playerType, "HaveRequirements");

				if (method == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					method,
					finalizer: new HarmonyMethod(typeof(ValheimCraftingDiagnosticsPatcher), nameof(HaveRequirementsFinalizer))
					{
						priority = Priority.Last
					});

				_log?.LogMessage("CacheFork: Valheim DIAG патч подключен (Player.HaveRequirements).");
				_patched = true;
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить DIAG-патч крафта ({ex.Message}).");
			}
		}

		private static Exception HaveRequirementsFinalizer(Exception __exception, object __instance, object[] __args, MethodBase __originalMethod)
		{
			try
			{
				if (__exception == null)
					return null;

				if (!ShouldLog())
					return __exception;

				var sb = new StringBuilder(256);
				sb.Append("CacheFork: DIAG HaveRequirements exception: ");
				sb.Append(__exception.GetType().Name);
				if (!string.IsNullOrEmpty(__exception.Message))
					sb.Append(" - ").Append(__exception.Message);

				AppendPlayerInfo(sb, __instance);
				AppendRecipeInfo(sb, __args);
				AppendArgsInfo(sb, __args);
				AppendStackTop(sb, __exception);

				_log?.LogWarning(sb.ToString());
			}
			catch
			{
			}

			return __exception;
		}

		private static bool ShouldLog()
		{
			if (_log == null)
				return false;

			if (_logged < 5)
			{
				_logged++;
				return true;
			}

			if (!_suppressedLogged)
			{
				_suppressedLogged = true;
				_log.LogWarning("CacheFork: DIAG HaveRequirements: слишком много исключений, дальнейшие будут подавлены.");
			}

			return false;
		}

		private static void AppendPlayerInfo(StringBuilder sb, object player)
		{
			try
			{
				if (player == null)
				{
					sb.Append(" | player=null");
					return;
				}

				string name = null;
				var type = player.GetType();
				var getName = AccessTools.Method(type, "GetPlayerName");
				if (getName != null)
					name = getName.Invoke(player, null) as string;

				if (string.IsNullOrEmpty(name))
				{
					var field = type.GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (field != null)
						name = field.GetValue(player) as string;
				}

				sb.Append(" | player=").Append(string.IsNullOrEmpty(name) ? "<unknown>" : name);
			}
			catch
			{
				sb.Append(" | player=<error>");
			}
		}

		private static void AppendRecipeInfo(StringBuilder sb, object[] args)
		{
			var recipe = (args != null && args.Length > 0) ? args[0] : null;
			if (recipe == null)
			{
				sb.Append(" | recipe=null");
				return;
			}

			try
			{
				var item = TryGetMember(recipe, "m_item");
				var shared = TryGetMember(item, "m_shared");
				var name = TryGetMember(shared, "m_name") as string;
				var resources = TryGetMember(recipe, "m_resources");
				var resCount = GetCollectionCount(resources);

				sb.Append(" | recipe=");
				sb.Append(string.IsNullOrEmpty(name) ? "<unknown>" : name);
				sb.Append(" item=");
				sb.Append(item != null ? "ok" : "null");
				sb.Append(" shared=");
				sb.Append(shared != null ? "ok" : "null");
				sb.Append(" resources=");
				sb.Append(resCount.HasValue ? resCount.Value.ToString() : "n/a");
			}
			catch
			{
				sb.Append(" | recipe=<error>");
			}
		}

		private static void AppendArgsInfo(StringBuilder sb, object[] args)
		{
			if (args == null || args.Length == 0)
				return;

			try
			{
				var discover = args.Length > 1 ? args[1] : null;
				var quality = args.Length > 2 ? args[2] : null;
				var amount = args.Length > 3 ? args[3] : null;

				sb.Append(" | discover=").Append(discover ?? "n/a");
				sb.Append(" quality=").Append(quality ?? "n/a");
				sb.Append(" amount=").Append(amount ?? "n/a");
			}
			catch
			{
				sb.Append(" | args=<error>");
			}
		}

		private static void AppendStackTop(StringBuilder sb, Exception exception)
		{
			try
			{
				var stack = exception.StackTrace;
				if (string.IsNullOrEmpty(stack))
					return;

				var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
				if (lines.Length == 0)
					return;

				var limit = Math.Min(lines.Length, 3);
				sb.Append(" | stackTop=");
				for (var i = 0; i < limit; i++)
				{
					if (i > 0)
						sb.Append(" ; ");
					sb.Append(lines[i].Trim());
				}
			}
			catch
			{
			}
		}

		private static object TryGetMember(object instance, string memberName)
		{
			if (instance == null || string.IsNullOrEmpty(memberName))
				return null;

			try
			{
				var type = instance.GetType();
				var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null)
					return field.GetValue(instance);

				var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (prop != null && prop.CanRead)
					return prop.GetValue(instance, null);
			}
			catch
			{
			}

			return null;
		}

		private static int? GetCollectionCount(object value)
		{
			if (value == null)
				return null;

			try
			{
				if (value is ICollection col)
					return col.Count;

				var countProp = value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
				if (countProp != null && countProp.PropertyType == typeof(int))
					return (int)countProp.GetValue(value, null);
			}
			catch
			{
			}

			return null;
		}
	}
}
