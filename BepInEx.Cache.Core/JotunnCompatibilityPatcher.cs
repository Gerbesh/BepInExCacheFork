using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.Cache.Core
{
	internal static class JotunnCompatibilityPatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.Jotunn.Compatibility";
		private static readonly object PatchLock = new object();
		private static bool _patched;

		internal static bool IsInitialized => _patched;

		internal static void Initialize(ManualLogSource log)
		{
			if (_patched)
				return;

			lock (PatchLock)
			{
				if (_patched)
					return;

				var utilsType = AccessTools.TypeByName("Jotunn.Utils.BepInExUtils");
				if (utilsType == null)
				{
					log?.LogMessage("CacheFork: Jotunn не загружен, патч совместимости будет применен позже.");
					return;
				}

				var getSourceMod = AccessTools.Method(utilsType, "GetSourceModMetadata");
				if (getSourceMod == null)
					return;

				var harmony = new Harmony(HarmonyId);
				try
				{
					harmony.Patch(getSourceMod, postfix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataPostfix)));
					_patched = true;
					log?.LogMessage("CacheFork: Jotunn патч совместимости подключен (GetSourceModMetadata).");
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось пропатчить GetSourceModMetadata ({ex.Message}).");
				}
			}
		}

		private static void GetSourceModMetadataPostfix(ref BepInPlugin __result)
		{
			if (__result != null)
				return;

			try
			{
				var fallback = FindMetadataFromStack();
				if (fallback != null)
					__result = fallback;
			}
			catch
			{
			}
		}

		private static BepInPlugin FindMetadataFromStack()
		{
			var stack = new StackTrace();
			var frames = stack.GetFrames();
			if (frames == null || frames.Length == 0)
				return null;

			foreach (var frame in frames)
			{
				var method = frame.GetMethod();
				var declaringType = method?.DeclaringType;
				var assembly = declaringType?.Assembly;
				if (assembly == null)
					continue;

				foreach (var info in Chainloader.PluginInfos.Values)
				{
					var instance = info.Instance;
					if (instance == null)
						continue;

					if (instance.GetType().Assembly == assembly)
						return info.Metadata;
				}
			}

			return null;
		}
	}
}
