using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class LocalizationCachePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Localization";
		private static readonly object PatchLock = new object();
		private static bool _patched;
		private static FieldInfo _translationsField;
		private static FieldInfo _cacheField;
		private static MethodInfo _cacheEvictMethod;

		internal static void Initialize(ManualLogSource log)
		{
			if (_patched || !LocalizationCache.IsEnabled)
				return;

			lock (PatchLock)
			{
				if (_patched || !LocalizationCache.IsEnabled)
					return;

				var localizationType = AccessTools.TypeByName("Localization");
				if (localizationType == null)
				{
					log?.LogWarning("CacheFork: класс Localization не найден, кеш локализации отключен.");
					return;
				}

				var setupMethod = AccessTools.Method(localizationType, "SetupLanguage");
				if (setupMethod == null)
				{
					log?.LogWarning("CacheFork: метод Localization.SetupLanguage не найден, кеш локализации отключен.");
					return;
				}

				_translationsField = AccessTools.Field(localizationType, "m_translations");
				_cacheField = AccessTools.Field(localizationType, "m_cache");

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					setupMethod,
					prefix: new HarmonyMethod(typeof(LocalizationCachePatcher), nameof(SetupLanguagePrefix)),
					postfix: new HarmonyMethod(typeof(LocalizationCachePatcher), nameof(SetupLanguagePostfix)));

				_patched = true;
				log?.LogMessage("CacheFork: кеш локализации подключен (патч SetupLanguage).");
			}
		}

		private static bool SetupLanguagePrefix(object __instance, string language, ref bool __result, ref bool __state)
		{
			__state = false;
			if (!LocalizationCache.IsEnabled || string.IsNullOrEmpty(language))
				return true;

			if (!LocalizationCache.TryLoadRuntimeDictionary(language, out var cached, CacheManager.Log))
				return true;

			if (cached == null || cached.Count == 0)
				return true;

			if (!ApplyTranslations(__instance, cached))
				return true;

			__state = true;
			__result = true;
			return false;
		}

		private static void SetupLanguagePostfix(object __instance, string language, bool __state, bool __result)
		{
			if (__state || !__result)
				return;

			if (!LocalizationCache.IsEnabled || string.IsNullOrEmpty(language))
				return;

			if (!TryGetTranslations(__instance, out var translations))
				return;

			if (translations == null || translations.Count == 0)
				return;

			LocalizationCache.SaveRuntimeDictionary(language, translations, CacheManager.Log);
		}

		private static bool ApplyTranslations(object instance, Dictionary<string, string> cached)
		{
			if (!TryGetTranslations(instance, out var target) || target == null)
				return false;

			target.Clear();
			foreach (var pair in cached)
				target[pair.Key] = pair.Value ?? string.Empty;

			EvictTranslationCache(instance);
			return true;
		}

		private static bool TryGetTranslations(object instance, out Dictionary<string, string> translations)
		{
			translations = null;
			if (instance == null || _translationsField == null)
				return false;

			translations = _translationsField.GetValue(instance) as Dictionary<string, string>;
			return translations != null;
		}

		private static void EvictTranslationCache(object instance)
		{
			if (instance == null || _cacheField == null)
				return;

			var cache = _cacheField.GetValue(instance);
			if (cache == null)
				return;

			if (_cacheEvictMethod == null)
				_cacheEvictMethod = AccessTools.Method(cache.GetType(), "EvictAll");

			_cacheEvictMethod?.Invoke(cache, null);
		}
	}
}
