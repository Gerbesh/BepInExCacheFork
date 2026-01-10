using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	internal static class JotunnLocalizationCache
	{
		private const int FileCacheVersion = 1;

		internal static bool TryLoadFileCache(string sourcePath, bool isJson, out Dictionary<string, Dictionary<string, string>> translations, ManualLogSource log)
		{
			translations = null;
			if (!LocalizationCache.IsEnabled || string.IsNullOrEmpty(sourcePath))
				return false;

			if (!File.Exists(sourcePath))
				return false;

			var info = new FileInfo(sourcePath);
			var cachePath = GetFileCachePath(sourcePath, info.Length, info.LastWriteTimeUtc.Ticks, isJson);
			if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
				return false;

			try
			{
				using (var stream = File.OpenRead(cachePath))
				using (var reader = new BinaryReader(stream, Encoding.UTF8))
				{
					var version = reader.ReadInt32();
					if (version != FileCacheVersion)
						return false;

					var storedSize = reader.ReadInt64();
					var storedTicks = reader.ReadInt64();
					var storedIsJson = reader.ReadBoolean();
					if (storedSize != info.Length || storedTicks != info.LastWriteTimeUtc.Ticks || storedIsJson != isJson)
						return false;

					var languageCount = reader.ReadInt32();
					if (languageCount <= 0 || languageCount > 256)
						return false;

					var result = new Dictionary<string, Dictionary<string, string>>(languageCount);
					for (var i = 0; i < languageCount; i++)
					{
						var language = reader.ReadString();
						var entryCount = reader.ReadInt32();
						if (entryCount < 0 || entryCount > 200000)
							throw new InvalidDataException("слишком большой кеш локализации Jotunn");

						var entries = new Dictionary<string, string>(entryCount);
						for (var j = 0; j < entryCount; j++)
						{
							var key = reader.ReadString();
							var value = reader.ReadString();
							if (string.IsNullOrEmpty(key))
								continue;
							entries[key] = value ?? string.Empty;
						}

						if (!string.IsNullOrEmpty(language))
							result[language] = entries;
					}

					if (result.Count == 0)
						return false;

					translations = result;
					log?.LogMessage($"CacheFork: Jotunn локализация из кеша ({Path.GetFileName(sourcePath)}).");
					return true;
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось прочитать кеш Jotunn локализации ({ex.Message}).");
				return false;
			}
		}

		internal static void SaveFileCache(string sourcePath, bool isJson, Dictionary<string, Dictionary<string, string>> translations, ManualLogSource log)
		{
			if (!LocalizationCache.IsEnabled || string.IsNullOrEmpty(sourcePath) || translations == null || translations.Count == 0)
				return;

			if (!File.Exists(sourcePath))
				return;

			var info = new FileInfo(sourcePath);
			var cachePath = GetFileCachePath(sourcePath, info.Length, info.LastWriteTimeUtc.Ticks, isJson);
			if (string.IsNullOrEmpty(cachePath))
				return;

			try
			{
				var directory = Path.GetDirectoryName(cachePath);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				using (var stream = File.Open(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read))
				using (var writer = new BinaryWriter(stream, Encoding.UTF8))
				{
					writer.Write(FileCacheVersion);
					writer.Write(info.Length);
					writer.Write(info.LastWriteTimeUtc.Ticks);
					writer.Write(isJson);
					writer.Write(translations.Count);

					foreach (var languageEntry in translations)
					{
						writer.Write(languageEntry.Key ?? string.Empty);
						var entries = languageEntry.Value ?? new Dictionary<string, string>();
						writer.Write(entries.Count);
						foreach (var entry in entries)
						{
							writer.Write(entry.Key ?? string.Empty);
							writer.Write(entry.Value ?? string.Empty);
						}
					}
				}

				log?.LogMessage($"CacheFork: Jotunn локализация сохранена ({Path.GetFileName(sourcePath)}).");
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось сохранить кеш Jotunn локализации ({ex.Message}).");
			}
		}

		private static string GetFileCachePath(string sourcePath, long size, long writeTicks, bool isJson)
		{
			var root = GetJotunnCacheRoot();
			if (string.IsNullOrEmpty(root))
				return null;

			var key = $"{sourcePath}|{size.ToString(CultureInfo.InvariantCulture)}|{writeTicks.ToString(CultureInfo.InvariantCulture)}|{(isJson ? "1" : "0")}";
			var hash = ComputeHash(key);
			return Path.Combine(root, $"file_{hash}.bin");
		}

		private static string GetJotunnCacheRoot()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			var localizationRoot = Path.Combine(cacheRoot, "localization");
			var processRoot = Path.Combine(localizationRoot, processName);
			return Path.Combine(processRoot, "jotunn");
		}

		private static string ComputeHash(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "empty";

			using (var sha = SHA256.Create())
			{
				var bytes = Encoding.UTF8.GetBytes(value);
				var hash = sha.ComputeHash(bytes);
				var builder = new StringBuilder(hash.Length * 2);
				foreach (var b in hash)
					builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
				return builder.ToString();
			}
		}
	}
}
