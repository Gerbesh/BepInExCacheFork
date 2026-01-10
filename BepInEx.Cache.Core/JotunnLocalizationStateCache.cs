using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	internal static class JotunnLocalizationStateCache
	{
		private const int CacheVersion = 1;
		private static readonly Dictionary<string, ModLocalizationState> States = new Dictionary<string, ModLocalizationState>(StringComparer.OrdinalIgnoreCase);
		private static bool _loaded;
		private static bool _dirty;

		internal sealed class ModLocalizationState
		{
			public string ModGuid;
			public Dictionary<string, Dictionary<string, string>> Translations = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
			public List<SourceFileInfo> SourceFiles = new List<SourceFileInfo>();
			public bool CacheValid;
		}

		internal sealed class SourceFileInfo
		{
			public string Path;
			public long Size;
			public long WriteTicks;
			public bool IsJson;
		}

		internal static void EnsureLoaded(ManualLogSource log)
		{
			if (_loaded || !LocalizationCache.IsEnabled)
				return;

			_loaded = true;
			var path = GetCachePath();
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return;

			try
			{
				using (var stream = File.OpenRead(path))
				using (var reader = new BinaryReader(stream, Encoding.UTF8))
				{
					var version = reader.ReadInt32();
					if (version != CacheVersion)
						return;

					var modCount = reader.ReadInt32();
					if (modCount <= 0 || modCount > 2048)
						return;

					for (var i = 0; i < modCount; i++)
					{
						var guid = reader.ReadString();
						var fileCount = reader.ReadInt32();
						var languageCount = reader.ReadInt32();

						var state = new ModLocalizationState
						{
							ModGuid = guid
						};

						for (var f = 0; f < fileCount; f++)
						{
							state.SourceFiles.Add(new SourceFileInfo
							{
								Path = reader.ReadString(),
								Size = reader.ReadInt64(),
								WriteTicks = reader.ReadInt64(),
								IsJson = reader.ReadBoolean()
							});
						}

						for (var l = 0; l < languageCount; l++)
						{
							var language = reader.ReadString();
							var entryCount = reader.ReadInt32();
							if (entryCount < 0 || entryCount > 200000)
								throw new InvalidDataException("слишком большой кеш локализации Jotunn");

							var map = new Dictionary<string, string>(entryCount);
							for (var e = 0; e < entryCount; e++)
							{
								var key = reader.ReadString();
								var value = reader.ReadString();
								if (string.IsNullOrEmpty(key))
									continue;
								map[key] = value ?? string.Empty;
							}

							if (!string.IsNullOrEmpty(language))
								state.Translations[language] = map;
						}

						if (!string.IsNullOrEmpty(guid))
							States[guid] = state;
					}
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось загрузить кеш Jotunn локализаций ({ex.Message}).");
			}
		}

		internal static bool TryGetState(string modGuid, out ModLocalizationState state)
		{
			state = null;
			if (string.IsNullOrEmpty(modGuid))
				return false;

			return States.TryGetValue(modGuid, out state);
		}

		internal static bool ValidateState(ModLocalizationState state)
		{
			if (state == null || state.SourceFiles.Count == 0)
				return false;

			foreach (var file in state.SourceFiles)
			{
				if (string.IsNullOrEmpty(file.Path))
					return false;
				if (!File.Exists(file.Path))
					return false;

				var info = new FileInfo(file.Path);
				if (info.Length != file.Size)
					return false;
				if (info.LastWriteTimeUtc.Ticks != file.WriteTicks)
					return false;
			}

			return true;
		}

		internal static bool IsSourceFileKnown(ModLocalizationState state, string path, bool isJson)
		{
			if (state == null || string.IsNullOrEmpty(path))
				return false;

			for (var i = 0; i < state.SourceFiles.Count; i++)
			{
				var file = state.SourceFiles[i];
				if (file == null)
					continue;
				if (!string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
					continue;
				return file.IsJson == isJson;
			}

			return false;
		}

		internal static void RecordSourceFile(string modGuid, string path, bool isJson)
		{
			if (string.IsNullOrEmpty(modGuid) || string.IsNullOrEmpty(path))
				return;

			if (!States.TryGetValue(modGuid, out var state))
			{
				state = new ModLocalizationState { ModGuid = modGuid };
				States[modGuid] = state;
			}

			var existing = state.SourceFiles.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
			var info = new FileInfo(path);
			var entry = new SourceFileInfo
			{
				Path = path,
				Size = info.Exists ? info.Length : 0,
				WriteTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
				IsJson = isJson
			};

			if (existing >= 0)
				state.SourceFiles[existing] = entry;
			else
				state.SourceFiles.Add(entry);

			_dirty = true;
		}

		internal static void UpdateTranslations(string modGuid, Dictionary<string, Dictionary<string, string>> translations)
		{
			if (string.IsNullOrEmpty(modGuid) || translations == null || translations.Count == 0)
				return;

			if (!States.TryGetValue(modGuid, out var state))
			{
				state = new ModLocalizationState { ModGuid = modGuid };
				States[modGuid] = state;
			}

			state.Translations = CloneTranslations(translations);
			_dirty = true;
		}

		internal static void Save(ManualLogSource log)
		{
			if (!_dirty || !LocalizationCache.IsEnabled)
				return;

			var path = GetCachePath();
			if (string.IsNullOrEmpty(path))
				return;

			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read))
				using (var writer = new BinaryWriter(stream, Encoding.UTF8))
				{
					writer.Write(CacheVersion);
					writer.Write(States.Count);

					foreach (var state in States.Values)
					{
						writer.Write(state.ModGuid ?? string.Empty);
						writer.Write(state.SourceFiles.Count);
						writer.Write(state.Translations.Count);

						foreach (var file in state.SourceFiles)
						{
							writer.Write(file.Path ?? string.Empty);
							writer.Write(file.Size);
							writer.Write(file.WriteTicks);
							writer.Write(file.IsJson);
						}

						foreach (var languageEntry in state.Translations)
						{
							writer.Write(languageEntry.Key ?? string.Empty);
							var entries = languageEntry.Value ?? new Dictionary<string, string>();
							writer.Write(entries.Count);
							foreach (var entry in entries)
							{
								writer.Write(entry.Key ?? string.Empty);
								writer.Write(entry.Value ?? string.Empty);
							}
						}
					}
				}

				_dirty = false;
				log?.LogMessage("CacheFork: кеш Jotunn локализаций обновлён.");
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось сохранить кеш Jotunn локализаций ({ex.Message}).");
			}
		}

		internal static Dictionary<string, Dictionary<string, string>> CloneTranslations(Dictionary<string, Dictionary<string, string>> source)
		{
			var copy = new Dictionary<string, Dictionary<string, string>>(source.Count, StringComparer.OrdinalIgnoreCase);
			foreach (var languageEntry in source)
			{
				var language = languageEntry.Key;
				var map = languageEntry.Value;
				if (string.IsNullOrEmpty(language) || map == null)
					continue;

				var mapCopy = new Dictionary<string, string>(map.Count);
				foreach (var entry in map)
					mapCopy[entry.Key] = entry.Value;
				copy[language] = mapCopy;
			}
			return copy;
		}

		private static string GetCachePath()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			var stateRoot = Path.Combine(cacheRoot, "state");
			var processRoot = Path.Combine(stateRoot, processName);
			return Path.Combine(processRoot, "jotunn_localization_state.bin");
		}
	}
}
