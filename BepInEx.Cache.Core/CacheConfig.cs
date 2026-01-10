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

		public static bool EnableCache { get; private set; }
		public static bool EnableAssetsCache { get; private set; }
		public static bool EnableLocalizationCache { get; private set; }
		public static bool EnableStateCache { get; private set; }
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
			CacheDir = _cacheDir.Value ?? "auto";
			ValidateStrict = _validateStrict.Value;
			MaxCacheSizeBytes = ParseSize(_maxCacheSize.Value, 16L * 1024 * 1024 * 1024);
			CacheDirResolved = ResolveCacheDir(CacheDir);
		}

		private static string ResolveCacheDir(string cacheDir)
		{
			if (string.IsNullOrEmpty(cacheDir) || cacheDir.Equals("auto", StringComparison.OrdinalIgnoreCase))
				return Paths.CachePath;

			if (Path.IsPathRooted(cacheDir))
				return cacheDir;

			return Path.Combine(Paths.BepInExRootPath ?? ".", cacheDir);
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
