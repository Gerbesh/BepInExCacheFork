using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class CacheFingerprint
	{
		internal const string SnapshotFileName = "fingerprint_snapshot.txt";

		internal struct SnapshotEntry
		{
			public string RelativePath;
			public string FullPath;
			public long Size;
			public long LastWriteTimeUtcTicks;
			public bool Exists;
		}

		public static string Compute(ManualLogSource logSource)
		{
			var log = logSource ?? Logger.CreateLogSource("BepInEx.Cache");
			var files = CollectFiles();

			if (files.Count == 0)
				return string.Empty;

			files.Sort(StringComparer.OrdinalIgnoreCase);

			using (var sha = SHA256.Create())
			{
				// Strict режим читаeт содержимое всех файлов, поэтому оставляем последовательный путь.
				// В Fast режиме используем параллельную подготовку строк-сегментов (I/O + метаданные) и
				// затем последовательно хешируем их в фиксированном порядке, сохраняя стабильность результата.
				if (IsStrictMode() || CacheConfig.FingerprintParallelism <= 1 || files.Count < 8)
				{
					for (var i = 0; i < files.Count; i++)
						AppendFingerprintSegmentSequential(sha, log, files[i]);
				}
				else
				{
					var segments = BuildFastSegmentsParallel(log, files, CacheConfig.FingerprintParallelism);
					for (var i = 0; i < segments.Length; i++)
						AppendString(sha, segments[i] ?? string.Empty);
				}

				sha.TransformFinalBlock(new byte[0], 0, 0);
				return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
			}
		}

		private static void AppendFingerprintSegmentSequential(HashAlgorithm sha, ManualLogSource log, string file)
		{
			AppendString(sha, GetRelativePath(file));
			AppendString(sha, "|");

			try
			{
				var info = new FileInfo(file);
				AppendString(sha, info.Length.ToString(CultureInfo.InvariantCulture));
				AppendString(sha, "|");

				// AutoTranslatorConfig.ini часто "трогается" при старте, меняя только timestamp.
				// Чтобы не ломать cache-hit, в Fast-режиме учитываем содержимое, а не LastWriteTimeUtc.
				if (!IsStrictMode() && ShouldUseContentHashInFastMode(file))
				{
					using (var stream = File.OpenRead(file))
					{
						var contentHash = ComputeSha256Hex(stream);
						AppendString(sha, "content:");
						AppendString(sha, contentHash);
					}
					AppendString(sha, "|");
				}
				else
				{
					AppendString(sha, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
					AppendString(sha, "|");

					if (IsStrictMode())
					{
						using (var stream = File.OpenRead(file))
							AppendStream(sha, stream);
					}
				}
			}
			catch (Exception ex)
			{
				log.LogWarning($"Не удалось прочитать файл для хеша: {file} ({ex.Message})");
				AppendString(sha, "missing");
			}

			AppendString(sha, "\n");
		}

		private static string[] BuildFastSegmentsParallel(ManualLogSource log, List<string> files, int parallelism)
		{
			var segments = new string[files.Count];
			var nextIndex = -1;
			var workerCount = Math.Min(Math.Max(1, parallelism), files.Count);
			var workersLeft = workerCount;
			using (var done = new ManualResetEvent(false))
			{
				for (var w = 0; w < workerCount; w++)
				{
					ThreadPool.QueueUserWorkItem(_ =>
					{
						try
						{
							while (true)
							{
								var idx = Interlocked.Increment(ref nextIndex);
								if (idx >= files.Count)
									break;

								var file = files[idx];
								segments[idx] = BuildFastSegment(log, file);
							}
						}
						catch
						{
						}
						finally
						{
							if (Interlocked.Decrement(ref workersLeft) == 0)
								done.Set();
						}
					});
				}

				done.WaitOne();
			}

			return segments;
		}

		private static string BuildFastSegment(ManualLogSource log, string file)
		{
			var rel = GetRelativePath(file);
			try
			{
				var info = new FileInfo(file);
				var size = info.Length.ToString(CultureInfo.InvariantCulture);

				// AutoTranslatorConfig.ini часто "трогается" при старте, меняя только timestamp.
				// Чтобы не ломать cache-hit, в Fast-режиме учитываем содержимое, а не LastWriteTimeUtc.
				if (ShouldUseContentHashInFastMode(file))
				{
					using (var stream = File.OpenRead(file))
					{
						var contentHash = ComputeSha256Hex(stream);
						return rel + "|" + size + "|content:" + contentHash + "|\n";
					}
				}

				var ticks = info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
				return rel + "|" + size + "|" + ticks + "|\n";
			}
			catch (Exception ex)
			{
				log.LogWarning($"Не удалось прочитать файл для хеша: {file} ({ex.Message})");
				return rel + "|missing\n";
			}
		}

		internal static List<SnapshotEntry> CollectSnapshotEntries(ManualLogSource logSource)
		{
			var log = logSource ?? Logger.CreateLogSource("BepInEx.Cache");
			var files = CollectFiles();
			files.Sort(StringComparer.OrdinalIgnoreCase);

			var result = new List<SnapshotEntry>(files.Count);
			foreach (var file in files)
			{
				var entry = new SnapshotEntry
				{
					RelativePath = GetRelativePath(file),
					FullPath = file,
					Exists = false,
					Size = 0,
					LastWriteTimeUtcTicks = 0
				};

				try
				{
					var info = new FileInfo(file);
					entry.Exists = info.Exists;
					entry.Size = info.Exists ? info.Length : 0;
					entry.LastWriteTimeUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось прочитать файл для fingerprint-snapshot: {file} ({ex.Message})");
				}

				result.Add(entry);
			}

			return result;
		}

		internal static void WriteSnapshot(ManualLogSource logSource, string cacheRoot)
		{
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			try
			{
				var path = Path.Combine(cacheRoot, SnapshotFileName);
				var entries = CollectSnapshotEntries(logSource);
				var lines = new List<string>(entries.Count + 4)
				{
					"# CacheFork fingerprint snapshot",
					"# Формат: relativePath|size|lastWriteUtcTicks|exists|fullPath",
					"# Важно: fullPath добавлен для диагностики; может содержать абсолютные пути.",
					"# ---"
				};

				for (var i = 0; i < entries.Count; i++)
				{
					var e = entries[i];
					lines.Add($"{e.RelativePath}|{e.Size.ToString(CultureInfo.InvariantCulture)}|{e.LastWriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture)}|{e.Exists.ToString()}|{e.FullPath}");
				}

				File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
			}
			catch (Exception ex)
			{
				logSource?.LogWarning($"CacheFork: не удалось записать fingerprint-snapshot ({ex.Message}).");
			}
		}

		internal static void LogDiffAgainstSnapshot(ManualLogSource logSource, string cacheRoot, int maxLines)
		{
			if (logSource == null || string.IsNullOrEmpty(cacheRoot))
				return;

			try
			{
				var snapshotPath = Path.Combine(cacheRoot, SnapshotFileName);
				if (!File.Exists(snapshotPath))
					return;

				var oldEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (var line in File.ReadAllLines(snapshotPath))
				{
					if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
						continue;

					var parts = line.Split(new[] { '|' }, 5);
					if (parts.Length < 4)
						continue;

					var relative = parts[0];
					var size = parts[1];
					var ticks = parts[2];
					var exists = parts[3];
					oldEntries[relative] = $"{size}|{ticks}|{exists}";
				}

				var current = CollectSnapshotEntries(logSource);
				var changes = new List<string>();
				var compared = 0;
				var changed = 0;
				var missing = 0;
				var added = 0;

				for (var i = 0; i < current.Count; i++)
				{
					var e = current[i];
					compared++;

					var currentKey = $"{e.Size.ToString(CultureInfo.InvariantCulture)}|{e.LastWriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture)}|{e.Exists.ToString()}";
					if (oldEntries.TryGetValue(e.RelativePath, out var oldKey))
					{
						if (!string.Equals(oldKey, currentKey, StringComparison.Ordinal))
						{
							changed++;
							if (changes.Count < maxLines)
								changes.Add($"изменён: {e.RelativePath} (old={oldKey}, new={currentKey})");
						}

						oldEntries.Remove(e.RelativePath);
					}
					else
					{
						added++;
						if (changes.Count < maxLines)
							changes.Add($"добавлен: {e.RelativePath} (new={currentKey})");
					}
				}

				foreach (var kv in oldEntries)
				{
					missing++;
					if (changes.Count < maxLines)
						changes.Add($"удалён: {kv.Key} (old={kv.Value})");
				}

				if (changed == 0 && added == 0 && missing == 0)
					return;

				logSource.LogMessage($"CacheFork: fingerprint diff: изменено={changed}, добавлено={added}, удалено={missing}, всего={compared}.");
				for (var i = 0; i < changes.Count; i++)
					logSource.LogMessage($"CacheFork: fingerprint diff: {changes[i]}");
			}
			catch (Exception ex)
			{
				logSource?.LogWarning($"CacheFork: не удалось сравнить fingerprint-snapshot ({ex.Message}).");
			}
		}

		private static bool IsStrictMode()
		{
			try
			{
				var mode = CacheConfig.FingerprintMode;
				if (string.IsNullOrEmpty(mode))
					return false;
				return mode.Trim().Equals("Strict", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static bool ShouldUseContentHashInFastMode(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path))
					return false;

				// Явный хак под XUnity.AutoTranslator (это единственный конфиг, который мы добавляем в fingerprint).
				// Условие по имени файла достаточно для текущего сценария.
				var fileName = Path.GetFileName(path) ?? string.Empty;
				return fileName.Equals("AutoTranslatorConfig.ini", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static string ComputeSha256Hex(Stream stream)
		{
			try
			{
				using (var sha = SHA256.Create())
				{
					var buffer = new byte[8192];
					int read;
					while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
						sha.TransformBlock(buffer, 0, read, null, 0);

					sha.TransformFinalBlock(new byte[0], 0, 0);
					return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
				}
			}
			catch
			{
				return string.Empty;
			}
		}

		private static List<string> CollectFiles()
		{
			var files = new List<string>();

			AddIfExists(files, Paths.ExecutablePath);
			AddManagedUnity(files);
			AddDirectoryDlls(files, Paths.PluginPath);
			AddDirectoryDlls(files, Paths.PatcherPluginPath);
			AddAssetBundles(files, Paths.PluginPath);
			AddLocalizationFiles(files);

			return files;
		}

		private static void AddManagedUnity(List<string> files)
		{
			if (string.IsNullOrEmpty(Paths.ManagedPath) || !Directory.Exists(Paths.ManagedPath))
				return;

			AddIfExists(files, Path.Combine(Paths.ManagedPath, "UnityEngine.CoreModule.dll"));
			AddIfExists(files, Path.Combine(Paths.ManagedPath, "UnityEngine.dll"));
		}

		private static void AddDirectoryDlls(List<string> files, string directory)
		{
			if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				return;

			files.AddRange(Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories));
		}

		private static void AddAssetBundles(List<string> files, string directory)
		{
			if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				return;

			foreach (var file in AssetCache.EnumerateBundleFiles(directory))
				files.Add(file);
		}

		private static void AddLocalizationFiles(List<string> files)
		{
			if (!LocalizationCache.IsEnabled)
				return;

			if (string.IsNullOrEmpty(Paths.BepInExRootPath))
				return;

			var translationRoot = Path.Combine(Paths.BepInExRootPath, "Translation");
			foreach (var file in LocalizationCache.EnumerateLocalizationFiles(translationRoot))
				files.Add(file);

			var autoTranslatorConfigPath = LocalizationCache.GetAutoTranslatorConfigPathIfAvailable();
			if (!string.IsNullOrEmpty(autoTranslatorConfigPath))
				files.Add(autoTranslatorConfigPath);
		}

		private static void AddIfExists(List<string> files, string path)
		{
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
				files.Add(path);
		}

		private static string GetRelativePath(string path)
		{
			var root = Paths.GameRootPath;
			if (string.IsNullOrEmpty(root))
				return path;

			try
			{
				var rootUri = new Uri(AppendDirectorySeparator(root));
				var fileUri = new Uri(path);
				var relative = rootUri.MakeRelativeUri(fileUri).ToString();
				return Uri.UnescapeDataString(relative.Replace('/', Path.DirectorySeparatorChar));
			}
			catch
			{
				return path;
			}
		}

		private static string AppendDirectorySeparator(string path)
		{
			if (string.IsNullOrEmpty(path))
				return path;
			if (path[path.Length - 1] == Path.DirectorySeparatorChar)
				return path;
			return path + Path.DirectorySeparatorChar;
		}

		private static void AppendString(HashAlgorithm hash, string value)
		{
			if (string.IsNullOrEmpty(value))
				return;

			var bytes = Encoding.UTF8.GetBytes(value);
			hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
		}

		private static void AppendStream(HashAlgorithm hash, Stream stream)
		{
			var buffer = new byte[8192];
			int read;
			while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
				hash.TransformBlock(buffer, 0, read, null, 0);
		}
	}
}
