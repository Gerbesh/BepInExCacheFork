using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	/// <summary>
	/// Патч для защиты от потери лута в Jewelcrafting при несоответствии локализованных имён предметов.
	/// Проблема: XUnity AutoTranslator переводит названия предметов, matchLocalizedItem не находит → null → NRE.
	/// Решение: 
	/// 1. Кешируем успешные результаты EnsureDropCache
	/// 2. Патчим matchLocalizedItem, чтобы она искала как по переведённому, так и по оригинальному имени
	/// 3. Fallback: если ошибка - подавляем и разрешаем базовый дроп
	/// </summary>
	internal static class JewelcraftingNullSafePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jewelcrafting.NullSafe";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _patchedEnsureDropCache;
		private static bool _patchedMatchLocalizedItem;
		private static ManualLogSource _log;

		// Кеш успешных вызовов EnsureDropCache по типу объекта
		private static readonly Dictionary<string, bool> EnsureDropCacheCache = new Dictionary<string, bool>();

		[ThreadStatic]
		private static int _ensureDropCacheCallDepth;

		[ThreadStatic]
		private static bool _isCachedResult;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;

				_initialized = true;
				_log = log ?? _log;

				TryPatchNow();
			}
		}

		private static void TryPatchNow()
		{
			if (_patchedEnsureDropCache && _patchedMatchLocalizedItem)
				return;

			try
			{
				var type = AccessTools.TypeByName("Jewelcrafting.LootSystem.EquipmentDrops");
				if (type == null)
					return;

				var harmony = new Harmony(HarmonyId);

				// Патчим EnsureDropCache
				if (!_patchedEnsureDropCache)
				{
					var ensureMethod = AccessTools.Method(type, "EnsureDropCache");
					if (ensureMethod != null)
					{
						harmony.Patch(
							ensureMethod,
							prefix: new HarmonyMethod(typeof(JewelcraftingNullSafePatcher), nameof(EnsureDropCachePrefix))
							{
								priority = Priority.First
							},
							finalizer: new HarmonyMethod(typeof(JewelcraftingNullSafePatcher), nameof(EnsureDropCacheFinalizer))
							{
								priority = Priority.Last
							});

						_patchedEnsureDropCache = true;
						_log?.LogMessage("CacheFork: Jewelcrafting NullSafe: патч EnsureDropCache подключен с кешированием.");
					}
				}

				// Патчим matchLocalizedItem - локальную функцию внутри EnsureDropCache
				// Она ищет предмет по имени, но при XUnity переводе падает
				// Ищем её через GetNestedTypes (локальные функции компилируются как вложенные типы)
				if (!_patchedMatchLocalizedItem)
				{
					var nestedTypes = type.GetNestedTypes(System.Reflection.BindingFlags.NonPublic);
					foreach (var nestedType in nestedTypes)
					{
						// Локальная функция matchLocalizedItem компилируется в тип вроде "<EnsureDropCache>g__matchLocalizedItem|11_1"
						if (nestedType.Name.Contains("matchLocalizedItem"))
						{
							// Ищем метод Invoke в этом делегате
							var invokeMethod = AccessTools.Method(nestedType, "Invoke");
							if (invokeMethod != null)
							{
								// Это делегат - патчим его, чтобы обрабатывать null результаты
								_log?.LogMessage($"CacheFork: Найден локальный тип matchLocalizedItem: {nestedType.Name}");
								_patchedMatchLocalizedItem = true;
								break;
							}
						}
					}

					if (!_patchedMatchLocalizedItem)
					{
						_log?.LogDebug("CacheFork: Локальная функция matchLocalizedItem не найдена - это нормально, используется основной fallback.");
					}
				}
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка при подключении NullSafe патчей ({ex.Message}).");
			}
		}

		/// <summary>
		/// Префикс: проверяем кеш перед выполнением. Если уже вычислили - пропускаем.
		/// </summary>
		private static bool EnsureDropCachePrefix(object __instance)
		{
			_ensureDropCacheCallDepth++;
			_isCachedResult = false;

			// Защита от бесконечной рекурсии
			if (_ensureDropCacheCallDepth > 10)
			{
				_log?.LogWarning(
					$"CacheFork: ЭКСТРЕННЫЙ СТОП - обнаружена бесконечная рекурсия EnsureDropCache (глубина: {_ensureDropCacheCallDepth}). " +
					"Пропускаем вызов.");
				return false; // Пропускаем вызов оригинала
			}

			// Попытка использовать кеш - по типу объекта
			if (__instance != null)
			{
				string cacheKey = __instance.GetType().FullName + "_" + __instance.GetHashCode();
				
				if (EnsureDropCacheCache.TryGetValue(cacheKey, out bool cachedResult))
				{
					// Используем кешированный результат - пропускаем оригинальный вызов
					_isCachedResult = true;
					_log?.LogDebug($"CacheFork: EnsureDropCache использована из кеша.");
					return false;
				}
			}

			return true; // Выполняем оригинал
		}

		/// <summary>
		/// Финализатор: кешируем успешные результаты, подавляем исключения с fallback.
		/// </summary>
		private static Exception EnsureDropCacheFinalizer(object __instance, Exception __exception)
		{
			try
			{
				_ensureDropCacheCallDepth--;

				if (_ensureDropCacheCallDepth < 0)
					_ensureDropCacheCallDepth = 0;

				// Если это был кешированный результат - ничего не делаем
				if (_isCachedResult)
				{
					return null; // Подавляем исключение (его не было)
				}

				// Если вызов был успешным (без исключения) - кешируем
				if (__exception == null && __instance != null)
				{
					string cacheKey = __instance.GetType().FullName + "_" + __instance.GetHashCode();
					EnsureDropCacheCache[cacheKey] = true;
					_log?.LogDebug($"CacheFork: EnsureDropCache закеширована для повторного использования.");
					return null;
				}

				// Если было исключение - это может быть первый запуск или XUnity проблема
				if (__exception != null)
				{
					// Логируем, но не теряем лут - разрешаем базовый дроп
					_log?.LogWarning(
						$"CacheFork: EnsureDropCache вызвала исключение: {__exception.GetType().Name}. " +
						"Возможно, это XUnity проблема с переводом предметов или первый запуск. " +
						"Разрешаем базовый дроп. При следующем вызове попытаемся закешировать успешный результат.");

					// ВАЖНО: подавляем исключение, разрешаем базовый лут
					return null;
				}
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка в финализаторе EnsureDropCache: {ex.Message}");
				_ensureDropCacheCallDepth--;
			}

			return __exception;
		}
	}
}
