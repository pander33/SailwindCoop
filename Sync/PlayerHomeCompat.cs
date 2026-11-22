using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// PlayerHome (com.yourname.playerhome) stores owned homes/taverns and warehouse item blobs in a
    /// sidecar file via SailwindModdingHelper.ModSave (slotN/com.yourname.playerhome.save).
    /// In co-op:
    /// - Host owns the sidecar file and item capture/restore (restored ShipItems replicate via ItemSync).
    /// - Guests suppress local ModSave load/save and local item capture/restore so stale slot5 data
    ///   never destroys unclaimed taverns or spawns duplicate unsynced items.
    /// - Ownership (baseline + live HomeBuyButton claims) syncs via PlayerHomeStateMsg.
    /// </summary>
    public static class PlayerHomeCompat
    {
        public const string Guid = "com.yourname.playerhome";
        private const string AssemblyName = "PlayerHome";

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool ClientActive => SyncEnabled && Net?.Role == Role.Client &&
            (Net.State == LinkState.Connected || Net.State == LinkState.Handshaking || Net.State == LinkState.Connecting);
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.PlayerHome) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _pluginType;
        private static FieldInfo _fInstance;
        private static FieldInfo _fHomes;
        private static FieldInfo _fSave;
        private static FieldInfo _fWorldReady;
        private static FieldInfo _fOwnedIds;
        private static FieldInfo _fHomeDef;
        private static FieldInfo _fHomeOwned;
        private static FieldInfo _fHomeIsClaimedTavern;
        private static FieldInfo _fHomeBuyButton;
        private static FieldInfo _fHomeRestButton;
        private static FieldInfo _fDefId;

        private static MethodInfo _mTryBuy;
        private static MethodInfo _mDisableTavernBusiness;
        private static MethodInfo _mRestoreItemsFor;
        private static MethodInfo _mCaptureItemsFor;
        private static MethodInfo _mUpdateHomePresence;
        private static MethodInfo _mOnGameSave;
        private static MethodInfo _mModSaveLoad;
        private static MethodInfo _mModSaveSave;

        private static readonly HashSet<string> _targetOwnedIds = new HashSet<string>(StringComparer.Ordinal);
        private static bool _hasBaseline;
        private static bool _pendingApply;
        private static bool _applying;
        private static float _nextRetryAt;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _pluginType = null;
            _fInstance = _fHomes = _fSave = _fWorldReady = _fOwnedIds = null;
            _fHomeDef = _fHomeOwned = _fHomeIsClaimedTavern = _fHomeBuyButton = _fHomeRestButton = _fDefId = null;
            _mTryBuy = _mDisableTavernBusiness = _mRestoreItemsFor = _mCaptureItemsFor = null;
            _mUpdateHomePresence = _mOnGameSave = _mModSaveLoad = _mModSaveSave = null;

            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                if (asm == null) return;

                _pluginType = asm.GetType("PlayerHome.PlayerHomePlugin");
                var homeType = asm.GetType("PlayerHome.Home");
                var defType = asm.GetType("PlayerHome.HomeDef");
                var saveType = asm.GetType("PlayerHome.SaveData");
                if (_pluginType == null || homeType == null || defType == null || saveType == null) return;

                _fInstance = AccessTools.Field(_pluginType, "I");
                _fHomes = AccessTools.Field(_pluginType, "_homes");
                _fSave = AccessTools.Field(_pluginType, "_save");
                _fWorldReady = AccessTools.Field(_pluginType, "_worldReady");
                _fOwnedIds = AccessTools.Field(saveType, "ownedIds");
                _fHomeDef = AccessTools.Field(homeType, "def");
                _fHomeOwned = AccessTools.Field(homeType, "owned");
                _fHomeIsClaimedTavern = AccessTools.Field(homeType, "isClaimedTavern");
                _fHomeBuyButton = AccessTools.Field(homeType, "buyButton");
                _fHomeRestButton = AccessTools.Field(homeType, "restButton");
                _fDefId = AccessTools.Field(defType, "id");

                _mTryBuy = AccessTools.Method(_pluginType, "TryBuy", new[] { homeType });
                _mDisableTavernBusiness = AccessTools.Method(_pluginType, "DisableTavernBusiness", new[] { homeType });
                _mRestoreItemsFor = AccessTools.Method(_pluginType, "RestoreItemsFor", new[] { homeType });
                _mCaptureItemsFor = AccessTools.Method(_pluginType, "CaptureItemsFor", new[] { homeType });
                _mUpdateHomePresence = AccessTools.Method(_pluginType, "UpdateHomePresence", Type.EmptyTypes);
                _mOnGameSave = AccessTools.Method(_pluginType, "OnGameSave", Type.EmptyTypes);

                var modSaveType = AccessTools.TypeByName("SailwindModdingHelper.ModSave");
                if (modSaveType != null)
                {
                    var objRef = typeof(object).MakeByRefType();
                    var methods = modSaveType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    _mModSaveLoad = methods.FirstOrDefault(m =>
                    {
                        if (m.Name != "Load" || m.IsGenericMethod) return false;
                        var ps = m.GetParameters();
                        return ps.Length == 2 && ps[0].ParameterType == typeof(PluginInfo) && ps[1].ParameterType == objRef;
                    });
                    _mModSaveSave = methods.FirstOrDefault(m =>
                    {
                        if (m.Name != "Save" || m.IsGenericMethod) return false;
                        var ps = m.GetParameters();
                        return ps.Length == 2 && ps[0].ParameterType == typeof(PluginInfo) && ps[1].ParameterType == typeof(object);
                    });
                }

                SyncEnabled = _fInstance != null && _fHomes != null && _fSave != null && _fOwnedIds != null &&
                              _fHomeDef != null && _fHomeOwned != null && _fDefId != null && _mTryBuy != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[PlayerHomeCompat] PlayerHome detected; ownership sync enabled.");
                else
                    Plugin.Logger.LogWarning("[PlayerHomeCompat] PlayerHome detected, but required members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[PlayerHomeCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                harmony.Patch(_mTryBuy,
                    prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreTryBuy)),
                    postfix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PostTryBuy)));
                if (_mRestoreItemsFor != null)
                    harmony.Patch(_mRestoreItemsFor,
                        prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreGuestZeroInt)));
                if (_mCaptureItemsFor != null)
                    harmony.Patch(_mCaptureItemsFor,
                        prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreGuestZeroInt)));
                if (_mUpdateHomePresence != null)
                    harmony.Patch(_mUpdateHomePresence,
                        prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreGuestSkip)));
                if (_mOnGameSave != null)
                    harmony.Patch(_mOnGameSave,
                        prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreGuestSkip)));
                if (_mModSaveLoad != null)
                    harmony.Patch(_mModSaveLoad,
                        prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreModSaveLoad)));
                if (_mModSaveSave != null)
                    harmony.Patch(_mModSaveSave,
                        prefix: new HarmonyMethod(typeof(PlayerHomeCompat), nameof(PreModSaveSave)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[PlayerHomeCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _targetOwnedIds.Clear();
            _hasBaseline = false;
            _pendingApply = false;
            _applying = false;
            _nextRetryAt = 0f;
        }

        private static bool PreModSaveLoad(PluginInfo __0, ref object __1, ref bool __result)
        {
            try
            {
                if (ClientActive && __0?.Metadata != null &&
                    string.Equals(__0.Metadata.GUID, Guid, StringComparison.OrdinalIgnoreCase))
                {
                    __1 = null;
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static bool PreModSaveSave(PluginInfo __0)
        {
            try
            {
                if (ClientActive && __0?.Metadata != null &&
                    string.Equals(__0.Metadata.GUID, Guid, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { }
            return true;
        }

        private static bool PreGuestZeroInt(ref int __result)
        {
            if (!ClientActive) return true;
            __result = 0;
            return false;
        }

        private static bool PreGuestSkip() => !ClientActive;

        private static void PreTryBuy(object __0, out bool __state)
        {
            __state = false;
            try
            {
                if (__0 != null && _fHomeOwned != null)
                    __state = (bool)_fHomeOwned.GetValue(__0);
            }
            catch { __state = false; }
        }

        private static void PostTryBuy(object __0, bool __state)
        {
            try
            {
                if (_applying || !Active || __0 == null || __state) return;
                if (!(bool)_fHomeOwned.GetValue(__0)) return;
                string id = IdOf(__0);
                if (string.IsNullOrEmpty(id)) return;
                Net.SendMod(new PlayerHomeStateMsg
                {
                    ReplaceAll = false,
                    OwnedIds = new[] { id },
                }, ModFeatures.PlayerHome, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerHomeCompat] PostTryBuy: " + e.Message);
            }
        }

        public static void OnRemote(PlayerHomeStateMsg msg, NetPeer fromPeer)
        {
            if (!Active || msg == null) return;
            try
            {
                if (Host)
                {
                    if (msg.OwnedIds == null || msg.OwnedIds.Length == 0) return;
                    var applied = new List<string>(msg.OwnedIds.Length);
                    foreach (string id in msg.OwnedIds)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        if (ApplyClaimLocally(id)) applied.Add(id);
                    }
                    if (applied.Count > 0)
                    {
                        SaveHostData();
                        Net.SendMod(new PlayerHomeStateMsg
                        {
                            ReplaceAll = false,
                            OwnedIds = applied.ToArray(),
                        }, ModFeatures.PlayerHome, DeliveryMethod.ReliableOrdered, except: fromPeer);
                    }
                    return;
                }

                if (msg.ReplaceAll)
                {
                    _targetOwnedIds.Clear();
                    _hasBaseline = true;
                }
                if (msg.OwnedIds != null)
                {
                    for (int i = 0; i < msg.OwnedIds.Length; i++)
                        if (!string.IsNullOrEmpty(msg.OwnedIds[i])) _targetOwnedIds.Add(msg.OwnedIds[i]);
                }
                _pendingApply = true;
                TryApplyTargets();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerHomeCompat] OnRemote: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!Active || Host || !_pendingApply) return;
            if (Time.unscaledTime < _nextRetryAt) return;
            _nextRetryAt = Time.unscaledTime + 0.5f;
            TryApplyTargets();
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !SyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.PlayerHome) == 0) return;
            try
            {
                var ids = ReadOwnedIds();
                peer.Send(new PlayerHomeStateMsg
                {
                    ReplaceAll = true,
                    OwnedIds = ids,
                }, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerHomeCompat] ResyncTo: " + e.Message);
            }
        }

        private static string[] ReadOwnedIds()
        {
            var plugin = _fInstance.GetValue(null);
            if (plugin == null) return Array.Empty<string>();
            var save = _fSave.GetValue(plugin);
            if (save == null) return Array.Empty<string>();
            var list = _fOwnedIds.GetValue(save) as List<string>;
            if (list == null || list.Count == 0) return Array.Empty<string>();
            return list.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal)
                       .Take(PlayerHomeStateMsg.MaxIds).ToArray();
        }

        private static void TryApplyTargets()
        {
            try
            {
                var plugin = _fInstance.GetValue(null);
                if (plugin == null) return;
                if (_fWorldReady != null && !(bool)_fWorldReady.GetValue(plugin)) return;

                var save = _fSave.GetValue(plugin);
                var ownedList = save != null ? _fOwnedIds.GetValue(save) as List<string> : null;
                if (ownedList != null && _hasBaseline)
                {
                    ownedList.Clear();
                    foreach (string id in _targetOwnedIds) ownedList.Add(id);
                }

                foreach (string id in _targetOwnedIds)
                    ApplyClaimLocally(id);

                _pendingApply = false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerHomeCompat] TryApplyTargets: " + e.Message);
            }
        }

        private static bool ApplyClaimLocally(string id)
        {
            var plugin = _fInstance.GetValue(null);
            if (plugin == null || string.IsNullOrEmpty(id)) return false;

            bool changed = false;
            var save = _fSave.GetValue(plugin);
            var ownedList = save != null ? _fOwnedIds.GetValue(save) as List<string> : null;
            if (ownedList != null && !ownedList.Contains(id))
            {
                ownedList.Add(id);
                changed = true;
            }

            var homes = _fHomes.GetValue(plugin) as IList;
            if (homes == null) return changed;

            _applying = true;
            try
            {
                for (int i = 0; i < homes.Count; i++)
                {
                    var home = homes[i];
                    if (home == null || !string.Equals(IdOf(home), id, StringComparison.Ordinal)) continue;
                    bool already = (bool)_fHomeOwned.GetValue(home);
                    if (already) continue;

                    _fHomeOwned.SetValue(home, true);
                    changed = true;
                    if (_fHomeIsClaimedTavern != null && (bool)_fHomeIsClaimedTavern.GetValue(home) &&
                        _mDisableTavernBusiness != null)
                        _mDisableTavernBusiness.Invoke(plugin, new[] { home });

                    var buyBtn = _fHomeBuyButton?.GetValue(home) as Component;
                    if (buyBtn != null) buyBtn.gameObject.SetActive(false);

                    var restBtn = _fHomeRestButton?.GetValue(home) as GoPointerButton;
                    if (restBtn != null) restBtn.unclickable = false;
                }
            }
            finally { _applying = false; }
            return changed;
        }

        private static void SaveHostData()
        {
            try
            {
                var plugin = _fInstance.GetValue(null) as BaseUnityPlugin;
                if (plugin == null || _mModSaveSave == null) return;
                var save = _fSave.GetValue(plugin);
                if (save != null) _mModSaveSave.Invoke(null, new object[] { plugin.Info, save });
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerHomeCompat] SaveHostData: " + e.Message);
            }
        }

        private static string IdOf(object home)
        {
            var def = _fHomeDef.GetValue(home);
            return def != null ? _fDefId.GetValue(def) as string : null;
        }
    }
}
