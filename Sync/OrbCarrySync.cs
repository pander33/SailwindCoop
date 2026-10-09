using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed partial class WindTotemSync
    {
        public static WindTotemSync Instance { get; private set; }
        private sealed class Carry
        {
            internal WindTotemOrb Orb;
            internal readonly ChangeStream Stream = new ChangeStream();
            internal OrbStateMsg SentState;
            internal readonly ChangeStream RequestStream = new ChangeStream();
            internal OrbBody SentRequest;
            internal string Id, Parent;
            internal uint Holder, Revision, Request, AckActor, AckRequest;
            internal bool Slaved;
            // The remote copy's particles and sound are already off: OnDrop is not repeated every frame.
            internal bool Quiet;
            internal int OriginalLayer;
            internal readonly ItemStateGate Gate = new ItemStateGate();
            internal readonly ItemRequestOrder Order = new ItemRequestOrder();
            internal readonly NetTransform Pose = new NetTransform(), ParentPose = new NetTransform();
        }
        private readonly Dictionary<string, Carry> _orbs = new Dictionary<string, Carry>();
        private readonly Dictionary<string, OrbStateMsg> _waitingOrbs = new Dictionary<string, OrbStateMsg>();
        private readonly Dictionary<WindTotemOrb, Carry> _orbByInstance = new Dictionary<WindTotemOrb, Carry>();
        private uint _nextOrbRequest;
        private float _orbScan, _orbTimer;
        public string LastOrbAction { get; private set; } = "—";
        private static string Identity(Transform transform)
        {
            if (transform == null) return "";
            string path = "";
            for (var t = transform; t != null; t = t.parent)
            {
                int ordinal = 0;
                if (t.parent != null)
                { for (int i = 0; i < t.GetSiblingIndex(); i++) if (t.parent.GetChild(i).name == t.name) ordinal++; }
                else foreach (var root in t.gameObject.scene.GetRootGameObjects())
                { if (root.transform == t) break; if (root.name == t.name) ordinal++; }
                path = "/" + t.name + "[" + ordinal + "]" + path;
            }
            return transform.gameObject.scene.name + path;
        }
        private Carry BindOrb(WindTotemOrb orb)
        {
            if (orb == null || orb.totem == null || !orb.gameObject.scene.IsValid() || !orb.gameObject.scene.isLoaded) return null;
            if (_orbByInstance.TryGetValue(orb, out var bound) && bound.Orb == orb) return bound;
            string id = Identity(orb.transform), parent = Identity(orb.totem);
            if (_orbs.TryGetValue(id, out var carry)) { carry.Orb = orb; carry.Quiet = false; _orbByInstance[orb] = carry; return carry; }
            carry = new Carry { Orb = orb, Id = id, Parent = parent, OriginalLayer = orb.held != null ? 0 : orb.gameObject.layer };
            carry.Pose.InterpDelayMs = carry.ParentPose.InterpDelayMs = _net.Role == Role.Host ? 0 : 100;
            _orbs[id] = carry; _orbByInstance[orb] = carry;
            if (_net.Role == Role.Host) SendOrbState(carry, true);
            else if (_net.Role == Role.Client) _net.Broadcast(new OrbRequestMsg { OrbId = id, ParentId = parent, Action = OrbAction.Baseline }, DeliveryMethod.ReliableOrdered);
            return carry;
        }
        private Carry FindOrb(string id, string parent = null)
        {
            if (_orbs.TryGetValue(id, out var carry) && carry.Orb != null && (parent == null || carry.Parent == parent)) return carry;
            foreach (var orb in Resources.FindObjectsOfTypeAll<WindTotemOrb>())
                if (orb != null && orb.gameObject.scene.IsValid() && orb.gameObject.scene.isLoaded && Identity(orb.transform) == id &&
                    (parent == null || Identity(orb.totem) == parent)) return BindOrb(orb);
            return null;
        }
        private static bool Local(WindTotemOrb orb) => orb != null && orb.held != null && orb.held.GetHeldItem() == orb;
        internal void NotifyOrb(WindTotemOrb orb, bool held)
        {
            if (InteractionContext.Suppressed || _net.State != LinkState.Connected || !CoordSpace.Ready) return;
            var carry = BindOrb(orb); if (carry == null) return;
            RestoreOrb(carry); carry.Holder = held ? _net.MyNetId : 0; carry.Pose.Clear(); carry.ParentPose.Clear(); carry.Quiet = false;
            orb.gameObject.layer = held ? 2 : carry.OriginalLayer;
            if (_net.Role == Role.Client) SendOrbRequest(carry, held ? OrbAction.Pickup : OrbAction.Drop);
            else if (_net.Role == Role.Host) SendOrbState(carry, true);
            LastOrbAction = "out orb " + (held ? "pickup" : "drop") + " " + carry.Id;
        }
        private void CaptureOrb(Carry carry, OrbBody body)
        {
            body.OrbId = carry.Id; body.ParentId = carry.Parent; body.Tick = _net.Clock.ServerTick;
            body.Held = Local(carry.Orb) || carry.Holder != 0;
            body.RealPos = CoordSpace.LocalToReal(carry.Orb.transform.position); body.Rot = carry.Orb.transform.rotation;
            body.ParentReal = CoordSpace.LocalToReal(carry.Orb.totem.position); body.ParentRot = carry.Orb.totem.rotation;
        }
        private void SendOrbRequest(Carry carry, OrbAction action)
        {
            if (action != OrbAction.Pose) { if (++_nextOrbRequest == 0) ++_nextOrbRequest; carry.Request = _nextOrbRequest; carry.Gate.Begin(carry.Request); }
            var msg = new OrbRequestMsg { Action = action, RequestId = carry.Request }; CaptureOrb(carry, msg);
            bool reliable = action != OrbAction.Pose;
            if (action == OrbAction.Pose)
            {
                var mode = carry.RequestStream.Next(OrbPoseChanged(carry.SentRequest, msg));
                if (mode == StreamSend.None) return;
                reliable = mode == StreamSend.Reliable;
            }
            else carry.RequestStream.Sent(true);
            carry.SentRequest = msg;
            _net.Broadcast(msg, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }
        private void SendOrbState(Carry carry, bool reliable, bool periodic = false)
        {
            var msg = new OrbStateMsg { Revision = ++carry.Revision, Holder = Local(carry.Orb) ? _net.MyNetId : carry.Holder,
                Requester = carry.AckActor, RequestId = carry.AckRequest };
            CaptureOrb(carry, msg); _net.Broadcast(msg, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            if (!periodic) carry.Stream.Sent(reliable);
            carry.SentState = msg;
        }
        private static bool OrbPoseChanged(OrbBody a, OrbBody b)
            => a == null || a.Held != b.Held || (a.RealPos - b.RealPos).sqrMagnitude > 1e-4f ||
               (a.ParentReal - b.ParentReal).sqrMagnitude > 1e-4f || Quaternion.Angle(a.Rot, b.Rot) > 0.1f || Quaternion.Angle(a.ParentRot, b.ParentRot) > 0.1f;
        private static bool OrbChanged(OrbStateMsg a, OrbBody b, uint holder)
            => a == null || a.Holder != holder || OrbPoseChanged(a, b);
        /// <summary>Host: an orb sends only while it or its totem moves or its holder changes.</summary>
        private void StreamOrb(Carry carry)
        {
            var probe = new OrbStateMsg(); CaptureOrb(carry, probe);
            var mode = carry.Stream.Next(OrbChanged(carry.SentState, probe, Local(carry.Orb) ? _net.MyNetId : carry.Holder));
            if (mode != StreamSend.None) SendOrbState(carry, mode == StreamSend.Reliable, periodic: true);
        }
        private void TickOrbs(float dt)
        {
            if (_net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading || !CoordSpace.Ready) return;
            _orbScan += dt; _orbTimer += dt;
            if (_orbScan >= 0.5f)
            {
                _orbScan = 0;
                foreach (var orb in Resources.FindObjectsOfTypeAll<WindTotemOrb>()) BindOrb(orb);
                foreach (var pair in new List<KeyValuePair<string, OrbStateMsg>>(_waitingOrbs))
                { var carry = FindOrb(pair.Key, pair.Value.ParentId); if (carry != null) { ReceiveOrb(carry, pair.Value); _waitingOrbs.Remove(pair.Key); } }
            }
            bool send = _orbTimer >= 1f / 15f; if (send) _orbTimer = 0;
            foreach (var carry in _orbs.Values)
            {
                if (carry.Orb == null || carry.Orb.totem == null) continue;
                if (Local(carry.Orb)) RestoreOrb(carry);
                else if (!carry.Gate.Pending && carry.Pose.HasData && (_net.Role == Role.Client || carry.Holder != 0))
                {
                    SlaveOrb(carry); carry.Pose.Apply(carry.Orb.transform, _net.Clock.ServerTick);
                    carry.ParentPose.Apply(carry.Orb.totem, _net.Clock.ServerTick);
                    OrbVisual(carry);
                }
                if (!send) continue;
                if (_net.Role == Role.Host) StreamOrb(carry);
                else if (_net.Role == Role.Client && Local(carry.Orb)) SendOrbRequest(carry, OrbAction.Pose);
            }
        }
        public void OnOrbRequest(OrbRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer); if (actor == 0) return;
            var carry = FindOrb(msg.OrbId, msg.ParentId);
            if (carry == null)
            { peer.Send(new OrbStateMsg { OrbId = msg.OrbId, ParentId = msg.ParentId, Missing = true, Requester = actor, RequestId = msg.RequestId }, DeliveryMethod.ReliableOrdered); return; }
            if (msg.Action == OrbAction.Baseline) { SendOrbState(carry, true); return; }
            if (!carry.Order.Accept(actor, msg.RequestId, msg.Action == OrbAction.Pose)) return;
            uint previous = carry.Holder; carry.Holder = msg.Held ? actor : 0;
            if (msg.Action != OrbAction.Pose) { carry.AckActor = actor; carry.AckRequest = msg.RequestId; carry.Pose.Clear(); carry.ParentPose.Clear(); }
            BufferOrb(carry, msg, _net.Clock.ServerTick);
            if (!Local(carry.Orb))
            {
                SlaveOrb(carry); carry.Pose.Apply(carry.Orb.transform, _net.Clock.ServerTick);
                carry.ParentPose.Apply(carry.Orb.totem, _net.Clock.ServerTick);
                OrbVisual(carry);
                if (!msg.Held) { RestoreOrb(carry); carry.Orb.OnDrop(); }
            }
            if (msg.Action != OrbAction.Pose)
            { SendOrbState(carry, true); if (previous != carry.Holder) LastOrbAction = "in orb " + msg.Action + " actor=" + actor + " " + carry.Id; }
        }
        public void OnOrbState(OrbStateMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Client || !_net.IsHostPeer(peer)) return;
            var carry = FindOrb(msg.OrbId, msg.ParentId);
            if (msg.Missing)
            { carry?.Gate.Cancel(msg.Requester, msg.RequestId, _net.MyNetId); Plugin.Logger.LogWarning("[WindTotemSync] missing host orb=" + msg.OrbId + " parent=" + msg.ParentId + " request=" + msg.RequestId); return; }
            if (carry == null)
            {
                if (!_waitingOrbs.TryGetValue(msg.OrbId, out var previous) || unchecked((int)(msg.Revision - previous.Revision)) > 0) _waitingOrbs[msg.OrbId] = msg;
                return;
            }
            ReceiveOrb(carry, msg);
        }
        private void ReceiveOrb(Carry carry, OrbStateMsg msg)
        {
            if (!carry.Gate.Receive(msg.Revision, msg.Tick, msg.Requester, msg.RequestId, _net.MyNetId, out var semantic, out var pose) || Local(carry.Orb)) return;
            if (carry.Holder != msg.Holder) { carry.Pose.Clear(); carry.ParentPose.Clear(); }
            carry.Holder = msg.Holder; SlaveOrb(carry); BufferOrb(carry, msg, msg.Tick);
        }
        private static void BufferOrb(Carry carry, OrbBody msg, long tick)
        { carry.Pose.Push(tick, msg.RealPos, msg.Rot); carry.ParentPose.Push(tick, msg.ParentReal, msg.ParentRot); }
        private static void SlaveOrb(Carry carry)
        {
            if (!carry.Slaved) { carry.Slaved = true; carry.OriginalLayer = carry.Orb.gameObject.layer; }
            carry.Orb.gameObject.layer = carry.Holder != 0 ? 2 : carry.OriginalLayer;
        }
        private static void RestoreOrb(Carry carry)
        {
            if (!carry.Slaved || carry.Orb == null) return;
            carry.Orb.gameObject.layer = Local(carry.Orb) ? 2 : carry.OriginalLayer; carry.Slaved = false; carry.Pose.Clear(); carry.ParentPose.Clear();
        }
        private static void OrbVisual(Carry carry)
        {
            var orb = carry.Orb;
            if (carry.Holder == 0) { if (!carry.Quiet) { orb.OnDrop(); carry.Quiet = true; } return; }
            carry.Quiet = false;
            float distance = Vector3.Distance(orb.transform.position, orb.totem.position), fraction = distance / orb.maxCarryDistance;
            if (orb.orbParticles != null)
            {
                var emission = orb.orbParticles.emission; emission.enabled = true; emission.rateOverTime = distance * 20;
                var main = orb.orbParticles.main; main.startSize = Mathf.Lerp(orb.particlesMinSize, orb.particlesMaxSize, fraction);
            }
            if (orb.audio != null)
            { orb.audio.volume = fraction; orb.audio.pitch = Mathf.Lerp(orb.minPitch, orb.maxPitch, fraction); if (!orb.audio.isPlaying) { orb.audio.enabled = true; orb.audio.Play(); } }
        }
        internal bool RemoteOrb(WindTotemOrb orb)
        {
            if (_net.State != LinkState.Connected || Local(orb)) return false;
            var carry = BindOrb(orb);
            return carry != null && (_net.Role == Role.Client || carry.Holder != 0);
        }
        public void ClearRemoteActor(uint actor)
        {
            if (_net.Role != Role.Host) return;
            foreach (var carry in _orbs.Values) if (carry.Holder == actor && carry.Orb != null)
            { carry.Holder = 0; RestoreOrb(carry); carry.Orb.OnDrop(); SendOrbState(carry, true); }
        }
        private void ClearOrbs()
        {
            foreach (var carry in _orbs.Values) if (carry.Orb != null) { RestoreOrb(carry); if (!Local(carry.Orb)) carry.Orb.OnDrop(); }
            _orbs.Clear(); _orbByInstance.Clear(); _waitingOrbs.Clear(); _orbScan = _orbTimer = 0; _nextOrbRequest = 0; LastOrbAction = "—";
        }
    }
    internal static class OrbCarryPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) }, m => harmony.Patch(m, postfix: Callback(nameof(PostPickup))));
            hooks.Install(typeof(GoPointer), "DropItem", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreDrop)), postfix: Callback(nameof(PostDrop))));
            hooks.Install(typeof(WindTotemOrb), "Update", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreUpdate))));
            PatchHealth.Report("Wind orb carry", hooks);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(OrbCarryPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[OrbCarryPatches] " + error); } catch { } }
        private static void PostPickup(PickupableItem item) => PatchGuard.Run(() => { if (item is WindTotemOrb orb && orb.held != null) WindTotemSync.Instance?.NotifyOrb(orb, true); }, Report);
        private static void PreDrop(GoPointer __instance, out WindTotemOrb __state)
        { WindTotemOrb state = null; PatchGuard.Run(() => state = __instance.GetHeldItem() as WindTotemOrb, Report); __state = state; }
        private static void PostDrop(WindTotemOrb __state) => PatchGuard.Run(() => { if (__state != null && __state.held == null) WindTotemSync.Instance?.NotifyOrb(__state, false); }, Report);
        private static bool PreUpdate(WindTotemOrb __instance) => PatchGuard.Prefix(() => !(WindTotemSync.Instance?.RemoteOrb(__instance) ?? false), Report);
    }
}
