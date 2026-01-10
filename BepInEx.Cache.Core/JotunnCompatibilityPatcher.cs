using System;
using System.Collections.Generic;
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
		private static bool _logInitPatched;
		private static ManualLogSource _log;
		private static bool _metadataLogged;
		private static bool _prefixLogged;
		private static int _prefixVerboseCount;
		private static bool _finalizerLogged;
		private static bool _patchInfoLogged;
		private static MethodInfo _getSourceModMethod;
		private static BepInPlugin _jotunnMeta;
		private static Assembly _jotunnAssembly;
		private static PropertyInfo _jotunnMainInfo;
		private static PropertyInfo _pluginInfoMetadata;
		private static FieldInfo _jotunnMainInstance;
		private static bool _logInitVerboseLogged;
		private static int _logInitVerboseCount;

		internal static bool IsInitialized => _patched;

		internal static void Initialize(ManualLogSource log)
		{
			Initialize(log, FindJotunnAssembly());
		}

		internal static void Initialize(ManualLogSource log, Assembly jotunnAssembly)
		{
			lock (PatchLock)
			{
				_log = log ?? _log;

				var utilsType = FindUtilsType(jotunnAssembly);
				if (utilsType == null)
				{
					_log?.LogMessage("CacheFork: Jotunn не загружен, патч совместимости будет применен позже.");
					return;
				}

				_jotunnAssembly = jotunnAssembly ?? utilsType.Assembly;
				if (CacheConfig.VerboseDiagnostics)
				{
					var jotunnName = string.Empty;
					try { jotunnName = _jotunnAssembly.GetName().FullName; } catch { }
					_log?.LogMessage($"CacheFork: JotunnCompatibilityPatcher.Initialize(assembly={jotunnName}).");
				}

				CacheJotunnHelpers(_jotunnAssembly);

				var getSourceMod = AccessTools.Method(utilsType, "GetSourceModMetadata");
				if (getSourceMod != null && (!_patched || !IsPatched(getSourceMod)))
				{
					_getSourceModMethod = getSourceMod;
					_jotunnMeta = FindMetadataFromAssembly(utilsType.Assembly) ?? FindAnyPlugin("Jotunn");

					try
					{
						var harmony = new Harmony(HarmonyId);
						harmony.Patch(
							getSourceMod,
							prefix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataPrefix))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataPostfix))
							{
								priority = Priority.Last
							},
							finalizer: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(GetSourceModMetadataFinalizer))
							{
								priority = Priority.Last
							});
						_patched = true;
						_log?.LogMessage("CacheFork: Jotunn патч совместимости подключен (GetSourceModMetadata).");

						LogPatchInfoOnce(getSourceMod);
					}
					catch (Exception ex)
					{
						_log?.LogWarning($"CacheFork: не удалось пропатчить GetSourceModMetadata ({ex.Message}).");
					}
				}

				var mainType = FindMainType(_jotunnAssembly);
				var logInit = AccessTools.Method(mainType, "LogInit", new[] { typeof(string) });
				if (logInit != null && (!_logInitPatched || !IsPatched(logInit)))
				{
					try
					{
						var harmony = new Harmony(HarmonyId);
						harmony.Patch(
							logInit,
							prefix: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(LogInitPrefix))
							{
								priority = Priority.First
							},
							finalizer: new HarmonyMethod(typeof(JotunnCompatibilityPatcher), nameof(LogInitFinalizer))
							{
								priority = Priority.Last
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
				// Важно: полностью заменяем оригинал, т.к. он может падать NRE на ранних стадиях (до/во время Awake)
				// и это ломает .cctor менеджеров Jotunn с каскадом по всем зависимым модам.
				__result = GetSafeMetadata();
				if (!_prefixLogged)
				{
					_prefixLogged = true;
					_log?.LogWarning("CacheFork: GetSourceModMetadata перехвачен prefix-ом; возвращена безопасная метадата (оригинал пропущен).");
				}

				if (CacheConfig.VerboseDiagnostics && _prefixVerboseCount < 5)
				{
					_prefixVerboseCount++;
					_log?.LogWarning(BuildVerbosePrefixReport("Prefix short-circuit (original skipped)"));
				}

				return false;
			}
			catch (Exception ex)
			{
				__result = CreateStubPlugin();
				_log?.LogWarning($"CacheFork: ошибка в prefix GetSourceModMetadata ({ex.Message}); возвращен stub.");
				return false;
			}
		}

		private static bool LogInitPrefix(string module)
		{
			try
			{
				// Всегда пропускаем оригинал: он может дергать GetSourceModMetadata и падать в .cctor менеджеров.
				_log?.LogInfo($"Jotunn.Main Initializing {module}");

				if (!_logInitVerboseLogged)
				{
					_logInitVerboseLogged = true;
					if (CacheConfig.VerboseDiagnostics)
						_log?.LogWarning(BuildVerboseLogInitReport("LogInit перехвачен (оригинал пропущен)"));
				}
				else if (CacheConfig.VerboseDiagnostics && _logInitVerboseCount < 5)
				{
					_logInitVerboseCount++;
					_log?.LogWarning(BuildVerboseLogInitReport("LogInit повторный вызов (оригинал пропущен)"));
				}
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка в LogInitPrefix ({ex.Message}).");
			}

			return false;
		}

		private static Exception LogInitFinalizer(Exception __exception)
		{
			if (__exception == null)
				return null;

			_log?.LogWarning($"CacheFork: LogInit завершился исключением ({__exception.GetType().Name}); исключение подавлено.");
			if (CacheConfig.VerboseDiagnostics)
				_log?.LogWarning($"CacheFork: LogInit exception details: {__exception}");

			return null;
		}

		private static void GetSourceModMetadataPostfix(ref BepInPlugin __result)
		{
			try
			{
				if (__result == null)
				{
					__result = GetSafeMetadata();
					_log?.LogWarning("CacheFork: GetSourceModMetadata вернул null, подставлен stub.");
					return;
				}

				if (_metadataLogged)
					return;

				_metadataLogged = true;
				var name = string.IsNullOrEmpty(__result.Name) ? "unknown" : __result.Name;
				_log?.LogMessage($"CacheFork: кеш метаданных {name} ({__result.GUID} v{__result.Version}).");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка кеширования метаданных ({ex.Message}).");
			}
		}

		private static Exception GetSourceModMetadataFinalizer(Exception __exception, ref BepInPlugin __result)
		{
			if (__exception == null)
				return null;

			if (__result == null)
				__result = CreateStubPlugin();

			if (!_finalizerLogged)
			{
				_finalizerLogged = true;
				_log?.LogWarning($"CacheFork: GetSourceModMetadata завершился исключением ({__exception.GetType().Name}); возвращен stub.");
			}

			if (CacheConfig.VerboseDiagnostics)
				_log?.LogWarning($"CacheFork: GetSourceModMetadata exception details: {__exception}");

			return null;
		}

		private static bool IsJotunnMainReady()
		{
			try
			{
				if (_jotunnMainInstance == null || _jotunnMainInfo == null || _pluginInfoMetadata == null)
					return false;

				var instance = _jotunnMainInstance.GetValue(null);
				if (instance == null)
					return false;

				var info = _jotunnMainInfo.GetValue(instance, null);
				if (info == null)
					return false;

				var meta = _pluginInfoMetadata.GetValue(info, null) as BepInPlugin;
				return meta != null;
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: ошибка проверки Jotunn.Main ({ex.Message}).");
			}

			return false;
		}

		private static string BuildVerbosePrefixReport(string reason)
		{
			var sb = new System.Text.StringBuilder();
			sb.Append("CacheFork: DIAG GetSourceModMetadata: ").Append(reason).AppendLine();

			try
			{
				if (_getSourceModMethod != null)
				{
					long ptr = 0;
					try { ptr = _getSourceModMethod.MethodHandle.GetFunctionPointer().ToInt64(); } catch { }
					sb.Append("  MethodPtr=0x").Append(ptr.ToString("X")).AppendLine();
				}
			}
			catch
			{
			}

			try
			{
				if (_getSourceModMethod != null)
				{
					var info = Harmony.GetPatchInfo(_getSourceModMethod);
					if (info != null)
					{
						var owners = info.Owners != null ? string.Join(", ", new System.Collections.Generic.List<string>(info.Owners).ToArray()) : string.Empty;
						sb.Append("  PatchInfo: prefix=").Append(info.Prefixes != null ? info.Prefixes.Count : 0)
						  .Append(", postfix=").Append(info.Postfixes != null ? info.Postfixes.Count : 0)
						  .Append(", finalizer=").Append(info.Finalizers != null ? info.Finalizers.Count : 0)
						  .Append(", owners=[").Append(owners).Append(']').AppendLine();
					}
				}
			}
			catch
			{
			}

			try
			{
				var pluginCount = 0;
				try { pluginCount = Chainloader.PluginInfos != null ? Chainloader.PluginInfos.Count : 0; } catch { }
				sb.Append("  PluginInfos.Count=").Append(pluginCount).AppendLine();
			}
			catch
			{
			}

			try
			{
				string jotunnName = string.Empty;
				try { jotunnName = _jotunnAssembly != null ? _jotunnAssembly.GetName().FullName : string.Empty; } catch { }
				sb.Append("  Jotunn.Assembly=").Append(jotunnName).AppendLine();
			}
			catch
			{
			}

			try
			{
				var state = DescribeJotunnMainState();
				sb.Append("  Jotunn.Main.State=").Append(state).AppendLine();
			}
			catch
			{
			}

			try
			{
				var st = new StackTrace(2, true);
				var frames = st.GetFrames();
				if (frames == null || frames.Length == 0)
					return sb.ToString();

				sb.AppendLine("  Stack (top):");
				var limit = Math.Min(frames.Length, 12);
				for (var i = 0; i < limit; i++)
				{
					var m = frames[i].GetMethod();
					var dt = m?.DeclaringType;
					var asm = dt?.Assembly;
					var asmName = string.Empty;
					try { asmName = asm?.GetName()?.Name ?? string.Empty; } catch { }
					sb.Append("    #").Append(i).Append(' ')
					  .Append(asmName).Append(' ')
					  .Append(dt != null ? dt.FullName : "<?>")
					  .Append("::").Append(m != null ? m.Name : "<?>");
					var file = frames[i].GetFileName();
					if (!string.IsNullOrEmpty(file))
						sb.Append(" @ ").Append(file).Append(':').Append(frames[i].GetFileLineNumber());
					sb.AppendLine();
				}
			}
			catch (Exception ex)
			{
				sb.Append("  Stack error: ").Append(ex.Message).AppendLine();
			}

			return sb.ToString();
		}

		private static string BuildVerboseLogInitReport(string reason)
		{
			var sb = new System.Text.StringBuilder();
			sb.Append("CacheFork: DIAG LogInit: ").Append(reason).AppendLine();

			try
			{
				var state = DescribeJotunnMainState();
				sb.Append("  Jotunn.Main.State=").Append(state).AppendLine();
			}
			catch
			{
			}

			try
			{
				var st = new StackTrace(2, true);
				var frames = st.GetFrames();
				if (frames == null || frames.Length == 0)
					return sb.ToString();

				sb.AppendLine("  Stack (top):");
				var limit = Math.Min(frames.Length, 10);
				for (var i = 0; i < limit; i++)
				{
					var m = frames[i].GetMethod();
					var dt = m?.DeclaringType;
					var asm = dt?.Assembly;
					var asmName = string.Empty;
					try { asmName = asm?.GetName()?.Name ?? string.Empty; } catch { }
					sb.Append("    #").Append(i).Append(' ')
					  .Append(asmName).Append(' ')
					  .Append(dt != null ? dt.FullName : "<?>")
					  .Append("::").Append(m != null ? m.Name : "<?>");
					var file = frames[i].GetFileName();
					if (!string.IsNullOrEmpty(file))
						sb.Append(" @ ").Append(file).Append(':').Append(frames[i].GetFileLineNumber());
					sb.AppendLine();
				}
			}
			catch
			{
			}

			return sb.ToString();
		}

		private static string DescribeJotunnMainState()
		{
			try
			{
				if (_jotunnMainInstance == null)
					return "no-Instance-field";
				if (_jotunnMainInfo == null)
					return "no-Info-property";
				if (_pluginInfoMetadata == null)
					return "no-Metadata-property";

				var instance = _jotunnMainInstance.GetValue(null);
				if (instance == null)
					return "Instance=null";

				var info = _jotunnMainInfo.GetValue(instance, null);
				if (info == null)
					return "Info=null";

				var meta = _pluginInfoMetadata.GetValue(info, null) as BepInPlugin;
				if (meta == null)
					return "Metadata=null";

				return "OK:" + (meta.GUID ?? "unknown") + "/" + (meta.Version != null ? meta.Version.ToString() : "unknown");
			}
			catch (Exception ex)
			{
				return "error:" + ex.GetType().Name + ":" + ex.Message;
			}
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

		private static void CacheJotunnHelpers(Assembly jotunnAssembly)
		{
			var mainType = FindMainType(jotunnAssembly);
			if (mainType == null)
				return;

			if (_jotunnMainInstance == null)
				_jotunnMainInstance = AccessTools.Field(mainType, "Instance");

			if (_jotunnMainInfo == null)
				_jotunnMainInfo = AccessTools.Property(mainType, "Info");

			if (_pluginInfoMetadata == null && _jotunnMainInfo != null)
				_pluginInfoMetadata = AccessTools.Property(_jotunnMainInfo.PropertyType, "Metadata");
		}

		private static void LogPatchInfoOnce(MethodInfo method)
		{
			if (_patchInfoLogged || method == null)
				return;

			_patchInfoLogged = true;

			try
			{
				var info = Harmony.GetPatchInfo(method);
				if (info == null)
					return;

				var owners = string.Empty;
				if (info.Owners != null)
					owners = string.Join(", ", new List<string>(info.Owners).ToArray());
				_log?.LogMessage($"CacheFork: GetSourceModMetadata патчи: prefix={info.Prefixes?.Count ?? 0}, postfix={info.Postfixes?.Count ?? 0}, finalizer={info.Finalizers?.Count ?? 0}, owners=[{owners}].");
			}
			catch
			{
			}
		}

		private static Assembly FindJotunnAssembly()
		{
			try
			{
				foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					var name = assembly.GetName().Name;
					if (string.Equals(name, "Jotunn", StringComparison.OrdinalIgnoreCase))
						return assembly;
				}
			}
			catch
			{
			}

			return null;
		}

		private static Type FindUtilsType(Assembly jotunnAssembly)
		{
			if (jotunnAssembly != null)
				return jotunnAssembly.GetType("Jotunn.Utils.BepInExUtils", false);

			return AccessTools.TypeByName("Jotunn.Utils.BepInExUtils");
		}

		private static Type FindMainType(Assembly jotunnAssembly)
		{
			if (jotunnAssembly != null)
				return jotunnAssembly.GetType("Jotunn.Main", false);

			return AccessTools.TypeByName("Jotunn.Main");
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
