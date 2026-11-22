using System;
using System.Collections;
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
    /// Sailor's Keepsakes / AdornosMod (com.vikingdmike.adornosmod) adds custom sword/helmet/lamp
    /// display racks (EspaderoItem, SoporteItem, SoporteLamparaItem), hidden treasures (AdornoItem.esTesoro),
    /// Mjolnir storm summoning, and incense burners (IncensoItem).
    /// </summary>
    public static class SailorsKeepsakesCompat
    {
        public const string Guid = "com.vikingdmike.adornosmod";
        private const string AssemblyName = "AdornosMod";

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool Client => Net?.Role == Role.Client;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.SailorsKeepsakes) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _espaderoType, _soporteType, _soporteLamparaType, _adornoType, _incensoType, _mjolnirType, _pluginType;
        private static FieldInfo _fPuestas, _fDesdeEsp, _fTodos;
        private static FieldInfo _fPuesto, _fDesdeSop, _fSoportes;
        private static FieldInfo _fLampara, _fAsa, _fDesdeLam;
        private static MethodInfo _mEspSujetar, _mSopFijar, _mLamSujetar, _mAsa;
        private static MethodInfo _mEspLateUpdate, _mSopLateUpdate, _mLamLateUpdate;
        private static FieldInfo _fEsTesoro;
        private static MethodInfo _mEspClick, _mSopClick, _mLamClick, _mAdornoLateUpdate, _mIncensoAlternar, _mLlamarTormenta;
        private static MethodInfo _mTrueno, _mRayo;
        private static FieldInfo _fMjDistancia, _fMjEspera, _fMjProximoUso, _fAlCoger;
        private static PropertyInfo _pIncensoEncendido;
        private static bool _applying;
        private static readonly System.Collections.Generic.Dictionary<ShipItem, float> _netAttached =
            new System.Collections.Generic.Dictionary<ShipItem, float>();

        /// <summary>Storm effects (lightning + thunder) play only for players this close to the summoner.</summary>
        private const float StormFxRange = 1500f;

        /// <summary>
        /// A rack keeps an item a remote player is holding for this long after it was attached, so the
        /// holder's drop (ItemState) can arrive after the attach message without the item being torn off.
        /// The mod's own local grace is 0.4 s; network ordering between channels needs more.
        /// </summary>
        private const float RemoteHeldGrace = 1.5f;

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

                _espaderoType = asm.GetType("AdornosMod.EspaderoItem");
                _soporteType = asm.GetType("AdornosMod.SoporteItem");
                _soporteLamparaType = asm.GetType("AdornosMod.SoporteLamparaItem");
                _adornoType = asm.GetType("AdornosMod.AdornoItem");
                _incensoType = asm.GetType("AdornosMod.IncensoItem");
                _mjolnirType = asm.GetType("AdornosMod.MjolnirItem");
                _pluginType = asm.GetType("AdornosMod.Plugin");

                if (_espaderoType != null)
                {
                    _mEspClick = AccessTools.Method(_espaderoType, "OnItemClick", new[] { typeof(PickupableItem) });
                    _mEspLateUpdate = AccessTools.Method(_espaderoType, "ExtraLateUpdate");
                    _mEspSujetar = AccessTools.Method(_espaderoType, "Sujetar", new[] { typeof(ShipItem), typeof(bool) });
                    _fPuestas = AccessTools.Field(_espaderoType, "puestas");
                    _fDesdeEsp = AccessTools.Field(_espaderoType, "desde");
                    _fTodos = AccessTools.Field(_espaderoType, "todos");
                }
                if (_soporteType != null)
                {
                    _mSopClick = AccessTools.Method(_soporteType, "OnItemClick", new[] { typeof(PickupableItem) });
                    _mSopLateUpdate = AccessTools.Method(_soporteType, "ExtraLateUpdate");
                    _mSopFijar = AccessTools.Method(_soporteType, "Fijar", new[] { typeof(ShipItem), typeof(bool) });
                    _fPuesto = AccessTools.Field(_soporteType, "puesto");
                    _fDesdeSop = AccessTools.Field(_soporteType, "desdeS");
                    _fSoportes = AccessTools.Field(_soporteType, "soportes");
                }
                if (_soporteLamparaType != null)
                {
                    _mLamClick = AccessTools.Method(_soporteLamparaType, "OnItemClick", new[] { typeof(PickupableItem) });
                    _mLamLateUpdate = AccessTools.Method(_soporteLamparaType, "ExtraLateUpdate");
                    _mLamSujetar = AccessTools.Method(_soporteLamparaType, "Sujetar", new[] { typeof(ShipItem), typeof(bool) });
                    _fLampara = AccessTools.Field(_soporteLamparaType, "lampara");
                    _fAsa = AccessTools.Field(_soporteLamparaType, "asa");
                    _fDesdeLam = AccessTools.Field(_soporteLamparaType, "desde");
                    _mAsa = AccessTools.Method(_soporteLamparaType, "Asa", new[] { typeof(ShipItem) });
                }
                if (_adornoType != null)
                {
                    _fEsTesoro = AccessTools.Field(_adornoType, "esTesoro");
                    _fAlCoger = AccessTools.Field(_adornoType, "alCoger");
                    _mAdornoLateUpdate = AccessTools.Method(_adornoType, "ExtraLateUpdate");
                }
                if (_incensoType != null)
                {
                    _mIncensoAlternar = AccessTools.Method(_incensoType, "Alternar");
                    _pIncensoEncendido = AccessTools.Property(_incensoType, "Encendido");
                }
                if (_mjolnirType != null)
                {
                    _mLlamarTormenta = AccessTools.Method(_mjolnirType, "LlamarTormenta");
                    _mTrueno = AccessTools.Method(_mjolnirType, "Trueno");
                    _mRayo = AccessTools.Method(_mjolnirType, "Rayo", new[] { typeof(WanderingStorm), typeof(Vector3) });
                    _fMjDistancia = AccessTools.Field(_mjolnirType, "distancia");
                    _fMjEspera = AccessTools.Field(_mjolnirType, "espera");
                    _fMjProximoUso = AccessTools.Field(_mjolnirType, "proximoUso");
                }

                SyncEnabled = _mEspClick != null || _mSopClick != null || _mLamClick != null || _mIncensoAlternar != null || _mLlamarTormenta != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[SailorsKeepsakesCompat] Sailor's Keepsakes detected; racks, treasures, incense & Mjolnir sync enabled.");
                else
                    Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] Sailor's Keepsakes detected, but target members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                if (_mEspClick != null)
                    harmony.Patch(_mEspClick, postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostEspaderoClick)));
                if (_mEspLateUpdate != null)
                    harmony.Patch(_mEspLateUpdate,
                        prefix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PreEspaderoLateUpdate)),
                        postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostEspaderoLateUpdate)));
                if (_mSopClick != null)
                    harmony.Patch(_mSopClick, postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostSoporteClick)));
                if (_mSopLateUpdate != null)
                    harmony.Patch(_mSopLateUpdate,
                        prefix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PreSoporteLateUpdate)),
                        postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostSoporteLateUpdate)));
                if (_mLamClick != null)
                    harmony.Patch(_mLamClick, postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostLamparaClick)));
                if (_mLamLateUpdate != null)
                    harmony.Patch(_mLamLateUpdate,
                        prefix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PreLamparaLateUpdate)),
                        postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostLamparaLateUpdate)));
                if (_mAdornoLateUpdate != null && _fEsTesoro != null)
                    harmony.Patch(_mAdornoLateUpdate,
                        prefix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PreAdornoLateUpdate)),
                        postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostAdornoLateUpdate)));
                if (_mIncensoAlternar != null)
                    harmony.Patch(_mIncensoAlternar, postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostIncensoAlternar)));
                if (_mLlamarTormenta != null)
                    harmony.Patch(_mLlamarTormenta,
                        prefix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PreLlamarTormenta)),
                        postfix: new HarmonyMethod(typeof(SailorsKeepsakesCompat), nameof(PostLlamarTormenta)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _applying = false;
            _netAttached.Clear();
        }

        private static int IdOf(Component c)
        {
            var sp = c != null ? c.GetComponent<SaveablePrefab>() : null;
            return sp != null ? sp.instanceId : 0;
        }

        private static void PostEspaderoClick(ShipItem __instance, PickupableItem __0, bool __result)
        {
            try
            {
                if (_applying || !Active || !__result || __instance == null || !(__0 is ShipItem item)) return;
                var arr = _fPuestas?.GetValue(__instance) as ShipItem[];
                int slot = arr != null ? Array.IndexOf(arr, item) : -1;
                if (slot < 0) return;
                int hid = IdOf(__instance), iid = IdOf(item);
                if (hid == 0 || iid == 0) return;
                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.RackAttach, HolderInstanceId = hid, ItemInstanceId = iid, Slot = (sbyte)slot },
                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PostEspaderoClick: " + e.Message); }
        }

        private static void PreEspaderoLateUpdate(ShipItem __instance, out ShipItem[] __state)
        {
            __state = null;
            try
            {
                if (!Active || __instance == null || _fPuestas == null) return;
                var arr = _fPuestas.GetValue(__instance) as ShipItem[];
                if (arr == null) return;
                var items = ItemSync.Instance;
                bool any = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == null) continue;
                    if (ShouldDetachRemoteHeld(items, arr[i]))
                    {
                        try { _mEspSujetar?.Invoke(null, new object[] { arr[i], false }); } catch { }
                        arr[i] = null;
                        continue;
                    }
                    any = true;
                }
                if (any) __state = (ShipItem[])arr.Clone();
            }
            catch { __state = null; }
        }

        private static void PostEspaderoLateUpdate(ShipItem __instance, ShipItem[] __state)
        {
            try
            {
                if (_applying || !Active || __instance == null || __state == null || _fPuestas == null) return;
                var arr = _fPuestas.GetValue(__instance) as ShipItem[];
                if (arr == null) return;
                int hid = IdOf(__instance);
                if (hid == 0) return;
                for (int i = 0; i < Math.Min(__state.Length, arr.Length); i++)
                {
                    if (__state[i] != null && arr[i] == null)
                        SendDetach(hid, IdOf(__state[i]), (sbyte)i);
                }
            }
            catch { }
        }

        private static void PostSoporteClick(ShipItem __instance, PickupableItem __0, bool __result)
        {
            try
            {
                if (_applying || !Active || !__result || __instance == null || !(__0 is ShipItem item)) return;
                int hid = IdOf(__instance), iid = IdOf(item);
                if (hid == 0 || iid == 0) return;
                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.RackAttach, HolderInstanceId = hid, ItemInstanceId = iid, Slot = 0 },
                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PostSoporteClick: " + e.Message); }
        }

        private static void PreSoporteLateUpdate(ShipItem __instance, out ShipItem __state)
        {
            __state = null;
            try
            {
                if (!Active || __instance == null || _fPuesto == null) return;
                var placed = _fPuesto.GetValue(__instance) as ShipItem;
                if (placed != null && ShouldDetachRemoteHeld(ItemSync.Instance, placed))
                {
                    try { _mSopFijar?.Invoke(null, new object[] { placed, false }); } catch { }
                    _fPuesto.SetValue(__instance, null);
                    return;
                }
                __state = placed;
            }
            catch { __state = null; }
        }

        private static void PostSoporteLateUpdate(ShipItem __instance, ShipItem __state)
        {
            try
            {
                if (_applying || !Active || __instance == null || __state == null || _fPuesto == null) return;
                if (_fPuesto.GetValue(__instance) == null)
                    SendDetach(IdOf(__instance), IdOf(__state), 0);
            }
            catch { }
        }

        private static void PostLamparaClick(ShipItem __instance, PickupableItem __0, bool __result)
        {
            try
            {
                if (_applying || !Active || !__result || __instance == null || !(__0 is ShipItem item)) return;
                int hid = IdOf(__instance), iid = IdOf(item);
                if (hid == 0 || iid == 0) return;
                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.RackAttach, HolderInstanceId = hid, ItemInstanceId = iid, Slot = 0 },
                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PostLamparaClick: " + e.Message); }
        }

        private static void PreLamparaLateUpdate(ShipItem __instance, out ShipItem __state)
        {
            __state = null;
            try
            {
                if (!Active || __instance == null || _fLampara == null) return;
                var placed = _fLampara.GetValue(__instance) as ShipItem;
                if (placed != null && ShouldDetachRemoteHeld(ItemSync.Instance, placed))
                {
                    try { _mLamSujetar?.Invoke(null, new object[] { placed, false }); } catch { }
                    _fLampara.SetValue(__instance, null);
                    return;
                }
                __state = placed;
            }
            catch { __state = null; }
        }

        private static void PostLamparaLateUpdate(ShipItem __instance, ShipItem __state)
        {
            try
            {
                if (_applying || !Active || __instance == null || __state == null || _fLampara == null) return;
                if (_fLampara.GetValue(__instance) == null)
                    SendDetach(IdOf(__instance), IdOf(__state), 0);
            }
            catch { }
        }

        private static void SendDetach(int holderId, int itemId, sbyte slot)
        {
            if (holderId == 0) return;
            Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.RackDetach, HolderInstanceId = holderId, ItemInstanceId = itemId, Slot = slot },
                ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
        }

        private static void PreAdornoLateUpdate(ShipItem __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (CoopBehaviour.Instance?.PlayingCoop != true || __instance == null) return;
                __state = (bool)_fEsTesoro.GetValue(__instance);
            }
            catch { __state = false; }
        }

        private static void PostAdornoLateUpdate(ShipItem __instance, bool __state)
        {
            try
            {
                if (!__state || __instance == null || _applying) return;
                if ((bool)_fEsTesoro.GetValue(__instance)) return;
                // This player just picked the treasure up (the mod cleared esTesoro and registered it).
                if (Client) ItemSync.Instance?.NotifyClientAuthored(__instance);
                if (Active) SendTreasureTaken(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PostAdornoLateUpdate: " + e.Message); }
        }

        /// <summary>
        /// Items attached by a network RackAttach while their holder's drop may still be in flight keep a grace
        /// window. The window ends early once the item is seen not remote-held (the drop arrived), so a later
        /// pickup by that player detaches immediately instead of fighting ItemSync for the rest of the window.
        /// </summary>
        private static bool ShouldDetachRemoteHeld(ItemSync items, ShipItem item)
        {
            if (items == null || item == null) return false;
            if (!items.IsRemoteHeld(item))
            {
                _netAttached.Remove(item);
                return false;
            }
            if (_netAttached.TryGetValue(item, out float at))
            {
                if (Time.time - at <= RemoteHeldGrace) return false;
                _netAttached.Remove(item);
            }
            return true;
        }

        private static void NoteNetAttach(ShipItem item)
        {
            if (item == null) return;
            if (_netAttached.Count > 64)
            {
                var stale = new System.Collections.Generic.List<ShipItem>();
                foreach (var kv in _netAttached)
                    if (kv.Key == null || Time.time - kv.Value > RemoteHeldGrace) stale.Add(kv.Key);
                foreach (var k in stale) _netAttached.Remove(k);
            }
            _netAttached[item] = Time.time;
        }

        private const float TreasureRadius = 8f;

        // Hidden treasures (Plugin.Poner) spawn on every peer at the same configured spot (instanceId 0, not
        // synced). Whoever picks one up first registers it as a shared item and announces TreasureTaken; the
        // others mark it found in their own config (AdornoItem.alCoger) and remove their local copy, so the
        // crew finds each treasure once.
        private static void SendTreasureTaken(ShipItem item)
        {
            var sp = item.GetComponent<SaveablePrefab>();
            if (sp == null || !CoordSpace.Ready) return;
            Vector3 real = CoordSpace.LocalToReal(item.transform.position);
            Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.TreasureTaken, ItemInstanceId = sp.prefabIndex, X = real.x, Y = real.y, Z = real.z },
                ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
        }

        private static void ApplyTreasureTaken(KeepsakeStateMsg msg)
        {
            if (_adornoType == null || _fEsTesoro == null || !CoordSpace.Ready) return;
            var spot = new Vector3(msg.X, msg.Y, msg.Z);
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_adornoType))
            {
                var a = o as ShipItem;
                if (a == null || !(bool)_fEsTesoro.GetValue(a)) continue;
                var sp = a.GetComponent<SaveablePrefab>();
                if (sp == null || sp.prefabIndex != msg.ItemInstanceId) continue;
                if (Vector3.Distance(CoordSpace.LocalToReal(a.transform.position), spot) > TreasureRadius) continue;
                _fEsTesoro.SetValue(a, false);
                try { (_fAlCoger?.GetValue(null) as Delegate)?.DynamicInvoke(a); }
                catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] Treasure found mark: " + (e.InnerException ?? e).Message); }
                UnityEngine.Object.Destroy(a.gameObject);
            }
        }

        private static void PostIncensoAlternar(ShipItem __instance)
        {
            try
            {
                if (_applying || !Active || __instance == null || _pIncensoEncendido == null) return;
                int hid = IdOf(__instance);
                if (hid == 0) return;
                bool lit = (bool)_pIncensoEncendido.GetValue(__instance, null);
                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.IncenseToggle, HolderInstanceId = hid, Flag = lit },
                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PostIncensoAlternar: " + e.Message); }
        }

        private static bool PreLlamarTormenta(ref bool __result)
        {
            try
            {
                if (_applying || !Active || !Client) return true;
                var cam = Camera.main;
                if (cam == null || !CoordSpace.Ready) return true;
                Vector3 real = CoordSpace.LocalToReal(cam.transform.position);
                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.MjolnirStorm, X = real.x, Y = real.y, Z = real.z },
                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
                // No local cooldown yet: the host answers with the result and the cooldown starts only on success.
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PreLlamarTormenta: " + e.Message);
                return true;
            }
        }

        private static float ConfigFloat(FieldInfo field, float fallback)
        {
            try
            {
                if (field?.GetValue(null) is BepInEx.Configuration.ConfigEntry<float> entry) return entry.Value;
            }
            catch { }
            return fallback;
        }

        /// <summary>Host: MjolnirItem.LlamarTormenta placed around the summoner instead of the host camera.</summary>
        private static bool SummonStormAt(Vector3 realPos)
        {
            if (!CoordSpace.Ready) return false;
            if (float.IsNaN(realPos.x) || float.IsNaN(realPos.y) || float.IsNaN(realPos.z) ||
                float.IsInfinity(realPos.x) || float.IsInfinity(realPos.y) || float.IsInfinity(realPos.z)) return false;
            Vector3 at = CoordSpace.RealToLocal(realPos);
            WanderingStorm best = null;
            float bestDist = float.MaxValue;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<WanderingStorm>())
            {
                if (s == null) continue;
                Vector3 d = s.transform.position - at;
                d.y = 0f;
                if (d.magnitude < bestDist) { bestDist = d.magnitude; best = s; }
            }
            if (best == null) return false;
            Vector3 wind = global::Wind.currentWind;
            wind.y = 0f;
            Vector3 dir = wind.sqrMagnitude > 0.01f ? wind.normalized : Vector3.forward;
            Vector3 pos = at - dir * (best.GetRadius() + Mathf.Max(0f, ConfigFloat(_fMjDistancia, 0f)));
            pos.y = best.transform.position.y;
            best.transform.position = pos;
            best.active = true;
            return true;
        }

        private static void OnMjolnirResult(bool success)
        {
            if (!success) return;
            try
            {
                if (_fMjProximoUso != null)
                    _fMjProximoUso.SetValue(null, Time.time + ConfigFloat(_fMjEspera, 0f) * 60f);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] OnMjolnirResult: " + e.Message); }
        }

        public static void OnRemote(KeepsakeStateMsg msg, NetPeer fromPeer)
        {
            if (!Active || msg == null) return;
            try
            {
                var items = ItemSync.Instance;
                _applying = true;
                try
                {
                    if (msg.Kind == KeepsakeKind.MjolnirStorm)
                    {
                        if (Host)
                        {
                            bool ok = false;
                            var at = new Vector3(msg.X, msg.Y, msg.Z);
                            try { ok = SummonStormAt(at); }
                            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] Mjolnir storm: " + e.Message); }
                            // The summoner gets the result (cooldown) and the effect; everyone else only the
                            // effect. The storm itself reaches everyone through WeatherStormSync.
                            fromPeer?.Send(new KeepsakeStateMsg { Kind = KeepsakeKind.MjolnirStorm, Flag = ok, HolderInstanceId = 1, X = msg.X, Y = msg.Y, Z = msg.Z },
                                DeliveryMethod.ReliableOrdered);
                            if (ok)
                            {
                                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.MjolnirStorm, Flag = true, X = msg.X, Y = msg.Y, Z = msg.Z },
                                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered, except: fromPeer);
                                PlayStormFx(at);
                            }
                        }
                        else
                        {
                            if (msg.HolderInstanceId == 1) OnMjolnirResult(msg.Flag);
                            if (msg.Flag) PlayStormFx(new Vector3(msg.X, msg.Y, msg.Z));
                        }
                        return;
                    }

                    if (msg.Kind == KeepsakeKind.TreasureTaken)
                    {
                        ApplyTreasureTaken(msg);
                    }
                    else
                    {
                        var holder = items?.FindByInstanceId(msg.HolderInstanceId);
                        if (holder == null) return;
                        ApplyHolderState(msg, holder, items);
                    }
                }
                finally { _applying = false; }

                if (Host)
                    Net.SendMod(msg, ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered, except: fromPeer);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] OnRemote: " + e.Message);
            }
        }

        private static void ApplyHolderState(KeepsakeStateMsg msg, ShipItem holder, ItemSync items)
        {
            if (msg.Kind == KeepsakeKind.IncenseToggle)
            {
                if (_incensoType != null && _incensoType.IsInstanceOfType(holder) && _pIncensoEncendido != null && _mIncensoAlternar != null)
                {
                    bool cur = (bool)_pIncensoEncendido.GetValue(holder, null);
                    if (cur != msg.Flag) _mIncensoAlternar.Invoke(holder, null);
                }
            }
            else if (msg.Kind == KeepsakeKind.RackAttach)
            {
                var item = items?.FindByInstanceId(msg.ItemInstanceId);
                if (item != null)
                {
                    NoteNetAttach(item);
                    ApplyAttach(holder, item, msg.Slot);
                }
            }
            else if (msg.Kind == KeepsakeKind.RackDetach)
            {
                ApplyDetach(holder, msg.ItemInstanceId, msg.Slot);
            }
        }

        private static void PlayStormFx(Vector3 realPos)
        {
            try
            {
                if (!CoordSpace.Ready) return;
                Vector3 at = CoordSpace.RealToLocal(realPos);
                var cam = Camera.main;
                if (cam == null || Vector3.Distance(cam.transform.position, at) > StormFxRange) return;
                WanderingStorm best = null;
                float bestDist = float.MaxValue;
                foreach (var s in UnityEngine.Object.FindObjectsOfType<WanderingStorm>())
                {
                    if (s == null) continue;
                    float d = Vector3.Distance(s.transform.position, at);
                    if (d < bestDist) { bestDist = d; best = s; }
                }
                // Rayo = lightning around the summoner + thunder (as in single player); thunder alone as fallback.
                if (best != null && _mRayo != null) _mRayo.Invoke(null, new object[] { best, at });
                else _mTrueno?.Invoke(null, new object[] { null });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] Storm effect: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>Host summoned with its own Mjolnir: show the effect to nearby crew.</summary>
        private static void PostLlamarTormenta(bool __result)
        {
            try
            {
                if (_applying || !__result || !Host || !Active || !CoordSpace.Ready) return;
                var cam = Camera.main;
                if (cam == null) return;
                Vector3 real = CoordSpace.LocalToReal(cam.transform.position);
                Net.SendMod(new KeepsakeStateMsg { Kind = KeepsakeKind.MjolnirStorm, Flag = true, X = real.x, Y = real.y, Z = real.z },
                    ModFeatures.SailorsKeepsakes, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] PostLlamarTormenta: " + e.Message); }
        }


        private static void ApplyAttach(ShipItem holder, ShipItem item, sbyte slot)
        {
            if (_espaderoType != null && _espaderoType.IsInstanceOfType(holder))
            {
                var arr = _fPuestas?.GetValue(holder) as ShipItem[];
                var times = _fDesdeEsp?.GetValue(holder) as float[];
                if (arr != null && slot >= 0 && slot < arr.Length)
                {
                    arr[slot] = item;
                    if (times != null && slot < times.Length) times[slot] = Time.time;
                }
            }
            else if (_soporteType != null && _soporteType.IsInstanceOfType(holder))
            {
                _fPuesto?.SetValue(holder, item);
                _fDesdeSop?.SetValue(holder, Time.time);
            }
            else if (_soporteLamparaType != null && _soporteLamparaType.IsInstanceOfType(holder))
            {
                _fLampara?.SetValue(holder, item);
                if (_fAsa != null && _mAsa != null)
                    _fAsa.SetValue(holder, _mAsa.Invoke(null, new object[] { item }));
                _fDesdeLam?.SetValue(holder, Time.time);
            }
        }

        private static void ApplyDetach(ShipItem holder, int itemId, sbyte slot)
        {
            if (_espaderoType != null && _espaderoType.IsInstanceOfType(holder))
            {
                var arr = _fPuestas?.GetValue(holder) as ShipItem[];
                if (arr != null && slot >= 0 && slot < arr.Length && arr[slot] != null && (itemId == 0 || IdOf(arr[slot]) == itemId))
                {
                    try { _mEspSujetar?.Invoke(null, new object[] { arr[slot], false }); } catch { }
                    arr[slot] = null;
                }
            }
            else if (_soporteType != null && _soporteType.IsInstanceOfType(holder))
            {
                var placed = _fPuesto?.GetValue(holder) as ShipItem;
                if (placed != null && (itemId == 0 || IdOf(placed) == itemId))
                {
                    try { _mSopFijar?.Invoke(null, new object[] { placed, false }); } catch { }
                    _fPuesto?.SetValue(holder, null);
                }
            }
            else if (_soporteLamparaType != null && _soporteLamparaType.IsInstanceOfType(holder))
            {
                var placed = _fLampara?.GetValue(holder) as ShipItem;
                if (placed != null && (itemId == 0 || IdOf(placed) == itemId))
                {
                    try { _mLamSujetar?.Invoke(null, new object[] { placed, false }); } catch { }
                    _fLampara?.SetValue(holder, null);
                }
            }
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !SyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.SailorsKeepsakes) == 0) return;
            try
            {
                if (_fTodos != null && _fPuestas != null && _fTodos.GetValue(null) is IEnumerable esps)
                {
                    foreach (var obj in esps)
                    {
                        var holder = obj as ShipItem;
                        int hid = IdOf(holder);
                        if (hid == 0) continue;
                        var arr = _fPuestas.GetValue(holder) as ShipItem[];
                        if (arr == null) continue;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            int iid = IdOf(arr[i]);
                            if (iid != 0)
                                peer.Send(new KeepsakeStateMsg { Kind = KeepsakeKind.RackAttach, HolderInstanceId = hid, ItemInstanceId = iid, Slot = (sbyte)i }, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
                if (_fSoportes != null && _fPuesto != null && _fSoportes.GetValue(null) is IEnumerable sops)
                {
                    foreach (var obj in sops)
                    {
                        var holder = obj as ShipItem;
                        int hid = IdOf(holder);
                        var placed = holder != null ? _fPuesto.GetValue(holder) as ShipItem : null;
                        int iid = IdOf(placed);
                        if (hid != 0 && iid != 0)
                            peer.Send(new KeepsakeStateMsg { Kind = KeepsakeKind.RackAttach, HolderInstanceId = hid, ItemInstanceId = iid, Slot = 0 }, DeliveryMethod.ReliableOrdered);
                    }
                }
                if (_soporteLamparaType != null && _fLampara != null)
                {
                    foreach (var obj in UnityEngine.Object.FindObjectsOfType(_soporteLamparaType))
                    {
                        var holder = obj as ShipItem;
                        int hid = IdOf(holder);
                        var placed = holder != null ? _fLampara.GetValue(holder) as ShipItem : null;
                        int iid = IdOf(placed);
                        if (hid != 0 && iid != 0)
                            peer.Send(new KeepsakeStateMsg { Kind = KeepsakeKind.RackAttach, HolderInstanceId = hid, ItemInstanceId = iid, Slot = 0 }, DeliveryMethod.ReliableOrdered);
                    }
                }
                if (_incensoType != null && _pIncensoEncendido != null)
                {
                    foreach (var obj in UnityEngine.Object.FindObjectsOfType(_incensoType))
                    {
                        var inc = obj as ShipItem;
                        int hid = IdOf(inc);
                        if (hid != 0 && (bool)_pIncensoEncendido.GetValue(inc, null))
                            peer.Send(new KeepsakeStateMsg { Kind = KeepsakeKind.IncenseToggle, HolderInstanceId = hid, Flag = true }, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SailorsKeepsakesCompat] ResyncTo: " + e.Message);
            }
        }
    }
}
