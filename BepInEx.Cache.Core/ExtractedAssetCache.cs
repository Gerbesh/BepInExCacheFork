using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Cache.Core
{
	internal static class ExtractedAssetCache
	{
		private const string ManifestFileName = "extracted_manifest.txt";
		private const string ResourceMapFileName = "extracted_resource_map.txt";

		private static readonly object LockObj = new object();
		private static bool _loaded;
		private static readonly Dictionary<string, ManifestEntry> Entries = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, PendingItem> Pending = new Dictionary<string, PendingItem>(StringComparer.OrdinalIgnoreCase);
		private static bool _pendingBuildActive;

		private static bool _resourceMapLoaded;
		private static readonly Dictionary<string, string> ResourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		internal sealed class ManifestEntry
		{
			internal string RelativePath;
			internal long SourceSize;
			internal long SourceWriteUtcTicks;
			internal string Compression;
			internal string ContentHashHex;
		}

		private sealed class PendingItem
		{
			internal string SourcePath;
			internal string TargetRelativePath;
			internal long SourceSize;
			internal long SourceWriteUtcTicks;
			internal string Compression;
			internal string ContentHashHex;
		}

		internal static bool IsEnabled => CacheConfig.EnableCache && CacheConfig.ExtractHeavyAssets;

		internal static string GetRoot()
		{
			var root = CacheConfig.ResolveExtractDir(CacheConfig.ExtractDir);
			if (string.IsNullOrEmpty(root))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			return Path.Combine(root, processName);
		}

		internal static string GetManifestPath()
		{
			var root = GetRoot();
			return string.IsNullOrEmpty(root) ? null : Path.Combine(root, ManifestFileName);
		}

		internal static bool HasManifest()
		{
			try
			{
				var path = GetManifestPath();
				return !string.IsNullOrEmpty(path) && File.Exists(path);
			}
			catch
			{
				return false;
			}
		}

		internal static string GetResourceMapPath()
		{
			var root = GetRoot();
			return string.IsNullOrEmpty(root) ? null : Path.Combine(root, ResourceMapFileName);
		}

		internal static void EnsureLoaded(ManualLogSource log)
		{
			if (_loaded)
				return;

			lock (LockObj)
			{
				if (_loaded)
					return;
				_loaded = true;

				Entries.Clear();
				ResourceMap.Clear();
				_resourceMapLoaded = false;

				var manifestPath = GetManifestPath();
				if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
					return;

				try
				{
					foreach (var line in File.ReadAllLines(manifestPath))
					{
						if (string.IsNullOrEmpty(line) || line.Trim().Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
							continue;

						var parts = line.Split('|');
						if (parts.Length < 4)
							continue;

						var rel = parts[0];
						if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
							continue;
						if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
							continue;

						var compression = parts[3] ?? string.Empty;
						var contentHash = parts.Length >= 5 ? (parts[4] ?? string.Empty) : string.Empty;
						Entries[NormalizeKey(rel)] = new ManifestEntry
						{
							RelativePath = rel,
							SourceSize = size,
							SourceWriteUtcTicks = ticks,
							Compression = compression,
							ContentHashHex = contentHash
						};
					}
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось прочитать extracted manifest ({ex.Message}).");
				}

				EnsureResourceMapLoaded(log);
			}
		}

		private static void EnsureResourceMapLoaded(ManualLogSource log)
		{
			if (_resourceMapLoaded)
				return;

			_resourceMapLoaded = true;
			ResourceMap.Clear();

			var mapPath = GetResourceMapPath();
			if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
				return;

			try
			{
				foreach (var line in File.ReadAllLines(mapPath))
				{
					if (string.IsNullOrEmpty(line) || line.Trim().Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
						continue;

					var parts = line.Split('|');
					if (parts.Length < 2)
						continue;

					var key = parts[0];
					var hash = parts[1];
					if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(hash))
						continue;

					ResourceMap[key] = hash;
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось прочитать resource-map ({ex.Message}).");
			}
		}

		internal static bool TryGetMappedContentHash(string resourceKey, out string contentHashHex, ManualLogSource log)
		{
			contentHashHex = null;
			if (!IsEnabled || string.IsNullOrEmpty(resourceKey))
				return false;

			EnsureLoaded(log);

			lock (LockObj)
			{
				return ResourceMap.TryGetValue(resourceKey, out contentHashHex) && !string.IsNullOrEmpty(contentHashHex);
			}
		}

		internal static void RecordResourceMapping(string resourceKey, string contentHashHex, ManualLogSource log)
		{
			try
			{
				if (!IsEnabled || string.IsNullOrEmpty(resourceKey) || string.IsNullOrEmpty(contentHashHex))
					return;

				EnsureLoaded(log);

				var mapPath = GetResourceMapPath();
				if (string.IsNullOrEmpty(mapPath))
					return;

				lock (LockObj)
				{
					if (ResourceMap.TryGetValue(resourceKey, out var existing) &&
					    string.Equals(existing, contentHashHex, StringComparison.OrdinalIgnoreCase))
						return;

					ResourceMap[resourceKey] = contentHashHex;
				}

				var dir = Path.GetDirectoryName(mapPath);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
					Directory.CreateDirectory(dir);

				File.AppendAllText(mapPath, resourceKey + "|" + contentHashHex + Environment.NewLine, Encoding.UTF8);
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось записать resource-map ({ex.Message}).");
			}
		}

		internal static IEnumerator DelayedWarmupAsync(ManualLogSource log)
		{
			if (!IsEnabled || !CacheConfig.BackgroundWarmup)
				yield break;

			yield return new WaitForSeconds(15f);

			EnsureLoaded(log);
			yield return WarmupOsCacheAsync(log, 256L * 1024 * 1024, 30);
		}

		internal static bool TryGetCachedBundlePath(string originalPath, out string cachedPath, out string reason, ManualLogSource log)
		{
			cachedPath = null;
			reason = null;

			if (!IsEnabled)
			{
				reason = "disabled";
				return false;
			}

			if (string.IsNullOrEmpty(originalPath))
			{
				reason = "empty-path";
				return false;
			}

			EnsureLoaded(log);

			string relativePath;
			try
			{
				relativePath = GetRelativeBundlePath(originalPath);
			}
			catch
			{
				reason = "relative-failed";
				return false;
			}

			if (string.IsNullOrEmpty(relativePath))
			{
				reason = "not-under-plugins";
				return false;
			}

			Entries.TryGetValue(NormalizeKey(relativePath), out var entry);
			if (entry == null)
			{
				reason = "no-entry";
				return false;
			}

			if (!File.Exists(originalPath))
			{
				reason = "source-missing";
				return false;
			}

			var root = GetRoot();
			if (string.IsNullOrEmpty(root))
			{
				reason = "root-null";
				return false;
			}

			var candidate = Path.Combine(root, entry.RelativePath);
			if (!File.Exists(candidate))
			{
				reason = "cached-missing";
				return false;
			}

			try
			{
				var info = new FileInfo(originalPath);
				if (info.Length != entry.SourceSize)
				{
					reason = "size-changed";
					return false;
				}

				if (info.LastWriteTimeUtc.Ticks != entry.SourceWriteUtcTicks)
				{
					reason = "mtime-changed";
					return false;
				}
			}
			catch
			{
				reason = "source-stat-failed";
				return false;
			}

			cachedPath = candidate;
			return true;
		}

		internal static bool TryGetCachedBundlePathFromContentHash(string contentHashHex, out string cachedPath, out string reason)
		{
			cachedPath = null;
			reason = null;

			if (!IsEnabled)
			{
				reason = "disabled";
				return false;
			}

			if (string.IsNullOrEmpty(contentHashHex))
			{
				reason = "no-hash";
				return false;
			}

			EnsureLoaded(null);

			var relative = GetRelativePathForContentHash(contentHashHex);
			if (string.IsNullOrEmpty(relative))
			{
				reason = "bad-key";
				return false;
			}

			lock (LockObj)
			{
				if (!Entries.TryGetValue(NormalizeKey(relative), out var entry) || entry == null)
				{
					reason = "no-entry";
					return false;
				}

				var root = GetRoot();
				if (string.IsNullOrEmpty(root))
				{
					reason = "no-root";
					return false;
				}

				var candidate = Path.Combine(root, entry.RelativePath);
				if (!File.Exists(candidate))
				{
					reason = "missing-file";
					return false;
				}

				if (!string.IsNullOrEmpty(entry.ContentHashHex) &&
				    !string.Equals(entry.ContentHashHex, contentHashHex, StringComparison.OrdinalIgnoreCase))
				{
					reason = "hash-mismatch";
					return false;
				}

				cachedPath = candidate;
				return true;
			}
		}

		internal static bool EnqueueObservedFile(string sourcePath, ManualLogSource log)
		{
			try
			{
				if (!IsEnabled || string.IsNullOrEmpty(sourcePath))
					return false;

				if (!File.Exists(sourcePath))
					return false;

				var relative = GetRelativePathForSourceFile(sourcePath);
				if (string.IsNullOrEmpty(relative))
					return false;

				var info = new FileInfo(sourcePath);
				var compression = NormalizeCompression(CacheConfig.PreferredCompression);
				var key = NormalizeKey(relative);

				lock (LockObj)
				{
					if (Pending.ContainsKey(key))
						return false;

					Pending[key] = new PendingItem
					{
						SourcePath = sourcePath,
						TargetRelativePath = relative,
						SourceSize = info.Length,
						SourceWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
						Compression = compression
					};
				}

				if (CacheConfig.VerboseDiagnostics)
					log?.LogMessage($"CacheFork: extracted enqueue file: {sourcePath}");

				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool EnqueueObservedContent(string contentHashHex, byte[] bytes, ManualLogSource log)
		{
			try
			{
				if (!IsEnabled || string.IsNullOrEmpty(contentHashHex) || bytes == null || bytes.Length == 0)
					return false;

				var root = GetRoot();
				if (string.IsNullOrEmpty(root))
					return false;

				var incomingRel = Path.Combine("__incoming", contentHashHex + ".bundle");
				var incomingPath = Path.Combine(root, incomingRel);
				var incomingDir = Path.GetDirectoryName(incomingPath);
				if (!string.IsNullOrEmpty(incomingDir) && !Directory.Exists(incomingDir))
					Directory.CreateDirectory(incomingDir);

				if (!File.Exists(incomingPath) || new FileInfo(incomingPath).Length != bytes.Length)
					File.WriteAllBytes(incomingPath, bytes);

				var targetRelative = GetRelativePathForContentHash(contentHashHex);
				if (string.IsNullOrEmpty(targetRelative))
					return false;

				var compression = NormalizeCompression(CacheConfig.PreferredCompression);
				var key = NormalizeKey(targetRelative);

				lock (LockObj)
				{
					if (Pending.ContainsKey(key))
						return false;

					Pending[key] = new PendingItem
					{
						SourcePath = incomingPath,
						TargetRelativePath = targetRelative,
						SourceSize = bytes.Length,
						SourceWriteUtcTicks = 0,
						Compression = compression,
						ContentHashHex = contentHashHex
					};
				}

				if (CacheConfig.VerboseDiagnostics)
					log?.LogMessage($"CacheFork: extracted enqueue memory: {contentHashHex} ({bytes.Length} bytes).");

				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool EnqueueObservedContentFile(string contentHashHex, string sourcePath, long sourceSize, ManualLogSource log)
		{
			try
			{
				if (!IsEnabled || string.IsNullOrEmpty(contentHashHex) || string.IsNullOrEmpty(sourcePath))
					return false;

				if (!File.Exists(sourcePath))
					return false;

				var targetRelative = GetRelativePathForContentHash(contentHashHex);
				if (string.IsNullOrEmpty(targetRelative))
					return false;

				var compression = NormalizeCompression(CacheConfig.PreferredCompression);
				var key = NormalizeKey(targetRelative);

				lock (LockObj)
				{
					if (Pending.ContainsKey(key))
						return false;

					Pending[key] = new PendingItem
					{
						SourcePath = sourcePath,
						TargetRelativePath = targetRelative,
						SourceSize = sourceSize,
						SourceWriteUtcTicks = 0,
						Compression = compression,
						ContentHashHex = contentHashHex
					};
				}

				if (CacheConfig.VerboseDiagnostics)
					log?.LogMessage($"CacheFork: extracted enqueue stream-file: {contentHashHex} ({sourceSize} bytes).");

				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static IEnumerator BuildPendingAsync(ManualLogSource log)
		{
			if (!IsEnabled)
				yield break;

			var swTotal = Stopwatch.StartNew();
			lock (LockObj)
			{
				if (_pendingBuildActive)
					yield break;
				_pendingBuildActive = true;
			}

			try
			{
				EnsureLoaded(log);

				var root = GetRoot();
				if (string.IsNullOrEmpty(root))
					yield break;

				Directory.CreateDirectory(root);

				var startedAt = DateTime.UtcNow;
				var processed = 0;
				var succeeded = 0;

				while (true)
				{
					PendingItem item = null;
					string key = null;
					lock (LockObj)
					{
						foreach (var kv in Pending)
						{
							key = kv.Key;
							item = kv.Value;
							break;
						}

						if (item != null && key != null)
							Pending.Remove(key);
					}

					if (item == null)
						break;

					processed++;

					if (string.IsNullOrEmpty(item.SourcePath) || !File.Exists(item.SourcePath))
						continue;

					var destPath = Path.Combine(root, item.TargetRelativePath);
					var destDir = Path.GetDirectoryName(destPath);
					if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
						Directory.CreateDirectory(destDir);

					if (CacheConfig.MaxExtractSizeBytes > 0)
					{
						try
						{
							var currentBytes = GetDirectorySizeBytes(root);
							if (currentBytes >= CacheConfig.MaxExtractSizeBytes)
							{
								log?.LogWarning("CacheFork: достигнут лимит extracted-кеша, новые бандлы пропущены.");
								break;
							}
						}
						catch
						{
						}
					}

					var srcInfo = new FileInfo(item.SourcePath);
					if (!NeedsRebuildForEntry(item.TargetRelativePath, srcInfo, item.Compression, item.ContentHashHex))
					{
						TryDeleteIncomingFile(item.SourcePath, root);
						continue;
					}

					var recompressSucceeded = false;
					yield return RecompressOrCopyAssetBundleAsync(item.SourcePath, destPath, item.Compression, log, s => recompressSucceeded = s);
					if (!recompressSucceeded)
						continue;

					TryDeleteIncomingFile(item.SourcePath, root);
					succeeded++;

					lock (LockObj)
					{
						Entries[NormalizeKey(item.TargetRelativePath)] = new ManifestEntry
						{
							RelativePath = item.TargetRelativePath,
							SourceSize = string.IsNullOrEmpty(item.ContentHashHex) ? srcInfo.Length : item.SourceSize,
							SourceWriteUtcTicks = string.IsNullOrEmpty(item.ContentHashHex) ? srcInfo.LastWriteTimeUtc.Ticks : 0,
							Compression = item.Compression,
							ContentHashHex = item.ContentHashHex ?? string.Empty
						};
					}

					yield return null;
				}

				try
				{
					WriteManifest(GetManifestPath(), new Dictionary<string, ManifestEntry>(Entries, StringComparer.OrdinalIgnoreCase));
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось записать extracted manifest ({ex.Message}).");
				}

				if (processed > 0)
				{
					var elapsed = DateTime.UtcNow - startedAt;
					log?.LogMessage($"CacheFork: extracted assets (on-demand): обработано {processed}, сохранено {succeeded} за {FormatTime(elapsed)}.");
				}
			}
			finally
			{
				try
				{
					swTotal.Stop();
					CacheMetrics.Add("ExtractedAssetCache.BuildPendingAsync", swTotal.ElapsedTicks);
				}
				catch
				{
				}

				lock (LockObj)
				{
					_pendingBuildActive = false;
				}
			}
		}

		private static void TryDeleteIncomingFile(string sourcePath, string root)
		{
			try
			{
				if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(root))
					return;

				var normalizedSource = sourcePath.Replace('\\', '/');
				var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
				if (!normalizedSource.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
					return;

				if (normalizedSource.IndexOf("/__incoming/", StringComparison.OrdinalIgnoreCase) >= 0 ||
				    normalizedSource.IndexOf("/__incoming_stream/", StringComparison.OrdinalIgnoreCase) >= 0 ||
				    normalizedSource.IndexOf("/__incoming_resource/", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					File.Delete(sourcePath);
				}
			}
			catch
			{
			}
		}

		// Важно: ресурс-ключ сам по себе не является content-hash.
		// Для embedded AssetBundle используется связка resourceKey -> contentHashHex (см. extracted_resource_map.txt).

		internal static IEnumerable<string> EnumerateBundleFiles()
		{
			var pluginRoot = Paths.PluginPath;
			if (string.IsNullOrEmpty(pluginRoot) || !Directory.Exists(pluginRoot))
				yield break;

			foreach (var file in AssetCache.EnumerateBundleFiles(pluginRoot))
				yield return file;
		}

		internal static IEnumerator BuildAllAsync(ManualLogSource log)
		{
			if (!IsEnabled)
				yield break;

			var swTotal = Stopwatch.StartNew();
			var root = GetRoot();
			if (string.IsNullOrEmpty(root))
				yield break;

			Directory.CreateDirectory(root);
			EnsureLoaded(log);

			var manifestPath = GetManifestPath();
			var newEntries = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

			long extractedBytes = 0;
			var startedAt = DateTime.UtcNow;
			var extractedCount = 0;

			var enumerated = 0;
			foreach (var bundlePath in EnumerateBundleFiles())
			{
				enumerated++;
				if (!File.Exists(bundlePath))
					continue;

				string rel;
				try { rel = GetRelativeBundlePath(bundlePath); }
				catch { continue; }
				if (string.IsNullOrEmpty(rel))
					continue;

				var srcInfo = new FileInfo(bundlePath);
				var destPath = Path.Combine(root, rel);
				var destDir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
					Directory.CreateDirectory(destDir);

				if (CacheConfig.MaxExtractSizeBytes > 0 && extractedBytes >= CacheConfig.MaxExtractSizeBytes)
				{
					log?.LogWarning("CacheFork: достигнут лимит extracted-кеша, оставшиеся бандлы пропущены.");
					break;
				}

				var compression = NormalizeCompression(CacheConfig.PreferredCompression);
				if (!NeedsRebuild(rel, srcInfo, compression))
				{
					newEntries[NormalizeKey(rel)] = new ManifestEntry
					{
						RelativePath = rel,
						SourceSize = srcInfo.Length,
						SourceWriteUtcTicks = srcInfo.LastWriteTimeUtc.Ticks,
						Compression = compression
					};
					continue;
				}

				var recompressSucceeded = false;
				yield return RecompressOrCopyAssetBundleAsync(bundlePath, destPath, compression, log, s => recompressSucceeded = s);
				if (!recompressSucceeded)
					continue;

				extractedCount++;
				try
				{
					var outInfo = new FileInfo(destPath);
					extractedBytes += outInfo.Length;
				}
				catch
				{
				}

				newEntries[NormalizeKey(rel)] = new ManifestEntry
				{
					RelativePath = rel,
					SourceSize = srcInfo.Length,
					SourceWriteUtcTicks = srcInfo.LastWriteTimeUtc.Ticks,
					Compression = compression
				};
			}

			try
			{
				WriteManifest(manifestPath, newEntries);
				lock (LockObj)
				{
					Entries.Clear();
					foreach (var kv in newEntries)
						Entries[kv.Key] = kv.Value;
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось записать extracted manifest ({ex.Message}).");
			}

			var elapsed = DateTime.UtcNow - startedAt;
			swTotal.Stop();
			CacheMetrics.Add("ExtractedAssetCache.BuildAllAsync", swTotal.ElapsedTicks);
			if (CacheConfig.VerboseDiagnostics)
				log?.LogMessage($"CacheFork: extracted assets scan: найдено кандидатов {enumerated}.");
			if (extractedCount > 0)
				log?.LogMessage($"CacheFork: extracted assets готово: перепаковано {extractedCount} бандлов за {FormatTime(elapsed)}.");
		}

		private static bool NeedsRebuild(string relativePath, FileInfo sourceInfo, string compression)
		{
			var key = NormalizeKey(relativePath);
			if (!Entries.TryGetValue(key, out var entry))
				return true;

			if (entry == null)
				return true;

			if (entry.SourceSize != sourceInfo.Length)
				return true;

			if (entry.SourceWriteUtcTicks != sourceInfo.LastWriteTimeUtc.Ticks)
				return true;

			if (!string.Equals(entry.Compression, compression, StringComparison.OrdinalIgnoreCase))
				return true;

			var root = GetRoot();
			if (string.IsNullOrEmpty(root))
				return true;

			var destPath = Path.Combine(root, entry.RelativePath);
			return !File.Exists(destPath);
		}

		private static bool NeedsRebuildForEntry(string relativePath, FileInfo sourceInfo, string compression, string contentHashHex)
		{
			var key = NormalizeKey(relativePath);
			lock (LockObj)
			{
				if (!Entries.TryGetValue(key, out var entry) || entry == null)
					return true;

				var hasContentHash = !string.IsNullOrEmpty(contentHashHex);
				if (hasContentHash)
				{
					if (!string.IsNullOrEmpty(entry.ContentHashHex) &&
					    !string.Equals(entry.ContentHashHex, contentHashHex, StringComparison.OrdinalIgnoreCase))
						return true;

					if (!string.Equals(entry.Compression, compression, StringComparison.OrdinalIgnoreCase))
						return true;

					var contentRoot = GetRoot();
					if (string.IsNullOrEmpty(contentRoot))
						return true;

					var contentDestPath = Path.Combine(contentRoot, entry.RelativePath);
					return !File.Exists(contentDestPath);
				}

				if (entry.SourceSize != sourceInfo.Length)
					return true;

				if (entry.SourceWriteUtcTicks != sourceInfo.LastWriteTimeUtc.Ticks)
					return true;

				if (!string.Equals(entry.Compression, compression, StringComparison.OrdinalIgnoreCase))
					return true;

				var root = GetRoot();
				if (string.IsNullOrEmpty(root))
					return true;

				var destPath = Path.Combine(root, entry.RelativePath);
				return !File.Exists(destPath);
			}
		}

		private static void WriteManifest(string manifestPath, Dictionary<string, ManifestEntry> entries)
		{
			if (string.IsNullOrEmpty(manifestPath))
				return;

			var lines = new List<string>
			{
				"# extracted_assets manifest",
				"# format: relativePath|sourceSize|sourceWriteUtcTicks|compression|contentHashHex(optional)"
			};

			foreach (var entry in entries.Values)
			{
				lines.Add(string.Join("|", new[]
				{
					entry.RelativePath ?? string.Empty,
					entry.SourceSize.ToString(CultureInfo.InvariantCulture),
					entry.SourceWriteUtcTicks.ToString(CultureInfo.InvariantCulture),
					entry.Compression ?? string.Empty,
					entry.ContentHashHex ?? string.Empty
				}));
			}

			File.WriteAllLines(manifestPath, lines.ToArray(), Encoding.UTF8);
		}

		private static string NormalizeKey(string relativePath)
		{
			return (relativePath ?? string.Empty).Replace('\\', '/');
		}

		private static string GetRelativeBundlePath(string fullPath)
		{
			var pluginRoot = Paths.PluginPath;
			if (string.IsNullOrEmpty(pluginRoot))
				return null;

			var rootUri = new Uri(AppendDirectorySeparator(pluginRoot));
			var fileUri = new Uri(fullPath);
			var relative = rootUri.MakeRelativeUri(fileUri).ToString();
			return Uri.UnescapeDataString(relative.Replace('/', Path.DirectorySeparatorChar));
		}

		private static string GetRelativePathForSourceFile(string fullPath)
		{
			if (string.IsNullOrEmpty(fullPath))
				return null;

			var pluginRoot = Paths.PluginPath;
			if (!string.IsNullOrEmpty(pluginRoot))
			{
				try
				{
					var rootUri = new Uri(AppendDirectorySeparator(pluginRoot));
					var fileUri = new Uri(fullPath);
					if (rootUri.IsBaseOf(fileUri))
					{
						var relative = rootUri.MakeRelativeUri(fileUri).ToString();
						return Uri.UnescapeDataString(relative.Replace('/', Path.DirectorySeparatorChar));
					}
				}
				catch
				{
				}
			}

			var fileName = Path.GetFileName(fullPath) ?? "bundle";
			var pathHash = ComputeSha256Hex(Encoding.UTF8.GetBytes(fullPath.Replace('\\', '/').ToLowerInvariant()));
			return Path.Combine(Path.Combine("__external", pathHash), fileName);
		}

		private static string GetRelativePathForContentHash(string contentHashHex)
		{
			if (string.IsNullOrEmpty(contentHashHex))
				return null;

			var normalized = contentHashHex.Trim();
			if (normalized.Length < 8)
				return null;

			return Path.Combine("__content", normalized + ".bundle");
		}

		internal static string ComputeSha256Hex(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0)
				return null;

			try
			{
				using (var sha = SHA256.Create())
				{
					var hash = sha.ComputeHash(bytes);
					var sb = new StringBuilder(hash.Length * 2);
					for (var i = 0; i < hash.Length; i++)
						sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
					return sb.ToString();
				}
			}
			catch
			{
				return null;
			}
		}

		private static long GetDirectorySizeBytes(string root)
		{
			long sum = 0;
			if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
				return 0;

			foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
			{
				try { sum += new FileInfo(file).Length; }
				catch { }
			}

			return sum;
		}

		private static string AppendDirectorySeparator(string path)
		{
			if (string.IsNullOrEmpty(path))
				return path;
			if (path[path.Length - 1] == Path.DirectorySeparatorChar)
				return path;
			return path + Path.DirectorySeparatorChar;
		}

		private static string NormalizeCompression(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "LZ4";

			var trimmed = value.Trim();
			if (trimmed.Equals("Uncompressed", StringComparison.OrdinalIgnoreCase))
				return "Uncompressed";
			return "LZ4";
		}

		private static IEnumerator RecompressOrCopyAssetBundleAsync(string sourcePath, string destPath, string compression, ManualLogSource log, Action<bool> setSuccess)
		{
			if (setSuccess == null)
				yield break;

			setSuccess(false);

			object opObj = null;
			AsyncOperation asyncOp = null;
			Exception invokeException = null;

			try
			{
				var method = ResolveRecompressMethod();
				if (method == null)
				{
					SafeCopyAssetBundle(sourcePath, destPath);
					setSuccess(true);
					yield break;
				}

				var parameters = method.GetParameters();
				object compressionValue = null;
				if (parameters.Length >= 3)
					compressionValue = ResolveCompressionValue(parameters[2].ParameterType, compression);

				object crcValue = null;
				if (parameters.Length >= 4)
					crcValue = Convert.ChangeType(0u, parameters[3].ParameterType, CultureInfo.InvariantCulture);

				object priorityValue = null;
				if (parameters.Length >= 5)
					priorityValue = ResolveEnumValue(parameters[4].ParameterType, "Low");

				var args = new List<object> { sourcePath, destPath };
				if (parameters.Length >= 3) args.Add(compressionValue);
				if (parameters.Length >= 4) args.Add(crcValue);
				if (parameters.Length >= 5) args.Add(priorityValue);

				opObj = method.Invoke(null, args.ToArray());
				asyncOp = opObj as AsyncOperation;
			}
			catch (Exception ex)
			{
				invokeException = ex;
			}

			if (invokeException != null)
			{
				try
				{
					log?.LogWarning($"CacheFork: ошибка перепаковки AssetBundle ({invokeException.GetType().Name}: {invokeException.Message}), выполнено копирование.");
					SafeCopyAssetBundle(sourcePath, destPath);
					setSuccess(true);
				}
				catch (Exception ex2)
				{
					log?.LogWarning($"CacheFork: не удалось сохранить AssetBundle в extracted-кеш ({ex2.GetType().Name}: {ex2.Message}): {sourcePath}");
					setSuccess(false);
				}

				yield break;
			}

			if (asyncOp != null)
				yield return asyncOp;
			else
				yield return null;

			var succeeded = false;
			try
			{
				if (TryGetBoolProperty(opObj, "success", out var success))
					succeeded = success;
				else
					succeeded = File.Exists(destPath) && new FileInfo(destPath).Length > 0;
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: ошибка проверки результата перепаковки AssetBundle ({ex.GetType().Name}: {ex.Message}): {sourcePath}");
				succeeded = File.Exists(destPath) && new FileInfo(destPath).Length > 0;
			}

			if (!succeeded)
				log?.LogWarning($"CacheFork: не удалось перепаковать AssetBundle (success=false): {sourcePath}");

			setSuccess(succeeded);
		}

		private static MethodInfo ResolveRecompressMethod()
		{
			try
			{
				var methods = typeof(AssetBundle).GetMethods(BindingFlags.Public | BindingFlags.Static);
				foreach (var method in methods)
				{
					if (!string.Equals(method.Name, "RecompressAssetBundleAsync", StringComparison.Ordinal))
						continue;

					var p = method.GetParameters();
					if (p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string))
						return method;
				}
			}
			catch
			{
			}

			return null;
		}

		private static object ResolveCompressionValue(Type compressionType, string compression)
		{
			if (compressionType == null)
				return null;

			var memberName = string.Equals(compression, "Uncompressed", StringComparison.OrdinalIgnoreCase)
				? "UncompressedRuntime"
				: "LZ4Runtime";

			try
			{
				var prop = compressionType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
				if (prop != null)
					return prop.GetValue(null, null);

				var field = compressionType.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
				if (field != null)
					return field.GetValue(null);

				return Activator.CreateInstance(compressionType);
			}
			catch
			{
				try { return Activator.CreateInstance(compressionType); }
				catch { return null; }
			}
		}

		private static object ResolveEnumValue(Type enumType, string name)
		{
			if (enumType == null)
				return null;

			try
			{
				if (enumType.IsEnum)
					return Enum.Parse(enumType, name, true);
				return Activator.CreateInstance(enumType);
			}
			catch
			{
				try { return Activator.CreateInstance(enumType); }
				catch { return null; }
			}
		}

		private static bool TryGetBoolProperty(object instance, string propertyName, out bool value)
		{
			value = false;
			if (instance == null || string.IsNullOrEmpty(propertyName))
				return false;

			try
			{
				var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
				if (prop == null || prop.PropertyType != typeof(bool))
					return false;

				value = (bool)prop.GetValue(instance, null);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static void SafeCopyAssetBundle(string sourcePath, string destPath)
		{
			var destDir = Path.GetDirectoryName(destPath);
			if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
				Directory.CreateDirectory(destDir);

			File.Copy(sourcePath, destPath, true);
		}

		private static string FormatTime(TimeSpan time)
		{
			if (time.TotalSeconds < 60)
				return ((int)time.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " сек";
			return ((int)time.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " мин " + time.Seconds.ToString(CultureInfo.InvariantCulture) + " сек";
		}

		private static IEnumerator WarmupOsCacheAsync(ManualLogSource log, long maxBytes, int maxSeconds)
		{
			if (maxBytes <= 0)
				yield break;

			var root = GetRoot();
			if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
				yield break;

			var startedAt = DateTime.UtcNow;
			var bytesRead = 0L;
			var buffer = new byte[1024 * 1024];

			List<string> paths;
			lock (LockObj)
			{
				paths = new List<string>(Entries.Count);
				foreach (var entry in Entries.Values)
				{
					if (entry == null || string.IsNullOrEmpty(entry.RelativePath))
						continue;
					paths.Add(Path.Combine(root, entry.RelativePath));
				}
			}

			if (paths.Count <= 0)
				yield break;

			log?.LogMessage($"CacheFork: extracted assets warmup: прогрев ОС-кэша до {maxBytes / (1024 * 1024)} MB.");

			for (var i = 0; i < paths.Count; i++)
			{
				if (bytesRead >= maxBytes)
					break;
				if (maxSeconds > 0 && (DateTime.UtcNow - startedAt).TotalSeconds >= maxSeconds)
					break;

				var path = paths[i];
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
					continue;

				FileStream fs = null;
				try
				{
					fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					while (bytesRead < maxBytes)
					{
						var toRead = (int)Math.Min(buffer.Length, maxBytes - bytesRead);
						var read = fs.Read(buffer, 0, toRead);
						if (read <= 0)
							break;

						bytesRead += read;
						if (maxSeconds > 0 && (DateTime.UtcNow - startedAt).TotalSeconds >= maxSeconds)
							break;

						yield return null;
					}
				}
				finally
				{
					if (fs != null)
						fs.Dispose();
				}
			}

			log?.LogMessage($"CacheFork: extracted assets warmup готово: {bytesRead / (1024 * 1024)} MB за {FormatTime(DateTime.UtcNow - startedAt)}.");
		}
	}
}
