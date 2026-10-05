using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed partial class MooringSync
    {
        private sealed class Carry
        {
            internal PickupableItem Item;
            internal ushort Index;
            internal byte Part;
            internal uint Holder, Revision, Request, AckActor, AckRequest;
            internal bool FromDock, Slaved, PreviousKinematic, Sent;
            internal Vector3 SentPos; internal Quaternion SentRot; internal CoordFrame SentFrame; internal uint SentHolder;
            internal readonly ChangeStream Stream = new ChangeStream();
            internal int PreviousLayer;
            internal CoordFrame Frame;
            internal readonly NetTransform Pose = new NetTransform();
            internal readonly ItemStateGate Gate = new ItemStateGate();
            internal readonly ItemRequestOrder Order = new ItemRequestOrder();
        }
        private readonly Dictionary<int, Carry> _carries = new Dictionary<int, Carry>();
        private float _carryTimer;
        private Carry CarryFor(ushort index, byte part)
        {
            if (part > 1 || _bm?.ropes == null || index >= _bm.ropes.Length || _bm.ropes[index] == null) return null;
            int key = index * 2 + part;
            if (_carries.TryGetValue(key, out var carry)) return carry;
            var rope = _bm.ropes[index];
            PickupableItem item = part == 0 ? (PickupableItem)rope : ItemComponents.Read<MooringRopeLengthAdjuster>(rope, "lengthAdjuster");
            if (item == null) return null;
            carry = new Carry { Item = item, Index = index, Part = part }; carry.Pose.InterpDelayMs = _net.Role == Role.Host ? 0 : 100;
            _carries[key] = carry; return carry;
        }
        private bool LocateCarry(PickupableItem item, out Carry carry)
        {
            carry = null;
            var rope = item as PickupableBoatMooringRope ?? (item as MooringRopeLengthAdjuster)?.mooringRope;
            int index = IndexOf(rope);
            if (index < 0) return false;
            carry = CarryFor((ushort)index, (byte)(item is MooringRopeLengthAdjuster ? 1 : 0)); return carry != null;
        }
        private bool CarryPending(ushort index)
        {
            return (_carries.TryGetValue(index * 2, out var rope) && rope.Gate.Pending) ||
                (_carries.TryGetValue(index * 2 + 1, out var adjuster) && adjuster.Gate.Pending);
        }
        internal void NotifyCarry(PickupableItem item, bool held)
        {
            if (InteractionContext.Suppressed || _net.State != LinkState.Connected || !CoordSpace.Ready) return;
            if (_fleet != null)
            { foreach (var c in _fleet.Values) if (c.LocateCarry(item, out var unused)) { c.NotifyCarry(item, held); return; } return; }
            if (!LocateCarry(item, out var carry)) return;
            RestoreCarry(carry); carry.Holder = held ? _net.MyNetId : 0; carry.Pose.Clear();
            if (item is MooringRopeLengthAdjuster adjuster) carry.FromDock = ItemComponents.Read<bool>(adjuster, "pickedUpFromMooring");
            if (_net.Role == Role.Client) SendCarryRequest(carry, held ? MooringCarryAction.Pickup : MooringCarryAction.Drop);
            else if (_net.Role == Role.Host) SendCarryState(carry, true);
            Remember("out " + (held ? "pickup" : "drop") + " #" + carry.Index + (carry.Part == 0 ? " rope" : " adjuster"));
            Plugin.Logger.LogInfo("[MooringSync] Local carry " + (held ? "pickup" : "drop") + " boat=" + _boatId + " rope #" + carry.Index +
                (carry.Part == 0 ? " end" : " coil") + " (role " + _net.Role + ")");
        }
        private void CaptureCarry(Carry carry, MooringCarryBody msg)
        {
            bool local = carry.Item.held != null;
            if (local && carry.Item is MooringRopeLengthAdjuster adjuster)
                carry.FromDock = ItemComponents.Read<bool>(adjuster, "pickedUpFromMooring");
            bool onDeck = local ? GameState.currentBoat == _boundBoat : carry.Frame == CoordFrame.Boat;
            // A free endpoint remains in the boat frame until MoorTo reparents it to a dock.
            if (carry.Holder == 0) onDeck = carry.Part == 1 || !_bm.ropes[carry.Index].IsMoored();
            msg.BoatIndex = _boatId; msg.LayoutHash = _layoutHash; msg.Index = carry.Index; msg.Part = carry.Part;
            msg.Frame = onDeck ? CoordFrame.Boat : CoordFrame.World; msg.Tick = _net.Clock.ServerTick;
            msg.Pos = onDeck ? _boundBoat.InverseTransformPoint(carry.Item.transform.position) : CoordSpace.LocalToReal(carry.Item.transform.position);
            msg.Rot = onDeck ? Quaternion.Inverse(_boundBoat.rotation) * carry.Item.transform.rotation : carry.Item.transform.rotation;
            msg.HolderNetId = local ? _net.MyNetId : carry.Holder; msg.Held = msg.HolderNetId != 0;
            msg.FromDock = carry.FromDock; msg.LinkPending = _dockRequests.ContainsKey(carry.Index);
        }
        private void SendCarryRequest(Carry carry, MooringCarryAction action)
        {
            if (action != MooringCarryAction.Pose)
            { if (++_nextRequestId == 0) ++_nextRequestId; carry.Request = _nextRequestId; carry.Gate.Begin(carry.Request); }
            var msg = new MooringCarryRequestMsg { Action = action, RequestId = carry.Request };
            CaptureCarry(carry, msg);
            // The held-pose pass repeats nothing: a rope carried without moving sends no packet.
            if (action == MooringCarryAction.Pose && carry.Sent && msg.Frame == carry.SentFrame &&
                (msg.Pos - carry.SentPos).sqrMagnitude <= 1e-6f && Quaternion.Angle(msg.Rot, carry.SentRot) <= 0.1f) return;
            carry.Sent = true; carry.SentPos = msg.Pos; carry.SentRot = msg.Rot; carry.SentFrame = msg.Frame;
            _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
        }
        private void SendCarryState(Carry carry, bool reliable, bool periodic = false)
        {
            var msg = new MooringCarryStateMsg { Revision = carry.Revision = SessionCounters.NextRevision(), Requester = carry.AckActor, RequestId = carry.AckRequest };
            CaptureCarry(carry, msg); _net.Broadcast(msg, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            if (!periodic) carry.Stream.Sent(reliable);
            carry.Sent = true; carry.SentPos = msg.Pos; carry.SentRot = msg.Rot; carry.SentFrame = msg.Frame; carry.SentHolder = msg.HolderNetId;
        }
        /// <summary>Host: a part sends only while its pose or holder changes, then one reliable
        /// resting pose. A part lying still, held or not, sends nothing.</summary>
        private void StreamCarry(Carry carry)
        {
            var probe = new MooringCarryStateMsg(); CaptureCarry(carry, probe);
            bool changed = !carry.Sent || probe.Frame != carry.SentFrame || probe.HolderNetId != carry.SentHolder ||
                (probe.Pos - carry.SentPos).sqrMagnitude > 1e-4f || Quaternion.Angle(probe.Rot, carry.SentRot) > 0.1f;
            var mode = carry.Stream.Next(changed);
            if (mode != StreamSend.None) SendCarryState(carry, mode == StreamSend.Reliable, periodic: true);
        }
        private bool _resyncAsked;
        /// <summary>Host: a client just bound this hull and has no state for it yet.</summary>
        public void Resync(ushort boat)
        {
            var context = _fleet?.Get(boat); if (context == null) return;
            foreach (var carry in context._carries.Values) carry.Stream.Reset();
            context._ropeSent.Clear();
        }
        private void AskResync()
        {
            if (_resyncAsked || _net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading) return;
            _resyncAsked = true;
            _net.Broadcast(new ResyncRequestMsg { BoatIndex = _boatId, Domain = ResyncDomain.Mooring }, DeliveryMethod.ReliableOrdered);
        }
        private void TickCarry(float dt)
        {
            if (_net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading || !CoordSpace.Ready || _bm?.ropes == null) return;
            _carryTimer += dt; bool send = _carryTimer >= 1f / 12f; if (send) _carryTimer = 0;
            for (ushort i = 0; i < _bm.ropes.Length; i++) for (byte part = 0; part < 2; part++)
            {
                var carry = CarryFor(i, part); if (carry == null || carry.Item == null) continue;
                if (carry.Item.held != null) RestoreCarry(carry);
                else if (!carry.Gate.Pending && carry.Pose.HasData && (_net.Role == Role.Client || carry.Holder != 0))
                {
                    // MoorTo owns the dock endpoint; a held/returning adjuster remains independent.
                    if (part == 1 || !_bm.ropes[i].IsMoored()) { SlaveCarry(carry); carry.Pose.Apply(carry.Item.transform, _net.Clock.ServerTick); }
                }
                if (!send) continue;
                if (_net.Role == Role.Host) StreamCarry(carry);
                else if (_net.Role == Role.Client && carry.Item.held != null) SendCarryRequest(carry, MooringCarryAction.Pose);
            }
        }
        public void OnCarryRequest(MooringCarryRequestMsg msg, NetPeer peer)
        {
            if (_fleet != null)
            {
                if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(peer) == 0 || !GameState.playing || GameState.currentlyLoading) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "MooringSync")) c.OnCarryRequest(msg, peer);
                return;
            }
            var carry = CarryFor(msg.Index, msg.Part); uint actor = _net.PlayerNetIdForPeer(peer);
            if (carry?.Item == null || actor == 0 || !carry.Order.Accept(actor, msg.RequestId, msg.Action == MooringCarryAction.Pose)) return;
            uint previous = carry.Holder;
            bool previousFromDock = carry.FromDock;
            if (msg.Action == MooringCarryAction.Pickup && msg.Part == 0 && _dockRequests.TryGetValue(msg.Index, out var waiting))
            { _dockRequests.Remove(msg.Index); SendMissingObject(waiting.Request, waiting.Actor); }
            carry.Holder = msg.Held ? actor : 0; carry.FromDock = msg.FromDock;
            if (msg.Action != MooringCarryAction.Pose) { carry.Pose.Clear(); carry.AckActor = actor; carry.AckRequest = msg.RequestId; }
            BufferCarry(carry, _net.Clock.ServerTick, msg.Frame, msg.Pos, msg.Rot, msg.Vel);
            if (carry.Item.held == null)
            {
                using (InteractionContext.Begin(InteractionSource.RemoteApply))
                {
                    if (msg.Held && msg.Part == 0 && _bm.ropes[msg.Index].IsMoored()) _bm.ropes[msg.Index].Unmoor();
                    SlaveCarry(carry); carry.Pose.Apply(carry.Item.transform, _net.Clock.ServerTick);
                    // A drop runs the game's own return sequence, which reads the rope attachment and
                    // resets the visual itself when it ends. Resetting the visual first clears that
                    // attachment: the sequence then throws on its first step and the part stays where
                    // it was dropped, with its collider off, for the rest of the session.
                    if (!msg.Held)
                    { RestoreCarry(carry); carry.Item.OnDrop(); }
                    else if (msg.Action != MooringCarryAction.Pose || previousFromDock != carry.FromDock) UpdateCarryVisual(carry, previous);
                }
            }
            if (msg.Action != MooringCarryAction.Pose)
            {
                Plugin.Logger.LogInfo("[MooringSync] Host carry " + msg.Action + " boat=" + _boatId + " rope #" + msg.Index +
                    (msg.Part == 0 ? " end" : " coil") + " player=" + actor + " request=" + msg.RequestId);
                SendCarryState(carry, true);
            }
        }
        public void OnCarryState(MooringCarryStateMsg msg, NetPeer peer)
        {
            if (_fleet != null)
            {
                if (!_net.IsHostPeer(peer) || !GameState.playing || GameState.currentlyLoading) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "MooringSync")) c.OnCarryState(msg, peer);
                return;
            }
            if (_net.Role != Role.Client) return;
            var carry = CarryFor(msg.Index, msg.Part);
            if (carry?.Item == null || !carry.Gate.Receive(msg.Revision, msg.Tick, msg.Requester, msg.RequestId, _net.MyNetId, out var revision, out var pose)) return;
            if (carry.Item.held != null || (msg.LinkPending && msg.Part == 0 && !msg.Held)) return;
            uint previous = carry.Holder; bool previousFromDock = carry.FromDock;
            carry.Holder = msg.HolderNetId; carry.FromDock = msg.FromDock;
            if (previous != carry.Holder) carry.Pose.Clear();
            // The visual is a function of holder/dock origin; a pose sample alone does not redo it.
            bool visual = !carry.Slaved || previous != carry.Holder || previousFromDock != carry.FromDock;
            using (InteractionContext.Begin(InteractionSource.RemoteApply))
            {
                if (carry.Holder != 0 && msg.Part == 0 && _bm.ropes[msg.Index].IsMoored()) _bm.ropes[msg.Index].Unmoor();
                SlaveCarry(carry); if (visual) UpdateCarryVisual(carry, previous);
            }
            BufferCarry(carry, msg.Tick, msg.Frame, msg.Pos, msg.Rot, msg.Vel);
        }
        private void BufferCarry(Carry carry, long tick, CoordFrame frame, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (carry.Frame != frame) carry.Pose.Clear(); carry.Frame = frame;
            carry.Pose.ToWorldPos = frame == CoordFrame.Boat ? (Func<Vector3, Vector3>)(p => _boundBoat.TransformPoint(p)) : CoordSpace.RealToLocal;
            carry.Pose.ToWorldRot = frame == CoordFrame.Boat ? (Func<Quaternion, Quaternion>)(r => _boundBoat.rotation * r) : r => r;
            carry.Pose.Push(tick, pos, rot, vel);
        }
        private static void SlaveCarry(Carry carry)
        {
            if (carry.Slaved) return;
            carry.Slaved = true; carry.PreviousLayer = carry.Item.gameObject.layer;
            var body = carry.Item.GetComponent<Rigidbody>(); if (body != null) { carry.PreviousKinematic = body.isKinematic; body.isKinematic = true; }
            carry.Item.StopAllCoroutines();
            // The stopped coroutine is the game's own throw/return sequence. It switches the part's
            // collider off at its start and back on only at its end, so a sequence cut short (the
            // host's reply to a drop arrives well inside it) left the part impossible to point at.
            if (carry.Item is PickupableBoatMooringRope)
            {
                ItemComponents.Set(carry.Item, "throwing", false);
                var collider = carry.Item.GetComponent<Collider>(); if (collider != null) collider.enabled = true;
            }
            else if (carry.Item is MooringRopeLengthAdjuster) ItemComponents.Set(carry.Item, "returnSequencePlaying", false);
        }
        private static void RestoreCarry(Carry carry)
        {
            if (!carry.Slaved || carry.Item == null) return;
            carry.Slaved = false;
            // GoPointer.PickUpItem has already moved a locally held part to the ignore-raycast layer.
            // Putting the old layer back makes the pointer hit the part in the hand, and a click then
            // lands on the part itself instead of dropping it.
            if (carry.Item.held == null) carry.Item.gameObject.layer = carry.PreviousLayer;
            var body = carry.Item.GetComponent<Rigidbody>(); if (body != null) body.isKinematic = carry.PreviousKinematic;
            carry.Pose.Clear();
        }
        private static void UpdateCarryVisual(Carry carry, uint previous)
        {
            carry.Item.gameObject.layer = carry.Holder != 0 ? 2 : carry.PreviousLayer;
            if (!(carry.Item is MooringRopeLengthAdjuster adjuster)) return;
            if (carry.Holder != 0 && previous == 0) adjuster.OnPickup();
            if (carry.Holder != 0) { if (carry.FromDock) adjuster.PickupFromMooring(); return; }
            var rope = adjuster.GetComponent<RopeEffect>(); rope.attachment = null; rope.Hide();
            ItemComponents.Read<Renderer>(adjuster, "renderer").enabled = true;
            ItemComponents.Read<Transform>(adjuster, "coiledRopeVisual").gameObject.SetActive(false);
            ItemComponents.Set(adjuster, "returnSequencePlaying", false);
            var material = typeof(MooringRopeLengthAdjuster).GetField("defaultMaterial", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Material;
            if (material != null) adjuster.GetComponent<LineRenderer>().sharedMaterial = material;
        }
        internal bool SuppressCarryTrigger(PickupableBoatMooringRope rope)
        {
            if (_fleet != null) { foreach (var c in _fleet.Values) if (c.IndexOf(rope) >= 0) return c.SuppressCarryTrigger(rope); return false; }
            return _net.Role == Role.Client && _net.State == LinkState.Connected && IndexOf(rope) >= 0;
        }
        public void ClearRemoteActor(uint actor)
        {
            if (_fleet != null) { foreach (var c in _fleet.Existing) c.ClearRemoteActor(actor); return; }
            if (_net.Role != Role.Host || actor == 0) return;
            foreach (var pair in new List<KeyValuePair<ushort, DeferredDock>>(_dockRequests))
                if (pair.Value.Actor == actor) { _dockRequests.Remove(pair.Key); SendMissingObject(pair.Value.Request, actor); }
            foreach (var carry in _carries.Values) if (carry.Holder == actor && carry.Item != null)
            {
                carry.Holder = 0; RestoreCarry(carry);
                using (InteractionContext.Begin(InteractionSource.RemoteApply)) carry.Item.OnDrop();
                SendCarryState(carry, true);
            }
        }
        private void ClearCarry()
        { foreach (var carry in _carries.Values) RestoreCarry(carry); _carries.Clear(); _carryTimer = 0; }
    }
}
