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
            public bool WasActive;   // host: was streaming this item last tick (to send a final resting pose)
        }

        private readonly CoopNet _net;
        private readonly List<ItemEntry> _items = new List<ItemEntry>();
        private readonly Dictionary<ShipItem, ItemEntry> _byItem = new Dictionary<ShipItem, ItemEntry>();
        private readonly Dictionary<int, ItemEntry> _byInstanceId = new Dictionary<int, ItemEntry>();
        private readonly Dictionary<ShipItem, uint> _localHeld = new Dictionary<ShipItem, uint>();

        private GoPointer _gp;
        private FieldInfo _fHeldItem;
        private static FieldInfo _fBoatCachedItems;
        private static FieldInfo _fShipItemCurrentBoatCollider;
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
                    last = _last + " " + age + "мс";
                }
                return _items.Count + " шт, в руках " + _localHeld.Count + " · " + last;
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

            if (_net.Role != Role.Host) return;
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
                if (irb != null && irb.enabled == puppet) irb.enabled = !puppet;
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

            SetPuppet(item, true);    // client-held items are visual only; no collision/boat push
            e.HolderNetId = _net.MyNetId;
            e.Net.Clear();
            _localHeld[item] = _net.MyNetId;
            SendRequest(e, ItemAction.Pickup, reliable: true);
            Remember("исх pickup #" + e.Index + " '" + item.name + "'");
        }

        public void NotifyDrop(GoPointer pointer, PickupableItem pickup)
        {
            if (_net.State != LinkState.Connected) return;
            var item = pickup as ShipItem;
            if (item == null) return;
            RefreshItems(force: true);
            if (!_byItem.TryGetValue(item, out var e)) return;
            _localHeld.Remove(item);

            if (_net.Role == Role.Client)
            {
                var msg = BuildRequest(e, ItemAction.Drop, _net.Clock.ServerTick);
                _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
                e.HolderNetId = _net.MyNetId;  // keep ApplyRemote off until the host sends the free state
                e.Net.Clear();
                SetPuppet(item, true);    // wait for the host's authoritative free pose
                Remember("исх drop #" + e.Index + " '" + item.name + "' " + PoseLabel(msg.Frame, msg.BoatIndex, msg.Pos));
            }
            else if (_net.Role == Role.Host)
            {
                // Host dropped: send exactly one reliable free-state so clients stop following the hand,
                // place the item at the release point and resume local physics with the throw impulse.
                e.HolderNetId = 0;
                var state = BuildState(e, _net.Clock.ServerTick);
                state.HolderNetId = 0;
                Vector3 rbVel = RealItemVelocity(item);
                if (rbVel.sqrMagnitude > 0.0001f) state.Vel = rbVel;
                _net.Broadcast(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("исх drop(host) #" + e.Index + " '" + item.name + "'");
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
                Remember("манифест по запросу");
                return;
            }

            var e = HostLookup(msg.InstanceId, msg.PrefabIndex);
            if (e == null || e.Item == null)
            {
                Remember("отказ req id=" + msg.InstanceId + " prefab=" + msg.PrefabIndex);
                return;
            }

            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0) return;

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
                    Remember("вх oar-row #" + e.Index + " actor=" + actor);
                    return;
                }

                ReplayHeldAction(e, msg.Action, actor);
                var afterAction = BuildState(e, _net.Clock.ServerTick);
                _net.Broadcast(afterAction, LiteNetLib.DeliveryMethod.Unreliable);
                Remember("вх " + msg.Action + " #" + e.Index + " actor=" + actor);
                return;
            }

            if (msg.Action == ItemAction.Pickup)
            {
                e.HolderNetId = actor;
                _localHeld[e.Item] = actor;
                // Disable the host's own item physics while a CLIENT holds it — otherwise the game's
                // ItemRigidbody keeps running and fights our hand-following positioning, which corrupts
                // the item's state and makes it vanish on drop. The item is a clean kinematic puppet
                // driven by the client's Pose stream, exactly like remote-held items on a client.
                SetPuppet(e.Item, true);
            }
            else if (msg.Action == ItemAction.Drop)
            {
                e.HolderNetId = 0;
                _localHeld.Remove(e.Item);
                // Give the host's physics back; it now simulates the fall authoritatively (lands, enters
                // the boat, settles) and streams the result — same path as the host's own drop.
                SetPuppet(e.Item, false);
            }
            else
            {
                if (msg.Action != ItemAction.State && e.HolderNetId != actor) return;   // Pose from a non-owner
                if (e.HolderNetId == actor) SetPuppet(e.Item, true);                    // keep it a clean puppet while held
            }

            bool acceptClientScalars = msg.Action == ItemAction.State;
            ApplyWirePose(e.Item, msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, msg.Vel,
                          acceptClientScalars ? msg.Amount : e.Item.amount,
                          acceptClientScalars ? msg.Health : e.Item.health,
                          e.Item.sold, e.Item.nailed, e.HolderNetId != 0);
            var state = BuildState(e, _net.Clock.ServerTick);
            // On drop, BuildState's velocity is derived from the position history and spikes because the
            // transform just teleported to the drop point — use the client's real rigidbody velocity
            // instead, or the dropped item would be flung off the ship on every receiver.
            if (msg.Action == ItemAction.Drop) state.Vel = msg.Vel;
            _net.Broadcast(state, msg.Action == ItemAction.Pose ? LiteNetLib.DeliveryMethod.Unreliable : LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("вх " + msg.Action + " #" + e.Index + " actor=" + actor + " " + PoseLabel(msg.Frame, msg.BoatIndex, msg.Pos));
        }

        public void OnItemState(ItemStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            RefreshItems(force: true);
            var e = ResolveClient(msg.InstanceId, msg.PrefabIndex, msg.Frame, msg.BoatIndex, msg.Pos,
                                  msg.Amount, msg.Health, msg.Sold, msg.Nailed, allowSpawn: msg.HolderNetId == 0);
            if (e == null || e.Item == null)
            {
                Remember("нет item id=" + msg.InstanceId + " prefab=" + msg.PrefabIndex);
                return;
            }

            uint prevHolder = e.HolderNetId;
            e.HolderNetId = msg.HolderNetId;
            ApplyScalarState(e.Item, msg.Amount, msg.Health, msg.Sold, msg.Nailed);

            if (msg.HolderNetId == _net.MyNetId)
            {
                Remember("эхо #" + e.Index);   // my own held item echoed back; the game drives it
                return;
            }

            // Free OR remote-held: feed the pose into NetTransform; ApplyRemote drives the item as a
            // kinematic puppet (game physics disabled), so it can't diverge / be ejected / vanish.
            ConfigureNetFrame(e, msg.Frame, msg.BoatIndex);
            if (prevHolder == _net.MyNetId && msg.HolderNetId == 0)
                e.Net.Clear();
            e.Net.Push(msg.Tick, msg.Pos, msg.Rot, msg.Vel);
            Remember("вх " + (msg.HolderNetId != 0 ? "held " + msg.HolderNetId : "free") + " #" + e.Index);
        }

        private void SendLocalHeldPose(float dt)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            float interval = 1f / Mathf.Max(1f, HeldPoseHz);
            _heldPoseTimer += dt;
            if (_heldPoseTimer < interval) return;
            _heldPoseTimer = 0f;

            var held = HeldItem();
            var item = held as ShipItem;
            if (item == null) return;

            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            _localHeld[item] = _net.MyNetId;
            SetPuppet(item, true);    // keep collisions disabled for the whole carry window
            SendRequest(e, ItemAction.Pose, reliable: false);
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
            // so the host launches the throw authoritatively.
            if (action == ItemAction.Drop)
            {
                Vector3 rbVel = RealItemVelocity(e.Item);
                if (rbVel.sqrMagnitude > 0.0001f) vel = rbVel;
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
            uint holder = e.Item.held != null ? _net.MyNetId : e.HolderNetId;
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
            };
        }

        private void BuildPose(ShipItem item, long tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel)
        {
            Transform boat = item.currentActualBoat != null ? item.currentActualBoat : ParentBoat(item.transform);
            if (boat == null && item.held != null)
                boat = LocalPlayerBoat();
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

            vel = Vector3.zero;
            if (_byItem.TryGetValue(item, out var e))
            {
                Vector3 current = frame == CoordFrame.World ? pos : (boat != null ? CoordSpace.LocalToReal(boat.TransformPoint(pos)) : item.transform.position);
                if (e.HaveLast)
                {
                    float secs = (tick - e.LastTick) / 1000f;
                    if (secs > 0.0001f) vel = (current - e.LastPos) / secs;
                }
                e.LastPos = current;
                e.LastTick = tick;
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
                item.gameObject.layer = 0;   // restore so the client's pointer can hit it again (fixes "can't interact after host held it")
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
                Plugin.Logger.LogWarning("[ItemSync] Не удалось привязать предмет к лодке: " + e.Message);
            }
        }

        private static void EnsureWorldParentState(ShipItem item)
        {
            if (item == null || item.currentActualBoat == null) return;
            try
            {
                if (_mShipItemExitBoat == null)
                    _mShipItemExitBoat = typeof(ShipItem).GetMethod("ExitBoat", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_mShipItemExitBoat != null)
                    _mShipItemExitBoat.Invoke(item, null);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] Не удалось вывести предмет из лодки: " + e.Message);
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
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("исх spawn id=" + e.InstanceId + " prefab=" + e.PrefabIndex);
        }

        private void BroadcastDespawn(ItemEntry e)
        {
            if (e == null) return;
            _net.Registry.Remove(e.NetId);
            _net.Broadcast(new DespawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = e.InstanceId },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("исх despawn id=" + e.InstanceId);
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
                Remember("отказ spawn id=" + msg.InstanceId);
                return;
            }
            e.HolderNetId = msg.HolderNetId;
            ApplyScalarState(e.Item, msg.Amount, msg.Health, msg.Sold, msg.Nailed);
            if (msg.HolderNetId != 0 && msg.HolderNetId != _net.MyNetId)
            {
                ConfigureNetFrame(e, msg.Frame, msg.BoatIndex);
                e.Net.Push(_net.Clock.ServerTick, msg.Pos, msg.Rot, msg.Vel);
            }
            Remember("вх spawn id=" + msg.InstanceId);
        }

        public void OnDespawnObject(DespawnObjectMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (msg.Kind != (byte)NetObjKind.Item) return;
            if (!_byInstanceId.TryGetValue(msg.InstanceId, out var e))
            {
                Remember("нет despawn id=" + msg.InstanceId);
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
            Remember("вх despawn id=" + msg.InstanceId);
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

        /// <summary>Client: a held item received a discrete OnAltActivate(GoPointer).</summary>
        public void NotifyAltActivate(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null || item.held == null) return;
            ForwardHeldAction(item, ItemAction.AltActivate, reliable: true);
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
            Remember("исх state #" + e.Index + " '" + item.name + "' " + reason +
                     " amount=" + item.amount.ToString("0.##") + " health=" + item.health.ToString("0.##"));
        }

        private void ForwardHeldAction(ShipItem item, ItemAction action, bool reliable)
        {
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            _localHeld[item] = _net.MyNetId;
            SendRequest(e, action, reliable);
            Remember("исх " + action + " #" + e.Index + " '" + item.name + "'");
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
                Plugin.Logger.LogWarning("[ItemSync] Ошибка воспроизведения " + method + " на '" +
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
                    Remember("oar-row без Rigidbody boat=" + msg.BoatIndex);
                    return true;
                }

                if (!WireToWorld(msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, out Vector3 worldPos, out Quaternion worldRot))
                {
                    Remember("oar-row без позы boat=" + msg.BoatIndex);
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
                Plugin.Logger.LogWarning("[ItemSync] Ошибка применения гребка веслом: " + ex.Message);
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
                if (kv.Value.Item != null) alive[kv.Key] = kv.Value.Item;   // Unity-null filters destroyed
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
            Remember("ремап id " + oldId + "→" + hostId + " '" + local.name + "'");
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
            Plugin.Logger.LogInfo("[ItemSync] Манифест предметов отправлен: " + n + " шт");
        }

        /// <summary>Client: ask the host for the full item set (InstanceId == 0 sentinel) once we're loaded.</summary>
        private void SendReadyPing()
        {
            if (_net.Role != Role.Client) return;
            _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Pose, InstanceId = 0, PrefabIndex = 0 },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("исх ready-ping");
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
            return saveable != null && saveable.instanceId > 0 && saveable.prefabIndex > 0 && item.sold;
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
                Plugin.Logger.LogWarning("[ItemSync] Не удалось создать предмет клиента id=" +
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
                Plugin.Logger.LogWarning("[ItemSync] Не удалось убрать cached item id=" + instanceId + ": " + e.Message);
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

        public void Clear()
        {
            _items.Clear();
            _byItem.Clear();
            _byInstanceId.Clear();
            _localHeld.Clear();
            _hostIds.Clear();
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
            Plugin.Logger.LogInfo("[ItemPatches] Патчи предметов: pickup=" + pickup + ", drop=" + drop +
                                  ", bottleClick=" + bottleClick + ", oarHeld=" + oarHeld);

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
                    Plugin.Logger.LogWarning("[ItemPatches] Не удалось пропатчить " + t.Name + "." + gameMethod + ": " + e.Message);
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

        private static void PostDrop(GoPointer __instance, PickupableItem __state)
        {
            try { ItemSync.Instance?.NotifyDrop(__instance, __state); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostDrop: " + e.Message); }
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
    }
}
