using System;
using System.Collections.Generic;
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
		private static bool _restoreModeActive;
		private static ManualLogSource _log;
		private static readonly object FingerprintLock = new object();
		private static string _fingerprintCached;
		private static bool _fingerprintCachedSet;
		private static readonly object JotunnLock = new object();
		private static bool _jotunnHooked;
		private static AssemblyLoadEventHandler _jotunnHandler;
		private static bool _jotunnPatchedFromChainloader;
		private static readonly object PluginInitTimingLock = new object();
		private static Dictionary<string, PluginInitTimingEntry> _pluginInitTimings;
		[ThreadStatic] private static string _currentPluginGuid;
		[ThreadStatic] private static string _currentPluginName;

		public static ManualLogSource Log => _log;
		public static bool CacheHit => _cacheHit;
		public static bool RestoreModeActive => _restoreModeActive;

		public static void BeginPluginInitContext(string guid, string name)
		{
			try
			{
				_currentPluginGuid = guid ?? string.Empty;
				_currentPluginName = name ?? string.Empty;
			}
			catch
			{
			}
		}

		public static void EndPluginInitContext()
		{
			try
			{
				_currentPluginGuid = null;
				_currentPluginName = null;
			}
			catch
			{
			}
		}

		internal static string GetCurrentPluginContextLabel()
		{
			try
			{
				var guid = _currentPluginGuid;
				var name = _currentPluginName;
				if (string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(name))
					return string.Empty;

				if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid))
					return $"{name} ({guid})";
				return !string.IsNullOrEmpty(name) ? name : guid;
			}
			catch
			{
				return string.Empty;
			}
		}

		private sealed class PluginInitTimingEntry
		{
			internal string Guid;
			internal string Name;
			internal string Version;
			internal long TotalTicks;
			internal long MaxTicks;
			internal int Count;
		}

		public static void RecordPluginInitTiming(string guid, string name, string version, long elapsedTicks)
		{
			try
			{
				Initialize();
				if (!CacheConfig.EnableCache || !CacheConfig.VerboseDiagnostics)
					return;

				if (string.IsNullOrEmpty(guid) || elapsedTicks <= 0)
					return;

				lock (PluginInitTimingLock)
				{
					if (_pluginInitTimings == null)
						_pluginInitTimings = new Dictionary<string, PluginInitTimingEntry>(StringComparer.OrdinalIgnoreCase);

					if (!_pluginInitTimings.TryGetValue(guid, out var entry))
					{
						entry = new PluginInitTimingEntry
						{
							Guid = guid,
							Name = name ?? string.Empty,
							Version = version ?? string.Empty,
							TotalTicks = 0,
							MaxTicks = 0,
							Count = 0
						};
						_pluginInitTimings[guid] = entry;
					}

					entry.Count++;
					entry.TotalTicks += elapsedTicks;
					if (elapsedTicks > entry.MaxTicks)
						entry.MaxTicks = elapsedTicks;
				}
			}
			catch
			{
			}
		}

		public static void LogPluginInitTimingSummary()
		{
			try
			{
				Initialize();
				if (!CacheConfig.EnableCache || !CacheConfig.VerboseDiagnostics)
					return;

				List<PluginInitTimingEntry> entries;
				lock (PluginInitTimingLock)
				{
					if (_pluginInitTimings == null || _pluginInitTimings.Count == 0)
						return;
					entries = new List<PluginInitTimingEntry>(_pluginInitTimings.Values);
				}

				entries.Sort((a, b) => b.TotalTicks.CompareTo(a.TotalTicks));

				long totalTicks = 0;
				for (var i = 0; i < entries.Count; i++)
					totalTicks += entries[i].TotalTicks;

				var totalMs = (long)TimeSpan.FromTicks(totalTicks).TotalMilliseconds;
				_log?.LogMessage($"CacheFork: DIAG plugin init summary: plugins={entries.Count}, total={totalMs} мс (Awake при AddComponent).");

				var maxLines = entries.Count < 20 ? entries.Count : 20;
				for (var i = 0; i < maxLines; i++)
				{
					var e = entries[i];
					var ms = (long)TimeSpan.FromTicks(e.TotalTicks).TotalMilliseconds;
					var maxMs = (long)TimeSpan.FromTicks(e.MaxTicks).TotalMilliseconds;
					_log?.LogMessage($"CacheFork: DIAG plugin init: #{i + 1} {e.Name} ({e.Guid}) v{e.Version}: total={ms} мс, count={e.Count}, max={maxMs} мс.");
				}
			}
			catch
			{
			}
		}

		public static void Initialize()
		{
			if (_initialized)
				return;

			lock (InitLock)
			{
				if (_initialized)
					return;

				_log = Logger.CreateLogSource("BepInEx.Cache");
				using (CacheMetrics.Measure("CacheManager.Initialize"))
				{
					CacheConfig.Initialize(_log);
					if (CacheConfig.EnableCache)
						EnsureInitialManifestOnStartup();
				}
				_initialized = true;
			}
		}

		public static void InitializeRuntimePatches()
		{
			using (CacheMetrics.Measure("CacheManager.InitializeRuntimePatches"))
			{
				Initialize();

				if (!CacheConfig.EnableCache)
					return;

				CacheSummaryReporter.Install(_log);

				if (CacheConfig.VerboseDiagnostics)
					HarmonyDiagnosticsPatcher.Initialize(_log);

				ConfigCompatibilityPatcher.Initialize(_log);

				ValheimRestoreModePatcher.Initialize(_log);
				ValheimCraftingDiagnosticsPatcher.Initialize(_log);
				AzuCraftyBoxesCompatibilityPatcher.Initialize(_log);
				JewelcraftingLocalizationGuardPatcher.Initialize(_log);
				ExtractedAssetCachePatcher.Initialize(_log);

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
				JotunnTimingPatcher.Initialize(_log, assembly);

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
			using (CacheMetrics.Measure("CacheManager.TryLoadCache"))
			{
				Initialize();
				_cacheHit = false;
				_restoreModeActive = false;

				if (!CacheConfig.EnableCache)
				{
					_log.LogMessage("CacheFork: кеш отключен настройкой.");
					return false;
				}

				EnsureCacheDirectory();

				var fingerprint = GetOrComputeFingerprint();
				if (string.IsNullOrEmpty(fingerprint))
				{
					HandleCacheInvalid("хеш окружения не рассчитан");
					return false;
				}

				var manifestPath = GetManifestPath();
				var manifestAliasPath = GetManifestAliasPath();

				CacheManifest manifest;
				using (CacheMetrics.Measure("Manifest.Load"))
					manifest = LoadManifest(manifestPath, manifestAliasPath);
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
					// Автовосстановление: если манифест "неполный", но все кеши уже готовы и fingerprint совпадает —
					// помечаем манифест complete и продолжаем как cache-hit. Это спасает сценарий "игру закрыли до BuildAndDump".
					var canHeal = string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
					              IsUnityVersionMatch(unityVersion, manifest);
					if (canHeal && !string.IsNullOrEmpty(gameExePath))
					{
						var currentExe = Path.GetFileName(gameExePath);
						if (!string.IsNullOrEmpty(manifest.GameExecutable) && !string.Equals(manifest.GameExecutable, currentExe, StringComparison.OrdinalIgnoreCase))
							canHeal = false;
					}

					if (canHeal && AssetCache.IsReady(_log) && LocalizationCache.IsReady(_log))
					{
						// state-cache не блокирует auto-heal: его отсутствие не делает весь кеш бесполезным.
					}
					else
					{
						canHeal = false;
					}

					if (canHeal)
					{
						manifest.IsComplete = true;
						manifest.CompletedUtc = DateTime.UtcNow.ToString("O");
						manifest.CacheFormatVersion = CacheManifest.CurrentFormatVersion;
						SaveManifest(manifest, manifestPath, manifestAliasPath);
						_log.LogMessage("CacheFork: манифест был неполный, но кеши готовы — манифест помечен complete (auto-heal).");

						_log.LogMessage("CacheFork: кеш валиден (манифест совпал).");
						_cacheHit = true;
						_restoreModeActive = CacheConfig.EnableStateCache;
						if (_restoreModeActive)
							_log.LogMessage("CacheFork: restore-mode активирован (cache-hit).");
						else
							_log.LogMessage("CacheFork: restore-mode отключен (EnableStateCache=false).");
						if (CacheConfig.EnableStateCache)
							JotunnStateCache.EnsureLoaded(_log);
						return true;
					}

					_log.LogMessage("CacheFork: манифест кеша неполный, выполняется полная пересборка.");
					if (CacheConfig.ValidateStrict)
						ClearCache(keepManifest: true);
					EnsureInitialManifest(manifestPath, manifestAliasPath, gameExePath, unityVersion, fingerprint, overwrite: true);
					return false;
				}

				if (!string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
				{
					var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
					if (CacheConfig.VerboseDiagnostics)
					{
						_log?.LogMessage($"CacheFork: fingerprint mismatch: manifest=\"{manifest.Fingerprint}\", current=\"{fingerprint}\", manifestPath=\"{manifestPath}\".");
						_log?.LogMessage($"CacheFork: fingerprint mismatch: cacheRoot=\"{cacheRoot}\", CacheDirResolved=\"{CacheConfig.CacheDirResolved}\".");
					}
					CacheFingerprint.LogDiffAgainstSnapshot(_log, cacheRoot, CacheConfig.VerboseDiagnostics ? 30 : 10);

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
				_restoreModeActive = CacheConfig.EnableStateCache;
				if (_restoreModeActive)
					_log.LogMessage("CacheFork: restore-mode активирован (cache-hit).");
				else
					_log.LogMessage("CacheFork: restore-mode отключен (EnableStateCache=false).");
				if (CacheConfig.EnableStateCache)
					JotunnStateCache.EnsureLoaded(_log);
				return true;
			}
		}

		public static bool IsEnabled()
		{
			Initialize();
			return CacheConfig.EnableCache;
		}

		public static bool ShouldSuppressPluginLoadLogs()
		{
			Initialize();
			return CacheConfig.EnableCache && CacheConfig.SuppressPluginLoadLogs;
		}

		public static bool ShouldDeferPluginInitializationOnCacheHit()
		{
			Initialize();
			return CacheConfig.EnableCache && CacheHit && CacheConfig.DeferPluginInitialization;
		}

		public static string GetDeferPluginInitializationMode()
		{
			Initialize();
			return CacheConfig.DeferPluginInitializationMode ?? "Whitelist";
		}

		public static string GetDeferPluginInitializationList()
		{
			Initialize();
			return CacheConfig.DeferPluginInitializationList ?? string.Empty;
		}

		public static int GetDeferPluginInitializationDelaySeconds()
		{
			Initialize();
			return CacheConfig.DeferPluginInitializationDelaySeconds;
		}

		public static int GetDeferPluginInitializationMaxPerFrame()
		{
			Initialize();
			return CacheConfig.DeferPluginInitializationMaxPerFrame;
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

			// Важно: fingerprint пересчитываем здесь заново. Во время загрузки модов они могут
			// создать/распаковать файлы в plugins/, и "ранний" fingerprint станет устаревшим.
			var fingerprint = ComputeFingerprintFresh("BuildAndDump");
			if (string.IsNullOrEmpty(fingerprint))
				return;

			using (CacheMetrics.Measure("CacheManager.BuildAndDump"))
			{
				var swTotal = Stopwatch.StartNew();

				var swAssets = Stopwatch.StartNew();
				AssetCache.Build(_log);
				swAssets.Stop();
				CacheMetrics.Add("AssetCache.Build", swAssets.ElapsedTicks);
				_log?.LogMessage($"CacheFork: этап кеша ассетов завершён за {swAssets.ElapsedMilliseconds} мс.");

				var swLoc = Stopwatch.StartNew();
				LocalizationCache.Build(_log);
				swLoc.Stop();
				CacheMetrics.Add("LocalizationCache.Build", swLoc.ElapsedTicks);
				_log?.LogMessage($"CacheFork: этап кеша локализации завершён за {swLoc.ElapsedMilliseconds} мс.");

				var swState = Stopwatch.StartNew();
				if (CacheConfig.EnableStateCache)
					JotunnStateCachePatcher.SnapshotNow(_log);
				swState.Stop();
				if (CacheConfig.EnableStateCache)
				{
					CacheMetrics.Add("JotunnStateCache.Snapshot", swState.ElapsedTicks);
					_log?.LogMessage($"CacheFork: этап кеша состояния завершён за {swState.ElapsedMilliseconds} мс.");
				}

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

				using (CacheMetrics.Measure("Manifest.Save"))
					SaveManifest(manifest, manifestPath, manifestAliasPath);

				try
				{
					var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
					CacheFingerprint.WriteSnapshot(_log, cacheRoot);
				}
				catch
				{
				}
				swTotal.Stop();
				CacheMetrics.Add("BuildAndDump.Total", swTotal.ElapsedTicks);
				_log?.LogMessage($"CacheFork: BuildAndDump завершён за {swTotal.ElapsedMilliseconds} мс.");
			}
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
			// extracted_assets может быть как внутри CacheDir, так и задан отдельным путем через ExtractDir.
			DeleteDirectory(Path.Combine(cacheRoot, "extracted_assets"));
			try
			{
				var extractedRoot = CacheConfig.ResolveExtractDir(CacheConfig.ExtractDir);
				if (!string.IsNullOrEmpty(extractedRoot))
					DeleteDirectory(extractedRoot);
			}
			catch
			{
			}

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
				var snapshotPath = Path.Combine(cacheRoot, CacheFingerprint.SnapshotFileName);
				if (File.Exists(snapshotPath))
					File.Delete(snapshotPath);
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

				var fingerprint = GetOrComputeFingerprint();
				if (string.IsNullOrEmpty(fingerprint))
					return;

				EnsureInitialManifest(manifestPath, manifestAliasPath, Paths.ExecutablePath, string.Empty, fingerprint, overwrite: false);
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка при создании начального манифеста ({ex.Message}).");
			}
		}

		private static string GetOrComputeFingerprint()
		{
			lock (FingerprintLock)
			{
				if (_fingerprintCachedSet)
					return _fingerprintCached ?? string.Empty;

				using (CacheMetrics.Measure("Fingerprint.Compute"))
					_fingerprintCached = CacheFingerprint.Compute(_log);
				_fingerprintCachedSet = true;
				return _fingerprintCached ?? string.Empty;
			}
		}

		private static string ComputeFingerprintFresh(string context)
		{
			lock (FingerprintLock)
			{
				using (CacheMetrics.Measure("Fingerprint.Compute"))
					_fingerprintCached = CacheFingerprint.Compute(_log);
				_fingerprintCachedSet = true;

				if (CacheConfig.VerboseDiagnostics && !string.IsNullOrEmpty(context))
					_log?.LogMessage($"CacheFork: fingerprint пересчитан (context={context}).");

				return _fingerprintCached ?? string.Empty;
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
						JotunnTimingPatcher.Initialize(_log, args.LoadedAssembly);

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
