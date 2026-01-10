using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Cache.Core
{
	internal sealed class CacheForkUnityRunner : MonoBehaviour
	{
		private static CacheForkUnityRunner _instance;
		private static ManualLogSource _log;

		internal static CacheForkUnityRunner Instance => _instance;

		internal static CacheForkUnityRunner Ensure(ManualLogSource log)
		{
			try
			{
				_log = log ?? _log;

				if (_instance != null)
					return _instance;

				var go = new GameObject("CacheFork.Runtime");
				DontDestroyOnLoad(go);
				_instance = go.AddComponent<CacheForkUnityRunner>();
				return _instance;
			}
			catch (Exception ex)
			{
				_log?.LogWarning($"CacheFork: не удалось создать runtime runner ({ex.Message}).");
				return null;
			}
		}

		internal void StartRoutine(IEnumerator routine)
		{
			if (routine == null)
				return;
			StartCoroutine(routine);
		}

		private void OnDestroy()
		{
			if (_instance == this)
				_instance = null;
		}
	}
}

