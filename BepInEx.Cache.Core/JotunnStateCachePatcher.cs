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
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static readonly HashSet<string> AttachedEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static MethodInfo _getItems;
		private static MethodInfo _getRecipes;
		private static MethodInfo _getStatusEffects;
		private static MethodInfo _getPieces;
		private static MethodInfo _getPieceTables;
		private static MethodInfo _getPrefabs;

		internal static bool IsInitialized => _initialized;

		internal static void Initialize(ManualLogSource log)
		{
			if (_initialized || !CacheConfig.EnableStateCache)
				return;

			lock (PatchLock)
			{
				if (_initialized || !CacheConfig.EnableStateCache)
					return;

				var itemManager = AccessTools.TypeByName("Jotunn.Managers.ItemManager");
				var pieceManager = AccessTools.TypeByName("Jotunn.Managers.PieceManager");
				var prefabManager = AccessTools.TypeByName("Jotunn.Managers.PrefabManager");
				var modRegistry = AccessTools.TypeByName("Jotunn.Utils.ModRegistry");

				if (itemManager == null && pieceManager == null && prefabManager == null)
				{
					log?.LogMessage("CacheFork: Jotunn не загружен, кеш состояния будет применен позже.");
					return;
				}

				if (modRegistry == null)
				{
					log?.LogWarning("CacheFork: ModRegistry Jotunn не найден, кеш состояния отключен.");
					return;
				}

				_getItems = AccessTools.Method(modRegistry, "GetItems");
				_getRecipes = AccessTools.Method(modRegistry, "GetRecipes");
				_getStatusEffects = AccessTools.Method(modRegistry, "GetStatusEffects");
				_getPieces = AccessTools.Method(modRegistry, "GetPieces");
				_getPieceTables = AccessTools.Method(modRegistry, "GetPieceTables");
				_getPrefabs = AccessTools.Method(modRegistry, "GetPrefabs");

				AttachEvent(itemManager, "OnItemsRegistered", nameof(OnItemsRegistered), log);
				AttachEvent(pieceManager, "OnPiecesRegistered", nameof(OnPiecesRegistered), log);
				AttachEvent(prefabManager, "OnPrefabsRegistered", nameof(OnPrefabsRegistered), log);

				_initialized = true;
				log?.LogMessage("CacheFork: Jotunn кеш состояния подключен (events).");
			}
		}

		private static void AttachEvent(Type managerType, string eventName, string handlerName, ManualLogSource log)
		{
			if (managerType == null || string.IsNullOrEmpty(eventName))
				return;

			var instance = GetManagerInstance(managerType);
			if (instance == null)
				return;

			var evt = managerType.GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (evt == null)
				return;

			var handler = AccessTools.Method(typeof(JotunnStateCachePatcher), handlerName);
			if (handler == null)
				return;

			if (AttachedEvents.Contains(eventName))
				return;

			try
			{
				var del = Delegate.CreateDelegate(evt.EventHandlerType, handler);
				evt.AddEventHandler(instance, del);
				AttachedEvents.Add(eventName);
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось подписаться на событие {eventName} ({ex.Message}).");
			}
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
		}

		private static void OnPiecesRegistered()
		{
			CacheFromRegistry("Piece", _getPieces);
			CacheFromRegistry("PieceTable", _getPieceTables);
		}

		private static void OnPrefabsRegistered()
		{
			CacheFromRegistry("Prefab", _getPrefabs);
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

				JotunnStateCache.Save(CacheManager.Log);
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
