using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	internal static class CacheMetrics
	{
		private static readonly object LockObj = new object();
		private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
		private static bool _enabled = true;

		internal sealed class Stat
		{
			internal long Count;
			internal long TotalTicks;
			internal long MaxTicks;
		}

		internal static bool Enabled
		{
			get => _enabled;
			set => _enabled = value;
		}

		internal static IDisposable Measure(string name)
		{
			if (!_enabled || string.IsNullOrEmpty(name))
				return NoopScope.Instance;

			return new Scope(name);
		}

		internal static void Add(string name, long ticks)
		{
			if (!_enabled || string.IsNullOrEmpty(name) || ticks <= 0)
				return;

			lock (LockObj)
			{
				if (!Stats.TryGetValue(name, out var stat) || stat == null)
				{
					stat = new Stat();
					Stats[name] = stat;
				}

				stat.Count++;
				stat.TotalTicks += ticks;
				if (ticks > stat.MaxTicks)
					stat.MaxTicks = ticks;
			}
		}

		internal static void LogSummary(ManualLogSource log, string header, int top, int minMs)
		{
			if (!_enabled)
				return;

			List<KeyValuePair<string, Stat>> snapshot;
			lock (LockObj)
			{
				snapshot = new List<KeyValuePair<string, Stat>>(Stats.Count);
				foreach (var kv in Stats)
				{
					if (kv.Value != null)
						snapshot.Add(kv);
				}
			}

			if (snapshot.Count == 0)
				return;

			snapshot.Sort((a, b) =>
			{
				var at = a.Value.TotalTicks;
				var bt = b.Value.TotalTicks;
				if (at == bt) return 0;
				return at > bt ? -1 : 1;
			});

			var minTicks = (long)minMs * Stopwatch.Frequency / 1000L;
			var lines = new List<string>();

			lines.Add(string.IsNullOrEmpty(header) ? "CacheFork: timing summary:" : header);

			var added = 0;
			for (var i = 0; i < snapshot.Count && added < top; i++)
			{
				var name = snapshot[i].Key;
				var stat = snapshot[i].Value;
				if (stat == null)
					continue;

				if (stat.TotalTicks < minTicks)
					continue;

				var totalMs = stat.TotalTicks * 1000.0 / Stopwatch.Frequency;
				var maxMs = stat.MaxTicks * 1000.0 / Stopwatch.Frequency;
				lines.Add(string.Format(CultureInfo.InvariantCulture,
					"  - {0}: total={1:0}ms, count={2}, max={3:0}ms",
					name, totalMs, stat.Count, maxMs));
				added++;
			}

			if (added == 0)
				return;

			for (var i = 0; i < lines.Count; i++)
				log?.LogMessage(lines[i]);
		}

		private sealed class Scope : IDisposable
		{
			private readonly string _name;
			private readonly long _start;
			private bool _disposed;

			internal Scope(string name)
			{
				_name = name;
				_start = Stopwatch.GetTimestamp();
			}

			public void Dispose()
			{
				if (_disposed)
					return;
				_disposed = true;
				var elapsed = Stopwatch.GetTimestamp() - _start;
				Add(_name, elapsed);
			}
		}

		private sealed class NoopScope : IDisposable
		{
			internal static readonly NoopScope Instance = new NoopScope();
			public void Dispose() { }
		}
	}
}
