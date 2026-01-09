using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class CacheManager
	{
		private static readonly object InitLock = new object();
		private static bool _initialized;
		private static ManualLogSource _log;

		public static ManualLogSource Log => _log;

		public static void Initialize()
		{
			if (_initialized)
				return;

			lock (InitLock)
			{
				if (_initialized)
					return;

				_log = Logger.CreateLogSource("BepInEx.Cache");
				CacheConfig.Initialize(_log);
				_initialized = true;
			}
		}

		public static void InitializeRuntimePatches()
		{
			Initialize();

			if (!CacheConfig.EnableCache || !CacheConfig.EnableLocalizationCache)
				return;

			LocalizationCachePatcher.Initialize(_log);
		}

		public static bool TryLoadCache(string gameExePath, string unityVersion)
		{
			Initialize();

			if (!CacheConfig.EnableCache)
			{
				_log.LogMessage("CacheFork: кеш отключен настройкой.");
				return false;
			}

			EnsureCacheDirectory();

			var fingerprint = CacheFingerprint.Compute(_log);
			if (string.IsNullOrEmpty(fingerprint))
			{
				HandleCacheInvalid("хеш окружения не рассчитан");
				return false;
			}

			var manifestPath = GetManifestPath();
			var manifest = CacheManifest.Load(manifestPath, _log);
			if (manifest == null)
			{
				HandleCacheInvalid("манифест кеша не найден");
				return false;
			}

			if (!string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
			{
				HandleCacheInvalid("хеш окружения изменился");
				return false;
			}

			if (!IsUnityVersionMatch(unityVersion, manifest))
			{
				HandleCacheInvalid("версия Unity изменилась");
				return false;
			}

			if (!string.IsNullOrEmpty(gameExePath))
			{
				var currentExe = Path.GetFileName(gameExePath);
				if (!string.IsNullOrEmpty(manifest.GameExecutable) && !string.Equals(manifest.GameExecutable, currentExe, StringComparison.OrdinalIgnoreCase))
				{
					HandleCacheInvalid("исполняемый файл игры изменился");
					return false;
				}
			}

			if (!AssetCache.IsReady(_log))
			{
				HandleCacheInvalid("кеш ассетов не готов");
				return false;
			}

			if (!LocalizationCache.IsReady(_log))
			{
				HandleCacheInvalid("кеш локализации не готов");
				return false;
			}

			_log.LogMessage("CacheFork: кеш валиден (манифест совпал).");
			return true;
		}

		public static bool IsEnabled()
		{
			Initialize();
			return CacheConfig.EnableCache;
		}

		public static string GetAssembliesCachePath()
		{
			Initialize();

			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			var assembliesRoot = Path.Combine(cacheRoot, "assemblies");
			return Path.Combine(assembliesRoot, processName);
		}

		public static void BuildAndDump(string gameExePath, string unityVersion)
		{
			Initialize();

			if (!CacheConfig.EnableCache)
				return;

			EnsureCacheDirectory();

			var fingerprint = CacheFingerprint.Compute(_log);
			if (string.IsNullOrEmpty(fingerprint))
				return;

			AssetCache.Build(_log);
			LocalizationCache.Build(_log);

			var manifest = new CacheManifest
			{
				Fingerprint = fingerprint,
				GameExecutable = string.IsNullOrEmpty(gameExePath) ? string.Empty : Path.GetFileName(gameExePath),
				UnityVersion = unityVersion ?? string.Empty,
				UnityVersionExe = GetExecutableVersion(gameExePath),
				CreatedUtc = DateTime.UtcNow.ToString("O")
			};

			manifest.Save(GetManifestPath(), _log);
		}

		private static void EnsureCacheDirectory()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			if (!Directory.Exists(cacheRoot))
				Directory.CreateDirectory(cacheRoot);
		}

		private static void HandleCacheInvalid(string reason)
		{
			_log.LogMessage($"CacheFork: кеш невалиден ({reason}).");

			if (!CacheConfig.ValidateStrict)
				return;

			ClearCache();
		}

		private static void ClearCache()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			DeleteDirectory(Path.Combine(cacheRoot, "assemblies"));
			DeleteDirectory(Path.Combine(cacheRoot, "assets"));
			DeleteDirectory(Path.Combine(cacheRoot, "localization"));
			DeleteDirectory(Path.Combine(cacheRoot, "state"));

			try
			{
				var manifestPath = GetManifestPath();
				if (File.Exists(manifestPath))
					File.Delete(manifestPath);
			}
			catch (Exception ex)
			{
				_log.LogWarning($"CacheFork: не удалось удалить манифест кеша ({ex.Message}).");
			}
		}

		private static void DeleteDirectory(string path)
		{
			if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
				return;

			try
			{
				Directory.Delete(path, true);
			}
			catch (Exception ex)
			{
				_log.LogWarning($"CacheFork: не удалось очистить каталог кеша {path} ({ex.Message}).");
			}
		}

		private static string GetManifestPath()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			return Path.Combine(cacheRoot ?? ".", CacheManifest.DefaultFileName);
		}

		private static bool IsUnityVersionMatch(string unityVersion, CacheManifest manifest)
		{
			if (string.IsNullOrEmpty(unityVersion))
				return true;

			if (unityVersion.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
				return true;

			if (!string.IsNullOrEmpty(manifest.UnityVersionExe) && string.Equals(manifest.UnityVersionExe, unityVersion, StringComparison.Ordinal))
				return true;

			if (!string.IsNullOrEmpty(manifest.UnityVersion) && string.Equals(manifest.UnityVersion, unityVersion, StringComparison.Ordinal))
				return true;

			if (string.IsNullOrEmpty(manifest.UnityVersionExe))
			{
				_log.LogMessage("CacheFork: версия Unity отличается, но манифест старого формата; проверка пропущена.");
				return true;
			}

			return false;
		}

		private static string GetExecutableVersion(string gameExePath)
		{
			if (string.IsNullOrEmpty(gameExePath))
				return string.Empty;

			try
			{
				var info = FileVersionInfo.GetVersionInfo(gameExePath);
				return info?.FileVersion ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}
	}
}
