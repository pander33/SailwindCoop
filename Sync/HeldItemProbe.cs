using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Client: gives the item in the local hand back the game's wall check.
    ///
    /// The game finds walls for a held item through its physics proxy (<c>ItemRigidbody</c>): the
    /// proxy follows the item into the boat's walk copy with its colliders as triggers, and the
    /// <c>PickupableItemCollisionChecker</c> on it counts what they touch. <c>GoPointer</c> then pushes
    /// a big item (a crate, cargo) out of the wall and refuses to drop any item that is stuck in one.
    /// On a client the item in the hand is a puppet (<see cref="ItemSync"/>): proxy disabled, colliders
    /// off, collisions off. The checker saw nothing, a crate went through bulkheads and the hull and
    /// could be let go inside them; the host then woke its physics there, the crate sank through the
    /// hull and, still counted on board by <c>BoatMass</c>, threw the boat's centre of mass far out —
    /// the boat rolled over or spun until the crate left it.
    ///
    /// Here the proxy of the local hand is a kinematic trigger again, placed each frame where the game
    /// would place it. Triggers push nothing, so the puppet rule (no shoving of other items) holds;
    /// the game's own checker and pointer do the rest. Whatever path ends the hand (drop, belt,
    /// disconnect) is followed by <see cref="Release"/>, which turns the proxy back into a puppet's.
    /// </summary>
    internal sealed class HeldItemProbe
    {
        private ShipItem _item;
        private readonly List<Collider> _colliders = new List<Collider>();
        /// <summary>Colliders this probe made triggers; they get their solid state back on release.</summary>
        private readonly List<Collider> _madeTrigger = new List<Collider>();
        private bool _failed;

        /// <summary>A new session: release the hand and try again after an earlier failure.</summary>
        internal void Reset()
        {
            Release();
            _failed = false;
        }

        /// <summary>This item's proxy is the probe now: it is still in the hand it was probed in.</summary>
        internal bool Holds(ShipItem item) => item != null && item == _item && item.held != null;

        internal void Tick(ShipItem held)
        {
            if (_failed) return;
            try
            {
                if (held != _item) Release();
                if (held == null || !Probes(held, out var irb, out var body)) { Release(); return; }
                _item = held;
                Place(held, irb.transform);
                irb.GetComponentsInChildren(true, _colliders);
                foreach (var col in _colliders)
                {
                    if (col == null) continue;
                    if (!col.isTrigger) { col.isTrigger = true; _madeTrigger.Add(col); }
                    if (!col.enabled) col.enabled = true;
                }
                _colliders.Clear();
                if (!body.detectCollisions) body.detectCollisions = true;
                ItemSync.PruneCollisionChecker(held);
            }
            catch (Exception e)
            {
                // One failure turns the probe off until the next session (ItemSync.Clear): the hand
                // then works as before.
                _failed = true;
                Plugin.Logger.LogWarning("[HeldItemProbe] disabled: " + e);
                Release();
            }
        }

        /// <summary>Turns the proxy of the last probed item back into a puppet's, unless the game's
        /// own ItemRigidbody drives it again (belt slot, disconnect), which sets its colliders itself.</summary>
        internal void Release()
        {
            var item = _item;
            _item = null;
            _colliders.Clear();
            // The game's ItemRigidbody sets isTrigger on the colliders it knows each physics step;
            // any other one under the proxy would otherwise stay a trigger and let the item fall
            // through the deck once it is the game's again (disconnect).
            foreach (var col in _madeTrigger)
                if (col != null) col.isTrigger = false;
            _madeTrigger.Clear();
            if (item == null) return;
            try
            {
                var irb = item.GetItemRigidbody();
                if (irb == null || irb.enabled) return;
                irb.GetComponentsInChildren(true, _colliders);
                foreach (var col in _colliders)
                    if (col != null && col.enabled) col.enabled = false;
                _colliders.Clear();
                var body = irb.GetBody();
                if (body != null) body.detectCollisions = false;
                // A disabled collider sends no OnTriggerExit: the checker would keep the walls.
                ItemSync.ClearCollisionChecker(item);
            }
            catch (Exception e)
            {
                _colliders.Clear();
                Plugin.Logger.LogWarning("[HeldItemProbe] release: " + e.Message);
            }
        }

        /// <summary>Only a puppet in the local hand: the game drives every other proxy itself.</summary>
        private static bool Probes(ShipItem item, out ItemRigidbody irb, out Rigidbody body)
        {
            irb = null; body = null;
            if (item.held == null || item.colChecker == null || !item.gameObject.activeInHierarchy) return false;
            if (ItemComponents.InStove(item)) return false;
            irb = item.GetItemRigidbody();
            if (irb == null || irb.enabled || irb.inStove || irb.GetCurrentInventorySlot() != null) return false;
            body = irb.GetBody();
            return body != null && body.isKinematic;
        }

        /// <summary>The game's placement of a held proxy (ItemRigidbody.MoveRigidbodyToWalkCol, or the
        /// item's own pose off a boat).</summary>
        private static void Place(ShipItem item, Transform proxy)
        {
            if (item.currentActualBoat != null && item.currentWalkCol != null)
            {
                Vector3 boatLocalPos = item.currentActualBoat.InverseTransformPoint(item.transform.position);
                Quaternion boatLocalRot = Quaternion.Inverse(item.currentActualBoat.rotation) * item.transform.rotation;
                proxy.position = item.currentWalkCol.TransformPoint(boatLocalPos);
                proxy.rotation = item.currentWalkCol.rotation * boatLocalRot;
            }
            else
            {
                proxy.position = item.transform.position;
                proxy.rotation = item.transform.rotation;
            }
        }
    }
}
