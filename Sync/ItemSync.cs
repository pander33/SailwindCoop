using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P3 foundation: host-authoritative replication for existing save-loaded ShipItem objects.
    /// Spawn/despawn and containers are later P3 passes; this layer gives pickup/drop/held-pose
    /// sync for items that exist in both peers' copies of the same save.
    /// </summary>
    public sealed class ItemSync
    {
        public static ItemSync Instance { get; private set; }

        private sealed class ItemEntry
        {
            public ushort Index;
            public int InstanceId;
            public int PrefabIndex;
            public uint NetId;
            public ShipItem Item;
            public readonly NetTransform Net = new NetTransform();
            public uint HolderNetId;
            public Vector3 LastPos;
            public long LastTick;
            public bool HaveLast;
            public CoordFrame LastFrame;
            public ushort LastBoatIndex;
            public bool WasActive;   // host: was streaming this item last tick (to send a final resting pose)
            public int InventorySlot = -1;
            public bool DropWithoutProxyVelocity;
            public bool ForceWorldPoseUntilDrop;
        }

        private sealed class PendingDynamicRelease
        {
            public ShipItem Item;
            public CoordFrame Frame;
            public Vector3 Vel;
            public float ReleaseAt;
            public string Reason;
        }

        private readonly CoopNet _net;
        private readonly List<ItemEntry> _items = new List<ItemEntry>();
        private readonly Dictionary<ShipItem, ItemEntry> _byItem = new Dictionary<ShipItem, ItemEntry>();
        private readonly Dictionary<int, ItemEntry> _byInstanceId = new Dictionary<int, ItemEntry>();
        private readonly Dictionary<ShipItem, uint> _localHeld = new Dictionary<ShipItem, uint>();
        private readonly List<ShipItem> _poseScratch = new List<ShipItem>();
        private readonly List<PendingDynamicRelease> _pendingDynamic = new List<PendingDynamicRelease>();
        private readonly HashSet<ShipItem> _suppressNextDrop = new HashSet<ShipItem>();

        // Client: host item ids we've "claimed" into our own belt. When the host's despawn (from our claim
        // request) echoes back, we keep the local item (it's now player-local) instead of destroying it.
        private readonly HashSet<int> _localClaimed = new HashSet<int>();
        // Client: a player-local belt item just withdrawn to hand and re-authored on the host; once the host
        // assigns it an id (remap), we send a Pickup so the host marks it held by us.
        private ShipItem _pendingHeldItem;

        private GoPointer _gp;
        private FieldInfo _fHeldItem;
        private static FieldInfo _fBoatCachedItems;
        private static FieldInfo _fShipItemCurrentBoatCollider;
        private static FieldInfo _fShipItemCurrentlyStayedEmbarkCol;
        private static FieldInfo _fItemRigidbodyOnBoat;
        private static FieldInfo _fColCheckerCollidedCols;
        private static MethodInfo _mShipItemExitBoat;
        private float _refreshTimer;
        private float _sendTimer;
        private float _heldPoseTimer;
        private float _extraTimer;
        private float _altHeldTimer;
        private bool _baselineReady;     // local world finished loading its save items
        private int _baselineCount = -1;
        private float _baselineChangedAt;
        private string _last = "—";
        private long _lastEventTick;

        // Save items stream in gradually (ShipItem delayed load). Until the local item set has been
        // stable for this long we treat everything as part of the shared baseline and create nothing:
        // otherwise a save item that loads a bit later on the host looks "new" and gets spawned onto a
        // client that is about to load the very same item itself -> duplicates.
        public const float SettleSeconds = 4f;

        // Host-authoritative identity via id-remap. SaveablePrefab.instanceId is NOT a stable
        // cross-machine identity: items created at runtime get Random.Range ids that differ per machine
        // (AssignRandomInstanceId), so the host's and client's copies of the same logical item have
        // different ids. Fix: when the host streams an unknown id, the client matches it to one of its
        // own still-loaded items by prefab + boat-local position and reassigns that item's instanceId
        // to the host's (RemapLocalItem) — single source of identity (host id), no destroy, no dup.
        // Only items the client has no match for are spawned. _hostIds = every id the host has named,
        // used both to mark items already claimed and to pick match candidates (id not in the set).
        private readonly HashSet<int> _hostIds = new HashSet<int>();
        public const float MatchRadius = 0.5f;   // metres; items at rest match near-exactly

        // Client: runtime items just authored locally (caught fish, counter-shop buys, market cargo buys),
        // awaiting host SpawnObject ids. Keep a queue: market cargo can be bought repeatedly before the
        // first host spawn returns, so a single pending slot would lose earlier local copies.
        private readonly List<ShipItem> _pendingClientItems = new List<ShipItem>();

        public float SnapshotHz = 5f;
        public float HeldPoseHz = 15f;
        public float ExtraStateHz = 2f;
        public float AltHeldHz = 15f;
        public int ItemCount => _items.Count;
        public int HeldCount => _localHeld.Count;
        public string ItemText
        {
            get
            {
                string last = "—";
                if (_lastEventTick != 0)
                {
                    long age = _net.Clock.ServerTick - _lastEventTick;
                    if (age < 0) age = 0;
                    last = _last + " " + age + "ms";
                }
                return _items.Count + " pcs, held " + _localHeld.Count + " · " + last;
            }
        }

        public ItemSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            RefreshItems(dt);
            SendLocalHeldPose(dt);
            TickRods(dt);

            if (_net.Role != Role.Host) return;
            ProcessPendingDynamic();
            SendExtraState(dt);

            // Host is the sole simulator; clients are kinematic puppets (see ApplyRemote). Stream an item
            // only while it is "active" — physically in the host's hand, or free and still moving. When it
            // settles we send one last pose and stop, so a resting item costs no bandwidth and the client
            // just holds the last boat-local pose (riding the boat). Client-held items are relayed via
            // OnItemRequest(Pose), not here.
            float interval = 1f / Mathf.Max(1f, HeldPoseHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            long tick = _net.Clock.ServerTick;
            foreach (var e in _items)
            {
                if (e.Item == null || !ShouldReplicate(e.Item)) continue;
                bool hostHeld = e.Item.held != null;
                bool freeMoving = e.HolderNetId == 0 && IsMoving(e.Item);
                if (hostHeld || freeMoving)
                {
                    _net.Broadcast(BuildState(e, tick), LiteNetLib.DeliveryMethod.Unreliable);
                    e.WasActive = true;
                }
                else if (e.WasActive && e.HolderNetId == 0)
                {
                    _net.Broadcast(BuildState(e, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);   // final resting pose
                    e.WasActive = false;
                }
            }
        }

        private void ProcessPendingDynamic()
        {
            if (_pendingDynamic.Count == 0) return;
            for (int i = _pendingDynamic.Count - 1; i >= 0; i--)
            {
                var p = _pendingDynamic[i];
                if (p == null || p.Item == null)
                {
                    _pendingDynamic.RemoveAt(i);
                    continue;
                }
                if (Time.time < p.ReleaseAt) continue;

                EnterFreeDynamic(p.Item, p.Frame, p.Vel, p.Reason);
                _pendingDynamic.RemoveAt(i);
            }
        }

        private static bool IsMoving(ShipItem item)
        {
            try
            {
                var body = item != null && item.GetItemRigidbody() != null ? item.GetItemRigidbody().GetBody() : null;
                return body != null && body.velocity.sqrMagnitude > 0.0025f;   // > 0.05 m/s
            }
            catch { return false; }
        }

        public void ApplyRemote()
        {
            if (_net.Role != Role.Client) return;
            if (!CoordSpace.Ready) return;
            if (Time.timeScale <= 0.0001f) return;

            foreach (var e in _items)
            {
                if (e.Item == null) continue;
                if (e.HolderNetId == _net.MyNetId) continue;   // I hold it → the game drives it locally
                if (!e.Net.HasData) continue;

                // Every other item (free OR held by a remote player) is a kinematic PUPPET driven purely
                // by the host's stream. We disable the game's own ItemRigidbody so it can't fight the
                // network transform (the old jitter) or drift/destroy the item; the client never
                // simulates item physics, so piles can't diverge and a placed item can't end up inside
                // another collider and vanish. The last received pose is boat-local, so a settled item
                // keeps riding the boat after the host stops streaming it.
                bool held = e.HolderNetId != 0;
                PrepareForRemotePose(e.Item, held);   // layer 2 while in a hand, else 0
                SetPuppet(e.Item, true);
                e.Net.Apply(e.Item.transform, _net.Clock.ServerTick);
                MoveProxyToItem(e.Item, kinematic: true, Vector3.zero);
            }
        }

        /// <summary>
        /// Turn the game's own physics for an item on/off. As a puppet (true) its ItemRigidbody is
        /// disabled, its body is kinematic, and collision detection is off. This is used for remote
        /// network puppets and for items carried by a client: GoPointer still moves item.transform
        /// locally, but the item cannot push the client-side kinematic boat or local props.
        /// </summary>
        private static void SetPuppet(ShipItem item, bool puppet)
        {
            try
            {
                var irb = item != null ? item.GetItemRigidbody() : null;
                bool keepWallAttachDriver = puppet && item != null && item.wallAttachment && item.held != null;
                if (irb != null && keepWallAttachDriver && !irb.enabled) irb.enabled = true;
                else if (irb != null && !keepWallAttachDriver && irb.enabled == puppet) irb.enabled = !puppet;
                if (irb != null)
                {
                    irb.ToggleCollider(!puppet);
                    foreach (var col in irb.GetComponentsInChildren<Collider>(true))
                        if (col != null) col.enabled = !puppet;
                }
                var body = irb != null ? irb.GetBody() : null;
                if (body != null)
                {
                    if (body.isKinematic != puppet)
                    {
                        body.isKinematic = puppet;
                        if (puppet) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                    }
                    // A kinematic puppet still shoves dynamic items; keep it purely visual so it can't
                    // push the client's other items out of sync with the host.
                    if (body.detectCollisions == puppet) body.detectCollisions = !puppet;
                }
                if (puppet && item != null && item.colChecker != null)
                {
                    item.colChecker.collisions = 0;
                    item.colChecker.allowObstructedDropping = true;
                    if (_fColCheckerCollidedCols == null)
                        _fColCheckerCollidedCols = typeof(PickupableItemCollisionChecker).GetField("collidedCols", BindingFlags.NonPublic | BindingFlags.Instance);
                    var list = _fColCheckerCollidedCols != null ? _fColCheckerCollidedCols.GetValue(item.colChecker) as System.Collections.IList : null;
                    list?.Clear();
                }
            }
            catch { }
        }

        public void NotifyPickup(GoPointer pointer, PickupableItem pickup)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            var item = pickup as ShipItem;
            if (item == null) return;
            RefreshItems(force: true);
            if (!_byItem.TryGetValue(item, out var e)) return;

            PrepareLocalPickupPose(pointer, item);
            SetPuppet(item, true);    // client-held items are visual only; no collision/boat push
            e.HolderNetId = _net.MyNetId;
            e.Net.Clear();
            _localHeld[item] = _net.MyNetId;
            // A player-local item (just withdrawn from our belt) has no host id yet — don't send a Pickup the
            // host can't resolve. OnLocalInventory(slot<0) re-authors it; the deferred Pickup follows the remap.
            if (!_hostIds.Contains(e.InstanceId))
            {
                Remember("local pickup (player-local, waiting for authoring) '" + item.name + "'");
                return;
            }
            SendRequest(e, ItemAction.Pickup, reliable: true);
            Remember("out pickup #" + e.Index + " '" + item.name + "'");
        }

        public void NotifyDrop(GoPointer pointer, PickupableItem pickup, Vector3 throwVelocity)
        {
            if (_net.State != LinkState.Connected) return;
            var item = pickup as ShipItem;
            if (item == null) return;
            if (_suppressNextDrop.Remove(item))
            {
                Remember("local drop suppressed '" + item.name + "'");
                return;
            }
            RefreshItems(force: true);
            if (!_byItem.TryGetValue(item, out var e)) return;

            int personalSlot = PersonalInventorySlotOf(item);
            if (personalSlot >= 0)
            {
                // Item dropped into a personal belt slot → it becomes player-local (see ClaimItemToBelt).
                ClaimItemToBelt(item, personalSlot);
                return;
            }

            _localHeld.Remove(item);
            e.InventorySlot = -1;

            if (_net.Role == Role.Client)
            {
                RestoreLocalInventoryVisual(item, inInventory: false);
                // F-place (wall/surface attach): vanilla OnDrop just teleported the PROXY to the attach
                // pose and set ItemRigidbody.attached. Read the flag before EnsureWorldParentState (its
                // ExitBoat clears it) and adopt the proxy's pose so the wire carries the attach point,
                // not the stale hand pose.
                bool placedAttach = IsProxyAttached(item);
                if (placedAttach) MoveItemToProxy(item);
                if (LocalPlayerBoat() == null)
                    EnsureWorldParentState(item);
                if (!placedAttach)
                    MoveProxyToItem(item, kinematic: true, Vector3.zero);
                var msg = BuildRequest(e, ItemAction.Drop, _net.Clock.ServerTick);
                msg.Attached = placedAttach;
                if (placedAttach)
                {
                    msg.Vel = Vector3.zero;   // BuildPose saw the hand→wall teleport as a velocity spike
                }
                else if (e.DropWithoutProxyVelocity)
                {
                    msg.Vel = Vector3.zero;
                    msg.CargoIndex = -2; // sentinel: drop immediately after inventory/cargo withdraw
                }
                else if (throwVelocity.sqrMagnitude > 0.0001f)
                {
                    // T-throw: the item was a kinematic puppet while held, so vanilla's deferred
                    // ThrowItemAfterDelay impulse never lands on the body (RealItemVelocity is ~0 here).
                    // Send the throw velocity we computed from the pointer (world axes) in the wire frame.
                    msg.Vel = WorldToFrameAxes(msg.Frame, msg.BoatIndex, throwVelocity);
                }
                e.DropWithoutProxyVelocity = false;
                e.ForceWorldPoseUntilDrop = false;
                _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
                e.HolderNetId = _net.MyNetId;  // keep ApplyRemote off until the host sends the free state
                e.Net.Clear();
                SetPuppet(item, true);    // wait for the host's authoritative free pose
                Remember("out drop #" + e.Index + " '" + item.name + "' " + PoseLabel(msg.Frame, msg.BoatIndex, msg.Pos));
            }
            else if (_net.Role == Role.Host)
            {
                // Host dropped: send exactly one reliable free-state so clients stop following the hand,
                // place the item at the release point and resume local physics with the throw impulse.
                e.HolderNetId = 0;
                // F-place: only the proxy is at the attach pose yet — adopt it so the broadcast carries
                // the wall pose instead of the hand pose (vanilla would sync the visual next FixedUpdate).
                bool placedAttach = IsProxyAttached(item);
                if (placedAttach) MoveItemToProxy(item);
                var state = BuildState(e, _net.Clock.ServerTick);
                state.HolderNetId = 0;
                if (placedAttach)
                {
                    state.Vel = Vector3.zero;
                }
                else
                {
                    Vector3 rbVel = RealItemVelocity(item);   // proxy body → walk-copy axes
                    if (rbVel.sqrMagnitude > 0.0001f)
                        state.Vel = state.Frame == CoordFrame.Boat ? ProxyToBoatAxes(item, rbVel) : rbVel;
                    else if (throwVelocity.sqrMagnitude > 0.0001f)   // T-throw impulse hasn't hit the body yet
                        state.Vel = WorldToFrameAxes(state.Frame, state.BoatIndex, throwVelocity);
                }
                _net.Broadcast(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("out drop(host) #" + e.Index + " '" + item.name + "'");
            }
        }

        public void OnItemRequest(ItemRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshItems(force: true);

            // Ready-ping (InstanceId == 0): a client finished loading and asks for the full item set so
            // it can match/remap its own copies to host ids. Reply with a SpawnObject for every item.
            if (msg.InstanceId == 0)
            {
                SendManifest();
                Remember("manifest on request");
                return;
            }

            var e = HostLookup(msg.InstanceId, msg.PrefabIndex);
            if (e == null || e.Item == null)
            {
                Remember("reject req id=" + msg.InstanceId + " prefab=" + msg.PrefabIndex);
                return;
            }

            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0) return;

            if (msg.Action == ItemAction.Consume)
            {
                // The client ate/consumed the item. The eater's PlayerNeeds is personal and already
                // applied on the client; the host only owns the shared lifecycle, so it just destroys
                // its authoritative copy. RefreshItems then diffs it gone and broadcasts DespawnObject.
                var ce = HostLookup(msg.InstanceId, msg.PrefabIndex);
                if (ce != null && ce.Item != null)
                {
                    try { ce.Item.DestroyItem(); } catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] Consume destroy: " + ex.Message); }
                    RefreshItems(force: true);   // emit the despawn now
                    Remember("in consume #" + ce.Index + " actor=" + actor);
                }
                else Remember("reject consume id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.RodHook)
            {
                // Крючок удочки поставлен/потерян на машине держащего (attach/DetachHook — симуляция
                // рыбалки бежит только там). Хост принимает результат (health = наличие крючка),
                // обновляет визуал и рассылает ItemState — покоящаяся удочка иначе не стримится.
                var rodEntry = HostLookup(msg.InstanceId, msg.PrefabIndex);
                var rrod = rodEntry != null ? rodEntry.Item as ShipItemFishingRod : null;
                if (rrod != null)
                {
                    rrod.health = msg.Health;
                    InvokeRodUpdateHook(rrod);
                    _net.Broadcast(BuildState(rodEntry, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in rod-hook #" + rodEntry.Index + " health=" + msg.Health + " actor=" + actor);
                }
                else Remember("reject rod-hook id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.Nail)
            {
                // The client (un)nailed a TARGET item (chosen by its own pointer) with a hammer. The
                // hammer's vanilla replay would aim the HOST's pointer at the wrong thing, so instead
                // we apply the only authoritative result — target.nailed — directly and broadcast it.
                var ne = HostLookup(msg.InstanceId, msg.PrefabIndex);
                if (ne != null && ne.Item != null)
                {
                    ne.Item.nailed = msg.Nailed;
                    _net.Broadcast(BuildState(ne, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in nail #" + ne.Index + "=" + msg.Nailed + " actor=" + actor);
                }
                else Remember("reject nail id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.Crate)
            {
                // Client moved a target item in/out of a crate. Mirror the membership on the host's copy
                // and broadcast the item's state (carries CrateId) so every peer converges.
                var ie = HostLookup(msg.InstanceId, msg.PrefabIndex);
                if (ie != null && ie.Item != null)
                {
                    ApplyCrateMembership(ie.Item, msg.CrateId);
                    _net.Broadcast(BuildState(ie, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in crate #" + ie.Index + "->" + msg.CrateId + " actor=" + actor);
                }
                else Remember("reject crate id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.Unseal)
            {
                // Client unsealed a crate. Item creation is host-only, so the host runs the vanilla unseal
                // (authoring the contained items with host ids + currentCrateId); RefreshItems then
                // broadcasts the new items, and we push the crate's new amount explicitly.
                var ce = HostLookup(msg.InstanceId, msg.PrefabIndex);
                var crate = ce != null ? ce.Item as ShipItemCrate : null;
                if (crate != null)
                {
                    try { crate.UnsealCrate(); }   // amount decrements synchronously; items insert next frame (coroutine)
                    catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] UnsealCrate: " + ex.Message); }
                    // Broadcast the crate's new amount now. The contained items are authored across the next
                    // frames (InsertItem coroutine sets currentCrateId); the periodic RefreshItems then
                    // broadcasts them as SpawnObject WITH their CrateId — so we deliberately don't force a
                    // refresh here, which would send them before they're marked as crate contents.
                    if (ce != null) _net.Broadcast(BuildState(ce, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in unseal crate #" + (ce != null ? ce.Index.ToString() : "?") + " actor=" + actor);
                }
                else Remember("reject unseal id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.Cargo)
            {
                // Client loaded/unloaded a port carrier with its OWN wallet (vanilla ran locally). Mirror
                // only the physical membership on the host's copy (no wallet) and broadcast the new state.
                var ge = HostLookup(msg.InstanceId, msg.PrefabIndex);
                if (ge != null && ge.Item != null)
                {
                    ApplyCargoMembership(ge.Item, msg.CargoPort);
                    if (msg.CargoPort >= 0)
                    {
                        ge.HolderNetId = 0;   // stored item is held by nobody
                    }
                    else
                    {
                        // Выгрузка. Ваниль зовёт PickUpItem ВНУТРИ WithdrawItem, поэтому Pickup приходит
                        // РАНЬШЕ этого Cargo-запроса — ждать его нельзя, он уже обработан. Cargo-запрос сам
                        // несёт позу руки (PrepareLocalWithdrawPickup), применяем её сразу: иначе предмет
                        // «проявляется» на месте старой парковки в телеге (для перевезённого груза — порт
                        // погрузки, за километры) и висит там до первого Pose.
                        ge.HolderNetId = actor;
                        _localHeld[ge.Item] = actor;
                        EnterRemoteHeldVisual(ge.Item, "host cargo out #" + ge.Index);
                        ApplyWirePose(ge.Item, msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, Vector3.zero,
                                      ge.Item.amount, ge.Item.health, ge.Item.sold, ge.Item.nailed, held: true);
                    }
                    _net.Broadcast(BuildState(ge, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in cargo #" + ge.Index + "->" + msg.CargoPort + " actor=" + actor);
                }
                else Remember("reject cargo id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.Inventory)
            {
                // Personal belt slots are per-player UI. Do not insert a guest item into the host's own
                // belt; keep the authoritative copy hidden/kinematic until the guest withdraws it.
                var ie = HostLookup(msg.InstanceId, msg.PrefabIndex);
                if (ie != null && ie.Item != null)
                {
                    ie.InventorySlot = msg.InventorySlot;
                    if (msg.InventorySlot >= 0)
                    {
                        ie.HolderNetId = actor;
                        _localHeld[ie.Item] = actor;
                        EnterRemoteInventoryHidden(ie.Item, "host inventory in #" + ie.Index);
                    }
                    else
                    {
                        // Fallback for the old inventory-out path: do not leave the hidden belt puppet
                        // parked at its slot while waiting for a later Pickup/Pose. The request already
                        // carries the client's hand pose, so reveal and move the host copy immediately.
                        ie.InventorySlot = -1;
                        ie.HolderNetId = actor;
                        _localHeld[ie.Item] = actor;
                        EnterRemoteHeldVisual(ie.Item, "host inventory out #" + ie.Index);
                        ApplyWirePose(ie.Item, msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, Vector3.zero,
                                      ie.Item.amount, ie.Item.health, ie.Item.sold, ie.Item.nailed, held: true);
                    }

                    var invState = BuildState(ie, _net.Clock.ServerTick);
                    invState.InventorySlot = ie.InventorySlot;
                    _net.Broadcast(invState, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in inventory #" + ie.Index + " slot=" + msg.InventorySlot + " actor=" + actor);
                }
                else Remember("reject inventory id=" + msg.InstanceId);
                return;
            }

            if (msg.Action == ItemAction.AltHeld || msg.Action == ItemAction.AltActivate)
            {
                // The client holds the item and triggered an alt action whose effect is
                // authoritative (hammer nail/repair, oar rowing, eat/drink). Replay the same
                // handler on the host's copy so its game logic — and the resulting state sync —
                // is the source of truth. The host doesn't physically hold the item, so we point
                // its 'held' at the host pointer for the duration so handlers gated on being held
                // still run, then restore it.
                if (msg.Action == ItemAction.AltHeld && TryApplyOarRow(e, msg, actor))
                {
                    var afterOar = BuildState(e, _net.Clock.ServerTick);
                    _net.Broadcast(afterOar, LiteNetLib.DeliveryMethod.Unreliable);
                    Remember("in oar-row #" + e.Index + " actor=" + actor);
                    return;
                }

                ReplayHeldAction(e, msg.Action, actor);
                var afterAction = BuildState(e, _net.Clock.ServerTick);
                _net.Broadcast(afterAction, LiteNetLib.DeliveryMethod.Unreliable);
                Remember("in " + msg.Action + " #" + e.Index + " actor=" + actor);
                return;
            }

            if (msg.Action == ItemAction.LampHook)
            {
                var hookEntry = HostLookup(msg.CrateId, msg.CargoIndex);
                var hook = hookEntry != null ? hookEntry.Item as ShipItemLampHook : FindLiveItem(msg.CrateId, msg.CargoIndex) as ShipItemLampHook;
                if (hook != null && e.Item != null && e.Item.GetComponent<HangableItem>() != null)
                {
                    e.InventorySlot = -1;
                    e.HolderNetId = 0;
                    _localHeld.Remove(e.Item);
                    SetProxyAttached(e.Item, false);
                    ApplyWirePose(e.Item, msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, Vector3.zero,
                                  e.Item.amount, e.Item.health, e.Item.sold, e.Item.nailed, held: false);
                    try { hook.OnItemClick(e.Item); }
                    catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] LampHook replay: " + ex.Message); }
                    SnapHangableToHook(e.Item, hook);
                    MoveProxyToItem(e.Item, kinematic: true, Vector3.zero);
                    SetProxyAttached(e.Item, true);
                    var hookState = BuildState(e, _net.Clock.ServerTick);
                    hookState.HolderNetId = 0;
                    hookState.Attached = true;
                    _net.Broadcast(hookState, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Remember("in lamp-hook #" + e.Index + " -> hook=" + msg.CrateId + " actor=" + actor);
                }
                else Remember("reject lamp-hook item=" + msg.InstanceId + " hook=" + msg.CrateId);
                return;
            }

            if (msg.Action == ItemAction.Pickup)
            {
                e.InventorySlot = -1;
                e.HolderNetId = actor;
                _localHeld[e.Item] = actor;
                SetProxyAttached(e.Item, false);   // vanilla OnPickup ran only on the client's copy
                // Выгрузка из телеги/крейта: ваниль зовёт PickUpItem ВНУТРИ WithdrawItem, поэтому этот
                // Pickup приходит РАНЬШЕ Cargo/Crate-запроса. Зеркалим членство прямо из запроса
                // (идемпотентно), иначе state уйдёт со старым CargoPort/CrateId и клиент по эху засунет
                // только что выданный в руку предмет обратно в carrier/крейт.
                ApplyCrateMembership(e.Item, msg.CrateId);
                ApplyCargoMembership(e.Item, msg.CargoPort);
            }
            else if (msg.Action == ItemAction.Drop)
            {
                e.InventorySlot = -1;
                e.HolderNetId = 0;
                _localHeld.Remove(e.Item);
            }
            else
            {
                if (msg.Action != ItemAction.State && e.HolderNetId != actor) return;   // Pose from a non-owner
                if (e.HolderNetId == actor) SetPuppet(e.Item, true);                    // keep it a clean puppet while held
            }

            bool acceptClientScalars = msg.Action == ItemAction.State;
            bool worldDrop = msg.Action == ItemAction.Drop && msg.Frame == CoordFrame.World;
            bool delayedContainerDrop = msg.Action == ItemAction.Drop && msg.CargoIndex == -2;
            bool attachedDrop = msg.Action == ItemAction.Drop && msg.Attached;
            ApplyWirePose(e.Item, msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, msg.Vel,
                          acceptClientScalars ? msg.Amount : e.Item.amount,
                          acceptClientScalars ? msg.Health : e.Item.health,
                          e.Item.sold, e.Item.nailed, e.HolderNetId != 0 || worldDrop);
            if (msg.Action == ItemAction.Pickup)
                EnterRemoteHeldVisual(e.Item, "host pickup #" + e.Index);
            else if (msg.Action == ItemAction.Drop)
            {
                if (attachedDrop)
                    EnterAttachedStatic(e.Item, msg.Frame, "host place #" + e.Index);
                else if (delayedContainerDrop)
                    ScheduleFreeDynamic(e.Item, msg.Frame, msg.Vel, "host delayed drop #" + e.Index);
                else
                    EnterFreeDynamic(e.Item, msg.Frame, msg.Vel, "host drop #" + e.Index);
            }
            var state = BuildState(e, _net.Clock.ServerTick);
            // On drop, BuildState's velocity is derived from the position history and spikes because the
            // transform just teleported to the drop point — use the client's real rigidbody velocity
            // instead, or the dropped item would be flung off the ship on every receiver.
            if (msg.Action == ItemAction.Drop) state.Vel = msg.Vel;
            _net.Broadcast(state, msg.Action == ItemAction.Pose ? LiteNetLib.DeliveryMethod.Unreliable : LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("in " + msg.Action + " #" + e.Index + " actor=" + actor + " " + PoseLabel(msg.Frame, msg.BoatIndex, msg.Pos));
        }

        public void OnItemState(ItemStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            RefreshItems(force: true);
            var e = ResolveClient(msg.InstanceId, msg.PrefabIndex, msg.Frame, msg.BoatIndex, msg.Pos,
                                  msg.Amount, msg.Health, msg.Sold, msg.Nailed, allowSpawn: msg.HolderNetId == 0);
            if (e == null || e.Item == null)
            {
                Remember("missing item id=" + msg.InstanceId + " prefab=" + msg.PrefabIndex);
                return;
            }

            uint prevHolder = e.HolderNetId;
            e.HolderNetId = msg.HolderNetId;
            e.InventorySlot = msg.InventorySlot;
            ApplyScalarState(e.Item, msg.Amount, msg.Health, msg.Sold, msg.Nailed);
            // Членство crate/cargo НЕ зеркалим из состояния предмета, который держим МЫ: ваниль уже
            // выполнила операцию локально, а эхо может нести устаревший CargoPort/CrateId (Pickup при
            // выгрузке обгоняет Cargo/Crate-запрос) — по нему свежевыданный предмет засовывался
            // обратно в carrier (scale 0, слот) прямо из руки.
            if (msg.HolderNetId != _net.MyNetId)
            {
                ApplyCrateMembership(e.Item, msg.CrateId);
                ApplyCargoMembership(e.Item, msg.CargoPort);
            }
            // Mirror the vanilla attach flag (F-place) so the copy behaves like the host's after a
            // reconnect/handover; puppets are kinematic anyway, so this is purely state fidelity.
            if (msg.HolderNetId != _net.MyNetId)
                SetProxyAttached(e.Item, msg.Attached);

            if (msg.HolderNetId == _net.MyNetId)
            {
                SetRemoteInventoryVisual(e.Item, hidden: false);
                Remember("echo #" + e.Index);   // my own held item echoed back; the game drives it
                return;
            }

            SetRemoteInventoryVisual(e.Item, hidden: msg.InventorySlot >= 0);

            // Free OR remote-held: feed the pose into NetTransform; ApplyRemote drives the item as a
            // kinematic puppet (game physics disabled), so it can't diverge / be ejected / vanish.
            ConfigureNetFrame(e, msg.Frame, msg.BoatIndex);
            if (prevHolder == _net.MyNetId && msg.HolderNetId == 0)
                e.Net.Clear();
            e.Net.Push(msg.Tick, msg.Pos, msg.Rot, msg.Vel);
            Remember("in " + (msg.HolderNetId != 0 ? "held " + msg.HolderNetId : "free") + " #" + e.Index);
        }

        public void ClearRemoteActor(uint actorNetId)
        {
            if (actorNetId == 0) return;

            int released = 0;
            foreach (var e in _items)
            {
                if (e == null || e.Item == null || e.HolderNetId != actorNetId) continue;

                e.HolderNetId = 0;
                e.InventorySlot = -1;
                _localHeld.Remove(e.Item);
                RestoreDisconnectedItem(e.Item);

                if (_net.Role == Role.Host && _net.State == LinkState.Connected)
                    _net.Broadcast(BuildState(e, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                released++;
            }

            if (released > 0)
            {
                Remember("released actor " + actorNetId + " items=" + released);
                Plugin.Logger.LogInfo("[ItemSync] Released " + released + " item(s) held by player " + actorNetId);
            }
        }

        private void SendLocalHeldPose(float dt)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            float interval = 1f / Mathf.Max(1f, HeldPoseHz);
            _heldPoseTimer += dt;
            if (_heldPoseTimer < interval) return;
            _heldPoseTimer = 0f;

            var held = HeldItem() as ShipItem;
            if (held != null) StreamLocalPose(held);

            // Items the local player tucked into a personal belt slot (GPButtonInventorySlot) are no longer
            // pointer-held — vanilla parents them to a slot transform on the body. Without continuing to
            // stream their pose the host puppet would freeze at the pickup spot ("hangs in the air"). Keep
            // them riding the client's avatar by streaming the belt-local pose every tick; the host already
            // thinks the actor holds them (set at Pickup), so the Pose updates keep applying.
            if (_localHeld.Count > 0)
            {
                _poseScratch.Clear();
                foreach (var kv in _localHeld) _poseScratch.Add(kv.Key);
                foreach (var it in _poseScratch)
                {
                    if (it == null || it == held) continue;
                    if (InPersonalInventory(it)) StreamLocalPose(it);
                }
            }
        }

        /// <summary>Send one unreliable Pose for a locally-carried item (hand or belt).</summary>
        private void StreamLocalPose(ShipItem item)
        {
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            _localHeld[item] = _net.MyNetId;
            if (!InPersonalInventory(item))
                SetPuppet(item, true);    // hand-held items stay visual/kinematic; belt slots need vanilla ItemRigidbody
            else
                RestoreLocalInventoryVisual(item, inInventory: true);
            SendRequest(e, ItemAction.Pose, reliable: false);
        }

        // A personal belt slot returns slotIndex 0..4 from GetCurrentInventorySlot(); cargo carriers return
        // port+100 (see CargoPortOf) and nothing returns -1. So [0,100) means "in the local player's belt".
        private static bool InPersonalInventory(ShipItem item)
        {
            return PersonalInventorySlotOf(item) >= 0;
        }

        private static int PersonalInventorySlotOf(ShipItem item)
        {
            if (item == null) return -1;
            try
            {
                int slot = item.GetCurrentInventorySlot();
                return slot >= 0 && slot < 100 ? slot : -1;
            }
            catch { return -1; }
        }

        /// <summary>Что мы выключили/поменяли, пряча предмет в чужом инвентаре, — чтобы при
        /// разскрытии вернуть ровно это, а не «включить все рендереры и scale=1» (белые кубы
        /// у предметов со служебными выключенными мешами, например мангала).</summary>
        private sealed class HiddenVisualState
        {
            public readonly List<Renderer> Disabled = new List<Renderer>();
            public Vector3 RootScale = Vector3.one;
            public readonly List<Vector3> ChildScales = new List<Vector3>();
        }

        private static readonly Dictionary<ShipItem, HiddenVisualState> _hiddenVisuals =
            new Dictionary<ShipItem, HiddenVisualState>();

        private static void SetRemoteInventoryVisual(ShipItem item, bool hidden)
        {
            try
            {
                if (item == null) return;
                if (hidden)
                {
                    // Запоминаем, что именно выключили/каким был масштаб: у сложных предметов
                    // (мангал/печь) есть дочерние рендереры, которые ваниль ДЕРЖИТ выключенными,
                    // и дети с неединичным масштабом. Слепое "включить всё и scale=1" при
                    // разскрытии превращало такой предмет в белый куб.
                    var st = new HiddenVisualState();
                    foreach (var r in item.GetComponentsInChildren<Renderer>(true))
                        if (r != null && r.enabled)
                        {
                            r.enabled = false;
                            st.Disabled.Add(r);
                        }
                    st.RootScale = item.transform.localScale;
                    var tr = item.transform;
                    for (int i = 0; i < tr.childCount; i++)
                        st.ChildScales.Add(tr.GetChild(i).localScale);
                    _hiddenVisuals[item] = st;
                }
                else if (_hiddenVisuals.TryGetValue(item, out var st))
                {
                    // Восстанавливаем ровно то, что скрывали сами; без записи — не трогаем визуал
                    // (предмет и так виден, «включать всё» ему только вредит).
                    foreach (var r in st.Disabled)
                        if (r != null) r.enabled = true;
                    item.transform.localScale = st.RootScale;
                    var tr = item.transform;
                    for (int i = 0; i < tr.childCount && i < st.ChildScales.Count; i++)
                        tr.GetChild(i).localScale = st.ChildScales[i];
                    _hiddenVisuals.Remove(item);
                }
                var irb = item.GetItemRigidbody();
                if (irb != null)
                {
                    irb.ToggleCollider(!hidden);
                    irb.disableCol = hidden;
                }
                var col = item.GetComponent<Collider>();
                if (col != null) col.enabled = !hidden;
            }
            catch { }
        }

        private static void RestoreLocalInventoryVisual(ShipItem item, bool inInventory)
        {
            try
            {
                if (item == null) return;
                var irb = item.GetItemRigidbody();
                if (!inInventory && irb != null && irb.GetCurrentInventorySlot() != null)
                    irb.ExitInventorySlot();

                // Как и в SetRemoteInventoryVisual: включаем только те рендереры, что выключали
                // сами, и возвращаем сохранённые масштабы — иначе служебные меши сложных
                // предметов (мангал) становятся видимыми белыми кубами.
                if (_hiddenVisuals.TryGetValue(item, out var st))
                {
                    foreach (var r in st.Disabled)
                        if (r != null) r.enabled = true;
                    if (!inInventory)
                    {
                        item.transform.localScale = st.RootScale;
                        var t = item.transform;
                        for (int i = 0; i < t.childCount && i < st.ChildScales.Count; i++)
                            t.GetChild(i).localScale = st.ChildScales[i];
                    }
                    _hiddenVisuals.Remove(item);
                }
                var col = item.GetComponent<Collider>();
                if (col != null) col.enabled = !inInventory;
                if (irb != null)
                {
                    if (!irb.enabled) irb.enabled = true;
                    irb.disableCol = false;
                    irb.ToggleCollider(!inInventory);
                }
            }
            catch { }
        }

        private static void EnterRemoteInventoryHidden(ShipItem item, string reason)
        {
            LogItemTransition("before hidden " + reason, item);
            EnsureWorldParentState(item); // vanilla OnEnterInventory exits boats; mirror that on remote copies
            SetRemoteInventoryVisual(item, hidden: true);
            SetPuppet(item, true);
            LogItemTransition("after hidden " + reason, item);
        }

        private static void LeaveRemoteInventoryHidden(ShipItem item, string reason)
        {
            LogItemTransition("before unhidden " + reason, item);
            SetRemoteInventoryVisual(item, hidden: false);
            LogItemTransition("after unhidden " + reason, item);
        }

        private static void EnterRemoteHeldVisual(ShipItem item, string reason)
        {
            LogItemTransition("before held " + reason, item);
            SetRemoteInventoryVisual(item, hidden: false);
            SetRootCollider(item, false);
            SetPuppet(item, true);
            LogItemTransition("after held " + reason, item);
        }

        private static void EnterFreeDynamic(ShipItem item, CoordFrame frame, Vector3 vel, string reason)
        {
            LogItemTransition("before free " + reason, item);
            if (frame == CoordFrame.World)
                EnsureWorldParentState(item);
            SetRemoteInventoryVisual(item, hidden: false);
            RestoreInteractableLayer(item);
            SetRootCollider(item, true);
            SetPuppet(item, false);
            // Boat-frame wire velocity is in boat-local axes; the proxy body simulates in the boat's
            // static walk copy, so map the direction into that frame's axes.
            if (frame == CoordFrame.Boat) vel = BoatToProxyAxes(item, vel);
            MoveProxyToItem(item, kinematic: false, vel);
            LogItemTransition("after free " + reason, item);
        }

        /// <summary>Boat-local axes -> the axes the item's physics proxy simulates in (the boat's static
        /// walk copy; falls back to the boat itself if the item has no walk collider).</summary>
        private static Vector3 BoatToProxyAxes(ShipItem item, Vector3 v)
        {
            try
            {
                var walk = item != null ? item.currentWalkCol : null;
                if (walk != null) return walk.TransformDirection(v);
                var boat = item != null ? item.currentActualBoat : null;
                return boat != null ? boat.TransformDirection(v) : v;
            }
            catch { return v; }
        }

        /// <summary>Inverse of <see cref="BoatToProxyAxes"/>: the proxy body's velocity -> boat-local axes.</summary>
        private static Vector3 ProxyToBoatAxes(ShipItem item, Vector3 v)
        {
            try
            {
                var walk = item != null ? item.currentWalkCol : null;
                if (walk != null) return walk.InverseTransformDirection(v);
                var boat = item != null ? item.currentActualBoat : null;
                return boat != null ? boat.InverseTransformDirection(v) : v;
            }
            catch { return v; }
        }

        /// <summary>World axes -> the wire frame's axes (no-op for the World frame).</summary>
        private static Vector3 WorldToFrameAxes(CoordFrame frame, ushort boatIndex, Vector3 v)
        {
            if (frame != CoordFrame.Boat) return v;
            var boat = BoatLocator.FindByIndex(boatIndex);
            return boat != null ? boat.InverseTransformDirection(v) : v;
        }

        /// <summary>True if the item's physics proxy is vanilla-attached to a wall/surface
        /// (<c>ItemRigidbody.attached</c> — the F-"положить" mechanic of wallAttachment items).</summary>
        private static bool IsProxyAttached(ShipItem item)
        {
            try
            {
                var irb = item != null ? item.GetItemRigidbody() : null;
                return irb != null && irb.attached;
            }
            catch { return false; }
        }

        private static void SetProxyAttached(ShipItem item, bool value)
        {
            try
            {
                var irb = item != null ? item.GetItemRigidbody() : null;
                if (irb != null) irb.attached = value;
            }
            catch { }
        }

        /// <summary>Snap the visual item to its physics proxy — what vanilla's next
        /// <c>ItemRigidbody.FixedUpdate</c> (MoveItemToWalkColRigidbody) would do. Needed on F-place:
        /// vanilla <c>ShipItem.OnDrop</c> teleports only the PROXY to the wall-attach pose, and our
        /// drop hook runs before the frame that would move the visual — so adopt that pose here
        /// before <see cref="MoveProxyToItem"/> overwrites the proxy from the stale hand pose.</summary>
        private static void MoveItemToProxy(ShipItem item)
        {
            try
            {
                var proxy = item != null ? item.GetItemRigidbody() : null;
                if (proxy == null) return;
                if (item.currentActualBoat != null && item.currentWalkCol != null)
                {
                    Vector3 walkLocalPos = item.currentWalkCol.InverseTransformPoint(proxy.transform.position);
                    Quaternion walkLocalRot = Quaternion.Inverse(item.currentWalkCol.rotation) * proxy.transform.rotation;
                    item.transform.position = item.currentActualBoat.TransformPoint(walkLocalPos);
                    item.transform.rotation = item.currentActualBoat.rotation * walkLocalRot;
                }
                else
                {
                    item.transform.position = proxy.transform.position;
                    item.transform.rotation = proxy.transform.rotation;
                }
            }
            catch { }
        }

        /// <summary>Host: a client F-"placed" (attached) an item. Freeze the proxy at the item's wire
        /// pose with <c>ItemRigidbody.attached</c>, so vanilla keeps it kinematic and it neither falls
        /// nor slides; collisions stay on so other items can rest against it like in vanilla.</summary>
        private static void EnterAttachedStatic(ShipItem item, CoordFrame frame, string reason)
        {
            LogItemTransition("before attach " + reason, item);
            if (item != null)
            {
                if (frame == CoordFrame.World)
                    EnsureWorldParentState(item);
                SetRemoteInventoryVisual(item, hidden: false);
                RestoreInteractableLayer(item);
                SetRootCollider(item, true);
                SetPuppet(item, false);
                MoveProxyToItem(item, kinematic: true, Vector3.zero);
                SetProxyAttached(item, true);
                try
                {
                    var irb = item.GetItemRigidbody();
                    var body = irb != null ? irb.GetBody() : null;
                    if (body != null) body.detectCollisions = true;   // MoveProxyToItem(kinematic) turned it off
                }
                catch { }
            }
            LogItemTransition("after attach " + reason, item);
        }

        private static void SnapHangableToHook(ShipItem item, ShipItemLampHook hook)
        {
            try
            {
                if (item == null || hook == null) return;
                item.transform.position = hook.transform.position + hook.transform.forward * -0.128f;
                var rot = item.transform.eulerAngles;
                rot.x = 0f;
                rot.z = 0f;
                item.transform.eulerAngles = rot;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] SnapHangableToHook: " + ex.Message);
            }
        }

        private void ScheduleFreeDynamic(ShipItem item, CoordFrame frame, Vector3 vel, string reason)
        {
            LogItemTransition("before pending-free " + reason, item);
            if (item != null)
            {
                if (frame == CoordFrame.World)
                    EnsureWorldParentState(item);
                SetRemoteInventoryVisual(item, hidden: false);
                SetRootCollider(item, false);
                SetPuppet(item, true);
                MoveProxyToItem(item, kinematic: true, Vector3.zero);
                _pendingDynamic.Add(new PendingDynamicRelease
                {
                    Item = item,
                    Frame = frame,
                    Vel = vel,
                    ReleaseAt = Time.time + 0.15f,
                    Reason = reason,
                });
            }
            LogItemTransition("after pending-free " + reason, item);
        }

        private static void SetRootCollider(ShipItem item, bool enabled)
        {
            try
            {
                var col = item != null ? item.GetComponent<Collider>() : null;
                if (col != null) col.enabled = enabled;
            }
            catch { }
        }

        private static void RestoreInteractableLayer(ShipItem item)
        {
            try
            {
                // Мы меняем слой ТОЛЬКО у корня (layer 2 в руке, см. PrepareForRemotePose) —
                // и откатывать надо только его. Рекурсивный сброс всех детей в 0 выводил на
                // экран служебные меши, которые ваниль прячет нерендеримым слоем (мангал →
                // белый куб после взаимодействия клиента).
                if (item == null) return;
                if (item.gameObject.layer == 2) item.gameObject.layer = 0;
            }
            catch { }
        }

        private static void RestoreDisconnectedItem(ShipItem item)
        {
            try
            {
                if (item == null) return;
                item.held = null;
                SetRemoteInventoryVisual(item, hidden: false);
                RestoreLocalInventoryVisual(item, inInventory: false);
                RestoreInteractableLayer(item);
                SetRootCollider(item, true);
                SetPuppet(item, false);
                MoveProxyToItem(item, kinematic: false, Vector3.zero);
            }
            catch { }
        }

        private static void LogItemTransition(string label, ShipItem item)
        {
            try
            {
                if (item == null)
                {
                    Plugin.Logger.LogInfo("[ItemSync] " + label + ": item=null");
                    return;
                }

                var irb = item.GetItemRigidbody();
                var body = irb != null ? irb.GetBody() : null;
                string inv = "-";
                try { inv = irb != null && irb.GetCurrentInventorySlot() != null ? irb.GetCurrentInventorySlot().name : "-"; } catch { }
                string onBoat = "?";
                try
                {
                    if (irb != null)
                    {
                        if (_fItemRigidbodyOnBoat == null)
                            _fItemRigidbodyOnBoat = typeof(ItemRigidbody).GetField("onBoat", BindingFlags.NonPublic | BindingFlags.Instance);
                        onBoat = _fItemRigidbodyOnBoat != null ? ((bool)_fItemRigidbodyOnBoat.GetValue(irb)).ToString() : "?";
                    }
                }
                catch { }

                Plugin.Logger.LogInfo("[ItemSync] " + label +
                    " '" + item.name + "'" +
                    " pos=" + item.transform.position.ToString("F2") +
                    " parent=" + (item.transform.parent != null ? item.transform.parent.name : "-") +
                    " boat=" + (item.currentActualBoat != null ? item.currentActualBoat.name : "-") +
                    " walk=" + (item.currentWalkCol != null ? item.currentWalkCol.name : "-") +
                    " slot=" + inv +
                    " irbEnabled=" + (irb != null ? irb.enabled.ToString() : "-") +
                    " rbKin=" + (body != null ? body.isKinematic.ToString() : "-") +
                    " rbDetect=" + (body != null ? body.detectCollisions.ToString() : "-") +
                    " onBoat=" + onBoat +
                    " scale=" + item.transform.localScale.ToString("F2"));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] LogItemTransition: " + ex.Message);
            }
        }

        private static void PrepareLocalWithdrawPickup(ShipItem item)
        {
            try
            {
                if (item == null || item.held == null) return;
                RestoreLocalInventoryVisual(item, inInventory: false);

                Transform pointer = item.held.transform;
                if (pointer != null)
                {
                    Vector3 pos = pointer.position + pointer.forward * Mathf.Max(0.7f, item.holdDistance) + pointer.up * item.holdHeight;
                    Quaternion rot = pointer.rotation * Quaternion.Euler(item.heldRotationOffset, 0f, 0f);
                    item.transform.position = pos;
                    item.transform.rotation = rot;
                }

                if (LocalPlayerBoat() == null)
                    EnsureWorldParentState(item);
                MoveProxyToItem(item, kinematic: true, Vector3.zero);
                ResetPointerBigItemCapture(item);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] PrepareLocalWithdrawPickup: " + ex.Message);
            }
        }

        private static FieldInfo _fPointerBigItemLocalPos;
        private static FieldInfo _fPointerDecolLocalPos;
        private static FieldInfo _fPointerBigItemLocalRot;

        /// <summary>
        /// Big-предметы ваниль держит НЕ у руки, а на смещении, захваченном в момент PickUpItem
        /// (GoPointer.bigItemLocalPos = pointer.InverseTransformPoint(item.pos) — «неси там, где взял»).
        /// При выгрузке из карго-телеги WithdrawItem сперва телепортирует предмет на +10 м от телеги, а
        /// «парковка» предмета вообще в точке вставки — захват происходит далеко впереди, и крейт так и
        /// едет в 10+ м перед игроком (наш телепорт к руке ваниль перетирает следующим же LateUpdate).
        /// Пере-захватываем смещение на нормальную дистанцию удержания.
        /// </summary>
        private static void ResetPointerBigItemCapture(ShipItem item)
        {
            try
            {
                if (item == null || item.held == null || !item.big) return;
                Transform p = item.held.transform;
                if (p == null) return;
                float dist = Mathf.Max(1.6f, item.holdDistance);   // ближе 0.6 ваниль сама сбрасывает decol («Close decol limit»)
                Vector3 holdPos = p.position + p.forward * dist + p.up * item.holdHeight;
                Quaternion holdRot = p.rotation * Quaternion.Euler(item.heldRotationOffset, 0f, 0f);
                item.transform.position = holdPos;
                item.transform.rotation = holdRot;
                if (_fPointerBigItemLocalPos == null)
                {
                    _fPointerBigItemLocalPos = typeof(GoPointer).GetField("bigItemLocalPos", BindingFlags.NonPublic | BindingFlags.Instance);
                    _fPointerDecolLocalPos = typeof(GoPointer).GetField("decolLocalPos", BindingFlags.NonPublic | BindingFlags.Instance);
                    _fPointerBigItemLocalRot = typeof(GoPointer).GetField("bigItemLocalRot", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                Vector3 localPos = p.InverseTransformPoint(holdPos);
                if (_fPointerBigItemLocalPos != null) _fPointerBigItemLocalPos.SetValue(item.held, localPos);
                if (_fPointerDecolLocalPos != null) _fPointerDecolLocalPos.SetValue(item.held, localPos);
                if (_fPointerBigItemLocalRot != null) _fPointerBigItemLocalRot.SetValue(item.held, Quaternion.Inverse(p.rotation) * holdRot);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] ResetPointerBigItemCapture: " + ex.Message);
            }
        }

        private static void PrepareLocalPickupPose(GoPointer pointer, ShipItem item)
        {
            try
            {
                if (pointer == null || item == null) return;
                RestoreLocalInventoryVisual(item, inInventory: false);

                Transform p = pointer.transform;
                if (p != null)
                {
                    Vector3 pos = p.position + p.forward * Mathf.Max(0.7f, item.holdDistance) + p.up * item.holdHeight;
                    Quaternion rot = p.rotation * Quaternion.Euler(item.heldRotationOffset, 0f, 0f);
                    item.transform.position = pos;
                    item.transform.rotation = rot;
                }

                if (LocalPlayerBoat() == null)
                    EnsureWorldParentState(item);
                MoveProxyToItem(item, kinematic: true, Vector3.zero);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] PrepareLocalPickupPose: " + ex.Message);
            }
        }

        private void SendRequest(ItemEntry e, ItemAction action, bool reliable)
        {
            if (e == null || e.Item == null) return;
            var msg = BuildRequest(e, action, _net.Clock.ServerTick);
            _net.Broadcast(msg, reliable ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
        }

        private ItemRequestMsg BuildRequest(ItemEntry e, ItemAction action, long tick)
        {
            BuildPose(e.Item, tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel);
            if (action == ItemAction.AltHeld && e.Item is ShipItemOar oar && oar.waterPos != null)
            {
                BuildTransformPose(oar.waterPos, tick, out frame, out boatIndex, out pos, out rot);
                vel = Vector3.zero;
            }
            // On drop/throw the meaningful velocity is the impulse Sailwind just applied to the
            // item's physics proxy, not the smoothed hand motion — read it straight off the body
            // so the host launches the throw authoritatively. The proxy simulates in the boat's
            // walk copy, so re-express it in the wire frame's axes.
            if (action == ItemAction.Drop)
            {
                Vector3 rbVel = RealItemVelocity(e.Item);
                if (rbVel.sqrMagnitude > 0.0001f)
                    vel = frame == CoordFrame.Boat ? ProxyToBoatAxes(e.Item, rbVel) : rbVel;
            }
            return new ItemRequestMsg
            {
                Action = action,
                Index = e.Index,
                InstanceId = e.InstanceId,
                PrefabIndex = e.PrefabIndex,
                Tick = tick,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                Amount = e.Item.amount,
                Health = e.Item.health,
                Sold = e.Item.sold,
                Nailed = e.Item.nailed,
                CrateId = CrateIdOf(e.Item),
                CargoPort = CargoPortOf(e.Item),
                InventorySlot = PersonalInventorySlotOf(e.Item),
                Attached = IsProxyAttached(e.Item),
            };
        }

        private static void BuildTransformPose(Transform source, long tick, out CoordFrame frame, out ushort boatIndex,
                                               out Vector3 pos, out Quaternion rot)
        {
            Transform boat = ParentBoat(source);
            if (boat == null)
                boat = LocalPlayerBoat();
            if (boat != null)
            {
                frame = CoordFrame.Boat;
                boatIndex = BoatLocator.IndexOf(boat);
                pos = boat.InverseTransformPoint(source.position);
                rot = Quaternion.Inverse(boat.rotation) * source.rotation;
            }
            else
            {
                frame = CoordFrame.World;
                boatIndex = BoatLocator.NoBoat;
                pos = CoordSpace.Ready ? CoordSpace.LocalToReal(source.position) : source.position;
                rot = source.rotation;
            }
        }

        private ItemStateMsg BuildState(ItemEntry e, long tick)
        {
            BuildPose(e.Item, tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel);
            uint holder = e.HolderNetId != 0 ? e.HolderNetId : (e.Item.held != null ? _net.MyNetId : 0);
            int inventorySlot = PersonalInventorySlotOf(e.Item);
            if (inventorySlot < 0) inventorySlot = e.InventorySlot;
            return new ItemStateMsg
            {
                Index = e.Index,
                InstanceId = e.InstanceId,
                PrefabIndex = e.PrefabIndex,
                Tick = tick,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                HolderNetId = holder,
                Amount = e.Item.amount,
                Health = e.Item.health,
                Sold = e.Item.sold,
                Nailed = e.Item.nailed,
                CrateId = CrateIdOf(e.Item),
                CargoPort = CargoPortOf(e.Item),
                InventorySlot = inventorySlot,
                Attached = IsProxyAttached(e.Item),
            };
        }

        private void BuildPose(ShipItem item, long tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel)
        {
            _byItem.TryGetValue(item, out var e);
            bool forceWorld = e != null && e.ForceWorldPoseUntilDrop;
            Transform boat = forceWorld
                ? null
                : (item.held != null
                    ? LocalPlayerBoat()
                    : (item.currentActualBoat != null ? item.currentActualBoat : ParentBoat(item.transform)));
            if (boat != null)
            {
                frame = CoordFrame.Boat;
                boatIndex = BoatLocator.IndexOf(boat);
                pos = boat.InverseTransformPoint(item.transform.position);
                rot = Quaternion.Inverse(boat.rotation) * item.transform.rotation;
            }
            else
            {
                frame = CoordFrame.World;
                boatIndex = BoatLocator.NoBoat;
                pos = CoordSpace.Ready ? CoordSpace.LocalToReal(item.transform.position) : item.transform.position;
                rot = item.transform.rotation;
            }

            // Velocity must be in the SAME frame as the wire pos (boat-local axes for Boat frame, real
            // space for World): receivers extrapolate pos + vel*dt in that frame, and on drop the host
            // feeds it into the item's physics proxy, which simulates inside the boat's STATIC walk copy.
            // Real-space history here used to leak the boat's world speed into items dropped while sailing
            // (the item slid forward across the deck at the ship's speed).
            vel = Vector3.zero;
            if (e != null)
            {
                if (e.HaveLast && e.LastFrame == frame && e.LastBoatIndex == boatIndex)
                {
                    float secs = (tick - e.LastTick) / 1000f;
                    if (secs > 0.0001f) vel = (pos - e.LastPos) / secs;
                }
                e.LastPos = pos;
                e.LastTick = tick;
                e.LastFrame = frame;
                e.LastBoatIndex = boatIndex;
                e.HaveLast = true;
            }
        }

        private static Transform LocalPlayerBoat()
        {
            try
            {
                var emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
                return emb != null ? emb.debugOutCurrentBoat : null;
            }
            catch { return null; }
        }

        private void ApplyWirePose(ShipItem item, CoordFrame frame, ushort boatIndex, Vector3 pos, Quaternion rot, Vector3 vel, float amount, float health, bool sold, bool nailed, bool held)
        {
            if (item == null) return;
            Vector3 worldPos;
            Quaternion worldRot;
            if (frame == CoordFrame.Boat)
            {
                Transform boat = BoatLocator.FindByIndex(boatIndex);
                if (boat == null) return;
                worldPos = boat.TransformPoint(pos);
                worldRot = boat.rotation * rot;
            }
            else
            {
                worldPos = CoordSpace.Ready ? CoordSpace.RealToLocal(pos) : pos;
                worldRot = rot;
            }

            item.transform.position = worldPos;
            item.transform.rotation = worldRot;
            item.transform.localScale = Vector3.one;
            if (frame == CoordFrame.Boat)
                EnsureBoatParentState(item, boatIndex);
            else
                EnsureWorldParentState(item);
            ApplyScalarState(item, amount, health, sold, nailed);
            // Host simulates authoritatively (never a client here): drop → dynamic, held → kinematic puppet.
            MoveProxyToItem(item, kinematic: held, vel);
        }

        private void ApplyScalarState(ShipItem item, float amount, float health, bool sold, bool nailed)
        {
            if (item == null) return;
            item.amount = amount;
            item.health = health;
            item.sold = sold;
            item.nailed = nailed;
            // У удочки health = наличие крючка; ваниль обновляет hookVisuals только из своих методов
            // (OnItemClick/DetachHook/OnLoad), поэтому после сетевого health зовём UpdateHook сами —
            // иначе удочка «выглядит без крючка», хотя health уже 1 (и наоборот).
            if (item is ShipItemFishingRod fr) InvokeRodUpdateHook(fr);
        }

        private static readonly MethodInfo RodUpdateHookMethod = typeof(ShipItemFishingRod).GetMethod(
            "UpdateHook", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void InvokeRodUpdateHook(ShipItemFishingRod rod)
        {
            try { RodUpdateHookMethod?.Invoke(rod, null); }
            catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] Fishing rod UpdateHook: " + ex.Message); }
        }

        // Anti-echo: set while we apply a remote crate change, so the Insert/Withdraw postfix patches
        // don't re-forward it (R13). Static because the patches are static.
        internal static bool ApplyingCrate;

        /// <summary>
        /// Mirror a crate membership change onto a local item: withdraw it from its old crate and/or
        /// insert it into the new one. The crate is resolved by its instanceId via the item registry
        /// (the vanilla static <c>ShipItemCrate.crates</c> dict is never populated). Insert/Withdraw is
        /// done under the anti-echo guard so the relay patches don't bounce it back.
        /// </summary>
        private void ApplyCrateMembership(ShipItem item, int crateId)
        {
            if (item == null) return;
            var sv = item.GetComponent<SaveablePrefab>();
            if (sv == null || sv.currentCrateId == crateId) return;

            ApplyingCrate = true;
            try
            {
                int oldId = sv.currentCrateId;
                if (oldId != 0 && _byInstanceId.TryGetValue(oldId, out var oldC) && oldC.Item != null)
                {
                    var inv = oldC.Item.GetComponent<CrateInventory>();
                    if (inv != null) inv.WithdrawItem(item);
                }
                if (crateId != 0 && _byInstanceId.TryGetValue(crateId, out var newC) && newC.Item != null)
                {
                    var inv = newC.Item.GetComponent<CrateInventory>();
                    if (inv != null) inv.InsertItem(item);
                }
                sv.currentCrateId = crateId;   // ensure exact match even if Insert/Withdraw were no-ops
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] ApplyCrateMembership: " + ex.Message); }
            finally { ApplyingCrate = false; }
        }

        // Anti-echo guard for cargo membership (the Insert/Withdraw patches check it).
        internal static bool ApplyingCargo;

        /// <summary>
        /// Mirror a cargo-storage membership change: pull the item out of its old carrier and/or push it
        /// into the new one — VISUAL/state only (LoadSavedItem adds without charging the wallet; each
        /// player's own wallet already ran the vanilla load/unload locally). Carriers are addressed by
        /// stable portIndex via the static <c>CargoCarrier.carriers</c> array.
        /// </summary>
        private void ApplyCargoMembership(ShipItem item, int port)
        {
            if (item == null) return;
            int cur = CargoPortOf(item);
            if (cur == port) return;

            ApplyingCargo = true;
            try
            {
                var carriers = CargoCarrier.carriers;
                if (cur >= 0 && carriers != null && cur < carriers.Length && carriers[cur] != null)
                {
                    var c = carriers[cur];
                    c.cargo.Remove(item);
                    item.WithdrawFromCarrier();
                    RestoreLocalInventoryVisual(item, inInventory: false);
                }
                if (port >= 0 && carriers != null && port < carriers.Length && carriers[port] != null)
                {
                    carriers[port].LoadSavedItem(item);   // EnterInventorySlot + InsertIntoCargoCarrier + scale 0, no wallet
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] ApplyCargoMembership: " + ex.Message); }
            finally { ApplyingCargo = false; }
        }

        private void ConfigureNetFrame(ItemEntry e, CoordFrame frame, ushort boatIndex)
        {
            if (frame == CoordFrame.Boat)
            {
                e.Net.ToWorldPos = p =>
                {
                    Transform boat = BoatLocator.FindByIndex(boatIndex);
                    return boat != null ? boat.TransformPoint(p) : p;
                };
                e.Net.ToWorldRot = q =>
                {
                    Transform boat = BoatLocator.FindByIndex(boatIndex);
                    return boat != null ? boat.rotation * q : q;
                };
            }
            else
            {
                e.Net.ToWorldPos = CoordSpace.RealToLocal;
                e.Net.ToWorldRot = q => q;
            }
        }

        private void PrepareForRemotePose(ShipItem item, bool held)
        {
            if (item == null) return;
            if (held)
            {
                item.held = null;
                if (item.gameObject.layer != 2) item.gameObject.layer = 2;   // IgnoreRaycast while in a hand
            }
            else if (item.gameObject.layer == 2)
            {
                RestoreInteractableLayer(item);   // restore so the pointer can hit it again after inventory/held states
            }
        }

        private const float MaxDropSpeed = 15f;        // clamp so a bad/teleport-derived velocity can't launch an item off the ship
        private const float SettleDepenetration = 1f;  // cap overlap-resolution speed so dropping into a pile can't fling the item away

        private static void MoveProxyToItem(ShipItem item, bool kinematic, Vector3 vel)
        {
            try
            {
                var proxy = item != null ? item.GetItemRigidbody() : null;
                var body = proxy != null ? proxy.GetBody() : null;
                if (proxy == null || body == null) return;
                if (item.currentActualBoat != null && item.currentWalkCol != null)
                {
                    Vector3 boatLocalPos = item.currentActualBoat.InverseTransformPoint(item.transform.position);
                    Quaternion boatLocalRot = Quaternion.Inverse(item.currentActualBoat.rotation) * item.transform.rotation;
                    proxy.transform.position = item.currentWalkCol.TransformPoint(boatLocalPos);
                    proxy.transform.rotation = item.currentWalkCol.rotation * boatLocalRot;
                }
                else
                {
                    proxy.transform.position = item.transform.position;
                    proxy.transform.rotation = item.transform.rotation;
                }
                body.isKinematic = kinematic;
                // While an item is in a hand (kinematic puppet) it must not push or collide with other
                // items on this machine — a kinematic body still shoves dynamic ones, so turn collision
                // detection off entirely; restore it when the item is freed to local physics.
                body.detectCollisions = !kinematic;
                if (kinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                else
                {
                    // A dropped item is snapped to the authoritative drop point, which may overlap other
                    // items whose local pile differs from the sender's. Re-enabling collisions then makes
                    // Unity eject it at high speed. Cap the depenetration speed and clamp the throw velocity.
                    body.maxDepenetrationVelocity = SettleDepenetration;
                    body.velocity = Vector3.ClampMagnitude(vel, MaxDropSpeed);
                    body.angularVelocity = Vector3.zero;
                }
            }
            catch { }
        }

        private static void EnsureBoatParentState(ShipItem item, ushort boatIndex)
        {
            if (item == null) return;
            try
            {
                Transform boat = BoatLocator.FindByIndex(boatIndex);
                if (boat == null) return;

                BoatEmbarkCollider embark = null;
                foreach (var candidate in boat.GetComponentsInChildren<BoatEmbarkCollider>(true))
                {
                    if (candidate != null && candidate.transform.parent == boat)
                    {
                        embark = candidate;
                        break;
                    }
                    if (embark == null) embark = candidate;
                }
                if (embark == null || embark.walkCollider == null) return;

                item.currentActualBoat = boat;
                item.currentWalkCol = embark.walkCollider;
                item.transform.parent = boat;

                var embarkCollider = embark.GetComponent<Collider>();
                if (embarkCollider != null)
                {
                    if (_fShipItemCurrentBoatCollider == null)
                        _fShipItemCurrentBoatCollider = typeof(ShipItem).GetField("currentBoatCollider", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fShipItemCurrentBoatCollider != null)
                        _fShipItemCurrentBoatCollider.SetValue(item, embarkCollider);
                }

                var saveable = item.GetComponent<SaveablePrefab>();
                var boatSaveable = boat.parent != null ? boat.parent.GetComponent<SaveableObject>() : null;
                if (saveable != null && boatSaveable != null)
                    saveable.SetParentObject(boatSaveable.sceneIndex);

                var irb = item.GetItemRigidbody();
                if (irb == null) return;
                if (_fItemRigidbodyOnBoat == null)
                    _fItemRigidbodyOnBoat = typeof(ItemRigidbody).GetField("onBoat", BindingFlags.NonPublic | BindingFlags.Instance);
                bool onBoat = _fItemRigidbodyOnBoat != null && (bool)_fItemRigidbodyOnBoat.GetValue(irb);
                if (!onBoat)
                    irb.EnterBoat();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] Failed to parent item to boat: " + e.Message);
            }
        }

        private static void EnsureWorldParentState(ShipItem item)
        {
            if (item == null) return;
            try
            {
                if (_mShipItemExitBoat == null)
                    _mShipItemExitBoat = typeof(ShipItem).GetMethod("ExitBoat", BindingFlags.NonPublic | BindingFlags.Instance);
                if (item.currentActualBoat != null && _mShipItemExitBoat != null)
                    _mShipItemExitBoat.Invoke(item, null);

                Transform world = FloatingOriginManager.instance != null ? FloatingOriginManager.instance.transform : null;
                if (world != null)
                {
                    item.transform.parent = world;
                    var irb = item.GetItemRigidbody();
                    if (irb != null) irb.transform.parent = world;
                }

                item.currentActualBoat = null;
                item.currentWalkCol = null;
                if (_fShipItemCurrentBoatCollider == null)
                    _fShipItemCurrentBoatCollider = typeof(ShipItem).GetField("currentBoatCollider", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fShipItemCurrentBoatCollider != null)
                    _fShipItemCurrentBoatCollider.SetValue(item, null);
                if (_fShipItemCurrentlyStayedEmbarkCol == null)
                    _fShipItemCurrentlyStayedEmbarkCol = typeof(ShipItem).GetField("currentlyStayedEmbarkCol", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fShipItemCurrentlyStayedEmbarkCol != null)
                    _fShipItemCurrentlyStayedEmbarkCol.SetValue(item, null);

                var itemRb = item.GetItemRigidbody();
                if (itemRb != null)
                {
                    if (_fItemRigidbodyOnBoat == null)
                        _fItemRigidbodyOnBoat = typeof(ItemRigidbody).GetField("onBoat", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fItemRigidbodyOnBoat != null)
                        _fItemRigidbodyOnBoat.SetValue(itemRb, false);
                }

                var saveable = item.GetComponent<SaveablePrefab>();
                if (saveable != null && saveable.GetParentObject() != -3)
                    saveable.SetParentObject(-1);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] Failed to unparent item from boat: " + e.Message);
            }
        }

        // -----------------------------------------------------------------
        // Lifecycle: spawn/despawn (host authoritative)
        // -----------------------------------------------------------------

        private void BroadcastSpawn(ItemEntry e)
        {
            if (e == null || e.Item == null) return;
            BuildPose(e.Item, _net.Clock.ServerTick, out CoordFrame frame, out ushort boatIndex,
                      out Vector3 pos, out Quaternion rot, out Vector3 vel);
            _net.Broadcast(new SpawnObjectMsg
            {
                Kind = (byte)NetObjKind.Item,
                InstanceId = e.InstanceId,
                PrefabIndex = e.PrefabIndex,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                HolderNetId = e.HolderNetId,
                Amount = e.Item.amount,
                Health = e.Item.health,
                Sold = e.Item.sold,
                Nailed = e.Item.nailed,
                CrateId = CrateIdOf(e.Item),
                CargoPort = CargoPortOf(e.Item),
                InventorySlot = e.InventorySlot,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out spawn id=" + e.InstanceId + " prefab=" + e.PrefabIndex);
        }

        private void BroadcastDespawn(ItemEntry e)
        {
            if (e == null) return;
            _net.Registry.Remove(e.NetId);
            _net.Broadcast(new DespawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = e.InstanceId },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out despawn id=" + e.InstanceId);
        }

        public void OnSpawnObject(SpawnObjectMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (msg.Kind != (byte)NetObjKind.Item) return;
            // Match the host's item to one of our own (remap id) or spawn it if we have none.
            var e = ResolveClient(msg.InstanceId, msg.PrefabIndex, msg.Frame, msg.BoatIndex, msg.Pos,
                                  msg.Amount, msg.Health, msg.Sold, msg.Nailed, allowSpawn: msg.HolderNetId == 0);
            if (e == null || e.Item == null)
            {
                Remember("reject spawn id=" + msg.InstanceId);
                return;
            }
            e.HolderNetId = msg.HolderNetId;
            e.InventorySlot = msg.InventorySlot;
            ApplyScalarState(e.Item, msg.Amount, msg.Health, msg.Sold, msg.Nailed);
            ApplyCrateMembership(e.Item, msg.CrateId);
            ApplyCargoMembership(e.Item, msg.CargoPort);
            SetRemoteInventoryVisual(e.Item, hidden: msg.InventorySlot >= 0 && msg.HolderNetId != _net.MyNetId);
            if (msg.HolderNetId != 0 && msg.HolderNetId != _net.MyNetId)
            {
                ConfigureNetFrame(e, msg.Frame, msg.BoatIndex);
                e.Net.Push(_net.Clock.ServerTick, msg.Pos, msg.Rot, msg.Vel);
            }
            Remember("in spawn id=" + msg.InstanceId);
        }

        public void OnDespawnObject(DespawnObjectMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (msg.Kind != (byte)NetObjKind.Item) return;
            // We claimed this item into our own belt — the despawn is the host dropping its shared copy in
            // response. Keep our local (now player-local) item; just untrack it. Other peers destroy theirs.
            if (_localClaimed.Remove(msg.InstanceId))
            {
                if (_byInstanceId.TryGetValue(msg.InstanceId, out var claimed))
                {
                    _items.Remove(claimed);
                    _byInstanceId.Remove(msg.InstanceId);
                    _net.Registry.Remove(claimed.NetId);
                    if (claimed.Item != null) _byItem.Remove(claimed.Item);
                }
                Remember("in despawn id=" + msg.InstanceId + " (claimed -> keeping in belt)");
                return;
            }

            if (!_byInstanceId.TryGetValue(msg.InstanceId, out var e))
            {
                Remember("missing despawn id=" + msg.InstanceId);
                return;
            }
            var item = e.Item;
            _net.Registry.Remove(e.NetId);
            _items.Remove(e);
            _byInstanceId.Remove(msg.InstanceId);
            if (item != null)
            {
                _byItem.Remove(item);
                _localHeld.Remove(item);
                try { UnityEngine.Object.Destroy(item.gameObject); } catch { }
            }
            Remember("in despawn id=" + msg.InstanceId);
        }

        // -----------------------------------------------------------------
        // Held alt-actions (hammer/oar/eat/drink) — client forward, host replay
        // -----------------------------------------------------------------

        /// <summary>Client: a held item received a continuous OnAltHeld(GoPointer) tick. Throttled.</summary>
        public void NotifyAltHeld(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null || item.held == null) return;
            float interval = 1f / Mathf.Max(1f, AltHeldHz);
            _altHeldTimer += Time.deltaTime;
            if (_altHeldTimer < interval) return;
            _altHeldTimer = 0f;
            ForwardHeldAction(item, ItemAction.AltHeld, reliable: false);
        }

        /// <summary>
        /// Client: the player consumed the item (ate food → it will DestroyItem locally). Tell the host
        /// to destroy its authoritative copy. Must be called BEFORE the local destroy so the entry still
        /// resolves; the eater's PlayerNeeds is personal and stays local.
        /// </summary>
        public void NotifyConsume(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            SendRequest(e, ItemAction.Consume, reliable: true);
            Remember("out consume #" + e.Index + " '" + item.name + "'");
        }

        /// <summary>
        /// A hammer (un)nailed a TARGET item locally (chosen by the local player's own pointer). The
        /// hammer's own held-action replay can't aim at the right target, so we sync only the result —
        /// target.nailed. Client forwards it to the host; the host (which already applied it via vanilla)
        /// just broadcasts so other peers learn it (a resting nailed item isn't otherwise streamed).
        /// </summary>
        public void OnLocalNail(ShipItem target)
        {
            if (_net.State != LinkState.Connected || target == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(target, out var e)) return;
            long tick = _net.Clock.ServerTick;
            if (_net.Role == Role.Client)
            {
                var msg = BuildRequest(e, ItemAction.Nail, tick);
                msg.Nailed = target.nailed;
                _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("out nail #" + e.Index + "=" + target.nailed + " '" + target.name + "'");
            }
            else
            {
                _net.Broadcast(BuildState(e, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("nail(host) #" + e.Index + "=" + target.nailed);
            }
        }

        /// <summary>
        /// Крючок удочки появился/пропал ЛОКАЛЬНО: attach через ванильный OnItemClick (health 0→1,
        /// крючок-предмет уничтожен) или DetachHook (рыба сорвалась / шанс при CollectFish). Симуляция
        /// рыбалки бежит только на машине держащего, поэтому форвардим результат — health удочки (как
        /// nail): клиент шлёт RodHook (+Consume за потраченный крючок), хост применяет и рассылает
        /// ItemState; хост-рыбак просто рассылает своё уже изменённое состояние.
        /// </summary>
        public void OnLocalRodHook(ShipItemFishingRod rod, bool attached, ShipItem consumedHook)
        {
            if (_net.State != LinkState.Connected || rod == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(rod, out var e)) return;
            long tick = _net.Clock.ServerTick;
            if (_net.Role == Role.Client)
            {
                // Ваниль уже уничтожила локальную копию крючка; пусть хост убьёт общую (как еда/продажа).
                if (consumedHook != null)
                {
                    int hookId = InstanceIdOf(consumedHook);
                    int hookPrefab = PrefabIndexOf(consumedHook);
                    if (hookId > 0 && hookPrefab > 0 && _hostIds.Contains(hookId))
                        _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Consume, InstanceId = hookId, PrefabIndex = hookPrefab },
                                       LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                _net.Broadcast(BuildRequest(e, ItemAction.RodHook, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("out rod-hook #" + e.Index + "=" + (attached ? 1 : 0));
            }
            else
            {
                _net.Broadcast(BuildState(e, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("rod-hook(host) #" + e.Index + "=" + (attached ? 1 : 0));
            }
        }

        /// <summary>
        /// A crate's contents changed locally (the player inserted/withdrew an item via the crate UI).
        /// The new membership is already on the item (currentCrateId, set by vanilla Insert/Withdraw).
        /// Client forwards it; the host (authoritative) broadcasts the item's state so peers mirror it.
        /// </summary>
        public void OnLocalCrate(ShipItem item)
        {
            if (ApplyingCrate || _net.State != LinkState.Connected || item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            long tick = _net.Clock.ServerTick;
            if (_net.Role == Role.Client)
            {
                _net.Broadcast(BuildRequest(e, ItemAction.Crate, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("out crate #" + e.Index + "->" + CrateIdOf(item));
            }
            else
            {
                _net.Broadcast(BuildState(e, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("crate(host) #" + e.Index + "->" + CrateIdOf(item));
            }
        }

        /// <summary>Client: forward an unseal so the host authors the crate's contents (host-only creation).</summary>
        public bool ForwardUnseal(ShipItemCrate crate)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || crate == null) return false;
            var sv = crate.GetComponent<SaveablePrefab>();
            if (sv == null || sv.instanceId <= 0) return false;
            _net.Broadcast(new ItemRequestMsg
            {
                Action = ItemAction.Unseal,
                InstanceId = sv.instanceId,
                PrefabIndex = sv.prefabIndex,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out unseal crate id=" + sv.instanceId);
            return true;
        }

        // -----------------------------------------------------------------
        // Cargo storage — local money, shared membership. The player loads/unloads a port carrier with
        // their OWN wallet (vanilla runs locally); only the physical membership is synced, exactly like a
        // crate: the item's CargoPort rides every state message and ApplyCargoMembership mirrors it
        // (visual-only, no wallet). This method just triggers a state send when membership changes locally.
        // -----------------------------------------------------------------

        /// <summary>A cargo membership change happened locally (load/unload). Client forwards the request;
        /// the host mirrors it and broadcasts authoritative state.</summary>
        public void OnLocalCargo(ShipItem item)
        {
            if (ApplyingCargo || _net.State != LinkState.Connected || item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            int port = CargoPortOf(item);
            if (_net.Role == Role.Client && port < 0)
            {
                PrepareLocalWithdrawPickup(item);
                e.DropWithoutProxyVelocity = true;
                e.ForceWorldPoseUntilDrop = LocalPlayerBoat() == null;
            }
            else if (_net.Role == Role.Host && port < 0)
            {
                // У хоста ваниль отработала сама, но big-предмет она держит на смещении, захваченном
                // от «парковки+10 м» — пере-захватываем к руке (та же болячка, что у клиента).
                ResetPointerBigItemCapture(item);
            }
            long tick = _net.Clock.ServerTick;
            if (_net.Role == Role.Client)
                _net.Broadcast(BuildRequest(e, ItemAction.Cargo, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
            else
                _net.Broadcast(BuildState(e, tick), LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out cargo #" + e.Index + " port=" + port);
        }

        /// <summary>
        /// A personal belt membership changed locally (item entered or left a slot 0..4). Belt items are
        /// PLAYER-LOCAL, so this drives the two transitions between the shared world and the local belt:
        /// <list type="bullet">
        /// <item>world/hand → belt (slot &gt;= 0): claim the item — the host drops its shared copy and we keep
        /// ours locally (<see cref="ClaimItemToBelt"/>).</item>
        /// <item>belt → hand (slot &lt; 0): re-author the item on the host so it becomes shared again
        /// (<see cref="NotifyClientAuthored"/> + deferred Pickup once the host id lands).</item>
        /// </list>
        /// On the host the same transitions are handled implicitly by the <see cref="RefreshItems"/> diff
        /// (excluded belt item → despawn; reappearing item → spawn), so the host just forces a refresh.
        /// </summary>
        public void OnLocalInventory(ShipItem item)
        {
            if (_net.State != LinkState.Connected || item == null) return;
            int slot = PersonalInventorySlotOf(item);

            if (_net.Role == Role.Host)
            {
                // Host belt items are player-local too; the RefreshItems diff broadcasts despawn (in) / spawn (out).
                RefreshItems(force: true);
                Remember("local host belt slot=" + slot);
                return;
            }

            // Client
            if (slot >= 0)
            {
                ClaimItemToBelt(item, slot);
                return;
            }

            // belt → hand: the item is player-local (host doesn't know it). Re-author it on the host so it
            // rejoins the shared world; mark it for a Pickup once the host id is remapped onto our copy.
            RefreshItems(force: true);
            PrepareLocalWithdrawPickup(item);
            _pendingHeldItem = item;
            NotifyClientAuthored(item);
            Remember("out belt->hand author '" + item.name + "'");
        }

        /// <summary>Client (or host) put a shared item into a personal belt slot: it becomes player-local.
        /// The client asks the host to drop its authoritative copy and ignores the resulting despawn echo for
        /// this id (so the local belt item survives). Idempotent per id.</summary>
        private void ClaimItemToBelt(ShipItem item, int slot)
        {
            RefreshItems(force: true);

            if (_net.Role == Role.Host)
            {
                // Host's own belt: the RefreshItems diff already despawned it for clients. Nothing to send.
                _localHeld.Remove(item);
                RestoreLocalInventoryVisual(item, inInventory: true);
                Remember("local host claim->belt slot=" + slot);
                return;
            }

            int id = InstanceIdOf(item);
            int prefab = PrefabIndexOf(item);
            if (id > 0 && prefab > 0 && _hostIds.Contains(id) && !_localClaimed.Contains(id))
            {
                _localClaimed.Add(id);
                _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Consume, InstanceId = id, PrefabIndex = prefab },
                               LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("out claim->belt id=" + id + " slot=" + slot);
            }
            _localHeld.Remove(item);
            RestoreLocalInventoryVisual(item, inInventory: true);
        }

        /// <summary>Client: a held item received a discrete OnAltActivate(GoPointer).</summary>
        public void NotifyAltActivate(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null || item.held == null) return;
            // A crate's OnAltActivate only opens a LOCAL window (CrateSealUI / CrateInventory.OpenCrate) —
            // each player browses their own. Forwarding it would replay OnAltActivate on the host and pop
            // the window open on the HOST's screen instead of the client's. The crate's real state changes
            // (unseal, insert/withdraw) are mediated by their own relays (PreUnseal, OnLocalCrate), so the
            // UI-opening alt must stay local.
            if (item is ShipItemCrate) return;
            ForwardHeldAction(item, ItemAction.AltActivate, reliable: true);
        }

        public void NotifyLampHook(ShipItemLampHook hook, PickupableItem heldItem)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            var item = heldItem as ShipItem;
            if (hook == null || item == null || item.GetComponent<HangableItem>() == null) return;
            RefreshItems(force: true);
            if (!_byItem.TryGetValue(item, out var e)) return;
            int hookId = InstanceIdOf(hook);
            int hookPrefab = PrefabIndexOf(hook);
            if (hookId <= 0 || hookPrefab <= 0) return;

            _suppressNextDrop.Add(item);
            _localHeld.Remove(item);
            SnapHangableToHook(item, hook);
            MoveProxyToItem(item, kinematic: true, Vector3.zero);
            SetProxyAttached(item, true);
            var msg = BuildRequest(e, ItemAction.LampHook, _net.Clock.ServerTick);
            msg.CrateId = hookId;
            msg.CargoIndex = hookPrefab;
            msg.Attached = true;
            msg.Vel = Vector3.zero;
            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out lamp-hook #" + e.Index + " hook=" + hookId);
        }

        /// <summary>Client: vanilla changed scalars on the held item locally; make the host copy authoritative.</summary>
        public void NotifyHeldItemStateChanged(ShipItem item, string reason)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null || item.held == null) return;
            NotifyItemStateChanged(item, reason);
        }

        /// <summary>Client: vanilla changed item scalar state locally; make the host copy authoritative.</summary>
        public void NotifyItemStateChanged(ShipItem item, string reason)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            if (item.held != null)
            {
                _localHeld[item] = _net.MyNetId;
                SetPuppet(item, true);
            }
            SendRequest(e, ItemAction.State, reliable: true);
            Remember("out state #" + e.Index + " '" + item.name + "' " + reason +
                     " amount=" + item.amount.ToString("0.##") + " health=" + item.health.ToString("0.##"));
        }

        private void ForwardHeldAction(ShipItem item, ItemAction action, bool reliable)
        {
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            _localHeld[item] = _net.MyNetId;
            SendRequest(e, action, reliable);
            Remember("out " + action + " #" + e.Index + " '" + item.name + "'");
        }

        private void ReplayHeldAction(ItemEntry e, ItemAction action, uint actor)
        {
            if (e == null || e.Item == null) return;
            // Treat the actor as the holder so subsequent state carries the right owner.
            e.HolderNetId = actor;
            _localHeld[e.Item] = actor;

            string method = action == ItemAction.AltActivate ? "OnAltActivate" : "OnAltHeld";
            var prevHeld = e.Item.held;
            try
            {
                e.Item.held = HostPointer();
                var mi = e.Item.GetType().GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(GoPointer) }, null);
                if (mi != null) mi.Invoke(e.Item, new object[] { HostPointer() });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] Replay error " + method + " on '" +
                                         e.Item.GetType().Name + "': " + ex.Message);
            }
            finally
            {
                try { e.Item.held = prevHeld; } catch { }
            }
        }

        private bool TryApplyOarRow(ItemEntry e, ItemRequestMsg msg, uint actor)
        {
            var oar = e != null ? e.Item as ShipItemOar : null;
            if (oar == null) return false;

            try
            {
                e.HolderNetId = actor;
                _localHeld[e.Item] = actor;

                if (!e.Item.sold) return true;
                Transform boat = msg.Frame == CoordFrame.Boat ? BoatLocator.FindByIndex(msg.BoatIndex) : null;
                var body = BoatBody(boat);
                if (body == null)
                {
                    Remember("oar-row without Rigidbody boat=" + msg.BoatIndex);
                    return true;
                }

                if (!WireToWorld(msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, out Vector3 worldPos, out Quaternion worldRot))
                {
                    Remember("oar-row without pose boat=" + msg.BoatIndex);
                    return true;
                }

                float speedFactor = Mathf.InverseLerp(oar.maxBoatSpeed, 0f, body.velocity.magnitude);
                float step = 1f / Mathf.Max(1f, AltHeldHz);
                Vector3 force = (worldRot * Vector3.forward) * (0f - oar.rowForce) * step * speedFactor;
                body.AddForceAtPosition(force, worldPos);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] Failed to apply oar stroke: " + ex.Message);
                return true;
            }
        }

        private static Rigidbody BoatBody(Transform boat)
        {
            if (boat == null) return null;
            var body = boat.GetComponent<Rigidbody>();
            if (body != null) return body;
            return boat.parent != null ? boat.parent.GetComponent<Rigidbody>() : null;
        }

        private static bool WireToWorld(CoordFrame frame, ushort boatIndex, Vector3 pos, Quaternion rot,
                                        out Vector3 worldPos, out Quaternion worldRot)
        {
            if (frame == CoordFrame.Boat)
            {
                Transform boat = BoatLocator.FindByIndex(boatIndex);
                if (boat == null)
                {
                    worldPos = Vector3.zero;
                    worldRot = Quaternion.identity;
                    return false;
                }
                worldPos = boat.TransformPoint(pos);
                worldRot = boat.rotation * rot;
                return true;
            }

            worldPos = CoordSpace.Ready ? CoordSpace.RealToLocal(pos) : pos;
            worldRot = rot;
            return true;
        }

        private GoPointer HostPointer()
        {
            if (_gp == null) _gp = UnityEngine.Object.FindObjectOfType<GoPointer>();
            return _gp;
        }

        private static Vector3 RealItemVelocity(ShipItem item)
        {
            try
            {
                var proxy = item != null ? item.GetItemRigidbody() : null;
                var body = proxy != null ? proxy.GetBody() : null;
                return body != null ? body.velocity : Vector3.zero;
            }
            catch { return Vector3.zero; }
        }

        // -----------------------------------------------------------------
        // Per-type state (cooking / consumption)
        // -----------------------------------------------------------------

        // type name -> ordered field names. Both peers share this table so the wire array is positional.
        private static readonly Dictionary<string, string[]> ExtraFields = new Dictionary<string, string[]>
        {
            { "ShipItemStove", new[] { "currentHeat" } },
            { "ShopStove", new[] { "currentHeat" } },
            { "CookableFood", new[] { "currentHeat", "foodState" } },
            { "CookableFoodSoup", new[] { "currentHeat" } },
            { "CookableFoodKettle", new[] { "currentHeat" } },
            { "ShipItemFood", new[] { "foodState" } },
            { "ShipItemKettle", new[] { "currentWater", "currentTeaAmount", "currentTeaType" } },
            { "ShipItemSoup", new[] { "currentWater", "currentEnergy", "currentSpoiled", "currentSalted" } },
            { "ShipItemBottle", new[] { "capacity" } },
            { "StoveFuel", new[] { "lit", "inserted" } },
            { "ShipItemStoveFuel", new[] { "lit" } },
        };

        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();

        private void SendExtraState(float dt)
        {
            float interval = 1f / Mathf.Max(0.5f, ExtraStateHz);
            _extraTimer += dt;
            if (_extraTimer < interval) return;
            _extraTimer = 0f;

            foreach (var e in _items)
            {
                if (e.Item == null) continue;
                if (!ExtraFields.TryGetValue(e.Item.GetType().Name, out var names)) continue;
                var values = ReadExtra(e.Item, names);
                if (values == null) continue;
                _net.Broadcast(new ItemExtraStateMsg
                {
                    InstanceId = e.InstanceId,
                    PrefabIndex = e.PrefabIndex,
                    Values = values,
                }, LiteNetLib.DeliveryMethod.Unreliable);
            }
        }

        public void OnItemExtraState(ItemExtraStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (!_byInstanceId.TryGetValue(msg.InstanceId, out var e) || e.Item == null) return;
            if (e.HolderNetId == _net.MyNetId) return;   // don't fight the holder's local state
            if (!ExtraFields.TryGetValue(e.Item.GetType().Name, out var names)) return;
            WriteExtra(e.Item, names, msg.Values);
        }

        private static float[] ReadExtra(ShipItem item, string[] names)
        {
            try
            {
                var values = new float[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var fi = GetFieldDeep(item.GetType(), names[i]);
                    if (fi == null) { values[i] = 0f; continue; }
                    object v = fi.GetValue(item);
                    values[i] = ToFloat(v, fi.FieldType);
                }
                return values;
            }
            catch { return null; }
        }

        private static void WriteExtra(ShipItem item, string[] names, float[] values)
        {
            try
            {
                int n = Mathf.Min(names.Length, values.Length);
                for (int i = 0; i < n; i++)
                {
                    var fi = GetFieldDeep(item.GetType(), names[i]);
                    if (fi == null) continue;
                    fi.SetValue(item, FromFloat(values[i], fi.FieldType));
                }
            }
            catch { }
        }

        private static float ToFloat(object v, Type t)
        {
            if (v == null) return 0f;
            if (t == typeof(bool)) return (bool)v ? 1f : 0f;
            if (t.IsEnum) return Convert.ToInt32(v);
            if (t == typeof(int)) return (int)v;
            if (t == typeof(float)) return (float)v;
            try { return Convert.ToSingle(v); } catch { return 0f; }
        }

        private static object FromFloat(float f, Type t)
        {
            if (t == typeof(bool)) return f > 0.5f;
            if (t.IsEnum) return Enum.ToObject(t, Mathf.RoundToInt(f));
            if (t == typeof(int)) return Mathf.RoundToInt(f);
            if (t == typeof(float)) return f;
            try { return Convert.ChangeType(f, t); } catch { return f; }
        }

        private static FieldInfo GetFieldDeep(Type type, string name)
        {
            string key = type.FullName + "::" + name;
            if (_fieldCache.TryGetValue(key, out var cached)) return cached;
            FieldInfo fi = null;
            for (Type t = type; t != null && fi == null; t = t.BaseType)
                fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            _fieldCache[key] = fi;
            return fi;
        }

        private PickupableItem HeldItem()
        {
            try
            {
                if (_gp == null) _gp = UnityEngine.Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return null;
                if (_fHeldItem == null)
                    _fHeldItem = typeof(GoPointer).GetField("heldItem", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fHeldItem != null ? _fHeldItem.GetValue(_gp) as PickupableItem : null;
            }
            catch { return null; }
        }

        private void RefreshItems(float dt)
        {
            _refreshTimer += dt;
            if (_refreshTimer < 2f && _items.Count > 0) return;
            _refreshTimer = 0f;
            RefreshItems(force: true);
        }

        private void RefreshItems(bool force)
        {
            // Previously-known entries (carry live ShipItem refs; a destroyed item reads Unity-null).
            var prev = new Dictionary<int, ItemEntry>(_byInstanceId);

            // Alive set = previously-known still-alive (covers items that went temporarily inactive,
            // e.g. inside a crate, which FindObjectsOfType skips) + everything found this scan.
            var alive = new Dictionary<int, ShipItem>();
            foreach (var kv in prev)
                if (kv.Value.Item != null && HasStableIdentity(kv.Value.Item)) alive[kv.Key] = kv.Value.Item;   // Unity-null filters destroyed
            foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                if (item != null && HasStableIdentity(item)) alive[InstanceIdOf(item)] = item;

            // Establish the baseline once the local item set stops growing (save load finished).
            // Before that we never broadcast spawn/despawn or create copies — both peers are still
            // loading the same save and any "new" item is just a late-loaded shared one.
            if (alive.Count != _baselineCount)
            {
                _baselineCount = alive.Count;
                _baselineChangedAt = Time.unscaledTime;
            }
            if (!_baselineReady && _baselineCount > 0 && Time.unscaledTime - _baselineChangedAt >= SettleSeconds)
            {
                _baselineReady = true;
                // Client just finished loading its own world — ask the host for its full item set so we
                // can match/remap our copies to host ids (and spawn whatever we're missing).
                if (_net.Role == Role.Client) SendReadyPing();
            }

            // Host: an item that was known but is no longer alive was destroyed/consumed -> despawn.
            if (_net.Role == Role.Host && _baselineReady)
            {
                foreach (var kv in prev)
                    if (!alive.ContainsKey(kv.Key)) BroadcastDespawn(kv.Value);
            }

            var ordered = new List<ShipItem>(alive.Values);
            ordered.Sort(CompareItems);

            _items.Clear();
            _byItem.Clear();
            _byInstanceId.Clear();

            var newlyAdded = new List<ItemEntry>();
            for (int i = 0; i < ordered.Count && i <= ushort.MaxValue; i++)
            {
                var item = ordered[i];
                int instanceId = InstanceIdOf(item);
                int prefabIndex = PrefabIndexOf(item);
                prev.TryGetValue(instanceId, out var e);
                bool isNew = e == null;
                if (e == null)
                {
                    e = new ItemEntry
                    {
                        InstanceId = instanceId,
                        PrefabIndex = prefabIndex,
                        NetId = NetIdFor(instanceId),
                    };
                    if (_localHeld.TryGetValue(item, out var holder))
                        e.HolderNetId = holder;
                }
                e.Index = (ushort)i;
                e.InstanceId = instanceId;
                e.PrefabIndex = prefabIndex;
                e.Item = item;
                e.Net.InterpDelayMs = 90f;
                _items.Add(e);
                _byItem[item] = e;
                _byInstanceId[instanceId] = e;
                _net.Registry.RegisterFixed(e.NetId, NetObjKind.Item, NetRegistry.HostAuthority, item);
                if (isNew && _net.Role == Role.Host && _baselineReady) newlyAdded.Add(e);
            }

            // Host: items that appeared after load (caught fish, cooked food, crate contents) -> spawn.
            foreach (var e in newlyAdded) BroadcastSpawn(e);
        }

        /// <summary>Host: look up one of its own items by id (host is the id authority — no matching).</summary>
        private ItemEntry HostLookup(int instanceId, int prefabIndex)
        {
            if (instanceId <= 0 || prefabIndex <= 0) return null;
            if (_byInstanceId.TryGetValue(instanceId, out var e) && e.PrefabIndex == prefabIndex) return e;
            return null;
        }

        private static ShipItem FindLiveItem(int instanceId, int prefabIndex)
        {
            if (instanceId <= 0 || prefabIndex <= 0) return null;
            foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
            {
                if (item == null) continue;
                var saveable = item.GetComponent<SaveablePrefab>();
                if (saveable != null && saveable.instanceId == instanceId && saveable.prefabIndex == prefabIndex)
                    return item;
            }
            return null;
        }

        /// <summary>
        /// Client: resolve a host item id to a local entry. If we already track it, return it. Otherwise
        /// (once our own world has loaded) match it to one of our still-loaded items by prefab + position
        /// and adopt the host's id (RemapLocalItem); if there's no local candidate, spawn it. This is the
        /// host-authoritative identity bridge — no destroy, and only genuinely-missing items are created.
        /// </summary>
        private ItemEntry ResolveClient(int instanceId, int prefabIndex, CoordFrame frame, ushort boatIndex,
                                        Vector3 wirePos, float amount, float health, bool sold, bool nailed,
                                        bool allowSpawn)
        {
            if (instanceId <= 0 || prefabIndex <= 0) return null;
            _hostIds.Add(instanceId);

            if (_byInstanceId.TryGetValue(instanceId, out var known))
            {
                if (known.PrefabIndex != prefabIndex)
                {
                    Plugin.Logger.LogWarning("[ItemSync] id=" + instanceId + " prefab mismatch local=" +
                                             known.PrefabIndex + ", wire=" + prefabIndex);
                    return null;
                }
                return known;
            }

            if (!_baselineReady) return null;   // wait until our own save items are loaded

            // An item we just authored locally (caught fish / bought good) is authored by the host now;
            // adopt the host id onto that exact local copy before generic matching. Market cargo can be
            // bought in batches, so consume the oldest pending item with the matching prefab.
            var pending = PopPendingClientItem(prefabIndex);
            if (pending != null)
            {
                RemapLocalItem(pending, instanceId);
                RefreshItems(force: true);
                _byInstanceId.TryGetValue(instanceId, out var fr);
                // If this item was just withdrawn from our belt to hand, tell the host we hold it now.
                if (pending == _pendingHeldItem)
                {
                    _pendingHeldItem = null;
                    if (fr != null && fr.Item != null)
                    {
                        fr.HolderNetId = _net.MyNetId;
                        _localHeld[fr.Item] = _net.MyNetId;
                        SetPuppet(fr.Item, true);
                        SendRequest(fr, ItemAction.Pickup, reliable: true);
                        Remember("out belt->hand pickup id=" + instanceId);
                    }
                }
                return fr;
            }

            var local = FindUnclaimedMatch(prefabIndex, frame, boatIndex, wirePos);
            if (local != null)
            {
                RemapLocalItem(local, instanceId);
                RefreshItems(force: true);
                return _byInstanceId.TryGetValue(instanceId, out var rm) ? rm : null;
            }

            // No local match. Only spawn for FREE items (allowSpawn): a held item's pose is the holder's
            // hand, so position matching can't work — defer; once it's dropped we match/spawn at rest.
            if (!allowSpawn) return null;

            var spawned = SpawnClientItem(instanceId, prefabIndex, frame, boatIndex, wirePos,
                                          Quaternion.identity, amount, health, sold, nailed);
            if (spawned == null) return null;
            RefreshItems(force: true);
            return _byInstanceId.TryGetValue(instanceId, out var e) && e.PrefabIndex == prefabIndex ? e : null;
        }

        private ShipItem PopPendingClientItem(int prefabIndex)
        {
            for (int i = 0; i < _pendingClientItems.Count; i++)
            {
                var item = _pendingClientItems[i];
                if (item == null)
                {
                    _pendingClientItems.RemoveAt(i--);
                    continue;
                }
                if (PrefabIndexOf(item) != prefabIndex) continue;
                _pendingClientItems.RemoveAt(i);
                return item;
            }
            return null;
        }

        /// <summary>Find the nearest local ShipItem (same prefab, not yet claimed by a host id) within MatchRadius.</summary>
        private ShipItem FindUnclaimedMatch(int prefabIndex, CoordFrame frame, ushort boatIndex, Vector3 wirePos)
        {
            ShipItem best = null;
            float bestSq = MatchRadius * MatchRadius;
            foreach (var e in _items)
            {
                if (e.Item == null) continue;
                if (e.PrefabIndex != prefabIndex) continue;
                if (_hostIds.Contains(e.InstanceId)) continue;   // already a host id (claimed/remapped)

                Vector3 cand;
                if (frame == CoordFrame.Boat)
                {
                    Transform boat = BoatLocator.FindByIndex(boatIndex);
                    if (boat == null) continue;
                    cand = boat.InverseTransformPoint(e.Item.transform.position);
                }
                else
                {
                    cand = CoordSpace.Ready ? CoordSpace.LocalToReal(e.Item.transform.position) : e.Item.transform.position;
                }

                float sq = (cand - wirePos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e.Item; }
            }
            return best;
        }

        /// <summary>Reassign a live local item's instanceId to the host's, keeping engine dedup/caches sane.</summary>
        private void RemapLocalItem(ShipItem local, int hostId)
        {
            var saveable = local != null ? local.GetComponent<SaveablePrefab>() : null;
            if (saveable == null) return;
            int oldId = saveable.instanceId;
            if (oldId == hostId) return;

            try
            {
                if (SaveablePrefab.existingInstanceIds != null)
                {
                    SaveablePrefab.existingInstanceIds.Remove(oldId);
                    if (!SaveablePrefab.existingInstanceIds.Contains(hostId))
                        SaveablePrefab.existingInstanceIds.Add(hostId);
                }
            }
            catch { }

            RemoveCachedLocalItem(oldId);   // drop stale BoatLocalItems cache entry so streaming won't re-add old id
            saveable.instanceId = hostId;

            if (_byInstanceId.TryGetValue(oldId, out var old))
            {
                _byInstanceId.Remove(oldId);
                if (old.Item != null) _byItem.Remove(old.Item);
                _net.Registry.Remove(old.NetId);
            }
            Remember("remap id " + oldId + "→" + hostId + " '" + local.name + "'");
        }

        /// <summary>Host: send a SpawnObject for every replicated item so a freshly-ready client can match/spawn.</summary>
        private void SendManifest()
        {
            if (_net.Role != Role.Host) return;
            int n = 0;
            foreach (var e in _items)
            {
                if (e.Item == null || !ShouldReplicate(e.Item)) continue;
                BroadcastSpawn(e);
                n++;
            }
            Plugin.Logger.LogInfo("[ItemSync] Item manifest sent: " + n + " items");
        }

        /// <summary>Client: ask the host for the full item set (InstanceId == 0 sentinel) once we're loaded.</summary>
        private void SendReadyPing()
        {
            if (_net.Role != Role.Client) return;
            _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Pose, InstanceId = 0, PrefabIndex = 0 },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out ready-ping");
        }

        private static bool ShouldReplicate(ShipItem item)
        {
            if (item == null) return false;
            return HasStableIdentity(item);
        }

        private static bool HasStableIdentity(ShipItem item)
        {
            if (item == null) return false;
            var saveable = item.GetComponent<SaveablePrefab>();
            // Personal belt items (slots 0..4) are PLAYER-LOCAL: each player owns its own belt, persisted via
            // CoopProfile. They are excluded from the shared/host-authoritative item set so they never leak
            // across peers. The host's RefreshItems diff turns the belt-in/out transitions into despawn/spawn.
            return saveable != null && saveable.instanceId > 0 && saveable.prefabIndex > 0 && item.sold
                   && !InPersonalInventory(item);
        }

        private static int CompareItems(ShipItem a, ShipItem b)
        {
            int ai = InstanceIdOf(a);
            int bi = InstanceIdOf(b);
            if (ai != 0 && bi != 0 && ai != bi) return ai.CompareTo(bi);
            if (ai != bi) return bi.CompareTo(ai);
            return string.CompareOrdinal(BoatLocator.PathOf(a.transform), BoatLocator.PathOf(b.transform));
        }

        private static int InstanceIdOf(ShipItem item)
        {
            var s = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return s != null ? s.instanceId : 0;
        }

        private static int CrateIdOf(ShipItem item)
        {
            var s = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return s != null ? s.currentCrateId : 0;
        }

        // GetCurrentInventorySlot() returns portIndex+100 while the item is stored in a port cargo carrier
        // (currentCargoCarrier is private); other inventory slots are < 100. So slot >= 100 → cargo.
        private static int CargoPortOf(ShipItem item)
        {
            if (item == null) return -1;
            try { int slot = item.GetCurrentInventorySlot(); return slot >= 100 ? slot - 100 : -1; }
            catch { return -1; }
        }

        private static int PrefabIndexOf(ShipItem item)
        {
            var s = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return s != null ? s.prefabIndex : 0;
        }

        private static string PoseLabel(CoordFrame frame, ushort boatIndex, Vector3 pos)
        {
            return "frame=" + frame + " boat=" + boatIndex + " pos=" + pos.ToString("F2");
        }

        private ShipItem SpawnClientItem(int instanceId, int prefabIndex, CoordFrame frame, ushort boatIndex,
                                         Vector3 wirePos, Quaternion wireRot, float amount, float health, bool sold, bool nailed)
        {
            try
            {
                if (!_baselineReady) return null;   // don't create until our own save items are loaded
                if (instanceId <= 0 || prefabIndex <= 0) return null;
                if (_byInstanceId.ContainsKey(instanceId)) return _byInstanceId[instanceId].Item;
                var dir = PrefabsDirectory.instance;
                if (dir == null || dir.directory == null || prefabIndex <= 0 || prefabIndex >= dir.directory.Length)
                    return null;
                var prefab = dir.directory[prefabIndex];
                if (prefab == null) return null;

                Vector3 pos;
                Quaternion rot;
                Transform boatParent = null;
                if (frame == CoordFrame.Boat)
                {
                    boatParent = BoatLocator.FindByIndex(boatIndex);
                    if (boatParent == null) return null;
                    pos = boatParent.TransformPoint(wirePos);
                    rot = boatParent.rotation * wireRot;
                }
                else
                {
                    pos = CoordSpace.Ready ? CoordSpace.RealToLocal(wirePos) : wirePos;
                    rot = wireRot;
                }

                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                var saveable = go.GetComponent<SaveablePrefab>();
                var item = go.GetComponent<ShipItem>();
                if (saveable == null || item == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return null;
                }
                saveable.instanceId = instanceId;
                saveable.prefabIndex = prefabIndex;
                item.sold = sold;
                item.nailed = nailed;
                item.amount = amount;
                item.health = health;
                if (boatParent != null) item.transform.parent = boatParent;
                RemoveCachedLocalItem(instanceId);
                Remember("spawn client id=" + instanceId + " prefab=" + prefabIndex);
                return item;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] Failed to create client item id=" +
                                         instanceId + ": " + e.Message);
                return null;
            }
        }

        private static void RemoveCachedLocalItem(int instanceId)
        {
            if (instanceId <= 0) return;
            try
            {
                if (_fBoatCachedItems == null)
                    _fBoatCachedItems = typeof(BoatLocalItems).GetField("cachedItems", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fBoatCachedItems == null) return;

                foreach (var localItems in UnityEngine.Object.FindObjectsOfType<BoatLocalItems>())
                {
                    var list = _fBoatCachedItems.GetValue(localItems) as List<SavePrefabData>;
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] != null && list[i].instanceId == instanceId)
                            list.RemoveAt(i);
                    }
                    if (list.Count == 0) _fBoatCachedItems.SetValue(localItems, null);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] Failed to remove cached item id=" + instanceId + ": " + e.Message);
            }
        }

        private static uint NetIdFor(int instanceId)
        {
            unchecked
            {
                return 0x40000000u | ((uint)instanceId & 0x3fffffffu);
            }
        }

        private static Transform ParentBoat(Transform t)
        {
            while (t != null)
            {
                if (t.GetComponent<BoatEmbarkCollider>() != null && t.parent != null)
                    return t.parent;
                t = t.parent;
            }
            return null;
        }

        private void Remember(string text)
        {
            _last = text;
            _lastEventTick = _net.Clock.ServerTick;
            if (text != null &&
                text.IndexOf("Pose", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("held ", StringComparison.OrdinalIgnoreCase) < 0)
                Plugin.Logger.LogInfo("[ItemSync] " + text);
        }

        // -----------------------------------------------------------------
        // Client-authored runtime items (fishing, shop buy). Such an item is created on the client
        // (RNG id) but must be host-authoritative. The client asks the host to create the real twin;
        // the host's SpawnObject then replicates it and the client remaps its local copy (ResolveClient
        // via _pendingClientItems). Sold goods take the inverse path: NotifySold tells the host to destroy.
        // -----------------------------------------------------------------

        /// <summary>
        /// Client: a runtime item was just authored locally (a caught fish, or a good just bought with
        /// the player's own wallet); ask the host to author the authoritative twin so it becomes shared.
        /// </summary>
        public void NotifyClientAuthored(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || item == null) return;
            if (!_pendingClientItems.Contains(item))
                _pendingClientItems.Add(item);
            BuildPose(item, _net.Clock.ServerTick, out CoordFrame frame, out ushort boatIndex,
                      out Vector3 pos, out Quaternion rot, out _);
            _net.Broadcast(new FishCatchMsg
            {
                PrefabIndex = PrefabIndexOf(item),
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out client-authored prefab=" + PrefabIndexOf(item) + " '" + item.name + "'");
        }

        /// <summary>
        /// Client: a shared item was sold to a shopkeeper locally (own wallet credited by vanilla, item
        /// destroyed locally). Tell the host to destroy its authoritative copy so it despawns for everyone.
        /// Reuses the Consume action (host destroys its copy by id). Call BEFORE the local destroy.
        /// </summary>
        public void NotifySold(int instanceId, int prefabIndex)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (instanceId <= 0 || prefabIndex <= 0) return;
            _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Consume, InstanceId = instanceId, PrefabIndex = prefabIndex },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out sold(despawn) id=" + instanceId);
        }

        /// <summary>Host: author the item the client created; RefreshItems then broadcasts the SpawnObject.</summary>
        public void OnFishCatch(FishCatchMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            try
            {
                var dir = PrefabsDirectory.instance;
                if (dir == null || dir.directory == null || msg.PrefabIndex <= 0 || msg.PrefabIndex >= dir.directory.Length)
                {
                    Remember("reject fish prefab=" + msg.PrefabIndex);
                    return;
                }
                var prefab = dir.directory[msg.PrefabIndex];
                if (prefab == null) return;

                Vector3 pos;
                Quaternion rot;
                if (msg.Frame == CoordFrame.Boat)
                {
                    Transform boat = BoatLocator.FindByIndex(msg.BoatIndex);
                    if (boat == null) { Remember("reject fish boat=" + msg.BoatIndex); return; }
                    pos = boat.TransformPoint(msg.Pos);
                    rot = boat.rotation * msg.Rot;
                }
                else
                {
                    pos = CoordSpace.Ready ? CoordSpace.RealToLocal(msg.Pos) : msg.Pos;
                    rot = msg.Rot;
                }

                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                var item = go.GetComponent<ShipItem>();
                var saveable = go.GetComponent<SaveablePrefab>();
                if (item == null || saveable == null) { UnityEngine.Object.Destroy(go); return; }
                item.sold = true;
                saveable.prefabIndex = msg.PrefabIndex;
                saveable.RegisterToSave();   // assigns a fresh nonzero host id
                RefreshItems(force: true);   // diff detects the new item and broadcasts SpawnObject
                Remember("in fish catch prefab=" + msg.PrefabIndex + " id=" + saveable.instanceId);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] OnFishCatch: " + e.Message);
            }
        }

        // -----------------------------------------------------------------
        // Рыбалка: косметика заброса. Леска/боббер — локальная физика держащего; остальные машины
        // видели удочку «без лески» (боббер висит у удилища на min-длине). Держащий (rod.held != null —
        // поле ставит только машина реального держателя) стримит позицию боббера (real-space) + длину
        // лески + изгиб ~10 Гц; хост ретранслирует. Получатель делает боббер кинематическим и ведёт его
        // к цели, а длину подставляет в currentTargetLength — ванильный ExtraLateUpdate сам лерпит
        // linearLimit и рисует леску (UpdateRope). Поток пропал (дроп/дисконнект) → таймаут, физика
        // боббера возвращается.
        // -----------------------------------------------------------------

        private const float RodStateHz = 10f;
        private const float RodRemoteTimeout = 1.5f;

        private sealed class RodRemote
        {
            public Vector3 RealPos;
            public float Limit;
            public float Bend;
            public float LastTime;
            public bool Kinematic;   // мы уже перевели боббер в kinematic (надо вернуть при выходе)
        }

        private readonly Dictionary<int, RodRemote> _rodRemote = new Dictionary<int, RodRemote>();
        private readonly List<int> _rodDone = new List<int>();
        private float _rodSendTimer;

        private static readonly FieldInfo RodBobberJointField = typeof(ShipItemFishingRod).GetField(
            "bobberJoint", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RodTargetLengthField = typeof(ShipItemFishingRod).GetField(
            "currentTargetLength", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RodBendField = typeof(ShipItemFishingRod).GetField(
            "currentRodBend", BindingFlags.Instance | BindingFlags.NonPublic);

        private void TickRods(float dt)
        {
            SendLocalRodState(dt);
            ApplyRemoteRods();
        }

        /// <summary>Стрим состояния заброса удочки, которую физически держит ЛОКАЛЬНЫЙ игрок.</summary>
        private void SendLocalRodState(float dt)
        {
            _rodSendTimer += dt;
            if (_rodSendTimer < 1f / RodStateHz) return;
            _rodSendTimer = 0f;
            if (!CoordSpace.Ready) return;

            try
            {
                foreach (var e in _items)
                {
                    var rod = e.Item as ShipItemFishingRod;
                    if (rod == null || rod.held == null || !rod.sold || e.InstanceId <= 0) continue;
                    var joint = RodBobberJointField?.GetValue(rod) as ConfigurableJoint;
                    if (joint == null) continue;
                    _net.Broadcast(new RodStateMsg
                    {
                        InstanceId = e.InstanceId,
                        PrefabIndex = e.PrefabIndex,
                        RealPos = CoordSpace.LocalToReal(joint.transform.position),
                        Limit = joint.linearLimit.limit,
                        Bend = RodBendField != null ? (float)RodBendField.GetValue(rod) : 0f,
                    }, LiteNetLib.DeliveryMethod.Unreliable);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] Fishing rod stream error: " + ex.Message);
            }
        }

        /// <summary>Входящее состояние заброса чужой удочки; хост дополнительно ретранслирует всем.</summary>
        public void OnRodState(RodStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            // Ретрансляция всем (в т.ч. отправителю — его копия held != null, он проигнорирует ниже).
            if (_net.Role == Role.Host) _net.Broadcast(msg, LiteNetLib.DeliveryMethod.Unreliable);

            if (!_byInstanceId.TryGetValue(msg.InstanceId, out var e)) return;
            var rod = e.Item as ShipItemFishingRod;
            if (rod == null || rod.held != null) return;   // сами держим — эхо, игнор

            if (!_rodRemote.TryGetValue(msg.InstanceId, out var r))
            {
                r = new RodRemote();
                _rodRemote[msg.InstanceId] = r;
                Remember("in rod-cast id=" + msg.InstanceId);
            }
            r.RealPos = msg.RealPos;
            r.Limit = msg.Limit;
            r.Bend = msg.Bend;
            r.LastTime = Time.unscaledTime;
        }

        /// <summary>Каждый кадр (origin дрейфует): ведём бобберы удочек, которые держат другие игроки.</summary>
        private void ApplyRemoteRods()
        {
            if (_rodRemote.Count == 0) return;
            _rodDone.Clear();

            foreach (var kv in _rodRemote)
            {
                var r = kv.Value;
                ItemEntry e;
                var rod = _byInstanceId.TryGetValue(kv.Key, out e) ? e.Item as ShipItemFishingRod : null;
                ConfigurableJoint joint = null;
                try { joint = rod != null ? RodBobberJointField?.GetValue(rod) as ConfigurableJoint : null; } catch { }

                bool expired = rod == null || joint == null || rod.held != null ||
                               Time.unscaledTime - r.LastTime > RodRemoteTimeout;
                if (expired)
                {
                    try
                    {
                        var body = joint != null ? joint.GetComponent<Rigidbody>() : null;
                        if (r.Kinematic && body != null)
                        {
                            body.isKinematic = false;
                            body.velocity = Vector3.zero;
                            body.angularVelocity = Vector3.zero;
                        }
                    }
                    catch { }
                    _rodDone.Add(kv.Key);
                    continue;
                }

                if (!CoordSpace.Ready) continue;
                try
                {
                    var body = joint.GetComponent<Rigidbody>();
                    if (body != null && !body.isKinematic) { body.isKinematic = true; r.Kinematic = true; }

                    Vector3 target = CoordSpace.RealToLocal(r.RealPos);
                    var t = joint.transform;
                    t.position = (target - t.position).sqrMagnitude > 25f
                        ? target
                        : Vector3.Lerp(t.position, target, Time.deltaTime * 12f);

                    // Ваниль сама лерпит linearLimit к currentTargetLength и рисует леску/изгиб
                    // (ExtraLateUpdate → UpdateRope; FishingRodFish.FixedUpdate → UpdateBend).
                    RodTargetLengthField?.SetValue(rod, r.Limit);
                    RodBendField?.SetValue(rod, r.Bend);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning("[ItemSync] Bobber tracking error id=" + kv.Key + ": " + ex.Message);
                    _rodDone.Add(kv.Key);
                }
            }

            foreach (int id in _rodDone) _rodRemote.Remove(id);
        }

        public void Clear()
        {
            foreach (var e in _items)
                if (e != null && e.Item != null)
                    RestoreDisconnectedItem(e.Item);

            // Вернуть физику бобберам удочек, которые мы вели удалённо (пока живы lookup-таблицы).
            foreach (var kv in _rodRemote)
            {
                if (!kv.Value.Kinematic) continue;
                try
                {
                    var rod = _byInstanceId.TryGetValue(kv.Key, out var re) ? re.Item as ShipItemFishingRod : null;
                    var joint = rod != null ? RodBobberJointField?.GetValue(rod) as ConfigurableJoint : null;
                    var body = joint != null ? joint.GetComponent<Rigidbody>() : null;
                    if (body != null) { body.isKinematic = false; body.velocity = Vector3.zero; }
                }
                catch { }
            }
            _rodRemote.Clear();
            _rodSendTimer = 0f;

            _items.Clear();
            _byItem.Clear();
            _byInstanceId.Clear();
            _localHeld.Clear();
            _pendingDynamic.Clear();
            _suppressNextDrop.Clear();
            _hostIds.Clear();
            _localClaimed.Clear();
            _pendingClientItems.Clear();
            _pendingHeldItem = null;
            _gp = null;
            _fHeldItem = null;
            _refreshTimer = 0f;
            _sendTimer = 0f;
            _heldPoseTimer = 0f;
            _extraTimer = 0f;
            _altHeldTimer = 0f;
            _baselineReady = false;
            _baselineCount = -1;
            _baselineChangedAt = 0f;
            _last = "—";
            _lastEventTick = 0L;
        }
    }

    public static class ItemPatches
    {
        private static FieldInfo _fHeldItem;
        private static FieldInfo _fOarIsRowing;

        public static void Apply(Harmony harmony)
        {
            bool pickup = TryPatch(harmony, typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) }, postfixName: nameof(PostPickup));
            bool drop = TryPatch(harmony, typeof(GoPointer), "DropItem", Type.EmptyTypes, prefixName: nameof(PreDrop), postfixName: nameof(PostDrop));
            bool bottleClick = TryPatch(harmony, typeof(ShipItemBottle), "OnItemClick", new[] { typeof(PickupableItem) },
                prefixName: nameof(PreBottleItemClick), postfixName: nameof(PostBottleItemClick));
            bool oarHeld = TryPatch(harmony, typeof(ShipItemOar), "OnAltHeld", Type.EmptyTypes, postfixName: nameof(PostOarAltHeld));
            // Eating food is not an OnAltHeld replay (OnAltHeld only sets a flag; EatFood does the consume
            // + DestroyItem and touches the eater's personal PlayerNeeds). Forward the actual consume so
            // the host destroys its copy without running its own PlayerNeeds.
            bool eat = TryPatch(harmony, typeof(ShipItemFood), "EatFood", Type.EmptyTypes, prefixName: nameof(PreEatFood));
            // Hammer nailing targets the item the LOCAL pointer aims at; the held-action replay can't
            // reproduce that aim, so we sync the result (target.nailed) from the two sites that change it:
            // NailItem (nail completes after the 2s hold) and OnAltActivate (instant un-nail).
            bool nail = TryPatch(harmony, typeof(ShipItemHammer), "NailItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostNailItem));
            bool unnail = TryPatch(harmony, typeof(ShipItemHammer), "OnAltActivate", Type.EmptyTypes, postfixName: nameof(PostHammerAltActivate));
            // A caught fish is created on the client by FishingRodFish.CollectFish (returns the new item);
            // forward it so the host authors the authoritative copy (client item replication is host-only).
            bool fish = TryPatch(harmony, typeof(FishingRodFish), "CollectFish", Type.EmptyTypes, postfixName: nameof(PostCollectFish));
            // Крючок удочки: наличие = rod.health; ставится/теряется только на машине держащего
            // (OnItemClick attach / DetachHook при сходе рыбы) — форвардим результат, как nail.
            bool rodDetach = TryPatch(harmony, typeof(ShipItemFishingRod), "DetachHook", Type.EmptyTypes, postfixName: nameof(PostDetachHook));
            bool rodAttach = TryPatch(harmony, typeof(ShipItemFishingRod), "OnItemClick", new[] { typeof(PickupableItem) },
                prefixName: nameof(PreRodItemClick), postfixName: nameof(PostRodItemClick));
            bool lampHook = TryPatch(harmony, typeof(ShipItemLampHook), "OnItemClick", new[] { typeof(PickupableItem) },
                postfixName: nameof(PostLampHookItemClick));
            // Crates: mirror inventory membership (Insert/Withdraw) and relay unseal (item creation) to host.
            bool crateIn = TryPatch(harmony, typeof(CrateInventory), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCrateInsert));
            bool crateOut = TryPatch(harmony, typeof(CrateInventory), "WithdrawItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCrateWithdraw));
            bool unseal = TryPatch(harmony, typeof(ShipItemCrate), "UnsealCrate", Type.EmptyTypes, prefixName: nameof(PreUnseal));
            // Cargo load/unload uses each player's OWN wallet (local money) → vanilla runs locally; we only
            // mirror the resulting membership (like crates). Postfix on insert; withdraw captures the item
            // in a prefix (it isn't an argument) and forwards it in the postfix.
            bool cargoIn = TryPatch(harmony, typeof(CargoCarrier), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCargoInsert));
            bool cargoOut = TryPatch(harmony, typeof(CargoCarrier), "WithdrawItem", new[] { typeof(GoPointer), typeof(int) }, prefixName: nameof(PreCargoWithdraw), postfixName: nameof(PostCargoWithdraw));
            bool invIn = TryPatch(harmony, typeof(GPButtonInventorySlot), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostInventoryInsert));
            bool invOut = TryPatch(harmony, typeof(GPButtonInventorySlot), "WithdrawItem", Type.EmptyTypes, prefixName: nameof(PreInventoryWithdraw), postfixName: nameof(PostInventoryWithdraw));
            bool marketBuy = TryPatch(harmony, typeof(IslandMarket), "SpawnGood", new[] { typeof(GameObject) },
                prefixName: nameof(PreMarketSpawnGood), postfixName: nameof(PostMarketSpawnGood));
            bool marketSell = TryPatch(harmony, typeof(IslandMarketWarehouseArea), "SellGood", new[] { typeof(int) },
                prefixName: nameof(PreWarehouseSellGood));
            Plugin.Logger.LogInfo("[ItemPatches] Item patches: pickup=" + pickup + ", drop=" + drop +
                                  ", bottleClick=" + bottleClick + ", oarHeld=" + oarHeld + ", eat=" + eat +
                                  ", nail=" + nail + ", unnail=" + unnail + ", fish=" + fish +
                                  ", rodDetach=" + rodDetach + ", rodAttach=" + rodAttach + ", lampHook=" + lampHook +
                                  ", crateIn=" + crateIn + ", crateOut=" + crateOut + ", unseal=" + unseal +
                                  ", cargoIn=" + cargoIn + ", cargoOut=" + cargoOut +
                                  ", invIn=" + invIn + ", invOut=" + invOut +
                                  ", marketBuy=" + marketBuy + ", marketSell=" + marketSell);

            // Held alt-actions on ShipItem and its subclasses: a client holding the item triggers an
            // authoritative effect (hammer nail/repair, oar rowing, eat/drink). We forward these so the
            // host replays them on its copy. Patch the (GoPointer) overloads across the hierarchy so
            // subclassed handlers are caught (the no-arg variants the game also calls are left alone to
            // avoid double-forwarding).
            int held = PatchShipItem(harmony, "OnAltHeld", nameof(PostAltHeld));
            int alt = PatchShipItem(harmony, "OnAltActivate", nameof(PostAltActivate));
            Plugin.Logger.LogInfo("[ItemPatches] Held alt-actions: OnAltHeld=" + held + ", OnAltActivate=" + alt);
        }

        private static int PatchShipItem(Harmony harmony, string gameMethod, string postfixName)
        {
            var postfix = new HarmonyMethod(typeof(ItemPatches).GetMethod(
                postfixName, BindingFlags.Static | BindingFlags.NonPublic));
            var args = new[] { typeof(GoPointer) };
            int patched = 0;
            var baseType = typeof(ShipItem);
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
                try { harmony.Patch(mi, postfix: postfix); patched++; }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[ItemPatches] Failed to patch " + t.Name + "." + gameMethod + ": " + e.Message);
                }
            }
            return patched;
        }

        private static bool TryPatch(Harmony harmony, Type type, string method, Type[] args, string prefixName = null, string postfixName = null)
        {
            try
            {
                var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null) return false;
                HarmonyMethod prefix = prefixName == null ? null : new HarmonyMethod(typeof(ItemPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod postfix = postfixName == null ? null : new HarmonyMethod(typeof(ItemPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static void PostPickup(GoPointer __instance, PickupableItem item)
        {
            try { ItemSync.Instance?.NotifyPickup(__instance, item); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostPickup: " + e.Message); }
        }

        private static void PreDrop(GoPointer __instance, ref PickupableItem __state)
        {
            try
            {
                if (_fHeldItem == null)
                    _fHeldItem = typeof(GoPointer).GetField("heldItem", BindingFlags.NonPublic | BindingFlags.Instance);
                __state = _fHeldItem != null ? _fHeldItem.GetValue(__instance) as PickupableItem : null;
            }
            catch { __state = null; }
        }

        private static FieldInfo _fCurrentThrowPower;

        private static void PostDrop(GoPointer __instance, PickupableItem __state)
        {
            try { ItemSync.Instance?.NotifyDrop(__instance, __state, ComputeThrowVelocity(__instance)); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostDrop: " + e.Message); }
        }

        // Vanilla throws via ThrowItemAfterDelay, started right after DropItem(): one WaitForFixedUpdate
        // later it AddForce(forward * throwForce * f * mass) with ForceMode.Force. Δv = force/mass * dt,
        // so mass cancels: Δv = forward * throwForce * f * fixedDeltaTime, where f = min(power - delay, 1)
        // and the coroutine only runs when power > throwDelay. We replicate that velocity at drop time
        // because the deferred impulse never lands on our kinematic puppet. currentThrowPower is still set
        // here (the game zeroes it after the StartCoroutine call). Returns zero for a plain (non-T) drop.
        private static Vector3 ComputeThrowVelocity(GoPointer pointer)
        {
            try
            {
                if (pointer == null) return Vector3.zero;
                if (_fCurrentThrowPower == null)
                    _fCurrentThrowPower = typeof(GoPointer).GetField("currentThrowPower", BindingFlags.NonPublic | BindingFlags.Instance);
                float power = _fCurrentThrowPower != null ? (float)_fCurrentThrowPower.GetValue(pointer) : 0f;
                if (power <= pointer.throwDelay) return Vector3.zero;
                float f = Mathf.Min(power - pointer.throwDelay, 1f);
                return pointer.transform.forward * pointer.throwForce * f * Time.fixedDeltaTime;
            }
            catch { return Vector3.zero; }
        }

        private struct BottleClickState
        {
            public bool HasTarget;
            public float TargetAmount;
            public float TargetHealth;
            public bool HasHeld;
            public float HeldAmount;
            public float HeldHealth;
        }

        private static void PreBottleItemClick(ShipItemBottle __instance, PickupableItem __0, out BottleClickState __state)
        {
            __state = new BottleClickState();
            try
            {
                if (__instance != null)
                {
                    __state.HasTarget = true;
                    __state.TargetAmount = __instance.amount;
                    __state.TargetHealth = __instance.health;
                }

                var heldBottle = __0 as ShipItemBottle;
                if (heldBottle != null)
                {
                    __state.HasHeld = true;
                    __state.HeldAmount = heldBottle.amount;
                    __state.HeldHealth = heldBottle.health;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemPatches] PreBottleItemClick: " + e.Message);
            }
        }

        private static void PostBottleItemClick(ShipItemBottle __instance, PickupableItem __0, BottleClickState __state)
        {
            try
            {
                var sync = ItemSync.Instance;
                if (sync == null) return;

                const float eps = 0.0001f;
                var heldBottle = __0 as ShipItemBottle;
                bool targetChanged = __state.HasTarget && __instance != null &&
                    (Mathf.Abs(__instance.amount - __state.TargetAmount) > eps ||
                     Mathf.Abs(__instance.health - __state.TargetHealth) > eps);
                bool heldChanged = __state.HasHeld && heldBottle != null &&
                    (Mathf.Abs(heldBottle.amount - __state.HeldAmount) > eps ||
                     Mathf.Abs(heldBottle.health - __state.HeldHealth) > eps);

                if (targetChanged) sync.NotifyItemStateChanged(__instance, "bottle-click-target");
                if (heldChanged && !ReferenceEquals(heldBottle, __instance)) sync.NotifyItemStateChanged(heldBottle, "bottle-click-held");

                if (targetChanged || heldChanged)
                {
                    Plugin.Logger.LogInfo("[ItemPatches] BottleClick sync target=" +
                        (targetChanged ? __instance.name : "-") + " held=" +
                        (heldChanged && heldBottle != null ? heldBottle.name : "-"));
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemPatches] PostBottleItemClick: " + e.Message);
            }
        }

        private static void PostAltHeld(GoPointerButton __instance)
        {
            try
            {
                if (__instance is ShipItemOar) return; // handled by the no-arg oar hook after vanilla sets isRowing
                ItemSync.Instance?.NotifyAltHeld(__instance as ShipItem);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostAltHeld: " + e.Message); }
        }

        private static void PostOarAltHeld(ShipItemOar __instance)
        {
            try
            {
                if (!OarIsRowing(__instance)) return;
                ItemSync.Instance?.NotifyAltHeld(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostOarAltHeld: " + e.Message); }
        }

        private static bool OarIsRowing(ShipItemOar oar)
        {
            try
            {
                if (oar == null) return false;
                if (_fOarIsRowing == null)
                    _fOarIsRowing = typeof(ShipItemOar).GetField("isRowing", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fOarIsRowing != null && (bool)_fOarIsRowing.GetValue(oar);
            }
            catch { return false; }
        }

        private static void PostAltActivate(GoPointerButton __instance)
        {
            try { ItemSync.Instance?.NotifyAltActivate(__instance as ShipItem); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostAltActivate: " + e.Message); }
        }

        // Runs just before vanilla EatFood. EatFood only consumes when the eat cooldown is clear;
        // if it will consume, the item is destroyed this call, so forward the consume first (while the
        // item still resolves). Client-only inside NotifyConsume; the host eats locally via vanilla.
        private static void PreEatFood(ShipItemFood __instance)
        {
            try
            {
                if (__instance == null) return;
                if (PlayerNeeds.instance != null && PlayerNeeds.instance.eatCooldown > 0f) return; // won't eat this call
                ItemSync.Instance?.NotifyConsume(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PreEatFood: " + e.Message); }
        }

        // NailItem(item) is where a nail completes (item.nailed set true unless it bailed). Forward the
        // target's resulting nailed flag.
        private static void PostNailItem(ShipItem __0)
        {
            try { if (__0 != null) ItemSync.Instance?.OnLocalNail(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostNailItem: " + e.Message); }
        }

        // OnAltActivate toggles an already-nailed target off (instant un-nail). Forward the pointed-at
        // item's nailed flag; harmless if nothing changed (idempotent on the host).
        private static void PostHammerAltActivate(ShipItemHammer __instance)
        {
            try
            {
                var target = __instance != null && __instance.held != null ? __instance.held.GetPointedAtItem() : null;
                if (target != null) ItemSync.Instance?.OnLocalNail(target);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostHammerAltActivate: " + e.Message); }
        }

        // Рыба сорвалась (ReleaseFish) или шанс при CollectFish: держащий потерял крючок — форвардим.
        private static void PostDetachHook(ShipItemFishingRod __instance)
        {
            try { if (__instance != null) ItemSync.Instance?.OnLocalRodHook(__instance, attached: false, consumedHook: null); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostDetachHook: " + e.Message); }
        }

        // Attach крючка: ваниль в OnItemClick ставит health=1 и уничтожает крючок-предмет. Ловим
        // переход health 0→>0 (prefix запоминает старое значение) и форвардим + Consume за крючок.
        private static float _rodPreClickHealth;

        private static void PreRodItemClick(ShipItemFishingRod __instance)
        {
            try { _rodPreClickHealth = __instance != null ? __instance.health : 1f; }
            catch { _rodPreClickHealth = 1f; }
        }

        private static void PostRodItemClick(ShipItemFishingRod __instance, PickupableItem __0)
        {
            try
            {
                if (__instance == null || _rodPreClickHealth > 0f || __instance.health <= 0f) return;
                var hook = __0 != null ? __0.GetComponent<ShipItem>() : null;
                ItemSync.Instance?.OnLocalRodHook(__instance, attached: true, consumedHook: hook);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostRodItemClick: " + e.Message); }
        }

        private static void PostLampHookItemClick(ShipItemLampHook __instance, PickupableItem __0, bool __result)
        {
            try
            {
                if (!__result || __0 == null || __0.GetComponent<HangableItem>() == null) return;
                ItemSync.Instance?.NotifyLampHook(__instance, __0);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostLampHookItemClick: " + e.Message); }
        }

        // FishingRodFish.CollectFish returns the freshly-instantiated fish ShipItem. Forward it so the
        // host authors the authoritative copy (client-only inside NotifyClientAuthored).
        private static void PostCollectFish(ShipItem __result)
        {
            try { if (__result != null) ItemSync.Instance?.NotifyClientAuthored(__result); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCollectFish: " + e.Message); }
        }

        private sealed class MarketSpawnState
        {
            public int PrefabIndex;
            public Vector3 Pos;
            public HashSet<int> ExistingIds;
        }

        private static void PreMarketSpawnGood(IslandMarket __instance, GameObject goodPrefab, out MarketSpawnState __state)
        {
            __state = new MarketSpawnState
            {
                PrefabIndex = PatchPrefabIndex(goodPrefab),
                Pos = __instance != null ? __instance.transform.position : Vector3.zero,
                ExistingIds = new HashSet<int>(),
            };
            try
            {
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                {
                    int id = PatchInstanceId(item);
                    if (id > 0) __state.ExistingIds.Add(id);
                }
            }
            catch { }
        }

        private static void PostMarketSpawnGood(MarketSpawnState __state)
        {
            try
            {
                if (__state == null || __state.PrefabIndex <= 0) return;
                ShipItem best = null;
                float bestSq = 25f;
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                {
                    if (item == null || !item.sold) continue;
                    int id = PatchInstanceId(item);
                    if (id <= 0 || __state.ExistingIds.Contains(id)) continue;
                    if (PatchPrefabIndex(item.gameObject) != __state.PrefabIndex) continue;
                    var good = item.GetComponent<Good>();
                    if (good == null || good.GetMissionIndex() != -1) continue;
                    float sq = (item.transform.position - __state.Pos).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = item;
                    }
                }

                if (best != null)
                {
                    ItemSync.Instance?.NotifyClientAuthored(best);
                    Plugin.Logger.LogInfo("[ItemPatches] Market buy sync prefab=" + __state.PrefabIndex +
                                          " id=" + PatchInstanceId(best) + " '" + best.name + "'");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostMarketSpawnGood: " + e.Message); }
        }

        private static FieldInfo _fWarehouseGoodsInArea;
        private static MethodInfo _mWarehouseIsGoodValid;

        private static void PreWarehouseSellGood(IslandMarketWarehouseArea __instance, int goodIndex)
        {
            try
            {
                var good = FindWarehouseGood(__instance, goodIndex);
                if (good == null) return;
                var item = good.GetComponent<ShipItem>();
                var sv = good.GetComponent<SaveablePrefab>();
                if (item != null && sv != null)
                    ItemSync.Instance?.NotifySold(sv.instanceId, sv.prefabIndex);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PreWarehouseSellGood: " + e.Message); }
        }

        private static Good FindWarehouseGood(IslandMarketWarehouseArea area, int goodIndex)
        {
            if (area == null) return null;
            if (_fWarehouseGoodsInArea == null)
                _fWarehouseGoodsInArea = typeof(IslandMarketWarehouseArea).GetField("goodsInArea", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_mWarehouseIsGoodValid == null)
                _mWarehouseIsGoodValid = typeof(IslandMarketWarehouseArea).GetMethod("IsGoodValid", BindingFlags.NonPublic | BindingFlags.Instance);
            var goods = _fWarehouseGoodsInArea != null ? _fWarehouseGoodsInArea.GetValue(area) as System.Collections.IEnumerable : null;
            if (goods == null) return null;
            foreach (var obj in goods)
            {
                var good = obj as Good;
                if (good == null) continue;
                if (PatchGoodIndex(good) != goodIndex) continue;
                bool valid = true;
                if (_mWarehouseIsGoodValid != null)
                    valid = (bool)_mWarehouseIsGoodValid.Invoke(area, new object[] { good });
                if (valid) return good;
            }
            return null;
        }

        private static int PatchPrefabIndex(GameObject go)
        {
            var sv = go != null ? go.GetComponent<SaveablePrefab>() : null;
            return sv != null ? sv.prefabIndex : 0;
        }

        private static int PatchInstanceId(ShipItem item)
        {
            var sv = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return sv != null ? sv.instanceId : 0;
        }

        private static int PatchGoodIndex(Good good)
        {
            var sv = good != null ? good.GetComponent<SaveablePrefab>() : null;
            return sv != null ? PrefabsDirectory.ItemToGoodIndex(sv.prefabIndex) : -1;
        }

        // Crate Insert/Withdraw set the item's currentCrateId; forward the membership change (skipped while
        // we're applying a remote one — ItemSync.ApplyingCrate guard).
        private static void PostCrateInsert(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCrate) ItemSync.Instance?.OnLocalCrate(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCrateInsert: " + e.Message); }
        }

        private static void PostCrateWithdraw(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCrate) ItemSync.Instance?.OnLocalCrate(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCrateWithdraw: " + e.Message); }
        }

        // UnsealCrate authors the contained items. On the client we don't author (phantoms); forward to the
        // host and skip vanilla. Host/offline runs vanilla normally.
        private static bool PreUnseal(ShipItemCrate __instance)
        {
            try
            {
                var sync = ItemSync.Instance;
                if (sync == null) return true;
                return !sync.ForwardUnseal(__instance);   // forwarded → skip vanilla; else run it
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PreUnseal: " + e.Message); return true; }
        }

        // Cargo load/unload runs locally (own wallet); we only mirror the resulting membership.
        // After InsertItem the item's CargoPort is the carrier's port — forward it.
        private static void PostCargoInsert(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCargo) ItemSync.Instance?.OnLocalCargo(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCargoInsert: " + e.Message); }
        }

        // WithdrawItem(GoPointer, int index) doesn't take the item; capture it from carrier.cargo[index]
        // before vanilla removes it, then forward its new (out-of-carrier) membership afterwards.
        private static void PreCargoWithdraw(CargoCarrier __instance, int __1, out ShipItem __state)
        {
            __state = null;
            try
            {
                if (__instance != null && __instance.cargo != null && __1 >= 0 && __1 < __instance.cargo.Count)
                    __state = __instance.cargo[__1];
            }
            catch { __state = null; }
        }

        private static void PostCargoWithdraw(ShipItem __state)
        {
            try { if (__state != null && !ItemSync.ApplyingCargo) ItemSync.Instance?.OnLocalCargo(__state); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCargoWithdraw: " + e.Message); }
        }

        private static void PostInventoryInsert(ShipItem __0)
        {
            try { ItemSync.Instance?.OnLocalInventory(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostInventoryInsert: " + e.Message); }
        }

        private static void PreInventoryWithdraw(GPButtonInventorySlot __instance, out ShipItem __state)
        {
            __state = null;
            try { __state = __instance != null ? __instance.currentItem : null; }
            catch { __state = null; }
        }

        private static void PostInventoryWithdraw(ShipItem __state)
        {
            try { if (__state != null) ItemSync.Instance?.OnLocalInventory(__state); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostInventoryWithdraw: " + e.Message); }
        }
    }
}
