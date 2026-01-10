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
		private static int _copyOtherDbLoggedCount;
		private static Stopwatch _updateRegistersStopwatch;
		private static bool _fastCopyLogged;
		private static int _updateRegistersLoggedCount;

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
								priority = Priority.First,
								before = new[] { "com.jotunn.jotunn", "Jotunn" }
							},
							postfix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbCopyOtherDbPostfix))
							{
								priority = Priority.Last
							});
					}

					var updateRegisters = AccessTools.Method(objectDbType, "UpdateRegisters");
					if (updateRegisters != null)
					{
						harmony.Patch(
							updateRegisters,
							prefix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbUpdateRegistersPrefix))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(ValheimRestoreModePatcher), nameof(ObjectDbUpdateRegistersPostfix))
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
				if (CacheManager.RestoreModeActive && CacheManager.CacheHit)
				{
					// На cache-hit гарантируем, что словари ObjectDB не null до любых сторонних Harmony-патчей.
					EnsureObjectDbDictionariesNotNull(__instance);
				}

				if (_objectDbAwakeStopwatch != null)
				{
					_objectDbAwakeStopwatch.Stop();
					CacheMetrics.Add("Valheim.ObjectDB.Awake", _objectDbAwakeStopwatch.ElapsedTicks);
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

		private static bool ObjectDbCopyOtherDbPrefix(object __instance, object[] __args)
		{
			try
			{
				// Реальная оптимизация: на cache-hit пытаемся заменить дорогой CopyOtherDB на быстрый путь,
				// если otherDb уже содержит полный набор модовых данных.
				if (CacheManager.RestoreModeActive && CacheManager.CacheHit)
				{
					if (!_fastCopyLogged)
					{
						_fastCopyLogged = true;
						_log?.LogMessage("CacheFork: restore-mode fast-path для ObjectDB.CopyOtherDB включен (эвристика по otherDb).");
					}

					var other = (__args != null && __args.Length > 0) ? __args[0] : null;
					if (other != null)
					{
						// Защита от падений чужих Harmony-патчей (Jotunn.ModQuery): словари должны быть не null.
						EnsureObjectDbDictionariesNotNull(__instance);
						EnsureObjectDbDictionariesNotNull(other);

						var otherItems = TryGetCollectionCount(other, "m_items");
						var otherRecipes = TryGetCollectionCount(other, "m_recipes");
						var otherStatus = TryGetCollectionCount(other, "m_StatusEffects") ?? TryGetCollectionCount(other, "m_statusEffects");

						var curItems = TryGetCollectionCount(__instance, "m_items");
						var curRecipes = TryGetCollectionCount(__instance, "m_recipes");
						var curStatus = TryGetCollectionCount(__instance, "m_StatusEffects") ?? TryGetCollectionCount(__instance, "m_statusEffects");

						// Эвристика: fast-path можно применять только на поздней стадии, когда otherDb уже "большой".
						// Иначе есть риск проскочить ранний CopyOtherDB (FejdStartup.SetupObjectDB) и сломать моды.
						var looksFull = otherItems.HasValue && curItems.HasValue &&
						                otherItems.Value >= 2000 &&
						                otherItems.Value > curItems.Value;

						if (looksFull)
						{
							var sw = Stopwatch.StartNew();
							if (ApplyFastCopyOtherDb(__instance, other))
							{
								sw.Stop();
								_log?.LogMessage($"CacheFork: fast CopyOtherDB применён (items {curItems}->{otherItems}, recipes {curRecipes}->{otherRecipes}, SE {curStatus}->{otherStatus}) за {sw.ElapsedMilliseconds} мс.");
								return false; // skip original
							}
						}
					}
				}

				if (!CacheConfig.VerboseDiagnostics)
					return true;

				_log?.LogMessage($"CacheFork: ObjectDB.CopyOtherDB (prefix), restore-mode={(CacheManager.RestoreModeActive ? "ON" : "OFF")}.");
				if (_copyOtherDbLoggedCount < 3)
				{
					_copyOtherDbLoggedCount++;
					try
					{
						var arg0 = (__args != null && __args.Length > 0) ? __args[0] : null;
						var arg0Type = arg0 != null ? arg0.GetType().FullName : "null";
						_log?.LogMessage($"CacheFork: DIAG ObjectDB.CopyOtherDB args: count={(__args != null ? __args.Length : 0)}, arg0Type={arg0Type}.");
						if (arg0 != null)
							LogObjectDbCounts(arg0, "otherDb");
					}
					catch
					{
					}

					try
					{
						var st = new StackTrace(2, true);
						var frames = st.GetFrames();
						if (frames != null && frames.Length > 0)
						{
							var limit = Math.Min(frames.Length, 10);
							_log?.LogMessage("CacheFork: DIAG ObjectDB.CopyOtherDB stack (top):");
							for (var i = 0; i < limit; i++)
							{
								var m = frames[i].GetMethod();
								var dt = m?.DeclaringType;
								var asm = dt?.Assembly;
								var asmName = string.Empty;
								try { asmName = asm?.GetName()?.Name ?? string.Empty; } catch { }
								var line = "  #" + i + " " + asmName + " " + (dt != null ? dt.FullName : "<?>") + "::" + (m != null ? m.Name : "<?>");
								_log?.LogMessage(line);
							}
						}
					}
					catch
					{
					}
				}

				_copyOtherDbStopwatch = Stopwatch.StartNew();
				LogObjectDbCounts(__instance, "CopyOtherDB:prefix");
			}
			catch
			{
			}

			return true;
		}

		private static void ObjectDbCopyOtherDbPostfix(object __instance)
		{
			try
			{
				if (_copyOtherDbStopwatch != null)
				{
					_copyOtherDbStopwatch.Stop();
					CacheMetrics.Add("Valheim.ObjectDB.CopyOtherDB", _copyOtherDbStopwatch.ElapsedTicks);
					if (CacheConfig.VerboseDiagnostics)
						_log?.LogMessage($"CacheFork: DIAG timing ObjectDB.CopyOtherDB = {_copyOtherDbStopwatch.ElapsedMilliseconds} мс.");
					_copyOtherDbStopwatch = null;
				}
				if (CacheConfig.VerboseDiagnostics)
					LogObjectDbCounts(__instance, "CopyOtherDB:postfix");
			}
			catch
			{
			}
		}

		private static void ObjectDbUpdateRegistersPrefix(object __instance)
		{
			try
			{
				if (!CacheConfig.VerboseDiagnostics && !CacheManager.RestoreModeActive)
					return;
				_updateRegistersStopwatch = Stopwatch.StartNew();
			}
			catch
			{
			}
		}

		private static void ObjectDbUpdateRegistersPostfix(object __instance)
		{
			try
			{
				if (_updateRegistersStopwatch == null)
					return;
				_updateRegistersStopwatch.Stop();
				var ms = _updateRegistersStopwatch.ElapsedMilliseconds;
				var shouldLog = CacheConfig.VerboseDiagnostics || ms >= 10;
				if (!shouldLog && CacheManager.RestoreModeActive && _updateRegistersLoggedCount < 5)
				{
					_updateRegistersLoggedCount++;
					shouldLog = true;
				}

				if (shouldLog)
					_log?.LogMessage($"CacheFork: DIAG timing ObjectDB.UpdateRegisters = {ms} мс.");
				CacheMetrics.Add("Valheim.ObjectDB.UpdateRegisters", _updateRegistersStopwatch.ElapsedTicks);
				_updateRegistersStopwatch = null;
			}
			catch
			{
			}
		}

		private static bool ApplyFastCopyOtherDb(object targetDb, object otherDb)
		{
			if (targetDb == null || otherDb == null)
				return false;

			try
			{
				var t = targetDb.GetType();
				var otherT = otherDb.GetType();
				if (t != otherT)
					return false;

				var items = TryGetObject(otherDb, "m_items");
				var recipes = TryGetObject(otherDb, "m_recipes");
				var status = TryGetObject(otherDb, "m_StatusEffects") ?? TryGetObject(otherDb, "m_statusEffects");
				var otherByHash = TryGetObject(otherDb, "m_itemByHash");
				var otherByData = TryGetObject(otherDb, "m_itemByData");

				if (items == null)
					return false;

				if (!TrySetObject(targetDb, "m_items", items))
					return false;
				TrySetObject(targetDb, "m_recipes", recipes);
				TrySetObject(targetDb, "m_StatusEffects", status);
				TrySetObject(targetDb, "m_statusEffects", status);

				// Важно: словари должны быть не null (Jotunn.ModQuery делает new Dictionary(existing)).
				if (otherByHash != null)
					TrySetObject(targetDb, "m_itemByHash", otherByHash);
				else
					EnsureObjectDbDictionariesNotNull(targetDb);
				if (otherByData != null)
					TrySetObject(targetDb, "m_itemByData", otherByData);
				else
					EnsureObjectDbDictionariesNotNull(targetDb);

				var update = AccessTools.Method(t, "UpdateRegisters");
				if (update != null)
					update.Invoke(targetDb, null);

				return true;
			}
			catch
			{
				return false;
			}
		}

		private static void EnsureObjectDbDictionariesNotNull(object objectDb)
		{
			if (objectDb == null)
				return;

			try
			{
				var type = objectDb.GetType();
				EnsureFieldNotNull(objectDb, type, "m_itemByHash");
				EnsureFieldNotNull(objectDb, type, "m_itemByData");
			}
			catch
			{
			}
		}

		private static void EnsureFieldNotNull(object instance, Type type, string fieldName)
		{
			if (instance == null || type == null || string.IsNullOrEmpty(fieldName))
				return;

			try
			{
				var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field == null)
					return;

				var current = field.GetValue(instance);
				if (current != null)
					return;

				var created = Activator.CreateInstance(field.FieldType);
				field.SetValue(instance, created);
			}
			catch
			{
			}
		}

		private static object TryGetObject(object instance, string memberName)
		{
			if (instance == null || string.IsNullOrEmpty(memberName))
				return null;

			try
			{
				var type = instance.GetType();
				var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null)
					return field.GetValue(instance);

				var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (prop != null && prop.CanRead)
					return prop.GetValue(instance, null);
			}
			catch
			{
			}

			return null;
		}

		private static bool TrySetObject(object instance, string memberName, object value)
		{
			if (instance == null || string.IsNullOrEmpty(memberName))
				return false;

			try
			{
				var type = instance.GetType();
				var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null)
				{
					field.SetValue(instance, value);
					return true;
				}
			}
			catch
			{
			}

			return false;
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
