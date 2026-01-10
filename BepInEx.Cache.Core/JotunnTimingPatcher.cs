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
		private static readonly object TimingLock = new object();
		private static readonly System.Collections.Generic.Dictionary<string, int> CallCounts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

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

				var dt = __originalMethod?.DeclaringType;
				var name = (dt != null ? dt.Name : "<?>") + "." + (__originalMethod != null ? __originalMethod.Name : "<?>");

				var shouldLog = CacheConfig.VerboseDiagnostics;
				var callIndex = 0;
				lock (TimingLock)
				{
					CallCounts.TryGetValue(name, out callIndex);
					callIndex++;
					CallCounts[name] = callIndex;
				}

				// На cache-hit логируем первые вызовы даже если они короткие — чтобы понять полный порядок.
				if (!shouldLog)
				{
					if (CacheManager.RestoreModeActive && callIndex <= 3)
						shouldLog = true;
					else if (ms >= 10)
						shouldLog = true;
				}

				if (!shouldLog)
					return;

				_log?.LogMessage($"CacheFork: DIAG timing {name} = {ms} мс (call #{callIndex}).");
			}
			catch
			{
			}
		}
	}
}
