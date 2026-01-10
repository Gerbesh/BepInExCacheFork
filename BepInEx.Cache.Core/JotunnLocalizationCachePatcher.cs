using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class JotunnLocalizationCachePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jotunn.Localization";
		private static readonly object PatchLock = new object();
		private static bool _patched;
		private static MethodInfo _addTranslationBulk;
		private static PropertyInfo _mapProperty;
		private static PropertyInfo _sourceModProperty;
		private static readonly HashSet<string> ModCacheActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		internal static bool IsInitialized => _patched;

		internal static void Initialize(ManualLogSource log)
		{
			if (_patched || !LocalizationCache.IsEnabled)
				return;

			lock (PatchLock)
			{
				if (_patched || !LocalizationCache.IsEnabled)
					return;

				var customLocalizationType = FindCustomLocalizationType();
				if (customLocalizationType == null)
				{
					log?.LogMessage("CacheFork: Jotunn не загружен, патч локализации будет применен позже.");
					return;
				}

				var addFileByPath = AccessTools.Method(customLocalizationType, "AddFileByPath");
				if (addFileByPath == null)
					return;

				_mapProperty = AccessTools.Property(customLocalizationType, "Map");
				_sourceModProperty = AccessTools.Property(customLocalizationType, "SourceMod");
				_addTranslationBulk = FindAddTranslation(customLocalizationType);

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					addFileByPath,
					prefix: new HarmonyMethod(typeof(JotunnLocalizationCachePatcher), nameof(AddFileByPathPrefix)),
					postfix: new HarmonyMethod(typeof(JotunnLocalizationCachePatcher), nameof(AddFileByPathPostfix)));

				var ctorByMod = AccessTools.Constructor(customLocalizationType, new[] { typeof(BepInPlugin) });
				if (ctorByMod != null)
				{
					harmony.Patch(
						ctorByMod,
						postfix: new HarmonyMethod(typeof(JotunnLocalizationCachePatcher), nameof(CustomLocalizationCtorPostfix)));
				}

				var ctorDefault = AccessTools.Constructor(customLocalizationType, Type.EmptyTypes);
				if (ctorDefault != null)
				{
					harmony.Patch(
						ctorDefault,
						postfix: new HarmonyMethod(typeof(JotunnLocalizationCachePatcher), nameof(CustomLocalizationCtorPostfix)));
				}

				_patched = true;
				log?.LogMessage("CacheFork: Jotunn кеш локализации подключен (AddFileByPath).");
			}
		}

		private static void CustomLocalizationCtorPostfix(object __instance)
		{
			var modGuid = GetModGuid(__instance);
			if (string.IsNullOrEmpty(modGuid))
				return;

			JotunnLocalizationStateCache.EnsureLoaded(CacheManager.Log);
			if (!JotunnLocalizationStateCache.TryGetState(modGuid, out var state))
				return;

			state.CacheValid = JotunnLocalizationStateCache.ValidateState(state);
			if (!state.CacheValid)
				return;

			ApplyTranslations(__instance, state.Translations);
			ModCacheActive.Add(modGuid);
			CacheManager.Log?.LogMessage($"CacheFork: Jotunn локализация восстановлена ({modGuid}).");
		}

		private static bool AddFileByPathPrefix(object __instance, string path, bool isJson, ref object __state)
		{
			__state = null;
			if (!LocalizationCache.IsEnabled || string.IsNullOrEmpty(path))
				return true;

			var modGuid = GetModGuid(__instance);
			if (!string.IsNullOrEmpty(modGuid) && ModCacheActive.Contains(modGuid))
			{
				if (JotunnLocalizationStateCache.TryGetState(modGuid, out var state) &&
					JotunnLocalizationStateCache.IsSourceFileKnown(state, path, isJson))
				{
					return false;
				}

				ModCacheActive.Remove(modGuid);
			}

			if (JotunnLocalizationCache.TryLoadFileCache(path, isJson, out var cached, CacheManager.Log))
			{
				ApplyTranslations(__instance, cached);
				return false;
			}

			__state = SnapshotMap(__instance);
			return true;
		}

		private static void AddFileByPathPostfix(object __instance, string path, bool isJson, object __state)
		{
			if (!LocalizationCache.IsEnabled || string.IsNullOrEmpty(path))
				return;

			if (__state == null)
				return;

			var modGuid = GetModGuid(__instance);
			if (!string.IsNullOrEmpty(modGuid))
				JotunnLocalizationStateCache.RecordSourceFile(modGuid, path, isJson);

			var before = __state as Dictionary<string, Dictionary<string, string>>;
			if (before == null)
				return;

			if (!TryGetMap(__instance, out var after) || after == null)
				return;

			var diff = BuildDiff(before, after);
			if (diff.Count == 0)
				return;

			JotunnLocalizationCache.SaveFileCache(path, isJson, diff, CacheManager.Log);

			if (!string.IsNullOrEmpty(modGuid))
			{
				JotunnLocalizationStateCache.UpdateTranslations(modGuid, after);
				JotunnLocalizationStateCache.Save(CacheManager.Log);
			}
		}

		private static void ApplyTranslations(object instance, Dictionary<string, Dictionary<string, string>> cached)
		{
			if (instance == null || cached == null || cached.Count == 0)
				return;

			foreach (var languageEntry in cached)
			{
				if (string.IsNullOrEmpty(languageEntry.Key))
					continue;
				var payload = languageEntry.Value ?? new Dictionary<string, string>();
				_addTranslationBulk?.Invoke(instance, new object[] { languageEntry.Key, payload });
			}
		}

		private static Dictionary<string, Dictionary<string, string>> SnapshotMap(object instance)
		{
			if (!TryGetMap(instance, out var map) || map == null)
				return null;

			var snapshot = new Dictionary<string, Dictionary<string, string>>(map.Count);
			foreach (var languageEntry in map)
			{
				var language = languageEntry.Key;
				var translations = languageEntry.Value;
				if (string.IsNullOrEmpty(language) || translations == null)
					continue;

				var clone = new Dictionary<string, string>(translations.Count);
				foreach (var entry in translations)
					clone[entry.Key] = entry.Value;
				snapshot[language] = clone;
			}

			return snapshot;
		}

		private static Dictionary<string, Dictionary<string, string>> BuildDiff(Dictionary<string, Dictionary<string, string>> before, Dictionary<string, Dictionary<string, string>> after)
		{
			var diff = new Dictionary<string, Dictionary<string, string>>();
			foreach (var languageEntry in after)
			{
				var language = languageEntry.Key;
				var current = languageEntry.Value;
				if (string.IsNullOrEmpty(language) || current == null)
					continue;

				before.TryGetValue(language, out var previous);
				var delta = new Dictionary<string, string>();
				foreach (var entry in current)
				{
					if (previous == null || !previous.TryGetValue(entry.Key, out var previousValue) || previousValue != entry.Value)
						delta[entry.Key] = entry.Value;
				}

				if (delta.Count > 0)
					diff[language] = delta;
			}

			return diff;
		}

		private static bool TryGetMap(object instance, out Dictionary<string, Dictionary<string, string>> map)
		{
			map = null;
			if (instance == null || _mapProperty == null)
				return false;

			map = _mapProperty.GetValue(instance, null) as Dictionary<string, Dictionary<string, string>>;
			return map != null;
		}

		private static string GetModGuid(object instance)
		{
			if (instance == null || _sourceModProperty == null)
				return null;

			var plugin = _sourceModProperty.GetValue(instance, null) as BepInPlugin;
			return plugin?.GUID;
		}

		private static Type FindCustomLocalizationType()
		{
			try
			{
				foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					var name = assembly.GetName().Name;
					if (!string.Equals(name, "Jotunn", StringComparison.OrdinalIgnoreCase))
						continue;

					var type = assembly.GetType("Jotunn.Entities.CustomLocalization", false);
					if (type != null)
						return type;
				}
			}
			catch
			{
			}

			return null;
		}

		private static MethodInfo FindAddTranslation(Type customLocalizationType)
		{
			if (customLocalizationType == null)
				return null;

			foreach (var method in AccessTools.GetDeclaredMethods(customLocalizationType))
			{
				if (!string.Equals(method.Name, "AddTranslation", StringComparison.Ordinal))
					continue;

				var parameters = method.GetParameters();
				if (parameters.Length != 2)
					continue;

				var dictType = parameters[1].ParameterType;
				if (!dictType.IsGenericType)
					continue;

				if (dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
					continue;

				var args = dictType.GetGenericArguments();
				if (args.Length != 2)
					continue;

				if (args[0] != typeof(string) || args[1] != typeof(string))
					continue;

				return method;
			}

			return null;
		}
	}
}
