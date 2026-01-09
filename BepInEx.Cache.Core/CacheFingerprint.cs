using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class CacheFingerprint
	{
		public static string Compute(ManualLogSource logSource)
		{
			var log = logSource ?? Logger.CreateLogSource("BepInEx.Cache");
			var files = CollectFiles();

			if (files.Count == 0)
				return string.Empty;

			files.Sort(StringComparer.OrdinalIgnoreCase);

			using (var sha = SHA256.Create())
			{
				foreach (var file in files)
				{
					AppendString(sha, GetRelativePath(file));
					AppendString(sha, "|");

					try
					{
						var info = new FileInfo(file);
						AppendString(sha, info.Length.ToString(CultureInfo.InvariantCulture));
						AppendString(sha, "|");

						using (var stream = File.OpenRead(file))
							AppendStream(sha, stream);
					}
					catch (Exception ex)
					{
						log.LogWarning($"Не удалось прочитать файл для хеша: {file} ({ex.Message})");
						AppendString(sha, "missing");
					}

					AppendString(sha, "\n");
				}

				sha.TransformFinalBlock(new byte[0], 0, 0);
				return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
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
