using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 2 — general shared interaction. Everything a player can click on the boat is a
    /// <see cref="GoPointerButton"/>: cleats, quick-release levers, mooring lines, hatches, etc.
    /// Continuous drags (winch/wheel) already flow as values through <see cref="ControlsSync"/>;
    /// this handles the <i>discrete</i> press/alt-press interactions that don't.
    ///
    /// <para>The flow follows F3 (request → host applies → state syncs back): when the local
    /// client triggers a button, a Harmony postfix forwards a <see cref="ControlEventMsg"/> with
    /// the button's index (its position in the boat's <c>GetComponentsInChildren&lt;GoPointerButton&gt;</c>
    /// order — identical on both machines). The host looks the button up and <b>replays the same
    /// handler</b>, so the authoritative game logic runs host-side and the result reaches everyone
    /// through the normal control/state broadcasts.</para>
    ///
    /// <para>Two safety exclusions on the <c>Activate</c> (primary) path, because replaying it on
    /// the host with the host's own pointer would hijack the host: continuous grabs
    /// (<c>GPButtonRopeWinch</c>/<c>GPButtonSteeringWheel</c>, handled by the value channel) and
    /// pickups (<c>PickupableItem</c>, which would put the item in the host's hands). Their
    /// <c>AltActivate</c> (untie/unmoor/quick-release) is still forwarded.</para>
    /// </summary>
    public sealed class InteractionSync
    {
        public static InteractionSync Instance { get; private set; }

        private readonly CoopNet _net;
        private PlayerEmbarkerNew _emb;

        private Transform _cachedBoat;
        private GoPointerButton[] _buttons = Array.Empty<GoPointerButton>();
        private readonly Dictionary<GoPointerButton, int> _index = new Dictionary<GoPointerButton, int>();

        private GoPointer _hostPointer;
        private bool _replaying;          // guard so the host's replay doesn't re-forward
        private GoPointer _gp;
        private FieldInfo _fClicked;
        private FieldInfo _fHeldItem;
        private float _pushTimer;
        private float _pushAccumDt;
        private string _lastPush = "—";
        private long _lastPushTick;
        private const float PushHz = 20f;

        // Overlay diagnostics.
        private string _lastEvent = "—";
        private long _lastEventTick;
        public string LastEventText
        {
            get
            {
                if (_lastEventTick == 0) return "—";
                long age = _net.Clock.ServerTick - _lastEventTick;
                if (age < 0) age = 0;
                string push = "—";
                if (_lastPushTick != 0)
                {
                    long pushAge = _net.Clock.ServerTick - _lastPushTick;
                    if (pushAge < 0) pushAge = 0;
                    push = _lastPush + " " + pushAge + "ms";
                }
                return _lastEvent + " " + age + "ms" + " · push " + push;
            }
        }
        public int ButtonCount => _buttons.Length;

        public InteractionSync(CoopNet net) { _net = net; Instance = this; }

        /// <summary>
        /// True if this interaction must NOT run on the local machine: a connected client
        /// triggering a HOST-ONLY action (sleep/time, economy, missions, save). The Harmony
        /// prefix skips the original handler so the guest can't affect the host's world.
        /// </summary>
        public static bool ShouldBlockLocally(GoPointerButton btn)
        {
            var self = Instance;
            if (self == null || btn == null) return false;
            if (self._net.Role != Role.Client || self._net.State != LinkState.Connected) return false;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.HostOnly) return false;

            self.Remember("block(host-only) '" + ButtonLabel(btn) + "'");
            return true;
        }

        public static bool ShouldBlockPushWhileHolding(GoPointerButton btn)
        {
            var self = Instance;
            if (self == null || btn == null) return false;
            if (self._net.Role != Role.Client || self._net.State != LinkState.Connected) return false;
            if (!IsPushButton(btn)) return false;
            if (!self.HasHeldItem()) return false;

            self.RememberPush("block held '" + ButtonLabel(btn) + "'");
            return true;
        }

        // -----------------------------------------------------------------
        // Boat binding (both roles need a consistent button index)
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            RefreshButtons();
            ForwardPushRequests(dt);
        }

        private void RefreshButtons()
        {
            if (_emb == null) _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat == _cachedBoat) return;

            _cachedBoat = boat;
            _index.Clear();
            if (boat == null)
            {
                _buttons = Array.Empty<GoPointerButton>();
                return;
            }

            _buttons = boat.GetComponentsInChildren<GoPointerButton>(true);
            for (int i = 0; i < _buttons.Length; i++)
                if (_buttons[i] != null) _index[_buttons[i]] = i;

            Plugin.Logger.LogInfo("[InteractionSync] Boat changed: buttons=" + _buttons.Length +
                                  (boat != null ? " ('" + boat.name + "')" : ""));
        }

        // -----------------------------------------------------------------
        // Client: a local interaction happened (called from the Harmony postfix)
        // -----------------------------------------------------------------

        /// <summary>
        /// Forward the local player's discrete interaction to the host. No-op unless we're a
        /// connected client doing this for real (not while replaying a remote event).
        /// </summary>
        public void NotifyLocalInteract(GoPointerButton btn, InteractKind kind)
        {
            if (_replaying) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (btn == null) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;  // only SHARED is forwarded
            if (IsExcluded(btn, kind)) return;

            RefreshButtons();
            if (!_index.TryGetValue(btn, out int idx)) return;   // not a button on the shared boat

            _net.Broadcast(new ControlEventMsg { Index = (ushort)idx, Kind = kind },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out " + kind + " #" + idx + " '" + ButtonLabel(btn) + "'");
        }

        /// <summary>
        /// Forward held-button transitions. This is intentionally narrower than the generic
        /// click relay: only known held mechanics are sent, and the host applies domain logic
        /// instead of replaying <c>OnActivate</c> with its own pointer.
        /// </summary>
        public void NotifyLocalHold(GoPointerButton btn, InteractKind kind, bool down)
        {
            if (_replaying) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (btn == null || !HasHeldChannel(btn, kind)) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;

            RefreshButtons();
            if (!_index.TryGetValue(btn, out int idx)) return;

            _net.Broadcast(new HoldRequestMsg { Index = (ushort)idx, Kind = kind, Down = down },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out hold " + (down ? "down" : "up") + " #" + idx + " '" + ButtonLabel(btn) + "'");
        }

        // -----------------------------------------------------------------
        // Host: replay a client's interaction authoritatively
        // -----------------------------------------------------------------

        public void OnControlEvent(ControlEventMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshButtons();

            int i = msg.Index;
            if (i < 0 || i >= _buttons.Length) return;
            var btn = _buttons[i];
            if (btn == null) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;
            if (IsExcluded(btn, msg.Kind)) return;   // never replay something we'd never forward

            string method = msg.Kind == InteractKind.AltActivate ? "OnAltActivate" : "OnActivate";
            Remember("in " + msg.Kind + " #" + i + " '" + ButtonLabel(btn) + "'");

            try
            {
                Type[] args = msg.Kind == InteractKind.ActivateNoArg ? Type.EmptyTypes : new[] { typeof(GoPointer) };
                object[] invokeArgs = msg.Kind == InteractKind.ActivateNoArg ? null : new object[] { HostPointer() };
                var mi = btn.GetType().GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, args, null);
                if (mi == null)
                {
                    Plugin.Logger.LogWarning("[InteractionSync] '" + btn.GetType().Name + "' missing " + method +
                                             (msg.Kind == InteractKind.ActivateNoArg ? "()" : "(GoPointer)"));
                    return;
                }
                _replaying = true;
                mi.Invoke(btn, invokeArgs);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[InteractionSync] Replay error " + method + " on '" +
                                         btn.GetType().Name + "': " + e.Message);
            }
            finally
            {
                _replaying = false;
            }
        }

        /// <summary>Host: apply a client's held interaction request through the owning sync domain.</summary>
        public void OnHoldRequest(HoldRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshButtons();

            int i = msg.Index;
            if (i < 0 || i >= _buttons.Length) return;
            var btn = _buttons[i];
            if (btn == null) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;
            if (!HasHeldChannel(btn, msg.Kind)) return;

            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (btn is BilgePump)
            {
                BoatDamageSync.Instance?.SetRemotePump(msg.Index, msg.Down, actor);
                Remember("in hold " + (msg.Down ? "down" : "up") + " #" + i + " '" + ButtonLabel(btn) + "'");
            }
        }

        /// <summary>Host: apply one continuous push sample to the authoritative rigidbody.</summary>
        public void OnPushRequest(PushRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            if (!CoordSpace.Ready) return;
            RefreshButtons();

            int i = msg.Index;
            if (i < 0 || i >= _buttons.Length) return;
            var btn = _buttons[i];
            if (btn == null || !IsPushButton(btn)) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;

            Rigidbody body = PushTargetBody(btn);
            if (body == null) return;

            Vector3 pos = CoordSpace.RealToLocal(msg.RealPos);
            float dt = Mathf.Clamp(msg.DeltaTime, 0.001f, 0.2f);
            body.AddForceAtPosition(msg.Force * dt, pos, ForceMode.Impulse);
            RememberPush("in #" + i + " '" + ButtonLabel(btn) + "'");
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Whether this interaction is handled elsewhere or is unsafe to replay host-side.
        /// Primary-click grabs of winch/wheel go through the value channel; primary-click pickups
        /// would put the item in the host's hands. Their alt action is still allowed.
        /// </summary>
        private static bool IsExcluded(GoPointerButton btn, InteractKind kind)
        {
            if (kind != InteractKind.Activate) return false;
            return btn is GPButtonRopeWinch
                || btn is GPButtonSteeringWheel
                || btn is BilgePump
                || btn is PickupableItem;
        }

        private static bool HasHeldChannel(GoPointerButton btn, InteractKind kind)
        {
            return kind == InteractKind.Activate && btn is BilgePump;
        }

        private void ForwardPushRequests(float dt)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;
            if (HasHeldItem())
            {
                _pushAccumDt = 0f;
                _pushTimer = 0f;
                return;
            }

            _pushAccumDt += dt;
            _pushTimer += dt;
            float interval = 1f / PushHz;
            if (_pushTimer < interval) return;
            _pushTimer = 0f;

            GoPointerButton btn = ClickedButton();
            if (btn == null || !IsPushButton(btn))
            {
                _pushAccumDt = 0f;
                return;
            }
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared)
            {
                _pushAccumDt = 0f;
                return;
            }

            RefreshButtons();
            if (!_index.TryGetValue(btn, out int idx))
            {
                _pushAccumDt = 0f;
                return;
            }

            if (!TryBuildPush(btn, out Vector3 force, out Vector3 atPos))
            {
                _pushAccumDt = 0f;
                return;
            }

            float sampleDt = Mathf.Clamp(_pushAccumDt, 0.001f, 0.2f);
            _pushAccumDt = 0f;

            _net.Broadcast(new PushRequestMsg
            {
                Index = (ushort)idx,
                RealPos = CoordSpace.LocalToReal(atPos),
                Force = force,
                DeltaTime = sampleDt,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            RememberPush("out #" + idx + " '" + ButtonLabel(btn) + "'");
        }

        private GoPointerButton ClickedButton()
        {
            try
            {
                if (_gp == null) _gp = UnityEngine.Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return null;
                if (_fClicked == null)
                    _fClicked = typeof(GoPointer).GetField("clickedButton", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fClicked != null ? _fClicked.GetValue(_gp) as GoPointerButton : null;
            }
            catch { return null; }
        }

        private bool HasHeldItem()
        {
            try
            {
                if (_gp == null) _gp = UnityEngine.Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return false;
                if (_fHeldItem == null)
                    _fHeldItem = typeof(GoPointer).GetField("heldItem", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fHeldItem != null && _fHeldItem.GetValue(_gp) != null;
            }
            catch { return false; }
        }

        private GoPointer HostPointer()
        {
            if (_hostPointer == null) _hostPointer = UnityEngine.Object.FindObjectOfType<GoPointer>();
            return _hostPointer;
        }

        private bool TryBuildPush(GoPointerButton btn, out Vector3 force, out Vector3 atPos)
        {
            force = Vector3.zero;
            atPos = Vector3.zero;
            if (_gp == null) _gp = UnityEngine.Object.FindObjectOfType<GoPointer>();
            if (_gp == null) return false;

            Transform p = _gp.movement != null ? _gp.movement.transform : _gp.transform;
            atPos = p.position;

            if (btn is GPButtonSailPusher sail)
            {
                force = sail.pushForceMult * p.forward * 2.5f;
                return force.sqrMagnitude > 0.000001f;
            }

            if (btn is GPButtonBoatPushCol)
            {
                Rigidbody rb = PushTargetBody(btn);
                if (rb == null) return false;
                float speed = Mathf.Max(1f, rb.velocity.magnitude);
                float push = GetField(btn, "pushForceMult", 3f);
                float up = GetField(btn, "upForceMult", 1f);
                float vertical = GetField(btn, "verticalOffset", -2f);
                float swimMult = PlayerSwimming.swimming ? 0f : 1f;
                force = (push * rb.mass * p.forward + up * rb.mass * Vector3.up) * swimMult / speed;
                atPos = p.position + Vector3.up * vertical;
                return force.sqrMagnitude > 0.000001f;
            }

            if (btn is DockPushCol)
            {
                Rigidbody rb = GameState.currentBoat != null && GameState.currentBoat.parent != null
                    ? GameState.currentBoat.parent.GetComponent<Rigidbody>()
                    : null;
                if (rb == null) return false;
                float speed = Mathf.Max(1f, rb.velocity.magnitude);
                float push = GetField(btn, "pushForceMult", -0.55f);
                float up = GetField(btn, "upForceMult", 0f);
                float vertical = GetField(btn, "verticalOffset", 0f);
                force = (push * rb.mass * p.forward + up * rb.mass * Vector3.up) / speed;
                atPos = p.position + Vector3.up * vertical;
                return force.sqrMagnitude > 0.000001f;
            }

            return false;
        }

        private static bool IsPushButton(GoPointerButton btn)
        {
            return btn is GPButtonBoatPushCol || btn is DockPushCol || btn is GPButtonSailPusher;
        }

        private static Rigidbody PushTargetBody(GoPointerButton btn)
        {
            if (btn == null) return null;
            if (btn is GPButtonSailPusher)
            {
                var body = GetField<Rigidbody>(btn, "body");
                return body != null ? body : btn.transform.parent != null ? btn.transform.parent.GetComponent<Rigidbody>() : null;
            }
            if (btn is GPButtonBoatPushCol)
            {
                Transform t = btn.transform;
                if (t.parent != null && t.parent.parent != null)
                {
                    var rb = t.parent.parent.GetComponent<Rigidbody>();
                    if (rb != null) return rb;
                    if (t.parent.parent.parent != null) return t.parent.parent.parent.GetComponent<Rigidbody>();
                }
                return null;
            }
            if (btn is DockPushCol)
            {
                return GameState.currentBoat != null && GameState.currentBoat.parent != null
                    ? GameState.currentBoat.parent.GetComponent<Rigidbody>()
                    : null;
            }
            return null;
        }

        private static float GetField(object obj, string name, float fallback)
        {
            try
            {
                var fi = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return fi != null ? (float)fi.GetValue(obj) : fallback;
            }
            catch { return fallback; }
        }

        private static T GetField<T>(object obj, string name) where T : class
        {
            try
            {
                var fi = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return fi != null ? fi.GetValue(obj) as T : null;
            }
            catch { return null; }
        }

        private static string ButtonLabel(GoPointerButton btn)
        {
            if (btn == null) return "—";
            return string.IsNullOrEmpty(btn.lookText) ? btn.GetType().Name : btn.lookText;
        }

        private void Remember(string text)
        {
            _lastEvent = text;
            _lastEventTick = _net.Clock.ServerTick;
        }

        private void RememberPush(string text)
        {
            _lastPush = text;
            _lastPushTick = _net.Clock.ServerTick;
        }

        public void Clear()
        {
            _cachedBoat = null;
            _buttons = Array.Empty<GoPointerButton>();
            _index.Clear();
            _hostPointer = null;
            _replaying = false;
            _gp = null;
            _fClicked = null;
            _pushTimer = 0f;
            _pushAccumDt = 0f;
            _lastPush = "—";
            _lastPushTick = 0L;
            _lastEvent = "—";
            _lastEventTick = 0L;
        }
    }

    /// <summary>
    /// Harmony postfixes that turn every local <see cref="GoPointerButton"/> press into a forwarded
    /// event. Patching is done across the base type and all overrides so subclassed handlers are
    /// caught too. We only hook the discrete single-shot handlers (Activate / AltActivate); the
    /// held/continuous paths are covered by <see cref="ControlsSync"/>.
    /// </summary>
    public static class InteractionPatches
    {
        public static void Apply(Harmony harmony)
        {
            int onActivateNoArg = PatchAll(harmony, "OnActivate", nameof(PostActivateNoArg), Type.EmptyTypes);
            int onActivate = PatchAll(harmony, "OnActivate", nameof(PostActivate));
            int onAltActivate = PatchAll(harmony, "OnAltActivate", nameof(PostAltActivate));
            int onUnactivate = PatchAll(harmony, "OnUnactivate", nameof(PostUnactivate), withBlockPrefix: false);
            int push = 0;
            if (PatchPushFixedUpdate(harmony, typeof(GPButtonBoatPushCol))) push++;
            if (PatchPushFixedUpdate(harmony, typeof(DockPushCol))) push++;
            if (PatchPushFixedUpdate(harmony, typeof(GPButtonSailPusher))) push++;
            int ok = (onActivateNoArg > 0 ? 1 : 0) + (onActivate > 0 ? 1 : 0) +
                     (onAltActivate > 0 ? 1 : 0) + (onUnactivate > 0 ? 1 : 0) + push;
            SailwindCoop.Runtime.PatchHealth.Report("Interactions", ok, 7,
                "activate=" + onActivate + ", alt=" + onAltActivate + ", unactivate=" + onUnactivate + ", push=" + push + "/3");
        }

        private static bool PatchPushFixedUpdate(Harmony harmony, Type type)
        {
            try
            {
                var mi = type.GetMethod("ExtraFixedUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null)
                {
                    Plugin.Logger.LogWarning("[InteractionPatches] " + type.Name + " missing ExtraFixedUpdate");
                    return false;
                }
                var prefix = new HarmonyMethod(typeof(InteractionPatches).GetMethod(
                    nameof(PrePushFixedUpdate), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                Plugin.Logger.LogInfo("[InteractionPatches] " + type.Name + ".ExtraFixedUpdate: push-block patched");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[InteractionPatches] Failed to patch " + type.Name +
                                         ".ExtraFixedUpdate: " + e.Message);
                return false;
            }
        }

        private static int PatchAll(Harmony harmony, string gameMethod, string postfixName, Type[] args = null, bool withBlockPrefix = true)
        {
            if (args == null) args = new[] { typeof(GoPointer) };
            var postfix = new HarmonyMethod(typeof(InteractionPatches).GetMethod(
                postfixName, BindingFlags.Static | BindingFlags.NonPublic));
            var prefix = withBlockPrefix
                ? new HarmonyMethod(typeof(InteractionPatches).GetMethod(
                    nameof(PreBlock), BindingFlags.Static | BindingFlags.NonPublic))
                : null;

            int patched = 0;
            var baseType = typeof(GoPointerButton);
            foreach (var t in baseType.Assembly.GetTypes())
            {
                if (!baseType.IsAssignableFrom(t)) continue;
                MethodInfo mi;
                try
                {
                    mi = t.GetMethod(gameMethod,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, args, null);
                }
                catch { continue; }
                if (mi == null || mi.IsAbstract) continue;
                try
                {
                    harmony.Patch(mi, prefix: prefix, postfix: postfix);
                    patched++;
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[InteractionPatches] Failed to patch " +
                                             t.Name + "." + gameMethod + ": " + e.Message);
                }
            }
            string sig = args.Length == 0 ? "()" : "(GoPointer)";
            Plugin.Logger.LogInfo("[InteractionPatches] " + gameMethod + sig + ": patched " + patched + " methods");
            return patched;
        }

        // The first GoPointer parameter is __0 (its name varies across overrides).
        private static void PostActivateNoArg(GoPointerButton __instance)
            => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.ActivateNoArg);

        private static void PostActivate(GoPointerButton __instance)
        {
            InteractionSync.Instance?.NotifyLocalHold(__instance, InteractKind.Activate, down: true);
            InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.Activate);
        }

        private static void PostAltActivate(GoPointerButton __instance)
            => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.AltActivate);

        private static void PostUnactivate(GoPointerButton __instance)
            => InteractionSync.Instance?.NotifyLocalHold(__instance, InteractKind.Activate, down: false);

        /// <summary>Block HOST-ONLY actions on the client: returning false skips the original handler.</summary>
        private static bool PreBlock(GoPointerButton __instance)
            => !InteractionSync.ShouldBlockLocally(__instance);

        private static bool PrePushFixedUpdate(GoPointerButton __instance)
            => !InteractionSync.ShouldBlockPushWhileHolding(__instance);
    }
}
