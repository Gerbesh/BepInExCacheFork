using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
		private static bool _jotunnPatchedFromChainloader;

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
				if (CacheConfig.EnableCache)
					EnsureInitialManifestOnStartup();
				_initialized = true;
			}
		}

		public static void InitializeRuntimePatches()
		{
			Initialize();

			if (!CacheConfig.EnableCache)
				return;

			if (CacheConfig.VerboseDiagnostics)
				HarmonyDiagnosticsPatcher.Initialize(_log);

			if (CacheConfig.EnableLocalizationCache)
			{
				LocalizationCachePatcher.Initialize(_log);
				JotunnLocalizationCachePatcher.Initialize(_log);
			}

			JotunnCompatibilityPatcher.Initialize(_log);

			if (CacheConfig.EnableStateCache)
				JotunnStateCachePatcher.Initialize(_log);

			if (CacheConfig.EnableLocalizationCache || CacheConfig.EnableStateCache || !JotunnCompatibilityPatcher.IsInitialized)
				EnsureJotunnPatchDeferred();
		}

		public static void OnPluginAssemblyLoaded(Assembly assembly)
		{
			if (assembly == null)
				return;

			Initialize();

			if (!CacheConfig.EnableCache)
				return;

			string name;
			try
			{
				name = assembly.GetName().Name;
			}
			catch
			{
				return;
			}

			if (!string.Equals(name, "Jotunn", StringComparison.OrdinalIgnoreCase))
				return;

			lock (JotunnLock)
			{
				if (_jotunnPatchedFromChainloader)
					return;
				_jotunnPatchedFromChainloader = true;
			}

			try
			{
				if (CacheConfig.VerboseDiagnostics)
				{
					var location = string.Empty;
					try { location = assembly.Location ?? string.Empty; } catch { }
					_log?.LogMessage($"CacheFork: Chainloader уведомил о загрузке Jotunn (Location=\"{location}\").");
				}

				// Важно: сначала подключаем защитные патчи совместимости, затем остальную интеграцию.
				JotunnCompatibilityPatcher.Initialize(_log, assembly);

				if (CacheConfig.EnableLocalizationCache)
					JotunnLocalizationCachePatcher.Initialize(_log);
				if (CacheConfig.EnableStateCache)
					JotunnStateCachePatcher.Initialize(_log);
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка при ранней инициализации Jotunn ({ex.Message}).");
			}
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

			var fingerprint = CacheFingerprint.Compute(_log);
			if (string.IsNullOrEmpty(fingerprint))
			{
				HandleCacheInvalid("хеш окружения не рассчитан");
				return false;
			}

			var manifestPath = GetManifestPath();
			var manifestAliasPath = GetManifestAliasPath();

			var manifest = LoadManifest(manifestPath, manifestAliasPath);
			if (manifest == null)
			{
				_log.LogMessage("CacheFork: манифест кеша не найден, создаётся начальный манифест и выполняется пересборка.");
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: false);
				return false;
			}

			if (!string.Equals(manifest.CacheFormatVersion, CacheManifest.CurrentFormatVersion, StringComparison.OrdinalIgnoreCase))
			{
				HandleCacheInvalid("формат манифеста устарел");
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
				return false;
			}

			if (!manifest.IsComplete)
			{
				_log.LogMessage("CacheFork: манифест кеша неполный, выполняется полная пересборка.");
				if (CacheConfig.ValidateStrict)
					ClearCache(keepManifest: true);
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
				return false;
			}

			if (!string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
			{
				HandleCacheInvalid("хеш окружения изменился");
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
				return false;
			}

			if (!IsUnityVersionMatch(unityVersion, manifest))
			{
				HandleCacheInvalid("версия Unity изменилась");
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
				return false;
			}

			if (!string.IsNullOrEmpty(gameExePath))
			{
				var currentExe = Path.GetFileName(gameExePath);
				if (!string.IsNullOrEmpty(manifest.GameExecutable) && !string.Equals(manifest.GameExecutable, currentExe, StringComparison.OrdinalIgnoreCase))
				{
					HandleCacheInvalid("исполняемый файл игры изменился");
					EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
					return false;
				}
			}

			if (!AssetCache.IsReady(_log))
			{
				HandleCacheInvalid("кеш ассетов не готов");
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
				return false;
			}

			if (!LocalizationCache.IsReady(_log))
			{
				HandleCacheInvalid("кеш локализации не готов");
				EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
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
			if (CacheConfig.EnableStateCache)
				JotunnStateCachePatcher.SnapshotNow(_log);

			var manifestPath = GetManifestPath();
			var manifestAliasPath = GetManifestAliasPath();
			var existing = LoadManifest(manifestPath, manifestAliasPath);

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

			SaveManifest(manifest, manifestPath, manifestAliasPath);
		}

		private static void EnsureCacheDirectory()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			if (!Directory.Exists(cacheRoot))
				Directory.CreateDirectory(cacheRoot);
		}

		private static void EnsureInitialManifest(string manifestPath, string manifestAliasPath, string gameExePath, string unityVersion, string fingerprint, bool overwrite)
		{
			if (string.IsNullOrEmpty(manifestPath) || string.IsNullOrEmpty(fingerprint))
				return;

			if (!overwrite && (File.Exists(manifestPath) || File.Exists(manifestAliasPath)))
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

				SaveManifest(manifest, manifestPath, manifestAliasPath);
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

			ClearCache(keepManifest: false);
		}

		private static void ClearCache(bool keepManifest)
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			DeleteDirectory(Path.Combine(cacheRoot, "assemblies"));
			DeleteDirectory(Path.Combine(cacheRoot, "assets"));
			DeleteDirectory(Path.Combine(cacheRoot, "localization"));
			DeleteDirectory(Path.Combine(cacheRoot, "state"));

			if (keepManifest)
				return;

			try
			{
				var manifestPath = GetManifestPath();
				var aliasPath = GetManifestAliasPath();
				if (File.Exists(manifestPath))
					File.Delete(manifestPath);
				if (File.Exists(aliasPath))
					File.Delete(aliasPath);
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

		private static string GetManifestAliasPath()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			return Path.Combine(cacheRoot ?? ".", CacheManifest.JsonAliasFileName);
		}

		private static CacheManifest LoadManifest(string path, string aliasPath)
		{
			var manifest = CacheManifest.Load(path, _log);
			if (manifest != null)
				return manifest;

			return CacheManifest.Load(aliasPath, _log);
		}

		private static void SaveManifest(CacheManifest manifest, string path, string aliasPath)
		{
			if (manifest == null)
				return;

			manifest.Save(path, _log);
			manifest.Save(aliasPath, _log);
		}

		private static void EnsureInitialManifestOnStartup()
		{
			try
			{
				EnsureCacheDirectory();

				var manifestPath = GetManifestPath();
				var manifestAliasPath = GetManifestAliasPath();
				if (File.Exists(manifestPath) || File.Exists(manifestAliasPath))
					return;

				var fingerprint = CacheFingerprint.Compute(_log);
				if (string.IsNullOrEmpty(fingerprint))
					return;

				EnsureInitialManifest(manifestPath, manifestAliasPath, Paths.ExecutablePath, string.Empty, fingerprint, overwrite: false);
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка при создании начального манифеста ({ex.Message}).");
			}
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
						JotunnCompatibilityPatcher.Initialize(_log, args.LoadedAssembly);

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
