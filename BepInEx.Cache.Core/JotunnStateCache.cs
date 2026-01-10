using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	internal static class JotunnStateCache
	{
		private const int CacheVersion = 1;
		private static readonly Dictionary<string, RegistryEntry> Entries = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> PayloadWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static bool _loaded;
		private static bool _valid;
		private static bool _dirty;
		private static bool _restoring;

		internal static bool IsValid => _loaded && _valid;
		internal static bool IsRestoring => _restoring;

		internal sealed class RegistryEntry
		{
			internal string Kind;
			internal string Name;
			internal string ModGuid;
			internal byte[] Payload;
		}

		internal static void EnsureLoaded(ManualLogSource log)
		{
			if (_loaded)
				return;

			_loaded = true;

			if (!CacheConfig.EnableStateCache || !CacheManager.CacheHit)
				return;

			var path = GetStateFilePath();
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return;

			try
			{
				using (var stream = File.OpenRead(path))
				using (var reader = new BinaryReader(stream))
				{
					var version = reader.ReadInt32();
					if (version != CacheVersion)
						return;

					var fingerprint = reader.ReadString();
					var currentFingerprint = CacheFingerprint.Compute(log);
					if (!string.Equals(fingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
						return;

					var count = reader.ReadInt32();
					for (var i = 0; i < count; i++)
					{
						var entry = new RegistryEntry
						{
							Kind = reader.ReadString(),
							Name = reader.ReadString(),
							ModGuid = reader.ReadString()
						};

						var hasPayload = reader.ReadBoolean();
						if (hasPayload)
						{
							var length = reader.ReadInt32();
							if (length < 0 || length > 50 * 1024 * 1024)
								throw new InvalidDataException("слишком большой кеш состояния Jotunn");
							entry.Payload = reader.ReadBytes(length);
						}

						Entries[BuildKey(entry.Kind, entry.Name, entry.ModGuid)] = entry;
					}
				}

				_valid = true;
				log?.LogMessage("CacheFork: кеш состояния Jotunn загружен.");
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось загрузить кеш состояния Jotunn ({ex.Message}).");
			}
		}

		internal static void RecordEntry(string kind, string name, string modGuid, object payload, ManualLogSource log)
		{
			if (!CacheConfig.EnableStateCache || _restoring)
				return;

			if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name))
				return;

			var key = BuildKey(kind, name, modGuid);
			if (Entries.ContainsKey(key))
				return;

			Entries[key] = new RegistryEntry
			{
				Kind = kind,
				Name = name,
				ModGuid = modGuid,
				Payload = TrySerializePayload(payload, log)
			};

			_dirty = true;
		}

		internal static List<RegistryEntry> GetEntries(string kind)
		{
			if (string.IsNullOrEmpty(kind))
				return new List<RegistryEntry>();

			var result = new List<RegistryEntry>();
			foreach (var entry in Entries.Values)
			{
				if (string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
					result.Add(entry);
			}

			return result;
		}

		internal static void Save(ManualLogSource log)
		{
			if (!CacheConfig.EnableStateCache || !_dirty)
				return;

			var root = GetStateRoot();
			if (string.IsNullOrEmpty(root))
				return;

			try
			{
				Directory.CreateDirectory(root);
				var path = GetStateFilePath();
				var fingerprint = CacheFingerprint.Compute(log);
				if (string.IsNullOrEmpty(fingerprint))
					return;

				using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
				using (var writer = new BinaryWriter(stream))
				{
					writer.Write(CacheVersion);
					writer.Write(fingerprint);
					writer.Write(Entries.Count);
					foreach (var entry in Entries.Values)
					{
						writer.Write(entry.Kind ?? string.Empty);
						writer.Write(entry.Name ?? string.Empty);
						writer.Write(entry.ModGuid ?? string.Empty);
						var payload = entry.Payload;
						writer.Write(payload != null);
						if (payload != null)
						{
							writer.Write(payload.Length);
							writer.Write(payload);
						}
					}
				}

				_dirty = false;
				log?.LogMessage("CacheFork: кеш состояния Jotunn сохранён.");
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось сохранить кеш состояния Jotunn ({ex.Message}).");
			}
		}

		internal static void BeginRestore()
		{
			_restoring = true;
		}

		internal static void EndRestore()
		{
			_restoring = false;
		}

		internal static object TryDeserializePayload(byte[] payload, ManualLogSource log)
		{
			if (payload == null || payload.Length == 0)
				return null;

			try
			{
				using (var stream = new MemoryStream(payload))
				{
					var formatter = new BinaryFormatter();
					return formatter.Deserialize(stream);
				}
			}
			catch (Exception ex)
			{
				log?.LogWarning($"CacheFork: не удалось восстановить объект Jotunn ({ex.Message}).");
				return null;
			}
		}

		private static string BuildKey(string kind, string name, string modGuid)
		{
			return $"{kind}|{modGuid}|{name}";
		}

		private static byte[] TrySerializePayload(object payload, ManualLogSource log)
		{
			if (payload == null)
				return null;

			try
			{
				using (var stream = new MemoryStream())
				{
					var formatter = new BinaryFormatter();
					formatter.Serialize(stream, payload);
					return stream.ToArray();
				}
			}
			catch (Exception ex)
			{
				var typeName = payload.GetType().FullName ?? "unknown";
				if (!PayloadWarnings.Contains(typeName))
				{
					PayloadWarnings.Add(typeName);
					log?.LogMessage($"CacheFork: объект Jotunn {typeName} не сериализуется, сохранён только идентификатор ({ex.Message}).");
				}

				return null;
			}
		}

		private static string GetStateRoot()
		{
			var cacheRoot = CacheConfig.CacheDirResolved ?? Paths.CachePath;
			if (string.IsNullOrEmpty(cacheRoot))
				return null;

			var processName = Paths.ProcessName ?? string.Empty;
			var stateRoot = Path.Combine(cacheRoot, "state");
			return Path.Combine(stateRoot, processName);
		}

		private static string GetStateFilePath()
		{
			var root = GetStateRoot();
			return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "jotunn_state.bin");
		}
	}
}
