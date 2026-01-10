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
		private static bool _cacheHit;
		private static ManualLogSource _log;
		private static readonly object JotunnLock = new object();
		private static bool _jotunnHooked;
		private static AssemblyLoadEventHandler _jotunnHandler;

		public static ManualLogSource Log => _log;
		public static bool CacheHit => _cacheHit;

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

			if (!CacheConfig.EnableCache)
				return;

			if (CacheConfig.EnableLocalizationCache)
			{
				LocalizationCachePatcher.Initialize(_log);
				JotunnLocalizationCachePatcher.Initialize(_log);
			}

			if (CacheConfig.EnableStateCache)
				JotunnStateCachePatcher.Initialize(_log);

			JotunnCompatibilityPatcher.Initialize(_log);

			if (CacheConfig.EnableLocalizationCache || CacheConfig.EnableStateCache || !JotunnCompatibilityPatcher.IsInitialized)
				EnsureJotunnPatchDeferred();
		}

		public static bool TryLoadCache(string gameExePath, string unityVersion)
		{
			Initialize();
			_cacheHit = false;

			if (!CacheConfig.EnableCache)
				{
					_log.LogMessage("CacheFork: кеш отключен настройкой.");
					return false;
				}

			EnsureCacheDirectory();

			var manifestPath = GetManifestPath();
			var fingerprint = CacheFingerprint.Compute(_log);
			if (string.IsNullOrEmpty(fingerprint))
			{
				HandleCacheInvalid("хеш окружения не рассчитан");
				return false;
			}

			EnsureInitialManifest(manifestPath, gameExePath, unityVersion, fingerprint);

 			var manifest = CacheManifest.Load(manifestPath, _log);
			if (manifest == null)
			{
				HandleCacheInvalid("манифест кеша не найден");
				return false;
			}

			if (!string.Equals(manifest.CacheFormatVersion, CacheManifest.CurrentFormatVersion, StringComparison.OrdinalIgnoreCase))
			{
				HandleCacheInvalid("формат манифеста устарел");
				return false;
			}

			if (!manifest.IsComplete)
			{
				HandleCacheInvalid("манифест кеша неполный");
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
			_cacheHit = true;
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

			var manifestPath = GetManifestPath();
			var existing = CacheManifest.Load(manifestPath, _log);

			var manifest = new CacheManifest
			{
				Fingerprint = fingerprint,
				GameExecutable = string.IsNullOrEmpty(gameExePath) ? string.Empty : Path.GetFileName(gameExePath),
				UnityVersion = unityVersion ?? string.Empty,
				UnityVersionExe = GetExecutableVersion(gameExePath),
				CacheFormatVersion = CacheManifest.CurrentFormatVersion,
				IsComplete = true,
				CreatedUtc = existing?.CreatedUtc ?? DateTime.UtcNow.ToString("O"),
				CompletedUtc = DateTime.UtcNow.ToString("O")
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

		private static void EnsureInitialManifest(string manifestPath, string gameExePath, string unityVersion, string fingerprint)
		{
			if (string.IsNullOrEmpty(manifestPath) || string.IsNullOrEmpty(fingerprint))
				return;

			if (File.Exists(manifestPath))
				return;

			try
			{
				var manifest = new CacheManifest
				{
					Fingerprint = fingerprint,
					GameExecutable = string.IsNullOrEmpty(gameExePath) ? string.Empty : Path.GetFileName(gameExePath),
					UnityVersion = unityVersion ?? string.Empty,
					UnityVersionExe = GetExecutableVersion(gameExePath),
					CacheFormatVersion = CacheManifest.CurrentFormatVersion,
					IsComplete = false,
					CreatedUtc = DateTime.UtcNow.ToString("O")
				};

				manifest.Save(manifestPath, _log);
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось создать начальный манифест ({ex.Message}).");
			}
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

		private static void EnsureJotunnPatchDeferred()
		{
			if (AreJotunnPatchesReady())
				return;

			lock (JotunnLock)
			{
				if (_jotunnHooked || AreJotunnPatchesReady())
					return;

				_jotunnHandler = (sender, args) =>
				{
					try
					{
						var name = args.LoadedAssembly?.GetName()?.Name;
						if (!string.Equals(name, "Jotunn", StringComparison.OrdinalIgnoreCase))
							return;

						if (CacheConfig.EnableLocalizationCache)
							JotunnLocalizationCachePatcher.Initialize(_log);
						if (CacheConfig.EnableStateCache)
							JotunnStateCachePatcher.Initialize(_log);
						JotunnCompatibilityPatcher.Initialize(_log);

						if (AreJotunnPatchesReady())
						{
							AppDomain.CurrentDomain.AssemblyLoad -= _jotunnHandler;
							_jotunnHooked = false;
						}
					}
					catch (Exception ex)
					{
						_log?.LogWarning($"CacheFork: ошибка при отложенной инициализации Jotunn ({ex.Message}).");
					}
				};

				AppDomain.CurrentDomain.AssemblyLoad += _jotunnHandler;
				_jotunnHooked = true;
			}
		}
		
		private static bool AreJotunnPatchesReady()
		{
			var localizationReady = !CacheConfig.EnableLocalizationCache || JotunnLocalizationCachePatcher.IsInitialized;
			var stateReady = !CacheConfig.EnableStateCache || JotunnStateCachePatcher.IsInitialized;
			var compatReady = JotunnCompatibilityPatcher.IsInitialized;
			return localizationReady && stateReady && compatReady;
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
