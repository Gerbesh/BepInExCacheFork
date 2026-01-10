using System;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	/// <summary>
	/// Дополнительный гвард для matchLocalizedItem функции в Jewelcrafting.
	/// Проблема: matchLocalizedItem ищет предмет по переведённому имени (XUnity), 
	/// и когда не находит - вызывает FirstOrDefault на null коллекции → NRE.
	/// 
	/// Решение: перехватываем вызовы LINQ FirstOrDefault и добавляем fallback поиск
	/// по оригинальному имени, если переведённый вариант не сработал.
	/// 
	/// Примечание: это сложный патч, потому что matchLocalizedItem - это локальная функция.
	/// Основной guard в JewelcraftingNullSafePatcher.cs подавляет исключение на уровне EnsureDropCache.
	/// Этот модуль предназначен для будущих оптимизаций, если потребуется более точное управление.
	/// </summary>
	internal static class JewelcraftingMatchLocalizedItemGuard
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jewelcrafting.MatchLocalizedItem";
		private static ManualLogSource _log;
		private static bool _initialized;

		internal static void Initialize(ManualLogSource log)
		{
			if (_initialized)
				return;

			_initialized = true;
			_log = log ?? _log;

			// На данный момент это заглушка.
			// matchLocalizedItem - локальная функция, и её сложно патчить напрямую.
			// Основной guard уже работает на уровне EnsureDropCache, подавляя исключение
			// и позволяя игре использовать базовый дроп.
			// 
			// Если в будущем потребуется более тонкое управление (например, fallback поиск
			// по оригинальному имени), это можно реализовать здесь через reflection на
			// вложенные типы компилятором функции.

			_log?.LogDebug("CacheFork: MatchLocalizedItemGuard инициализирован (данные фичи в работе).");
		}
	}
}
