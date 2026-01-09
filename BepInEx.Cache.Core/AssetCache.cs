using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class AssetCache
	{
		private static readonly string[] BundleExtensions =
		{
			".unity3d",
			".bundle",
			".assetbundle",
			".ab"
		};

		public static bool IsEnabled => CacheConfig.EnableCache && CacheConfig.EnableAssetsCache;

		internal static IEnumerable<string> EnumerateBundleFiles(string rootPath)
		{
			if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
				yield break;

			foreach (var file in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories))
			{
				var extension = Path.GetExtension(file);
				if (!string.IsNullOrEmpty(extension))
				{
					foreach (var candidate in BundleExtensions)
					{
						if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
						{
							yield return file;
							goto NextFile;
						}
					}
				}

				if (IsUnityBundle(file))
					yield return file;

				NextFile:
				continue;
			}
		}

		private static bool IsUnityBundle(string path)
		{
			try
			{
				using (var stream = File.OpenRead(path))
				{
					var buffer = new byte[8];
					var read = stream.Read(buffer, 0, buffer.Length);
					if (read < 6)
						return false;

					var signature = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
					return signature.StartsWith("UnityFS", StringComparison.Ordinal) || signature.StartsWith("UnityWeb", StringComparison.Ordinal);
				}
			}
			catch
			{
				return false;
			}
		}

		public static bool IsReady(ManualLogSource log)
		{
			if (!IsEnabled)
				return true;

			var cacheRoot = GetCacheRoot();
			if (string.IsNullOrEmpty(cacheRoot))
				return false;

			var manifestPath = GetManifestPath(cacheRoot);
			if (!File.Exists(manifestPath))
				return false;

			try
			{
				foreach (var line in File.ReadAllLines(manifestPath))
				{
					if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
						continue;

					var parts = line.Split('|');
					if (parts.Length < 2)
						continue;

					var relativePath = parts[0];
					var sizeText = parts[1];
					if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedSize))
						continue;

					var cachedPath = Path.Combine(cacheRoot, relativePath);
					if (!File.Exists(cachedPath))
						return false;

					var info = new FileInfo(cachedPath);
					if (info.Length != expectedSize)
						return false;
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось проверить кеш ассетов ({ex.Message}).");
				return false;
			}

			return true;
		}

		public static void Build(ManualLogSource log)
		{
			if (!IsEnabled)
				return;

			var cacheRoot = GetCacheRoot();
			if (string.IsNullOrEmpty(cacheRoot))
				return;

			var pluginRoot = Paths.PluginPath;
			if (string.IsNullOrEmpty(pluginRoot) || !Directory.Exists(pluginRoot))
				return;

			Directory.CreateDirectory(cacheRoot);

			var manifestLines = new List<string>();
			long totalBytes = 0;
			var count = 0;

			foreach (var file in EnumerateBundleFiles(pluginRoot))
			{
				var relativePath = GetRelativePath(pluginRoot, file);
				if (string.IsNullOrEmpty(relativePath))
					continue;

				var destPath = Path.Combine(cacheRoot, relativePath);
				var destDir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
					Directory.CreateDirectory(destDir);

				try
				{
					var sourceInfo = new FileInfo(file);
					var copyNeeded = true;

					if (File.Exists(destPath))
					{
						var destInfo = new FileInfo(destPath);
						if (destInfo.Length == sourceInfo.Length)
							copyNeeded = false;
					}

					if (copyNeeded)
						File.Copy(file, destPath, true);

					manifestLines.Add($"{relativePath}|{sourceInfo.Length.ToString(CultureInfo.InvariantCulture)}");
					totalBytes += sourceInfo.Length;
					count++;
				}
				catch (Exception ex)
				{
					log?.LogWarning($"CacheFork: не удалось кешировать ассет {file} ({ex.Message}).");
				}
			}

			try
			{
				File.WriteAllLines(GetManifestPath(cacheRoot), manifestLines.ToArray());
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось записать манифест ассетов ({ex.Message}).");
			}

			if (count > 0)
				log?.LogMessage($"CacheFork: кеш ассетов обновлён (файлов: {count}, размер: {FormatBytes(totalBytes)}).");
			else
				log?.LogMessage("CacheFork: ассеты для кеширования не найдены.");
		}

		private static string GetCacheRoot()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			var assetsRoot = Path.Combine(cacheRoot, "assets");
			return Path.Combine(assetsRoot, processName);
		}

		private static string GetManifestPath(string cacheRoot)
		{
			return Path.Combine(cacheRoot, "assets_manifest.txt");
		}

		private static string GetRelativePath(string rootPath, string fullPath)
		{
			try
			{
				var rootUri = new Uri(AppendDirectorySeparator(rootPath));
				var fileUri = new Uri(fullPath);
				var relative = rootUri.MakeRelativeUri(fileUri).ToString();
				return Uri.UnescapeDataString(relative.Replace('/', Path.DirectorySeparatorChar));
			}
			catch
			{
				return null;
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

		private static string FormatBytes(long bytes)
		{
			const double kb = 1024;
			const double mb = kb * 1024;
			const double gb = mb * 1024;

			if (bytes >= gb)
				return (bytes / gb).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
			if (bytes >= mb)
				return (bytes / mb).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
			if (bytes >= kb)
				return (bytes / kb).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
			return bytes.ToString(CultureInfo.InvariantCulture) + " B";
		}
	}
}
