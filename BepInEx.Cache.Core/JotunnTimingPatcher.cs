using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class JotunnTimingPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jotunn.Timing";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static ManualLogSource _log;

		internal static void Initialize(ManualLogSource log, Assembly jotunnAssembly)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;
				_initialized = true;

				_log = log ?? _log;

				if (!CacheManager.RestoreModeActive && !CacheConfig.VerboseDiagnostics)
					return;

				try
				{
					var harmony = new Harmony(HarmonyId);
					PatchTypeMethods(harmony, jotunnAssembly, "Jotunn.Managers.ItemManager",
						"RegisterCustomItems",
						"RegisterCustomRecipes",
						"RegisterCustomStatusEffects",
						"RegisterCustomItemConversions",
						"UpdateRegistersSafe");

					PatchTypeMethods(harmony, jotunnAssembly, "Jotunn.Managers.PrefabManager",
						"RegisterAllToZNetScene",
						"RegisterToZNetScene");

					PatchTypeMethods(harmony, jotunnAssembly, "Jotunn.Managers.PieceManager",
						"RegisterCustomPiece",
						"RegisterInPieceTables",
						"LoadPieceTables",
						"ReorderAllCategoryPieces",
						"UpdatePieceTableCategories");

					_log?.LogMessage("CacheFork: Jotunn timing-патчи подключены.");
				}
				catch (Exception ex)
				{
					_log?.LogWarning($"CacheFork: не удалось подключить timing-патчи Jotunn ({ex.Message}).");
				}
			}
		}

		private static void PatchTypeMethods(Harmony harmony, Assembly jotunnAssembly, string typeName, params string[] methodNames)
		{
			if (harmony == null || string.IsNullOrEmpty(typeName) || methodNames == null || methodNames.Length == 0)
				return;

			var type = FindType(jotunnAssembly, typeName);
			if (type == null)
				return;

			foreach (var name in methodNames)
			{
				try
				{
					var method = AccessTools.Method(type, name);
					if (method == null)
						continue;

					harmony.Patch(
						method,
						prefix: new HarmonyMethod(typeof(JotunnTimingPatcher), nameof(TimedPrefix))
						{
							priority = Priority.First
						},
						postfix: new HarmonyMethod(typeof(JotunnTimingPatcher), nameof(TimedPostfix))
						{
							priority = Priority.Last
						});
				}
				catch
				{
				}
			}
		}

		private static Type FindType(Assembly jotunnAssembly, string typeName)
		{
			try
			{
				if (jotunnAssembly != null)
					return jotunnAssembly.GetType(typeName, false);
			}
			catch
			{
			}

			return AccessTools.TypeByName(typeName);
		}

		private static void TimedPrefix(MethodBase __originalMethod, ref Stopwatch __state)
		{
			try
			{
				if (!CacheManager.RestoreModeActive && !CacheConfig.VerboseDiagnostics)
					return;

				__state = Stopwatch.StartNew();
			}
			catch
			{
			}
		}

		private static void TimedPostfix(MethodBase __originalMethod, Stopwatch __state)
		{
			try
			{
				if (__state == null)
					return;

				__state.Stop();
				var ms = __state.ElapsedMilliseconds;

				if (ms < 5 && !CacheConfig.VerboseDiagnostics)
					return;

				var dt = __originalMethod?.DeclaringType;
				var name = (dt != null ? dt.Name : "<?>") + "." + (__originalMethod != null ? __originalMethod.Name : "<?>");
				_log?.LogMessage($"CacheFork: DIAG timing {name} = {ms} мс.");
			}
			catch
			{
			}
		}
	}
}

