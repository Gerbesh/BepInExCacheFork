using System;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class ConfigCompatibilityPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Config.Compatibility";
		private static readonly object PatchLock = new object();
		private static bool _patched;
		private static ManualLogSource _log;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_patched)
					return;

				_log = log ?? _log;

				try
				{
					var method = AccessTools.Method(typeof(ConfigDefinition), "CheckInvalidConfigChars");
					if (method == null)
						return;

					var harmony = new Harmony(HarmonyId);
					harmony.Patch(
						method,
						prefix: new HarmonyMethod(typeof(ConfigCompatibilityPatcher), nameof(CheckInvalidConfigCharsPrefix))
						{
							priority = Priority.First
						});
					_patched = true;
					_log?.LogMessage("CacheFork: патч совместимости конфигов подключен (санитайз section/key, опционально).");
				}
				catch (Exception ex)
				{
					_log?.LogWarning($"CacheFork: не удалось подключить патч совместимости конфигов ({ex.Message}).");
				}
			}
		}

		private static bool CheckInvalidConfigCharsPrefix(ref string val, string name)
		{
			try
			{
				if (!CacheConfig.EnableCache || !CacheConfig.SanitizeInvalidConfigChars)
					return true;

				if (val == null)
				{
					val = string.Empty;
					return false;
				}

				var trimmed = val.Trim();
				if (trimmed.Length == 0)
				{
					val = string.Empty;
					return false;
				}

				var sanitized = Sanitize(trimmed);
				if (!string.Equals(trimmed, sanitized, StringComparison.Ordinal))
				{
					val = sanitized;
					if (CacheConfig.VerboseDiagnostics)
						_log?.LogWarning($"CacheFork: санитайз config {name}: \"{trimmed}\" -> \"{sanitized}\".");
				}
				else
				{
					val = trimmed;
				}

				return false; // skip original validation
			}
			catch
			{
				return true;
			}
		}

		private static string Sanitize(string input)
		{
			if (string.IsNullOrEmpty(input))
				return string.Empty;

			var sb = new StringBuilder(input.Length);
			for (var i = 0; i < input.Length; i++)
			{
				var c = input[i];
				switch (c)
				{
					case '=':
					case '\n':
					case '\t':
					case '\\':
					case '"':
					case '\'':
					case '[':
					case ']':
						sb.Append('_');
						break;
					default:
						sb.Append(c);
						break;
				}
			}

			// Убираем лишние подчёркивания по краям (когда секция/key состоят только из "мусора").
			var result = sb.ToString().Trim('_').Trim();
			return result.Length == 0 ? "unnamed" : result;
		}
	}
}

