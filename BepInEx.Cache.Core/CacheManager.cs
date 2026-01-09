using BepInEx.Logging;

namespace BepInEx.Cache.Core
{
	public static class CacheManager
	{
		private static readonly object InitLock = new object();
		private static bool _initialized;
		private static ManualLogSource _log;

		public static ManualLogSource Log => _log;

		public static void Initialize()
		{
			if (_initialized)
				return;

			lock (InitLock)
			{
				if (_initialized)
					return;

				_log = Logger.CreateLogSource("BepInEx.Cache");
				CacheConfig.Initialize(_log);
				_initialized = true;
			}
		}

		public static string BuildFingerprint()
		{
			Initialize();
			return CacheFingerprint.Compute(_log);
		}
	}
}