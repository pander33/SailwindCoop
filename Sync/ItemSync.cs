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
    public sealed partial class ItemSync
    {
        public static ItemSync Instance { get; private set; }
        internal bool IsClientSession => _net.Role == Role.Client && _net.State == LinkState.Connected;
        internal bool IsHostSession => _net.Role == Role.Host && _net.State == LinkState.Connected;

        /// <summary>Host: the item is in another player's hand. Its boat membership then follows the
        /// frame of that player's poses (<see cref="ApplyWirePose"/>), not the game's embark trigger:
        /// the copy's root collider is off, so the game never sees it inside the boat and would take
        /// it out of the boat two physics steps after every boat-frame pose.</summary>
        internal bool HeldByRemotePlayer(ShipItem item)
            => item != null && _localHeld.TryGetValue(item, out uint actor) && actor != 0 && actor != _net.MyNetId;

        private sealed class ItemEntry
        {
            public ushort Index;
            public int InstanceId;
            public int PrefabIndex;
            public uint NetId;
            public ShipItem Item;
            public NetTransform Net = new NetTransform { SmoothUnderruns = true };
            public List<HeldFramePose> HeldFrames;
            public PlayerPlaybackClock FreePlayback;
            /// <summary>Host: the last pose sample of the client holding this item, as it came. The
            /// host shows the item from <see cref="Net"/> (delayed like the holder's avatar), but
            /// what it sends on is this sample, or every client would get the delay twice.</summary>
            public ItemRequestMsg HeldSample;
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
            public uint Revision, LastLocalRequest, AckRequester, AckRequest;
            public InstrumentRequestEpoch InstrumentEpoch;
            public uint AuthorRequester, AuthorRequestId;
            public bool SemanticActive;
            public uint SentRevision;
            public readonly ChangeStream PoseStream = new ChangeStream();
            public ItemRequestMsg SentPose;
            public ItemStateMsg LastSemantic;
            public ItemStateMsg RemotePose;
            public ItemStateMsg AppliedRemoteState;
            public uint AppliedLocalRequest;
            public readonly ItemStateGate Gate = new ItemStateGate();
            public readonly ItemRequestOrder Requests = new ItemRequestOrder();
            public StoveFuel Fuel;
            public bool FuelResolved;
            public float LampFull;
            public bool LampResolved;
            /// <summary>Client: the last report of this item from the local hand (SendHolderReport).</summary>
            public uint HolderReport;
        }

        private struct HeldFramePose
        {
            public NetTransform Net;
            public long LastTick, ChangeTick;
        }

        /// <summary>
        /// Host, for a drop and for a report from a hand (SendHolderReport). Drying, spoiling, the
        /// heat left after the stove and the fuel of a burning lamp or log are moved by the host's
        /// world alone; a guest's copy gets them by steps (<see cref="ItemAgeing"/>) and neither a
        /// drop nor a hand report changes them. So these requests do not carry them: the host
        /// keeps its own, otherwise every sip from a pot would take its cooling a step back.
        /// A soup's spoiling is the host's as well, lowered by what left the pot
        /// (<see cref="ItemDetails.TakeWorldOwned"/>).
        /// Returns the health to apply.
        /// </summary>
        private static float KeepWorldOwned(ShipItem item, ItemDetails incoming, float health)
        {
            if (item == null || incoming == null) return health;
            try
            {
                incoming.TakeWorldOwned(ItemComponents.Capture(item));
                if (item is ShipItemLight light && LightSync.IsOn(light)) return item.health;
                var fuel = item.GetComponent<StoveFuel>();
                if (fuel != null && ItemComponents.Read<bool>(fuel, "lit")) return item.health;
            }
            catch (Exception error) { Plugin.Logger.LogWarning("[ItemSync] KeepWorldOwned: " + error.Message); }
            return health;
        }

        /// <summary>Client, for the item in the local hand that this hand reports
        /// (<see cref="KeepWorldOwned"/> is the host's side): of a host state only the values of
        /// the host's world are taken; the rest is the hand's own and newer than any echo.</summary>
        private static void ApplyWorldOwned(ShipItem item, ItemDetails state)
        {
            if (item == null || state == null) return;
            try
            {
                // The fields of ItemDetails.TakeWorldOwned, written to the game's components.
                var food = item.GetComponent<FoodState>();
                if (food != null && state.HasFood) { food.dried = state.Dried; food.spoiled = state.Spoiled; }
                var cook = item.GetComponent<CookableFood>();
                if (cook != null && state.HasCookable) ItemComponents.Set(cook, "currentHeat", state.CookHeat);
                if (item is ShipItemSoup soup && state.RecipeKind == 1) soup.currentSpoiled = state.SoupSpoiled;
            }
            catch (Exception error) { Plugin.Logger.LogWarning("[ItemSync] ApplyWorldOwned: " + error.Message); }
        }
        /// <summary>The full fuel of a lamp (private <c>ShipItemLight.initialHealth</c>), 0 for any
        /// other item or when the game no longer has the field.</summary>
        private static float LampFull(ItemEntry e)
        {
            if (e.LampResolved) return e.LampFull;
            e.LampResolved = true; e.LampFull = 0f;
            if (e.Item is ShipItemLight light)
            {
                try { e.LampFull = ItemComponents.Read<float>(light, "initialHealth"); }
                catch (Exception error) { Plugin.Logger.LogWarning("[ItemSync] ShipItemLight.initialHealth: " + error.Message); }
            }
            return e.LampFull;
        }

        /// <summary>A log burning in a stove is the stove's child with its physics switched off
        /// (vanilla InsertFuel, <see cref="ItemComponents"/>): it has no pose of its own to apply.
        /// The same test as <see cref="ItemComponents.InStove"/>; the entry only remembers whether
        /// the item is a log at all, for the paths that ask every frame.</summary>
        private static bool InStove(ItemEntry e)
        {
            if (!e.FuelResolved) { e.Fuel = e.Item.GetComponent<StoveFuel>(); e.FuelResolved = true; }
            return e.Fuel != null && ItemComponents.InStove(e.Item);
        }

        private sealed class PendingDynamicRelease
        {
            public ShipItem Item;
            public CoordFrame Frame;
            public Vector3 Vel;
            public float ReleaseAt;
            public string Reason;
        }

        private uint _nextRequest;
        private readonly HashSet<int> _tombstones = new HashSet<int>();
        private readonly ItemMembership<ShipItem> _crateMembership = new ItemMembership<ShipItem>();
        private readonly ItemMembership<ShipItem> _cargoMembership = new ItemMembership<ShipItem>();
        private readonly Dictionary<int, ItemStateMsg> _pendingStates = new Dictionary<int, ItemStateMsg>();
        private readonly List<ItemStateMsg> _pendingStateBuffer = new List<ItemStateMsg>();
        private float _nextPendingStateRetry;
        private int _pendingStateCursor;
        private readonly Dictionary<int, SpawnObjectMsg> _pendingSpawns = new Dictionary<int, SpawnObjectMsg>();
        private readonly SpawnWait _spawnWait = new SpawnWait();
        // Client: mission goods whose mission slot is not in our journal yet (item id -> slot).
        private readonly Dictionary<int, int> _pendingMissionGoods = new Dictionary<int, int>();
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
        private readonly HashSet<int> _rejectedPoseLogged = new HashSet<int>();
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
        // SendExtraState goes through the item list in ExtraSlices parts, one part per tick at
        // ExtraSlices times the rate: every item is still looked at ExtraStateHz times a second, and the
        // work and garbage of a full hold are not built in one frame.
        private const int ExtraSlices = 4;
        private int _extraCursor;
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

        private readonly ItemAuthoring<ShipItem> _pendingClientItems = new ItemAuthoring<ShipItem>();
        private readonly HashSet<ShipItem> _baselineItems = new HashSet<ShipItem>();
        private readonly ItemSaveIdentity _saveIdentity = new ItemSaveIdentity();
        private readonly OperationLedger<List<ItemEntry>> _authoredResults = new OperationLedger<List<ItemEntry>>();
        private ShipItem _authoringItem;
        private readonly Dictionary<ShipItem, KeyValuePair<uint, uint>> _authoredIdentity = new Dictionary<ShipItem, KeyValuePair<uint, uint>>();
        // Host: items just taken from the host's belt into its hand. The game empties the belt slot inside
        // GoPointer.PickUpItem (ShipItem.OnPickup -> GPButtonInventorySlot.WithdrawItem), before the
        // pointer holds the item, so the spawn the belt hook sends says nobody holds it. One reliable state
        // the next frame, when LocalHand already reads true, tells the clients whose hand it is in.
        private readonly HashSet<ShipItem> _hostHeldFromBelt = new HashSet<ShipItem>();

        public float SnapshotHz = 5f;
        public float HeldPoseHz = 15f;
        public float InterpDelayMs = 100f;
        public float ExtraStateHz = 2f;
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

        /// <summary>The item in the local hand, or null.</summary>
        internal ShipItem LocalHeldItem => HeldItem() as ShipItem;

        /// <summary>Synced items no player holds or carries in a slot: the ones that can fly
        /// (<see cref="PlayerKnockdown"/>).</summary>
        internal void CollectLoose(List<ShipItem> into)
        {
            into.Clear();
            foreach (var e in _items)
            {
                if (e.Item == null || e.HolderNetId != 0 || e.InventorySlot >= 0 || e.Item.held != null) continue;
                if (!e.Item.gameObject.activeInHierarchy) continue;
                into.Add(e.Item);
            }
        }

        internal void SetSaveBaseline(IEnumerable<SavePrefabData> saved)
        {
            _saveIdentity.Clear();
            if (saved == null) return;
            foreach (var data in saved)
                if (data != null && !(data.itemParentObject <= 0 && data.inventorySlot >= 0 && data.inventorySlot < 100))
                    _saveIdentity.Add(data.instanceId, data.prefabIndex);
        }

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            RefreshItems(dt);
            if (_net.Role == Role.Client && _baselineReady && _pendingSpawns.Count != 0)
            {
                var waiting = new List<SpawnObjectMsg>(_pendingSpawns.Values);
                float now = Time.unscaledTime;
                List<SpawnObjectMsg> reported = null;
                foreach (var spawn in waiting)
                {
                    bool due = _spawnWait.Due(spawn.InstanceId, now, out bool report);
                    if (report) (reported ?? (reported = new List<SpawnObjectMsg>())).Add(spawn);
                    if (due) OnSpawnObject(spawn, null);
                }
                if (reported != null) ReportSpawnWait(reported);
            }
            if (_net.Role == Role.Client) RetryPendingItemStates();
            if (_net.Role == Role.Client && _pendingMissionGoods.Count != 0) RetryMissionGoods();
            TickUnsealedCrate();
            if (_rowingOar != null && Time.frameCount - _lastRowFrame > 1) FlushOarStroke();
            SendLocalHeldPose(dt);
            TickInstruments(dt);
            TickOperations();
            TickPuppetsAwaitingStart();

            if (_net.Role != Role.Host) return;
            ProcessPendingDynamic();
            SendHeldFromBelt();
            SendExtraState(dt);

            // Host is the sole simulator; clients are kinematic puppets (see ApplyRemote). Stream an item
            // only while it is "active" — physically in the host's hand, or free and still moving. When it
            // settles we send one last pose and stop, so a resting item costs no bandwidth and the client
            // just holds the last boat-local pose (riding the boat). Client-held items are relayed via
            // OnItemRequest(Pose), not here.
            float interval = 1f / Mathf.Max(1f, HeldPoseHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer %= interval;

            long tick = _net.Clock.ServerTick;
            foreach (var e in _items)
            {
                if (e.Item == null || !ShouldReplicate(e.Item)) continue;
                bool hostHeld = LocalHand(e.Item);
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

        private void RetryPendingItemStates()
        {
            if (_pendingStateBuffer.Count == 0)
            {
                if (_pendingStates.Count == 0 || Time.unscaledTime < _nextPendingStateRetry) return;
                _pendingStateBuffer.AddRange(_pendingStates.Values);
            }

            // Spread retries across frames. A newer packet or despawn can supersede a queued retry.
            int end = Math.Min(_pendingStateCursor + 32, _pendingStateBuffer.Count);
            while (_pendingStateCursor < end)
            {
                var state = _pendingStateBuffer[_pendingStateCursor++];
                if (_pendingStates.TryGetValue(state.InstanceId, out var current) && current == state &&
                    TryApplyItemState(state) == ItemApplyStatus.Applied)
                    _pendingStates.Remove(state.InstanceId);
            }
            if (_pendingStateCursor < _pendingStateBuffer.Count) return;

            _pendingStateBuffer.Clear();
            _pendingStateCursor = 0;
            _nextPendingStateRetry = Time.unscaledTime + 0.1f;
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
            if (_net.Role == Role.Host) { ApplyClientHeldOnHost(); return; }
            if (_net.Role != Role.Client) return;
            if (!CoordSpace.Ready) return;
            if (Time.timeScale <= 0.0001f) return;
            ItemComponents.RetryBindings();
            long localNow = _net.Clock.LocalTick;
            long serverNow = localNow + (long)_net.Clock.OffsetMs;

            foreach (var e in _items)
            {
                if (e.Item == null) continue;
                if (e.HolderNetId == _net.MyNetId) continue;   // I hold it → the game drives it locally
                if (!e.Net.HasData) continue;
                if (InStove(e)) continue;   // rides its stove

                // Every other item (free OR held by a remote player) is a kinematic PUPPET driven purely
                // by the host's stream. We disable the game's own ItemRigidbody so it can't fight the
                // network transform (the old jitter) or drift/destroy the item; the client never
                // simulates item physics, so piles can't diverge and a placed item can't end up inside
                // another collider and vanish. The last received pose is boat-local, so a settled item
                // keeps riding the boat after the host stops streaming it.
                bool held = e.HolderNetId != 0;
                PrepareForRemotePose(e.Item, held);   // layer 2 while in a hand, else 0
                SetPuppet(e.Item, true);
                ApplyItemPose(e, localNow, serverNow);
                MoveProxyToItem(e.Item, kinematic: true, Vector3.zero);
            }
        }

        /// <summary>
        /// Host: an item in a client's hand is shown from the buffer of that client's pose stream,
        /// at the same delay as the client's avatar. Put down packet by packet it moved in 15 Hz
        /// steps and ran ahead of the avatar's hand by the interpolation delay.
        /// </summary>
        private void ApplyClientHeldOnHost()
        {
            if (!CoordSpace.Ready || Time.timeScale <= 0.0001f) return;
            long localNow = _net.Clock.LocalTick;
            long serverNow = localNow + (long)_net.Clock.OffsetMs;
            foreach (var e in _items)
            {
                if (e.Item == null || e.HeldSample == null || !e.Net.HasData) continue;
                if (e.HolderNetId == 0 || e.HolderNetId == _net.MyNetId) continue;
                if (InStove(e)) continue;
                ApplyItemPose(e, localNow, serverNow);
                MoveProxyToItem(e.Item, kinematic: true, Vector3.zero);
            }
        }

        private void ApplyItemPose(ItemEntry e, long localNow, long serverNow)
        {
            var target = e.Item.transform;
            var coop = Runtime.CoopBehaviour.Instance;
            var players = coop?.Players;
            if (e.HolderNetId != 0 && players != null &&
                players.TryGetRenderTick(e.HolderNetId, localNow, serverNow, out long heldTick))
            {
                var frames = e.HeldFrames;
                if (frames != null)
                {
                    while (frames.Count > 0 && frames[0].ChangeTick <= heldTick) frames.RemoveAt(0);
                    if (frames.Count > 0)
                    {
                        var current = frames[0];
                        var next = frames.Count > 1 ? frames[1].Net : e.Net;
                        if (!current.Net.TryApplyTransition(target, next, current.LastTick, current.ChangeTick, heldTick))
                            current.Net.ApplyAt(target, heldTick);
                        return;
                    }
                }
                e.Net.ApplyAt(target, heldTick);
                return;
            }
            var boats = coop?.Boats;
            if (e.HolderNetId == 0 && e.RemotePose?.Frame == CoordFrame.Boat && boats != null &&
                boats.TryGetRenderTick(e.RemotePose.BoatIndex, out long hullTick)) e.Net.ApplyAt(target, hullTick);
            else if (e.HolderNetId == 0 && e.FreePlayback != null)
                e.Net.ApplyAt(target, e.FreePlayback.Advance(serverNow, localNow, InterpDelayMs));
            else e.Net.Apply(target, serverNow);
        }

        private static void PreserveHeldFrame(ItemEntry entry, long lastTick, long changeTick)
        {
            entry.HeldFrames ??= new List<HeldFramePose>();
            if (entry.HeldFrames.Count == 32) entry.HeldFrames.RemoveAt(0);
            entry.HeldFrames.Add(new HeldFramePose { Net = entry.Net, LastTick = lastTick, ChangeTick = changeTick });
            entry.Net = new NetTransform { SmoothUnderruns = true, InterpDelayMs = entry.Net.InterpDelayMs };
        }

        private static void ResetItemPose(ItemEntry e)
        {
            e.Net.Clear();
            e.HeldFrames?.Clear();
            e.FreePlayback = null;
        }

        /// <summary>Host: one pose sample of the client holding the item goes into the buffer
        /// <see cref="ApplyClientHeldOnHost"/> plays. A change of frame starts the buffer again.</summary>
        private void BufferClientHeldPose(ItemEntry e, ItemRequestMsg msg)
        {
            if (ItemComponents.InStove(e.Item)) return;
            var last = e.HeldSample;
            if (last == null || !e.Net.HasData || last.Frame != msg.Frame || last.BoatIndex != msg.BoatIndex)
            {
                if (last != null && e.Net.HasData) PreserveHeldFrame(e, last.Tick, msg.Tick);
                else ResetItemPose(e);
                ConfigureNetFrame(e, msg.Frame, msg.BoatIndex);
                if (msg.Frame == CoordFrame.Boat) EnsureBoatParentState(e.Item, msg.BoatIndex);
                else EnsureWorldParentState(e.Item);
            }
            e.HeldSample = msg;
            e.Net.Push(msg.Tick, msg.Pos, msg.Rot, msg.Vel);
        }

        /// <summary>Host: forget the buffered hand of a client — any action but a pose sample ends it.</summary>
        private static void ClearClientHeldPose(ItemEntry e)
        {
            e.HeldSample = null;
            ResetItemPose(e);
        }

        private static readonly List<Collider> PuppetColliders = new List<Collider>();

        // Items that must be puppets but whose ItemRigidbody.Start has not run yet (see SetPuppet).
        private static readonly HashSet<ShipItem> _puppetAwaitingStart = new HashSet<ShipItem>();
        private static readonly List<ShipItem> _puppetStartScratch = new List<ShipItem>();

        /// <summary>Postfix of ItemRigidbody.Start: the body exists now, finish the switch before
        /// the game's item physics makes its first step.</summary>
        internal static void PuppetStarted(ItemRigidbody proxy)
        {
            if (_puppetAwaitingStart.Count == 0 || proxy == null) return;
            var item = proxy.GetShipItem();
            if (item == null || !_puppetAwaitingStart.Contains(item)) return;
            FinishPuppet(item);
        }

        private static void FinishPuppet(ShipItem item)
        {
            SetPuppet(item, true);
            // One-shot callers moved the proxy while there was no body to move.
            MoveProxyToItem(item, kinematic: true, Vector3.zero);
        }

        /// <summary>The same from Tick, for a game where the Start patch did not install.</summary>
        private static void TickPuppetsAwaitingStart()
        {
            if (_puppetAwaitingStart.Count == 0) return;
            _puppetStartScratch.Clear();
            _puppetStartScratch.AddRange(_puppetAwaitingStart);
            foreach (var item in _puppetStartScratch)
            {
                ItemRigidbody irb = null;
                try { irb = item != null ? item.GetItemRigidbody() : null; } catch { }
                if (irb == null) { _puppetAwaitingStart.Remove(item); continue; }
                if (irb.GetBody() == null) continue;
                FinishPuppet(item);
            }
            _puppetStartScratch.Clear();
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
                _puppetAwaitingStart.Remove(item);
                // ItemRigidbody.Start creates the body and the floater. Unity never runs Start on a
                // component that was disabled first, and without the body every vanilla
                // itemRigidbodyC.UpdateMass() throws: a pour then fills the bottle and loses the
                // remainder it should write back to the barrel. Leave the component enabled until
                // Start has run; PuppetStarted finishes the switch.
                if (puppet && irb != null && irb.GetBody() == null)
                {
                    if (!irb.enabled) irb.enabled = true;
                    // An inactive proxy (a log in a stove) gets its Start when it is activated.
                    _puppetAwaitingStart.Add(item);
                    return;
                }
                bool keepWallAttachDriver = puppet && item != null && item.wallAttachment && item.held != null;
                if (irb != null && keepWallAttachDriver && !irb.enabled) irb.enabled = true;
                else if (irb != null && !keepWallAttachDriver && irb.enabled == puppet) irb.enabled = !puppet;
                if (irb != null)
                {
                    irb.ToggleCollider(!puppet);
                    // Components can change without a transform hierarchy notification.
                    irb.GetComponentsInChildren(true, PuppetColliders);
                    try
                    {
                        foreach (var col in PuppetColliders)
                            if (col != null && col.enabled == puppet) col.enabled = !puppet;
                    }
                    finally { PuppetColliders.Clear(); }
                }
                var body = irb != null ? irb.GetBody() : null;
                if (body != null)
                {
                    if (body.isKinematic != puppet)
                    {
                        SetKinematic(body, puppet);
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected) return;
            var item = pickup as ShipItem;
            if (item == null) return;
            if (pointer != null && pointer.GetHeldItem() == item) InstrumentPickedUp(item);
            if (_net.Role != Role.Client) return;
            if (pointer != null) _gp = pointer;   // HeldItem() reads _gp
            RefreshItems(force: true);
            if (!_byItem.TryGetValue(item, out var e))
            {
                // Not synced (no stable id / not sold): the host keeps its copy where it lay.
                Remember("local pickup NOT tracked '" + item.name + "' id=" + InstanceIdOf(item) +
                         " prefab=" + PrefabIndexOf(item) + " sold=" + item.sold);
                return;
            }

            PrepareLocalPickupPose(pointer, item);
            // Taken off a hook that holds it only on the host: this copy has the host's attached flag
            // but no joint, and the game's OnPickup clears the flags only through a joint. Left set,
            // the drop below reads it as a wall attach and the item hangs in the air for everyone.
            ClearStaleHangFlags(item);
            SetPuppet(item, true);    // client-held items are visual only; no collision/boat push
            e.HolderNetId = _net.MyNetId;
            ResetItemPose(e);
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

        public void NotifyDrop(GoPointer pointer, PickupableItem pickup, Vector3 throwVelocity, bool surfacePlaced = false)
        {
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected) return;
            var item = pickup as ShipItem;
            if (item == null) return;
            if (_rowingOar?.Item == item) FlushOarStroke();
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

            if (_net.Role == Role.Client && !_hostIds.Contains(e.InstanceId)) return;

            _localHeld.Remove(item);
            e.InventorySlot = -1;
            if (surfacePlaced)
                e.DropWithoutProxyVelocity = true;

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
                // The game drops from inside GoPointer.LateUpdate, right after it turned the item to
                // the pointer: a mug or a bowl is then tilted by the pitch of the camera, and its
                // own ExtraLateUpdate, which holds it upright, has not run and will not (it is no
                // longer held). The game goes on from the proxy, which has the upright pose; a held
                // item here is a puppet with a stale proxy, so the pose the player saw is put back.
                if (!placedAttach && ReferenceEquals(_seenHeld, item) && _seenHeldFrame == Time.frameCount)
                    item.transform.rotation = _seenHeldRot;
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
                ResetItemPose(e);
                SetPuppet(item, true);    // wait for the host's authoritative free pose
                Remember("out drop #" + e.Index + " '" + item.name + "' " + PoseLabel(msg.Frame, msg.BoatIndex, msg.Pos));
            }
            else if (_net.Role == Role.Host)
            {
                // Host dropped: send exactly one reliable free-state so clients stop following the hand,
                // place the item at the release point and resume local physics with the throw impulse.
                e.HolderNetId = 0;
                // The host's own copy may still be a puppet from when a client carried it (a lantern
                // a client hung on a hook keeps that state while hanging): its ItemRigidbody is off,
                // so the game would never make it fall. The host's drop gives it back to the game.
                SetPuppet(item, false);
                // F-place: only the proxy is at the attach pose yet — adopt it so the broadcast carries
                // the wall pose instead of the hand pose (vanilla would sync the visual next FixedUpdate).
                bool placedAttach = IsProxyAttached(item);
                if (placedAttach) MoveItemToProxy(item);
                // The same tilt the client's drop puts back (see above): the release happens right after
                // the item was turned to the camera's pitch. The host's own game goes on from the proxy,
                // whose rotation is upright, but this one state carried the tilt, and every client
                // resumed its local physics from it — a mug the host set down fell over and spilled on
                // the clients only. Send the rotation the host is about to show.
                else UprightFromProxy(item);
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
            if (_net.Role == Role.Client)
            {
                if (msg.Action == ItemAction.AltActivate)
                {
                    RefreshItems(force: false);
                    var visual = ResolveClient(msg.InstanceId, msg.PrefabIndex, msg.Frame, msg.BoatIndex, msg.Pos,
                                               msg.Amount, msg.Health, msg.Sold, msg.Nailed, allowSpawn: false);
                    if (visual != null && visual.Item is ShipItemBroom)
                        PulseBroom(visual.Item);
                }
                return;
            }
            if (_net.Role != Role.Host) return;

            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0) return;
            // Ready-ping (InstanceId == 0): a client finished loading and asks for the full item set so
            // it can match/remap its own copies to host ids. Reply with a SpawnObject for every item.
            if (msg.InstanceId == 0)
            {
                // Charts and dirt textures are not pushed here: the client asks for each map and each
                // surface when it binds it, and pushing them as well sent every texture twice.
                RefreshItems(force: true);
                SendManifest(fromPeer);
                return;
            }

            bool pose = msg.Action == ItemAction.Pose;
            var e = pose ? HostLookup(msg.InstanceId, msg.PrefabIndex) : null;
            if (!IsCurrentItemEntry(e))
            {
                RefreshItems(force: true);
                e = HostLookup(msg.InstanceId, msg.PrefabIndex);
            }
            if (e == null || e.Item == null)
            {
                Remember("reject req id=" + msg.InstanceId + " prefab=" + msg.PrefabIndex);
                return;
            }

            bool continuous = pose || msg.Action == ItemAction.AltHeld;
            if (pose ? !e.Requests.AcceptPose(actor, msg.RequestId, msg.Tick) :
                !e.Requests.Accept(actor, msg.RequestId, continuous)) return;
            if (!continuous) ClearClientHeldPose(e);
            if (!continuous && msg.RequestId != 0) { e.AckRequester = actor; e.AckRequest = msg.RequestId; }
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
                    var crateStatus = TryApplyCrateMembership(ie.Item, msg.CrateId);
                    _net.Broadcast(BuildState(ie, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                    // Not applied (a crate this host does not know, or no physics proxy yet): nothing
                    // changed and nothing retries a request; the state above carries the acknowledgement
                    // and the item's real crate. Say so instead of reporting an insert.
                    if (crateStatus != ItemApplyStatus.Applied)
                        Remember("crate #" + ie.Index + "->" + msg.CrateId + " not applied (" + crateStatus +
                                 (msg.CrateId != 0 && CrateInventoryFor(msg.CrateId) == null ? ", unknown crate" : "") +
                                 ") actor=" + actor);
                    else
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
                    // UnsealCrate ends by opening the crate window (OpenAfterDelay) — on this machine, in
                    // front of the host's camera. The window belongs to the client that unsealed it.
                    var unsealed = crate.GetComponent<CrateInventory>();
                    if (unsealed != null)
                    {
                        if (_remoteUnsealed.Count > 0 && Time.frameCount > _remoteUnsealedLast) _remoteUnsealed.Clear();
                        _remoteUnsealedLast = Time.frameCount + RemoteUnsealFrames;
                        _remoteUnsealed[unsealed] = _remoteUnsealedLast;
                    }
                    // The seal break is the unsealing player's interface sound; on the host it played
                    // in front of the host's camera.
                    try { WithoutUiSound(UISounds.crateSealBreak, crate.UnsealCrate); }   // amount decrements synchronously; items insert next frame (coroutine)
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
                                      FoldableState.Capture(ge.Item), ge.Item.health, ge.Item.sold, ge.Item.nailed, held: true);
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
                                      FoldableState.Capture(ie.Item), ie.Item.health, ie.Item.sold, ie.Item.nailed, held: true);
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
                ReplayHeldAction(e, msg.Action, actor);
                if (msg.Action == ItemAction.AltActivate && e.Item is ShipItemBroom)
                {
                    PulseBroom(e.Item);
                    _net.RelayExcept(msg, fromPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                var afterAction = BuildState(e, _net.Clock.ServerTick);
                // AltActivate carries the acknowledgement of a pending request; AltHeld is a stream.
                _net.Broadcast(afterAction, msg.Action == ItemAction.AltActivate
                    ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
                if (msg.Action != ItemAction.AltHeld) Remember("in " + msg.Action + " #" + e.Index + " actor=" + actor);
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
                                  FoldableState.Capture(e.Item), e.Item.health, e.Item.sold, e.Item.nailed, held: false);
                    // A repeated request finds the item already hanging; that counts as hung.
                    var hangableItem = e.Item.GetComponent<HangableItem>();
                    bool hung = hangableItem.IsHanging();
                    try { if (!hung) hung = hook.OnItemClick(e.Item) && hangableItem.IsHanging(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] LampHook replay: " + ex.Message); }
                    if (!hung)
                    {
                        // The hook is occupied here (its joint is on this machine only, so the client
                        // could not see it). The item stays where the client released it, free; the
                        // state tells the client it does not hang.
                        ClearStaleHangFlags(e.Item);
                        var freeState = BuildState(e, _net.Clock.ServerTick);
                        freeState.HolderNetId = 0;
                        freeState.Attached = false;
                        _net.Broadcast(freeState, LiteNetLib.DeliveryMethod.ReliableOrdered);
                        Remember("reject lamp-hook #" + e.Index + " hook=" + msg.CrateId + " occupied, actor=" + actor);
                        return;
                    }
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
                DisconnectHangable(e.Item);
                ItemComponents.ClearCookBinding(e.Item);
                ItemComponents.ApplyInstruments(e.Item, msg.Details);
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
                e.Item.held = null;
            }
            else
            {
                if (e.HolderNetId == actor) SetPuppet(e.Item, true);                    // keep it a clean puppet while held
            }

            bool acceptClientScalars = msg.Action == ItemAction.State || msg.Action == ItemAction.Drop;
            bool worldDrop = msg.Action == ItemAction.Drop && msg.Frame == CoordFrame.World;
            bool delayedContainerDrop = msg.Action == ItemAction.Drop && msg.CargoIndex == -2;
            bool attachedDrop = msg.Action == ItemAction.Drop && msg.Attached;
            // An interactive State is an absolute result of the player's action and is taken whole.
            if (msg.Action == ItemAction.Drop || (msg.Action == ItemAction.State && !msg.IsInteraction))
                msg.Health = KeepWorldOwned(e.Item, msg.Details, msg.Health);
            // The holder's pose stream is played from a buffer (ApplyClientHeldOnHost); a pose from
            // anyone else, and every other action, is put down at once as before.
            if (msg.Action == ItemAction.Pose && e.HolderNetId == actor)
                BufferClientHeldPose(e, msg);
            else
                ApplyWirePose(e.Item, msg.Frame, msg.BoatIndex, msg.Pos, msg.Rot, msg.Vel,
                              acceptClientScalars ? msg.Amount : FoldableState.Capture(e.Item),
                              acceptClientScalars ? msg.Health : e.Item.health,
                              acceptClientScalars ? msg.Sold : e.Item.sold, acceptClientScalars ? msg.Nailed : e.Item.nailed, e.HolderNetId != 0 || worldDrop);
            if (acceptClientScalars)
            {
                ItemComponents.Apply(e.Item, msg.Details);
                if (e.Item is ShipItemLight light) LightSync.ApplyState(light, msg.LightOn, msg.Health);
            }
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
            if (msg.Action != ItemAction.Pose && state.HolderNetId == actor)
                state.PoseTick = msg.Tick;
            // On drop, BuildState's velocity is derived from the position history and spikes because the
            // transform just teleported to the drop point — use the client's real rigidbody velocity
            // instead, or the dropped item would be flung off the ship on every receiver.
            if (msg.Action == ItemAction.Drop) state.Vel = msg.Vel;
            // A holder's resting pose arrives reliably and is relayed the same way: no later packet would repair it.
            _net.Broadcast(state, msg.Action == ItemAction.Pose && _net.ReceivingUnreliable
                ? LiteNetLib.DeliveryMethod.Unreliable : LiteNetLib.DeliveryMethod.ReliableOrdered);
            if (msg.Action != ItemAction.Pose && msg.IsInteraction)
                Remember("in " + msg.Action + " #" + e.Index + " actor=" + actor + " " + PoseLabel(msg.Frame, msg.BoatIndex, msg.Pos));
        }

        public void OnItemState(ItemStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (TryApplyItemState(msg) != ItemApplyStatus.Applied) QueueState(msg);
        }
        private ItemApplyStatus TryApplyItemState(ItemStateMsg msg, bool operationResult = false)
        {
            if (_net.Role != Role.Client || _tombstones.Contains(msg.InstanceId)) return ItemApplyStatus.Applied;
            if (!_hostIds.Contains(msg.InstanceId) || !_byInstanceId.TryGetValue(msg.InstanceId, out var e) || e.Item == null)
                return WaitForItemState(msg, "item identity");
            if (e.PrefabIndex != msg.PrefabIndex) return WaitForItemState(msg, "prefab mismatch");
            var accepted = e.Gate.Copy();
            if (!accepted.Receive(msg.Revision, msg.SampleTick, msg.Requester, msg.RequestId, _net.MyNetId, out bool semantic, out bool pose))
                return ItemApplyStatus.Applied;
            bool locallyHeld = msg.HolderNetId == _net.MyNetId && HeldItem() == e.Item;
            if (!operationResult && !semantic && !locallyHeld && msg.HolderNetId != _net.MyNetId &&
                e.AppliedLocalRequest == e.LastLocalRequest && !ItemComponents.HasPendingBinding(e.Item) &&
                ItemSemanticState.Equal(e.AppliedRemoteState, msg))
            {
                if (pose) PushRemoteItemPose(e, msg);
                e.Gate.Commit(accepted);
                return ItemApplyStatus.Applied;
            }
            if (!ItemComponents.BindingsReady(e.Item, msg.Details, out var stoveTargets))
                return WaitForItemState(msg, "stove binding");
            if (!locallyHeld && (msg.CrateId != 0 || msg.CargoPort >= 0) && !MembershipTargetsReady(e.Item, msg))
                return WaitForItemState(msg, "container target");
            return e.Gate.Apply(accepted, () => {
            // The writes below can partially succeed before a container or binding fails.
            e.AppliedRemoteState = null;
            e.HolderNetId = msg.HolderNetId;
            e.InventorySlot = msg.InventorySlot;
            // Every accepted revision is complete; no individual metadata field bypasses ordering.
            // The one exception is an item this hand reports (SendHolderReport), for as long as the
            // host has nothing newer than the report: the hand has gone on since, and the host's
            // copy of it would put back what was drunk or smoked meanwhile. The values of the
            // host's world are still taken.
            bool ownReport = msg.RequestId != 0 && msg.Requester == _net.MyNetId && msg.RequestId == e.HolderReport && LocalHand(e.Item);
            if (ownReport) ApplyWorldOwned(e.Item, msg.Details);
            else
            {
                ApplyScalarState(e.Item, msg.Amount, msg.Health, msg.Sold, msg.Nailed);
                ItemComponents.Apply(e.Item, msg.Details, stoveTargets);
                if (e.Item is ShipItemLight light) LightSync.ApplyState(light, msg.LightOn, msg.Health);
            }
            if (!locallyHeld)
            {
                var crate = TryApplyCrateMembership(e.Item, msg.CrateId);
                if (crate != ItemApplyStatus.Applied) return WaitForItemState(msg, "crate apply", crate);
                var cargo = TryApplyCargoMembership(e.Item, msg.CargoPort);
                if (cargo != ItemApplyStatus.Applied) return WaitForItemState(msg, "cargo apply", cargo);
                SetProxyAttached(e.Item, msg.Attached);
                // The host refused a hook this client hung the item on (occupied there), or another
                // player took it: a joint made by this machine's own hook click must not stay.
                if (!msg.Attached && msg.HolderNetId != _net.MyNetId) DisconnectHangable(e.Item);
            }
            if (ItemComponents.HasPendingBinding(e.Item)) return WaitForItemState(msg, "binding apply");
            // The call below switches the root collider back on, which a log in a stove must not have.
            if (!InStove(e)) SetRemoteInventoryVisual(e.Item, hidden: msg.InventorySlot >= 0 && msg.HolderNetId != _net.MyNetId);
            if (!locallyHeld && pose) PushRemoteItemPose(e, msg);
            e.AppliedRemoteState = ownReport || locallyHeld ? null : msg;
            e.AppliedLocalRequest = e.LastLocalRequest;
            return ItemApplyStatus.Applied;
            });
        }

        private void PushRemoteItemPose(ItemEntry e, ItemStateMsg msg)
        {
            var previous = e.RemotePose;
            bool frameChanged = previous == null || previous.Frame != msg.Frame ||
                (msg.Frame == CoordFrame.Boat && previous.BoatIndex != msg.BoatIndex);
            bool attachmentChanged = previous == null || previous.HolderNetId != msg.HolderNetId ||
                previous.InventorySlot != msg.InventorySlot || previous.CrateId != msg.CrateId ||
                previous.CargoPort != msg.CargoPort || previous.Attached != msg.Attached ||
                previous.Nailed != msg.Nailed ||
                previous.Details?.CookStoveId != msg.Details?.CookStoveId ||
                previous.Details?.CookSlot != msg.Details?.CookSlot ||
                previous.Details?.FuelStoveId != msg.Details?.FuelStoveId ||
                previous.Details?.FuelInserted != msg.Details?.FuelInserted;
            if (!attachmentChanged && frameChanged && msg.HolderNetId != 0 && e.Net.HasData && msg.SampleTick > previous.SampleTick)
                PreserveHeldFrame(e, previous.SampleTick, msg.SampleTick);
            else if (frameChanged || attachmentChanged) ResetItemPose(e);
            if (frameChanged) ConfigureNetFrame(e, msg.Frame, msg.BoatIndex);
            if (msg.HolderNetId == 0 && msg.Frame == CoordFrame.World &&
                (e.FreePlayback != null || msg.Vel.sqrMagnitude > 0.0001f || (!frameChanged && Quaternion.Angle(previous.Rot, msg.Rot) > 0.1f)))
            {
                e.FreePlayback ??= new PlayerPlaybackClock();
                long localNow = _net.Clock.LocalTick;
                e.FreePlayback.Observe(msg.SampleTick, localNow + (long)_net.Clock.OffsetMs, localNow);
            }
            e.RemotePose = msg;
            e.Net.Push(msg.SampleTick, msg.Pos, msg.Rot, msg.Vel);
        }

#if DEBUG
        private float _nextPendingReport;
        private readonly Dictionary<string, int> _pendingAttemptsByReason = new Dictionary<string, int>();
#endif
        private ItemApplyStatus WaitForItemState(ItemStateMsg msg, string reason, ItemApplyStatus status = ItemApplyStatus.Pending)
        {
#if DEBUG
            _pendingAttemptsByReason.TryGetValue(reason, out int attempts);
            _pendingAttemptsByReason[reason] = attempts + 1;
            float now = Time.unscaledTime;
            if (now >= _nextPendingReport)
            {
                _nextPendingReport = now + 2f;
                var summary = new System.Text.StringBuilder();
                foreach (var count in _pendingAttemptsByReason)
                    summary.Append(count.Key).Append('=').Append(count.Value).Append(' ');
                Plugin.Logger.LogInfo("[ItemSync] Waiting for " + reason + " id=" + msg.InstanceId +
                    " pending=" + _pendingStates.Count + " sampleAgeMs=" + (_net.Clock.ServerTick - msg.Tick) +
                    " attempts: " + summary);
                _pendingAttemptsByReason.Clear();
            }
#endif
            return status;
        }

        private void QueueState(ItemStateMsg msg)
        {
            ItemStateMsg previous;
            if (!_pendingStates.TryGetValue(msg.InstanceId, out previous) ||
                unchecked((int)(msg.Revision - previous.Revision)) > 0 ||
                (msg.Revision == previous.Revision &&
                    (msg.SampleTick > previous.SampleTick || (msg.SampleTick == previous.SampleTick && msg.Tick > previous.Tick))))
                _pendingStates[msg.InstanceId] = msg;
        }

        public void ClearRemoteActor(uint actorNetId)
        {
            if (actorNetId == 0) return;
            ForgetWaitingOperations(actorNetId);

            int released = 0;
            foreach (var e in _items)
            {
                if (e == null || e.Item == null || e.HolderNetId != actorNetId) continue;

                e.HolderNetId = 0;
                e.InventorySlot = -1;
                if (_net.Role == Role.Host) ClearClientHeldPose(e);
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

        private ShipItem _seenHeld;
        private Quaternion _seenHeldRot;
        private int _seenHeldFrame = -1;
        private void SendLocalHeldPose(float dt)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            var held = HeldItem() as ShipItem;
            // Every frame, not at the pose rate: what the player saw last frame, after every
            // LateUpdate (see NotifyDrop).
            _seenHeld = held; _seenHeldFrame = Time.frameCount;
            if (held != null) _seenHeldRot = held.transform.rotation;
            float interval = 1f / Mathf.Max(1f, HeldPoseHz);
            _heldPoseTimer += dt;
            if (_heldPoseTimer < interval) return;
            _heldPoseTimer %= interval;

            if (held != null) StreamLocalPose(held);

            // Items the local player tucked into a personal belt slot (GPButtonInventorySlot) are no longer
            // pointer-held — vanilla parents them to a slot transform on the body. Without continuing to
            // stream their pose the host puppet would freeze at the pickup spot ("hangs in the air"). Keep
            // them riding the client's avatar by streaming the belt-local pose every tick; the host already
            // thinks the actor holds them (set at Pickup), so the Pose updates keep applying.
            // Also streams an item in hand that HeldItem() missed (it may read a different GoPointer).
            if (_localHeld.Count > 0)
            {
                _poseScratch.Clear();
                foreach (var kv in _localHeld)
                    if (kv.Value == _net.MyNetId) _poseScratch.Add(kv.Key);
                foreach (var it in _poseScratch)
                {
                    if (it == null || it == held) continue;
                    if (InPersonalInventory(it) || it.held != null) StreamLocalPose(it);
                }
            }
        }

        /// <summary>Send one unreliable Pose for a locally-carried item (hand or belt).</summary>
        private void StreamLocalPose(ShipItem item)
        {
            if (!_byItem.TryGetValue(item, out var e) || !IsCurrentItemEntry(e))
            {
                RefreshItems(force: false);
                if (!_byItem.TryGetValue(item, out e)) return;
            }
            _localHeld[item] = _net.MyNetId;
            if (!InPersonalInventory(item))
                SetPuppet(item, true);    // hand-held items stay visual/kinematic; belt slots need vanilla ItemRigidbody
            else
                RestoreLocalInventoryVisual(item, inInventory: true);
            if (!_hostIds.Contains(e.InstanceId)) return;
            // The pose is boat-local on deck: an item held still (or parked on the belt) sends nothing.
            var msg = BuildRequest(e, ItemAction.Pose, _net.Clock.ServerTick);
            bool changed = e.SentPose == null || e.SentPose.Frame != msg.Frame || e.SentPose.BoatIndex != msg.BoatIndex ||
                (e.SentPose.Pos - msg.Pos).sqrMagnitude > 1e-6f || Quaternion.Angle(e.SentPose.Rot, msg.Rot) > 0.1f;
            var mode = e.PoseStream.Next(changed);
            if (mode == StreamSend.None) return;
            e.SentPose = msg;
            _net.Broadcast(msg, mode == StreamSend.Reliable ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
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
                    _puppetAwaitingStart.Remove(item);   // a belt slot needs vanilla ItemRigidbody
                    if (!irb.enabled) irb.enabled = true;
                    irb.disableCol = false;
                    irb.ToggleCollider(!inInventory);
                    if (!inInventory)
                    {
                        item.transform.localScale = Vector3.one;
                        irb.transform.localScale = Vector3.one;
                    }
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
            if (ItemComponents.InStove(item)) return;   // a delayed drop of a log that was put into a stove meanwhile
            LogItemTransition("before free " + reason, item);
            if (frame == CoordFrame.World)
                EnsureWorldParentState(item);
            SetRemoteInventoryVisual(item, hidden: false);
            RestoreInteractableLayer(item);
            SetRootCollider(item, true);
            ClearStaleHangFlags(item);
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

        /// <summary>Host drop: take only the rotation of the proxy (the pose of the last physics step),
        /// keeping the release point. See <see cref="NotifyDrop"/>.</summary>
        private static void UprightFromProxy(ShipItem item)
        {
            try
            {
                var proxy = item != null ? item.GetItemRigidbody() : null;
                if (proxy == null || item.held != null) return;
                if (item.currentActualBoat != null && item.currentWalkCol != null)
                    item.transform.rotation = item.currentActualBoat.rotation *
                        (Quaternion.Inverse(item.currentWalkCol.rotation) * proxy.transform.rotation);
                else
                    item.transform.rotation = proxy.transform.rotation;
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
                // A log burning in a stove stays the stove's child without a collider or physics.
                if (ItemComponents.InStove(item)) return;
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
                    item.transform.localScale = Vector3.one;
                }

                if (LocalPlayerBoat() == null)
                    EnsureWorldParentState(item);
                MoveProxyToItem(item, kinematic: true, Vector3.zero);
                var irb = item.GetItemRigidbody();
                if (irb != null) irb.transform.localScale = Vector3.one;
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
                item.transform.localScale = Vector3.one;
                var irb = item.GetItemRigidbody();
                if (irb != null) irb.transform.localScale = Vector3.one;
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
            if (_net.Role == Role.Client && !_hostIds.Contains(e.InstanceId)) return;
            var msg = BuildRequest(e, action, _net.Clock.ServerTick);
            _net.Broadcast(msg, reliable ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
        }

        private ItemRequestMsg BuildRequest(ItemEntry e, ItemAction action, long tick)
        {
            if (action != ItemAction.Pose && action != ItemAction.AltHeld)
            {
                if (++_nextRequest == 0) ++_nextRequest;
                e.LastLocalRequest = _nextRequest;
                e.Gate.Begin(_nextRequest);
            }
            BuildPose(e.Item, tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel);
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
                IsInteraction = action != ItemAction.Pose && action != ItemAction.AltHeld,
                RequestId = e.LastLocalRequest,
                LightOn = e.Item is ShipItemLight light && LightSync.IsOn(light),
                Extras = Array.Empty<float>(),
                Details = ItemComponents.Capture(e.Item),
                Index = e.Index,
                InstanceId = e.InstanceId,
                PrefabIndex = e.PrefabIndex,
                Tick = tick,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                Amount = FoldableState.Capture(e.Item),
                Health = e.Item.health,
                Sold = e.Item.sold,
                Nailed = e.Item.nailed,
                CrateId = CrateIdOf(e.Item),
                CargoPort = CargoPortOf(e.Item),
                InventorySlot = PersonalInventorySlotOf(e.Item),
                Attached = IsProxyAttached(e.Item),
            };
        }

        private ItemStateMsg BuildState(ItemEntry e, long tick)
        {
            BuildPose(e.Item, tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel);
            // The host shows a client's held item delayed (ApplyClientHeldOnHost); what it sends is the
            // holder's own last sample, so the other clients do not get that delay on top of theirs.
            var held = e.HeldSample;
            if (held != null && e.HolderNetId != 0 && e.HolderNetId != _net.MyNetId)
            { frame = held.Frame; boatIndex = held.BoatIndex; pos = held.Pos; rot = held.Rot; vel = held.Vel; }
            else held = null;
            uint holder = e.HolderNetId != 0 ? e.HolderNetId : (LocalHand(e.Item) ? _net.MyNetId : 0);
            int inventorySlot = PersonalInventorySlotOf(e.Item);
            if (inventorySlot < 0) inventorySlot = e.InventorySlot;
            var state = new ItemStateMsg
            {
                Requester = e.AckRequester,
                RequestId = e.AckRequest,
                LightOn = e.Item is ShipItemLight light && LightSync.IsOn(light),
                Extras = Array.Empty<float>(),
                Details = ItemComponents.Capture(e.Item),
                Index = e.Index,
                InstanceId = e.InstanceId,
                PrefabIndex = e.PrefabIndex,
                Tick = tick,
                PoseTick = held?.Tick ?? long.MinValue,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                HolderNetId = holder,
                Amount = FoldableState.Capture(e.Item),
                Health = e.Item.health,
                Sold = e.Item.sold,
                Nailed = e.Item.nailed,
                CrateId = CrateIdOf(e.Item),
                CargoPort = CargoPortOf(e.Item),
                InventorySlot = inventorySlot,
                Attached = IsProxyAttached(e.Item),
            };
            // A new revision is what makes SendExtraState send the item. Drying, spoiling, a burning
            // log and a burning lamp move every frame, so compared exactly every such item in the
            // world was sent twice a second for the whole session.
            if (!ItemSemanticState.Settled(e.LastSemantic, state, LampFull(e)))
            {
                if (++e.Revision == 0) ++e.Revision;
                e.LastSemantic = state;
            }
            state.Revision = e.Revision;
            e.InstrumentEpoch.Observe(state.Revision, state.Requester, state.RequestId);
            return state;
        }

        private void BuildPose(ShipItem item, long tick, out CoordFrame frame, out ushort boatIndex, out Vector3 pos, out Quaternion rot, out Vector3 vel)
        {
            _byItem.TryGetValue(item, out var e);
            bool forceWorld = e != null && e.ForceWorldPoseUntilDrop;
            Transform boat = forceWorld
                ? null
                : (LocalHand(item)
                    ? LocalPlayerBoat()
                    : (item.currentActualBoat != null ? item.currentActualBoat : ParentBoat(item.transform)));
            // The frame follows the INDEX, not the transform. A boat whose index is not currently
            // resolvable (BoatLocator withholds them while the boat set is still moving) cannot be used
            // as a frame at all: the receiver addresses the frame by index, so a Boat-frame pose carrying
            // NoBoat is a boat-LOCAL coordinate with nothing to map it through. ConfigureNetFrame's
            // fallback then passed it straight into world space, and a deck-local (2, 1, -5) resolves to
            // the floating origin — i.e. every item on the boat jumps to the local player's feet.
            // Falling back to world frame instead only costs the item its rigid attachment to the deck
            // for the ~0.2 s of the settling window (it drifts by the boat's travel, then snaps back),
            // matching what PlayerSync already does with the local avatar.
            boatIndex = boat != null ? BoatLocator.IndexOf(boat) : BoatLocator.NoBoat;
            if (boatIndex != BoatLocator.NoBoat)
            {
                frame = CoordFrame.Boat;
                pos = boat.InverseTransformPoint(item.transform.position);
                rot = Quaternion.Inverse(boat.rotation) * item.transform.rotation;
            }
            else
            {
                frame = CoordFrame.World;
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
            // A late pose of a log already put into a stove would take it out of the stove again.
            if (ItemComponents.InStove(item)) { ApplyScalarState(item, amount, health, sold, nailed); return; }
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
            bool localFoldable = item is ShipItemFoldable && _net.Role == Role.Client && item.held != null;
            if (localFoldable) amount = item.amount; // Do not undo a fresh local fold with an old host echo.
            item.amount = amount;
            item.health = health;
            item.sold = sold;
            item.nailed = nailed;
            // У удочки health = наличие крючка; ваниль обновляет hookVisuals только из своих методов
            // (OnItemClick/DetachHook/OnLoad), поэтому после сетевого health зовём UpdateHook сами —
            // иначе удочка «выглядит без крючка», хотя health уже 1 (и наоборот).
            if (item is ShipItemFishingRod fr) InvokeRodUpdateHook(fr);
            if (item is ShipItemFoldable foldable && !localFoldable) FoldableState.Apply(foldable, amount);
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
        // Host: the crates it unsealed for clients, each with the last frame its window stays
        // shut. The game opens the window one frame after the unseal (OpenAfterDelay); for these
        // frames every opening of that crate is dropped, a later click of the host is not.
        private static readonly Dictionary<CrateInventory, int> _remoteUnsealed = new Dictionary<CrateInventory, int>();
        private static int _remoteUnsealedLast;
        private const int RemoteUnsealFrames = 3;
        internal static bool DropsCrateWindow(CrateInventory crate)
        {
            if (_remoteUnsealed.Count == 0 || crate == null) return false;
            return _remoteUnsealed.TryGetValue(crate, out int last) && Time.frameCount <= last;
        }

        // A crate window keeps the items it showed and puts each of them at its button every frame.
        // After another player took one out, the window still held it and dragged the item, now
        // free and solid, back to the window — into the crate, which it then threw about.
        private static void RefreshCrateWindow()
        {
            try
            {
                var ui = CrateInventoryUI.instance;
                if (ui != null && ui.showingUI && ui.currentCrate != null) ui.RefreshButtons();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] RefreshCrateWindow: " + ex.Message); }
        }

        // Client: the crate it unsealed. Vanilla is skipped there, so its window is opened here once
        // the host's contents have arrived.
        private ShipItemCrate _unsealedCrate;
        private int _unsealedCount;
        private float _unsealedDeadline;
        private const float UnsealOpenWait = 5f;
        private const float UnsealOpenReach = 3f;

        private void TickUnsealedCrate()
        {
            if (ReferenceEquals(_unsealedCrate, null)) return;
            var crate = _unsealedCrate;
            var inventory = crate != null ? crate.GetComponent<CrateInventory>() : null;
            bool late = Time.unscaledTime > _unsealedDeadline;
            if (inventory == null || (late && crate.amount > 0f)) { _unsealedCrate = null; return; }
            if (crate.amount > 0f || (!late && inventory.containedItems.Count < _unsealedCount)) return;
            _unsealedCrate = null;
            var cam = Camera.main;
            if (cam == null || crate.held != null ||
                Vector3.Distance(cam.transform.position, crate.transform.position) > UnsealOpenReach) return;
            try { inventory.OpenCrate(); }
            catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] OpenCrate after unseal: " + ex.Message); }
        }

        /// <summary>
        /// Mirror a crate membership change onto a local item: withdraw it from its old crate and/or
        /// insert it into the new one. The crate is resolved by its instanceId via the item registry
        /// (the vanilla static <c>ShipItemCrate.crates</c> dict is never populated). Insert/Withdraw is
        /// done under the anti-echo guard so the relay patches don't bounce it back.
        /// </summary>
        private void ApplyCrateMembership(ShipItem item, int crateId) => TryApplyCrateMembership(item, crateId);
        private ItemApplyStatus TryApplyCrateMembership(ShipItem item, int crateId)
        {
            if (item == null) return ItemApplyStatus.Pending;
            var sv = item.GetComponent<SaveablePrefab>();
            var target = CrateInventoryFor(crateId);
            return _crateMembership.Apply(item,
                () => sv != null && sv.currentCrateId == crateId && CrateListsMatch(item, target) && (crateId == 0 ||
                    (target != null && target.containedItems.Contains(item))),
                () => sv != null && item.itemRigidbodyC != null && (crateId == 0 || target != null),
                () => {
                    ApplyingCrate = true;
                    try
                    {
                        foreach (var entry in _items)
                        {
                            var inventory = entry.Item != null ? entry.Item.GetComponent<CrateInventory>() : null;
                                if (inventory != null && inventory != target &&
                                    (inventory.containedItems.Contains(item) || InstanceIdOf(entry.Item) == sv.currentCrateId)) inventory.WithdrawItem(item);
                        }
                        if (target != null) target.InsertItem(item);
                        else sv.currentCrateId = 0;
                    }
                    finally { ApplyingCrate = false; }
                    RefreshCrateWindow();
                }, error => Plugin.Logger.LogWarning("[ItemSync] ApplyCrateMembership id=" + InstanceIdOf(item) + " crate=" + crateId + ": " + error));
        }
        private CrateInventory CrateInventoryFor(int id)
        {
            return id != 0 && _byInstanceId.TryGetValue(id, out var entry) && entry.Item != null
                ? entry.Item.GetComponent<CrateInventory>() : null;
        }
        // Asked for every incoming item state: resolve the crates once per frame, not once per packet.
        private readonly List<CrateInventory> _crateScan = new List<CrateInventory>();
        private int _crateScanFrame = -1, _crateScanCount = -1;
        private bool CrateListsMatch(ShipItem item, CrateInventory target)
        {
            if (_crateScanFrame != Time.frameCount || _crateScanCount != _items.Count)
            {
                _crateScan.Clear(); _crateScanFrame = Time.frameCount; _crateScanCount = _items.Count;
                foreach (var entry in _items)
                {
                    var inventory = entry.Item != null ? entry.Item.GetComponent<CrateInventory>() : null;
                    if (inventory != null) _crateScan.Add(inventory);
                }
            }
            foreach (var inventory in _crateScan)
                if (inventory != null && inventory != target && inventory.containedItems.Contains(item)) return false;
            return true;
        }
        private bool MembershipTargetsReady(ShipItem item, ItemStateMsg state)
        {
            var carriers = CargoCarrier.carriers;
            return item != null && item.itemRigidbodyC != null &&
                (state.CrateId == 0 || CrateInventoryFor(state.CrateId) != null) &&
                (state.CargoPort < 0 || (carriers != null && state.CargoPort < carriers.Length && carriers[state.CargoPort] != null &&
                    carriers[state.CargoPort].cargo != null));
        }

        // Anti-echo guard for cargo membership (the Insert/Withdraw patches check it).
        internal static bool ApplyingCargo;

        /// <summary>
        /// Mirror a cargo-storage membership change without replaying the local wallet.
        /// </summary>
        private void ApplyCargoMembership(ShipItem item, int port) => TryApplyCargoMembership(item, port);
        private ItemApplyStatus TryApplyCargoMembership(ShipItem item, int port)
        {
            if (item == null) return ItemApplyStatus.Pending;
            var carriers = CargoCarrier.carriers;
            var target = port >= 0 && carriers != null && port < carriers.Length ? carriers[port] : null;
            return _cargoMembership.Apply(item,
                () => ItemComponents.Read<CargoCarrier>(item, "currentCargoCarrier") == target &&
                    CargoListsMatch(item, target) && (port < 0 || (target != null && target.cargo.Contains(item))),
                () => item.itemRigidbodyC != null && (port < 0 || (target != null && target.cargo != null)),
                () => {
                    ApplyingCargo = true;
                    try
                    {
                        int current = CargoPortOf(item);
                        if (carriers != null)
                            foreach (var carrier in carriers)
                                if (carrier != null && carrier != target) carrier.cargo.Remove(item);
                        if (current >= 0 && current != port || (port < 0 && item.itemRigidbodyC.GetCurrentInventorySlot() != null &&
                            item.itemRigidbodyC.GetCurrentInventorySlot().GetComponent<CargoCarrier>() != null))
                        {
                            item.itemRigidbodyC.ExitInventorySlot();
                            item.WithdrawFromCarrier();
                            RestoreLocalInventoryVisual(item, inInventory: false);
                        }
                        if (target != null)
                        {
                            target.cargo.Remove(item);
                            target.LoadSavedItem(item);
                        }
                    }
                    finally { ApplyingCargo = false; }
                }, error => Plugin.Logger.LogWarning("[ItemSync] ApplyCargoMembership id=" + InstanceIdOf(item) + " port=" + port + ": " + error));
        }
        private static bool CargoListsMatch(ShipItem item, CargoCarrier target)
        {
            if (CargoCarrier.carriers != null)
                foreach (var carrier in CargoCarrier.carriers)
                    if (carrier != null && carrier != target && carrier.cargo != null && carrier.cargo.Contains(item)) return false;
            return true;
        }

        private void ConfigureNetFrame(ItemEntry e, CoordFrame frame, ushort boatIndex)
        {
            if (frame == CoordFrame.Boat)
            {
                // When the index will not resolve, HOLD the item instead of passing the value through.
                // p is boat-local; returning it unchanged reinterprets it as a world coordinate and
                // teleports the item to the floating origin (right at the local player). Standing still
                // for the frames the index is unavailable is the harmless failure.
                e.Net.ToWorldPos = p =>
                {
                    Transform boat = BoatLocator.FindByIndex(boatIndex);
                    if (boat != null) return boat.TransformPoint(p);
                    return e.Item != null ? e.Item.transform.position : p;
                };
                e.Net.ToWorldRot = q =>
                {
                    Transform boat = BoatLocator.FindByIndex(boatIndex);
                    if (boat != null) return boat.rotation * q;
                    return e.Item != null ? e.Item.transform.rotation : q;
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
                item.held = item is ShipItemBroom ? VisualPointer() : null;
                if (item.gameObject.layer != 2) item.gameObject.layer = 2;   // IgnoreRaycast while in a hand
            }
            else if (item.gameObject.layer == 2)
            {
                item.held = null;
                RestoreInteractableLayer(item);   // restore so the pointer can hit it again after inventory/held states
            }
        }

        private static GoPointer _visualPointer;
        private static GoPointer VisualPointer()
        {
            try
            {
                if (_visualPointer == null)
                    _visualPointer = UnityEngine.Object.FindObjectOfType<GoPointer>();
                return _visualPointer;
            }
            catch { return null; }
        }

        private const float MaxDropSpeed = 15f;        // clamp so a bad/teleport-derived velocity can't launch an item off the ship
        private const float SettleDepenetration = 1f;  // cap overlap-resolution speed so dropping into a pile can't fling the item away

        private static AccessTools.FieldRef<UISoundPlayer, AudioSource[]> _uiSources;
        private static AccessTools.FieldRef<UISoundPlayer, AudioClip[]> _uiClips;

        /// <summary>
        /// Runs a game method that plays one interface sound the local player must not hear (it is
        /// another player's action replayed here). UISoundPlayer.PlayUISound is too small to patch
        /// safely (Mono may inline it), so the interface sources are muted for the call and the one
        /// that started the clip is stopped before they are unmuted. Never throws on the sound side.
        /// </summary>
        private static void WithoutUiSound(UISounds sound, Action action)
        {
            AudioSource[] sources = null;
            bool[] muted = null;
            AudioClip clip = null;
            try
            {
                var player = UISoundPlayer.instance;
                if (player != null)
                {
                    if (_uiSources == null) _uiSources = AccessTools.FieldRefAccess<UISoundPlayer, AudioSource[]>("audioSources");
                    if (_uiClips == null) _uiClips = AccessTools.FieldRefAccess<UISoundPlayer, AudioClip[]>("uiSounds");
                    sources = _uiSources(player);
                    AudioClip[] clips = _uiClips(player);
                    clip = clips != null && (int)sound < clips.Length ? clips[(int)sound] : null;
                    if (sources != null && clip != null)
                    {
                        muted = new bool[sources.Length];
                        for (int i = 0; i < sources.Length; i++)
                            if (sources[i] != null) { muted[i] = sources[i].mute; sources[i].mute = true; }
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemSync] UI sound mute: " + e.Message); muted = null; }

            try { action(); }
            finally
            {
                if (muted != null)
                {
                    try
                    {
                        for (int i = 0; i < sources.Length; i++)
                        {
                            var s = sources[i];
                            if (s == null) continue;
                            if (s.isPlaying && s.clip == clip && s.time < 0.1f) s.Stop();
                            s.mute = muted[i];
                        }
                    }
                    catch (Exception e) { Plugin.Logger.LogWarning("[ItemSync] UI sound unmute: " + e.Message); }
                }
            }
        }

        /// <summary>
        /// PhysX supports only speculative continuous detection on a kinematic body, and Unity logs
        /// a warning for every switch made with another mode. The game gives an item with a mesh
        /// collider ContinuousDynamic (ItemRigidbody.Start), so each puppet switch logged one; the
        /// mode goes back when the item returns to physics, or a fast throw could pass through a wall.
        /// </summary>
        private static void SetKinematic(Rigidbody body, bool kinematic)
        {
            if (kinematic)
            {
                if (body.collisionDetectionMode == CollisionDetectionMode.Continuous ||
                    body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
                    body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.isKinematic = true;
                return;
            }
            body.isKinematic = false;
            if (body.collisionDetectionMode == CollisionDetectionMode.ContinuousSpeculative &&
                body.GetComponent<MeshCollider>() != null)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

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
                SetKinematic(body, kinematic);
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
                // Vanilla's ShipItem.EnterHouse runs once, on trigger enter. An item RESTING in a house gets
                // no second enter, so undoing its membership here would be permanent: the host would then
                // save it as a loose world item and destroy it by distance instead of caching it with the
                // house. A copy in a remote hand has its root collider off (EnterRemoteHeldVisual) and gets
                // a fresh enter when the drop re-enables it, so only an enabled collider keeps the house.
                // Only the host's membership decides the save and the house cache.
                Transform house = Instance != null && Instance._net.Role == Role.Host ? RestingHouseOf(item) : null;

                if (item.currentActualBoat != null && _mShipItemExitBoat != null)
                    _mShipItemExitBoat.Invoke(item, null);

                Transform world = FloatingOriginManager.instance != null ? FloatingOriginManager.instance.transform : null;
                if (house != null)
                {
                    item.transform.parent = house;
                    var irb = item.GetItemRigidbody();
                    if (irb != null && world != null) irb.transform.parent = world;
                }
                else if (world != null)
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
                if (saveable != null && house != null)
                    saveable.SetParentObject(house.GetComponent<SaveableObject>().sceneIndex);
                else if (saveable != null && saveable.GetParentObject() != -3)
                    saveable.SetParentObject(-1);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] Failed to unparent item from boat: " + e.Message);
            }
        }

        /// <summary>The island house (game 0.39) a free item belongs to through vanilla's own trigger
        /// (<c>ShipItem.EnterHouse</c>: parent = the "House" trigger, parentObject = its sceneIndex), or null.</summary>
        private static Transform RestingHouseOf(ShipItem item)
        {
            var parent = item.transform.parent;
            if (parent == null || !parent.CompareTag("House")) return null;
            var col = item.GetComponent<Collider>();
            if (col == null || !col.enabled) return null;
            var saveable = item.GetComponent<SaveablePrefab>();
            var house = parent.GetComponent<SaveableObject>();
            return saveable != null && house != null && saveable.GetParentObject() == house.sceneIndex ? parent : null;
        }

        // -----------------------------------------------------------------
        // Lifecycle: spawn/despawn (host authoritative)
        // -----------------------------------------------------------------

        private void BroadcastSpawn(ItemEntry e, LiteNetLib.NetPeer peer = null, bool snapshot = false)
        {
            if (e == null || e.Item == null || _operationApplying || e.Item == _authoringItem) return;
            var state = BuildState(e, _net.Clock.ServerTick);
            CoordFrame frame = state.Frame; ushort boatIndex = state.BoatIndex;
            Vector3 pos = state.Pos, vel = state.Vel; Quaternion rot = state.Rot;
            var msg = new SpawnObjectMsg
            {
                AuthorRequester = e.AuthorRequester, AuthorRequestId = e.AuthorRequestId,
                IsBaselineItem = _baselineItems.Contains(e.Item),
                Kind = (byte)NetObjKind.Item,
                Revision = state.Revision, Tick = state.Tick,
                Requester = state.Requester, RequestId = state.RequestId,
                LightOn = state.LightOn, Extras = state.Extras, Details = state.Details, Attached = state.Attached,
                InstanceId = e.InstanceId,
                PrefabIndex = e.PrefabIndex,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                HolderNetId = state.HolderNetId,
                Amount = state.Amount,
                Health = e.Item.health,
                Sold = e.Item.sold,
                Nailed = e.Item.nailed,
                CrateId = CrateIdOf(e.Item),
                CargoPort = CargoPortOf(e.Item),
                InventorySlot = state.InventorySlot,
                IsSnapshot = snapshot,
                MissionIndex = MissionIndexOf(e.Item),
            };
            if (peer == null) _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            else peer.Send(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);

        }

        private void BroadcastDespawn(ItemEntry e)
        {
            if (e == null) return;
            _tombstones.Add(e.InstanceId);
            _net.Registry.Remove(e.NetId);
            if (_operationApplying) return;
            _net.Broadcast(new DespawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = e.InstanceId },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);

        }

        // A far boat or house keeps its items in a cache: waiting for those is the normal case.
        private const string WaitsInCache = "in the cache of a boat or house that is not loaded here";

        /// <summary>
        /// Host items that still have no local counterpart: one line per cause, not per item — a far
        /// boat alone can hold dozens. Only a cause other than the cache is a warning.
        /// </summary>
        private void ReportSpawnWait(List<SpawnObjectMsg> waiting)
        {
            var cached = CachedLocalItemIds();
            var causes = new Dictionary<string, List<int>>();
            foreach (var spawn in waiting)
            {
                string cause = SpawnWaitReason(spawn, cached);
                if (!causes.TryGetValue(cause, out var ids)) causes[cause] = ids = new List<int>();
                ids.Add(spawn.InstanceId);
            }
            foreach (var pair in causes)
            {
                string text = "[ItemSync] role=Client " + pair.Value.Count + " host item(s) without a local counterpart after " +
                              SpawnWait.ReportAfter + " s, still waiting: " + pair.Key + " (ids " +
                              string.Join(", ", pair.Value.GetRange(0, Math.Min(8, pair.Value.Count))) +
                              (pair.Value.Count > 8 ? ", ..." : "") + ")";
                if (pair.Key == WaitsInCache) Plugin.Logger.LogInfo(text); else Plugin.Logger.LogWarning(text);
            }
        }

        private string SpawnWaitReason(SpawnObjectMsg msg, HashSet<int> cached)
        {
            bool local = _byInstanceId.TryGetValue(msg.InstanceId, out var entry) && entry.Item != null;
            if (local && entry.PrefabIndex != msg.PrefabIndex) return "the local item with the same id has another prefab";
            if (!local && cached.Contains(msg.InstanceId)) return WaitsInCache;
            if (_saveIdentity.Contains(msg.InstanceId, msg.PrefabIndex))
                return !local ? "in the save, but neither loaded nor cached on this machine"
                     : !ItemComponents.SaveLoaded(entry.Item) ? "saved state has not finished loading"
                     : "the local copy is still being created by this player";
            if (msg.AuthorRequester == _net.MyNetId && msg.AuthorRequestId != 0) return "the item this player created is gone";
            return "could not be created";
        }

        private static HashSet<int> CachedLocalItemIds()
        {
            var ids = new HashSet<int>();
            try
            {
                if (_fBoatCachedItems == null)
                    _fBoatCachedItems = typeof(BoatLocalItems).GetField("cachedItems", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fBoatCachedItems == null) return ids;
                foreach (var localItems in UnityEngine.Object.FindObjectsOfType<BoatLocalItems>())
                {
                    var list = _fBoatCachedItems.GetValue(localItems) as List<SavePrefabData>;
                    if (list == null) continue;
                    foreach (var data in list) if (data != null) ids.Add(data.instanceId);
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemSync] Failed to read cached items: " + e.Message); }
            return ids;
        }

        public void OnSpawnObject(SpawnObjectMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client || msg.Kind != (byte)NetObjKind.Item || _tombstones.Contains(msg.InstanceId)) return;
            if (!_baselineReady) { _pendingSpawns[msg.InstanceId] = msg; return; }
            using (InteractionContext.Begin(InteractionSource.Baseline))
            {
                var e = ResolveClient(msg.InstanceId, msg.PrefabIndex, msg.Frame, msg.BoatIndex, msg.Pos,
                    msg.Amount, msg.Health, msg.Sold, msg.Nailed, allowSpawn: true,
                    authorRequestId: msg.AuthorRequester == _net.MyNetId ? msg.AuthorRequestId : 0,
                    baseline: msg.IsBaselineItem && msg.AuthorRequestId == 0);
                if (e == null || e.Item == null) { _pendingSpawns[msg.InstanceId] = msg; return; }
                // Before the state: applying it rebuilds the look text, which reads the mission.
                ApplyMissionIndex(e.Item, msg.InstanceId, msg.MissionIndex);
                OnItemState(new ItemStateMsg {
                    InstanceId = msg.InstanceId, PrefabIndex = msg.PrefabIndex,
                    Revision = msg.Revision, Tick = msg.Tick, Requester = msg.Requester, RequestId = msg.RequestId,
                    Frame = msg.Frame, BoatIndex = msg.BoatIndex, Pos = msg.Pos, Rot = msg.Rot, Vel = msg.Vel,
                    HolderNetId = msg.HolderNetId, Amount = msg.Amount, Health = msg.Health, Sold = msg.Sold,
                    Nailed = msg.Nailed, CrateId = msg.CrateId, CargoPort = msg.CargoPort,
                    InventorySlot = msg.InventorySlot, Attached = msg.Attached, LightOn = msg.LightOn, Extras = msg.Extras, Details = msg.Details
                }, fromPeer);
                ItemStateMsg waiting;
                if (_pendingStates.TryGetValue(msg.InstanceId, out waiting))
                    if (TryApplyItemState(waiting) == ItemApplyStatus.Applied) _pendingStates.Remove(msg.InstanceId);
                _pendingSpawns.Remove(msg.InstanceId);
                _spawnWait.Remove(msg.InstanceId);
            }
            // Lifecycle notices are emitted by the originating domain, never by manifests/echoes.
        }

        public void OnDespawnObject(DespawnObjectMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (msg.Kind != (byte)NetObjKind.Item) return;
            // InstanceId == 0 is the end of the host's item manifest (see SendManifest), not an item.
            if (msg.InstanceId == 0)
            {
                // Only a packet from the host ends a manifest; an operation result replays despawns
                // through this method with no peer, and a zero id there must not clear the world.
                if (fromPeer == null) return;
                try { PruneUnsharedItems(); }
                catch (Exception ex) { Plugin.Logger.LogError("[ItemSync] role=Client removing leftover items failed: " + ex); }
                return;
            }
            _tombstones.Add(msg.InstanceId);
            _pendingStates.Remove(msg.InstanceId);
            _pendingSpawns.Remove(msg.InstanceId);
            _spawnWait.Remove(msg.InstanceId);
            _pendingMissionGoods.Remove(msg.InstanceId);
            // Our own copy may already sit in a BoatLocalItems cache (we left the boat's or house's range
            // before the host did). The host brings the item back under a NEW id, so a cached entry with
            // this one would respawn as a ghost next to it.
            RemoveCachedLocalItem(msg.InstanceId);
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

                return;
            }

            if (!_byInstanceId.TryGetValue(msg.InstanceId, out var e))
            {

                return;
            }
            DestroyClientEntry(e, msg.InstanceId);
        }

        /// <summary>
        /// Client: the host finished naming its items (manifest end). The host's set is the whole shared
        /// world, so a local item it did not name is a leftover — most visibly after a join from an
        /// already loaded world, where our old copies used to stay next to the host's. Items in the belt
        /// or without a stable identity are not in <c>_items</c> and are never touched.
        /// </summary>
        private void PruneUnsharedItems()
        {
            if (!_baselineReady) return;
            RefreshItems(force: true);
            var leftovers = new List<ItemEntry>();
            foreach (var e in _items)
            {
                if (e.Item == null || _hostIds.Contains(e.InstanceId)) continue;
                // Still on its way to a host id: a spawn that could not be resolved yet, an item we are
                // authoring, or one in our hand.
                if (_pendingSpawns.ContainsKey(e.InstanceId) || _pendingStates.ContainsKey(e.InstanceId) ||
                    _localClaimed.Contains(e.InstanceId) || _pendingClientItems.Contains(e.Item) ||
                    IsOperationCreated(e.Item) || e.Item == _pendingHeldItem || e.Item.held != null ||
                    _localHeld.ContainsKey(e.Item)) continue;
                leftovers.Add(e);
            }
            foreach (var e in leftovers)
            {
                RemoveCachedLocalItem(e.InstanceId);
                // Destroy lands at the end of the frame; keep a rescan before that from finding it again.
                _destroyingItems.Add(e.Item);
                DestroyClientEntry(e, e.InstanceId);
            }
            // No host save was loaded in this session, so the world and its item caches are left over
            // from an earlier one. A cached item of a far boat or house would come back later under an
            // id the host no longer uses. After a normal join the caches hold the host's own save items
            // and are matched through _saveIdentity when they load — those must stay.
            int cached = _saveIdentity.Count == 0 ? RemoveCachedItemsNotShared() : 0;
            Plugin.Logger.LogInfo("[ItemSync] role=Client host manifest complete: " + _hostIds.Count +
                                  " host items, removed " + leftovers.Count + " local and " + cached +
                                  " cached items the host does not have");
        }

        /// <summary>Client: drop every cached far-boat/house item whose id the host has not named.</summary>
        private int RemoveCachedItemsNotShared()
        {
            int removed = 0;
            try
            {
                if (_fBoatCachedItems == null)
                    _fBoatCachedItems = typeof(BoatLocalItems).GetField("cachedItems", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fBoatCachedItems == null) return 0;
                foreach (var localItems in UnityEngine.Object.FindObjectsOfType<BoatLocalItems>())
                {
                    var list = _fBoatCachedItems.GetValue(localItems) as List<SavePrefabData>;
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] != null && _hostIds.Contains(list[i].instanceId)) continue;
                        list.RemoveAt(i);
                        removed++;
                    }
                    if (list.Count == 0) _fBoatCachedItems.SetValue(localItems, null);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] role=Client failed to clear stale cached items: " + e.Message);
            }
            return removed;
        }

        private void DestroyClientEntry(ItemEntry e, int instanceId)
        {
            var item = e.Item;
            if (item != null) ItemComponents.RemoveBindings(item);
            _net.Registry.Remove(e.NetId);
            _items.Remove(e);
            _byInstanceId.Remove(instanceId);
            if (item != null)
            {
                _byItem.Remove(item);
                _localHeld.Remove(item);
                // Like vanilla DestroyItem: a destroyed prefab left in SaveLoadManager.currentPrefabs makes
                // BoatLocalItems.CacheCurrentItems throw on it every frame once we leave its boat or house.
                try { item.GetComponent<SaveablePrefab>()?.Unregister(); }
                catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] Despawn unregister id=" + instanceId + ": " + ex.Message); }
                UnityEngine.Object.Destroy(item.gameObject);
            }

        }

        // -----------------------------------------------------------------
        // Held alt-actions (hammer/oar/eat/drink) — client forward, host replay
        // -----------------------------------------------------------------

        /// <summary>
        /// Client: the player consumed the item (ate food → it will DestroyItem locally). Tell the host
        /// to destroy its authoritative copy. Must be called BEFORE the local destroy so the entry still
        /// resolves; the eater's PlayerNeeds is personal and stays local.
        /// </summary>
        public void NotifyConsume(ShipItem item)
        {
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected || target == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(target, out var e)) return;
            if (_net.Role == Role.Client && !_hostIds.Contains(e.InstanceId)) return;
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected || rod == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(rod, out var e)) return;
            if (_net.Role == Role.Client && !_hostIds.Contains(e.InstanceId)) return;
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (ApplyingCrate || _net.State != LinkState.Connected || item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            if (_net.Role == Role.Client && !_hostIds.Contains(e.InstanceId)) return;
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
            if (InteractionContext.Suppressed) return false;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || crate == null) return false;
            var sv = crate.GetComponent<SaveablePrefab>();
            if (sv == null || sv.instanceId <= 0) return false;
            if (!_hostIds.Contains(sv.instanceId))
            {
                // The host does not know this crate under its id yet (a crate this client just
                // bought waits for its host id). A vanilla unseal here would author the contents
                // with ids of this client only, and every later crate action would name items and a
                // crate the host has never seen. Skip the unseal; it works once the id has arrived.
                Remember("hold unseal crate id=" + sv.instanceId + " (no host id yet)");
                return true;
            }
            _net.Broadcast(new ItemRequestMsg
            {
                Action = ItemAction.Unseal,
                InstanceId = sv.instanceId,
                PrefabIndex = sv.prefabIndex,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out unseal crate id=" + sv.instanceId);
            _unsealedCrate = crate;
            _unsealedCount = (int)crate.amount;
            _unsealedDeadline = Time.unscaledTime + UnsealOpenWait;
            try { UISoundPlayer.instance.PlayUISound(UISounds.crateSealBreak, 1f, 1f); } catch { }
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (ApplyingCargo || _net.State != LinkState.Connected || item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            if (_net.Role == Role.Client && !_hostIds.Contains(e.InstanceId)) return;
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected || item == null) return;
            int slot = PersonalInventorySlotOf(item);

            if (_net.Role == Role.Host)
            {
                // Host belt items are player-local too; the RefreshItems diff broadcasts despawn (in) / spawn (out).
                // Into the belt nothing more is sent: the item has left the shared set, and a state for an
                // id the clients just despawned would only wait in their pending states.
                RefreshItems(force: true);
                if (slot < 0) _hostHeldFromBelt.Add(item);
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null || item.held == null) return;
            // A crate's OnAltActivate only opens a LOCAL window (CrateSealUI / CrateInventory.OpenCrate) —
            // each player browses their own. Forwarding it would replay OnAltActivate on the host and pop
            // the window open on the HOST's screen instead of the client's. The crate's real state changes
            // (unseal, insert/withdraw) are mediated by their own relays (PreUnseal, OnLocalCrate), so the
            // UI-opening alt must stay local.
            if (item is ShipItemCrate || item is ShipItemFoldable || item is ShipItemChipLog || item is ShipItemFishingRod) return;
            ForwardHeldAction(item, ItemAction.AltActivate, reliable: true);
        }

        public void NotifyBroomActivated(ShipItemBroom broom)
        {
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected || broom == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(broom, out var e)) return;

            if (_net.Role == Role.Client)
            {
                if (broom.held == null) return;
                _localHeld[broom] = _net.MyNetId;
                SendRequest(e, ItemAction.AltActivate, reliable: true);
                Remember("out broom #" + e.Index + " '" + broom.name + "'");
                return;
            }

            if (_net.Role == Role.Host)
            {
                var msg = BuildRequest(e, ItemAction.AltActivate, _net.Clock.ServerTick);
                _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("broom(host) #" + e.Index + " '" + broom.name + "'");
            }
        }

        public void NotifyLampHook(ShipItemLampHook hook, PickupableItem heldItem)
        {
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            var item = heldItem as ShipItem;
            if (hook == null || item == null || item.GetComponent<HangableItem>() == null) return;
            RefreshItems(force: true);
            if (!_byItem.TryGetValue(item, out var e)) return;
            int hookId = InstanceIdOf(hook);
            int hookPrefab = PrefabIndexOf(hook);
            if (hookId <= 0 || hookPrefab <= 0) return;
            if (!_hostIds.Contains(e.InstanceId) || !_hostIds.Contains(hookId)) return;

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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (item == null || item.held == null) return;
            NotifyItemStateChanged(item, reason);
        }

        /// <summary>Client: vanilla changed item scalar state locally; make the host copy authoritative.</summary>
        public void NotifyItemStateChanged(ShipItem item, string reason)
        {
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.State != LinkState.Connected) return;
            if (item == null) return;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e)) return;
            if (_net.Role == Role.Host)
            {
                _net.Broadcast(BuildState(e, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("state(host) #" + e.Index + " '" + item.name + "' " + reason);
                return;
            }
            if (_net.Role != Role.Client) return;
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
            if (action != ItemAction.AltHeld) Remember("out " + action + " #" + e.Index + " '" + item.name + "'");
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
                if (ShouldReplayNoArgHeldAction(e.Item, action))
                {
                    var noArg = e.Item.GetType().GetMethod(method,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (noArg != null) noArg.Invoke(e.Item, null);
                }
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

        private static void DisconnectHangable(ShipItem item)
        {
            try
            {
                var hangable = item != null ? item.GetComponent<HangableItem>() : null;
                if (hangable != null && hangable.IsHanging())
                    hangable.DisconnectJoint();
                SetProxyAttached(item, false);
                ClearStaleHangFlags(item);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] DisconnectHangable: " + ex.Message);
            }
        }

        /// <summary>HangableItem.ConnectJoint sets the proxy's attached, disableCol and inStove; only its
        /// own DisconnectJoint clears them, and that does nothing on a copy that never had the joint (the
        /// item was hung on another machine). inStove skips the proxy's physics and disableCol keeps its
        /// collider off, so such an item, taken off the hook and dropped, froze in the air. Clear the
        /// three for a hangable that does not hang, is not in a crate and is not fuel in a stove.</summary>
        private static void ClearStaleHangFlags(ShipItem item)
        {
            try
            {
                var hangable = item != null ? item.GetComponent<HangableItem>() : null;
                if (hangable == null || hangable.IsHanging() || CrateIdOf(item) != 0 || ItemComponents.InStove(item)) return;
                var irb = item.GetItemRigidbody();
                if (irb == null || item.GetCurrentInventorySlot() >= 0) return;
                irb.attached = false;
                irb.disableCol = false;
                irb.inStove = false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[ItemSync] ClearStaleHangFlags: " + ex.Message); }
        }

        private static bool ShouldReplayNoArgHeldAction(ShipItem item, ItemAction action)
        {
            if (item == null) return false;
            // Hammer target selection depends on the local player's GoPointer aim. The actual target result
            // is synced through ItemAction.Nail, so replaying no-arg hammer logic on the host would aim at
            // the wrong thing.
            if (item is ShipItemHammer || item is ShipItemChipLog || item is ShipItemFishingRod) return false;
            if (item is ShipItemBroom) return false;
            if (item is ShipItemFoldable) return false;
            if (action == ItemAction.AltActivate && !item.sold) return false;
            // Food/elixir/bottle personal effects are handled by their own state/consume paths; do not apply
            // PlayerNeeds to the host while replaying a client's held action.
            if (item is ShipItemFood) return false;
            return true;
        }

        private void PulseBroom(ShipItem item)
        {
            try
            {
                var cleaner = item != null ? item.GetComponentInChildren<Cleaner>(true) : null;
                if (cleaner != null)
                {
                    if (item.held == null)
                        item.held = _net.Role == Role.Host ? HostPointer() : VisualPointer();
                    cleaner.activated = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] PulseBroom: " + ex.Message);
            }
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

        /// <summary>Host: the reliable state of an item taken from the host's belt (see
        /// <see cref="_hostHeldFromBelt"/>). Sent only once the hand holds it, from the entry as it is:
        /// BuildState reads the holder from the hand, so nothing is overridden and the holder is not kept
        /// past the drop.</summary>
        private void SendHeldFromBelt()
        {
            if (_hostHeldFromBelt.Count == 0) return;
            // One look per item: the belt hook runs inside PickUpItem, so by this tick the hand holds it.
            // An item that is not in the hand by then was not taken to it, and nothing is sent.
            foreach (var item in new List<ShipItem>(_hostHeldFromBelt))
            {
                _hostHeldFromBelt.Remove(item);
                if (item == null || !LocalHand(item) || !_byItem.TryGetValue(item, out var e)) continue;
                _net.Broadcast(BuildState(e, _net.Clock.ServerTick), LiteNetLib.DeliveryMethod.ReliableOrdered);
                Remember("out held from belt #" + e.Index + " '" + item.name + "'");
            }
        }

        private void SendExtraState(float dt)
        {
            _extraTimer += dt;
            float interval = 1f / (Mathf.Max(0.5f, ExtraStateHz) * ExtraSlices);
            if (_extraTimer < interval) return;
            _extraTimer %= interval;
            int count = _items.Count;
            if (count == 0) { _extraCursor = 0; return; }
            // The list is rebuilt by RefreshItems; the cursor carries on (an item moved by the rebuild is
            // at worst looked at one part late or early), so items at the end are never starved.
            if (_extraCursor >= count) _extraCursor = 0;
            int end = Mathf.Min(count, _extraCursor + (count + ExtraSlices - 1) / ExtraSlices);
            int from = _extraCursor;
            _extraCursor = end >= count ? 0 : end;
            // BuildState advances the revision exactly when the item's semantic state differs from the
            // last one built. Unchanged items send nothing; a run of changes ends with one reliable state.
            for (int i = from; i < end; i++)
            {
                var e = _items[i];
                if (e.Item == null) continue;
                // Other callers of BuildState (operation capture, pose stream) advance the revision too,
                // so compare with the revision this pass last sent rather than with the value before the call.
                var state = BuildState(e, _net.Clock.ServerTick);
                if (e.Revision != e.SentRevision) { e.SentRevision = e.Revision; _net.Broadcast(state, LiteNetLib.DeliveryMethod.Unreliable); e.SemanticActive = true; }
                else if (e.SemanticActive) { _net.Broadcast(state, LiteNetLib.DeliveryMethod.ReliableOrdered); e.SemanticActive = false; }
            }
        }
        // The former positional-only ItemExtra packet has no ordering; active senders use full ItemState.
        public void OnItemExtraState(ItemExtraStateMsg msg, LiteNetLib.NetPeer fromPeer) { }

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

        /// <summary>Collections of one <see cref="RefreshItems(bool)"/> pass, kept between passes so a pass
        /// allocates nothing new. A nested pass (a broadcast inside the pass calling back) gets its own.</summary>
        private sealed class RefreshBuffers
        {
            public readonly Dictionary<int, ItemEntry> Prev = new Dictionary<int, ItemEntry>();
            public readonly Dictionary<int, ShipItem> Alive = new Dictionary<int, ShipItem>();
            public readonly List<OrderedItem> Ordered = new List<OrderedItem>();
            public readonly List<ItemEntry> NewlyAdded = new List<ItemEntry>();
            public readonly List<ShipItem> Scene = new List<ShipItem>();

            public void Clear() { Prev.Clear(); Alive.Clear(); Ordered.Clear(); NewlyAdded.Clear(); Scene.Clear(); }
        }

        /// <summary>An item of the pass with its save identity read once.</summary>
        private struct OrderedItem
        {
            public ShipItem Item;
            public int InstanceId;
            public int PrefabIndex;
        }

        private readonly RefreshBuffers _refreshBuffers = new RefreshBuffers();
        private int _refreshDepth;

        private void RefreshItems(bool force)
        {
            var buffers = _refreshDepth == 0 ? _refreshBuffers : new RefreshBuffers();
            buffers.Clear();
            _refreshDepth++;
            try { RefreshItems(buffers); }
            finally { _refreshDepth--; buffers.Clear(); }
        }

        private void RefreshItems(RefreshBuffers buffers)
        {
            // Previously-known entries (carry live ShipItem refs; a destroyed item reads Unity-null).
            var prev = buffers.Prev;
            foreach (var kv in _byInstanceId) prev.Add(kv.Key, kv.Value);
            _pendingClientItems.Prune(item => item != null);

            // Alive set = previously-known still-alive (covers items that went temporarily inactive,
            // e.g. inside a crate, which FindObjectsOfType skips) + everything found this scan.
            var alive = buffers.Alive;
            foreach (var kv in prev)
                if (kv.Value.Item != null && !_destroyingItems.Contains(kv.Value.Item) && HasStableIdentity(kv.Value.Item)) alive[kv.Key] = kv.Value.Item;   // Unity-null filters destroyed
            ShipItemRegistry.Snapshot(buffers.Scene);
            foreach (var item in buffers.Scene)
            {
                if (item == null || _destroyingItems.Contains(item) || !HasStableIdentity(item)) continue;
                int id = InstanceIdOf(item);
                if (_net.Role == Role.Host && (_tombstones.Contains(id) ||
                    (alive.TryGetValue(id, out var other) && other != item)))
                {
                    var saveable = item.GetComponent<SaveablePrefab>();
                    do { id = UnityEngine.Random.Range(1, int.MaxValue); }
                    while (_tombstones.Contains(id) || alive.ContainsKey(id) || prev.ContainsKey(id));
                    saveable.instanceId = id;
                    // A new id is a new shared item (it came back from the host's belt): the client
                    // that once authored this object no longer waits for it, and a spawn still naming
                    // that client's request would never be created there.
                    _authoredIdentity.Remove(item);
                }
                alive[id] = item;
            }

            // Establish the baseline once the local item set stops growing (save load finished).
            // Before that we never broadcast spawn/despawn or create copies — both peers are still
            // loading the same save and any "new" item is just a late-loaded shared one.
            if (alive.Count != _baselineCount)
            {
                _baselineCount = alive.Count;
                _baselineChangedAt = Time.unscaledTime;
            }
            if (!_baselineReady && (!GameState.playing || GameState.currentlyLoading)) _baselineChangedAt = Time.unscaledTime;
            if (!_baselineReady && ItemBaseline.Ready(GameState.playing, GameState.currentlyLoading,
                _baselineCount, Time.unscaledTime - _baselineChangedAt, SettleSeconds))
            {
                _baselineReady = true;
                foreach (var item in alive.Values)
                    if (!_pendingClientItems.Contains(item) && !IsOperationCreated(item)) _baselineItems.Add(item);
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

            // Ordered by instance id: the index is the item's address in messages. The ids are read
            // once per item here, not on every comparison.
            var ordered = buffers.Ordered;
            foreach (var item in alive.Values)
            {
                var s = item.GetComponent<SaveablePrefab>();
                ordered.Add(new OrderedItem { Item = item, InstanceId = s != null ? s.instanceId : 0, PrefabIndex = s != null ? s.prefabIndex : 0 });
            }
            ordered.Sort(CompareOrdered);

            _items.Clear();
            _byItem.Clear();
            _byInstanceId.Clear();

            var newlyAdded = buffers.NewlyAdded;
            for (int i = 0; i < ordered.Count && i <= ushort.MaxValue; i++)
            {
                var item = ordered[i].Item;
                int instanceId = ordered[i].InstanceId;
                int prefabIndex = ordered[i].PrefabIndex;
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
                if (e.Item != item)
                {
                    e.FuelResolved = false;
                    e.LampResolved = false;
                    e.AppliedRemoteState = null;
                    e.RemotePose = null;
                    ResetItemPose(e);
                }
                e.Item = item;
                if (_net.Role == Role.Host && _authoredIdentity.TryGetValue(item, out var author))
                { e.AuthorRequester = author.Key; e.AuthorRequestId = author.Value; }
                e.Net.InterpDelayMs = InterpDelayMs;
                _items.Add(e);
                _byItem[item] = e;
                _byInstanceId[instanceId] = e;
                _net.Registry.RegisterFixed(e.NetId, NetObjKind.Item, NetRegistry.HostAuthority, item);
                if (isNew)
                {
                    if (_net.Role == Role.Host && _baselineReady) newlyAdded.Add(e);
                    if (_net.Role == Role.Client) _nextPendingStateRetry = 0f;
                }
            }

            // Host: items that appeared after load (caught fish, cooked food, crate contents) -> spawn.
            foreach (var e in newlyAdded) BroadcastSpawn(e);
        }

        /// <summary>Client: host id of an item the local player carries.</summary>
        public bool TryGetLocallyHeldKey(ShipItem item, out int instanceId, out int prefabIndex)
        {
            instanceId = 0;
            prefabIndex = 0;
            if (_net.Role != Role.Client || item == null) return false;
            RefreshItems(force: false);
            if (!_byItem.TryGetValue(item, out var e) || e.HolderNetId != _net.MyNetId) return false;
            if (!_hostIds.Contains(e.InstanceId)) return false;
            instanceId = e.InstanceId;
            prefabIndex = e.PrefabIndex;
            return true;
        }

        /// <summary>Host: find own item by id, null if gone.</summary>
        public ShipItem HostFindItem(int instanceId, int prefabIndex)
        {
            if (_net.Role != Role.Host) return null;
            RefreshItems(force: true);
            var e = HostLookup(instanceId, prefabIndex);
            return e != null ? e.Item : null;
        }
        internal bool TrySharedIdentity(ShipItem item, out int id, out int prefab)
        {
            id = prefab = 0;
            if (!_byItem.TryGetValue(item, out var entry)) return false;
            if (_net.Role == Role.Client && !_hostIds.Contains(entry.InstanceId)) return false;
            id = entry.InstanceId; prefab = entry.PrefabIndex; return true;
        }
        internal IEnumerable<MapChart> SharedMaps()
        {
            foreach (var entry in _items)
                if (entry.Item is ShipItemFoldable foldable && foldable.mapChart != null &&
                    (_net.Role == Role.Host || _hostIds.Contains(entry.InstanceId))) yield return foldable.mapChart;
        }

        /// <summary>Host: rescan on the next Tick (Destroy is deferred to end of frame).</summary>
        public void HostRefreshNextTick()
        {
            if (_net.Role == Role.Host) _refreshTimer = 2f;
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
            foreach (var item in ShipItemRegistry.Active())
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
                                         bool allowSpawn, uint authorRequestId = 0, bool baseline = false)
        {
            if (instanceId <= 0 || prefabIndex <= 0 || _tombstones.Contains(instanceId)) return null;
            bool shared = _hostIds.Contains(instanceId);
            if (shared && _byInstanceId.TryGetValue(instanceId, out var known))
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

            if (_saveIdentity.Contains(instanceId, prefabIndex))
            {
                if (!_byInstanceId.TryGetValue(instanceId, out var saved) || saved.PrefabIndex != prefabIndex ||
                    saved.Item == null || _pendingClientItems.Contains(saved.Item) || IsOperationCreated(saved.Item)) return null;
                if (!ItemComponents.SaveLoaded(saved.Item)) return null;
                _baselineItems.Remove(saved.Item);
                _hostIds.Add(instanceId);
                return saved;
            }

            var pending = _pendingClientItems.Resolve(_net.MyNetId, _net.MyNetId, authorRequestId, prefabIndex, instanceId);
            if (pending != null)
            {
                RemapLocalItem(pending, instanceId);
                _hostIds.Add(instanceId);
                RefreshItems(force: true);
                _byInstanceId.TryGetValue(instanceId, out var fr);
                if (fr != null && fr.Item == pending) _pendingClientItems.Bind(authorRequestId, instanceId);
                // If this item was just withdrawn from our belt to hand, tell the host we hold it now.
                if (pending == _pendingHeldItem || LocalHand(pending))
                {
                    _pendingHeldItem = null;
                    if (fr != null && fr.Item != null)
                    {
                        fr.HolderNetId = _net.MyNetId;
                        _localHeld[fr.Item] = _net.MyNetId;
                        SetPuppet(fr.Item, true);
                        SendRequest(fr, ItemAction.Pickup, reliable: true);
                    }
                }
                return fr;
            }

            // Our own request: wait for the item it created, but only while that request and its item
            // still exist here. A request we no longer hold (an older author mark) or whose item is
            // gone leaves an ordinary host item, created below like any other.
            if (authorRequestId != 0 && _pendingClientItems.Awaits(authorRequestId, item => item != null)) return null;

            // A local item already carries this host id and prefab: we joined from a loaded world (a
            // rejoin without going through the main menu), where our copies kept the ids the host gave
            // them last time. Take it as the host's item instead of spawning a second one beside it.
            if (_byInstanceId.TryGetValue(instanceId, out var same) && same.PrefabIndex == prefabIndex &&
                same.Item != null && !_pendingClientItems.Contains(same.Item) && !IsOperationCreated(same.Item))
            {
                _baselineItems.Remove(same.Item);
                _hostIds.Add(instanceId);
                return same;
            }

            // No local match. Only spawn for FREE items (allowSpawn): a held item's pose is the holder's
            // hand, so position matching can't work — defer; once it's dropped we match/spawn at rest.
            if (!allowSpawn) return null;

            // The host's copy is the one we create now; a copy of the same id waiting in a far boat's
            // or house's cache would otherwise load beside it later.
            RemoveCachedLocalItem(instanceId);
            var spawned = SpawnClientItem(instanceId, prefabIndex, frame, boatIndex, wirePos,
                                          Quaternion.identity, amount, health, sold, nailed);
            if (spawned == null) return null;
            _hostIds.Add(instanceId);
            RefreshItems(force: true);
            return _byInstanceId.TryGetValue(instanceId, out var e) && e.PrefabIndex == prefabIndex ? e : null;
        }

        /// <summary>Reassign a live local item's instanceId to the host's, keeping engine dedup/caches sane.</summary>
        private void RemapLocalItem(ShipItem local, int hostId)
        {
            var saveable = local != null ? local.GetComponent<SaveablePrefab>() : null;
            if (saveable == null) return;
            ReleaseUnsharedCollision(hostId, local);
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
            RetargetCrateContents(local, oldId, hostId);

            if (_byInstanceId.TryGetValue(oldId, out var old))
            {
                _byInstanceId.Remove(oldId);
                if (old.Item != null) _byItem.Remove(old.Item);
                _net.Registry.Remove(old.NetId);
            }
        }

        /// <summary>Contents name their crate by its instanceId (<c>SaveablePrefab.currentCrateId</c>).
        /// When a crate takes the host's id, items already inside still carry the old one, and a
        /// crate action from this client would then name a crate the host does not have.</summary>
        private void RetargetCrateContents(ShipItem crate, int oldId, int newId)
        {
            if (oldId == 0 || oldId == newId) return;
            var inventory = crate != null ? crate.GetComponent<CrateInventory>() : null;
            if (inventory == null) return;
            try
            {
                foreach (var entry in _items)
                {
                    var sv = entry.Item != null ? entry.Item.GetComponent<SaveablePrefab>() : null;
                    if (sv != null && sv.currentCrateId == oldId) sv.currentCrateId = newId;
                }
                if (inventory.containedItems != null)
                    foreach (var item in inventory.containedItems)
                    {
                        var sv = item != null ? item.GetComponent<SaveablePrefab>() : null;
                        if (sv != null && sv.currentCrateId == oldId) sv.currentCrateId = newId;
                    }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemSync] RetargetCrateContents: " + e.Message); }
        }

        private void ReleaseUnsharedCollision(int hostId, ShipItem target)
        {
            if (_hostIds.Contains(hostId) || !_byInstanceId.TryGetValue(hostId, out var collision) || collision.Item == target) return;
            int replacement;
            do { replacement = UnityEngine.Random.Range(1, int.MaxValue); }
            while (_hostIds.Contains(replacement) || _byInstanceId.ContainsKey(replacement) || _tombstones.Contains(replacement));
            RemapLocalItem(collision.Item, replacement);
            RefreshItems(force: true);
        }

        /// <summary>Host: send a SpawnObject for every replicated item so a freshly-ready client can match/spawn.</summary>
        private void SendManifest(LiteNetLib.NetPeer peer)
        {
            if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(peer) == 0) return;
            int n = 0;
            foreach (var e in _items)
            {
                if (e.Item == null || !ShouldReplicate(e.Item)) continue;
                BroadcastSpawn(e, peer, snapshot: true);
                n++;
            }
            // Manifest end (InstanceId == 0, same sentinel idea as the ready ping): the client now knows
            // the whole host set and drops local items that are not in it.
            peer.Send(new DespawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = 0 },
                      LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[ItemSync] Item manifest sent: " + n + " items");
        }

        /// <summary>Client: ask the host for the full item set (InstanceId == 0 sentinel) once we're loaded.</summary>
        private void SendReadyPing()
        {
            if (_net.Role != Role.Client) return;
            _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Pose, InstanceId = 0, PrefabIndex = 0 },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private static bool ShouldReplicate(ShipItem item)
        {
            if (item == null) return false;
            return HasStableIdentity(item);
        }

        private bool IsCurrentItemEntry(ItemEntry entry)
        {
            if (entry == null || entry.Item == null || _destroyingItems.Contains(entry.Item)) return false;
            var saveable = entry.Item.GetComponent<SaveablePrefab>();
            return HasStableIdentity(entry.Item, saveable) &&
                saveable.instanceId == entry.InstanceId && saveable.prefabIndex == entry.PrefabIndex;
        }

        private static bool HasStableIdentity(ShipItem item)
            => item != null && HasStableIdentity(item, item.GetComponent<SaveablePrefab>());

        private static bool HasStableIdentity(ShipItem item, SaveablePrefab saveable)
        {
            // Personal belt items (slots 0..4) are PLAYER-LOCAL: each player owns its own belt, persisted via
            // CoopProfile. They are excluded from the shared/host-authoritative item set so they never leak
            // across peers. The host's RefreshItems diff turns the belt-in/out transitions into despawn/spawn.
            return saveable != null && saveable.instanceId > 0 && saveable.prefabIndex > 0 && item.sold
                   && !InPersonalInventory(item);
        }

        private static int CompareOrdered(OrderedItem a, OrderedItem b)
        {
            int ai = a.InstanceId;
            int bi = b.InstanceId;
            if (ai != 0 && bi != 0 && ai != bi) return ai.CompareTo(bi);
            if (ai != bi) return bi.CompareTo(ai);
            return string.CompareOrdinal(BoatLocator.PathOf(a.Item.transform), BoatLocator.PathOf(b.Item.transform));
        }

        internal ShipItemStove FindTrackedStove(int id)
        {
            if (_byInstanceId.TryGetValue(id, out var entry) && entry.Item is ShipItemStove stove &&
                stove != null && stove.gameObject.activeInHierarchy && InstanceIdOf(stove) == id)
                return stove;
            return null;
        }

        private static int InstanceIdOf(ShipItem item)
        {
            var s = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return s != null ? s.instanceId : 0;
        }

        private static int MissionIndexOf(ShipItem item)
        {
            var good = item != null ? item.GetComponent<Good>() : null;
            if (good == null) return -1;
            int index = good.GetMissionIndex();
            if (index < 0 || MissionNamesProduct(item, index)) return index;
            // A Good made without IslandMarket.SpawnGood (a mod's purchase, for one) keeps the prefab's
            // serialized slot, usually 0. A slot whose mission carries another product cannot be this
            // item's mission: the item is ordinary stock, here and on every machine it goes to.
            good.RegisterAsMissionless();
            try { item.UpdateLookText(); } catch { }
            return -1;
        }

        /// <summary>False only when the mission in <paramref name="index"/> is known and carries a
        /// different product; an unknown slot is left to the journal (<see cref="ApplyMissionIndex"/>).</summary>
        private static bool MissionNamesProduct(ShipItem item, int index)
        {
            var missions = PlayerMissions.missions;
            var mission = missions != null && index < missions.Length ? missions[index] : null;
            var expected = mission != null && mission.goodPrefab != null ? mission.goodPrefab.GetComponent<SaveablePrefab>() : null;
            var actual = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return expected == null || actual == null || expected.prefabIndex == actual.prefabIndex;
        }

        /// <summary>Client: give a good the mission slot the host's copy has. A Good prefab carries slot 0,
        /// so without this every good created during the session reads as cargo of mission 0.</summary>
        private void ApplyMissionIndex(ShipItem item, int instanceId, int missionIndex)
        {
            var good = item != null ? item.GetComponent<Good>() : null;
            if (good == null) return;
            try
            {
                _pendingMissionGoods.Remove(instanceId);
                if (missionIndex < 0) { good.RegisterAsMissionless(); return; }
                var missions = PlayerMissions.missions;
                var mission = missions != null && missionIndex < missions.Length ? missions[missionIndex] : null;
                if (mission == null)
                {
                    // The journal has not brought this mission yet. ShipItem.UpdateLookText dereferences
                    // the slot, so the good stays plain until the mission is there (same as a vanilla load).
                    good.RegisterAsMissionless();
                    _pendingMissionGoods[instanceId] = missionIndex;
                    return;
                }
                if (!MissionNamesProduct(item, missionIndex))
                {
                    bool changed = good.GetMissionIndex() != -1;
                    good.RegisterAsMissionless();
                    if (changed) item.UpdateLookText();
                    return;
                }
                if (good.GetMissionIndex() == missionIndex) return;
                ItemComponents.Set(good, "missionIndex", missionIndex);
                ItemComponents.Set(good, "dueDay", mission.dueDay);
                item.UpdateLookText();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ItemSync] role=Client mission slot " + missionIndex + " for item id=" +
                                         instanceId + " not applied: " + ex.Message);
            }
        }

        private void RetryMissionGoods()
        {
            var missions = PlayerMissions.missions;
            if (missions == null) return;
            List<int> ready = null;
            foreach (var pair in _pendingMissionGoods)
            {
                bool gone = !_byInstanceId.TryGetValue(pair.Key, out var e) || e.Item == null;
                if (gone || (pair.Value < missions.Length && missions[pair.Value] != null))
                    (ready ?? (ready = new List<int>())).Add(pair.Key);
            }
            if (ready == null) return;
            foreach (int id in ready)
            {
                int slot = _pendingMissionGoods[id];
                _pendingMissionGoods.Remove(id);
                if (_byInstanceId.TryGetValue(id, out var e) && e.Item != null) ApplyMissionIndex(e.Item, id, slot);
            }
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
                if (_hostIds.Contains(instanceId) && _byInstanceId.ContainsKey(instanceId)) return _byInstanceId[instanceId].Item;
                ReleaseUnsharedCollision(instanceId, null);
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || item == null) return;
            if (_pendingClientItems.Contains(item)) return;
            int currentId = InstanceIdOf(item);
            if (_hostIds.Contains(currentId) && !_tombstones.Contains(currentId)) return;
            uint requestId = ++_nextRequest;
            if (requestId == 0) requestId = ++_nextRequest;
            _pendingClientItems.Add(requestId, item, PrefabIndexOf(item));
            BuildPose(item, _net.Clock.ServerTick, out CoordFrame frame, out ushort boatIndex,
                      out Vector3 pos, out Quaternion rot, out _);
            _net.Broadcast(new FishCatchMsg
            {
                RequestId = requestId,
                PrefabIndex = PrefabIndexOf(item),
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                Amount = FoldableState.Capture(item),
                Health = item.health,
                Sold = item.sold,
                MissionIndex = MissionIndexOf(item),
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
            if (InteractionContext.Suppressed) return;
            if (ItemOperationCapture.Absorb()) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (instanceId <= 0 || prefabIndex <= 0) return;
            if (!_hostIds.Contains(instanceId)) return;
            _net.Broadcast(new ItemRequestMsg { Action = ItemAction.Consume, InstanceId = instanceId, PrefabIndex = prefabIndex },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out sold(despawn) id=" + instanceId);
        }

        /// <summary>Host: author the item the client created; RefreshItems then broadcasts the SpawnObject.</summary>
        public void OnFishCatch(FishCatchMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0 || msg.RequestId == 0) return;
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

                var retained = new List<ItemEntry>();
                if (!_authoredResults.TryBegin(actor, msg.RequestId, retained, out var previous))
                {
                    foreach (var entry in previous) BroadcastSpawn(entry, fromPeer, snapshot: true);
                    return;
                }
                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                var item = go.GetComponent<ShipItem>();
                var saveable = go.GetComponent<SaveablePrefab>();
                if (item == null || saveable == null) { UnityEngine.Object.Destroy(go); return; }
                _authoringItem = item;
                _authoredIdentity[item] = new KeyValuePair<uint, uint>(actor, msg.RequestId);
                item.sold = true;
                // As IslandMarket.SpawnGood does: a Good prefab carries a serialized mission slot
                // (usually 0), and a bought good is ordinary stock.
                var good = go.GetComponent<Good>();
                if (good != null) good.RegisterAsMissionless();
                saveable.prefabIndex = msg.PrefabIndex;
                saveable.RegisterToSave();   // assigns a fresh nonzero host id
                ApplyScalarState(item, msg.Amount, msg.Health, msg.Sold, item.nailed);
                // A mission good the client took from its belt keeps its mission: the game's own
                // registration, as for a good the mission handed out. A slot that is empty here or
                // names another product leaves it ordinary stock (set above).
                var missions = PlayerMissions.missions;
                if (good != null && msg.MissionIndex >= 0 && missions != null && msg.MissionIndex < missions.Length &&
                    missions[msg.MissionIndex] != null && MissionNamesProduct(item, msg.MissionIndex))
                    good.RegisterToMission(msg.MissionIndex, missions[msg.MissionIndex].dueDay);
                RefreshItems(force: true);
                var authored = _byItem[item];
                authored.AuthorRequester = actor;
                authored.AuthorRequestId = msg.RequestId;
                retained.Add(authored);
                _authoringItem = null;
                BroadcastSpawn(authored);
                Remember("in fish catch prefab=" + msg.PrefabIndex + " id=" + saveable.instanceId);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemSync] OnFishCatch: " + e.Message);
            }
            finally { _authoringItem = null; }
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

        // Protocol 75 retires the legacy unclocked RodState stream; keep its wire slot reserved.
        public void OnRodState(RodStateMsg msg, LiteNetLib.NetPeer fromPeer) { }

        public void Clear()
        {
            foreach (var e in _items)
                if (e != null && e.Item != null)
                    RestoreDisconnectedItem(e.Item);

            ClearInstruments();

            _items.Clear();
            _byItem.Clear();
            _byInstanceId.Clear();
            _localHeld.Clear();
            _pendingDynamic.Clear();
            _suppressNextDrop.Clear();
            _hostIds.Clear();
            _tombstones.Clear();
            _pendingStates.Clear();
#if DEBUG
            _nextPendingReport = 0f;
            _pendingAttemptsByReason.Clear();
#endif
            _pendingStateBuffer.Clear();
            _pendingStateCursor = 0;
            _nextPendingStateRetry = 0f;
            _crateMembership.Clear();
            _unsealedCrate = null;
            _cargoMembership.Clear();
            _pendingSpawns.Clear();
            _spawnWait.Clear();
            _pendingMissionGoods.Clear();
            _nextRequest = 0;
            ClearOperations();
            _puppetAwaitingStart.Clear();
            ItemComponents.Clear();
            _localClaimed.Clear();
            _pendingClientItems.Clear();
            _baselineItems.Clear();
            _saveIdentity.Clear();
            _authoredResults.Clear();
            _authoredIdentity.Clear();
            _hostHeldFromBelt.Clear();
            _authoringItem = null;
            _pendingHeldItem = null;
            _gp = null;
            _rejectedPoseLogged.Clear();
            _fHeldItem = null;
            _refreshTimer = 0f;
            _sendTimer = 0f;
            _heldPoseTimer = 0f;
            _extraTimer = 0f; _extraCursor = 0;
            ClearOarStroke();
            _baselineReady = false;
            _baselineCount = -1;
            _baselineChangedAt = 0f;
            _last = "—";
            _lastEventTick = 0L;
        }
    }

}
