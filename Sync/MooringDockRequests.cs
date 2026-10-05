using System;
using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed partial class MooringSync
    {
        private sealed class DeferredDock { internal MooringRequestMsg Request; internal uint Actor; }
        private readonly Dictionary<ushort, DeferredDock> _dockRequests = new Dictionary<ushort, DeferredDock>();
        private static string DockIdentity(GPButtonDockMooring dock)
        {
            if (dock == null) return "";
            string path = "";
            for (var t = dock.transform; t != null; t = t.parent) path = "/" + t.name + "[" + t.GetSiblingIndex() + "]" + path;
            return dock.gameObject.scene.name + path;
        }
        private static GPButtonDockMooring FindDock(string id, Vector3 real)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (var dock in UnityEngine.Object.FindObjectsOfType<GPButtonDockMooring>())
                    if (DockIdentity(dock) == id) return dock;
            return FindDockNear(real); // Identity fallback locates a loaded object, not action eligibility.
        }
        private void SendWaitingDock(MooringRequestMsg msg, uint actor)
            => _net.Broadcast(new MooringStateMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = msg.Index,
                Kind = msg.Kind, DockId = msg.DockId, DockReal = msg.DockReal, LengthSq = msg.LengthSq,
                RequesterNetId = actor, RequestId = msg.RequestId, StateAvailable = false, Outcome = MooringOutcome.WaitingForDock }, DeliveryMethod.ReliableOrdered);
        private void SendMissingObject(MooringRequestMsg msg, uint actor)
            => _net.Broadcast(new MooringStateMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = msg.Index,
                RequesterNetId = actor, RequestId = msg.RequestId, StateAvailable = false, Outcome = MooringOutcome.MissingObject }, DeliveryMethod.ReliableOrdered);
        private void RetryDockRequests()
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected || _dockRequests.Count == 0) return;
            foreach (var pair in new List<KeyValuePair<ushort, DeferredDock>>(_dockRequests))
            {
                var msg = pair.Value.Request;
                if (FindDock(msg.DockId, msg.DockReal) == null) continue;
                if (!Apply(msg.Index, msg.Kind, msg.DockReal, msg.LengthSq, "in deferred request", applyMoorLength: false, dockId: msg.DockId)) continue;
                _dockRequests.Remove(pair.Key);
                SendRopeState(msg.Index, pair.Value.Actor, msg.RequestId);
            }
        }
        internal void NotifyThrow(PickupableBoatMooringRope rope, GPButtonDockMooring dock)
        {
            // The only Moor request of a client throw: its dock trigger is suppressed (SuppressCarryTrigger),
            // so the local MoorTo runs later, while the host state is applied, and sends nothing.
            if (_net.Role == Role.Client && InteractionContext.HasInput && !InteractionContext.Suppressed)
                NotifyLocalMoor(rope, dock);
        }
    }
}
