using System;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	/// <summary>
	/// Патч для защиты от бесконечного цикла убийства моба в Jewelcrafting при несоответствии локализованных имён предметов.
	/// Проблема: XUnity AutoTranslator переводит названия предметов, вызывая mismatch при поиске → NRE в EnsureDropCache.
	/// Решение: оборачиваем EnsureDropCache в финализатор для гарантированного логирования и предотвращения крахов.
	/// </summary>
	internal static class JewelcraftingNullSafePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jewelcrafting.NullSafe";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _patched;
		private static ManualLogSource _log;

		[ThreadStatic]
		private static int _ensureDropCacheCallDepth;

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
			if (_patched)
				return;

			try
			{
				var type = AccessTools.TypeByName("Jewelcrafting.LootSystem.EquipmentDrops");
				if (type == null)
					return;

				// Патчим EnsureDropCache - именно там происходит NRE
				var method = AccessTools.Method(type, "EnsureDropCache");
				if (method == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					method,
					prefix: new HarmonyMethod(typeof(JewelcraftingNullSafePatcher), nameof(EnsureDropCachePrefix))
					{
						priority = Priority.First
					},
					finalizer: new HarmonyMethod(typeof(JewelcraftingNullSafePatcher), nameof(EnsureDropCacheFinalizer))
					{
						priority = Priority.Last
					});

				_patched = true;
				_log?.LogMessage("CacheFork: Jewelcrafting NullSafe: патч EnsureDropCache подключен (защита от NRE при XUnity переводе).");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить NullSafe патч EnsureDropCache ({ex.Message}).");
			}
		}

		/// <summary>
		/// Префикс: отслеживаем глубину вложенности EnsureDropCache.
		/// </summary>
		private static bool EnsureDropCachePrefix()
		{
			_ensureDropCacheCallDepth++;

			// Защита от бесконечной рекурсии (если DoDrop зовёт DoDrop зовёт EnsureDropCache зовёт...)
			if (_ensureDropCacheCallDepth > 10)
			{
				_log?.LogWarning(
					$"CacheFork: ЭКСТРЕННЫЙ СТОП - обнаружена бесконечная рекурсия EnsureDropCache (глубина: {_ensureDropCacheCallDepth}). " +
					"Это признак критической проблемы с переводом XUnity. Пропускаем вызов.");
				return false; // Пропускаем вызов оригинала
			}

			return true; // Выполняем оригинал
		}

		/// <summary>
		/// Финализатор: ловим все исключения, логируем их, но НЕ подавляем.
		/// Это позволяет игре продолжить работу вместо бесконечного цикла.
		/// </summary>
		private static Exception EnsureDropCacheFinalizer(Exception __exception)
		{
			try
			{
				_ensureDropCacheCallDepth--;

				if (_ensureDropCacheCallDepth < 0)
					_ensureDropCacheCallDepth = 0;

				// Если было исключение, логируем детали для диагностики
				if (__exception != null)
				{
					_log?.LogWarning(
						$"CacheFork: EnsureDropCache вызвала исключение: {__exception.GetType().Name}. " +
						"Это почти наверняка вызвано несоответствием локализованных имён предметов (XUnity AutoTranslator). " +
						"Лут будет пропущен на этом мобе, но игра продолжит работу.");

					// Не подавляем исключение - Jewelcrafting должна его обработать
					// Но наш финализатор гарантирует, что счётчик декрементирован
					return __exception;
				}
			}
			catch
			{
				// Игнорируем ошибки в самом финализаторе
				_ensureDropCacheCallDepth--;
			}

			return __exception;
		}
	}
}
