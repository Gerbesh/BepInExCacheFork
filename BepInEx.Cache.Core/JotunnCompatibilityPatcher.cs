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
		private static BepInPlugin _jotunnMeta;

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

				_jotunnMeta = FindMetadataFromAssembly(utilsType.Assembly) ?? FindAnyPlugin("Jotunn");

				var harmony = new Harmony(HarmonyId);
				try
				{
					harmony.Patch(
						getSourceMod,
						prefix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataPrefix)));
					_patched = true;
					log?.LogMessage("CacheFork: Jotunn патч совместимости подключен (GetSourceModMetadata).");
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось пропатчить GetSourceModMetadata ({ex.Message}).");
				}
			}
		}

		private static bool GetSourceModMetadataPrefix(ref BepInPlugin __result)
		{
			try
			{
				__result =
					FindMetadataFromAssembly(MethodBase.GetCurrentMethod()?.DeclaringType?.Assembly) ??
					FindMetadataFromStack() ??
					_jotunnMeta ??
					FindAnyPlugin("Jotunn") ??
					CreateStubPlugin();
			}
			catch
			{
			}

			return false; // блокируем оригинал полностью
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

				var meta = FindMetadataFromAssembly(assembly);
				if (meta != null)
					return meta;
			}

			return null;
		}

		private static BepInPlugin FindMetadataFromAssembly(Assembly assembly)
		{
			if (assembly == null)
				return null;

			foreach (var info in Chainloader.PluginInfos.Values)
			{
				var instance = info.Instance;
				if (instance == null)
					continue;

				if (instance.GetType().Assembly == assembly)
					return info.Metadata;
			}

			return null;
		}

		private static BepInPlugin FindAnyPlugin(string nameOrGuid)
		{
			if (string.IsNullOrEmpty(nameOrGuid))
				return null;

			foreach (var info in Chainloader.PluginInfos.Values)
			{
				if (string.Equals(info.Metadata.GUID, nameOrGuid, StringComparison.OrdinalIgnoreCase) ||
				    string.Equals(info.Metadata.Name, nameOrGuid, StringComparison.OrdinalIgnoreCase))
					return info.Metadata;
			}

			return null;
		}

		private static BepInPlugin CreateStubPlugin()
		{
			return new BepInPlugin("CacheFork.JotunnCompat", "CacheFork Jotunn Compat", "0.0.0");
		}
	}
}
