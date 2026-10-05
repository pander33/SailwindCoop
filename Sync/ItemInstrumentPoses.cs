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
    public sealed partial class ItemSync
    {
        private sealed class Instrument
        {
            internal ShipItem Item;
            internal ConfigurableJoint Joint;
            internal Rigidbody Body;
            internal bool Slaved, OriginalKinematic;
            internal uint Revision;
            internal readonly ChangeStream Stream = new ChangeStream();
            internal InstrumentBody Sent;
            internal InstrumentStateMsg Latest;
            internal readonly ItemStateGate Gate = new ItemStateGate();
            internal readonly NetTransform Pose = new NetTransform(), Rotator = new NetTransform();
        }
        private readonly Dictionary<int, Instrument> _instruments = new Dictionary<int, Instrument>();
        private readonly Dictionary<int, InstrumentStateMsg> _waitingInstruments = new Dictionary<int, InstrumentStateMsg>();
        private float _instrumentTimer;
        private static bool HasInstrument(ShipItem item) => item is ShipItemChipLog || item is ShipItemFishingRod;
        private static bool LocalHand(ShipItem item) => item?.held != null && item.held.GetHeldItem() == item;
        private Instrument BindInstrument(ItemEntry entry)
        {
            if (entry?.Item == null || !HasInstrument(entry.Item) || (_net.Role == Role.Client && !_hostIds.Contains(entry.InstanceId))) return null;
            if (_instruments.TryGetValue(entry.InstanceId, out var previous) && previous.Item == entry.Item) return previous;
            if (previous != null) RestoreInstrument(previous);
            var joint = ItemComponents.Read<ConfigurableJoint>(entry.Item, "bobberJoint");
            var body = ItemComponents.Read<Rigidbody>(entry.Item, "bobberBody");
            if (joint == null || body == null) return null; // OnLoad has not bound the child's real-world body yet.
            var child = new Instrument { Item = entry.Item, Joint = joint, Body = body };
            child.Pose.InterpDelayMs = child.Rotator.InterpDelayMs = _net.Role == Role.Host ? 0 : 100;
            child.Rotator.ToWorldPos = p => child.Item.transform.TransformPoint(p);
            child.Rotator.ToWorldRot = r => child.Item.transform.rotation * r;
            _instruments[entry.InstanceId] = child; return child;
        }
        internal bool RemoteInstrument(ShipItem item)
        {
            if (_net.State != LinkState.Connected || !HasInstrument(item) || LocalHand(item) || !_byItem.TryGetValue(item, out var entry)) return false;
            return (_net.Role == Role.Client && _hostIds.Contains(entry.InstanceId)) ||
                (_net.Role == Role.Host && entry.HolderNetId != 0 && entry.HolderNetId != _net.MyNetId);
        }
        private static void SlaveInstrument(Instrument child)
        {
            if (child.Slaved) return;
            child.Slaved = true; child.OriginalKinematic = child.Body.isKinematic;
            child.Item.StopAllCoroutines(); child.Body.isKinematic = true;
        }
        private static void RestoreInstrument(Instrument child)
        {
            if (!child.Slaved) return;
            if (child.Body != null) child.Body.isKinematic = child.OriginalKinematic;
            if (child.Item != null) { ItemComponents.Set(child.Item, "activated", false); ItemComponents.Set(child.Item, "holding", false); ItemComponents.Set(child.Item, "throwing", false); }
            child.Slaved = false; child.Pose.Clear(); child.Rotator.Clear();
        }
        private void CaptureInstrument(ItemEntry entry, Instrument child, InstrumentBody msg)
        {
            msg.InstanceId = entry.InstanceId; msg.PrefabIndex = entry.PrefabIndex; msg.RequestId = entry.LastLocalRequest; msg.Tick = _net.Clock.ServerTick;
            msg.ParentRevision = _net.Role == Role.Host ? BuildState(entry, _net.Clock.ServerTick).Revision : entry.Gate.Version;
            msg.RealPos = CoordSpace.LocalToReal(child.Joint.transform.position); msg.Rot = child.Joint.transform.rotation;
            msg.Velocity = child.Body.velocity; msg.Kinematic = child.Body.isKinematic;
            msg.BodyActive = child.Body.gameObject.activeSelf;
            msg.Limit = child.Joint.linearLimit.limit; msg.TargetLength = ItemComponents.Read<float>(child.Item, "currentTargetLength");
            msg.Throwing = ItemComponents.Read<bool>(child.Item, "throwing"); msg.ReelVolume = ItemComponents.Read<float>(child.Item, "targetReelVolume");
            if (child.Item is ShipItemChipLog) msg.Thrown = ItemComponents.Read<bool>(child.Item, "thrown");
            if (child.Item is ShipItemFishingRod rod)
            {
                msg.Bend = ItemComponents.Read<float>(rod, "currentRodBend");
                msg.StaticBobberActive = ItemComponents.Read<GameObject>(rod, "staticBobber")?.activeSelf ?? false;
                var fish = ItemComponents.Read<FishingRodFish>(rod, "fish");
                if (fish != null)
                {
                    msg.FishPrefab = fish.currentFish != null ? fish.currentFish.GetComponent<SaveablePrefab>()?.prefabIndex ?? 0 : 0;
                    msg.FishDead = fish.fishDead; msg.FishEnergy = ItemComponents.Read<float>(fish, "fishEnergy");
                    msg.FishTension = ItemComponents.Read<float>(fish, "currentTargetTension");
                    msg.Splash = fish.GetComponent<ParticleSystem>()?.emission.enabled ?? false;
                    msg.TensionSound = ItemComponents.Read<AudioSource>(fish, "tensionAudio")?.isPlaying ?? false;
                    var rotator = ItemComponents.Read<Transform>(fish, "rodRotator");
                    if (rotator != null) { msg.RotatorPos = rod.transform.InverseTransformPoint(rotator.position); msg.RotatorRot = Quaternion.Inverse(rod.transform.rotation) * rotator.rotation; }
                }
            }
        }
        /// <summary>periodic: the pose-rate pass. It sends only while the child's captured state differs
        /// from the last packet, then one reliable resting state; a rod lying still sends nothing.</summary>
        private void SendInstrument(ItemEntry entry, Instrument child, bool final, bool periodic = false)
        {
            bool reliable = final;
            if (_net.Role == Role.Client)
            {
                var request = new InstrumentRequestMsg { Final = final }; CaptureInstrument(entry, child, request);
                if (periodic)
                {
                    var mode = child.Stream.Next(InstrumentChanged(child.Sent, request));
                    if (mode == StreamSend.None) return;
                    reliable = mode == StreamSend.Reliable;
                }
                else child.Stream.Sent(final);
                child.Sent = request;
                _net.Broadcast(request, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable); return;
            }
            var state = new InstrumentStateMsg { Final = final, ParentRevision = BuildState(entry, _net.Clock.ServerTick).Revision };
            CaptureInstrument(entry, child, state);
            // A slave body's kinematic flag/zero velocity are transport mechanics, not the owner's physical state.
            if (child.Slaved && child.Latest != null) { state.Kinematic = child.Latest.Kinematic; state.Velocity = child.Latest.Velocity; }
            if (periodic)
            {
                var mode = child.Stream.Next(InstrumentChanged(child.Sent, state));
                if (mode == StreamSend.None) return;
                reliable = mode == StreamSend.Reliable;
            }
            else child.Stream.Sent(final);
            state.Revision = ++child.Revision; child.Sent = state;
            _net.Broadcast(state, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }
        private static bool InstrumentChanged(InstrumentBody a, InstrumentBody b)
            => a == null || a.ParentRevision != b.ParentRevision || a.RequestId != b.RequestId ||
               a.Kinematic != b.Kinematic || a.Thrown != b.Thrown || a.Throwing != b.Throwing || a.BodyActive != b.BodyActive ||
               a.StaticBobberActive != b.StaticBobberActive || a.FishPrefab != b.FishPrefab || a.FishDead != b.FishDead ||
               a.Splash != b.Splash || a.TensionSound != b.TensionSound ||
               !a.Limit.Equals(b.Limit) || !a.TargetLength.Equals(b.TargetLength) || !a.Bend.Equals(b.Bend) || !a.ReelVolume.Equals(b.ReelVolume) ||
               !a.FishEnergy.Equals(b.FishEnergy) || !a.FishTension.Equals(b.FishTension) ||
               (a.RealPos - b.RealPos).sqrMagnitude > 1e-4f || (a.RotatorPos - b.RotatorPos).sqrMagnitude > 1e-6f ||
               (a.Velocity - b.Velocity).sqrMagnitude > 1e-4f ||
               Quaternion.Angle(a.Rot, b.Rot) > 0.1f || Quaternion.Angle(a.RotatorRot, b.RotatorRot) > 0.1f;
        /// <summary>Host: a client finished loading and has no instrument child state yet.</summary>
        public void ResyncInstruments() { foreach (var child in _instruments.Values) child.Stream.Reset(); }
        internal void InstrumentFinal(ShipItem item)
        {
            if (InteractionContext.Suppressed || _net.State != LinkState.Connected || !CoordSpace.Ready || !HasInstrument(item)) return;
            if (!_byItem.TryGetValue(item, out var entry)) return;
            var child = BindInstrument(entry); if (child == null) return;
            if (_net.Role == Role.Client && !LocalHand(item)) return; // Drop uses its separate final hook below.
            SendInstrument(entry, child, true);
        }
        internal void InstrumentDropped(ShipItem item)
        {
            if (InteractionContext.Suppressed || _net.State != LinkState.Connected || !CoordSpace.Ready || !HasInstrument(item)) return;
            if (!_byItem.TryGetValue(item, out var entry)) return;
            var child = BindInstrument(entry); if (child == null) return;
            SendInstrument(entry, child, true);
            if (_net.Role == Role.Client) SlaveInstrument(child);
        }
        private void TickInstruments(float dt)
        {
            if (!CoordSpace.Ready || !GameState.playing || GameState.currentlyLoading) return;
            foreach (var pair in new List<KeyValuePair<int, Instrument>>(_instruments))
                if (!_byInstanceId.TryGetValue(pair.Key, out var alive) || alive.Item != pair.Value.Item)
                { RestoreInstrument(pair.Value); _instruments.Remove(pair.Key); _waitingInstruments.Remove(pair.Key); }
            _instrumentTimer += dt; bool send = _instrumentTimer >= 1f / 15f; if (send) _instrumentTimer = 0;
            foreach (var entry in _items)
            {
                var child = BindInstrument(entry); if (child == null) continue;
                if (_waitingInstruments.TryGetValue(entry.InstanceId, out var waiting)) { ReceiveInstrument(entry, child, waiting); _waitingInstruments.Remove(entry.InstanceId); }
                bool remote = RemoteInstrument(child.Item);
                if (remote) SlaveInstrument(child); else RestoreInstrument(child);
                if (remote && !entry.Gate.Pending && child.Latest != null &&
                    (_net.Role == Role.Host || child.Latest.ParentRevision == entry.Gate.Version))
                {
                    child.Pose.Apply(child.Joint.transform, _net.Clock.ServerTick); ApplyInstrumentFields(child, child.Latest);
                    if (child.Item is ShipItemFishingRod rod)
                    {
                        var fish = ItemComponents.Read<FishingRodFish>(rod, "fish");
                        if (fish != null) child.Rotator.Apply(ItemComponents.Read<Transform>(fish, "rodRotator"), _net.Clock.ServerTick);
                    }
                }
                if (!send) continue;
                if (_net.Role == Role.Host) SendInstrument(entry, child, false, periodic: true);
                else if (_net.Role == Role.Client && LocalHand(child.Item)) SendInstrument(entry, child, false, periodic: true);
            }
        }
        public void OnInstrumentRequest(InstrumentRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer); if (actor == 0) return;
            var entry = HostLookup(msg.InstanceId, msg.PrefabIndex); var child = BindInstrument(entry);
            if (child == null || !entry.Requests.Accept(actor, msg.RequestId, true)) return;
            uint parentRevision = BuildState(entry, _net.Clock.ServerTick).Revision;
            // A preceding handoff's child stream cannot cross a later root action. A final
            // drop may still carry the pre-ack revision when it names that exact root request.
            if (unchecked((int)(msg.ParentRevision - parentRevision)) < 0 &&
                !(entry.AckRequester == actor && entry.AckRequest == msg.RequestId)) return;
            bool remote = RemoteInstrument(child.Item);
            if (remote) SlaveInstrument(child); else RestoreInstrument(child);
            // Requests describe the exact child result. Host stamps time and root revision; no new RNG/input.
            var state = new InstrumentStateMsg { Final = msg.Final, ParentRevision = parentRevision, Revision = ++child.Revision };
            CopyInstrument(msg, state); state.Tick = _net.Clock.ServerTick;
            if (child.Latest != null && child.Latest.ParentRevision != state.ParentRevision) { child.Pose.Clear(); child.Rotator.Clear(); }
            child.Latest = state; BufferInstrument(child, state);
            child.Pose.Apply(child.Joint.transform, _net.Clock.ServerTick); ApplyInstrumentFields(child, state);
            if (!remote) { child.Body.isKinematic = msg.Kinematic; if (!msg.Kinematic) child.Body.velocity = msg.Velocity; }
            _net.Broadcast(state, msg.Final ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }
        private static void CopyInstrument(InstrumentBody source, InstrumentBody target)
        {
            target.InstanceId = source.InstanceId; target.PrefabIndex = source.PrefabIndex; target.RequestId = source.RequestId; target.Tick = source.Tick;
            target.Final = source.Final; target.Kinematic = source.Kinematic; target.Thrown = source.Thrown; target.Throwing = source.Throwing;
            target.RealPos = source.RealPos; target.Rot = source.Rot; target.Velocity = source.Velocity; target.Limit = source.Limit; target.TargetLength = source.TargetLength;
            target.Bend = source.Bend; target.ReelVolume = source.ReelVolume; target.RotatorPos = source.RotatorPos; target.RotatorRot = source.RotatorRot;
            target.FishPrefab = source.FishPrefab; target.FishDead = source.FishDead; target.Splash = source.Splash; target.TensionSound = source.TensionSound;
            target.FishEnergy = source.FishEnergy; target.FishTension = source.FishTension;
            target.BodyActive = source.BodyActive; target.StaticBobberActive = source.StaticBobberActive;
        }
        public void OnInstrumentState(InstrumentStateMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Client || !_net.IsHostPeer(peer) || _tombstones.Contains(msg.InstanceId)) return;
            if (!_byInstanceId.TryGetValue(msg.InstanceId, out var entry) || BindInstrument(entry) == null)
            { if (!_waitingInstruments.TryGetValue(msg.InstanceId, out var old) || unchecked((int)(msg.Revision - old.Revision)) > 0) _waitingInstruments[msg.InstanceId] = msg; return; }
            ReceiveInstrument(entry, BindInstrument(entry), msg);
        }
        private void ReceiveInstrument(ItemEntry entry, Instrument child, InstrumentStateMsg msg)
        {
            if (entry.PrefabIndex != msg.PrefabIndex || LocalHand(child.Item) || unchecked((int)(msg.ParentRevision - entry.Gate.Version)) < 0) return;
            if (!child.Gate.Receive(msg.Revision, msg.Tick, 0, 0, _net.MyNetId, out var semantic, out var pose)) return;
            if (child.Latest != null && child.Latest.ParentRevision != msg.ParentRevision) { child.Pose.Clear(); child.Rotator.Clear(); }
            child.Latest = msg; BufferInstrument(child, msg); // A pending root drop defers application until root acknowledgement.
        }
        private static void BufferInstrument(Instrument child, InstrumentBody msg)
        { child.Pose.Push(msg.Tick, msg.RealPos, msg.Rot, msg.Velocity); child.Rotator.Push(msg.Tick, msg.RotatorPos, msg.RotatorRot); }
        private static void ApplyInstrumentFields(Instrument child, InstrumentBody msg)
        {
            var limit = child.Joint.linearLimit; limit.limit = msg.Limit; child.Joint.linearLimit = limit;
            if (child.Body.gameObject.activeSelf != msg.BodyActive) child.Body.gameObject.SetActive(msg.BodyActive);
            ItemComponents.Set(child.Item, "currentTargetLength", msg.TargetLength); ItemComponents.Set(child.Item, "targetReelVolume", msg.ReelVolume);
            // Don't leave remote input/throw coroutines latched when simulation later returns to this machine.
            ItemComponents.Set(child.Item, "activated", false); ItemComponents.Set(child.Item, "holding", false); ItemComponents.Set(child.Item, "throwing", false);
            var reel = ItemComponents.Read<AudioSource>(child.Item, "reelAudio"); if (reel != null) reel.volume = msg.ReelVolume;
            if (child.Item is ShipItemChipLog) ItemComponents.Set(child.Item, "thrown", msg.Thrown);
            if (child.Item is ShipItemFishingRod rod)
            {
                ItemComponents.Set(rod, "currentRodBend", msg.Bend); InvokeInstrument(rod, "UpdateBend");
                var staticBobber = ItemComponents.Read<GameObject>(rod, "staticBobber");
                if (staticBobber != null && staticBobber.activeSelf != msg.StaticBobberActive) staticBobber.SetActive(msg.StaticBobberActive);
                var fish = ItemComponents.Read<FishingRodFish>(rod, "fish");
                if (fish != null)
                {
                    var prefabs = PrefabsDirectory.instance?.directory;
                    var prefab = prefabs != null && msg.FishPrefab > 0 && msg.FishPrefab < prefabs.Length ? prefabs[msg.FishPrefab] : null;
                    fish.currentFish = prefab; fish.fishDead = msg.FishDead;
                    ItemComponents.Set(fish, "fishEnergy", msg.FishEnergy); ItemComponents.Set(fish, "currentTargetTension", msg.FishTension);
                    var mesh = fish.GetComponent<MeshFilter>(); if (mesh != null) mesh.sharedMesh = prefab?.GetComponent<MeshFilter>()?.sharedMesh;
                    var renderer = fish.GetComponent<Renderer>(); if (renderer != null) renderer.enabled = prefab != null;
                    var particles = fish.GetComponent<ParticleSystem>(); if (particles != null) { var emission = particles.emission; emission.enabled = msg.Splash; }
                    var sound = ItemComponents.Read<AudioSource>(fish, "tensionAudio");
                    if (sound != null) { if (msg.TensionSound && !sound.isPlaying) sound.Play(); else if (!msg.TensionSound && sound.isPlaying) sound.Stop(); }
                }
            }
            InvokeInstrument(child.Item, "UpdateRope");
        }
        private static void InvokeInstrument(ShipItem item, string method)
        {
            var type = item.GetType();
            if (!InstrumentCalls.TryGetValue(type, out var byName)) InstrumentCalls[type] = byName = new Dictionary<string, MethodInfo>();
            if (!byName.TryGetValue(method, out var call))
                byName[method] = call = type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            if (call == null) throw new MissingMethodException(type.Name, method); call.Invoke(item, null);
        }
        private static readonly Dictionary<Type, Dictionary<string, MethodInfo>> InstrumentCalls = new Dictionary<Type, Dictionary<string, MethodInfo>>();
        private void ClearInstruments()
        { foreach (var child in _instruments.Values) RestoreInstrument(child); _instruments.Clear(); _waitingInstruments.Clear(); _instrumentTimer = 0; }
    }
    internal static class InstrumentPosePatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            foreach (var type in new[] { typeof(ShipItemFishingRod), typeof(ShipItemChipLog) })
            {
                foreach (string method in new[] { "Update", "ExtraLateUpdate" }) hooks.Install(type, method, Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreSimulation))));
                foreach (string method in new[] { "OnAltActivate", "OnScroll" })
                {
                    if (type == typeof(ShipItemChipLog) && method == "OnScroll") continue;
                    hooks.Install(type, method, method == "OnScroll" ? new[] { typeof(float) } : Type.EmptyTypes, m => harmony.Patch(m, postfix: Callback(nameof(PostInput))));
                }
            }
            foreach (string method in new[] { "Update", "FixedUpdate" }) hooks.Install(typeof(FishingRodFish), method, Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreFish))));
            hooks.Install(typeof(GoPointer), "DropItem", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreDrop)), postfix: Callback(nameof(PostDrop), Priority.Last)));
            PatchHealth.Report("Instrument poses", hooks);
        }
        private static HarmonyMethod Callback(string name, int priority = Priority.Normal) => new HarmonyMethod(typeof(InstrumentPosePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[InstrumentPosePatches] " + error); } catch { } }
        private static bool PreSimulation(ShipItem __instance) => PatchGuard.Prefix(() => !(ItemSync.Instance?.RemoteInstrument(__instance) ?? false), Report);
        private static bool PreFish(FishingRodFish __instance) => PatchGuard.Prefix(() => !(ItemSync.Instance?.RemoteInstrument(ItemComponents.Read<ShipItemFishingRod>(__instance, "rod")) ?? false), Report);
        private static void PostInput(ShipItem __instance) => PatchGuard.Run(() => { if (InteractionContext.HasInput && !InteractionContext.Suppressed) ItemSync.Instance?.InstrumentFinal(__instance); }, Report);
        private static void PreDrop(GoPointer __instance, out ShipItem __state)
        { ShipItem state = null; PatchGuard.Run(() => state = __instance.GetHeldItem() as ShipItem, Report); __state = state; }
        private static void PostDrop(ShipItem __state) => PatchGuard.Run(() => { if (__state != null && __state.held == null) ItemSync.Instance?.InstrumentDropped(__state); }, Report);
    }
}
