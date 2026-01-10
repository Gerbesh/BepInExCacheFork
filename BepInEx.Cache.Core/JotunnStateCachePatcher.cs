using System;
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
		private static readonly HashSet<string> RestoredKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static bool _patched;

		internal static bool IsInitialized => _patched;

		internal static void Initialize(ManualLogSource log)
		{
			if (_patched || !CacheConfig.EnableStateCache)
				return;

			lock (PatchLock)
			{
				if (_patched || !CacheConfig.EnableStateCache)
					return;

				var itemManager = AccessTools.TypeByName("Jotunn.Managers.ItemManager");
				var pieceManager = AccessTools.TypeByName("Jotunn.Managers.PieceManager");
				var prefabManager = AccessTools.TypeByName("Jotunn.Managers.PrefabManager");

				if (itemManager == null && pieceManager == null && prefabManager == null)
				{
					log?.LogMessage("CacheFork: Jotunn не загружен, патч состояния будет применен позже.");
					return;
				}

				var harmony = new Harmony(HarmonyId);

				var customItemType = AccessTools.TypeByName("Jotunn.Entities.CustomItem");
				var customRecipeType = AccessTools.TypeByName("Jotunn.Entities.CustomRecipe");
				var customStatusEffectType = AccessTools.TypeByName("Jotunn.Entities.CustomStatusEffect");
				var customPieceType = AccessTools.TypeByName("Jotunn.Entities.CustomPiece");
				var customPieceTableType = AccessTools.TypeByName("Jotunn.Entities.CustomPieceTable");
				var customPrefabType = AccessTools.TypeByName("Jotunn.Entities.CustomPrefab");

				PatchRecord(harmony, itemManager, "AddItem", customItemType, nameof(AddItemPostfix), log);
				PatchRecord(harmony, itemManager, "AddRecipe", customRecipeType, nameof(AddRecipePostfix), log);
				PatchRecord(harmony, itemManager, "AddStatusEffect", customStatusEffectType, nameof(AddStatusEffectPostfix), log);
				PatchRecord(harmony, pieceManager, "AddPiece", customPieceType, nameof(AddPiecePostfix), log);
				PatchRecord(harmony, pieceManager, "AddPieceTable", customPieceTableType, nameof(AddPieceTablePostfix), log);
				PatchRecord(harmony, prefabManager, "AddPrefab", customPrefabType, nameof(AddPrefabPostfix), log);

				PatchRestore(harmony, itemManager, "Item", nameof(RestoreItemManagerPostfix));
				PatchRestore(harmony, itemManager, "Recipe", nameof(RestoreItemManagerPostfix));
				PatchRestore(harmony, itemManager, "StatusEffect", nameof(RestoreItemManagerPostfix));
				PatchRestore(harmony, pieceManager, "Piece", nameof(RestorePieceManagerPostfix));
				PatchRestore(harmony, pieceManager, "PieceTable", nameof(RestorePieceManagerPostfix));
				PatchRestore(harmony, prefabManager, "Prefab", nameof(RestorePrefabManagerPostfix));

				_patched = true;
				log?.LogMessage("CacheFork: Jotunn кеш состояния подключен (registries).");
			}
		}

		private static void PatchRecord(Harmony harmony, Type targetType, string methodName, Type parameterType, string postfixName, ManualLogSource log)
		{
			if (targetType == null)
				return;

			if (parameterType == null)
				return;

			var method = AccessTools.Method(targetType, methodName, new[] { parameterType });
			if (method == null)
				return;

			var postfix = AccessTools.Method(typeof(JotunnStateCachePatcher), postfixName);
			if (postfix == null)
				return;

			try
			{
				harmony.Patch(method, postfix: new HarmonyMethod(postfix));
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось пропатчить {targetType.FullName}.{methodName}({parameterType.FullName}) ({ex.Message}).");
			}
		}

		private static void PatchRestore(Harmony harmony, Type targetType, string kind, string postfixName)
		{
			if (targetType == null)
				return;

			var restoreMethod = FindLifecycleMethod(targetType);
			if (restoreMethod == null)
				return;

			var postfix = AccessTools.Method(typeof(JotunnStateCachePatcher), postfixName);
			if (postfix == null)
				return;

			harmony.Patch(restoreMethod, postfix: new HarmonyMethod(postfix));
		}

		private static MethodInfo FindLifecycleMethod(Type managerType)
		{
			return AccessTools.Method(managerType, "Awake")
			       ?? AccessTools.Method(managerType, "Start")
			       ?? AccessTools.Method(managerType, "Init");
		}

		private static void RestoreItemManagerPostfix(object __instance)
		{
			RestoreFromCache(__instance, "Item", "AddItem");
			RestoreFromCache(__instance, "Recipe", "AddRecipe");
			RestoreFromCache(__instance, "StatusEffect", "AddStatusEffect");
		}

		private static void RestorePieceManagerPostfix(object __instance)
		{
			RestoreFromCache(__instance, "Piece", "AddPiece");
			RestoreFromCache(__instance, "PieceTable", "AddPieceTable");
		}

		private static void RestorePrefabManagerPostfix(object __instance)
		{
			RestoreFromCache(__instance, "Prefab", "AddPrefab");
		}

		private static void RestoreFromCache(object manager, string kind, string addMethodName)
		{
			if (manager == null || string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(addMethodName))
				return;

			if (!CacheManager.CacheHit || !CacheConfig.EnableStateCache)
				return;

			if (RestoredKinds.Contains(kind))
				return;

			JotunnStateCache.EnsureLoaded(CacheManager.Log);
			if (!JotunnStateCache.IsValid)
				return;

			var entries = JotunnStateCache.GetEntries(kind);
			if (entries.Count == 0)
				return;

			var addMethod = FindAddMethod(manager.GetType(), addMethodName, entries);
			if (addMethod == null)
				return;

			var restored = 0;
			JotunnStateCache.BeginRestore();
			try
			{
				foreach (var entry in entries)
				{
					var payload = JotunnStateCache.TryDeserializePayload(entry.Payload, CacheManager.Log);
					if (payload == null)
						continue;

					if (!TryInvokeAdd(addMethod, manager, payload))
						continue;

					restored++;
				}
			}
			finally
			{
				JotunnStateCache.EndRestore();
			}

			if (restored > 0)
			{
				RestoredKinds.Add(kind);
				CacheManager.Log?.LogMessage($"CacheFork: Jotunn состояние восстановлено ({kind}: {restored}).");
			}
		}

		private static MethodInfo FindAddMethod(Type managerType, string methodName, List<JotunnStateCache.RegistryEntry> entries)
		{
			if (managerType == null || string.IsNullOrEmpty(methodName))
				return null;

			foreach (var method in AccessTools.GetDeclaredMethods(managerType))
			{
				if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
					continue;

				if (method.IsStatic)
					continue;

				var parameters = method.GetParameters();
				if (parameters.Length != 1)
					continue;

				var parameterType = parameters[0].ParameterType;
				foreach (var entry in entries)
				{
					var payload = JotunnStateCache.TryDeserializePayload(entry.Payload, CacheManager.Log);
					if (payload != null && parameterType.IsInstanceOfType(payload))
						return method;
				}
			}

			return null;
		}

		private static bool TryInvokeAdd(MethodInfo method, object manager, object payload)
		{
			try
			{
				var parameters = method.GetParameters();
				if (parameters.Length == 0)
					return false;

				var args = new object[parameters.Length];
				if (!parameters[0].ParameterType.IsInstanceOfType(payload))
					return false;

				args[0] = payload;

				for (var i = 1; i < parameters.Length; i++)
					args[i] = GetDefault(parameters[i].ParameterType);

				method.Invoke(manager, args);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static object GetDefault(Type type)
		{
			if (type == typeof(bool))
				return false;
			if (type.IsValueType)
				return Activator.CreateInstance(type);
			return null;
		}

		private static void AddItemPostfix(object __0)
		{
			Record("Item", __0);
		}

		private static void AddPiecePostfix(object __0)
		{
			Record("Piece", __0);
		}

		private static void AddPieceTablePostfix(object __0)
		{
			Record("PieceTable", __0);
		}

		private static void AddRecipePostfix(object __0)
		{
			Record("Recipe", __0);
		}

		private static void AddStatusEffectPostfix(object __0)
		{
			Record("StatusEffect", __0);
		}

		private static void AddPrefabPostfix(object __0)
		{
			Record("Prefab", __0);
		}

		private static void Record(string kind, object payload)
		{
			if (payload == null || !CacheConfig.EnableStateCache)
				return;

			var name = ExtractName(payload);
			if (string.IsNullOrEmpty(name))
				return;

			var modGuid = ExtractModGuid(payload);
			JotunnStateCache.RecordEntry(kind, name, modGuid, payload, CacheManager.Log);
			JotunnStateCache.Save(CacheManager.Log);
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
