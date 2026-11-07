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
            // Each step is the name plus its ordinal among same-named siblings, not the raw sibling
            // index (as BoatLayout.Hash): a mooring point on a boat (Towable Boats' cleats) sits among
            // children that other objects join and leave, and mods may add them in another order.
            string path = "";
            for (var t = dock.transform; t != null; t = t.parent)
            {
                int ordinal = 0;
                if (t.parent != null)
                {
                    int index = t.GetSiblingIndex();
                    for (int i = 0; i < index; i++) if (t.parent.GetChild(i).name == t.name) ordinal++;
                }
                else
                {
                    try
                    {
                        foreach (var root in t.gameObject.scene.GetRootGameObjects())
                        {
                            if (root.transform == t) break;
                            if (root.name == t.name) ordinal++;
                        }
                    }
                    catch { ordinal = t.GetSiblingIndex(); } // a scene that cannot list its roots
                }
                path = "/" + t.name + "[" + ordinal + "]" + path;
            }
            return dock.gameObject.scene.name + path;
        }
        private static GPButtonDockMooring FindDock(string id, Vector3 real)
        {
            if (!string.IsNullOrEmpty(id))
            {
                // The full identity walks every ancestor's siblings and reads names (each read
                // allocates); a pending request retries every frame. Compare the dock's own name,
                // the last step of the identity, first.
                foreach (var dock in UnityEngine.Object.FindObjectsOfType<GPButtonDockMooring>())
                    if (EndsWithStep(id, dock.name) && DockIdentity(dock) == id) return dock;
            }
            // Identity fallback locates a loaded object, not action eligibility.
            var near = FindDockNear(real);
            // The position fallback assumes docks do not move. A mooring point on a boat (Towable
            // Boats) moves, and on an interpolated copy the nearest one can be the other cleat of a
            // pair: with an identity given, wait for that identity instead.
            if (near != null && !string.IsNullOrEmpty(id) && OnBoat(near.transform)) return null;
            return near;
        }
        /// <summary>True when the identity's last step is "/name[n]" for this name (names may hold '/' or '[').</summary>
        private static bool EndsWithStep(string id, string name)
        {
            string step = "/" + name + "[";
            int at = id.LastIndexOf(step, StringComparison.Ordinal);
            return at >= 0 && id.IndexOf('/', at + step.Length) < 0;
        }
        private static bool OnBoat(Transform t)
        {
            for (; t != null; t = t.parent) if (BoatLocator.IndexOf(t) != BoatLocator.NoBoat) return true;
            return false;
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
