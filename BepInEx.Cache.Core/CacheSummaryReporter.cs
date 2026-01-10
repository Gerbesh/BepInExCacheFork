using System;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BepInEx.Cache.Core
{
	internal static class CacheSummaryReporter
	{
		private static readonly object LockObj = new object();
		private static bool _installed;
		private static bool _printed;
		private static ManualLogSource _log;

		internal static void Install(ManualLogSource log)
		{
			lock (LockObj)
			{
				if (_installed)
					return;
				_installed = true;
				_log = log;

				try
				{
					var harmony = new Harmony("BepInEx.CacheFork.Summary");

					// Valheim: FejdStartup - главный экран.
					TryPatchTypeMethod(harmony, "FejdStartup", "Awake");
					TryPatchTypeMethod(harmony, "FejdStartup", "Start");
				}
				catch (Exception ex)
				{
					_log?.LogWarning($"CacheFork: не удалось установить summary-репортер ({ex.Message}).");
				}

				try
				{
					var runner = CacheForkUnityRunner.Ensure(_log);
					if (runner != null)
						runner.StartRoutine(DelayedPrintAsync(90f));
				}
				catch
				{
				}
			}
		}

		internal static void PrintOnce(string reason)
		{
			lock (LockObj)
			{
				if (_printed)
					return;
				_printed = true;
			}

			try
			{
				var header = "CacheFork: timing summary (" + (reason ?? "unknown") + "):";
				CacheMetrics.LogSummary(_log, header, top: 20, minMs: 5);

				LogExtractedSummary(_log);
			}
			catch
			{
			}
		}

		private static void LogExtractedSummary(ManualLogSource log)
		{
			try
			{
				var root = ExtractedAssetCache.GetRoot();
				if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
					return;

				var bytes = GetDirectorySizeBytes(root);
				var mapPath = ExtractedAssetCache.GetResourceMapPath();
				var mapLines = CountNonEmptyLines(mapPath);

				log?.LogMessage(string.Format(CultureInfo.InvariantCulture,
					"CacheFork: extracted summary: size={0:0.00} GB, resource-map={1} lines, root={2}",
					bytes / (1024.0 * 1024 * 1024), mapLines, root));
			}
			catch
			{
			}
		}

		private static long GetDirectorySizeBytes(string root)
		{
			long sum = 0;
			foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
			{
				try { sum += new FileInfo(file).Length; }
				catch { }
			}
			return sum;
		}

		private static int CountNonEmptyLines(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return 0;

			try
			{
				var count = 0;
				foreach (var line in File.ReadAllLines(path))
				{
					if (string.IsNullOrEmpty(line))
						continue;
					if (line.StartsWith("#", StringComparison.Ordinal))
						continue;
					count++;
				}
				return count;
			}
			catch
			{
				return 0;
			}
		}

		private static void TryPatchTypeMethod(Harmony harmony, string typeName, string methodName)
		{
			var t = AccessTools.TypeByName(typeName);
			if (t == null)
				return;

			var m = AccessTools.Method(t, methodName);
			if (m == null)
				return;

			harmony.Patch(m, postfix: new HarmonyMethod(typeof(CacheSummaryReporter), nameof(SummaryPostfix)));
		}

		private static void SummaryPostfix()
		{
			PrintOnce("menu");
		}

		private static System.Collections.IEnumerator DelayedPrintAsync(float seconds)
		{
			yield return new WaitForSeconds(seconds);
			PrintOnce("timeout");
		}
	}
}
