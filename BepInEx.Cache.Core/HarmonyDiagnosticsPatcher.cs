using System;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class HarmonyDiagnosticsPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Diagnostics.Harmony";
		private static readonly object PatchLock = new object();
		private static bool _patched;
		private static ManualLogSource _log;
		private static int _unpatchAllCount;

		internal static bool IsInitialized => _patched;

		internal static void Initialize(ManualLogSource log)
		{
			if (_patched || !CacheConfig.VerboseDiagnostics)
				return;

			lock (PatchLock)
			{
				if (_patched || !CacheConfig.VerboseDiagnostics)
					return;

				_log = log ?? _log;

				try
				{
					var harmony = new Harmony(HarmonyId);
					var unpatchAll = AccessTools.Method(typeof(Harmony), nameof(Harmony.UnpatchAll), new[] { typeof(string) });
					if (unpatchAll != null)
					{
						harmony.Patch(
							unpatchAll,
							prefix: new HarmonyMethod(typeof(HarmonyDiagnosticsPatcher), nameof(UnpatchAllPrefix))
							{
								priority = Priority.First
							});
						_patched = true;
						_log?.LogMessage("CacheFork: DIAG патч Harmony.UnpatchAll подключен.");
					}
				}
				catch (Exception ex)
				{
					_log?.LogWarning($"CacheFork: не удалось подключить DIAG патч Harmony ({ex.Message}).");
				}
			}
		}

		private static void UnpatchAllPrefix(string harmonyID)
		{
			_unpatchAllCount++;
			if (_unpatchAllCount > 5)
				return;

			try
			{
				_log?.LogWarning($"CacheFork: DIAG Harmony.UnpatchAll вызван (harmonyID=\"{harmonyID ?? string.Empty}\").");

				var st = new StackTrace(2, true);
				var frames = st.GetFrames();
				if (frames == null || frames.Length == 0)
					return;

				_log?.LogWarning("CacheFork: DIAG Harmony.UnpatchAll stack (top):");
				var limit = Math.Min(frames.Length, 12);
				for (var i = 0; i < limit; i++)
				{
					var m = frames[i].GetMethod();
					var dt = m?.DeclaringType;
					var asm = dt?.Assembly;
					var asmName = string.Empty;
					try { asmName = asm?.GetName()?.Name ?? string.Empty; } catch { }

					var line =
						"  #" + i + " " +
						asmName + " " +
						(dt != null ? dt.FullName : "<?>") +
						"::" + (m != null ? m.Name : "<?>");

					var file = frames[i].GetFileName();
					if (!string.IsNullOrEmpty(file))
						line += " @ " + file + ":" + frames[i].GetFileLineNumber();

					_log?.LogWarning(line);
				}
			}
			catch
			{
			}
		}
	}
}

