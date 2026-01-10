using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class AzuCraftyBoxesCompatibilityPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.AzuCraftyBoxes";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static bool _patched;
		private static AssemblyLoadEventHandler _assemblyHandler;
		private static bool _assemblyHooked;
		private static ManualLogSource _log;
		private static bool _reported;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;

				_initialized = true;
				_log = log ?? _log;

				if (!CacheConfig.AzuCraftyBoxesGuard)
					return;

				TryPatchNowOrDefer();
			}
		}

		private static void TryPatchNowOrDefer()
		{
			TryPatch();

			if (_patched)
				return;

			if (_assemblyHooked)
				return;

			_assemblyHandler = (sender, args) =>
			{
				try
				{
					TryPatch();
					if (_patched)
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

		private static void TryPatch()
		{
			try
			{
				var type = AccessTools.TypeByName("AzuCraftyBoxes.Patches.PlayerHaveRequirementsPatchRBoolInt");
				if (type == null)
					return;

				var method = AccessTools.Method(type, "HaveRequirementItems");
				if (method == null)
					return;

				var harmony = new Harmony(HarmonyId);
				harmony.Patch(
					method,
					prefix: new HarmonyMethod(typeof(AzuCraftyBoxesCompatibilityPatcher), nameof(HaveRequirementItemsPrefix))
					{
						priority = Priority.First
					});

				_log?.LogMessage("CacheFork: патч совместимости AzuCraftyBoxes подключен (guard Recipe.m_item.m_shared).");
				_patched = true;
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось подключить патч AzuCraftyBoxes ({ex.Message}).");
			}
		}

		private static bool HaveRequirementItemsPrefix(object[] __args, ref bool __result)
		{
			try
			{
				if (!CacheConfig.AzuCraftyBoxesGuard)
					return true;

				var recipe = (__args != null && __args.Length > 1) ? __args[1] : null;
				if (recipe == null)
					return true;

				var item = TryGetMember(recipe, "m_item");
				if (item == null)
					return true;

				var shared = TryGetMember(item, "m_shared");
				if (shared != null)
					return true;

				if (!_reported)
				{
					_reported = true;
					_log?.LogWarning("CacheFork: AzuCraftyBoxes guard: у Recipe.m_item отсутствует m_shared. Требования будут считаться невыполненными, чтобы избежать NRE.");
				}

				__result = false;
				return false; // skip original
			}
			catch
			{
				return true;
			}
		}

		private static object TryGetMember(object instance, string memberName)
		{
			if (instance == null || string.IsNullOrEmpty(memberName))
				return null;

			try
			{
				var type = instance.GetType();
				var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null)
					return field.GetValue(instance);

				var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (prop != null && prop.CanRead)
					return prop.GetValue(instance, null);
			}
			catch
			{
			}

			return null;
		}
	}
}
