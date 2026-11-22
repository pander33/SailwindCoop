using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// RadRefinements (com.raddude.radrefinements) adds:
    /// - KnifeWood.CutContainer (breaks empty crates/barrels/bottles into firewood prefab 71)
    /// - SwapSlot.SwapItems / SwapSlotToOpenInvSlot / WithdrawFromSwapSlot (bypasses InsertItem/WithdrawItem)
    /// - Continual rowing in ShipItemOar.OnAltHeld (handled via Priority.Last on ItemPatches.PostOarAltHeld).
    /// </summary>
    public static class RadRefinementsCompat
    {
        public const string Guid = "com.raddude.radrefinements";
        private const string AssemblyName = "RadRefinements";

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static MethodInfo _cutContainer;
        private static MethodInfo _swapItems;
        private static MethodInfo _swapToOpen;
        private static MethodInfo _withdrawSwap;
        private static PropertyInfo _swapSlotProp;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _cutContainer = _swapItems = _swapToOpen = _withdrawSwap = null;
            _swapSlotProp = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);

                var knifeType = asm?.GetType("RadRefinements.KnifeWood");
                var swapType = asm?.GetType("RadRefinements.SwapSlot");
                _cutContainer = knifeType != null ? AccessTools.Method(knifeType, "CutContainer", new[] { typeof(ShipItem) }) : null;
                _swapItems = swapType != null ? AccessTools.Method(swapType, "SwapItems", new[] { typeof(PickupableItem), typeof(GPButtonInventorySlot) }) : null;
                _swapToOpen = swapType != null ? AccessTools.Method(swapType, "SwapSlotToOpenInvSlot", Type.EmptyTypes) : null;
                _withdrawSwap = swapType != null ? AccessTools.Method(swapType, "WithdrawFromSwapSlot", Type.EmptyTypes) : null;
                _swapSlotProp = swapType != null ? AccessTools.Property(swapType, "Slot") : null;

                SyncEnabled = _cutContainer != null || _swapItems != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[RadRefinementsCompat] RadRefinements detected; KnifeWood/SwapSlot sync enabled.");
                else
                    Plugin.Logger.LogWarning("[RadRefinementsCompat] RadRefinements detected, but target methods did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[RadRefinementsCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                if (_cutContainer != null)
                    harmony.Patch(_cutContainer,
                        prefix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PreCutContainer)),
                        postfix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PostCutContainer)));
                if (_swapItems != null)
                    harmony.Patch(_swapItems,
                        prefix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PreSwapItems)),
                        postfix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PostSwapItems)));
                if (_swapToOpen != null && _swapSlotProp != null)
                    harmony.Patch(_swapToOpen,
                        prefix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PreSwapSlotItem)),
                        postfix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PostSwapSlotItem)));
                if (_withdrawSwap != null && _swapSlotProp != null)
                    harmony.Patch(_withdrawSwap,
                        prefix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PreSwapSlotItem)),
                        postfix: new HarmonyMethod(typeof(RadRefinementsCompat), nameof(PostSwapSlotItem)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[RadRefinementsCompat] Patching threw: " + e.Message);
            }
        }

        private static bool IsClient =>
            Runtime.CoopBehaviour.Instance?.PlayingCoop == true &&
            Runtime.CoopBehaviour.Instance.Net.Role == Role.Client;

        private static void PreCutContainer(out HashSet<ShipItemStoveFuel> __state)
        {
            __state = null;
            try
            {
                if (!IsClient) return;
                __state = new HashSet<ShipItemStoveFuel>(UnityEngine.Object.FindObjectsOfType<ShipItemStoveFuel>());
            }
            catch { __state = null; }
        }

        private static void PostCutContainer(ShipItem __0, HashSet<ShipItemStoveFuel> __state)
        {
            try
            {
                if (__state == null || !IsClient) return;
                var items = ItemSync.Instance;
                if (items == null) return;
                int spawned = 0;
                foreach (var log in UnityEngine.Object.FindObjectsOfType<ShipItemStoveFuel>())
                {
                    if (log == null || __state.Contains(log)) continue;
                    items.NotifyClientAuthored(log);
                    spawned++;
                }
                if (spawned > 0 && __0 != null)
                    items.NotifyConsume(__0);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RadRefinementsCompat] PostCutContainer: " + e.Message);
            }
        }

        private static void PreSwapItems(GPButtonInventorySlot __1, out ShipItem __state)
        {
            __state = null;
            try
            {
                if (Runtime.CoopBehaviour.Instance?.PlayingCoop != true || __1 == null) return;
                __state = __1.currentItem;
            }
            catch { __state = null; }
        }

        private static void PostSwapItems(GPButtonInventorySlot __1, ShipItem __state)
        {
            try
            {
                if (Runtime.CoopBehaviour.Instance?.PlayingCoop != true || __1 == null) return;
                var inserted = __1.currentItem;
                if (inserted == __state) return;
                var items = ItemSync.Instance;
                if (items == null) return;
                if (inserted != null) items.OnLocalInventory(inserted);
                if (__state != null) items.OnLocalInventory(__state);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RadRefinementsCompat] PostSwapItems: " + e.Message);
            }
        }

        private static void PreSwapSlotItem(out ShipItem __state)
        {
            __state = null;
            try
            {
                if (Runtime.CoopBehaviour.Instance?.PlayingCoop != true || _swapSlotProp == null) return;
                var slot = _swapSlotProp.GetValue(null, null) as GPButtonInventorySlot;
                __state = slot != null ? slot.currentItem : null;
            }
            catch { __state = null; }
        }

        private static void PostSwapSlotItem(ShipItem __state)
        {
            try
            {
                if (Runtime.CoopBehaviour.Instance?.PlayingCoop != true || __state == null) return;
                ItemSync.Instance?.OnLocalInventory(__state);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RadRefinementsCompat] PostSwapSlotItem: " + e.Message);
            }
        }
    }
}
