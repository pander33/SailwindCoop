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
    /// HMS Leopard (com.winter.leopard) adds the HMS Leopard ship (sceneIndex 207) and its cutter (212).
    /// Base hull physics, sails, winches, anchor, mooring and damage sync natively through SailwindCoop.
    /// This adapter synchronizes Leopard's custom controls:
    /// - Ship's bell (LeopardBellInteract)
    /// - Gunport deck groups (Gunports lower/upper/quarter + overflows/water masks/interior triggers)
    /// - Cannons and carriages (push/pull, ready state, single & broadside firing)
    /// - Cutter launch & hoist (CutterController, CutterRopeController, Patches.cutterActive)
    /// - Cutter rowing (OarController input forwarded to host Rigidbody + remote oar visuals)
    /// </summary>
    public static class LeopardCompat
    {
        public const string Guid = "com.winter.leopard";
        private const string AssemblyName = "Leopard";

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool ClientActive => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            !Host && (Net.HostModFeatures & ModFeatures.Leopard) != 0;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.Leopard) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        // Reflected types & members
        private static FieldInfo _fShip;
        private static FieldInfo _fBoat;
        private static FieldInfo _fEmbarkLeopard;
        private static FieldInfo _fCutterActive;

        private static FieldInfo _fRecursive;
        private static FieldInfo _fLowerGunports;
        private static FieldInfo _fUpperGunports;
        private static FieldInfo _fQuarterGunports;
        private static MethodInfo _mToggleOverflows;
        private static MethodInfo _mToggleAudio;

        private static Type _bellType;
        private static Type _cannonType;
        private static Type _carriageType;
        private static Type _cutterCtrlType;
        private static Type _cutterRopeType;
        private static Type _oarType;

        private static MethodInfo _mBellActivate;
        private static MethodInfo _mCannonActivate;
        private static MethodInfo _mCannonUpdate;
        private static MethodInfo _mCannonFire;
        private static MethodInfo _mCannonFireAll;
        private static FieldInfo _fCannonReady;
        private static FieldInfo _fCannonFiring;
        private static FieldInfo _fCannonPort;

        private static MethodInfo _mCarriageActivate;
        private static MethodInfo _mCarriageMove;
        private static MethodInfo _mCarriageMoveAll;
        private static FieldInfo _fCarriageMoving;
        private static FieldInfo _fCarriagePort;
        private static FieldInfo _fCarriageDeck;
        private static FieldInfo _fCarriageCannon;

        private static MethodInfo _mCutterActivate;
        private static MethodInfo _mCutterRopeActivate;

        private static MethodInfo _mOarExtraLateUpdate;
        private static MethodInfo _mOarSetOars;
        private static FieldInfo _fOarRb;
        private static FieldInfo _fOarForce;
        private static FieldInfo _fOarTurn;
        private static FieldInfo _fOarLeft;
        private static FieldInfo _fOarRight;
        private static FieldInfo _fOarTime;
        private static FieldInfo _fOarTimeInc;
        private static FieldInfo _fOarFwdAngle;
        private static FieldInfo _fOarUpAngle;
        private static FieldInfo _fBtnStickyBy;
        private static FieldInfo _fBtnIsClicked;
        private static FieldInfo _fBoatHorizonCooldown;

        // Cached ship objects
        private static GameObject _cachedShip;
        private static Component[] _cannons;
        private static Component[] _carriages;

        // Runtime state
        private static bool _applying;
        private static int _lastFireAllFrame = -1;
        private static byte _lastLocalOarMask;
        private static byte _remoteOarMask;
        private static bool _remoteOarsShown;
        private static byte _hostLocalOarMask;
        private static readonly Dictionary<uint, byte> _hostRemoteOars = new Dictionary<uint, byte>();
        private static byte _hostCombinedOarMask;

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

                var patchesType = asm.GetType("Leopard.Patches");
                var gunportsType = asm.GetType("Leopard.Controllers.Gunports");
                _bellType = asm.GetType("Leopard.Controllers.LeopardBellInteract");
                _cannonType = asm.GetType("Leopard.Controllers.CannonController");
                _carriageType = asm.GetType("Leopard.Controllers.CarriageController");
                _cutterCtrlType = asm.GetType("Leopard.Controllers.CutterController");
                _cutterRopeType = asm.GetType("Leopard.Controllers.CutterRopeController");
                _oarType = asm.GetType("Leopard.Controllers.OarController");

                if (patchesType == null || gunportsType == null || _cannonType == null ||
                    _carriageType == null || _cutterCtrlType == null || _cutterRopeType == null || _oarType == null)
                    return;

                _fShip = AccessTools.Field(patchesType, "ship");
                _fBoat = AccessTools.Field(patchesType, "boat");
                _fEmbarkLeopard = AccessTools.Field(patchesType, "embarkLeopard");
                _fCutterActive = AccessTools.Field(patchesType, "cutterActive");

                _fRecursive = AccessTools.Field(gunportsType, "recursive");
                _fLowerGunports = AccessTools.Field(gunportsType, "lowerGunports");
                _fUpperGunports = AccessTools.Field(gunportsType, "upperGunports");
                _fQuarterGunports = AccessTools.Field(gunportsType, "quarterGunports");
                _mToggleOverflows = AccessTools.Method(gunportsType, "ToggleOverflows", Type.EmptyTypes);
                _mToggleAudio = AccessTools.Method(gunportsType, "ToggleAudio", new[] { typeof(string) });

                _mBellActivate = _bellType != null ? AccessTools.Method(_bellType, "OnActivate", new[] { typeof(GoPointer) }) : null;

                _mCannonActivate = AccessTools.Method(_cannonType, "OnActivate", Type.EmptyTypes);
                _mCannonUpdate = AccessTools.Method(_cannonType, "Update", Type.EmptyTypes);
                _mCannonFire = AccessTools.Method(_cannonType, "Fire", Type.EmptyTypes);
                _mCannonFireAll = AccessTools.Method(_cannonType, "FireAll", Type.EmptyTypes);
                _fCannonReady = AccessTools.Field(_cannonType, "ready");
                _fCannonFiring = AccessTools.Field(_cannonType, "firing");
                _fCannonPort = AccessTools.Field(_cannonType, "port");

                _mCarriageActivate = AccessTools.Method(_carriageType, "OnActivate", Type.EmptyTypes);
                _mCarriageMove = AccessTools.Method(_carriageType, "Move", Type.EmptyTypes);
                _mCarriageMoveAll = AccessTools.Method(_carriageType, "MoveAll", Type.EmptyTypes);
                _fCarriageMoving = AccessTools.Field(_carriageType, "moving");
                _fCarriagePort = AccessTools.Field(_carriageType, "port");
                _fCarriageDeck = AccessTools.Field(_carriageType, "deck");
                _fCarriageCannon = AccessTools.Field(_carriageType, "cannon");

                _mCutterActivate = AccessTools.Method(_cutterCtrlType, "OnActivate", new[] { typeof(GoPointer) });
                _mCutterRopeActivate = AccessTools.Method(_cutterRopeType, "OnActivate", Type.EmptyTypes);

                _mOarExtraLateUpdate = AccessTools.Method(_oarType, "ExtraLateUpdate", Type.EmptyTypes);
                _mOarSetOars = AccessTools.Method(_oarType, "SetOars", new[] { typeof(bool) });
                _fOarRb = AccessTools.Field(_oarType, "rigidbody");
                _fOarForce = AccessTools.Field(_oarType, "forceAmount");
                _fOarTurn = AccessTools.Field(_oarType, "turnForce");
                _fOarLeft = AccessTools.Field(_oarType, "leftOar");
                _fOarRight = AccessTools.Field(_oarType, "rightOar");
                _fOarTime = AccessTools.Field(_oarType, "time");
                _fOarTimeInc = AccessTools.Field(_oarType, "timeIncrease");
                _fOarFwdAngle = AccessTools.Field(_oarType, "forwardAngle");
                _fOarUpAngle = AccessTools.Field(_oarType, "upAngle");
                _fBtnStickyBy = AccessTools.Field(typeof(GoPointerButton), "stickyClickedBy");
                _fBtnIsClicked = AccessTools.Field(typeof(GoPointerButton), "isClicked");
                _fBoatHorizonCooldown = AccessTools.Field(typeof(BoatHorizon), "updateCooldown");

                SyncEnabled = _fShip != null && _fBoat != null && _fCutterActive != null &&
                              _mCannonFire != null && _mCarriageMove != null && _mOarExtraLateUpdate != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[LeopardCompat] HMS Leopard detected; ship & cutter sync enabled.");
                else
                    Plugin.Logger.LogWarning("[LeopardCompat] HMS Leopard detected, but required members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[LeopardCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                if (_mBellActivate != null)
                    harmony.Patch(_mBellActivate, postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostBellActivate)));
                harmony.Patch(AccessTools.Method(typeof(GPButtonTrapdoor), "OnActivate", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreTrapdoorActivate)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostTrapdoorActivate)));

                if (_mCarriageActivate != null)
                    harmony.Patch(_mCarriageActivate, prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCarriageActivate)));
                harmony.Patch(_mCarriageMove,
                    prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCarriageMove)),
                    postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostCarriageMove)));

                if (_mCannonActivate != null)
                    harmony.Patch(_mCannonActivate, prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCannonActivate)));
                if (_mCannonUpdate != null)
                    harmony.Patch(_mCannonUpdate, prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCannonUpdate)));
                harmony.Patch(_mCannonFire,
                    prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCannonFire)),
                    postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostCannonFire)));

                if (_mCutterActivate != null)
                    harmony.Patch(_mCutterActivate,
                        prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCutterLaunch)),
                        postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostCutterToggle)));
                if (_mCutterRopeActivate != null)
                    harmony.Patch(_mCutterRopeActivate,
                        prefix: new HarmonyMethod(typeof(LeopardCompat), nameof(PreCutterHoist)),
                        postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostCutterToggle)));

                harmony.Patch(_mOarExtraLateUpdate,
                    postfix: new HarmonyMethod(typeof(LeopardCompat), nameof(PostOarExtraLateUpdate)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[LeopardCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _applying = false;
            _lastFireAllFrame = -1;
            _lastLocalOarMask = 0;
            _remoteOarMask = 0;
            _remoteOarsShown = false;
            _hostLocalOarMask = 0;
            _hostRemoteOars.Clear();
            _hostCombinedOarMask = 0;
            _cachedShip = null;
            _cannons = null;
            _carriages = null;
        }

        public static void ClearRemoteActor(uint actor)
        {
            if (!Host || !_hostRemoteOars.Remove(actor)) return;
            UpdateHostCombinedOars();
        }

        private static void EnsureCached()
        {
            var ship = _fShip?.GetValue(null) as GameObject;
            if (ship == null || ReferenceEquals(ship, _cachedShip)) return;
            _cachedShip = ship;
            var root = ship.transform.Find("boat leopard/structure_container/CANNONS");
            if (root == null) { _cannons = Array.Empty<Component>(); _carriages = Array.Empty<Component>(); return; }

            var cannons = new List<Component>(60);
            var carriages = new List<Component>(60);
            for (int d = 0; d < root.childCount; d++)
            {
                var deck = root.GetChild(d);
                for (int g = 0; g < deck.childCount; g++)
                {
                    var gun = deck.GetChild(g);
                    var cannonTr = gun.Find("cannon");
                    var cannon = cannonTr != null ? cannonTr.GetComponent(_cannonType) : null;
                    var carriageTr = cannonTr != null ? cannonTr.Find("carriage") : null;
                    var carriage = carriageTr != null ? carriageTr.GetComponent(_carriageType) : null;
                    if (cannon != null && carriage != null)
                    {
                        cannons.Add(cannon);
                        carriages.Add(carriage);
                    }
                }
            }
            _cannons = cannons.ToArray();
            _carriages = carriages.ToArray();
        }

        private static int IndexOfCannon(Component c)
        {
            EnsureCached();
            if (_cannons == null) return -1;
            for (int i = 0; i < _cannons.Length; i++) if (ReferenceEquals(_cannons[i], c)) return i;
            return -1;
        }

        private static int IndexOfCarriage(Component c)
        {
            EnsureCached();
            if (_carriages == null) return -1;
            for (int i = 0; i < _carriages.Length; i++) if (ReferenceEquals(_carriages[i], c)) return i;
            return -1;
        }

        private static int GunportGroupOf(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf("gunport", StringComparison.OrdinalIgnoreCase) < 0) return -1;
            if (name.IndexOf("lower", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (name.IndexOf("upper", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (name.IndexOf("quarter", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return -1;
        }

        private static List<Transform> GunportList(int group)
        {
            if (group == 0) return _fLowerGunports?.GetValue(null) as List<Transform>;
            if (group == 1) return _fUpperGunports?.GetValue(null) as List<Transform>;
            if (group == 2) return _fQuarterGunports?.GetValue(null) as List<Transform>;
            return null;
        }

        private static GPButtonTrapdoor FirstLiveTrapdoor(List<Transform> list)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var tr = list[i];
                if (tr == null) continue;
                var td = tr.GetComponent<GPButtonTrapdoor>();
                if (td != null) return td;
            }
            return null;
        }

        private static void Send(LeopardStateMsg msg, NetPeer except = null)
            => Net.SendMod(msg, ModFeatures.Leopard, DeliveryMethod.ReliableOrdered, except: except);

        // ---- Bell ----

        private static void PostBellActivate()
        {
            try
            {
                if (_applying || !Active) return;
                Send(new LeopardStateMsg { Kind = LeopardEventKind.Bell });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PostBellActivate: " + e.Message); }
        }

        // ---- Gunports ----

        private static bool PreTrapdoorActivate(GPButtonTrapdoor __instance)
        {
            try
            {
                if (_applying || !ClientActive || __instance == null) return true;
                if (_fRecursive != null && (bool)_fRecursive.GetValue(null)) return true;
                int group = GunportGroupOf(__instance.name);
                if (group < 0) return true;
                Send(new LeopardStateMsg { Kind = LeopardEventKind.GunportToggle, Index = (byte)group });
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[LeopardCompat] PreTrapdoorActivate: " + e.Message);
                return true;
            }
        }

        private static void PostTrapdoorActivate(GPButtonTrapdoor __instance, bool __runOriginal)
        {
            try
            {
                if (!__runOriginal || !Host || !Active || __instance == null) return;
                if (_fRecursive != null && (bool)_fRecursive.GetValue(null)) return;
                int group = GunportGroupOf(__instance.name);
                if (group < 0) return;
                var first = FirstLiveTrapdoor(GunportList(group));
                if (first == null) return;
                Send(new LeopardStateMsg
                {
                    Kind = LeopardEventKind.GunportState,
                    Index = (byte)group,
                    Flag = first.IsOpen(),
                });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PostTrapdoorActivate: " + e.Message); }
        }

        // ---- Carriages ----

        private struct CarriageMoveState
        {
            public bool WasMoving;
            public bool WasReady;
        }

        private static bool PreCarriageActivate(Component __instance)
        {
            try
            {
                if (_applying || !ClientActive) return true;
                int idx = IndexOfCarriage(__instance);
                if (idx < 0 || idx > 255) return true;
                Send(new LeopardStateMsg
                {
                    Kind = LeopardEventKind.CarriageReq,
                    Index = (byte)idx,
                    Flag = Input.GetKey(KeyCode.LeftShift),
                });
                return false;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PreCarriageActivate: " + e.Message); return true; }
        }

        private static void PreCarriageMove(Component __instance, out CarriageMoveState __state)
        {
            __state = default;
            try
            {
                if (!Host || !Active || __instance == null) return;
                if (_fCarriageMoving != null)
                    __state.WasMoving = (bool)_fCarriageMoving.GetValue(__instance);
                int idx = IndexOfCarriage(__instance);
                if (idx >= 0 && _cannons != null && idx < _cannons.Length && _cannons[idx] != null && _fCannonReady != null)
                    __state.WasReady = (bool)_fCannonReady.GetValue(_cannons[idx]);
            }
            catch { __state = default; }
        }

        private static void PostCarriageMove(Component __instance, CarriageMoveState __state)
        {
            try
            {
                if (!Host || !Active || __instance == null || __state.WasMoving) return;
                if (!(bool)_fCarriageMoving.GetValue(__instance)) return;
                int idx = IndexOfCarriage(__instance);
                if (idx < 0 || idx > 255) return;
                // Pull() synchronously sets cannon.ready = false before Move() returns, so derive
                // target ready state from the pre-Move ready value captured in PreCarriageMove.
                Send(new LeopardStateMsg
                {
                    Kind = LeopardEventKind.CarriageMove,
                    Index = (byte)idx,
                    Flag = !__state.WasReady,
                });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PostCarriageMove: " + e.Message); }
        }

        // ---- Cannons ----

        private static bool PreCannonActivate(Component __instance)
        {
            try
            {
                if (_applying || !ClientActive) return true;
                int idx = IndexOfCannon(__instance);
                if (idx < 0 || idx > 255) return true;
                Send(new LeopardStateMsg
                {
                    Kind = LeopardEventKind.CannonFireReq,
                    Index = (byte)idx,
                    Flag = Input.GetKey(KeyCode.LeftShift),
                });
                return false;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PreCannonActivate: " + e.Message); return true; }
        }

        // Per-frame on ~50 cannons: plain fast-path, zero allocations.
        private static bool PreCannonUpdate(Component __instance)
        {
            if (!ClientActive) return true;
            try
            {
                if (Input.GetKeyDown(KeyCode.K) && Input.GetKey(KeyCode.LeftShift))
                {
                    if (_lastFireAllFrame != Time.frameCount)
                    {
                        _lastFireAllFrame = Time.frameCount;
                        int idx = IndexOfCannon(__instance);
                        Send(new LeopardStateMsg
                        {
                            Kind = LeopardEventKind.CannonFireAllReq,
                            Index = idx >= 0 && idx <= 255 ? (byte)idx : (byte)0,
                        });
                    }
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static void PreCannonFire(Component __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (Host && Active && __instance != null && _fCannonFiring != null)
                    __state = (bool)_fCannonFiring.GetValue(__instance);
            }
            catch { __state = false; }
        }

        private static void PostCannonFire(Component __instance, bool __state)
        {
            try
            {
                if (!Host || !Active || __instance == null || __state) return;
                if (!(bool)_fCannonFiring.GetValue(__instance)) return;
                int idx = IndexOfCannon(__instance);
                if (idx < 0 || idx > 255) return;
                Send(new LeopardStateMsg { Kind = LeopardEventKind.CannonFired, Index = (byte)idx });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PostCannonFire: " + e.Message); }
        }

        // ---- Cutter launch & hoist ----

        private static bool PreCutterLaunch(out bool __state)
        {
            __state = false;
            try
            {
                __state = _fCutterActive != null && (bool)_fCutterActive.GetValue(null);
                if (_applying || !ClientActive) return true;
                Send(new LeopardStateMsg { Kind = LeopardEventKind.CutterLaunchReq });
                return false;
            }
            catch { return true; }
        }

        private static bool PreCutterHoist(out bool __state)
        {
            __state = false;
            try
            {
                __state = _fCutterActive != null && (bool)_fCutterActive.GetValue(null);
                if (_applying || !ClientActive) return true;
                Send(new LeopardStateMsg { Kind = LeopardEventKind.CutterHoistReq });
                return false;
            }
            catch { return true; }
        }

        private static void PostCutterToggle(bool __state)
        {
            try
            {
                if (!Host || !Active) return;
                bool nowActive = (bool)_fCutterActive.GetValue(null);
                if (nowActive == __state) return;
                EnsureCutterStreamable();
                var cutter = _fBoat?.GetValue(null) as GameObject;
                Vector3 real = cutter != null ? CoordSpace.LocalToReal(cutter.transform.position) : Vector3.zero;
                Quaternion rot = cutter != null ? cutter.transform.rotation : Quaternion.identity;
                Send(new LeopardStateMsg
                {
                    Kind = LeopardEventKind.CutterState,
                    Flag = nowActive,
                    X = real.x, Y = real.y, Z = real.z,
                    Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w,
                });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[LeopardCompat] PostCutterToggle: " + e.Message); }
        }

        private static void EnsureCutterStreamable()
        {
            var cutter = _fBoat?.GetValue(null) as GameObject;
            if (cutter == null) return;
            var so = cutter.GetComponent<SaveableObject>();
            if (so != null && !so.extraSetting)
            {
                so.extraSetting = true;
                BoatLocator.Invalidate();
            }
        }

        // ---- Cutter rowing (OarController) ----

        // Runs every frame while cutter is active: plain try/catch, zero allocations.
        private static void PostOarExtraLateUpdate(GoPointerButton __instance)
        {
            if (!Active || __instance == null) return;
            try
            {
                bool localHeld = (_fBtnStickyBy != null && (_fBtnStickyBy.GetValue(__instance) as UnityEngine.Object) != null) ||
                                 (_fBtnIsClicked != null && (bool)_fBtnIsClicked.GetValue(__instance));
                byte localMask = 0;
                if (localHeld)
                {
                    localMask |= 0x10;
                    if (GameInput.GetKey((InputName)0)) localMask |= 1;
                    if (GameInput.GetKey((InputName)1)) localMask |= 2;
                    if (GameInput.GetKey((InputName)2)) localMask |= 4;
                    if (GameInput.GetKey((InputName)3)) localMask |= 8;
                }

                if (!Host)
                {
                    if (localMask != _lastLocalOarMask)
                    {
                        _lastLocalOarMask = localMask;
                        Send(new LeopardStateMsg { Kind = LeopardEventKind.OarInput, Index = localMask });
                    }
                    if (localHeld) _remoteOarsShown = false;
                    else DriveRemoteOarVisuals(__instance, _remoteOarMask);
                    return;
                }

                _hostLocalOarMask = localMask;
                UpdateHostCombinedOars();

                // Apply forces for any directional bits driven by remote guests that the host isn't already pressing.
                byte remoteOnly = 0;
                foreach (var kv in _hostRemoteOars) remoteOnly |= kv.Value;
                byte remoteExtra = (byte)(remoteOnly & ~localMask);
                if ((remoteExtra & 0x0F) != 0)
                {
                    var rb = _fOarRb?.GetValue(__instance) as Rigidbody;
                    if (rb != null && !rb.isKinematic)
                    {
                        float force = _fOarForce != null ? (float)_fOarForce.GetValue(__instance) : 1000f;
                        float turn = _fOarTurn != null ? (float)_fOarTurn.GetValue(__instance) : 3f;
                        if ((remoteExtra & 1) != 0) rb.AddForce(rb.transform.forward * force, ForceMode.Force);
                        if ((remoteExtra & 2) != 0) rb.AddForce(-rb.transform.forward * force, ForceMode.Force);
                        if ((remoteExtra & 4) != 0) rb.AddTorque(Vector3.up * -force * turn, ForceMode.Force);
                        if ((remoteExtra & 8) != 0) rb.AddTorque(Vector3.up * force * turn, ForceMode.Force);
                    }
                }
                if (localHeld) _remoteOarsShown = false;
                else DriveRemoteOarVisuals(__instance, remoteOnly);
            }
            catch { }
        }

        private static void UpdateHostCombinedOars()
        {
            byte combined = _hostLocalOarMask;
            foreach (var kv in _hostRemoteOars) combined |= kv.Value;
            if (combined == _hostCombinedOarMask) return;
            _hostCombinedOarMask = combined;
            Send(new LeopardStateMsg { Kind = LeopardEventKind.OarState, Index = combined });
        }

        private static void DriveRemoteOarVisuals(GoPointerButton oar, byte mask)
        {
            bool active = (mask & 0x10) != 0;
            if (active != _remoteOarsShown)
            {
                _remoteOarsShown = active;
                _mOarSetOars?.Invoke(oar, new object[] { active });
            }
            if (!active || (mask & 0x0F) == 0) return;

            var left = _fOarLeft?.GetValue(oar) as GameObject;
            var right = _fOarRight?.GetValue(oar) as GameObject;
            if (left == null || right == null) return;

            float t = _fOarTime != null ? (float)_fOarTime.GetValue(oar) : 0f;
            float inc = _fOarTimeInc != null ? (float)_fOarTimeInc.GetValue(oar) : 3f;
            float fwd = _fOarFwdAngle != null ? (float)_fOarFwdAngle.GetValue(oar) : 30f;
            float up = _fOarUpAngle != null ? (float)_fOarUpAngle.GetValue(oar) : 20f;

            if ((mask & 1) != 0) t += Time.deltaTime * inc;
            else if ((mask & 2) != 0) t -= Time.deltaTime * inc;
            else if ((mask & 12) != 0) t += Time.deltaTime * inc;
            _fOarTime?.SetValue(oar, t);

            float a = Mathf.Sin(t) * fwd;
            float b = -Mathf.Sin(t + 1.5f) * up;
            if ((mask & 4) != 0)
            {
                left.transform.localRotation = Quaternion.Euler(-b, 0f, a);
                right.transform.localRotation = Quaternion.Euler(-b, 0f, -a);
            }
            else if ((mask & 8) != 0)
            {
                left.transform.localRotation = Quaternion.Euler(b, 0f, a);
                right.transform.localRotation = Quaternion.Euler(b, 0f, -a);
            }
            else
            {
                left.transform.localRotation = Quaternion.Euler(b, 0f, a);
                right.transform.localRotation = Quaternion.Euler(-b, 0f, -a);
            }
        }

        // ---- Network receive & resync ----

        public static void OnRemote(LeopardStateMsg msg, NetPeer fromPeer)
        {
            if (!Active || msg == null) return;
            try
            {
                if (Host) HandleOnHost(msg, fromPeer);
                else HandleOnClient(msg);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[LeopardCompat] OnRemote: " + e.Message);
            }
        }

        private static void HandleOnHost(LeopardStateMsg msg, NetPeer fromPeer)
        {
            EnsureCached();
            uint actor = Net.PlayerNetIdForPeer(fromPeer);
            switch (msg.Kind)
            {
                case LeopardEventKind.Bell:
                    InvokeBell();
                    Send(msg, except: fromPeer);
                    break;
                case LeopardEventKind.GunportToggle:
                {
                    var first = FirstLiveTrapdoor(GunportList(msg.Index));
                    if (first != null)
                    {
                        _applying = true;
                        try { first.OnActivate(); }
                        finally { _applying = false; }
                    }
                    break;
                }
                case LeopardEventKind.CarriageReq:
                    if (_carriages != null && msg.Index < _carriages.Length && _carriages[msg.Index] != null)
                    {
                        var c = _carriages[msg.Index] as MonoBehaviour;
                        if (msg.Flag && _mCarriageMoveAll != null)
                            c?.StartCoroutine((IEnumerator)_mCarriageMoveAll.Invoke(c, null));
                        else
                            _mCarriageMove.Invoke(c, null);
                    }
                    break;
                case LeopardEventKind.CannonFireReq:
                    if (_cannons != null && msg.Index < _cannons.Length && _cannons[msg.Index] != null)
                    {
                        var c = _cannons[msg.Index] as MonoBehaviour;
                        if (msg.Flag && _mCannonFireAll != null)
                            c?.StartCoroutine((IEnumerator)_mCannonFireAll.Invoke(c, null));
                        else
                            _mCannonFire.Invoke(c, null);
                    }
                    break;
                case LeopardEventKind.CannonFireAllReq:
                    if (_cannons != null && _cannons.Length > 0 && _mCannonFireAll != null)
                    {
                        // Vanilla Shift+K triggers FireAll() on both port and starboard broadsides.
                        MonoBehaviour portCannon = null, stbdCannon = null;
                        for (int i = 0; i < _cannons.Length && (portCannon == null || stbdCannon == null); i++)
                        {
                            var c = _cannons[i] as MonoBehaviour;
                            if (c == null) continue;
                            bool isPort = _fCannonPort != null && (bool)_fCannonPort.GetValue(c);
                            if (isPort && portCannon == null) portCannon = c;
                            else if (!isPort && stbdCannon == null) stbdCannon = c;
                        }
                        if (portCannon != null) portCannon.StartCoroutine((IEnumerator)_mCannonFireAll.Invoke(portCannon, null));
                        if (stbdCannon != null) stbdCannon.StartCoroutine((IEnumerator)_mCannonFireAll.Invoke(stbdCannon, null));
                    }
                    break;
                case LeopardEventKind.CutterLaunchReq:
                {
                    var ship = _fShip?.GetValue(null) as GameObject;
                    var tr = ship?.transform.Find("boat leopard/structure_container/Wooden Rowboat");
                    var ctrl = tr != null ? tr.GetComponent(_cutterCtrlType) : null;
                    if (ctrl != null) _mCutterActivate?.Invoke(ctrl, new object[] { null });
                    break;
                }
                case LeopardEventKind.CutterHoistReq:
                {
                    var ship = _fShip?.GetValue(null) as GameObject;
                    var tr = ship?.transform.Find("boat leopard/structure_container/rowboat rope");
                    var ctrl = tr != null ? tr.GetComponent(_cutterRopeType) : null;
                    if (ctrl != null) _mCutterRopeActivate?.Invoke(ctrl, null);
                    break;
                }
                case LeopardEventKind.OarInput:
                    if (actor != 0)
                    {
                        if (msg.Index == 0) _hostRemoteOars.Remove(actor);
                        else _hostRemoteOars[actor] = msg.Index;
                        UpdateHostCombinedOars();
                    }
                    break;
            }
        }

        private static void HandleOnClient(LeopardStateMsg msg)
        {
            EnsureCached();
            switch (msg.Kind)
            {
                case LeopardEventKind.Bell:
                    InvokeBell();
                    break;
                case LeopardEventKind.GunportState:
                    ApplyClientGunportGroup(msg.Index, msg.Flag, allowAudio: true);
                    break;
                case LeopardEventKind.CarriageMove:
                    if (_carriages != null && msg.Index < _carriages.Length && _carriages[msg.Index] != null)
                    {
                        var carriage = _carriages[msg.Index];
                        var cannon = _cannons[msg.Index];
                        bool curReady = cannon != null && (bool)_fCannonReady.GetValue(cannon);
                        bool moving = _fCarriageMoving != null && (bool)_fCarriageMoving.GetValue(carriage);
                        if (curReady != msg.Flag && !moving)
                        {
                            _applying = true;
                            try { _mCarriageMove.Invoke(carriage, null); }
                            finally { _applying = false; }
                        }
                    }
                    break;
                case LeopardEventKind.CannonFired:
                    if (_cannons != null && msg.Index < _cannons.Length && _cannons[msg.Index] != null)
                    {
                        var cannon = _cannons[msg.Index];
                        _fCannonReady?.SetValue(cannon, true);
                        _applying = true;
                        try { _mCannonFire.Invoke(cannon, null); }
                        finally { _applying = false; }
                    }
                    break;
                case LeopardEventKind.CutterState:
                    ApplyClientCutter(msg.Flag, new Vector3(msg.X, msg.Y, msg.Z), new Quaternion(msg.Rx, msg.Ry, msg.Rz, msg.Rw));
                    break;
                case LeopardEventKind.OarState:
                    _remoteOarMask = msg.Index;
                    break;
                case LeopardEventKind.Snapshot:
                    ApplyClientGunportGroup(0, (msg.Index & 1) != 0, allowAudio: false);
                    ApplyClientGunportGroup(1, (msg.Index & 2) != 0, allowAudio: false);
                    ApplyClientGunportGroup(2, (msg.Index & 4) != 0, allowAudio: false);
                    SnapClientCannons(msg.ReadyMask);
                    ApplyClientCutter(msg.Flag, new Vector3(msg.X, msg.Y, msg.Z), new Quaternion(msg.Rx, msg.Ry, msg.Rz, msg.Rw));
                    _remoteOarMask = msg.Extra;
                    break;
            }
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !SyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.Leopard) == 0) return;
            try
            {
                EnsureCached();
                byte gpMask = 0;
                for (int g = 0; g < 3; g++)
                {
                    var td = FirstLiveTrapdoor(GunportList(g));
                    if (td != null && td.IsOpen()) gpMask |= (byte)(1 << g);
                }
                ulong readyMask = 0;
                if (_cannons != null)
                {
                    for (int i = 0; i < _cannons.Length && i < 64; i++)
                        if (_cannons[i] != null && (bool)_fCannonReady.GetValue(_cannons[i]))
                            readyMask |= 1UL << i;
                }
                bool cutterActive = _fCutterActive != null && (bool)_fCutterActive.GetValue(null);
                if (cutterActive) EnsureCutterStreamable();
                var cutter = _fBoat?.GetValue(null) as GameObject;
                Vector3 real = cutter != null ? CoordSpace.LocalToReal(cutter.transform.position) : Vector3.zero;
                Quaternion rot = cutter != null ? cutter.transform.rotation : Quaternion.identity;

                peer.Send(new LeopardStateMsg
                {
                    Kind = LeopardEventKind.Snapshot,
                    Index = gpMask,
                    Flag = cutterActive,
                    Extra = _hostCombinedOarMask,
                    ReadyMask = readyMask,
                    X = real.x, Y = real.y, Z = real.z,
                    Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w,
                }, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[LeopardCompat] ResyncTo: " + e.Message);
            }
        }

        private static void InvokeBell()
        {
            var ship = _fShip?.GetValue(null) as GameObject;
            var tr = ship?.transform.Find("boat leopard/structure_container/bell");
            var bell = tr != null && _bellType != null ? tr.GetComponent(_bellType) : null;
            if (bell == null || _mBellActivate == null) return;
            _applying = true;
            try { _mBellActivate.Invoke(bell, new object[] { null }); }
            finally { _applying = false; }
        }

        private static void ApplyClientGunportGroup(int group, bool open, bool allowAudio)
        {
            var list = GunportList(group);
            if (list == null || list.Count == 0) return;
            _applying = true;
            if (_fRecursive != null) _fRecursive.SetValue(null, true);
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var tr = list[i];
                    var td = tr != null ? tr.GetComponent<GPButtonTrapdoor>() : null;
                    if (td != null && td.IsOpen() != open) td.OnActivate();
                }
            }
            finally
            {
                if (_fRecursive != null) _fRecursive.SetValue(null, false);
                _applying = false;
            }

            var ship = _fShip?.GetValue(null) as GameObject;
            if (ship == null) return;
            if (group == 0)
            {
                _mToggleOverflows?.Invoke(null, null);
                var half = ship.transform.Find("boat leopard/mask water half");
                var full = ship.transform.Find("boat leopard/mask water full");
                if (half != null) half.gameObject.SetActive(open);
                if (full != null) full.gameObject.SetActive(!open);
                SyncInteriorTrigger(ship, "interior trigger 2", open, allowAudio);
            }
            else if (group == 1)
            {
                SyncInteriorTrigger(ship, "interior trigger 3", open, allowAudio);
            }
        }

        private static void SyncInteriorTrigger(GameObject ship, string triggerName, bool open, bool allowAudio)
        {
            var hull = ship.transform.Find("boat leopard");
            if (allowAudio && hull != null && GameState.currentBoat == hull && _mToggleAudio != null)
            {
                _mToggleAudio.Invoke(null, new object[] { triggerName });
                return;
            }
            var trig = ship.transform.Find("boat leopard/structure_container/" + triggerName);
            if (trig != null) trig.gameObject.SetActive(!open);
        }

        private static void SnapClientCannons(ulong readyMask)
        {
            if (_cannons == null || _carriages == null) return;
            var embark = _fEmbarkLeopard?.GetValue(null) as GameObject;
            for (int i = 0; i < _cannons.Length && i < 64; i++)
            {
                var cannon = _cannons[i];
                var carriage = _carriages[i];
                if (cannon == null || carriage == null) continue;
                bool targetReady = (readyMask & (1UL << i)) != 0;
                bool curReady = (bool)_fCannonReady.GetValue(cannon);
                if (curReady == targetReady) continue;

                bool port = _fCarriagePort != null && (bool)_fCarriagePort.GetValue(carriage);
                var cannonTr = _fCarriageCannon?.GetValue(carriage) as Transform ?? cannon.transform;
                var deck = _fCarriageDeck?.GetValue(carriage) as Transform;
                Vector3 pos = targetReady ? Vector3.zero : new Vector3(0f, port ? 0.3f : -0.3f, 0f);
                Quaternion rot = targetReady ? Quaternion.Euler(0f, 0f, port ? 0f : 180f) : Quaternion.Euler(0f, 0f, -90f);

                cannonTr.localPosition = pos;
                cannonTr.localRotation = rot;
                if (embark != null && deck != null && cannonTr.parent != null)
                {
                    var walk = embark.transform.Find("structure_container/CANNONS/" + deck.name + "/" + cannonTr.parent.name + "/cannon");
                    if (walk != null)
                    {
                        walk.localPosition = pos;
                        walk.localRotation = rot;
                    }
                }
                _fCannonReady.SetValue(cannon, targetReady);
            }
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        private static bool IsFinite(Quaternion q) => IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);

        private static void ApplyClientCutter(bool active, Vector3 realPos, Quaternion rot)
        {
            var ship = _fShip?.GetValue(null) as GameObject;
            var cutter = _fBoat?.GetValue(null) as GameObject;
            if (cutter == null) return;

            _fCutterActive?.SetValue(null, active);
            if (ship != null)
            {
                var rowboat = ship.transform.Find("boat leopard/structure_container/Wooden Rowboat");
                var rope = ship.transform.Find("boat leopard/structure_container/rowboat rope");
                if (rowboat != null) rowboat.gameObject.SetActive(!active);
                if (rope != null) rope.gameObject.SetActive(active);
            }

            if (active)
            {
                EnsureCutterStreamable();
                cutter.SetActive(true);
                if (IsFinite(realPos) && IsFinite(rot) && realPos != Vector3.zero)
                    cutter.transform.SetPositionAndRotation(CoordSpace.RealToLocal(realPos), rot);
                var horizon = cutter.transform.Find("boat cutter")?.GetComponent<BoatHorizon>();
                if (horizon != null && _fBoatHorizonCooldown != null)
                    _fBoatHorizonCooldown.SetValue(horizon, 0f);
            }
            else
            {
                var cutterHull = cutter.transform.Find("boat cutter");
                if (cutterHull != null && GameState.currentBoat == cutterHull)
                    CoopBehaviour.Instance?.Teleport?.Request();
                cutter.SetActive(false);
            }
        }
    }
}
