using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class JotunnStateCachePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jotunn.State";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _awakePatched;
		private static ManualLogSource _log;
		private static bool _snapshotDone;
		private static bool _bulkSnapshotActive;
		private static readonly HashSet<string> AttachedEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static MethodInfo _getItems;
		private static MethodInfo _getRecipes;
		private static MethodInfo _getStatusEffects;
		private static MethodInfo _getPieces;
		private static MethodInfo _getPieceTables;
		private static MethodInfo _getPrefabs;
		private static Type _itemManager;
		private static Type _pieceManager;
		private static Type _prefabManager;

		internal static bool IsInitialized => _initialized;

		internal static void SnapshotNow(ManualLogSource log)
		{
			if (!CacheConfig.EnableStateCache)
				return;

			lock (PatchLock)
			{
				if (_snapshotDone)
					return;
				_snapshotDone = true;
			}

			try
			{
				_log = log ?? _log;
				_bulkSnapshotActive = true;

				// Если Initialize ещё не вызывался (или Jotunn подтянулся позже) — попытаться собрать MethodInfo.
				if (_getItems == null && _getPrefabs == null)
				{
					var modRegistry = AccessTools.TypeByName("Jotunn.Utils.ModRegistry");
					if (modRegistry != null)
					{
						_getItems = AccessTools.Method(modRegistry, "GetItems");
						_getRecipes = AccessTools.Method(modRegistry, "GetRecipes");
						_getStatusEffects = AccessTools.Method(modRegistry, "GetStatusEffects");
						_getPieces = AccessTools.Method(modRegistry, "GetPieces");
						_getPieceTables = AccessTools.Method(modRegistry, "GetPieceTables");
						_getPrefabs = AccessTools.Method(modRegistry, "GetPrefabs");
					}
				}

				if (_getItems == null && _getRecipes == null && _getPieces == null && _getPrefabs == null)
					return;

				var beforeCount = JotunnStateCache.GetEntries("Prefab").Count +
				                JotunnStateCache.GetEntries("Item").Count +
				                JotunnStateCache.GetEntries("Piece").Count;

				OnItemsRegistered();
				OnPiecesRegistered();
				OnPrefabsRegistered();
				JotunnStateCache.Save(CacheManager.Log);
				_bulkSnapshotActive = false;

				var afterCount = JotunnStateCache.GetEntries("Prefab").Count +
				               JotunnStateCache.GetEntries("Item").Count +
				               JotunnStateCache.GetEntries("Piece").Count;

				if (CacheConfig.VerboseDiagnostics)
					_log?.LogMessage($"CacheFork: state-cache снапшот registries выполнен (записей: {afterCount}, добавлено: {Math.Max(0, afterCount - beforeCount)}).");
			}
			catch (Exception ex)
			{
				_bulkSnapshotActive = false;
				_log?.LogWarning($"CacheFork: ошибка при снапшоте state-cache ({ex.Message}).");
			}
		}

		internal static void Initialize(ManualLogSource log)
		{
			if (_initialized || !CacheConfig.EnableStateCache)
				return;

			lock (PatchLock)
			{
				if (_initialized || !CacheConfig.EnableStateCache)
					return;

				_log = log ?? _log;

				_itemManager = AccessTools.TypeByName("Jotunn.Managers.ItemManager");
				_pieceManager = AccessTools.TypeByName("Jotunn.Managers.PieceManager");
				_prefabManager = AccessTools.TypeByName("Jotunn.Managers.PrefabManager");
				var modRegistry = AccessTools.TypeByName("Jotunn.Utils.ModRegistry");

				if (_itemManager == null && _pieceManager == null && _prefabManager == null)
				{
					_log?.LogMessage("CacheFork: Jotunn не загружен, кеш состояния будет применен позже.");
					return;
				}

				if (modRegistry == null)
				{
					_log?.LogWarning("CacheFork: ModRegistry Jotunn не найден, кеш состояния отключен.");
					return;
				}

				_getItems = AccessTools.Method(modRegistry, "GetItems");
				_getRecipes = AccessTools.Method(modRegistry, "GetRecipes");
				_getStatusEffects = AccessTools.Method(modRegistry, "GetStatusEffects");
				_getPieces = AccessTools.Method(modRegistry, "GetPieces");
				_getPieceTables = AccessTools.Method(modRegistry, "GetPieceTables");
				_getPrefabs = AccessTools.Method(modRegistry, "GetPrefabs");

				// Важно: не трогаем Manager.Instance на этом этапе, иначе можно случайно запустить .cctor менеджеров
				// раньше Jotunn.Main.Awake и до установки защитных патчей.
				EnsureAwakeHook();

				_initialized = true;
				_log?.LogMessage("CacheFork: Jotunn кеш состояния подключен (events, deferred до Awake).");
			}
		}

		private static void EnsureAwakeHook()
		{
			if (_awakePatched)
				return;

			try
			{
				var mainType = AccessTools.TypeByName("Jotunn.Main");
				var awake = AccessTools.Method(mainType, "Awake");
				if (awake == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					awake,
					postfix: new HarmonyMethod(typeof(JotunnStateCachePatcher), nameof(MainAwakePostfix))
					{
						priority = Priority.Last
					});
				_awakePatched = true;
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить deferred-подписки state-cache ({ex.Message}).");
			}
		}

		private static void MainAwakePostfix()
		{
			try
			{
				AttachEventToInstance(_itemManager, "OnItemsRegistered", nameof(OnItemsRegistered));
				AttachEventToInstance(_pieceManager, "OnPiecesRegistered", nameof(OnPiecesRegistered));
				AttachEventToInstance(_prefabManager, "OnPrefabsRegistered", nameof(OnPrefabsRegistered));
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка при deferred-подписке state-cache ({ex.Message}).");
			}
		}

		private static void AttachEventToInstance(Type managerType, string eventName, string handlerName)
		{
			if (managerType == null || string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(handlerName))
				return;

			if (AttachedEvents.Contains(eventName))
				return;

			try
			{
				var instance = GetManagerInstance(managerType);
				if (instance == null)
					return;

				var evt = managerType.GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (evt == null)
					return;

				var handler = AccessTools.Method(typeof(JotunnStateCachePatcher), handlerName);
				if (handler == null)
					return;

				var del = Delegate.CreateDelegate(evt.EventHandlerType, handler);
				evt.AddEventHandler(instance, del);
				AttachedEvents.Add(eventName);

				if (CacheConfig.VerboseDiagnostics)
					_log?.LogMessage($"CacheFork: state-cache подписка на {managerType.Name}.{eventName} установлена.");
			}
			catch
			{
			}

			return;
		}

		private static object GetManagerInstance(Type managerType)
		{
			var prop = managerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (prop == null)
				return null;

			try
			{
				return prop.GetValue(null, null);
			}
			catch
			{
				return null;
			}
		}

		private static void OnItemsRegistered()
		{
			CacheFromRegistry("Item", _getItems);
			CacheFromRegistry("Recipe", _getRecipes);
			CacheFromRegistry("StatusEffect", _getStatusEffects);
			if (!_bulkSnapshotActive)
				JotunnStateCache.Save(CacheManager.Log);
		}

		private static void OnPiecesRegistered()
		{
			CacheFromRegistry("Piece", _getPieces);
			CacheFromRegistry("PieceTable", _getPieceTables);
			if (!_bulkSnapshotActive)
				JotunnStateCache.Save(CacheManager.Log);
		}

		private static void OnPrefabsRegistered()
		{
			CacheFromRegistry("Prefab", _getPrefabs);
			if (!_bulkSnapshotActive)
				JotunnStateCache.Save(CacheManager.Log);
		}

		private static void CacheFromRegistry(string kind, MethodInfo method)
		{
			if (string.IsNullOrEmpty(kind) || method == null || !CacheConfig.EnableStateCache)
				return;

			try
			{
				var result = method.Invoke(null, null) as IEnumerable;
				if (result == null)
					return;

				foreach (var entry in result)
				{
					if (entry == null)
						continue;

					var name = ExtractName(entry);
					if (string.IsNullOrEmpty(name))
						continue;

					var modGuid = ExtractModGuid(entry);
					JotunnStateCache.RecordEntry(kind, name, modGuid, entry, CacheManager.Log);
				}
			}
			catch (Exception ex)
			{
				CacheManager.Log?.LogWarning($"CacheFork: ошибка при сохранении состояния Jotunn ({kind}) ({ex.Message}).");
			}
		}

		private static string ExtractName(object payload)
		{
			var type = payload.GetType();
			var nameValue = TryGetString(type, payload, "Name") ?? TryGetString(type, payload, "PrefabName");
			if (!string.IsNullOrEmpty(nameValue))
				return nameValue;

			var prefab = TryGetObject(type, payload, "Prefab")
			             ?? TryGetObject(type, payload, "ItemPrefab")
			             ?? TryGetObject(type, payload, "PiecePrefab")
			             ?? TryGetObject(type, payload, "PrefabToRegister");

			return GetUnityName(prefab);
		}

		private static string ExtractModGuid(object payload)
		{
			var type = payload.GetType();
			var sourceMod = TryGetObject(type, payload, "SourceMod") as BepInPlugin;
			return sourceMod?.GUID;
		}

		private static string GetUnityName(object unityObject)
		{
			if (unityObject == null)
				return null;

			try
			{
				var prop = unityObject.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
				return prop?.GetValue(unityObject, null) as string;
			}
			catch
			{
				return null;
			}
		}

		private static string TryGetString(Type type, object instance, string name)
		{
			var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (prop != null && prop.PropertyType == typeof(string))
				return prop.GetValue(instance, null) as string;

			var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null && field.FieldType == typeof(string))
				return field.GetValue(instance) as string;

			return null;
		}

		private static object TryGetObject(Type type, object instance, string name)
		{
			var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (prop != null)
				return prop.GetValue(instance, null);

			var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
				return field.GetValue(instance);

			return null;
		}
	}
}
