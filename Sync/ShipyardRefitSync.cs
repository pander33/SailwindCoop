using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed partial class ShipyardSync
    {
        private sealed class Hull
        {
            internal ushort Id;
            internal Transform Boat;
            internal SaveableBoatCustomization Custom;
            internal RefitStateMsg Committed, Deferred;
            internal Shipyard Editor;
            internal bool Editing;
            internal int ReadyFrame;
            internal readonly ItemStateGate Gate = new ItemStateGate();
            internal readonly List<KeyValuePair<Rigidbody, bool>> Frozen = new List<KeyValuePair<Rigidbody, bool>>();
        }
        private sealed class Packet { internal MsgType Type; internal INetMessage Message; internal NetPeer Peer; }
        private readonly Dictionary<ushort, Hull> _hulls = new Dictionary<ushort, Hull>();
        private readonly BoatGenerationQueue<Packet> _waiting = new BoatGenerationQueue<Packet>();
        private readonly List<Packet> _refitWaiting = new List<Packet>();
        private readonly BoatGenerationQueue<Action> _embedded = new BoatGenerationQueue<Action>();
        private readonly OperationLedger<RefitStateMsg> _refits = new OperationLedger<RefitStateMsg>();
        private uint _nextRefit;
        internal bool ReadingActual, Confirming;
        public Action<ushort> RebuildHull;
        public Action<MsgType, INetMessage, NetPeer> Dispatch;
        private bool Connected => _net.State == LinkState.Connected;
        internal static bool Suspended(ushort id) => Instance != null && Instance._hulls.TryGetValue(id, out var hull) &&
            (hull.Editing || Time.frameCount < hull.ReadyFrame);
        internal static bool PendingClean(CleanableObject target)
        {
            if (Instance == null || target == null) return false;
            foreach (var hull in Instance._hulls.Values)
                if (hull.Custom != null && hull.Gate.Pending && target.transform.IsChildOf(hull.Custom.transform)) return true;
            return false;
        }
        private Hull Bind(ushort id)
        {
            var boat = BoatLocator.FindByIndex(id);
            var custom = boat != null ? (boat.GetComponent<SaveableBoatCustomization>() ?? boat.GetComponentInParent<SaveableBoatCustomization>() ?? boat.GetComponentInChildren<SaveableBoatCustomization>(true)) : null;
            if (custom == null) return null;
            if (_hulls.TryGetValue(id, out var hull) && hull.Boat == boat) return hull;
            hull = new Hull { Id = id, Boat = boat, Custom = custom }; _hulls[id] = hull;
            hull.Committed = Capture(hull); return hull;
        }
        private SaveBoatCustomizationData Actual(Hull hull)
        {
            bool old = ReadingActual; ReadingActual = true;
            try { return hull.Custom.GetData(); }
            finally { ReadingActual = old; }
        }
        private RefitStateMsg Capture(Hull hull)
        {
            var data = Actual(hull);
            // GetData includes editor previews. A paid command that faults midway must not install
            // every remaining uninstalled preview on the host as though the command had succeeded.
            var installed = new List<bool>();
            foreach (var mast in hull.Custom.GetComponent<BoatRefs>().masts)
                if (mast != null && mast.gameObject.activeSelf)
                    foreach (var sail in mast.sails) installed.Add(sail != null && sail.GetComponent<Sail>() != null && sail.GetComponent<Sail>().IsInstalled());
            if (installed.Count != data.sails.Count) throw new InvalidOperationException("sail capture changed during refit");
            var damage = hull.Custom.GetComponent<BoatDamage>();
            return new RefitStateMsg { BoatIndex = hull.Id, Generation = BoatGenerationBook.Session.Get(hull.Id),
                Parts = data.partActiveOptions?.ToArray() ?? Array.Empty<int>(),
                Sails = data.sails.Where((s, i) => installed[i]).Select(s => new NetSailConfiguration { Prefab = s.prefabIndex, Mast = s.mastIndex,
                    Height = s.installHeight, MinAngle = s.minAngle, MaxAngle = s.maxAngle, Health = s.health,
                    Color = s.sailColor, ScaleY = s.scaleY, ScaleZ = s.scaleZ }).ToArray(),
                HullDamage = damage != null ? damage.hullDamage : 0, WaterLevel = damage != null ? damage.waterLevel : 0 };
        }
        internal static SaveBoatCustomizationData Data(RefitBody message)
        {
            var data = new SaveBoatCustomizationData(); data.partActiveOptions.AddRange(message.Parts);
            foreach (var s in message.Sails) data.sails.Add(new SaveSailData { prefabIndex = s.Prefab, mastIndex = s.Mast,
                installHeight = s.Height, minAngle = s.MinAngle, maxAngle = s.MaxAngle, health = s.Health,
                sailColor = s.Color, scaleY = s.ScaleY, scaleZ = s.ScaleZ });
            return data;
        }
        internal bool SavedPreview(SaveableBoatCustomization target, out SaveBoatCustomizationData data)
        {
            data = null; if (!Connected || ReadingActual || Confirming || target == null) return false;
            ushort id = ResolveHull(target.transform);
            if (!_hulls.TryGetValue(id, out var hull) || !hull.Editing || hull.Committed == null) return false;
            data = Data(hull.Committed); return true;
        }
        internal void BeginPreview(Shipyard editor, GameObject boat)
        {
            if (!Connected || boat == null || InteractionContext.Suppressed) return;
            var hull = Bind(ResolveHull(boat.transform)); if (hull == null) return;
            hull.Committed = Capture(hull); hull.Editor = editor; hull.Editing = true;
            RebuildHull?.Invoke(hull.Id); // Release transient body flags before vanilla creates preview objects.
        }
        internal static ushort ResolveHull(Transform target)
        {
            for (var t = target; t != null; t = t.parent)
            { ushort id = BoatLocator.IndexOf(t); if (id != BoatLocator.NoBoat) return id; }
            ushort result = BoatLocator.NoBoat;
            if (target != null) foreach (var boat in BoatLocator.FindBoats()) if (boat != null && boat.IsChildOf(target))
            { if (result != BoatLocator.NoBoat) return BoatLocator.NoBoat; result = BoatLocator.IndexOf(boat); }
            return result;
        }
        internal void BeforeCancel(Shipyard editor)
        {
            var boat = editor?.GetCurrentBoat(); if (boat == null) return;
            if (!_hulls.TryGetValue(ResolveHull(boat.transform), out var hull)) return;
            if (hull.Deferred != null && !hull.Gate.Pending) Adopt(hull, hull.Deferred);
            ItemComponents.Set(editor, "originalData", Data(hull.Committed));
            if (hull.Committed.Clean || hull.Committed.Repair) ApplyResults(hull, hull.Committed);
        }
        internal void AfterCancel(Shipyard editor)
        {
            var boat = editor?.GetCurrentBoat(); if (boat == null) return;
            if (_hulls.TryGetValue(ResolveHull(boat.transform), out var hull)) ScheduleRebuild(hull);
        }
        internal void EndPreview(Shipyard editor)
        {
            foreach (var hull in _hulls.Values) if (hull.Editor == editor)
            { hull.Editing = false; hull.Editor = null; ScheduleRebuild(hull); }
        }
        internal void CommitLocal(Shipyard editor, bool repair, bool clean, bool fault)
        {
            if (!Connected || InteractionContext.Suppressed || editor?.GetCurrentBoat() == null) return;
            var hull = Bind(ResolveHull(editor.GetCurrentBoat().transform)); if (hull == null) return;
            var result = Capture(hull); result.Repair = repair; result.Clean = clean;
            if (fault) result.Outcome = RefitOutcome.PartialFault;
            if (clean) result.Dirt = DirtPainter.Capture(hull.Custom.GetComponent<SaveableObject>()?.GetCleanable());
            if (_net.Role == Role.Host)
            {
                result.Generation = BoatGenerationBook.Session.Next(hull.Id); hull.Committed = result;
                ScheduleRebuild(hull); _net.Broadcast(result, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                if (++_nextRefit == 0) ++_nextRefit; hull.Gate.Begin(_nextRefit);
                var request = new RefitRequestMsg(); Copy(result, request); request.RequestId = _nextRefit;
                _net.Broadcast(request, DeliveryMethod.ReliableOrdered);
            }
            Plugin.Logger.LogInfo("[Shipyard] committed boat=" + hull.Id + " request=" + _nextRefit + " repair=" + repair + " clean=" + clean);
        }
        private static void Copy(RefitBody source, RefitBody target)
        {
            target.BoatIndex = source.BoatIndex; target.Generation = source.Generation; target.Parts = source.Parts;
            target.Sails = source.Sails; target.RequestId = source.RequestId; target.Requester = source.Requester;
            target.Repair = source.Repair; target.Clean = source.Clean; target.HullDamage = source.HullDamage;
            target.WaterLevel = source.WaterLevel; target.Dirt = source.Dirt; target.Outcome = source.Outcome; target.Baseline = source.Baseline;
        }
        private void ScheduleRebuild(Hull hull)
        {
            hull.ReadyFrame = Time.frameCount + 2; RebuildHull?.Invoke(hull.Id);
            Thaw(hull);
            foreach (var body in hull.Custom.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body.transform == hull.Boat || body.transform == hull.Custom.transform || body.GetComponentInParent<ShipItem>() != null) continue;
                hull.Frozen.Add(new KeyValuePair<Rigidbody, bool>(body, body.isKinematic)); body.isKinematic = true;
            }
        }
        private static void Thaw(Hull hull)
        { foreach (var pair in hull.Frozen) if (pair.Key != null) pair.Key.isKinematic = pair.Value; hull.Frozen.Clear(); }
        private void ApplyConfiguration(Hull hull, RefitBody message)
        {
            using (InteractionContext.Begin(InteractionSource.RemoteApply))
            {
                var refs = hull.Custom.GetComponent<BoatRefs>();
                var parts = hull.Custom.GetComponent<BoatCustomParts>();
                // Detach old objects synchronously: Unity Destroy completes only at end of frame.
                foreach (var mast in refs.masts) if (mast != null && mast.sails != null)
                    foreach (var old in mast.sails.ToArray()) if (old != null) { old.SetActive(false); mast.DetachSailFromMast(old); }
                if (parts != null) for (int i = 0; i < message.Parts.Length && i < parts.availableParts.Count; i++)
                {
                    var part = parts.availableParts[i]; if (part == null) continue;
                    int option = message.Parts[i];
                    if (option < 0 || option >= part.partOptions.Count || part.partOptions[option] == null) throw new IndexOutOfRangeException("missing boat part=" + i + " option=" + option);
                    part.activeOption = option;
                }
                parts?.RefreshParts();
                foreach (var sail in message.Sails)
                {
                    if (sail.Mast < 0 || sail.Mast >= refs.masts.Length || refs.masts[sail.Mast] == null ||
                        sail.Prefab < 0 || sail.Prefab >= PrefabsDirectory.instance.sails.Length || PrefabsDirectory.instance.sails[sail.Prefab] == null)
                        throw new IndexOutOfRangeException("missing sail prefab/mast=" + sail.Prefab + "/" + sail.Mast);
                    refs.masts[sail.Mast].LoadSail(Data(new RefitStateMsg { Sails = new[] { sail } }).sails[0]);
                }
                ApplyResults(hull, message);
                foreach (var pointer in UnityEngine.Object.FindObjectsOfType<GoPointer>())
                    if (pointer.GetHeldItem() != null && pointer.GetHeldItem().transform.IsChildOf(hull.Custom.transform) && !pointer.GetHeldItem().gameObject.activeInHierarchy) pointer.DropItem();
            }
        }
        private static void ApplyResults(Hull hull, RefitBody message)
        {
            if (message.Repair)
            { var damage = hull.Custom.GetComponent<BoatDamage>(); if (damage != null) { damage.hullDamage = message.HullDamage; damage.waterLevel = message.WaterLevel; } }
            if (message.Clean && message.Dirt.Length > 0)
                DirtSync.Instance?.ApplyRefit(hull.Custom.GetComponent<SaveableObject>()?.GetCleanable(), message.Dirt);
        }
        public void OnRefitRequest(RefitRequestMsg message, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer); if (actor == 0) return;
            Hull hull;
            try { hull = Bind(message.BoatIndex); }
            catch (Exception error)
            {
                Report("host refit bind boat=" + message.BoatIndex, error);
                peer.Send(new RefitStateMsg { BoatIndex = message.BoatIndex, Requester = actor, RequestId = message.RequestId,
                    Outcome = RefitOutcome.PartialFault }, DeliveryMethod.ReliableOrdered); return;
            }
            if (hull == null) { peer.Send(new RefitStateMsg { BoatIndex = message.BoatIndex, Requester = actor,
                RequestId = message.RequestId, Outcome = RefitOutcome.MissingObject }, DeliveryMethod.ReliableOrdered); return; }
            if (message.Baseline) { peer.Send(hull.Committed, DeliveryMethod.ReliableOrdered); return; }
            if (Suspended(hull.Id)) { _refitWaiting.Add(new Packet { Type = message.Type, Message = message, Peer = peer }); return; }
            var result = new RefitStateMsg { BoatIndex = hull.Id, Requester = actor, RequestId = message.RequestId };
            if (!_refits.TryBegin(actor, message.RequestId, result, out var previous)) { peer.Send(previous, DeliveryMethod.ReliableOrdered); return; }
            result.Outcome = RefitOutcome.PartialFault;
            try { RebuildHull?.Invoke(hull.Id); ApplyConfiguration(hull, message); result.Outcome = RefitOutcome.Applied; }
            catch (Exception error) { Report("host refit boat=" + hull.Id, error); }
            finally
            {
                try
                {
                    var actual = Capture(hull); actual.Repair = message.Repair; actual.Clean = message.Clean;
                    if (actual.Clean) actual.Dirt = DirtPainter.Capture(hull.Custom.GetComponent<SaveableObject>()?.GetCleanable());
                    actual.Generation = BoatGenerationBook.Session.Next(hull.Id); actual.Requester = actor;
                    actual.RequestId = message.RequestId; actual.Outcome = result.Outcome; Copy(actual, result);
                    hull.Committed = result; _net.Broadcast(result, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception error) { Report("refit result capture boat=" + hull.Id, error); result.Generation = 0; peer.Send(result, DeliveryMethod.ReliableOrdered); }
                finally { ScheduleRebuild(hull); }
            }
        }
        public void OnRefitState(RefitStateMsg message, NetPeer peer)
        {
            if (_net.Role != Role.Client || !_net.IsHostPeer(peer)) return;
            if (message.Outcome == RefitOutcome.MissingObject || message.Generation == 0)
            {
                if (_hulls.TryGetValue(message.BoatIndex, out var previous)) previous.Gate.Cancel(message.Requester, message.RequestId, _net.MyNetId);
                Plugin.Logger.LogWarning("[Shipyard] host result boat=" + message.BoatIndex + " request=" + message.RequestId + " outcome=" + message.Outcome);
                return;
            }
            var hull = Bind(message.BoatIndex);
            if (hull == null) { _refitWaiting.Add(new Packet { Type = message.Type, Message = message, Peer = peer }); return; }
            if (!hull.Gate.Receive(message.Generation, 0, message.Requester, message.RequestId, _net.MyNetId, out _, out _)) return;
            if (hull.Editing) { hull.Deferred = message; return; }
            hull.Deferred = message; // A Unity bind/apply fault keeps the full result retryable.
            Install(hull, message);
        }
        private void Adopt(Hull hull, RefitStateMsg message)
        {
            hull.Committed = message; hull.Deferred = null; BoatGenerationBook.Session.Set(hull.Id, message.Generation);
        }
        private void Install(Hull hull, RefitStateMsg message)
        {
            if (SameConfiguration(Capture(hull), message))
            {
                bool changedGeneration = BoatGenerationBook.Session.Get(hull.Id) != message.Generation;
                ApplyResults(hull, message); Adopt(hull, message);
                if (changedGeneration) ScheduleRebuild(hull);
                return;
            }
            RebuildHull?.Invoke(hull.Id);
            try { ApplyConfiguration(hull, message); Adopt(hull, message); }
            finally { ScheduleRebuild(hull); }
        }
        private static bool SameConfiguration(RefitBody a, RefitBody b)
        {
            if (!a.Parts.SequenceEqual(b.Parts) || a.Sails.Length != b.Sails.Length) return false;
            for (int i = 0; i < a.Sails.Length; i++)
            {
                var x = a.Sails[i]; var y = b.Sails[i];
                if (x.Prefab != y.Prefab || x.Mast != y.Mast || x.Color != y.Color || !x.Height.Equals(y.Height) ||
                    !x.MinAngle.Equals(y.MinAngle) || !x.MaxAngle.Equals(y.MaxAngle) || !x.ScaleY.Equals(y.ScaleY) || !x.ScaleZ.Equals(y.ScaleZ)) return false;
            }
            return true;
        }
        internal bool DeferEmbedded(IBoatLayoutMessage message, Action apply)
        {
            if (message.LayoutGeneration == 0) message.LayoutGeneration = BoatGenerationBook.Session.Get(message.LayoutBoat);
            var order = BoatGenerationBook.Session.Compare(message.LayoutBoat, message.LayoutGeneration);
            if (order == GenerationOrder.Past) return true;
            if (order == GenerationOrder.Future || Suspended(message.LayoutBoat))
            { _embedded.Add(message.LayoutBoat, message.LayoutGeneration, apply); return true; }
            return false;
        }
        public bool Defer(MsgType type, INetMessage message, NetPeer peer)
        {
            if (!(message is IBoatLayoutMessage indexed)) return false;
            var order = BoatGenerationBook.Session.Compare(indexed.LayoutBoat, indexed.LayoutGeneration);
            if (order == GenerationOrder.Past) return true;
            if (order == GenerationOrder.Future || Suspended(indexed.LayoutBoat))
            {
                // Unreliable packets are loss-tolerant by contract: keep only the newest of each type.
                // A carried mooring part travels one per packet, so its address is part of the key.
                uint coalesce = _net.ReceivingUnreliable ? (uint)type : 0u;
                if (coalesce != 0 && message is MooringCarryBody carry)
                    coalesce |= ((uint)carry.Index << 8) | ((uint)carry.Part << 24);
                _waiting.Add(indexed.LayoutBoat, indexed.LayoutGeneration, new Packet { Type = type, Message = message, Peer = peer }, coalesce);
                return true;
            }
            return false;
        }
        public void Tick(float dt)
        {
            if (!Connected || !GameState.playing || GameState.currentlyLoading) return;
            // A missing hull may load later. Preserve its result until the native registry is ready.
            var refits = _refitWaiting.ToArray(); _refitWaiting.Clear();
            using (InteractionContext.Begin(InteractionSource.RemoteApply))
            foreach (var packet in refits)
            {
                if (packet.Peer == null || packet.Peer.ConnectionState != ConnectionState.Connected) continue;
                if (packet.Message is RefitRequestMsg request) OnRefitRequest(request, packet.Peer);
                else OnRefitState((RefitStateMsg)packet.Message, packet.Peer);
            }
            foreach (var hull in _hulls.Values)
            {
                if (!hull.Editing && !hull.Gate.Pending && hull.Deferred != null)
                    PatchGuard.Run(() => Install(hull, hull.Deferred), e => Report("client refit retry boat=" + hull.Id, e));
                if (!Suspended(hull.Id)) Thaw(hull);
            }
            // Same origin as inside PollEvents: a replayed packet is remote state, never local input.
            using (InteractionContext.Begin(InteractionSource.RemoteApply))
            {
                foreach (var apply in _embedded.TakeReady(BoatGenerationBook.Session, Suspended)) apply();
                foreach (var packet in _waiting.TakeReady(BoatGenerationBook.Session, Suspended))
                    if (packet.Peer != null && packet.Peer.ConnectionState == ConnectionState.Connected)
                        Dispatch?.Invoke(packet.Type, packet.Message, packet.Peer);
            }
        }
        public void SendBaseline(NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            foreach (var boat in BoatLocator.FindBoats())
            {
                var hull = Bind(BoatLocator.IndexOf(boat)); if (hull == null) continue;
                if (!hull.Editing) hull.Committed = Capture(hull);
                var baseline = new RefitStateMsg(); Copy(hull.Committed, baseline); baseline.Baseline = true;
                baseline.Requester = baseline.RequestId = 0; baseline.Repair = baseline.Clean = false;
                peer.Send(baseline, DeliveryMethod.ReliableOrdered);
            }
        }
        public void Clear()
        {
            foreach (var hull in _hulls.Values) if (hull.Editing && hull.Boat != null && GameState.playing && !GameState.currentlyLoading)
                PatchGuard.Run(() => { hull.Custom.LoadData(Data(hull.Committed)); }, e => Report("preview cleanup", e));
            foreach (var hull in _hulls.Values) Thaw(hull);
            _hulls.Clear(); _waiting.Clear(); _refitWaiting.Clear(); _embedded.Clear(); _refits.Clear(); _nextRefit = 0;
            ReadingActual = Confirming = false; BoatGenerationBook.Session.Clear();
        }
        private static int _faultWarnings;
        internal static void Report(string stage, Exception error)
        { try { if (Plugin.Logger != null && Plugin.Logger.ShouldReport(ref _faultWarnings)) Plugin.Logger.LogWarning("[Shipyard] " + stage + ": " + error); } catch { } }
        internal static void ReleaseControls(ushort id)
        {
            var boat = BoatLocator.FindByIndex(id); if (boat == null) return;
            using (InteractionContext.Begin(InteractionSource.RemoteApply))
            foreach (var pointer in UnityEngine.Object.FindObjectsOfType<GoPointer>())
            {
                foreach (string name in new[] { "clickedButton", "stickyClickedButton" })
                {
                    var button = ItemComponents.Read<GoPointerButton>(pointer, name);
                    if (button == null || !button.transform.IsChildOf(boat)) continue;
                    PatchGuard.Run(() => {
                        button.OnUnactivate(); button.OnUnactivate(pointer); button.Unclick(); button.UnStickyClick();
                        ItemComponents.Set(pointer, name, null);
                    }, e => Report("refit release boat=" + id, e));
                }
            }
        }
        // Called for every sail in the scene on every physics step: resolve the hull only while one is held.
        internal static bool PauseSail(Sail sail) => AnySuspended() && sail != null && sail.shipRigidbody != null && Suspended(ResolveHull(sail.shipRigidbody.transform));
        private static bool AnySuspended()
        {
            if (Instance == null || Instance._hulls.Count == 0) return false;
            int frame = Time.frameCount;
            foreach (var hull in Instance._hulls.Values) if (hull.Editing || frame < hull.ReadyFrame) return true;
            return false;
        }
    }
}
