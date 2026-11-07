using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
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

        private readonly Transform _boundBoat;
        private readonly ushort _boatId = BoatLocator.NoBoat;
        private uint _layoutHash;
        private readonly BoatContexts<InteractionSync> _fleet;
        private readonly HatchPreview _hatchPreview;

        private readonly CoopNet _net;

        private Transform _cachedBoat;
        private GoPointerButton[] _buttons = Array.Empty<GoPointerButton>();
        private readonly Dictionary<GoPointerButton, int> _index = new Dictionary<GoPointerButton, int>();
        private readonly Dictionary<GPButtonTrapdoor, DeferredToggle> _hatchTargets = new Dictionary<GPButtonTrapdoor, DeferredToggle>();
        private bool _sentInitialHatches;
        private sealed class QueuedHatchState
        {
            public uint LayoutHash;
            public readonly DeferredToggle Target = new DeferredToggle();
        }
        private readonly Dictionary<ushort, Dictionary<ushort, QueuedHatchState>> _pendingHatches
            = new Dictionary<ushort, Dictionary<ushort, QueuedHatchState>>();

        private GoPointer _hostPointer;
        private bool _replaying;          // guard so the host's replay doesn't re-forward
        private GoPointer _gp;
        private FieldInfo _fHeldItem;
        private DeckForceBatch _push;
        private ushort _pushIndex;
        private float _lastPushFixedTime;
        private readonly DeckForcePlayback _forces;
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

                if (_fleet != null) return _fleet.Describe(c => "boat " + c._boatId + ": " + c.LastEventText);
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
        public int ButtonCount { get { if (_fleet == null) return _buttons.Length; int n = 0; foreach (var c in _fleet.Values) n += c.ButtonCount; return n; } }

        public InteractionSync(CoopNet net)
        {
            _net = net;
            Instance = this;
            _hatchPreview = new HatchPreview();
            _forces = new DeckForcePlayback();
            _fleet = new BoatContexts<InteractionSync>((boat, id) => new InteractionSync(net, boat, id), c => c.Clear(),
                boat => BoatLayout.Stamp(SharedButtons(boat)));
        }

        private InteractionSync(CoopNet net, Transform boat, ushort id)
        {
            _net = net; _boundBoat = boat; _boatId = id;
            RefreshButtons();
        }

        /// <summary>
        /// True if this interaction must NOT run on the local machine: a connected client
        /// triggering a HOST-ONLY action (sleep/time, economy, missions, save). The Harmony
        /// prefix skips the original handler so the guest can't affect the host's world.
        /// </summary>
        public static bool ShouldBlockLocally(GoPointerButton btn)
        {
            var self = Instance;
            if (self == null || btn == null) return false;
            if (self._fleet != null)
                foreach (var context in self._fleet.Existing) if (context._replaying) return false;
            if (self._net.Role != Role.Client || self._net.State != LinkState.Connected) return false;
            // The real hatch awaits the host; the click postfix starts a visual preview.
            // Harmony still runs the postfix when this prefix skips the original method.
            // A hatch outside the owned fleet has no host replay, so it stays a local toggle.
            if (btn is GPButtonTrapdoor) return self.ForButton(btn) != null;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.HostOnly) return false;

            self.Remember("block(host-only) '" + ButtonLabel(btn) + "'");
            return true;
        }

        public static bool ShouldBlockPushWhileHolding(GoPointerButton btn)
        {
            var self = Instance;
            if (self == null || btn == null) return false;
            if (self._net.State != LinkState.Connected || !IsPushButton(btn)) return false;
            if (self._net.Role != Role.Client) return false;
            if (!self.HasHeldItem()) return false;

            self.RememberPush("block held '" + ButtonLabel(btn) + "'");
            return true;
        }

        // -----------------------------------------------------------------
        // Boat binding (both roles need a consistent button index)
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_fleet != null)
            {
                foreach (var c in _fleet.Values)
                {
                    ApplyQueuedHatches(c);
                    c.Tick(dt);
                }
                _hatchPreview.Tick();
                return;
            }
            if (_net.State != LinkState.Connected) return;
            RefreshButtons();
            if (_net.Role == Role.Client) ApplyHatchTargets();
            else if (_net.Role == Role.Host && !_sentInitialHatches)
            {
                SendInitialHatches();
                _sentInitialHatches = true;
            }
            if (_net.Role == Role.Client && _push.Seconds > 0f &&
                Time.fixedTime - _lastPushFixedTime >= 2f * Time.fixedDeltaTime - 0.0001f) FlushPush();
        }

        private static GoPointerButton[] SharedButtons(Transform boat)
        {
            var list = new List<GoPointerButton>();
            foreach (var button in boat.GetComponentsInChildren<GoPointerButton>(true))
                if (button != null && InteractionPolicy.Classify(button) == InteractPolicy.Shared &&
                    !(button is PickupableBoatMooringRope) && !InteractionPolicy.IsSleepInput(button)) list.Add(button);
            return list.ToArray();
        }

        private InteractionSync ForButton(GoPointerButton btn)
        {
            if (btn == null) return null;
            foreach (var context in _fleet.Values) if (context._index.ContainsKey(btn)) return context;
            return null;
        }

        private void RefreshButtons()
        {
            Transform boat = _boundBoat;
            if (boat == _cachedBoat) return;

            _cachedBoat = boat;
            _index.Clear();
            if (boat == null)
            {
                _buttons = Array.Empty<GoPointerButton>();
                return;
            }

            _buttons = SharedButtons(boat);
            _layoutHash = BoatLayout.Hash(boat, _buttons);
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
            if (InteractionPolicy.IsSleepInput(btn)) return;
            if (InteractionContext.Suppressed) return;
            if (_fleet != null) { ForButton(btn)?.NotifyLocalInteract(btn, kind); return; }
            if (_replaying || !InteractionContext.HasInput) return;
            if ((_net.Role != Role.Client && _net.Role != Role.Host) || _net.State != LinkState.Connected) return;
            if (btn == null) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;  // only SHARED is forwarded
            if (IsExcluded(btn, kind)) return;

            RefreshButtons();
            if (!_index.TryGetValue(btn, out int idx)) return;   // not a button on the shared boat

            if (btn is GPButtonTrapdoor hatch)
            {
                // Only the no-argument overload toggles a hatch. GoPointer also calls
                // the empty pointer overload during the same click; don't send it twice.
                if (kind != InteractKind.ActivateNoArg) return;
                if (_net.Role == Role.Host) { SendHatchState(hatch, idx); return; }
                try { if (!Instance._hatchPreview.Begin(hatch)) return; }
                catch (Exception e) { Plugin.Logger.LogWarning("[HatchPreview] " + e.Message); }
            }

            _net.Broadcast(new ControlEventMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = (ushort)idx, Kind = kind },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out " + kind + " #" + idx + " '" + ButtonLabel(btn) + "'");
        }

        // -----------------------------------------------------------------
        // Host: replay a client's interaction authoritatively
        // -----------------------------------------------------------------

        public void OnControlEvent(ControlEventMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (_net.IsHostPeer(fromPeer) && msg.Kind == InteractKind.HatchState)
                {
                    QueueHatchState(msg.BoatIndex, msg.LayoutHash, msg.Index, msg.HatchOpen, interaction: true);
                    return;
                }
                if (!GameState.playing || GameState.currentlyLoading) return;
                if (_net.Role == Role.Host)
                { if (_net.PlayerNetIdForPeer(fromPeer) == 0) return; }
                else if (!_net.IsHostPeer(fromPeer)) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "InteractionSync")) c.OnControlEvent(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Host && !(_net.Role == Role.Client && _net.IsHostPeer(fromPeer))) return;
            RefreshButtons();

            int i = msg.Index;
            if (i < 0 || i >= _buttons.Length) return;
            var btn = _buttons[i];
            if (btn == null) return;
            if (btn is GPButtonTrapdoor hatch)
            {
                if (_net.Role == Role.Client)
                {
                    if (msg.Kind != InteractKind.HatchState) return;
                    ReceiveHatchTarget((ushort)i, msg.HatchOpen, interaction: true);
                    return;
                }
                if (msg.Kind != InteractKind.ActivateNoArg) return;
                _replaying = true;
                try { hatch.OnActivate(); }
                catch (Exception e) { Plugin.Logger.LogWarning("[InteractionSync] Hatch request boat=" + _boatId + " index=" + i + ": " + e); }
                finally { _replaying = false; }
                // An in-motion or denied toggle returns the actual target instead of
                // pretending the request changed it.
                SendHatchState(hatch, i);
                return;
            }
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
                if (_net.Role == Role.Host)
                {
                    _net.RelayExcept(msg, fromPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
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

        private void SendHatchState(GPButtonTrapdoor hatch, int index, LiteNetLib.NetPeer peer = null)
        {
            var state = new ControlEventMsg { BoatIndex = _boatId, LayoutHash = _layoutHash,
                Index = (ushort)index, Kind = InteractKind.HatchState, HatchOpen = hatch.IsOpen() };
            if (peer == null) _net.Broadcast(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
            else peer.Send(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>One baseline for a joining peer or newly bound hull; no interaction is invented.</summary>
        public void SendInitialHatches(LiteNetLib.NetPeer peer = null)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            if (_fleet != null) { foreach (var c in _fleet.Values) c.SendInitialHatches(peer); return; }
            for (int i = 0; i < _buttons.Length; i++)
            {
                if (!(_buttons[i] is GPButtonTrapdoor hatch)) continue;
                var state = new HatchSnapshotMsg { BoatIndex = _boatId, LayoutHash = _layoutHash,
                    Index = (ushort)i, Open = hatch.IsOpen() };
                if (peer == null) _net.Broadcast(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
                else peer.Send(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        public void OnHatchSnapshot(HatchSnapshotMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (!_net.IsHostPeer(fromPeer)) return;
            QueueHatchState(msg.BoatIndex, msg.LayoutHash, msg.Index, msg.Open, interaction: false);
        }

        private void QueueHatchState(ushort boat, uint layout, ushort index, bool open, bool interaction)
        {
            if (!_pendingHatches.TryGetValue(boat, out var hatches))
                _pendingHatches[boat] = hatches = new Dictionary<ushort, QueuedHatchState>();
            if (!hatches.TryGetValue(index, out var state))
                hatches[index] = state = new QueuedHatchState();
            state.LayoutHash = layout;
            state.Target.Receive(open, interaction); // A baseline must not erase a queued real interaction.
        }

        private void ApplyQueuedHatches(InteractionSync context)
        {
            if (!_pendingHatches.TryGetValue(context._boatId, out var hatches)) return;
            foreach (var pair in hatches)
            {
                BoatLayout.Matches(context._boatId, pair.Value.LayoutHash, context._layoutHash, "InteractionSync");
                context.ReceiveHatchTarget(pair.Key, pair.Value.Target.Target, pair.Value.Target.HasInteraction);
            }
            _pendingHatches.Remove(context._boatId);
        }

        private void ReceiveHatchTarget(ushort index, bool open, bool interaction)
        {
            if (index >= _buttons.Length || !(_buttons[index] is GPButtonTrapdoor hatch)) return;
            if (!_hatchTargets.TryGetValue(hatch, out var target))
                _hatchTargets[hatch] = target = new DeferredToggle();
            target.Target = open;
            if (interaction)
            {
                Instance._hatchPreview.Receive(hatch, open);
                Remember("in hatch #" + index + " open=" + open);
            }
        }

        private void ApplyHatchTargets()
        {
            if (_hatchTargets.Count == 0) return;
            var done = new List<GPButtonTrapdoor>();
            _replaying = true;
            try
            {
                foreach (var pair in _hatchTargets)
                    if (pair.Key == null || pair.Value.TryApply(pair.Key.IsOpen, pair.Key.OnActivate))
                        done.Add(pair.Key);
            }
            finally { _replaying = false; }
            foreach (var hatch in done) _hatchTargets.Remove(hatch);
        }

        /// <summary>Host: apply one continuous push sample to the authoritative rigidbody.</summary>
        public void OnPushRequest(PushRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading) return;
                if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(fromPeer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "InteractionSync")) c.OnPushRequest(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Host) return;
            RefreshButtons();

            int i = msg.Index;
            // A dock push targets this hull directly: the guest's dock collider is trusted,
            // so the host does not need to find (or have loaded) a matching one.
            bool dockPush = msg.Index == ushort.MaxValue;
            GoPointerButton btn = null;
            if (!dockPush)
            {
                if (i >= _buttons.Length) return;
                btn = _buttons[i];
                if (btn == null || !IsPushButton(btn)) return;
            }

            Rigidbody body = dockPush || btn is DockPushCol
                ? (_boundBoat.GetComponent<Rigidbody>() ?? _boundBoat.GetComponentInParent<Rigidbody>())
                : PushTargetBody(btn);
            if (body == null) return;

            Instance._forces.Enqueue(_net.PlayerNetIdForPeer(fromPeer), _boundBoat, body, msg);
            RememberPush("in #" + i + " '" + (dockPush ? "dock" : ButtonLabel(btn)) + "'");
        }

        internal void OnOarStroke(OarStrokeMsg msg, LiteNetLib.NetPeer peer)
        {
            if (_net.Role != Role.Host || !GameState.playing || GameState.currentlyLoading) return;
            uint actor = _net.PlayerNetIdForPeer(peer);
            if (actor == 0) return;
            var hull = BoatLocator.FindByIndex(msg.BoatIndex);
            if (hull == null) return;
            var oar = CoopBehaviour.Instance?.Items?.FindOar(msg.InstanceId, msg.PrefabIndex);
            if (oar == null) return;
            var body = BoatForceBody(hull);
            if (body != null) _forces.Enqueue(actor, hull, body, msg, oar);
        }

        internal void ApplyForces(float dt)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading) return;
            _forces.Apply(dt);
        }

        internal void ClearForceActor(uint actor) => _forces.RemoveActor(actor);

        internal static Rigidbody BoatForceBody(Transform hull)
            => hull != null ? hull.GetComponent<Rigidbody>() ?? hull.GetComponentInParent<Rigidbody>() : null;

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
            string method = kind == InteractKind.AltActivate ? "OnAltActivate" : "OnActivate";
            var args = kind == InteractKind.ActivateNoArg ? Type.EmptyTypes : new[] { typeof(GoPointer) };
            var effective = btn.GetType().GetMethod(method, args);
            if (effective == null || effective.DeclaringType == typeof(GoPointerButton)) return true;
            if (kind != InteractKind.Activate) return false;
            return btn is GPButtonRopeWinch
                || btn is GPButtonSteeringWheel
                || btn is BilgePump
                || btn is PickupableItem;
        }

        internal void CapturePush(GoPointerButton button, Rigidbody body, Vector3 force, Vector3 point)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || !GameState.playing ||
                GameState.currentlyLoading || InteractionContext.Suppressed || force.sqrMagnitude == 0f) return;
            InteractionSync context;
            ushort index;
            if (button is DockPushCol)
            {
                ushort boat = BoatLocator.IndexOf(GameState.currentBoat);
                if (boat == BoatLocator.NoBoat) return;
                context = _fleet.Get(boat);
                index = ushort.MaxValue;
            }
            else
            {
                context = ForButton(button);
                if (context == null || !context._index.TryGetValue(button, out int found)) return;
                index = (ushort)found;
            }
            if (context == null || context._boundBoat == null) return;
            if (context._pushIndex != index) context.FlushPush();
            context._pushIndex = index;
            context._lastPushFixedTime = Time.fixedTime;
            DeckForcePlayback.Accumulate(ref context._push, context._boundBoat, body, force, point, Time.fixedDeltaTime);
            if (context._push.Seconds >= 1f / PushHz) context.FlushPush();
        }

        private void FlushPush()
        {
            if (_push.Seconds <= 0f) return;
            var msg = new PushRequestMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = _pushIndex };
            _push.Write(msg);
            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            _push = default;
            RememberPush("out #" + _pushIndex);
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

        public void InvalidateHull(ushort id) { _forces?.RemoveBoat(id); _fleet?.Invalidate(id); }

        public void Clear()
        {
            if (_fleet != null) { _forces.Clear(); _fleet.Clear(); _pendingHatches.Clear(); _hatchPreview.Clear(); return; }
            foreach (var button in _buttons)
                if (button is GPButtonTrapdoor hatch) Instance._hatchPreview.Remove(hatch);
            _cachedBoat = null;
            _buttons = Array.Empty<GoPointerButton>();
            _index.Clear();
            _hostPointer = null;
            _hatchTargets.Clear();
            _sentInitialHatches = false;
            _replaying = false;
            _gp = null;
            _push = default;
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
            var hooks = new PatchHookCatalog();
            PatchAll(harmony, hooks, "OnActivate", nameof(PostActivateNoArg), Type.EmptyTypes);
            PatchAll(harmony, hooks, "OnActivate", nameof(PostActivate));
            PatchAll(harmony, hooks, "OnAltActivate", nameof(PostAltActivate));
            // No-arg host-only handlers run BEFORE the empty pointer overload in GoPointer.
            // Prefix only: this entry must not invent an extra interaction event.
            foreach (var input in InteractionActionCatalog.HostOnlyInputs)
            {
                if (!InteractionPolicy.IsHostOnlyType(input.TypeName))
                    throw new InvalidOperationException("Host-only catalog/policy mismatch: " + input.TypeName);
                hooks.Install(typeof(GoPointerButton).Assembly.GetType(input.TypeName), input.Method,
                    Type.EmptyTypes, target => harmony.Patch(target, prefix: Callback(nameof(PreBlock))));
            }
            foreach (var type in new[] { typeof(GPButtonBoatPushCol), typeof(DockPushCol), typeof(GPButtonSailPusher) })
                hooks.Install(type, "ExtraFixedUpdate", Type.EmptyTypes,
                    target => harmony.Patch(target, prefix: Callback(nameof(PrePushFixedUpdate)), transpiler: Callback(nameof(CapturePushForces))));
            PatchHealth.Report("Interactions", hooks);
            Plugin.Logger.LogInfo("[InteractionPatches] role=initializing " + hooks.Detail);
        }

        private static HarmonyMethod Callback(string name)
        {
            var method = typeof(InteractionPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(typeof(InteractionPatches).Name, name);
            return new HarmonyMethod(method);
        }

        private static void PatchAll(Harmony harmony, PatchHookCatalog hooks, string gameMethod,
            string postfixName, Type[] args = null)
        {
            if (args == null) args = new[] { typeof(GoPointer) };
            var postfix = Callback(postfixName);
            var prefix = Callback(nameof(PreBlock));
            foreach (var type in typeof(GoPointerButton).Assembly.GetTypes())
            {
                if (!typeof(GoPointerButton).IsAssignableFrom(type)) continue;
                var target = type.GetMethod(gameMethod, BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly, null, args, null);
                if (target == null || target.IsAbstract) continue;
                hooks.Install(type, gameMethod, args,
                    method => harmony.Patch(method, prefix: prefix, postfix: postfix));
            }
        }

        // The first GoPointer parameter is __0 (its name varies across overrides).
        private static readonly CoopLog.Repeat[] Errors = new CoopLog.Repeat[7];
        private static void ReportPatch(int site, string name, GoPointerButton btn, Exception error)
        {
            Plugin.Logger?.ReportError("[InteractionPatches] " + name,
                "role=" + CoopBehaviour.Instance?.Net?.Role + " type=" +
                (ReferenceEquals(btn, null) ? "null" : btn.GetType().Name) + " reason=" + error,
                ref Errors[site]);
        }

        private static void PostActivateNoArg(GoPointerButton __instance)
            => PatchGuard.Run(() => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.ActivateNoArg),
                e => ReportPatch(0, nameof(PostActivateNoArg), __instance, e));

        private static void PostActivate(GoPointerButton __instance)
            => PatchGuard.Run(() => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.Activate),
                e => ReportPatch(1, nameof(PostActivate), __instance, e));

        private static void PostAltActivate(GoPointerButton __instance)
            => PatchGuard.Run(() => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.AltActivate),
                e => ReportPatch(2, nameof(PostAltActivate), __instance, e));

        /// <summary>Block HOST-ONLY actions on the client: returning false skips the original handler.</summary>
        private static bool PreBlock(GoPointerButton __instance)
            => PatchGuard.Prefix(() => !InteractionSync.ShouldBlockLocally(__instance),
                e => ReportPatch(4, nameof(PreBlock), __instance, e));

        // Every physics step for every push collider: a plain try/catch, not PatchGuard (no closure).
        private static bool PrePushFixedUpdate(GoPointerButton __instance)
        {
            try { return !InteractionSync.ShouldBlockPushWhileHolding(__instance); }
            catch (Exception e) { PatchGuard.Fail(x => ReportPatch(5, nameof(PrePushFixedUpdate), __instance, x), e); return true; }
        }

        private static IEnumerable<CodeInstruction> CapturePushForces(IEnumerable<CodeInstruction> instructions)
        {
            var force = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition), new[] { typeof(Vector3), typeof(Vector3) });
            var forceWithMode = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition), new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) });
            int calls = 0;
            foreach (var instruction in instructions)
            {
                bool withMode = instruction.Calls(forceWithMode);
                if (!withMode && !instruction.Calls(force)) { yield return instruction; continue; }
                var loadButton = new CodeInstruction(OpCodes.Ldarg_0);
                loadButton.labels.AddRange(instruction.labels);
                loadButton.blocks.AddRange(instruction.blocks);
                yield return loadButton;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InteractionPatches),
                    withMode ? nameof(ApplyPushWithMode) : nameof(ApplyPush)));
                calls++;
            }
            if (calls != 1) throw new InvalidOperationException("Expected one AddForceAtPosition in the push control, found " + calls);
        }

        private static void ApplyPush(Rigidbody body, Vector3 force, Vector3 point, GoPointerButton button)
            => ApplyPushWithMode(body, force, point, ForceMode.Force, button);

        private static void ApplyPushWithMode(Rigidbody body, Vector3 force, Vector3 point, ForceMode mode, GoPointerButton button)
        {
            body.AddForceAtPosition(force, point, mode);
            if (mode != ForceMode.Force) return;
            try { InteractionSync.Instance?.CapturePush(button, body, force, point); }
            catch (Exception e) { PatchGuard.Fail(x => ReportPatch(5, nameof(ApplyPushWithMode), button, x), e); }
        }

    }
}
