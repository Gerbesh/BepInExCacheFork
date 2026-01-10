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
		private static bool _logInitPatched;
		private static MethodInfo _logInfoMethod;
		private static MethodInfo _logWarningMethod;
		private static FieldInfo _mainInstanceField;
		private static MethodInfo _unityObjectImplicitMethod;

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
				_jotunnMeta = FindMetadataFromAssembly(utilsType.Assembly) ?? FindAnyPlugin("Jotunn");

				var harmony = new Harmony(HarmonyId);
				if (getSourceMod != null && (!_patched || !IsPatched(getSourceMod)))
				{
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

				CacheJotunnHelpers();

				var mainType = AccessTools.TypeByName("Jotunn.Main");
				var logInitMethod = AccessTools.Method(mainType, "LogInit", new[] { typeof(string) });
				if (logInitMethod != null && (!_logInitPatched || !IsPatched(logInitMethod)))
				{
					try
					{
						harmony.Patch(
							logInitMethod,
							prefix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(LogInitPrefix))
							{
								priority = Priority.First
							});
						_logInitPatched = true;
						_log?.LogMessage("CacheFork: Jotunn патч совместимости подключен (LogInit).");
					}
					catch (Exception ex)
					{
						_log?.LogWarning($"CacheFork: не удалось пропатчить LogInit ({ex.Message}).");
					}
				}
			}
		}

		private static bool GetSourceModMetadataPrefix(ref BepInPlugin __result)
		{
			try
			{
				__result = GetSafeMetadata();
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

		private static bool LogInitPrefix(string module)
		{
			try
			{
				LogInfo($"Initializing {module}");
				if (IsMainInstanceReady())
					return false;

				var warning = $"{module} was accessed before Jotunn Awake, this can cause unexpected behaviour. Please make sure to add `[BepInDependency(Jotunn.Main.ModGuid)]` next to your BaseUnityPlugin";
				LogWarning(GetSafeMetadata(), warning);
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: LogInit пропущен из-за ошибки ({ex.Message}).");
			}

			return false;
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

		private static void CacheJotunnHelpers()
		{
			if (_logInfoMethod == null || _logWarningMethod == null)
			{
				var loggerType = AccessTools.TypeByName("Jotunn.Logger");
				_logInfoMethod = AccessTools.Method(loggerType, "LogInfo", new[] { typeof(object) });
				_logWarningMethod = AccessTools.Method(loggerType, "LogWarning", new[] { typeof(BepInPlugin), typeof(object) });
			}

			if (_mainInstanceField == null)
			{
				var mainType = AccessTools.TypeByName("Jotunn.Main");
				_mainInstanceField = AccessTools.Field(mainType, "Instance");
			}

			if (_unityObjectImplicitMethod == null)
			{
				var unityObjectType = AccessTools.TypeByName("UnityEngine.Object");
				_unityObjectImplicitMethod = AccessTools.Method(unityObjectType, "op_Implicit", new[] { unityObjectType });
			}
		}

		private static bool IsMainInstanceReady()
		{
			var instance = _mainInstanceField?.GetValue(null);
			if (instance == null)
				return false;

			if (_unityObjectImplicitMethod == null)
				return true;

			try
			{
				return (bool)_unityObjectImplicitMethod.Invoke(null, new[] { instance });
			}
			catch
			{
				return true;
			}
		}

		private static void LogInfo(string message)
		{
			_logInfoMethod?.Invoke(null, new object[] { message });
		}

		private static void LogWarning(BepInPlugin plugin, string message)
		{
			_logWarningMethod?.Invoke(null, new object[] { plugin, message });
		}

		private static BepInPlugin GetSafeMetadata()
		{
			if (_jotunnMeta == null)
				_jotunnMeta = FindAnyPlugin("Jotunn");

			return FindMetadataFromStack() ?? _jotunnMeta ?? FindAnyPlugin("Jotunn") ?? CreateStubPlugin();
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
