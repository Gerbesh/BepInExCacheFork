using System;
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

		public static bool TryLoadCache(string gameExePath, string unityVersion)
		{
			Initialize();

			if (!CacheConfig.EnableCache)
			{
				_log.LogMessage("Кеш отключен настройкой.");
				return false;
			}

			EnsureCacheDirectory();

			var fingerprint = CacheFingerprint.Compute(_log);
			if (string.IsNullOrEmpty(fingerprint))
			{
				_log.LogWarning("Хеш окружения не рассчитан, кеш будет перестроен.");
				return false;
			}

			var manifestPath = GetManifestPath();
			var manifest = CacheManifest.Load(manifestPath, _log);
			if (manifest == null)
			{
				_log.LogMessage("Манифест кеша не найден, требуется построение.");
				return false;
			}

			if (!string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
			{
				_log.LogMessage("Хеш окружения изменился, кеш невалиден.");
				return false;
			}

			if (!string.IsNullOrEmpty(unityVersion) && !string.Equals(manifest.UnityVersion, unityVersion, StringComparison.Ordinal))
			{
				_log.LogMessage("Версия Unity изменилась, кеш невалиден.");
				return false;
			}

			if (!string.IsNullOrEmpty(gameExePath))
			{
				var currentExe = Path.GetFileName(gameExePath);
				if (!string.IsNullOrEmpty(manifest.GameExecutable) && !string.Equals(manifest.GameExecutable, currentExe, StringComparison.OrdinalIgnoreCase))
				{
					_log.LogMessage("Исполняемый файл игры изменился, кеш невалиден.");
					return false;
				}
			}

			_log.LogMessage("Кеш признан валидным (манифест совпал).");
			return true;
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

			var manifest = new CacheManifest
			{
				Fingerprint = fingerprint,
				GameExecutable = string.IsNullOrEmpty(gameExePath) ? string.Empty : Path.GetFileName(gameExePath),
				UnityVersion = unityVersion ?? string.Empty,
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

		private static string GetManifestPath()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			return Path.Combine(cacheRoot ?? ".", CacheManifest.DefaultFileName);
		}
	}
}
