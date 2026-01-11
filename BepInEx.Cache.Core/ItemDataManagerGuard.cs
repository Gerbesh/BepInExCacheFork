using System;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	/// <summary>
	/// Защита от NRE в AzuCraftyBoxes при обращении к ItemDataManager.
	/// Если state cache восстановил поломанные данные, это обнаружится здесь и инвалидирует кеш.
	/// </summary>
	internal static class ItemDataManagerGuard
	{
		private const string HarmonyId = "BepInEx.CacheFork.ItemDataManager.Guard";
		private static bool _patched;
		private static ManualLogSource _log;
		private static bool _errorDetected;

		internal static void Initialize(ManualLogSource log)
		{
			if (_patched || !CacheConfig.EnableStateCache)
				return;

			_log = log;
			_patched = true;

			try
			{
				// Получаем тип ItemDataManager
				var itemDataManagerType = AccessTools.TypeByName("AzuCraftyBoxes.ItemDataManager");
				if (itemDataManagerType == null)
					return;

				// Ищем метод Get<T> или ItemInfo.Get<T>
				var itemInfoType = AccessTools.TypeByName("AzuCraftyBoxes.ItemDataManager+ItemInfo");
				if (itemInfoType == null)
					return;

				// Патчим Get<T> метод - это где происходит ошибка
				var getMethod = AccessTools.Method(itemInfoType, "Get");
				if (getMethod == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					getMethod,
					finalizer: new HarmonyMethod(typeof(ItemDataManagerGuard), nameof(GetFinalizer))
					{
						priority = Priority.Last
					});

				_log?.LogMessage("CacheFork: ItemDataManager guard подключен для защиты от state cache ошибок.");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить ItemDataManager guard ({ex.Message}).");
			}
		}

		/// <summary>
		/// Финализатор: если ItemDataManager.Get выбросил исключение о несовместимых типах,
		/// значит state cache восстановил поломанные данные.
		/// Инвалидируем кеш и позволяем пересчитаться.
		/// </summary>
		private static Exception GetFinalizer(Exception __exception)
		{
			if (__exception == null)
				return null;

			// Проверяем, это ли ошибка о несовместимых типах
			if (__exception.Message.Contains("class not inheriting from ItemData") || 
			    __exception.InnerException?.Message.Contains("class not inheriting from ItemData") == true)
			{
				// Впервые? Инвалидируем state cache
				if (!_errorDetected)
				{
					_errorDetected = true;
					_log?.LogWarning("CacheFork: обнаружена ошибка ItemDataManager из-за поломанного state cache. Инвалидируем и пересчитываем на следующем запуске.");
					JotunnStateCache.Invalidate(_log);
					CacheManager.Log?.LogMessage("CacheFork: state cache инвалидирован, полная пересборка на следующем запуске.");
				}

				// Подавляем исключение, позволяем игре продолжаться
				return null;
			}

			// Другие исключения проходят дальше
			return __exception;
		}
	}
}
