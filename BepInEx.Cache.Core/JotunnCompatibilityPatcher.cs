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
		private static ManualLogSource _log;
		private static bool _prefixLogged;
		private static bool _finalizerLogged;

		internal static bool IsInitialized => _patched;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				_log = log ?? _log;

				var utilsType = AccessTools.TypeByName("Jotunn.Utils.BepInExUtils");
				if (utilsType == null)
				{
					_log?.LogMessage("CacheFork: Jotunn не загружен, патч совместимости будет применен позже.");
					return;
				}

				var getSourceMod = AccessTools.Method(utilsType, "GetSourceModMetadata");
				if (getSourceMod == null)
					return;

				if (_patched && IsPatched(getSourceMod))
					return;

				_jotunnMeta = FindMetadataFromAssembly(utilsType.Assembly) ?? FindAnyPlugin("Jotunn");

				var harmony = new Harmony(HarmonyId);
				try
				{
					harmony.Patch(
						getSourceMod,
						prefix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataPrefix))
						{
							priority = Priority.First
						},
						finalizer: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataFinalizer))
						{
							priority = Priority.Last
						});
					_patched = true;
					_log?.LogMessage("CacheFork: Jotunn патч совместимости подключен (GetSourceModMetadata).");
				}
				catch (Exception ex)
				{
					_log?.LogWarning($"CacheFork: не удалось пропатчить GetSourceModMetadata ({ex.Message}).");
				}
			}
		}

		private static bool GetSourceModMetadataPrefix(ref BepInPlugin __result)
		{
			try
			{
				if (_jotunnMeta == null)
					_jotunnMeta = FindAnyPlugin("Jotunn");

				__result = FindMetadataFromStack() ?? _jotunnMeta ?? FindAnyPlugin("Jotunn");
				if (__result == null)
					__result = CreateStubPlugin();

				LogPrefixOnce(__result);
			}
			catch
			{
				__result = CreateStubPlugin();
			}

			return false; // блокируем оригинал полностью
		}

		private static Exception GetSourceModMetadataFinalizer(Exception __exception, ref BepInPlugin __result)
		{
			if (__exception == null)
				return null;

			if (__result == null)
				__result = CreateStubPlugin();

			LogFinalizerOnce(__exception);
			return null;
		}

		private static bool IsPatched(MethodInfo method)
		{
			if (method == null)
				return false;

			var info = Harmony.GetPatchInfo(method);
			if (info == null || info.Owners == null)
				return false;

			return info.Owners.Contains(HarmonyId);
		}

		private static void LogPrefixOnce(BepInPlugin result)
		{
			if (_prefixLogged)
				return;

			_prefixLogged = true;
			_log?.LogMessage($"CacheFork: GetSourceModMetadata перехвачен ({result?.Name ?? "unknown"}).");
		}

		private static void LogFinalizerOnce(Exception ex)
		{
			if (_finalizerLogged)
				return;

			_finalizerLogged = true;
			_log?.LogWarning($"CacheFork: GetSourceModMetadata завершился исключением, возвращен stub ({ex.GetType().Name}).");
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
