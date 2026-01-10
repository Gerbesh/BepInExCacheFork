using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class JewelcraftingLocalizationGuardPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jewelcrafting.LocalizationGuard";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _localizationPatched;
		private static bool _ensureDropCachePatched;
		private static AssemblyLoadEventHandler _assemblyHandler;
		private static bool _assemblyHooked;
		private static ManualLogSource _log;

		[ThreadStatic]
		private static bool _jewelcraftingScopeActive;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;

				_initialized = true;
				_log = log ?? _log;

				if (!CacheConfig.JewelcraftingLocalizationGuard)
					return;

				TryPatchNowOrDefer();
			}
		}

		private static void TryPatchNowOrDefer()
		{
			TryPatchLocalization();
			TryPatchJewelcrafting();

			if (_localizationPatched && _ensureDropCachePatched)
				return;

			if (_assemblyHooked)
				return;

			_assemblyHandler = (sender, args) =>
			{
				try
				{
					TryPatchLocalization();
					TryPatchJewelcrafting();
					if (_localizationPatched && _ensureDropCachePatched)
					{
						AppDomain.CurrentDomain.AssemblyLoad -= _assemblyHandler;
						_assemblyHooked = false;
					}
				}
				catch
				{
				}
			};
			AppDomain.CurrentDomain.AssemblyLoad += _assemblyHandler;
			_assemblyHooked = true;
		}

		private static void TryPatchLocalization()
		{
			if (_localizationPatched)
				return;

			try
			{
				var localizationType = AccessTools.TypeByName("Localization");
				if (localizationType == null)
					return;

				var method = AccessTools.Method(localizationType, "Localize", new[] { typeof(string) });
				if (method == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					method,
					prefix: new HarmonyMethod(typeof(JewelcraftingLocalizationGuardPatcher), nameof(LocalizePrefix))
					{
						priority = Priority.First
					});

				_localizationPatched = true;
				_log?.LogMessage("CacheFork: Jewelcrafting guard: патч Localization.Localize подключен.");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить патч Localization.Localize ({ex.Message}).");
			}
		}

		private static void TryPatchJewelcrafting()
		{
			if (_ensureDropCachePatched)
				return;

			try
			{
				var type = AccessTools.TypeByName("Jewelcrafting.LootSystem.EquipmentDrops");
				if (type == null)
					return;

				var method = AccessTools.Method(type, "EnsureDropCache");
				if (method == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					method,
					prefix: new HarmonyMethod(typeof(JewelcraftingLocalizationGuardPatcher), nameof(EnsureDropCachePrefix))
					{
						priority = Priority.First
					},
					postfix: new HarmonyMethod(typeof(JewelcraftingLocalizationGuardPatcher), nameof(EnsureDropCachePostfix))
					{
						priority = Priority.Last
					},
					finalizer: new HarmonyMethod(typeof(JewelcraftingLocalizationGuardPatcher), nameof(EnsureDropCacheFinalizer))
					{
						priority = Priority.Last
					});

				_ensureDropCachePatched = true;
				_log?.LogMessage("CacheFork: Jewelcrafting guard: патч EnsureDropCache подключен.");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить патч EnsureDropCache ({ex.Message}).");
			}
		}

		private static void EnsureDropCachePrefix()
		{
			try
			{
				if (!CacheConfig.JewelcraftingLocalizationGuard)
					return;
				_jewelcraftingScopeActive = true;
			}
			catch
			{
			}
		}

		private static void EnsureDropCachePostfix()
		{
			_jewelcraftingScopeActive = false;
		}

		private static Exception EnsureDropCacheFinalizer(Exception __exception)
		{
			_jewelcraftingScopeActive = false;
			return __exception;
		}

		private static bool LocalizePrefix(string text, ref string __result)
		{
			try
			{
				if (!CacheConfig.JewelcraftingLocalizationGuard || !_jewelcraftingScopeActive)
					return true;

				if (text == null)
					return true;

				__result = text;
				return false;
			}
			catch
			{
				return true;
			}
		}
	}
}
