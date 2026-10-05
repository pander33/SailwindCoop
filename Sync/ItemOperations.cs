using System;
using System.Collections.Generic;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    public sealed partial class ItemSync
    {
        private float _lastPipeRequest;
        internal bool IsSessionActive => _net.State == LinkState.Connected && _net.Role != Role.None;
        internal bool IsRemoteSimulationItem(ShipItem item) => IsClientSession && item != null && _hostIds.Contains(InstanceIdOf(item));
        internal void NotifyPipeUse(ShipItem item)
        {
            if (InteractionContext.Suppressed || UnityEngine.Time.unscaledTime - _lastPipeRequest < 0.25f) return;
            if (!_byItem.TryGetValue(item, out var entry)) return;
            _lastPipeRequest = UnityEngine.Time.unscaledTime;
            var request = BuildRequest(entry, ItemAction.State, _net.Clock.ServerTick);
            request.IsInteraction = false;
            _net.Broadcast(request, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
        private bool _operationApplying;
        private readonly HashSet<ShipItem> _destroyingItems = new HashSet<ShipItem>();
        private sealed class PendingItemResult
        {
            internal ItemOperationResultMsg Message;
            internal readonly ItemResultProgress Progress = new ItemResultProgress();
        }
        private readonly Dictionary<ulong, PendingItemResult> _receivedOperations = new Dictionary<ulong, PendingItemResult>();
        private readonly List<PendingItemResult> _unfinishedResults = new List<PendingItemResult>();
        internal void MarkOperationDestroy(ShipItem item) { if (item != null) _destroyingItems.Add(item); }
        private readonly OperationLedger<ItemOperationResultMsg> _operationResults = new OperationLedger<ItemOperationResultMsg>();
        private readonly Dictionary<uint, Dictionary<int, ShipItem>> _operationLocalCreated = new Dictionary<uint, Dictionary<int, ShipItem>>();
        private readonly Dictionary<ulong, ItemOperationRequestMsg> _waitingOperations = new Dictionary<ulong, ItemOperationRequestMsg>();

        private bool IsOperationCreated(ShipItem item)
        {
            foreach (var created in _operationLocalCreated.Values)
                if (created.ContainsValue(item)) return true;
            return false;
        }

        internal Dictionary<ShipItem, ItemStateMsg> CaptureOperationItems()
        {
            RefreshItems(force: true);
            var result = new Dictionary<ShipItem, ItemStateMsg>();
            foreach (var e in _items)
                if (e.Item != null && (_net.Role == Role.Host || _hostIds.Contains(e.InstanceId))) result[e.Item] = BuildState(e, _net.Clock.ServerTick);
            return result;
        }
        internal ItemStateMsg CaptureOperationItem(ShipItem item)
        {
            ItemEntry e;
            if (!_byItem.TryGetValue(item, out e)) e = new ItemEntry { Item = item, InstanceId = InstanceIdOf(item), PrefabIndex = PrefabIndexOf(item) };
            return BuildState(e, _net.Clock.ServerTick);
        }
        internal void SendOperation(Dictionary<ShipItem, ItemStateMsg> before, HashSet<int> consumed,
            HashSet<int> originalObjects, List<DamageRequestMsg> damage, string label, byte worldEffects, float totemAttraction)
        {
            if (_net.State != LinkState.Connected) return;
            var changed = new List<ItemStateMsg>();
            foreach (var pair in before)
            {
                if (consumed.Contains(pair.Value.InstanceId)) continue;
                if (pair.Key == null) { consumed.Add(pair.Value.InstanceId); continue; }
                var after = CaptureOperationItem(pair.Key);
                if (!ItemSemanticState.Equal(pair.Value, after)) changed.Add(after);
            }
            var created = new List<CreatedItemState>();
            var localCreated = new Dictionary<int, ShipItem>();
            if (originalObjects != null)
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                    if (item != null && !originalObjects.Contains(item.GetInstanceID()) && HasStableIdentity(item))
                    {
                        var state = CaptureOperationItem(item);
                        created.Add(new CreatedItemState { LocalId = state.InstanceId, State = state });
                        localCreated[state.InstanceId] = item;
                    }
            if (changed.Count == 0 && consumed.Count == 0 && created.Count == 0 && damage.Count == 0 && worldEffects == 0) return;
            if (++_nextRequest == 0) ++_nextRequest;
            uint operation = _nextRequest;
            foreach (var state in changed)
            {
                state.RequestId = operation;
                if (_byInstanceId.TryGetValue(state.InstanceId, out var entry))
                { entry.LastLocalRequest = operation; entry.Gate.Begin(operation); }
            }
            var dead = new List<int>(consumed).ToArray();
            if (_net.Role == Role.Client)
            {
                _operationLocalCreated[operation] = localCreated;
                _net.Broadcast(new ItemOperationRequestMsg {
                    OperationId = operation, WorldEffects = worldEffects, TotemAttraction = totemAttraction, Changed = changed.ToArray(), Created = created.ToArray(),
                    Consumed = dead, Damage = damage.ToArray()
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                foreach (int id in dead) _tombstones.Add(id);
                foreach (var state in changed) { state.Requester = _net.MyNetId; state.RequestId = operation; }
                var hull = new List<BoatDamageStateMsg>();
                foreach (var request in damage)
                { var state = BoatDamageSync.Instance?.OperationState(request.BoatIndex); if (state != null) hull.Add(state); }
                _net.Broadcast(new ItemOperationResultMsg {
                    Actor = _net.MyNetId, OperationId = operation, WorldEffects = worldEffects, TotemAttraction = totemAttraction,
                    WeatherRevision = WeatherStormSync.Instance?.CaptureAttraction() ?? 0, WorldTick = _net.Clock.ServerTick,
                    Hull = hull.ToArray(), Changed = changed.ToArray(),
                    Created = created.ToArray(), Consumed = dead
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            Remember("out operation " + label + " #" + operation);
        }

        public void OnOperationRequest(ItemOperationRequestMsg msg, LiteNetLib.NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer);
            if (actor == 0) return;
            msg.Actor = actor; // never trust a packet's claimed author.
            ApplyOperation(msg, peer);
        }
        private void ApplyOperation(ItemOperationRequestMsg msg, LiteNetLib.NetPeer peer)
        {
            RefreshItems(force: true);
            ulong key = ((ulong)msg.Actor << 32) | msg.OperationId;
            if (_operationResults.TryGet(msg.Actor, msg.OperationId, out var retained))
            { if (peer != null) peer.Send(retained, LiteNetLib.DeliveryMethod.ReliableOrdered); return; }
            // Bind all existing targets before applying any effects; loading is a technical wait.
            foreach (var state in msg.Changed)
                if (!_tombstones.Contains(state.InstanceId))
                {
                    var item = HostLookup(state.InstanceId, state.PrefabIndex)?.Item;
                    if (item == null || !ItemComponents.BindingsReady(item, state.Details) || !MembershipTargetsReady(item, state))
                    { WaitForOperation(msg, peer, key); return; }
                }
            foreach (var damage in msg.Damage)
                if (BoatGenerationBook.Session.Compare(damage.BoatIndex, damage.Generation) != GenerationOrder.Past &&
                    !OperationHullReady(damage.BoatIndex, damage.Generation))
                { WaitForOperation(msg, peer, key); return; }
            foreach (int id in msg.Consumed)
                if (!_tombstones.Contains(id) && (!_byInstanceId.TryGetValue(id, out var target) || target.Item == null))
                { WaitForOperation(msg, peer, key); return; }
            foreach (var creation in msg.Created)
            {
                var state = creation.State;
                if (!CanCreateOperationItem(state) || !ItemComponents.BindingsReady(PrefabsDirectory.instance.directory[state.PrefabIndex].GetComponent<ShipItem>(), state.Details) ||
                    !CreationMembershipTargetsReady(state))
                { WaitForOperation(msg, peer, key); return; }
            }
            if ((msg.WorldEffects & 1) != 0 && Rainbow.instance == null)
            { WaitForOperation(msg, peer, key); return; }
            var result = new ItemOperationResultMsg { Actor = msg.Actor, OperationId = msg.OperationId };
            ItemOperationResultMsg previous;
            if (!_operationResults.TryBegin(msg.Actor, msg.OperationId, result, out previous))
            { if (peer != null) peer.Send(previous, LiteNetLib.DeliveryMethod.ReliableOrdered); return; }
            _waitingOperations.Remove(key);
            var changed = new List<ItemStateMsg>(); var created = new List<CreatedItemState>();
            var consumed = new List<int>(); var hull = new List<BoatDamageStateMsg>();
            _operationApplying = true;
            try
            {
                using (InteractionContext.Begin(InteractionSource.RemoteApply))
                {
                    foreach (var state in msg.Changed)
                    {
                        var e = HostLookup(state.InstanceId, state.PrefabIndex);
                        if (e?.Item == null) { result.Status = ItemOperationBody.MissingTarget; continue; }
                        // Results are absolute. No owner/distance/amount/cost eligibility is introduced.
                        try
                        {
                            ApplyScalarState(e.Item, state.Amount, state.Health, state.Sold, state.Nailed);
                            ItemComponents.Apply(e.Item, state.Details);
                            if (ItemComponents.HasPendingBinding(e.Item)) throw new InvalidOperationException("Item component binding remains pending");
                            if (e.Item is ShipItemLight light) LightSync.ApplyState(light, state.LightOn, state.Health);
                            if (TryApplyCrateMembership(e.Item, state.CrateId) != ItemApplyStatus.Applied ||
                                TryApplyCargoMembership(e.Item, state.CargoPort) != ItemApplyStatus.Applied)
                                throw new InvalidOperationException("Item membership was not applied");
                        }
                        finally
                        {
                            e.AckRequester = msg.Actor; e.AckRequest = msg.OperationId;
                            changed.Add(BuildState(e, _net.Clock.ServerTick));
                        }
                    }
                    foreach (var creation in msg.Created)
                    {
                        var item = CreateOperationItem(creation.State);
                        if (item == null) { result.Status = ItemOperationBody.MissingTarget; continue; }
                        try
                        {
                            ApplyScalarState(item, creation.State.Amount, creation.State.Health, creation.State.Sold, creation.State.Nailed);
                            ItemComponents.Apply(item, creation.State.Details);
                            if (ItemComponents.HasPendingBinding(item)) throw new InvalidOperationException("Created item binding remains pending");
                            if (TryApplyCrateMembership(item, creation.State.CrateId) != ItemApplyStatus.Applied ||
                                TryApplyCargoMembership(item, creation.State.CargoPort) != ItemApplyStatus.Applied)
                                throw new InvalidOperationException("Created item membership was not applied");
                        }
                        finally
                        {
                            RefreshItems(force: true);
                            var state = BuildState(_byItem[item], _net.Clock.ServerTick);
                            created.Add(new CreatedItemState { LocalId = creation.LocalId, State = state });
                        }
                    }
                    foreach (int id in msg.Consumed)
                    {
                        if (_byInstanceId.TryGetValue(id, out var e) && e.Item != null)
                        { e.Item.DestroyItem(); _destroyingItems.Add(e.Item); }
                        _tombstones.Add(id); consumed.Add(id);
                    }
                    foreach (var damage in msg.Damage)
                    {
                        if (BoatGenerationBook.Session.Compare(damage.BoatIndex, damage.Generation) == GenerationOrder.Past)
                        { result.Status = ItemOperationBody.MissingTarget; continue; }
                        try { BoatDamageSync.Instance.ApplyOperationDamage(damage); }
                        finally
                        {
                            var state = BoatDamageSync.Instance.OperationState(damage.BoatIndex);
                            if (state != null) hull.Add(state);
                        }
                    }
                    if ((msg.WorldEffects & 1) != 0)
                    { Rainbow.instance.ForceShowRainbow(); result.WorldEffects |= 1; }
                    if ((msg.WorldEffects & 2) != 0)
                    {
                        WeatherStorms.totemAttraction = msg.TotemAttraction;
                        result.TotemAttraction = msg.TotemAttraction; result.WorldTick = _net.Clock.ServerTick;
                        result.WeatherRevision = WeatherStormSync.Instance?.CaptureAttraction() ?? 0;
                        result.WorldEffects |= 2;
                    }
                }
            }
            catch (Exception e)
            { result.Status = ItemOperationBody.PartialFault; Plugin.Logger.LogWarning("[ItemSync] Partial operation actor=" + msg.Actor + " id=" + msg.OperationId + ": " + e); }
            finally
            {
                // Ledger retains exactly what succeeded, including after an exception; retry cannot rerun effects.
                result.Hull = hull.ToArray(); result.Changed = changed.ToArray(); result.Created = created.ToArray(); result.Consumed = consumed.ToArray();
                _operationApplying = false;
            }
            _net.Broadcast(result, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("in operation #" + msg.OperationId + " actor=" + msg.Actor + " status=" + result.Status);
        }
        private void WaitForOperation(ItemOperationRequestMsg msg, LiteNetLib.NetPeer peer, ulong key)
        {
            _waitingOperations[key] = msg;
            if (peer != null) peer.Send(new ItemOperationResultMsg {
                Actor = msg.Actor, OperationId = msg.OperationId, Status = ItemOperationBody.WaitingTarget
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
        /// <summary>Host: a request still waiting for its targets must not change the world after its
        /// author left. Results already applied stay in the ledger.</summary>
        private void ForgetWaitingOperations(uint actor)
        {
            if (_waitingOperations.Count == 0) return;
            int previous = _waitingOperations.Count;
            ItemOperationWaiting.RemoveActor(_waitingOperations, actor);
            int dropped = previous - _waitingOperations.Count;
            if (dropped != 0)
                Plugin.Logger.LogInfo("[ItemSync] role=" + _net.Role + " dropped " + dropped + " waiting operation(s) of player " + actor);
        }
        private ShipItem CreateOperationItem(ItemStateMsg state)
        {
            var directory = PrefabsDirectory.instance?.directory;
            if (directory == null || state.PrefabIndex < 0 || state.PrefabIndex >= directory.Length || directory[state.PrefabIndex] == null) return null;
            if (!WireToWorld(state.Frame, state.BoatIndex, state.Pos, state.Rot, out var pos, out var rot)) return null;
            var go = UnityEngine.Object.Instantiate(directory[state.PrefabIndex], pos, rot);
            var item = go.GetComponent<ShipItem>(); var saveable = go.GetComponent<SaveablePrefab>();
            if (item == null || saveable == null) { UnityEngine.Object.Destroy(go); return null; }
            item.sold = state.Sold; saveable.prefabIndex = state.PrefabIndex; saveable.RegisterToSave();
            return item;
        }
        private bool CreationMembershipTargetsReady(ItemStateMsg state)
        {
            var carriers = CargoCarrier.carriers;
            return (state.CrateId == 0 || CrateInventoryFor(state.CrateId) != null) &&
                (state.CargoPort < 0 || (carriers != null && state.CargoPort < carriers.Length &&
                    carriers[state.CargoPort] != null && carriers[state.CargoPort].cargo != null));
        }
        private static bool CanCreateOperationItem(ItemStateMsg state)
        {
            var directory = PrefabsDirectory.instance?.directory;
            return directory != null && state.PrefabIndex > 0 && state.PrefabIndex < directory.Length &&
                directory[state.PrefabIndex] != null && WireToWorld(state.Frame, state.BoatIndex, state.Pos, state.Rot, out _, out _);
        }
        public void OnOperationResult(ItemOperationResultMsg msg, LiteNetLib.NetPeer peer)
        {
            if (_net.Role != Role.Client) return;
            if (msg.Status == ItemOperationBody.WaitingTarget) return;
            ulong key = ((ulong)msg.Actor << 32) | msg.OperationId;
            if (!_receivedOperations.TryGetValue(key, out var pending))
            {
                pending = new PendingItemResult { Message = msg };
                _receivedOperations.Add(key, pending);
                if (msg.Status != 0) Plugin.Logger.LogWarning("[ItemSync] Operation result actor=" + msg.Actor + " id=" + msg.OperationId + " status=" + msg.Status);
                ApplyOperationResult(pending);
                if (!pending.Progress.Completed) _unfinishedResults.Add(pending);
                return;
            }
            ApplyOperationResult(pending);
        }
        private void ApplyOperationResult(PendingItemResult pending)
        {
            var progress = pending.Progress;
            if (!progress.Begin()) return;
            var msg = pending.Message;
            bool complete = true;
            bool finished = false;
            Action<Exception> fault = error => Plugin.Logger.LogWarning("[ItemSync] Result apply actor=" + msg.Actor + " id=" + msg.OperationId + ": " + error);
            try
            {
                using (InteractionContext.Begin(InteractionSource.RemoteApply))
                {
                    Dictionary<int, ShipItem> local = null;
                    if (msg.Actor == _net.MyNetId) _operationLocalCreated.TryGetValue(msg.OperationId, out local);
                    for (int index = 0; index < msg.Created.Length; index++)
                    {
                        var creation = msg.Created[index];
                        complete &= progress.TryApply(ItemResultSection.Identity, index, () => {
                            var state = creation.State;
                            if (_tombstones.Contains(state.InstanceId)) return true;
                            if (!_baselineReady) return false;
                            if (local != null && local.TryGetValue(creation.LocalId, out var item) && item != null)
                            {
                                RemapLocalItem(item, state.InstanceId);
                                _hostIds.Add(state.InstanceId);
                                RefreshItems(force: true);
                            }
                            return ResolveClient(state.InstanceId, state.PrefabIndex, state.Frame, state.BoatIndex,
                                state.Pos, state.Amount, state.Health, state.Sold, state.Nailed, true)?.Item != null;
                        }, fault);
                    }
                    if (!complete) return;
                    for (int index = 0; index < msg.Created.Length; index++)
                    {
                        var state = msg.Created[index].State;
                        complete &= progress.TryApply(ItemResultSection.CreatedState, index, () => ApplyResultItemState(state), fault);
                    }
                    for (int index = 0; index < msg.Changed.Length; index++)
                    {
                        var state = msg.Changed[index];
                        complete &= progress.TryApply(ItemResultSection.Changed, index, () => ApplyResultItemState(state), fault);
                    }
                    for (int index = 0; index < msg.Hull.Length; index++)
                    {
                        var state = msg.Hull[index];
                        complete &= progress.TryApply(ItemResultSection.Hull, index, () => {
                            if (BoatGenerationBook.Session.Compare(state.BoatIndex, state.Generation) == GenerationOrder.Past) return true;
                            if (!OperationHullReady(state.BoatIndex, state.Generation)) return false;
                            BoatDamageSync.Instance.ApplyOperationState(state); return true;
                        }, fault);
                    }
                    if (msg.Actor != _net.MyNetId && (msg.WorldEffects & 1) != 0)
                        complete &= progress.TryApply(ItemResultSection.Rainbow, 0, () => {
                            if (Rainbow.instance == null) return false;
                            Rainbow.instance.ForceShowRainbow(); return true;
                        }, fault);
                    if ((msg.WorldEffects & 2) != 0)
                        complete &= progress.TryApply(ItemResultSection.Weather, 0, () => {
                            if (WeatherStormSync.Instance == null) return false;
                            WeatherStormSync.Instance.ApplyAttraction(msg.TotemAttraction, msg.WeatherRevision, msg.WorldTick); return true;
                        }, fault);
                    for (int index = 0; index < msg.Consumed.Length; index++)
                    {
                        int id = msg.Consumed[index];
                        complete &= progress.TryApply(ItemResultSection.Consumed, index, () => {
                            OnDespawnObject(new DespawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = id }, null); return true;
                        }, fault);
                    }
                    if (complete && msg.Actor == _net.MyNetId)
                    {
                        if (msg.Status != ItemOperationBody.Applied && local != null)
                        {
                            foreach (var pair in local)
                            {
                                bool committed = false;
                                foreach (var creation in msg.Created)
                                    if (creation.LocalId == pair.Key) { committed = true; break; }
                                if (!committed && pair.Value != null)
                                    complete &= progress.TryApply(ItemResultSection.RejectedCreation, pair.Key, () => {
                                        ItemComponents.RemoveBindings(pair.Value);
                                        UnityEngine.Object.Destroy(pair.Value.gameObject); return true;
                                    }, fault);
                            }
                        }
                        if (!complete) return;
                        _operationLocalCreated.Remove(msg.OperationId);
                        if (msg.Status != 0)
                            foreach (var entry in _items) entry.Gate.Cancel(msg.Actor, msg.OperationId, _net.MyNetId);
                    }
                    finished = complete;
                }
            }
            finally
            {
                progress.End(finished);
                // The key stays as the duplicate filter; the payload is no longer needed.
                if (finished) pending.Message = null;
            }
        }
        private bool ApplyResultItemState(ItemStateMsg state)
        {
            if (_tombstones.Contains(state.InstanceId)) return true;
            if (!_hostIds.Contains(state.InstanceId) || !_byInstanceId.TryGetValue(state.InstanceId, out var entry) || entry.Item == null) return false;
            if (entry.PrefabIndex != state.PrefabIndex) return false;
            if (entry.Gate.HasState && unchecked((int)(state.Revision - entry.Gate.Version)) < 0)
            { entry.Gate.Cancel(state.Requester, state.RequestId, _net.MyNetId); return true; }
            if (entry.Gate.Pending && (state.Requester != _net.MyNetId || state.RequestId != entry.Gate.PendingRequest))
                return unchecked((int)(entry.Gate.PendingRequest - state.RequestId)) > 0 && state.Requester == _net.MyNetId;
            if (TryApplyItemState(state) != ItemApplyStatus.Applied) return false;
            if (entry.Gate.Pending) return false;
            var details = state.Details;
            if (details != null && details.HasCookable && details.CookStoveId != 0)
            {
                var cook = entry.Item.GetComponent<CookableFood>();
                var trigger = cook != null ? cook.GetCurrentCookTrigger() : null;
                if (trigger == null || trigger.stove == null || InstanceIdOf(trigger.stove) != details.CookStoveId ||
                    Array.IndexOf(trigger.stove.slots, trigger) != details.CookSlot) return false;
            }
            if (details != null && details.HasFuel && details.FuelStoveId != 0)
            {
                var fuel = entry.Item.GetComponent<StoveFuel>();
                var trigger = fuel != null ? ItemComponents.Read<StoveFuelTrigger>(fuel, "fuelTrigger") : null;
                var stove = trigger != null ? trigger.GetComponentInParent<ShipItemStove>() : null;
                if (stove == null || InstanceIdOf(stove) != details.FuelStoveId) return false;
            }
            return true;
        }
        private static bool OperationHullReady(ushort boat, uint generation)
        {
            if (generation == 0) generation = BoatGenerationBook.Session.Get(boat);
            if (BoatDamageSync.Instance == null || BoatGenerationBook.Session.Compare(boat, generation) != GenerationOrder.Current) return false;
            var hull = BoatLocator.FindByIndex(boat);
            return hull != null && (hull.GetComponent<BoatDamage>() != null || hull.GetComponentInParent<BoatDamage>() != null ||
                hull.GetComponentInChildren<BoatDamage>(true) != null);
        }
        private void TickOperations()
        {
            if (_net.Role == Role.Client)
            {
                // Only results still waiting for a target; finished ones stay in _receivedOperations
                // as the duplicate filter and are never walked again.
                for (int i = _unfinishedResults.Count - 1; i >= 0; i--)
                {
                    var result = _unfinishedResults[i];
                    ApplyOperationResult(result);
                    if (result.Progress.Completed) _unfinishedResults.RemoveAt(i);
                }
                return;
            }
            if (_net.Role != Role.Host) return;
            if (_waitingOperations.Count == 0) return;
            var waiting = new List<ItemOperationRequestMsg>(_waitingOperations.Values);
            foreach (var operation in waiting) ApplyOperation(operation, null);
        }
        private void ClearOperations()
        {
            _lastPipeRequest = 0f; _operationApplying = false; _operationResults.Clear(); _waitingOperations.Clear(); _operationLocalCreated.Clear();
            _receivedOperations.Clear(); _unfinishedResults.Clear(); _destroyingItems.Clear();
            ItemOperationCapture.Clear();
        }
    }
}
