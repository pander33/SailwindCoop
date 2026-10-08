using System;
using HarmonyLib;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
	internal static class DiceSavePatches
	{
		public static void Apply(Harmony harmony)
		{
			// The game keeps GameState.modData across a new game, and across a load of a save that has none.
			var hooks = new PatchHookCatalog();
			hooks.Install(typeof(SaveLoadManager), "LoadGame", new[] { typeof(int) }, method => harmony.Patch(method,
				prefix: new HarmonyMethod(typeof(DiceSavePatches), nameof(WorldChanging))));
			hooks.Install(typeof(StartMenu), "StartNewGame", Type.EmptyTypes, method => harmony.Patch(method,
				prefix: new HarmonyMethod(typeof(DiceSavePatches), nameof(WorldChanging))));
			PatchHealth.Report("Dice save lifecycle", hooks);
		}
		private static void WorldChanging() => PatchGuard.Run(() => CoopBehaviour.Instance?.Dice?.WorldChanging(), Report);
		private static void Report(Exception error) => Plugin.Logger.LogWarning("[Dice save] " + error);
	}
}
