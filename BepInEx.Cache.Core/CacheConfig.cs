using System;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class CacheConfig
	{
		private static readonly object InitLock = new object();
		private static bool _initialized;
		private static ConfigFile _config;
		private static ManualLogSource _log;

		private static ConfigEntry<bool> _enableCache;
		private static ConfigEntry<string> _cacheDir;
		private static ConfigEntry<bool> _validateStrict;
		private static ConfigEntry<string> _maxCacheSize;
		private static ConfigEntry<bool> _cacheAssets;
		private static ConfigEntry<bool> _cacheLocalization;
		private static ConfigEntry<bool> _cacheState;
		private static ConfigEntry<bool> _verboseDiagnostics;
		private static ConfigEntry<bool> _suppressPluginLoadLogs;
		private static ConfigEntry<string> _fingerprintMode;
		private static ConfigEntry<bool> _extractHeavyAssets;
		private static ConfigEntry<string> _extractDir;
		private static ConfigEntry<string> _preferredCompression;
		private static ConfigEntry<bool> _backgroundWarmup;
		private static ConfigEntry<string> _maxExtractSizeGb;

		public static bool EnableCache { get; private set; }
		public static bool EnableAssetsCache { get; private set; }
		public static bool EnableLocalizationCache { get; private set; }
		public static bool EnableStateCache { get; private set; }
		public static bool VerboseDiagnostics { get; private set; }
		public static bool SuppressPluginLoadLogs { get; private set; }
		public static string FingerprintMode { get; private set; }
		public static bool ExtractHeavyAssets { get; private set; }
		public static string ExtractDir { get; private set; }
		public static string PreferredCompression { get; private set; }
		public static bool BackgroundWarmup { get; private set; }
		public static long MaxExtractSizeBytes { get; private set; }
		public static string CacheDir { get; private set; }
		public static string CacheDirResolved { get; private set; }
		public static bool ValidateStrict { get; private set; }
		public static long MaxCacheSizeBytes { get; private set; }

		public static void Initialize(ManualLogSource logSource)
		{
			if (_initialized)
				return;

			lock (InitLock)
			{
				if (_initialized)
					return;

				_log = logSource ?? Logger.CreateLogSource("BepInEx.Cache");

				var rootPath = Paths.BepInExRootPath ?? ".";
				var configPath = Path.Combine(rootPath, "cache.cfg");

				_config = new ConfigFile(configPath, true);

				_enableCache = _config.Bind("Cache", "EnableCache", true, "Включает кеширование модов и ассетов.");
				_cacheDir = _config.Bind("Cache", "CacheDir", "auto", "Каталог кеша. auto = BepInEx/cache.");
				_validateStrict = _config.Bind("Cache", "ValidateStrict", true, "Строгая проверка изменений; при любом отличии кеш пересоздаётся.");
				_maxCacheSize = _config.Bind("Cache", "MaxCacheSize", "16GB", "Максимальный размер кеша.");
				_cacheAssets = _config.Bind("Cache", "CacheAssets", true, "Включает кеш ассетов (AssetBundles).");
				_cacheLocalization = _config.Bind("Cache", "CacheLocalization", true, "Включает кеш локализации (файлы перевода).");
				_cacheState = _config.Bind("Cache", "CacheState", true, "Включает кеш состояния модов (Jotunn registries).");
				_verboseDiagnostics = _config.Bind("Cache", "VerboseDiagnostics", false, "Избыточное логирование для диагностики (рекомендуется включать только на время поиска ошибок).");
				_suppressPluginLoadLogs = _config.Bind("Cache", "SuppressPluginLoadLogs", false, "Подавляет спам-логи \"Loading [Plugin]\" от Chainloader и выводит сводку одним сообщением. Не влияет на реальную загрузку плагинов.");
				_fingerprintMode = _config.Bind("Cache", "FingerprintMode", "Fast", "Режим вычисления fingerprint: Fast (размер+mtime) или Strict (читать весь файл). Strict может быть очень медленным на 100+ модах.");

				_extractHeavyAssets = _config.Bind("ExtractedAssets", "ExtractHeavyAssets", true, "Включает extracted-кеш тяжёлых ассетов (перепаковка AssetBundle в LZ4/Uncompressed для ускорения последующих запусков).");
				_extractDir = _config.Bind("ExtractedAssets", "ExtractDir", "BepInEx/cache/extracted_assets", "Каталог extracted-кеша. По умолчанию: BepInEx/cache/extracted_assets.");
				_preferredCompression = _config.Bind("ExtractedAssets", "PreferredCompression", "LZ4", "Предпочитаемая компрессия extracted-кеша: LZ4 или Uncompressed.");
				_backgroundWarmup = _config.Bind("ExtractedAssets", "BackgroundWarmup", true, "Фоновая прогревка ОС-кэша (последовательное чтение extracted-бандлов после загрузки меню).");
				_maxExtractSizeGb = _config.Bind("ExtractedAssets", "MaxExtractSizeGB", "auto", "Лимит размера extracted-кеша. auto = 4GB, либо число в GB/MB/KB, например 8GB.");

				Reload();
				_initialized = true;
			}
		}

		public static void Reload()
		{
			if (_config == null)
				return;

			EnableCache = _enableCache.Value;
			EnableAssetsCache = _cacheAssets.Value;
			EnableLocalizationCache = _cacheLocalization.Value;
			EnableStateCache = _cacheState.Value;
			VerboseDiagnostics = _verboseDiagnostics.Value;
			SuppressPluginLoadLogs = _suppressPluginLoadLogs.Value;
			FingerprintMode = _fingerprintMode.Value ?? "Fast";
			ExtractHeavyAssets = _extractHeavyAssets.Value;
			ExtractDir = _extractDir.Value ?? "BepInEx/cache/extracted_assets";
			PreferredCompression = _preferredCompression.Value ?? "LZ4";
			BackgroundWarmup = _backgroundWarmup.Value;
			MaxExtractSizeBytes = ParseSize(ResolveAutoSize(_maxExtractSizeGb.Value, "4GB"), 4L * 1024 * 1024 * 1024);
			// Нормализация пути extracted dir делается на месте использования (в рантайме Paths может быть ещё не готов).
			CacheDir = _cacheDir.Value ?? "auto";
			ValidateStrict = _validateStrict.Value;
			MaxCacheSizeBytes = ParseSize(_maxCacheSize.Value, 16L * 1024 * 1024 * 1024);
			CacheDirResolved = ResolveCacheDir(CacheDir);
		}

		private static string ResolveAutoSize(string value, string fallback)
		{
			if (string.IsNullOrEmpty(value))
				return fallback;

			var trimmed = value.Trim();
			if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
				return fallback;

			return trimmed;
		}

		private static string ResolveCacheDir(string cacheDir)
		{
			if (string.IsNullOrEmpty(cacheDir) || cacheDir.Equals("auto", StringComparison.OrdinalIgnoreCase))
				return Paths.CachePath;

			if (Path.IsPathRooted(cacheDir))
				return cacheDir;

			return Path.Combine(Paths.BepInExRootPath ?? ".", cacheDir);
		}

		public static string ResolveExtractDir(string extractDir)
		{
			if (string.IsNullOrEmpty(extractDir))
				return Path.Combine(Path.Combine(Paths.BepInExRootPath ?? ".", "cache"), "extracted_assets");

			var resolved = extractDir.Replace('/', Path.DirectorySeparatorChar);
			if (Path.IsPathRooted(resolved))
				return resolved;

			// Разрешаем указание относительно корня игры, либо относительно BepInExRootPath.
			if (resolved.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Paths.GameRootPath))
				return Path.Combine(Paths.GameRootPath, resolved);

			return Path.Combine(Paths.BepInExRootPath ?? ".", resolved);
		}

		private static long ParseSize(string value, long fallback)
		{
			if (string.IsNullOrEmpty(value))
				return fallback;

			var trimmed = value.Trim();
			var multiplier = 1L;
			var suffix = trimmed.Length >= 2 ? trimmed.Substring(trimmed.Length - 2) : string.Empty;
			var numberPart = trimmed;

			if (suffix.Equals("KB", StringComparison.OrdinalIgnoreCase))
			{
				multiplier = 1024L;
				numberPart = trimmed.Substring(0, trimmed.Length - 2);
			}
			else if (suffix.Equals("MB", StringComparison.OrdinalIgnoreCase))
			{
				multiplier = 1024L * 1024;
				numberPart = trimmed.Substring(0, trimmed.Length - 2);
			}
			else if (suffix.Equals("GB", StringComparison.OrdinalIgnoreCase))
			{
				multiplier = 1024L * 1024 * 1024;
				numberPart = trimmed.Substring(0, trimmed.Length - 2);
			}

			if (!long.TryParse(numberPart.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			{
				_log?.LogWarning($"Некорректное значение MaxCacheSize: \"{value}\". Используется {fallback}.");
				return fallback;
			}

			try
			{
				return checked(parsed * multiplier);
			}
			catch (OverflowException)
			{
				_log?.LogWarning($"Слишком большое значение MaxCacheSize: \"{value}\". Используется {fallback}.");
				return fallback;
			}
		}
	}
}
