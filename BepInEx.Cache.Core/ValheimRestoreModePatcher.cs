using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class ValheimRestoreModePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Valheim.Restore";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _patched;
		private static bool _objectDbPatched;
		private static bool _znetScenePatched;
		private static ManualLogSource _log;
		private static AssemblyLoadEventHandler _assemblyHandler;
		private static bool _assemblyHooked;
		private static bool _objectDbLogged;
		private static bool _znetSceneLogged;
		private static Stopwatch _copyOtherDbStopwatch;
		private static Stopwatch _objectDbAwakeStopwatch;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;

				_initialized = true;
				_log = log ?? _log;

				// Важно: пока это только «скелет» restore-mode — никакой подмены/skip-инициализации, только диагностика порядка.
				// Патчим только когда restore-mode реально активен (cache-hit), либо когда включена расширенная диагностика.
				if (!CacheManager.RestoreModeActive && !CacheConfig.VerboseDiagnostics)
					return;

				TryPatchNowOrDefer();
			}
		}

		private static void TryPatchNowOrDefer()
		{
			TryPatchValheimTypes();

			// Если не все нужные патчи подключены — цепляемся на AssemblyLoad, пока не появятся типы/методы.
			if (_objectDbPatched && _znetScenePatched)
				return;

			if (_assemblyHooked)
				return;

			_assemblyHandler = (sender, args) =>
			{
				try
				{
					TryPatchValheimTypes();
					if (_objectDbPatched && _znetScenePatched)
					{
						AppDomain.CurrentDomain.AssemblyLoad -= _assemblyHandler;
						_assemblyHooked = false;
					}
				}
				catch
				{
				}
			};
			AppDomain.CurrentDomain.AssemblyLoad += _assemblyHandler;
			_assemblyHooked = true;
		}

		private static bool TryPatchValheimTypes()
		{
			var objectDbType = AccessTools.TypeByName("ObjectDB");
			var znetSceneType = AccessTools.TypeByName("ZNetScene");
			if (objectDbType == null && znetSceneType == null)
				return false;

			try
			{
				var harmony = new Harmony(HarmonyId);

				if (objectDbType != null)
				{
					var awake = AccessTools.Method(objectDbType, "Awake");
					if (awake != null && !_objectDbPatched)
					{
						harmony.Patch(
							awake,
							prefix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbAwakePrefix))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbAwakePostfix))
							{
								priority = Priority.Last
							});
						_log?.LogMessage("CacheFork: Valheim патч подключен (ObjectDB.Awake).");
						_objectDbPatched = true;
					}

					var copyOtherDb = AccessTools.Method(objectDbType, "CopyOtherDB");
					if (copyOtherDb != null)
					{
						harmony.Patch(
							copyOtherDb,
							prefix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbCopyOtherDbPrefix))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbCopyOtherDbPostfix))
							{
								priority = Priority.Last
							});
					}
				}

				if (znetSceneType != null)
				{
					var awake = AccessTools.Method(znetSceneType, "Awake");
					if (awake != null && !_znetScenePatched)
					{
						harmony.Patch(
							awake,
							prefix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ZNetSceneAwakePrefix))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ZNetSceneAwakePostfix))
							{
								priority = Priority.Last
							});
						_log?.LogMessage("CacheFork: Valheim патч подключен (ZNetScene.Awake).");
						_znetScenePatched = true;
					}
				}

				_patched = _objectDbPatched || _znetScenePatched;
				return _patched;
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить Valheim restore-mode патчи ({ex.Message}).");
				return false;
			}
		}

		private static void ObjectDbAwakePrefix(object __instance)
		{
			try
			{
				if (_objectDbLogged && !CacheConfig.VerboseDiagnostics)
					return;
				_objectDbLogged = true;

				_objectDbAwakeStopwatch = Stopwatch.StartNew();
				_log?.LogMessage($"CacheFork: ObjectDB.Awake (prefix), restore-mode={(CacheManager.RestoreModeActive ? "ON" : "OFF")}, cache-hit={(CacheManager.CacheHit ? "true" : "false")}.");
				LogObjectDbCounts(__instance, "prefix");
			}
			catch
			{
			}
		}

		private static void ObjectDbAwakePostfix(object __instance)
		{
			try
			{
				if (_objectDbAwakeStopwatch != null)
				{
					_objectDbAwakeStopwatch.Stop();
					_log?.LogMessage($"CacheFork: DIAG timing ObjectDB.Awake = {_objectDbAwakeStopwatch.ElapsedMilliseconds} мс.");
					_objectDbAwakeStopwatch = null;
				}

				if (!CacheConfig.VerboseDiagnostics)
					return;
				LogObjectDbCounts(__instance, "postfix");
			}
			catch
			{
			}
		}

		private static void ObjectDbCopyOtherDbPrefix(object __instance)
		{
			try
			{
				if (!CacheConfig.VerboseDiagnostics)
					return;
				_log?.LogMessage($"CacheFork: ObjectDB.CopyOtherDB (prefix), restore-mode={(CacheManager.RestoreModeActive ? "ON" : "OFF")}.");
				_copyOtherDbStopwatch = Stopwatch.StartNew();
				LogObjectDbCounts(__instance, "CopyOtherDB:prefix");
			}
			catch
			{
			}
		}

		private static void ObjectDbCopyOtherDbPostfix(object __instance)
		{
			try
			{
				if (!CacheConfig.VerboseDiagnostics)
					return;
				if (_copyOtherDbStopwatch != null)
				{
					_copyOtherDbStopwatch.Stop();
					_log?.LogMessage($"CacheFork: DIAG timing ObjectDB.CopyOtherDB = {_copyOtherDbStopwatch.ElapsedMilliseconds} мс.");
					_copyOtherDbStopwatch = null;
				}
				LogObjectDbCounts(__instance, "CopyOtherDB:postfix");
			}
			catch
			{
			}
		}

		private static void ZNetSceneAwakePrefix(object __instance)
		{
			try
			{
				if (_znetSceneLogged && !CacheConfig.VerboseDiagnostics)
					return;
				_znetSceneLogged = true;

				_log?.LogMessage($"CacheFork: ZNetScene.Awake (prefix), restore-mode={(CacheManager.RestoreModeActive ? "ON" : "OFF")}, cache-hit={(CacheManager.CacheHit ? "true" : "false")}.");
				LogZNetSceneCounts(__instance, "prefix");
			}
			catch
			{
			}
		}

		private static void ZNetSceneAwakePostfix(object __instance)
		{
			try
			{
				if (!CacheConfig.VerboseDiagnostics)
					return;
				LogZNetSceneCounts(__instance, "postfix");
			}
			catch
			{
			}
		}

		private static void LogObjectDbCounts(object instance, string stage)
		{
			if (instance == null || _log == null)
				return;

			var items = TryGetCollectionCount(instance, "m_items");
			var recipes = TryGetCollectionCount(instance, "m_recipes");
			var statusEffects = TryGetCollectionCount(instance, "m_StatusEffects") ?? TryGetCollectionCount(instance, "m_statusEffects");

			_log.LogMessage($"CacheFork: ObjectDB ({stage}): items={FormatCount(items)}, recipes={FormatCount(recipes)}, statusEffects={FormatCount(statusEffects)}.");
		}

		private static void LogZNetSceneCounts(object instance, string stage)
		{
			if (instance == null || _log == null)
				return;

			var prefabs = TryGetCollectionCount(instance, "m_prefabs");
			var namedPrefabs = TryGetCollectionCount(instance, "m_namedPrefabs");
			var prefabsByHash = TryGetCollectionCount(instance, "m_namedPrefabsHash") ?? TryGetCollectionCount(instance, "m_namedPrefabs");

			_log.LogMessage($"CacheFork: ZNetScene ({stage}): prefabs={FormatCount(prefabs)}, namedPrefabs={FormatCount(namedPrefabs ?? prefabsByHash)}.");
		}

		private static string FormatCount(int? count)
		{
			return count.HasValue ? count.Value.ToString() : "n/a";
		}

		private static int? TryGetCollectionCount(object instance, string memberName)
		{
			if (instance == null || string.IsNullOrEmpty(memberName))
				return null;

			try
			{
				var type = instance.GetType();
				var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null)
					return GetCollectionCount(field.GetValue(instance));

				var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (prop != null && prop.CanRead)
					return GetCollectionCount(prop.GetValue(instance, null));
			}
			catch
			{
			}

			return null;
		}

		private static int? GetCollectionCount(object value)
		{
			if (value == null)
				return null;

			try
			{
				if (value is ICollection col)
					return col.Count;

				var countProp = value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
				if (countProp != null && countProp.PropertyType == typeof(int))
					return (int)countProp.GetValue(value, null);
			}
			catch
			{
			}

			return null;
		}
	}
}
