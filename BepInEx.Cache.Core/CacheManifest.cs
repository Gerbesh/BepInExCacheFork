using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public sealed class CacheManifest
	{
		public const string CurrentFormatVersion = "1";

		public string Fingerprint { get; set; }
		public string GameExecutable { get; set; }
		public string UnityVersion { get; set; }
		public string UnityVersionExe { get; set; }
		public string CacheFormatVersion { get; set; }
		public bool IsComplete { get; set; }
		public string CreatedUtc { get; set; }
		public string CompletedUtc { get; set; }

		public static string DefaultFileName => "manifest.txt";
		public static string JsonAliasFileName => "manifest.json";

		public static CacheManifest Load(string path, ManualLogSource log)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;

			var manifest = new CacheManifest();

			try
			{
				foreach (var line in File.ReadAllLines(path))
				{
					if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
						continue;
					if (line.StartsWith("#", StringComparison.Ordinal))
						continue;

					var separatorIndex = line.IndexOf('=');
					if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
						continue;

					var key = line.Substring(0, separatorIndex).Trim();
					var value = line.Substring(separatorIndex + 1).Trim();

					switch (key)
					{
						case "Fingerprint":
							manifest.Fingerprint = value;
							break;
						case "GameExecutable":
							manifest.GameExecutable = value;
							break;
						case "UnityVersion":
							manifest.UnityVersion = value;
							break;
						case "UnityVersionExe":
							manifest.UnityVersionExe = value;
							break;
					case "CacheFormatVersion":
						manifest.CacheFormatVersion = value;
						break;
					case "IsComplete":
						if (bool.TryParse(value, out var parsedIsComplete))
							manifest.IsComplete = parsedIsComplete;
						break;
					case "CreatedUtc":
						manifest.CreatedUtc = value;
						break;
					case "CompletedUtc":
						manifest.CompletedUtc = value;
							break;
					}
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"Не удалось прочитать манифест кеша: {ex.Message}");
				return null;
			}

			return manifest;
		}

		public void Save(string path, ManualLogSource log)
		{
			if (string.IsNullOrEmpty(path))
				return;

			try
			{
				var lines = new List<string>
				{
					"Fingerprint=" + (Fingerprint ?? string.Empty),
					"GameExecutable=" + (GameExecutable ?? string.Empty),
					"UnityVersion=" + (UnityVersion ?? string.Empty),
					"UnityVersionExe=" + (UnityVersionExe ?? string.Empty),
					"CacheFormatVersion=" + (CacheFormatVersion ?? string.Empty),
					"IsComplete=" + IsComplete.ToString(),
					"CreatedUtc=" + (CreatedUtc ?? string.Empty),
					"CompletedUtc=" + (CompletedUtc ?? string.Empty)
				};

				File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
			}
			catch (Exception ex)
			{
				log?.LogWarning($"Не удалось записать манифест кеша: {ex.Message}");
			}
		}
	}
}
