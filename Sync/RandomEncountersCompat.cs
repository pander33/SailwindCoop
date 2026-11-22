using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// RandomEncounters (com.raddude.randomencounters) runs independent RNG encounter timers and
    /// restores encounters from GameState.modData on load.
    /// In co-op:
    /// - Guests suppress EncounterGenerator.Generate() and LoadEncounter().
    /// - Standard flotsam items replicate naturally via ItemSync. Asset-bundle wreckage (SmallWreck/Hull)
    ///   is spawned without SaveablePrefab.RegisterToSave and relayed via EncounterStateMsg.
    /// - Host relays DenseFog flags/density, FishingBonanza active state (giving guests both seagulls
    ///   and the catch-rate boost), IntenseStorm local Crest wave/lightning/rain parameters (so Crest
    ///   wave heights match the host), and SeaLifeMod whale spawns.
    /// </summary>
    public static class RandomEncountersCompat
    {
        public const string Guid = "com.raddude.randomencounters";
        private const string AssemblyName = "RandomEncounters";

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        // True during world load (when SaveLoadManager.LoadModData triggers LoadEncounter before PlayingCoop flips true).
        private static bool ClientInSession => SyncEnabled && Net?.Role == Role.Client &&
            (Net.State == LinkState.Connected || Net.State == LinkState.Handshaking || Net.State == LinkState.Connecting);
        private static bool ClientActive => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            !Host && (Net.HostModFeatures & ModFeatures.RandomEncounters) != 0;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.RandomEncounters) != 0);

        private static readonly Crest.SampleHeightHelper _heightHelper = new Crest.SampleHeightHelper();

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _genType;
        private static Type _regType;
        private static Type _fogType;
        private static Type _bonanzaType;
        private static Type _stormType;
        private static Type _flotsamType;
        private static Type _assetType;
        private static Type _seaLifeType;

        private static MethodInfo _mGenerate;
        private static MethodInfo _mLoadEncounter;
        private static MethodInfo _mGetByName;
        private static PropertyInfo _pIsActive;
        private static PropertyInfo _pTimeRemaining;

        // DenseFog
        private static FieldInfo _fClearFog;
        private static FieldInfo _fFogCleared;
        private static FieldInfo _fCurFogDensity;
        private static FieldInfo _fOrigFogDensity;
        private static FieldInfo _fWaveAudioSources;
        private static FieldInfo _fWindAudioSource;
        private static MethodInfo _mFogSpawn;
        private static MethodInfo _mFogClear;

        // FishingBonanza
        private static FieldInfo _fSeagulls;

        // IntenseStorm
        private static FieldInfo _fOceanUpdaterCrest;
        private static FieldInfo _fWindSpeedMult;
        private static FieldInfo _fSmallWavesMult;
        private static FieldInfo _fLightningInterval;
        private static FieldInfo _fCurrentTargetRegion;

        // Flotsam / AssetLoader
        private static MethodInfo _mSpawnItem;
        private static PropertyInfo _pAssetHull;
        private static PropertyInfo _pAssetSmallWreck;
        private static FieldInfo _fAssetHull;
        private static FieldInfo _fAssetSmallWreck;

        // SeaLifeMod
        private static MethodInfo _mSpawnWhale;
        private static MethodInfo _mTriggerEntrance;
        private static FieldInfo _fWhaleSpawns;
        private static PropertyInfo _pActiveWhales;
        private static PropertyInfo _pWhalesReady;

        // Host polling state
        private static float _nextHostPoll;
        private static bool _hostFogActive;
        private static bool _hostFogClearing;
        private static bool _hostBonanzaActive;
        private static byte _hostStormPhase;

        // Client applied state
        private static GameObject _clientSeagulls;
        private static bool _clientBonanzaActive;
        private static bool _savedCrest;
        private static bool _savedLightning;
        private static bool _savedRegion;
        private static byte _clientStormPhase;
        private static float _origInertiaWindScale;
        private static float _origWindSpeedMult;
        private static float _origSmallWavesMult;
        private static float _origLightningInterval;
        private static float _origRainDensity;
        private static WanderingStormLightning _clientStormLightning;
        private static Region _clientStormRegion;
        private static OceanUpdaterCrest _clientCrest;
        private static bool _clientFogMuted;
        private static readonly List<GameObject> _clientWreckage = new List<GameObject>();

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                if (asm == null) return;

                _genType = asm.GetType("RandomEncounters.EncounterGenerator");
                _regType = asm.GetType("RandomEncounters.API.EncounterRegistry");
                var encType = asm.GetType("RandomEncounters.Encounter");
                _fogType = asm.GetType("RandomEncounters.DenseFogEncounter");
                _bonanzaType = asm.GetType("RandomEncounters.FishingBonanzaEncounter");
                _stormType = asm.GetType("RandomEncounters.IntenseStormEncounter");
                _flotsamType = asm.GetType("RandomEncounters.Flotsam");
                _assetType = asm.GetType("RandomEncounters.AssetLoader");
                _seaLifeType = asm.GetType("RandomEncounters.SeaLifeMod");

                if (_genType == null || _regType == null || encType == null) return;

                _mGenerate = AccessTools.Method(_genType, "Generate");
                _mLoadEncounter = AccessTools.Method(_genType, "LoadEncounter");
                _mGetByName = AccessTools.Method(_regType, "GetByName", new[] { typeof(string) });
                _pIsActive = AccessTools.Property(encType, "IsActive");
                _pTimeRemaining = AccessTools.Property(encType, "TimeRemaining");

                if (_fogType != null)
                {
                    _fClearFog = AccessTools.Field(_fogType, "_clearFog");
                    _fFogCleared = AccessTools.Field(_fogType, "_fogCleared");
                    _fCurFogDensity = AccessTools.Field(_fogType, "_currentFogDensity");
                    _fOrigFogDensity = AccessTools.Field(_fogType, "_originalFogDensity");
                    _fWaveAudioSources = AccessTools.Field(_fogType, "_waveAudioSources");
                    _fWindAudioSource = AccessTools.Field(_fogType, "_windAudioSource");
                    _mFogSpawn = AccessTools.Method(_fogType, "Spawn", Type.EmptyTypes);
                    _mFogClear = AccessTools.Method(_fogType, "ClearFog", Type.EmptyTypes);
                }
                if (_bonanzaType != null) _fSeagulls = AccessTools.Field(_bonanzaType, "_seagulls");
                if (_stormType != null) _fOceanUpdaterCrest = AccessTools.Field(_stormType, "_oceanUpdaterCrest");
                _fWindSpeedMult = AccessTools.Field(typeof(OceanUpdaterCrest), "windSpeedMult");
                _fSmallWavesMult = AccessTools.Field(typeof(OceanUpdaterCrest), "smallWavesMult");
                _fLightningInterval = AccessTools.Field(typeof(WanderingStormLightning), "lightningInterval");
                _fCurrentTargetRegion = AccessTools.Field(typeof(RegionBlender), "currentTargetRegion");

                if (_flotsamType != null)
                    _mSpawnItem = AccessTools.Method(_flotsamType, "SpawnItem",
                        new[] { typeof(Vector3), typeof(GameObject), typeof(float), typeof(bool) });
                if (_assetType != null)
                {
                    _pAssetHull = AccessTools.Property(_assetType, "Hull");
                    _pAssetSmallWreck = AccessTools.Property(_assetType, "SmallWreck");
                    _fAssetHull = AccessTools.Field(_assetType, "Hull");
                    _fAssetSmallWreck = AccessTools.Field(_assetType, "SmallWreck");
                }
                if (_seaLifeType != null)
                {
                    _mSpawnWhale = AccessTools.Method(_seaLifeType, "SpawnWhale", new[] { typeof(int), typeof(Vector3) });
                    _mTriggerEntrance = AccessTools.Method(_seaLifeType, "TriggerEntranceAnimation", Type.EmptyTypes);
                    _fWhaleSpawns = AccessTools.Field(_seaLifeType, "_whaleSpawns");
                    _pActiveWhales = AccessTools.Property(_seaLifeType, "ActiveWhales");
                    _pWhalesReady = AccessTools.Property(_seaLifeType, "WhalesReady");
                }

                SyncEnabled = _mGenerate != null && _mLoadEncounter != null && _mGetByName != null && _pIsActive != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[RandomEncountersCompat] RandomEncounters detected; host-authoritative sync enabled.");
                else
                    Plugin.Logger.LogWarning("[RandomEncountersCompat] RandomEncounters detected, but required members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[RandomEncountersCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                harmony.Patch(_mGenerate, prefix: new HarmonyMethod(typeof(RandomEncountersCompat), nameof(PreClientSkip)));
                harmony.Patch(_mLoadEncounter, prefix: new HarmonyMethod(typeof(RandomEncountersCompat), nameof(PreClientLoadEncounter)));
                if (_mSpawnItem != null)
                    harmony.Patch(_mSpawnItem, prefix: new HarmonyMethod(typeof(RandomEncountersCompat), nameof(PreFlotsamSpawnItem)));
                if (_mSpawnWhale != null)
                    harmony.Patch(_mSpawnWhale, postfix: new HarmonyMethod(typeof(RandomEncountersCompat), nameof(PostSpawnWhale)));
                if (_mTriggerEntrance != null)
                    harmony.Patch(_mTriggerEntrance, postfix: new HarmonyMethod(typeof(RandomEncountersCompat), nameof(PostTriggerEntrance)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[RandomEncountersCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _nextHostPoll = 0f;
            _hostFogActive = false;
            _hostFogClearing = false;
            _hostBonanzaActive = false;
            _hostStormPhase = 0;

            try
            {
                if (_clientFogMuted || (_fFogCleared != null && !(bool)_fFogCleared.GetValue(null)))
                    ApplyClientFog(active: false, clearing: true, density: 0f);
                if (_clientBonanzaActive || _clientSeagulls != null)
                    ApplyClientBonanza(active: false, remainingSec: 0f);
                if (_savedCrest || _savedLightning || _savedRegion || _clientStormPhase != 0)
                    ApplyClientStorm(phase: 0);
                for (int i = 0; i < _clientWreckage.Count; i++)
                    if (_clientWreckage[i] != null) UnityEngine.Object.Destroy(_clientWreckage[i]);
            }
            catch { }
            _clientWreckage.Clear();
            _clientFogMuted = false;
        }

        private static bool PreClientSkip() => !ClientInSession;
        private static bool PreClientLoadEncounter() => !ClientInSession;
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static GameObject AssetHull() =>
            (_pAssetHull != null ? _pAssetHull.GetValue(null, null) : _fAssetHull?.GetValue(null)) as GameObject;
        private static GameObject AssetSmallWreck() =>
            (_pAssetSmallWreck != null ? _pAssetSmallWreck.GetValue(null, null) : _fAssetSmallWreck?.GetValue(null)) as GameObject;

        // Intercept asset-bundle wreckage on host so it is NOT registered as a SaveablePrefab
        // (which is absent from PrefabsDirectory and would confuse ItemSync) and relay it explicitly.
        private static bool PreFlotsamSpawnItem(Vector3 __0, GameObject __1, float __2, bool __3)
        {
            if (!__3 || !Active || !Host || __1 == null) return true;
            try
            {
                Vector3 pos = __0;
                pos.y = OceanHeight.GetHeight(_heightHelper, pos);
                Quaternion rot = Quaternion.Euler(
                    UnityEngine.Random.Range(0, 360),
                    UnityEngine.Random.Range(0, 360),
                    UnityEngine.Random.Range(0, 360));
                GameObject spawned = UnityEngine.Object.Instantiate(__1, pos, rot);
                var shipItem = spawned.GetComponent<ShipItem>();
                if (shipItem != null)
                {
                    shipItem.sold = true;
                    shipItem.amount = __2;
                    shipItem.health = __2;
                    shipItem.unclickable = true;
                }
                if (Refs.shiftingWorld != null) spawned.transform.parent = Refs.shiftingWorld;

                Vector3 real = CoordSpace.LocalToReal(pos);
                bool isHull = ReferenceEquals(__1, AssetHull());
                Net.SendMod(new EncounterStateMsg
                {
                    Kind = EncounterSyncKind.WreckageSpawn,
                    Flag = isHull,
                    X = real.x, Y = real.y, Z = real.z,
                    Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w,
                }, ModFeatures.RandomEncounters, DeliveryMethod.ReliableOrdered);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RandomEncountersCompat] PreFlotsamSpawnItem: " + e.Message);
                return true;
            }
        }

        private static void PostSpawnWhale(int __0)
        {
            try
            {
                if (!Active || !Host || _fWhaleSpawns == null) return;
                var list = _fWhaleSpawns.GetValue(null) as IList;
                if (list == null || __0 < 0 || __0 >= list.Count) return;
                var go = list[__0] as GameObject;
                if (go == null) return;
                Vector3 real = CoordSpace.LocalToReal(go.transform.position);
                Net.SendMod(new EncounterStateMsg
                {
                    Kind = EncounterSyncKind.WhaleSpawn,
                    Index = (byte)__0,
                    Flag = false,
                    FloatArg = go.transform.localScale.x,
                    X = real.x, Y = real.y, Z = real.z,
                    Ry = go.transform.eulerAngles.y,
                }, ModFeatures.RandomEncounters, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RandomEncountersCompat] PostSpawnWhale: " + e.Message);
            }
        }

        private static void PostTriggerEntrance()
        {
            try
            {
                if (!Active || !Host) return;
                Net.SendMod(new EncounterStateMsg
                {
                    Kind = EncounterSyncKind.WhaleSpawn,
                    Index = 255,
                    Flag = true,
                }, ModFeatures.RandomEncounters, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RandomEncountersCompat] PostTriggerEntrance: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!Active || !GameState.playing || GameState.currentlyLoading) return;
            if (Host) TickHost();
            else TickClient();
        }

        private static void TickHost()
        {
            if (Time.unscaledTime < _nextHostPoll) return;
            _nextHostPoll = Time.unscaledTime + 0.25f;
            try
            {
                // 1. Dense Fog
                bool fogCleared = _fFogCleared == null || (bool)_fFogCleared.GetValue(null);
                bool fogClearing = _fClearFog != null && (bool)_fClearFog.GetValue(null);
                bool fogActive = !fogCleared;
                if (fogActive != _hostFogActive || (fogActive && fogClearing != _hostFogClearing))
                {
                    _hostFogActive = fogActive;
                    _hostFogClearing = fogClearing;
                    float density = _fCurFogDensity != null ? (float)_fCurFogDensity.GetValue(null) : 0f;
                    Net.SendMod(new EncounterStateMsg
                    {
                        Kind = EncounterSyncKind.DenseFog,
                        Flag = fogActive,
                        Flag2 = fogClearing,
                        FloatArg = density,
                    }, ModFeatures.RandomEncounters, DeliveryMethod.ReliableOrdered);
                }

                // 2. Fishing Bonanza
                var bonanza = GetEncounter("Fishing Bonanza");
                bool bonanzaActive = bonanza != null && (bool)_pIsActive.GetValue(bonanza, null);
                if (bonanzaActive != _hostBonanzaActive)
                {
                    _hostBonanzaActive = bonanzaActive;
                    float rem = bonanza != null && _pTimeRemaining != null ? (float)_pTimeRemaining.GetValue(bonanza, null) : 0f;
                    Net.SendMod(new EncounterStateMsg
                    {
                        Kind = EncounterSyncKind.FishingBonanza,
                        Flag = bonanzaActive,
                        FloatArg = rem,
                    }, ModFeatures.RandomEncounters, DeliveryMethod.ReliableOrdered);
                }

                // 3. Intense Storm
                byte stormPhase = ReadHostStormPhase();
                if (stormPhase != _hostStormPhase)
                {
                    _hostStormPhase = stormPhase;
                    Net.SendMod(new EncounterStateMsg
                    {
                        Kind = EncounterSyncKind.IntenseStorm,
                        Index = stormPhase,
                    }, ModFeatures.RandomEncounters, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RandomEncountersCompat] TickHost: " + e.Message);
            }
        }

        private static byte ReadHostStormPhase()
        {
            var storm = GetEncounter("Intense Storm");
            if (storm == null || !(bool)_pIsActive.GetValue(storm, null)) return 0;
            var crest = _fOceanUpdaterCrest?.GetValue(null) as OceanUpdaterCrest;
            if (crest != null && Mathf.Abs(crest.inertiaWindScale - 0.22f) < 0.01f) return 2;
            return 1;
        }

        private static void TickClient()
        {
            try
            {
                if (_clientBonanzaActive && _clientSeagulls != null && GameState.currentBoat != null)
                {
                    Vector3 target = GameState.currentBoat.position + GameState.currentBoat.up * 40f;
                    _clientSeagulls.transform.position = Vector3.Lerp(_clientSeagulls.transform.position, target, 0.2f * Time.deltaTime);
                    Vector3 euler = _clientSeagulls.transform.eulerAngles;
                    euler.y = Mathf.LerpAngle(euler.y, GameState.currentBoat.eulerAngles.y - 90f, 0.2f * Time.deltaTime);
                    _clientSeagulls.transform.rotation = Quaternion.Euler(euler);
                }
            }
            catch { }
        }

        public static void OnRemote(EncounterStateMsg msg)
        {
            if (!ClientActive || msg == null) return;
            try
            {
                switch (msg.Kind)
                {
                    case EncounterSyncKind.ClearAll:
                        ApplyClientFog(false, true, 0f);
                        ApplyClientBonanza(false, 0f);
                        ApplyClientStorm(0);
                        break;
                    case EncounterSyncKind.DenseFog:
                        ApplyClientFog(msg.Flag, msg.Flag2, msg.FloatArg);
                        break;
                    case EncounterSyncKind.FishingBonanza:
                        ApplyClientBonanza(msg.Flag, msg.FloatArg);
                        break;
                    case EncounterSyncKind.IntenseStorm:
                        ApplyClientStorm(msg.Index);
                        break;
                    case EncounterSyncKind.WreckageSpawn:
                        ApplyClientWreckage(msg);
                        break;
                    case EncounterSyncKind.WhaleSpawn:
                        ApplyClientWhale(msg);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RandomEncountersCompat] OnRemote: " + e.Message);
            }
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !SyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.RandomEncounters) == 0) return;
            try
            {
                peer.Send(new EncounterStateMsg { Kind = EncounterSyncKind.ClearAll }, DeliveryMethod.ReliableOrdered);

                bool fogCleared = _fFogCleared == null || (bool)_fFogCleared.GetValue(null);
                if (!fogCleared)
                {
                    peer.Send(new EncounterStateMsg
                    {
                        Kind = EncounterSyncKind.DenseFog,
                        Flag = true,
                        Flag2 = _fClearFog != null && (bool)_fClearFog.GetValue(null),
                        FloatArg = _fCurFogDensity != null ? (float)_fCurFogDensity.GetValue(null) : 0f,
                    }, DeliveryMethod.ReliableOrdered);
                }

                var bonanza = GetEncounter("Fishing Bonanza");
                if (bonanza != null && (bool)_pIsActive.GetValue(bonanza, null))
                {
                    float rem = _pTimeRemaining != null ? (float)_pTimeRemaining.GetValue(bonanza, null) : 300f;
                    peer.Send(new EncounterStateMsg
                    {
                        Kind = EncounterSyncKind.FishingBonanza,
                        Flag = true,
                        FloatArg = rem,
                    }, DeliveryMethod.ReliableOrdered);
                }

                byte stormPhase = ReadHostStormPhase();
                if (stormPhase != 0)
                {
                    peer.Send(new EncounterStateMsg
                    {
                        Kind = EncounterSyncKind.IntenseStorm,
                        Index = stormPhase,
                    }, DeliveryMethod.ReliableOrdered);
                }

                if (_fWhaleSpawns != null)
                {
                    var list = _fWhaleSpawns.GetValue(null) as IList;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count && i < 255; i++)
                        {
                            var go = list[i] as GameObject;
                            if (go == null || !go.activeInHierarchy) continue;
                            Vector3 real = CoordSpace.LocalToReal(go.transform.position);
                            peer.Send(new EncounterStateMsg
                            {
                                Kind = EncounterSyncKind.WhaleSpawn,
                                Index = (byte)i,
                                Flag = false,
                                FloatArg = go.transform.localScale.x,
                                X = real.x, Y = real.y, Z = real.z,
                                Ry = go.transform.eulerAngles.y,
                            }, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[RandomEncountersCompat] ResyncTo: " + e.Message);
            }
        }

        private static object GetEncounter(string name)
        {
            if (_mGetByName == null) return null;
            return _mGetByName.Invoke(null, new object[] { name });
        }

        private static void ApplyClientFog(bool active, bool clearing, float density)
        {
            if (_fFogCleared == null || _fClearFog == null) return;
            float clampedDensity = IsFinite(density) ? Mathf.Clamp(density, 0f, 0.06f) : 0f;
            if (active && !clearing)
            {
                // Avoid calling Spawn() while audio is already muted: Spawn() snapshots current
                // AudioSource volumes as the originals, which would permanently overwrite them with 0.
                if (!_clientFogMuted)
                    _mFogSpawn?.Invoke(null, null);
                else
                {
                    _fFogCleared.SetValue(null, false);
                    _fClearFog.SetValue(null, false);
                }
                if (clampedDensity > 0f && _fCurFogDensity != null) _fCurFogDensity.SetValue(null, clampedDensity);
                SetWaveVolumes(mute: true);
                _clientFogMuted = true;
            }
            else if (active && clearing)
            {
                bool alreadyCleared = (bool)_fFogCleared.GetValue(null);
                if (alreadyCleared && clampedDensity > 0f)
                {
                    // Late join during fade-out: seed fog density so the client observes it clearing.
                    _fFogCleared.SetValue(null, false);
                    _fClearFog.SetValue(null, true);
                    _fCurFogDensity?.SetValue(null, clampedDensity);
                }
                else
                {
                    _mFogClear?.Invoke(null, null);
                }
                SetWaveVolumes(mute: false);
                _clientFogMuted = false;
            }
            else
            {
                _fFogCleared.SetValue(null, true);
                _fClearFog.SetValue(null, false);
                _fCurFogDensity?.SetValue(null, 0f);
                _fOrigFogDensity?.SetValue(null, 0f);
                SetWaveVolumes(mute: false);
                _clientFogMuted = false;
            }
        }

        private static void SetWaveVolumes(bool mute)
        {
            if (_fWaveAudioSources != null)
            {
                var dict = _fWaveAudioSources.GetValue(null) as IDictionary;
                if (dict != null)
                {
                    foreach (DictionaryEntry e in dict)
                    {
                        var src = e.Key as AudioSource;
                        if (src == null) continue;
                        src.volume = mute ? 0f : (float)e.Value;
                    }
                }
            }
            if (_fWindAudioSource != null)
            {
                object tuple = _fWindAudioSource.GetValue(null);
                if (tuple != null)
                {
                    var t = tuple.GetType();
                    var src = t.GetField("Item1")?.GetValue(tuple) as AudioSource;
                    var orig = t.GetField("Item2")?.GetValue(tuple);
                    if (src != null && orig is float origVol)
                        src.volume = mute ? 0.0001f : origVol;
                }
            }
        }

        private static void ApplyClientBonanza(bool active, float remainingSec)
        {
            float safeRemaining = IsFinite(remainingSec) && remainingSec > 0f ? remainingSec : 0f;
            var bonanza = GetEncounter("Fishing Bonanza");
            if (bonanza != null)
            {
                _pIsActive.SetValue(bonanza, active, null);
                if (_pTimeRemaining != null) _pTimeRemaining.SetValue(bonanza, active ? Math.Max(1f, safeRemaining) : 0f, null);
            }
            _clientBonanzaActive = active;
            if (!active)
            {
                if (_clientSeagulls != null)
                {
                    UnityEngine.Object.Destroy(_clientSeagulls);
                    _clientSeagulls = null;
                }
                return;
            }
            if (_clientSeagulls != null || Refs.shiftingWorld == null) return;
            var template = _fSeagulls?.GetValue(null) as Seagulls;
            if (template == null) template = Refs.shiftingWorld.GetComponentInChildren<Seagulls>(true);
            if (template == null) return;
            _fSeagulls?.SetValue(null, template);
            _clientSeagulls = UnityEngine.Object.Instantiate(template.gameObject, Refs.shiftingWorld);
            _clientSeagulls.SetActive(true);
            if (GameState.currentBoat != null)
                _clientSeagulls.transform.position = GameState.currentBoat.position + GameState.currentBoat.up * 40f;
            var audio = _clientSeagulls.GetComponent<AudioSource>();
            if (audio != null && audio.clip != null) audio.PlayOneShot(audio.clip);
            var ps = _clientSeagulls.GetComponent<ParticleSystem>();
            if (ps != null)
            {
                var main = ps.main;
                main.maxParticles = 25;
                main.startLifetime = Mathf.Max(5f, safeRemaining > 0f ? safeRemaining : 300f);
                var em = ps.emission;
                em.enabled = true;
            }
        }

        private static void ApplyClientStorm(byte phase)
        {
            _clientStormPhase = phase;
            if (phase == 0)
            {
                if (_savedLightning && _clientStormLightning != null && _fLightningInterval != null)
                    _fLightningInterval.SetValue(_clientStormLightning, _origLightningInterval);
                if (_savedRegion && _clientStormRegion != null && _clientStormRegion.stormWeather != null && _clientStormRegion.stormWeather.particles != null)
                    _clientStormRegion.stormWeather.particles.rainDensity = _origRainDensity;
                if (_savedCrest && _clientCrest != null)
                {
                    _clientCrest.inertiaWindScale = _origInertiaWindScale;
                    _fWindSpeedMult?.SetValue(_clientCrest, _origWindSpeedMult);
                    _fSmallWavesMult?.SetValue(_clientCrest, _origSmallWavesMult);
                }
                _savedCrest = false;
                _savedLightning = false;
                _savedRegion = false;
                _clientStormLightning = null;
                _clientStormRegion = null;
                _clientCrest = null;
                return;
            }

            var crest = (_fOceanUpdaterCrest?.GetValue(null) as OceanUpdaterCrest) ?? UnityEngine.Object.FindObjectOfType<OceanUpdaterCrest>();
            var ws = WeatherStorms.instance;
            var storm = ws != null ? ws.GetCurrentStorm() : null;
            var lightning = storm != null && storm.transform.childCount > 3
                ? storm.transform.GetChild(3).GetComponent<WanderingStormLightning>() : null;
            var region = RegionBlender.instance != null && _fCurrentTargetRegion != null
                ? _fCurrentTargetRegion.GetValue(RegionBlender.instance) as Region : null;

            if (!_savedCrest && crest != null)
            {
                _clientCrest = crest;
                _origInertiaWindScale = crest.inertiaWindScale;
                _origWindSpeedMult = _fWindSpeedMult != null ? (float)_fWindSpeedMult.GetValue(crest) : 1f;
                _origSmallWavesMult = _fSmallWavesMult != null ? (float)_fSmallWavesMult.GetValue(crest) : 1f;
                _savedCrest = true;
            }
            if (!_savedLightning && lightning != null && _fLightningInterval != null)
            {
                _clientStormLightning = lightning;
                _origLightningInterval = (float)_fLightningInterval.GetValue(lightning);
                _savedLightning = true;
            }
            if (!_savedRegion && region != null && region.stormWeather != null && region.stormWeather.particles != null)
            {
                _clientStormRegion = region;
                _origRainDensity = region.stormWeather.particles.rainDensity;
                _savedRegion = true;
            }

            if (lightning != null && _fLightningInterval != null)
                _fLightningInterval.SetValue(lightning, 5f);
            if (region != null && region.stormWeather != null && region.stormWeather.particles != null)
                region.stormWeather.particles.rainDensity = 70f;
            if (phase >= 2 && crest != null)
            {
                crest.inertiaWindScale = 0.22f;
                _fWindSpeedMult?.SetValue(crest, 5f);
                _fSmallWavesMult?.SetValue(crest, 0.4f);
            }
        }

        private static void ApplyClientWreckage(EncounterStateMsg msg)
        {
            if (!IsFinite(msg.X) || !IsFinite(msg.Y) || !IsFinite(msg.Z) ||
                !IsFinite(msg.Rx) || !IsFinite(msg.Ry) || !IsFinite(msg.Rz) || !IsFinite(msg.Rw)) return;
            var prefab = msg.Flag ? AssetHull() : AssetSmallWreck();
            if (prefab == null) return;
            Vector3 pos = CoordSpace.RealToLocal(new Vector3(msg.X, msg.Y, msg.Z));
            Quaternion rot = new Quaternion(msg.Rx, msg.Ry, msg.Rz, msg.Rw);
            var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            var item = go.GetComponent<ShipItem>();
            if (item != null)
            {
                item.sold = true;
                item.unclickable = true;
            }
            if (Refs.shiftingWorld != null) go.transform.parent = Refs.shiftingWorld;
            _clientWreckage.Add(go);
            if (_clientWreckage.Count > 32)
            {
                if (_clientWreckage[0] != null) UnityEngine.Object.Destroy(_clientWreckage[0]);
                _clientWreckage.RemoveAt(0);
            }
        }

        private static void ApplyClientWhale(EncounterStateMsg msg)
        {
            if (_pWhalesReady == null || !(bool)_pWhalesReady.GetValue(null, null)) return;
            if (msg.Flag && msg.Index == 255)
            {
                _mTriggerEntrance?.Invoke(null, null);
                return;
            }
            var list = _fWhaleSpawns?.GetValue(null) as IList;
            if (list == null || msg.Index >= list.Count) return;
            var go = list[msg.Index] as GameObject;
            if (go == null) return;
            if (!IsFinite(msg.X) || !IsFinite(msg.Y) || !IsFinite(msg.Z) || !IsFinite(msg.Ry) || !IsFinite(msg.FloatArg)) return;
            bool wasActive = go.activeSelf;
            go.transform.position = CoordSpace.RealToLocal(new Vector3(msg.X, msg.Y, msg.Z));
            go.transform.rotation = Quaternion.Euler(0f, msg.Ry, 0f);
            float scale = msg.FloatArg > 0.1f ? msg.FloatArg : 1f;
            go.transform.localScale = new Vector3(scale, scale, scale);
            go.SetActive(true);
            if (!wasActive && _pActiveWhales != null)
            {
                int count = (int)_pActiveWhales.GetValue(null, null);
                _pActiveWhales.SetValue(null, count + 1, null);
            }
        }
    }
}
