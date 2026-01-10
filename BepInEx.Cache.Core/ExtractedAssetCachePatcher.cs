using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BepInEx.Cache.Core
{
	internal static class ExtractedAssetCachePatcher
	{
		private const string HarmonyId = "BepInEx.CacheFork.ExtractedAssets";
		private static readonly object PatchLock = new object();
		private static bool _initialized;
		private static ManualLogSource _log;
		private static bool _buildQueued;
		private static bool _warmupQueued;
		private static long _hitCount;
		private static long _missCount;
		private static DateTime _lastStatsUtc = DateTime.MinValue;

		internal static void Initialize(ManualLogSource log)
		{
			lock (PatchLock)
			{
				if (_initialized)
					return;
				_initialized = true;
				_log = log ?? _log;

				if (!ExtractedAssetCache.IsEnabled)
					return;

				try
				{
					var harmony = new Harmony(HarmonyId);

					PatchLoadFromFile(harmony, "LoadFromFile", new[] { typeof(string) });
					PatchLoadFromFile(harmony, "LoadFromFile", new[] { typeof(string), typeof(uint) });
					PatchLoadFromFile(harmony, "LoadFromFile", new[] { typeof(string), typeof(uint), typeof(ulong) });

					PatchLoadFromFile(harmony, "LoadFromFileAsync", new[] { typeof(string) });
					PatchLoadFromFile(harmony, "LoadFromFileAsync", new[] { typeof(string), typeof(uint) });
					PatchLoadFromFile(harmony, "LoadFromFileAsync", new[] { typeof(string), typeof(uint), typeof(ulong) });

					PatchLoadFromMemory(harmony, "LoadFromMemory", new[] { typeof(byte[]) }, nameof(LoadFromMemoryPrefix1));
					PatchLoadFromMemory(harmony, "LoadFromMemory", new[] { typeof(byte[]), typeof(uint) }, nameof(LoadFromMemoryPrefix2));
					PatchLoadFromMemory(harmony, "LoadFromMemoryAsync", new[] { typeof(byte[]) }, nameof(LoadFromMemoryAsyncPrefix1));
					PatchLoadFromMemory(harmony, "LoadFromMemoryAsync", new[] { typeof(byte[]), typeof(uint) }, nameof(LoadFromMemoryAsyncPrefix2));

					PatchLoadFromStream(harmony, "LoadFromStream", new[] { typeof(Stream) }, nameof(LoadFromStreamPrefix1));
					PatchLoadFromStream(harmony, "LoadFromStream", new[] { typeof(Stream), typeof(uint) }, nameof(LoadFromStreamPrefix2));
					PatchLoadFromStream(harmony, "LoadFromStream", new[] { typeof(Stream), typeof(uint), typeof(uint) }, nameof(LoadFromStreamPrefix3_BufferSize));
					PatchLoadFromStream(harmony, "LoadFromStream", new[] { typeof(Stream), typeof(uint), typeof(ulong) }, nameof(LoadFromStreamPrefix3_Offset));

					PatchLoadFromStream(harmony, "LoadFromStreamAsync", new[] { typeof(Stream) }, nameof(LoadFromStreamAsyncPrefix1));
					PatchLoadFromStream(harmony, "LoadFromStreamAsync", new[] { typeof(Stream), typeof(uint) }, nameof(LoadFromStreamAsyncPrefix2));
					PatchLoadFromStream(harmony, "LoadFromStreamAsync", new[] { typeof(Stream), typeof(uint), typeof(uint) }, nameof(LoadFromStreamAsyncPrefix3_BufferSize));
					PatchLoadFromStream(harmony, "LoadFromStreamAsync", new[] { typeof(Stream), typeof(uint), typeof(ulong) }, nameof(LoadFromStreamAsyncPrefix3_Offset));

					PatchAssemblyResourceStreams(harmony);

					_log?.LogMessage("CacheFork: extracted assets патчи AssetBundle.LoadFromFile подключены.");
				}
				catch (Exception ex)
				{
					_log?.LogWarning($"CacheFork: не удалось пропатчить AssetBundle.LoadFromFile ({ex.Message}).");
					return;
				}

				// По ТЗ: на первом запуске (когда cache-miss) извлекаем/перепаковываем бандлы.
				// Делается в фоне, чтобы не блокировать загрузку меню.
				if (!CacheManager.CacheHit && !_buildQueued)
				{
					_buildQueued = true;
					var runner = CacheForkUnityRunner.Ensure(_log);
					if (runner != null)
					{
						_log?.LogMessage("CacheFork: старт extracted assets (background build).");
						runner.StartRoutine(ExtractedAssetCache.BuildAllAsync(_log));
					}
				}

				if (CacheConfig.BackgroundWarmup && !_warmupQueued)
				{
					_warmupQueued = true;
					var runner = CacheForkUnityRunner.Ensure(_log);
					if (runner != null)
						runner.StartRoutine(ExtractedAssetCache.DelayedWarmupAsync(_log));
				}
			}
		}

		private static void PatchLoadFromFile(Harmony harmony, string methodName, Type[] signature)
		{
			var m = AccessTools.Method(typeof(AssetBundle), methodName, signature);
			if (m == null)
				return;

			var prefix = ResolvePrefixMethod(signature);
			if (prefix == null)
			{
				_log?.LogWarning($"CacheFork: не найден prefix для AssetBundle.{methodName} ({signature.Length} args).");
				return;
			}

			harmony.Patch(
				m,
				prefix: new HarmonyMethod(prefix)
				{
					priority = Priority.First
				});
		}

		private static void PatchLoadFromMemory(Harmony harmony, string methodName, Type[] signature, string prefixName)
		{
			var m = AccessTools.Method(typeof(AssetBundle), methodName, signature);
			if (m == null)
				return;

			var prefix = AccessTools.Method(typeof(ExtractedAssetCachePatcher), prefixName);
			if (prefix == null)
				return;

			harmony.Patch(
				m,
				prefix: new HarmonyMethod(prefix)
				{
					priority = Priority.First
				});
		}

		private static void PatchLoadFromStream(Harmony harmony, string methodName, Type[] signature, string prefixName)
		{
			var m = AccessTools.Method(typeof(AssetBundle), methodName, signature);
			if (m == null)
				return;

			var prefix = AccessTools.Method(typeof(ExtractedAssetCachePatcher), prefixName);
			if (prefix == null)
				return;

			harmony.Patch(
				m,
				prefix: new HarmonyMethod(prefix)
				{
					priority = Priority.First
				});
		}

		private static void PatchAssemblyResourceStreams(Harmony harmony)
		{
			try
			{
				var patched = 0;

				foreach (var type in EnumerateConcreteAssemblyTypes())
				{
					var m1 = AccessTools.Method(type, nameof(Assembly.GetManifestResourceStream), new[] { typeof(string) });
					if (m1 != null && !m1.IsAbstract)
					{
						harmony.Patch(
							m1,
							prefix: new HarmonyMethod(typeof(ExtractedAssetCachePatcher), nameof(GetManifestResourceStreamPrefix1))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(ExtractedAssetCachePatcher), nameof(GetManifestResourceStreamPostfix1))
							{
								priority = Priority.Last
							});
						patched++;
					}

					var m2 = AccessTools.Method(type, nameof(Assembly.GetManifestResourceStream), new[] { typeof(Type), typeof(string) });
					if (m2 != null && !m2.IsAbstract)
					{
						harmony.Patch(
							m2,
							prefix: new HarmonyMethod(typeof(ExtractedAssetCachePatcher), nameof(GetManifestResourceStreamPrefix2))
							{
								priority = Priority.First
							},
							postfix: new HarmonyMethod(typeof(ExtractedAssetCachePatcher), nameof(GetManifestResourceStreamPostfix2))
							{
								priority = Priority.Last
							});
						patched++;
					}
				}

				if (patched > 0)
					_log?.LogMessage($"CacheFork: extracted assets патчи GetManifestResourceStream подключены (методов: {patched}).");
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось пропатчить GetManifestResourceStream ({ex.Message}).");
			}
		}

		private static IEnumerable<Type> EnumerateConcreteAssemblyTypes()
		{
			var result = new List<Type>();
			try
			{
				var asm = typeof(Assembly).Assembly;
				Type[] types;
				try
				{
					types = asm.GetTypes();
				}
				catch (ReflectionTypeLoadException rtle)
				{
					types = rtle.Types;
				}

				if (types == null)
					return result;

				for (var i = 0; i < types.Length; i++)
				{
					var t = types[i];
					if (t == null)
						continue;
					if (t.IsAbstract)
						continue;
					if (!typeof(Assembly).IsAssignableFrom(t))
						continue;
					result.Add(t);
				}
			}
			catch
			{
			}

			return result;
		}

		private static MethodInfo ResolvePrefixMethod(Type[] signature)
		{
			var argCount = signature == null ? 0 : signature.Length;
			if (argCount == 1)
				return AccessTools.Method(typeof(ExtractedAssetCachePatcher), nameof(LoadFromFilePrefix1), new[] { typeof(string).MakeByRefType() });
			if (argCount == 2)
				return AccessTools.Method(typeof(ExtractedAssetCachePatcher), nameof(LoadFromFilePrefix2), new[] { typeof(string).MakeByRefType(), typeof(uint).MakeByRefType() });
			if (argCount == 3)
				return AccessTools.Method(typeof(ExtractedAssetCachePatcher), nameof(LoadFromFilePrefix3), new[] { typeof(string).MakeByRefType(), typeof(uint).MakeByRefType(), typeof(ulong) });

			return null;
		}

		private static void LoadFromFilePrefix1(ref string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path))
					return;

				var extractedRoot = ExtractedAssetCache.GetRoot();
				if (!string.IsNullOrEmpty(extractedRoot) && path.StartsWith(extractedRoot, StringComparison.OrdinalIgnoreCase))
					return;

				if (ExtractedAssetCache.TryGetCachedBundlePath(path, out var cachedPath, out var reason, _log))
				{
					path = cachedPath;
					var hits = Interlocked.Increment(ref _hitCount);
					if (CacheConfig.VerboseDiagnostics && hits <= 20)
						_log?.LogMessage($"CacheFork: extracted hit: {Path.GetFileName(path)}");
					MaybeReportStats();
					return;
				}

				var misses = Interlocked.Increment(ref _missCount);
				if (!CacheManager.CacheHit)
				{
					if (ExtractedAssetCache.EnqueueObservedFile(path, _log))
						QueuePendingBuild();
				}

				if (CacheConfig.VerboseDiagnostics && misses <= 50)
					_log?.LogMessage($"CacheFork: extracted miss ({reason}): {path}");

				MaybeReportStats();
			}
			catch
			{
			}
		}

		// Перегрузки с CRC: если перенаправляем на перепакованный файл, CRC оригинала не совпадает, поэтому сбрасываем.
		private static void LoadFromFilePrefix2(ref string path, ref uint crc)
		{
			try
			{
				if (string.IsNullOrEmpty(path))
					return;

				var extractedRoot = ExtractedAssetCache.GetRoot();
				if (!string.IsNullOrEmpty(extractedRoot) && path.StartsWith(extractedRoot, StringComparison.OrdinalIgnoreCase))
					return;

				if (ExtractedAssetCache.TryGetCachedBundlePath(path, out var cachedPath, out var reason, _log))
				{
					path = cachedPath;
					crc = 0u;
					var hits = Interlocked.Increment(ref _hitCount);
					if (CacheConfig.VerboseDiagnostics && hits <= 20)
						_log?.LogMessage($"CacheFork: extracted hit: {Path.GetFileName(path)}");
					MaybeReportStats();
					return;
				}

				var misses = Interlocked.Increment(ref _missCount);
				if (!CacheManager.CacheHit)
				{
					if (ExtractedAssetCache.EnqueueObservedFile(path, _log))
						QueuePendingBuild();
				}

				if (CacheConfig.VerboseDiagnostics && misses <= 50)
					_log?.LogMessage($"CacheFork: extracted miss ({reason}): {path}");

				MaybeReportStats();
			}
			catch
			{
			}
		}

		private static void LoadFromFilePrefix3(ref string path, ref uint crc, ulong offset)
		{
			try
			{
				if (string.IsNullOrEmpty(path))
					return;

				var extractedRoot = ExtractedAssetCache.GetRoot();
				if (!string.IsNullOrEmpty(extractedRoot) && path.StartsWith(extractedRoot, StringComparison.OrdinalIgnoreCase))
					return;

				if (ExtractedAssetCache.TryGetCachedBundlePath(path, out var cachedPath, out var reason, _log))
				{
					path = cachedPath;
					crc = 0u;
					var hits = Interlocked.Increment(ref _hitCount);
					if (CacheConfig.VerboseDiagnostics && hits <= 20)
						_log?.LogMessage($"CacheFork: extracted hit: {Path.GetFileName(path)}");
					MaybeReportStats();
					return;
				}

				var misses = Interlocked.Increment(ref _missCount);
				if (!CacheManager.CacheHit)
				{
					if (ExtractedAssetCache.EnqueueObservedFile(path, _log))
						QueuePendingBuild();
				}

				if (CacheConfig.VerboseDiagnostics && misses <= 50)
					_log?.LogMessage($"CacheFork: extracted miss ({reason}): {path}");

				MaybeReportStats();
			}
			catch
			{
			}
		}

		private static bool LoadFromMemoryPrefix1(byte[] binary, ref AssetBundle __result)
		{
			try
			{
				if (binary == null || binary.Length == 0)
					return true;

				var hash = ExtractedAssetCache.ComputeSha256Hex(binary);
				if (ExtractedAssetCache.TryGetCachedBundlePathFromContentHash(hash, out var cachedPath, out _))
				{
					__result = AssetBundle.LoadFromFile(cachedPath);
					return false;
				}

				if (!CacheManager.CacheHit && !string.IsNullOrEmpty(hash))
				{
					if (ExtractedAssetCache.EnqueueObservedContent(hash, binary, _log))
						QueuePendingBuild();
				}
			}
			catch
			{
			}

			return true;
		}

		private static bool LoadFromMemoryPrefix2(byte[] binary, uint crc, ref AssetBundle __result)
		{
			return LoadFromMemoryPrefix1(binary, ref __result);
		}

		private static bool LoadFromMemoryAsyncPrefix1(byte[] binary, ref AssetBundleCreateRequest __result)
		{
			try
			{
				if (binary == null || binary.Length == 0)
					return true;

				var hash = ExtractedAssetCache.ComputeSha256Hex(binary);
				if (ExtractedAssetCache.TryGetCachedBundlePathFromContentHash(hash, out var cachedPath, out _))
				{
					__result = AssetBundle.LoadFromFileAsync(cachedPath);
					return false;
				}

				if (!CacheManager.CacheHit && !string.IsNullOrEmpty(hash))
				{
					if (ExtractedAssetCache.EnqueueObservedContent(hash, binary, _log))
						QueuePendingBuild();
				}
			}
			catch
			{
			}

			return true;
		}

		private static bool LoadFromMemoryAsyncPrefix2(byte[] binary, uint crc, ref AssetBundleCreateRequest __result)
		{
			return LoadFromMemoryAsyncPrefix1(binary, ref __result);
		}

		private static bool LoadFromStreamPrefix1(ref Stream stream, ref AssetBundle __result)
		{
			return HandleStream(ref stream, false, 0, out __result, out _);
		}

		private static bool LoadFromStreamPrefix2(ref Stream stream, uint crc, ref AssetBundle __result)
		{
			return HandleStream(ref stream, false, 0, out __result, out _);
		}

		private static bool LoadFromStreamPrefix3_BufferSize(ref Stream stream, uint crc, uint managedReadBufferSize, ref AssetBundle __result)
		{
			return HandleStream(ref stream, false, 0, out __result, out _);
		}

		private static bool LoadFromStreamPrefix3_Offset(ref Stream stream, uint crc, ulong offset, ref AssetBundle __result)
		{
			return HandleStream(ref stream, false, offset, out __result, out _);
		}

		private static bool LoadFromStreamAsyncPrefix1(ref Stream stream, ref AssetBundleCreateRequest __result)
		{
			return HandleStream(ref stream, true, 0, out _, out __result);
		}

		private static bool LoadFromStreamAsyncPrefix2(ref Stream stream, uint crc, ref AssetBundleCreateRequest __result)
		{
			return HandleStream(ref stream, true, 0, out _, out __result);
		}

		private static bool LoadFromStreamAsyncPrefix3_BufferSize(ref Stream stream, uint crc, uint managedReadBufferSize, ref AssetBundleCreateRequest __result)
		{
			return HandleStream(ref stream, true, 0, out _, out __result);
		}

		private static bool LoadFromStreamAsyncPrefix3_Offset(ref Stream stream, uint crc, ulong offset, ref AssetBundleCreateRequest __result)
		{
			return HandleStream(ref stream, true, offset, out _, out __result);
		}

		private static bool HandleStream(ref Stream stream, bool async, ulong offset, out AssetBundle syncResult, out AssetBundleCreateRequest asyncResult)
		{
			syncResult = null;
			asyncResult = null;

			try
			{
				if (stream == null)
					return true;

				if (stream is FileStream fs && !string.IsNullOrEmpty(fs.Name))
				{
					var extractedRoot = ExtractedAssetCache.GetRoot();
					if (!string.IsNullOrEmpty(extractedRoot) && fs.Name.StartsWith(extractedRoot, StringComparison.OrdinalIgnoreCase))
					{
						var hits = Interlocked.Increment(ref _hitCount);
						if (CacheConfig.VerboseDiagnostics && hits <= 20)
							_log?.LogMessage($"CacheFork: extracted hit(stream->file): {Path.GetFileName(fs.Name)}");

						if (async)
							asyncResult = AssetBundle.LoadFromFileAsync(fs.Name);
						else
							syncResult = AssetBundle.LoadFromFile(fs.Name);

						MaybeReportStats();
						return false;
					}

					if (!CacheManager.CacheHit && ExtractedAssetCache.EnqueueObservedFile(fs.Name, _log))
						QueuePendingBuild();

					return true;
				}

				var hash = CaptureStreamHashWithOffset(ref stream, offset, out var capturedPath, out var capturedSize);
				if (string.IsNullOrEmpty(hash))
					return true;

				if (ExtractedAssetCache.TryGetCachedBundlePathFromContentHash(hash, out var cachedPath, out _))
				{
					if (!string.IsNullOrEmpty(capturedPath))
					{
						try { File.Delete(capturedPath); }
						catch { }
					}

					if (async)
						asyncResult = AssetBundle.LoadFromFileAsync(cachedPath);
					else
						syncResult = AssetBundle.LoadFromFile(cachedPath);
					return false;
				}

				if (!CacheManager.CacheHit)
				{
					if (!string.IsNullOrEmpty(capturedPath) && capturedSize > 0)
					{
						if (ExtractedAssetCache.EnqueueObservedContentFile(hash, capturedPath, capturedSize, _log))
							QueuePendingBuild();
					}
				}

				return true;
			}
			catch
			{
				return true;
			}
		}

		private static bool GetManifestResourceStreamPrefix1(Assembly __instance, string name, ref Stream __result)
		{
			return TryReturnCachedResource(__instance, name, ref __result);
		}

		private static void GetManifestResourceStreamPostfix1(Assembly __instance, string name, ref Stream __result)
		{
			TryWrapResourceStream(__instance, name, ref __result);
		}

		private static bool GetManifestResourceStreamPrefix2(Assembly __instance, Type type, string name, ref Stream __result)
		{
			var fullName = name;
			if (type != null && !string.IsNullOrEmpty(type.FullName) && !string.IsNullOrEmpty(name))
				fullName = type.FullName + "." + name;
			return TryReturnCachedResource(__instance, fullName, ref __result);
		}

		private static void GetManifestResourceStreamPostfix2(Assembly __instance, Type type, string name, ref Stream __result)
		{
			var fullName = name;
			if (type != null && !string.IsNullOrEmpty(type.FullName) && !string.IsNullOrEmpty(name))
				fullName = type.FullName + "." + name;
			TryWrapResourceStream(__instance, fullName, ref __result);
		}

		private static bool TryReturnCachedResource(Assembly asm, string resourceName, ref Stream result)
		{
			try
			{
				if (asm == null || string.IsNullOrEmpty(resourceName))
					return true;

				var resourceKey = ComputeResourceKey(asm, resourceName);
				if (string.IsNullOrEmpty(resourceKey))
					return true;

				if (!ExtractedAssetCache.TryGetMappedContentHash(resourceKey, out var contentHash, _log))
					return true;

				if (ExtractedAssetCache.TryGetCachedBundlePathFromContentHash(contentHash, out var cachedPath, out _))
				{
					result = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					var hits = Interlocked.Increment(ref _hitCount);
					if (CacheConfig.VerboseDiagnostics && hits <= 20)
						_log?.LogMessage($"CacheFork: extracted hit(resource): {resourceName}");
					MaybeReportStats();
					return false;
				}
			}
			catch
			{
			}

			return true;
		}

		private static void TryWrapResourceStream(Assembly asm, string resourceName, ref Stream result)
		{
			try
			{
				if (asm == null || string.IsNullOrEmpty(resourceName) || result == null)
					return;

				if (result is FileStream)
					return;

				if (!result.CanRead)
					return;

				// Если нет маппинга - учим его "на лету" без двойного чтения (хешируем по мере чтения Unity).
				var resourceKey = ComputeResourceKey(asm, resourceName);
				if (string.IsNullOrEmpty(resourceKey))
					return;

				if (ExtractedAssetCache.TryGetMappedContentHash(resourceKey, out var existingHash, _log) &&
				    !string.IsNullOrEmpty(existingHash))
					return;

				// Фильтр: стараемся не хешировать мелкие/не-bundle ресурсы.
				if (result.CanSeek)
				{
					try
					{
						if (result.Length < 1024 * 1024)
							return;

						var pos = result.Position;
						result.Position = 0;
						var header = new byte[8];
						var read = result.Read(header, 0, header.Length);
						result.Position = pos;
						if (read >= 6)
						{
							var sig = System.Text.Encoding.ASCII.GetString(header, 0, read);
							if (!sig.StartsWith("UnityFS", StringComparison.Ordinal) && !sig.StartsWith("UnityWeb", StringComparison.Ordinal))
								return;
						}
					}
					catch
					{
					}
				}

				result = new ResourceHashStream(result, resourceKey, _log);
				var misses = Interlocked.Increment(ref _missCount);
				if (CacheConfig.VerboseDiagnostics && misses <= 50)
					_log?.LogMessage($"CacheFork: extracted learn(resource-map): {resourceName}");
				MaybeReportStats();
			}
			catch
			{
			}
		}

		private static string ComputeResourceKey(Assembly asm, string resourceName)
		{
			try
			{
				if (asm == null || string.IsNullOrEmpty(resourceName))
					return null;

				var mvid = asm.ManifestModule.ModuleVersionId;
				var key = mvid.ToString("N") + "|" + resourceName;
				return ExtractedAssetCache.ComputeSha256Hex(System.Text.Encoding.UTF8.GetBytes(key));
			}
			catch
			{
				return null;
			}
		}

		private sealed class ResourceHashStream : Stream
		{
			private readonly Stream _inner;
			private readonly string _resourceKey;
			private readonly ManualLogSource _log;
			private readonly System.Security.Cryptography.SHA256 _sha;
			private bool _completed;
			private bool _invalid;

			internal ResourceHashStream(Stream inner, string resourceKey, ManualLogSource log)
			{
				_inner = inner;
				_resourceKey = resourceKey;
				_log = log;
				_sha = System.Security.Cryptography.SHA256.Create();
			}

			public override bool CanRead => _inner != null && _inner.CanRead;
			public override bool CanSeek => _inner != null && _inner.CanSeek;
			public override bool CanWrite => false;
			public override long Length => _inner.Length;
			public override long Position { get => _inner.Position; set => _inner.Position = value; }

			public override int Read(byte[] buffer, int offset, int count)
			{
				var read = _inner.Read(buffer, offset, count);
				if (read > 0)
				{
					try
					{
						if (_sha != null && !_invalid)
							_sha.TransformBlock(buffer, offset, read, null, 0);
					}
					catch
					{
						_invalid = true;
					}
				}
				else if (!_completed)
				{
					CompleteCapture();
				}

				return read;
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				_invalid = true;
				return _inner.Seek(offset, origin);
			}

			public override void Flush() { }
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			protected override void Dispose(bool disposing)
			{
				try
				{
					if (!_completed)
						CompleteCapture();
				}
				catch
				{
				}

				try { if (disposing) _inner.Dispose(); }
				catch { }

				base.Dispose(disposing);
			}

			private void CompleteCapture()
			{
				_completed = true;
				if (_invalid || _sha == null)
					return;

				try
				{
					_sha.TransformFinalBlock(new byte[0], 0, 0);
					var contentHash = BitConverter.ToString(_sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
					if (!string.IsNullOrEmpty(contentHash))
						ExtractedAssetCache.RecordResourceMapping(_resourceKey, contentHash, _log);
				}
				catch
				{
				}

				try
				{
					if (_sha != null)
						_sha.Clear();
				}
				catch
				{
				}
			}
		}

		private static string CaptureStreamHash(ref Stream stream, out string capturedPath, out long capturedSize)
		{
			return CaptureStreamHashWithOffset(ref stream, 0, out capturedPath, out capturedSize);
		}

		private static string CaptureStreamHashWithOffset(ref Stream stream, ulong offset, out string capturedPath, out long capturedSize)
		{
			capturedPath = null;
			capturedSize = 0;

			try
			{
				if (stream == null)
					return null;

				if (stream is MemoryStream ms)
				{
					var length = (long)ms.Length;
					var start = 0L;
					if (offset > 0 && offset < (ulong)length)
						start = (long)offset;
					else if (ms.CanSeek && ms.Position > 0 && ms.Position < ms.Length)
						start = ms.Position;

					var full = ms.ToArray();
					byte[] bytes;
					if (start <= 0)
					{
						bytes = full;
					}
					else if (start >= full.Length)
					{
						bytes = new byte[0];
					}
					else
					{
						var remaining = full.Length - (int)start;
						bytes = new byte[remaining];
						Buffer.BlockCopy(full, (int)start, bytes, 0, remaining);
					}

					capturedSize = bytes.Length;
					return ExtractedAssetCache.ComputeSha256Hex(bytes);
				}

				var root = ExtractedAssetCache.GetRoot();
				if (string.IsNullOrEmpty(root))
					return null;

				var incomingDir = Path.Combine(root, "__incoming_stream");
				Directory.CreateDirectory(incomingDir);

				var tmpPath = Path.Combine(incomingDir, "tmp_" + Guid.NewGuid().ToString("N") + ".bin");

				long originalPos = 0;
				if (stream.CanSeek)
				{
					originalPos = stream.Position;
					var start = originalPos;
					if (offset > 0)
						start = (long)offset;
					if (start < 0) start = 0;
					if (start > stream.Length) start = stream.Length;
					stream.Position = start;
				}

				string hex;
				using (var sha = System.Security.Cryptography.SHA256.Create())
				using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
				{
					var buffer = new byte[1024 * 1024];
					int read;
					while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
					{
						sha.TransformBlock(buffer, 0, read, null, 0);
						fs.Write(buffer, 0, read);
						capturedSize += read;
					}

					sha.TransformFinalBlock(new byte[0], 0, 0);
					hex = BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
				}

				if (stream.CanSeek)
				{
					stream.Position = originalPos;
				}
				else
				{
					try { stream = new FileStream(tmpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
					catch { }
				}

				capturedPath = tmpPath;
				return hex;
			}
			catch
			{
				return null;
			}
		}

		private static void QueuePendingBuild()
		{
			try
			{
				var runner = CacheForkUnityRunner.Ensure(_log);
				if (runner != null)
					runner.StartRoutine(ExtractedAssetCache.BuildPendingAsync(_log));
			}
			catch
			{
			}
		}

		private static void MaybeReportStats()
		{
			try
			{
				var now = DateTime.UtcNow;
				if (_lastStatsUtc != DateTime.MinValue && (now - _lastStatsUtc).TotalSeconds < 30)
					return;

				_lastStatsUtc = now;

				var hits = Interlocked.Read(ref _hitCount);
				var misses = Interlocked.Read(ref _missCount);
				if (hits + misses <= 0)
					return;

				_log?.LogMessage($"CacheFork: extracted assets статистика: hit={hits}, miss={misses}.");
			}
			catch
			{
			}
		}
	}
}
