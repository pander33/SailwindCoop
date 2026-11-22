using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// KemyFurnitureMod (com.kemy.kemyfurniture) levels nailed furniture via AutoLeveler.ExecuteLevel
    /// by mutating transform/ItemRigidbody directly. Relay the resulting pose through ItemSync.
    /// </summary>
    public static class KemyFurnitureCompat
    {
        public const string Guid = "com.kemy.kemyfurniture";
        private const string AssemblyName = "KemyFurniture";

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static MethodInfo _executeLevel;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _executeLevel = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                var type = asm?.GetType("KemyFurniture.Core.AutoLeveler");
                _executeLevel = type != null ? AccessTools.Method(type, "ExecuteLevel", new[] { typeof(ShipItem) }) : null;
                SyncEnabled = _executeLevel != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[KemyFurnitureCompat] KemyFurnitureMod detected; AutoLeveler sync enabled.");
                else
                    Plugin.Logger.LogWarning("[KemyFurnitureCompat] KemyFurnitureMod detected, but AutoLeveler.ExecuteLevel not found.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[KemyFurnitureCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled || _executeLevel == null) return;
            try
            {
                harmony.Patch(_executeLevel, postfix: new HarmonyMethod(typeof(KemyFurnitureCompat), nameof(PostExecuteLevel)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[KemyFurnitureCompat] Patching threw: " + e.Message);
            }
        }

        private static void PostExecuteLevel(ShipItem __0, bool __result)
        {
            try
            {
                if (!__result || __0 == null) return;
                if (Runtime.CoopBehaviour.Instance?.PlayingCoop != true) return;
                ItemSync.Instance?.NotifyItemStateChanged(__0, "kemy-autolevel");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[KemyFurnitureCompat] PostExecuteLevel: " + e.Message);
            }
        }
    }
}
