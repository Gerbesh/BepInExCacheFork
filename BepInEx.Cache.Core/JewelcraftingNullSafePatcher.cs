using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	/// <summary>
	/// Патч для защиты от потери лута в Jewelcrafting при несоответствии локализованных имён предметов.
	/// Проблема: XUnity AutoTranslator переводит названия предметов, matchLocalizedItem не находит → null → NRE.
	/// Решение: 
	/// 1. Подписываемся на AssemblyLoad и патчим при загрузке Jewelcrafting
	/// 2. Кешируем успешные результаты EnsureDropCache
	/// 3. Fallback: если ошибка - подавляем и разрешаем базовый дроп
	/// </summary>
	internal static class JewelcraftingNullSafePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jewelcrafting.NullSafe";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _patched;
		private static ManualLogSource _log;
		private static int _assemblyLoadEventCount;

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

				_log?.LogMessage("CacheFork: JewelcraftingNullSafePatcher инициализирован, подписываемся на AssemblyLoad...");

				// Подписываемся на загрузку сборок
				AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

				// Попытаемся патчить сразу, если сборка уже загружена
				TryPatchNow();
			}
		}

		private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
		{
			if (args?.LoadedAssembly == null)
				return;

			try
			{
				string name = args.LoadedAssembly.GetName().Name;
				_assemblyLoadEventCount++;
				
				// Логируем первые 20 сборок для диагностики
				if (_assemblyLoadEventCount <= 20)
					_log?.LogDebug($"CacheFork: AssemblyLoad #{_assemblyLoadEventCount} → {name}");
				
				if (string.Equals(name, "Jewelcrafting", StringComparison.OrdinalIgnoreCase))
				{
					_log?.LogMessage("CacheFork: ⭐ Обнаружена загрузка сборки Jewelcrafting — подключаем NullSafePatcher...");
					TryPatchNow();
				}
			}
			catch
			{
			}
		}

		private static void TryPatchNow()
		{
			if (_patched)
				return;

			try
			{
				var type = AccessTools.TypeByName("Jewelcrafting.LootSystem.EquipmentDrops");
				if (type == null)
				{
					_log?.LogDebug("CacheFork: Jewelcrafting.LootSystem.EquipmentDrops еще не загружен.");
					return;
				}

				var ensureMethod = AccessTools.Method(type, "EnsureDropCache");
				if (ensureMethod == null)
				{
					_log?.LogWarning("CacheFork: Не найден метод EnsureDropCache в Jewelcrafting.LootSystem.EquipmentDrops.");
					return;
				}

				var harmony = new Harmony(HarmonyId);

				// Проверяем, не был ли уже пропатчен (чтобы не было дублей)
				var patches = Harmony.GetPatchInfo(ensureMethod);
				if (patches != null && patches.Prefixes.Count > 0)
				{
					_log?.LogMessage("CacheFork: EnsureDropCache уже пропатчена!");
					_patched = true;
					return;
				}

				// Патчим EnsureDropCache
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

				_patched = true;
				_log?.LogMessage("CacheFork: ✅ Jewelcrafting NullSafe: патч EnsureDropCache успешно подключен!");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка при подключении JewelcraftingNullSafePatcher: {ex.Message}");
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
