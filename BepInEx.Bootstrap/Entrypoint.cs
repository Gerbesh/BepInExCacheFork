using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BepInEx.Bootstrap
{
	public static class Entrypoint
	{
		// Флаг для ранней инициализации Jewelry patching
		private static bool _jewelcraftingPatcherInitialized = false;

		public static void Init()
		{
			AppDomain.CurrentDomain.AssemblyResolve += ResolveBepInEx;
			AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;

			Linker.StartBepInEx();
		}

		private static readonly string LocalDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

		private static Assembly ResolveBepInEx(object sender, ResolveEventArgs args)
		{
			string path = Path.Combine(LocalDirectory, $@"BepInEx\core\{new AssemblyName(args.Name).Name}.dll");

			if (!File.Exists(path))
				return null;

			try
			{
				return Assembly.LoadFile(path);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
		{
			// Ранняя инициализация JewelcraftingNullSafePatcher
			// Это сработает ДО того, как BepInEx.Cache.Core загружается
			if (!_jewelcraftingPatcherInitialized && args?.LoadedAssembly?.GetName().Name == "Jewelcrafting")
			{
				_jewelcraftingPatcherInitialized = true;
				TryInitializeJewelcraftingPatcher();
			}
		}

		private static void TryInitializeJewelcraftingPatcher()
		{
			try
			{
				// Пытаемся загрузить BepInEx.Cache.Core и инициализировать пэтчер
				string corePath = Path.Combine(LocalDirectory, "BepInEx");
				corePath = Path.Combine(corePath, "core");
				string cacheCorePath = Path.Combine(corePath, "BepInEx.Cache.Core.dll");
				
				if (!File.Exists(cacheCorePath))
					return;

				var cacheAssembly = Assembly.LoadFrom(cacheCorePath);
				var patcherType = cacheAssembly.GetType("BepInEx.Cache.Core.JewelcraftingNullSafePatcher");
				if (patcherType == null)
					return;

				// Вызываем Initialize с null (логирование пока не доступно на этом этапе)
				var initMethod = patcherType.GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public);
				if (initMethod != null)
				{
					initMethod.Invoke(null, new object[] { null });
				}
			}
			catch
			{
				// Молчим при ошибках - это слишком ранний этап
			}
		}
	}
}