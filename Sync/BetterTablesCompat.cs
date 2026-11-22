using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// BetterTables (com.nandbrew.bettertables) locks its AutoLevel tabletop 3 frames after spawn
    /// and only unlocks when shipItem.held is true. Hold locked=false and locking=true while a
    /// remote player carries the table (avoiding per-frame LockRoutine coroutine leaks), then
    /// release locking=false once on drop so AutoLevel re-levels and locks cleanly.
    /// </summary>
    public static class BetterTablesCompat
    {
        public const string Guid = "com.nandbrew.bettertables";
        private const string AssemblyName = "BetterTables";

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static MethodInfo _lateUpdate;
        private static FieldInfo _shipItem;
        private static FieldInfo _locked;
        private static FieldInfo _locking;
        private static readonly HashSet<object> _remoteUnlocked = new HashSet<object>();

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _lateUpdate = null;
            _shipItem = _locked = _locking = null;
            _remoteUnlocked.Clear();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                var type = asm?.GetType("BetterTables.AutoLevel");
                _lateUpdate = type != null ? AccessTools.Method(type, "LateUpdate") : null;
                _shipItem = type != null ? AccessTools.Field(type, "shipItem") : null;
                _locked = type != null ? AccessTools.Field(type, "locked") : null;
                _locking = type != null ? AccessTools.Field(type, "locking") : null;
                SyncEnabled = _lateUpdate != null && _shipItem != null && _locked != null && _locking != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[BetterTablesCompat] BetterTables detected; remote AutoLevel unlock enabled.");
                else
                    Plugin.Logger.LogWarning("[BetterTablesCompat] BetterTables detected, but AutoLevel members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BetterTablesCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled || _lateUpdate == null) return;
            try
            {
                harmony.Patch(_lateUpdate, prefix: new HarmonyMethod(typeof(BetterTablesCompat), nameof(PreLateUpdate)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BetterTablesCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _remoteUnlocked.Clear();
        }

        private static void PreLateUpdate(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var items = ItemSync.Instance;
                if (items == null || Runtime.CoopBehaviour.Instance?.PlayingCoop != true)
                {
                    if (_remoteUnlocked.Remove(__instance))
                        _locking.SetValue(__instance, false);
                    return;
                }
                var shipItem = _shipItem.GetValue(__instance) as ShipItem;
                if (shipItem != null && items.IsRemoteHeld(shipItem))
                {
                    // locked=false lets AutoLevel level the tabletop; locking=true suppresses
                    // starting a duplicate LockRoutine() coroutine on every frame.
                    _locked.SetValue(__instance, false);
                    _locking.SetValue(__instance, true);
                    _remoteUnlocked.Add(__instance);
                }
                else if (_remoteUnlocked.Remove(__instance))
                {
                    // Released by the remote player: clear locking once so AutoLevel runs one LockRoutine().
                    _locking.SetValue(__instance, false);
                }
            }
            catch { }
        }
    }
}
