using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class LocalizationCache
	{
		private const int RuntimeCacheVersion = 1;
		private const string RuntimeCacheFolderName = "runtime";
		private static bool _xunityChecked;
		private static bool _xunityPresent;

		public static bool IsEnabled => CacheConfig.EnableCache && CacheConfig.EnableLocalizationCache;

		public static bool IsReady(ManualLogSource log)
		{
			if (!CacheConfig.EnableCache || !CacheConfig.EnableLocalizationCache)
				return true;

			var translationRoot = GetTranslationRoot();
			var hasLocalizationFiles = HasLocalizationFiles(translationRoot);
			var autoTranslatorConfigPath = GetAutoTranslatorConfigPathIfAvailable();
			if (!hasLocalizationFiles && string.IsNullOrEmpty(autoTranslatorConfigPath))
				return true;

			var cacheRoot = GetCacheRoot();
			if (string.IsNullOrEmpty(cacheRoot))
				return false;

			var manifestPath = GetManifestPath(cacheRoot);
			if (!File.Exists(manifestPath))
				return false;

			try
			{
				foreach (var line in File.ReadAllLines(manifestPath))
				{
					if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
						continue;

					var parts = line.Split('|');
					if (parts.Length < 2)
						continue;

					var relativePath = parts[0];
					var sizeText = parts[1];
					if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedSize))
						continue;

					var cachedPath = Path.Combine(cacheRoot, relativePath);
					if (!File.Exists(cachedPath))
						return false;

					var info = new FileInfo(cachedPath);
					if (info.Length != expectedSize)
						return false;
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось проверить кеш локализации ({ex.Message}).");
				return false;
			}

			return true;
		}

		public static void Build(ManualLogSource log)
		{
			if (!CacheConfig.EnableCache || !CacheConfig.EnableLocalizationCache)
				return;

			var cacheRoot = GetCacheRoot();
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			var translationRoot = GetTranslationRoot();
			Directory.CreateDirectory(cacheRoot);

			var manifestLines = new List<string>();
			long totalBytes = 0;
			var count = 0;

			if (!string.IsNullOrEmpty(translationRoot) && Directory.Exists(translationRoot))
			{
				foreach (var file in Directory.GetFiles(translationRoot, "*", SearchOption.AllDirectories))
				{
					var relativePath = GetRelativePath(translationRoot, file);
					if (string.IsNullOrEmpty(relativePath))
						continue;

					var destPath = Path.Combine(cacheRoot, relativePath);
					var destDir = Path.GetDirectoryName(destPath);
					if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
						Directory.CreateDirectory(destDir);

					try
					{
						var sourceInfo = new FileInfo(file);
						var copyNeeded = true;

						if (File.Exists(destPath))
						{
							var destInfo = new FileInfo(destPath);
							if (destInfo.Length == sourceInfo.Length)
								copyNeeded = false;
						}

						if (copyNeeded)
							File.Copy(file, destPath, true);

						manifestLines.Add($"{relativePath}|{sourceInfo.Length.ToString(CultureInfo.InvariantCulture)}");
						totalBytes += sourceInfo.Length;
						count++;
					}
					catch (Exception ex)
					{
						log?.LogWarning($"CacheFork: не удалось кешировать перевод {file} ({ex.Message}).");
					}
				}
			}

			var autoTranslatorConfigPath = GetAutoTranslatorConfigPathIfAvailable();
			if (!string.IsNullOrEmpty(autoTranslatorConfigPath))
			{
				var relativePath = Path.Combine("xunity", "AutoTranslatorConfig.ini");
				var destPath = Path.Combine(cacheRoot, relativePath);
				var destDir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
					Directory.CreateDirectory(destDir);

				try
				{
					var sourceInfo = new FileInfo(autoTranslatorConfigPath);
					var copyNeeded = true;

					if (File.Exists(destPath))
					{
						var destInfo = new FileInfo(destPath);
						if (destInfo.Length == sourceInfo.Length)
							copyNeeded = false;
					}

					if (copyNeeded)
						File.Copy(autoTranslatorConfigPath, destPath, true);

					manifestLines.Add($"{relativePath}|{sourceInfo.Length.ToString(CultureInfo.InvariantCulture)}");
					totalBytes += sourceInfo.Length;
					count++;
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось кешировать AutoTranslatorConfig.ini ({ex.Message}).");
				}
			}

			if (count == 0)
			{
				log?.LogMessage("CacheFork: файлы локализации для кеширования не найдены.");
				return;
			}

			try
			{
				File.WriteAllLines(GetManifestPath(cacheRoot), manifestLines.ToArray());
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось записать манифест локализации ({ex.Message}).");
			}

			log?.LogMessage($"CacheFork: кеш локализации обновлён (файлов: {count}, размер: {FormatBytes(totalBytes)}).");
		}

		internal static IEnumerable<string> EnumerateLocalizationFiles(string rootPath)
		{
			if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
				yield break;

			foreach (var file in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories))
				yield return file;
		}

		internal static string GetAutoTranslatorConfigPathIfAvailable()
		{
			if (!IsXUnityAutoTranslatorPresent())
				return null;

			var configPath = GetAutoTranslatorConfigPath();
			if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
				return null;

			return configPath;
		}

		private static string GetTranslationRoot()
		{
			if (string.IsNullOrEmpty(Paths.BepInExRootPath))
				return null;

			return Path.Combine(Paths.BepInExRootPath, "Translation");
		}

		private static bool IsXUnityAutoTranslatorPresent()
		{
			if (_xunityChecked)
				return _xunityPresent;

			_xunityChecked = true;

			var pluginRoot = Paths.PluginPath;
			if (!string.IsNullOrEmpty(pluginRoot) && Directory.Exists(pluginRoot))
			{
				if (Directory.Exists(Path.Combine(pluginRoot, "XUnity.AutoTranslator")))
				{
					_xunityPresent = true;
					return true;
				}

				var pluginDlls = Directory.GetFiles(pluginRoot, "XUnity.AutoTranslator*.dll", SearchOption.AllDirectories);
				if (pluginDlls.Length > 0)
				{
					_xunityPresent = true;
					return true;
				}
			}

			_xunityPresent = false;
			return false;
		}

		private static string GetAutoTranslatorConfigPath()
		{
			if (string.IsNullOrEmpty(Paths.BepInExRootPath))
				return null;

			var configDir = Path.Combine(Paths.BepInExRootPath, "config");
			return Path.Combine(configDir, "AutoTranslatorConfig.ini");
		}

		private static bool HasLocalizationFiles(string translationRoot)
		{
			if (string.IsNullOrEmpty(translationRoot) || !Directory.Exists(translationRoot))
				return false;

			try
			{
				return Directory.GetFiles(translationRoot, "*", SearchOption.AllDirectories).Length > 0;
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryLoadRuntimeDictionary(string language, out Dictionary<string, string> translations, ManualLogSource log)
		{
			translations = null;
			if (!IsEnabled || string.IsNullOrEmpty(language))
				return false;

			var path = GetRuntimeCachePath(language);
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return false;

			try
			{
				using (var stream = File.OpenRead(path))
				using (var reader = new BinaryReader(stream, Encoding.UTF8))
				{
					var version = reader.ReadInt32();
					if (version != RuntimeCacheVersion)
						return false;

					var count = reader.ReadInt32();
					if (count <= 0)
						return false;
					if (count > 1000000)
						throw new InvalidDataException("слишком большой кеш локализации");

					var result = new Dictionary<string, string>(count);
					for (var i = 0; i < count; i++)
					{
						var key = reader.ReadString();
						var value = reader.ReadString();
						if (string.IsNullOrEmpty(key))
							continue;
						result[key] = value ?? string.Empty;
					}

					if (result.Count == 0)
						return false;

					translations = result;
					log?.LogMessage($"CacheFork: локализация \"{language}\" загружена из кеша (ключей: {result.Count}).");
					return true;
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось загрузить кеш локализации \"{language}\" ({ex.Message}).");
				return false;
			}
		}

		internal static void SaveRuntimeDictionary(string language, Dictionary<string, string> translations, ManualLogSource log)
		{
			if (!IsEnabled || string.IsNullOrEmpty(language) || translations == null || translations.Count == 0)
				return;

			var path = GetRuntimeCachePath(language);
			if (string.IsNullOrEmpty(path))
				return;

			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read))
				using (var writer = new BinaryWriter(stream, Encoding.UTF8))
				{
					writer.Write(RuntimeCacheVersion);
					writer.Write(translations.Count);
					foreach (var pair in translations)
					{
						writer.Write(pair.Key ?? string.Empty);
						writer.Write(pair.Value ?? string.Empty);
					}
				}

				log?.LogMessage($"CacheFork: локализация \"{language}\" сохранена в кеш (ключей: {translations.Count}).");
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось сохранить кеш локализации \"{language}\" ({ex.Message}).");
			}
		}

		private static string GetCacheRoot()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			var localizationRoot = Path.Combine(cacheRoot, "localization");
			return Path.Combine(localizationRoot, processName);
		}

		private static string GetManifestPath(string cacheRoot)
		{
			return Path.Combine(cacheRoot, "localization_manifest.txt");
		}

		private static string GetRuntimeCachePath(string language)
		{
			var root = GetRuntimeCacheRoot();
			if (string.IsNullOrEmpty(root))
				return null;

			var fileName = SanitizeFileName(language) + ".bin";
			return Path.Combine(root, fileName);
		}

		private static string GetRuntimeCacheRoot()
		{
			var cacheRoot = GetCacheRoot();
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			return Path.Combine(cacheRoot, RuntimeCacheFolderName);
		}

		private static string SanitizeFileName(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "unknown";

			var chars = value.ToCharArray();
			var invalid = Path.GetInvalidFileNameChars();
			for (var i = 0; i < chars.Length; i++)
			{
				var current = chars[i];
				for (var j = 0; j < invalid.Length; j++)
				{
					if (current == invalid[j])
					{
						chars[i] = '_';
						break;
					}
				}
			}
			return new string(chars);
		}

		private static string GetRelativePath(string rootPath, string fullPath)
		{
			try
			{
				var rootUri = new Uri(AppendDirectorySeparator(rootPath));
				var fileUri = new Uri(fullPath);
				var relative = rootUri.MakeRelativeUri(fileUri).ToString();
				return Uri.UnescapeDataString(relative.Replace('/', Path.DirectorySeparatorChar));
			}
			catch
			{
				return null;
			}
		}

		private static string AppendDirectorySeparator(string path)
		{
			if (string.IsNullOrEmpty(path))
				return path;
			if (path[path.Length - 1] == Path.DirectorySeparatorChar)
				return path;
			return path + Path.DirectorySeparatorChar;
		}

		private static string FormatBytes(long bytes)
		{
			const double kb = 1024;
			const double mb = kb * 1024;
			const double gb = mb * 1024;

			if (bytes >= gb)
				return (bytes / gb).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
			if (bytes >= mb)
				return (bytes / mb).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
			if (bytes >= kb)
				return (bytes / kb).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
			return bytes.ToString(CultureInfo.InvariantCulture) + " B";
		}
	}
}
