using System.Collections;
using HarmonyLib;
using SailwindCoop.Avatar;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// F5 — the single MonoBehaviour that owns the network loop. Pumps
    /// <see cref="CoopNet.PollEvents"/> on the Unity main thread, hosts the mouse
    /// driven co-op menu, and drives Stage 1+ Sync components.
    /// </summary>
    public sealed class CoopBehaviour : MonoBehaviour
    {
        public static CoopBehaviour Instance { get; private set; }
        public CoopNet Net { get; private set; }
        public PlayerSync Players { get; private set; }
        public BoatSync Boats { get; private set; }

        /// <summary>AI-корабли: у хоста считаются, у клиента ведутся снапшотами.</summary>
        public NpcBoatSync NpcBoats { get; private set; }
        public EnvironmentSync Env { get; private set; }
        public CrestWaterSync CrestWater { get; private set; }
        public ControlsSync Controls { get; private set; }
        public AnchorSync Anchor { get; private set; }
        public MooringSync Mooring { get; private set; }
        public BoatDamageSync Damage { get; private set; }
        public LightSync Lights { get; private set; }
        public ItemSync Items { get; private set; }
        public InteractionSync Interactions { get; private set; }
        public WindTotemSync WindTotem { get; private set; }
        public ShopSync Shop { get; private set; }
        public WeatherStormSync Storms { get; private set; }
        public SleepSync Sleep { get; private set; }
        public MissionSync Missions { get; private set; }
        public ShipyardSync Shipyard { get; private set; }
        public SaveTransferSync SaveTransfer { get; private set; }
        public JoinPause Pause { get; private set; }

        /// <summary>Клиентская сторона паузы хоста: пока хост стоит, наш игрок не ходит.</summary>
        public HostPauseSync HostPause { get; private set; }

        private DebugOverlay _overlay;
        private bool _overlayVisible = false;
        private DebugPanel _debugPanel;
        private AvatarSelectUI _avatarUI;
        private CoopMenuUI _menuUI;
        private Harmony _harmony;
        private bool _clientProfileSavedOnShutdown;

        /// <summary>Is OUR co-op menu the thing holding the cursor? Lets <see cref="JoinPause"/> tell a
        /// game menu (which stops the clock) from this one (which does not).</summary>
        public bool CoopMenuOpen => _menuUI != null && _menuUI.Visible;

        public bool OverlayVisible
        {
            get => _overlayVisible;
            set => _overlayVisible = value;
        }

        /// <summary>
        /// Last user-facing problem, shown in the co-op menu. Deliberately independent of the logging
        /// switch: logging is off by default, so a join that fails for an actionable reason ("the host
        /// never loaded a save") would otherwise produce no trace anywhere — not in the log, not in the
        /// menu, not in the overlay — and the host would simply unfreeze after the 120 s timeout with
        /// the user none the wiser. Log lines explain; this tells the player what to do.
        /// </summary>
        public static string LastNotice { get; private set; } = "";

        /// <summary>Record a problem worth showing the player even with logging switched off.</summary>
        public static void Notice(string text)
        {
            LastNotice = text ?? "";
        }

        /// <summary>
        /// Drop a stale notice. Called when a session is started or torn down: the text describes one
        /// join attempt ("this host has no world loaded"), so without this it stayed pinned in the menu
        /// through every later — successful — session, telling the player to fix a problem that is gone.
        /// </summary>
        public static void ClearNotice()
        {
            LastNotice = "";
        }

        private void Awake()
        {
            Instance = this;
            Net = new CoopNet(m => Plugin.Logger.LogInfo(m))
            {
                ModVersion = Plugin.Version,
                PlayerName = Plugin.Cfg.PlayerName.Value,
                // Stage 1 will replace this with the host's loaded save identity.
                WorldIdProvider = () => "",
                MaxClients = Plugin.Cfg.MaxClients.Value,
                DisconnectTimeoutMs = Plugin.Cfg.DisconnectTimeoutMs.Value,
                UpdateTimeMs = Plugin.Cfg.UpdateTimeMs.Value,
                PingIntervalMs = Plugin.Cfg.PingIntervalMs.Value,
                ListenIp = Plugin.Cfg.ListenIp.Value,
                ConnectAttempts = Plugin.Cfg.ConnectAttempts.Value,
                ReconnectDelayMs = Plugin.Cfg.ReconnectDelayMs.Value,
            };

            Players = new PlayerSync(Net)
            {
                InterpDelayMs = Plugin.Cfg.InterpDelayMs.Value,
                SnapshotHz = Plugin.Cfg.SnapshotHz.Value,
            };

            Boats = new BoatSync(Net)
            {
                InterpDelayMs = Plugin.Cfg.InterpDelayMs.Value,
                SnapshotHz = Plugin.Cfg.SnapshotHz.Value,
                // Lets the first (potentially huge) boat correction carry us instead of dropping us
                // through the deck. Sync layers stay decoupled: BoatSync asks, it doesn't reach in.
                //
                // The RESOLVING variant, not the plain LocalPlayerBody property: BoatSync asks on the
                // very first engage frame, which on a fresh join happens before Players.Tick (step 19)
                // has ever run, so the property would still be reading a null embarker and the carry
                // would no-op on exactly the one frame it exists for. Body, not LocalPlayer — the
                // latter can be the camera transform.
                LocalPlayerProvider = () => Players.ResolveLocalPlayerBodyNow(),
                LocalBoatProvider = () => Players.LocalBoat,
                ResolveLocalBoatProvider = () => Players.ResolveLocalBoatNow(),
            };

            NpcBoats = new NpcBoatSync(Net);
            Env = new EnvironmentSync(Net);
            CrestWater = new CrestWaterSync();
            Env.Crest = CrestWater;
            Controls = new ControlsSync(Net);
            Anchor = new AnchorSync(Net);
            Mooring = new MooringSync(Net);
            Damage = new BoatDamageSync(Net);
            Lights = new LightSync(Net);
            Items = new ItemSync(Net);
            Interactions = new InteractionSync(Net);
            WindTotem = new WindTotemSync(Net);
            Shop = new ShopSync(Net);
            Storms = new WeatherStormSync(Net);
            Sleep = new SleepSync(Net);
            Missions = new MissionSync(Net);
            Shipyard = new ShipyardSync(Net);
            SaveTransfer = new SaveTransferSync(Net) { CoopSlot = Plugin.Cfg.CoopSaveSlot.Value };
            Pause = new JoinPause();
            HostPause = new HostPauseSync(Net);

            // F3 — intercept the game's interaction layer so a client's clicks reach the host.
            _harmony = new Harmony(Plugin.Guid);
            try { InteractionPatches.Apply(_harmony); MooringPatches.Apply(_harmony); BoatDamagePatches.Apply(_harmony); LightPatches.Apply(_harmony); ItemPatches.Apply(_harmony); ShopPatches.Apply(_harmony); SavePatches.Apply(_harmony); SleepPatches.Apply(_harmony); MissionPatches.Apply(_harmony); ShipyardPatches.Apply(_harmony); NpcBoatPatches.Apply(_harmony); }
            catch (System.Exception e) { Plugin.Logger.LogError("[Coop] Failed to apply Harmony patches: " + e); }

            Net.OnAccepted += ack =>
                Plugin.Logger.LogInfo("[Coop] Connection accepted, NetId=" + ack.AssignedNetId);
            Net.OnClientReady += s =>
            {
                Plugin.Logger.LogInfo("[Coop] Client ready: " + s.PlayerName +
                                      ", avatar=" + (string.IsNullOrEmpty(s.SelectedAvatar) ? "(default)" : s.SelectedAvatar));
                // Remember the bundle file this client wants; used when their first PlayerState arrives.
                Players.RegisterRemoteAvatarFile(s.PlayerNetId, s.SelectedAvatar);
                // Stream the host's world to the freshly-joined client so it loads into our world.
                // The join-freeze is taken INSIDE that coroutine, after the save is on disk — freezing
                // first would mean asking the game to save while its own clock is stopped.
                StartCoroutine(StreamSaveToClient(s.Peer, s.PlayerNetId));
            };
            Net.OnGameMessage += OnGameMessage;
            Net.OnPlayerLeft += netId =>
            {
                Players.RemoveRemote(netId);
                Items.ClearRemoteActor(netId);
                Damage.ClearRemoteActor(netId);
                Pause.Release(netId);
            };

            // Re-broadcast our own selection to the other side whenever it changes locally.
            AvatarCatalog.OnSelectionChanged += newFile =>
            {
                if (Net.State != LinkState.Connected) return;
                Net.SendAvatarChange(newFile);
            };

            _overlay = new DebugOverlay(Net);
            // Always created (lightweight). Availability is gated live by EnableDebugPanel so it can be
            // toggled in-game (e.g. via BepInEx.ConfigurationManager) without a restart.
            _debugPanel = new DebugPanel(Net);
            _avatarUI = new AvatarSelectUI(AvatarCatalog.CurrentSelection);
            _menuUI = new CoopMenuUI(this, Net);

            BuildSteps();
        }

        /// <summary>One isolated step of the per-frame sync pipeline. Delegates are built once in
        /// <see cref="BuildSteps"/>, so running them allocates nothing.</summary>
        private sealed class SyncStep
        {
            public readonly string Name;
            public readonly System.Action Run;
            public CoopLog.Repeat Failures;

            public SyncStep(string name, System.Action run) { Name = name; Run = run; }
        }

        private SyncStep[] _steps;
        private float _dt;
        private CoopLog.Repeat _menuFailures;
        private CoopLog.Repeat _pollFailures;

        /// <summary>
        /// The per-frame pipeline, in the one order that works: boat/environment/control state settles
        /// before player application, since players are children of the boat and must land on an
        /// already-updated deck. Adding a subsystem in the wrong position makes it read a stale
        /// boat/player pose for one frame.
        ///
        /// Each step is invoked behind its own try/catch. The mod patches and drives a live game, so a
        /// single subsystem hitting a destroyed object must degrade to "that subsystem is broken" and
        /// not "every subsystem after it stopped running this frame" — which is what a bare list of
        /// calls did, with Players.ApplyRemotes (last) failing first and most visibly.
        /// </summary>
        private void BuildSteps()
        {
            _steps = new[]
            {
                new SyncStep("Pause.Tick", () => Pause.Tick()),
                new SyncStep("Boats.Tick", () => Boats.Tick(_dt)),
                new SyncStep("Boats.ApplyRemote", () => Boats.ApplyRemote()),
                // Сразу за лодкой игрока: AI-корабли ни к кому не приаттачены, но должны встать до
                // применения поз игроков — иначе столкновение с ними читается по вчерашней позе.
                new SyncStep("NpcBoats.TickHost", () => NpcBoats.TickHost()),
                new SyncStep("NpcBoats.ApplyRemote", () => NpcBoats.ApplyRemote()),
                new SyncStep("Env.Tick", () => Env.Tick(_dt)),
                // Сразу за Env: именно он держит последний известный timeScale хоста.
                new SyncStep("HostPause.Tick", () => HostPause.Tick(Env)),
                new SyncStep("CrestWater.TickHost", () => { if (Net.Role == Role.Host) CrestWater.TickHost(Net); }),
                // Замер воды крутится на обеих ролях: он нужен для дампа, а дамп снимают одновременно.
                new SyncStep("CrestWater.TickProbe", () =>
                {
                    var t = Players.LocalBoat ?? Players.ResolveLocalPlayerBodyNow();
                    if (t != null) CrestWater.TickProbe(t.position);
                }),
                // Straight after Env.Tick: that is what advances WaveClock, and Crest reads the
                // provider in LateUpdate, so the value it sees is always this frame's.
                new SyncStep("Storms.Tick", () => Storms.Tick(_dt)),
                new SyncStep("Sleep.Tick", () => Sleep.Tick(_dt)),
                new SyncStep("Missions.Tick", () => Missions.Tick(_dt)),
                new SyncStep("Controls.Tick", () => Controls.Tick(_dt)),
                new SyncStep("Controls.ApplyClient", () => Controls.ApplyClient(_dt)),
                new SyncStep("Anchor.Tick", () => Anchor.Tick(_dt)),
                new SyncStep("Anchor.ApplyRemote", () => Anchor.ApplyRemote()),
                new SyncStep("Mooring.Tick", () => Mooring.Tick(_dt)),
                new SyncStep("Damage.Tick", () => Damage.Tick(_dt)),
                new SyncStep("Lights.Tick", () => Lights.Tick(_dt)),
                new SyncStep("Items.Tick", () => Items.Tick(_dt)),
                new SyncStep("Items.ApplyRemote", () => Items.ApplyRemote()),
                new SyncStep("WindTotem.Tick", () => WindTotem.Tick(_dt)),
                new SyncStep("Interactions.Tick", () => Interactions.Tick(_dt)),
                new SyncStep("Players.Tick", () => Players.Tick(_dt)),
                new SyncStep("Players.ApplyRemotes", () => Players.ApplyRemotes()),
            };
        }

        private void Update()
        {
            // Both of these can fail every single frame (a null _menuUI or Net after a failed Awake), so
            // they are throttled like every other repeating site — an unthrottled per-frame stack trace
            // is the sustained disk I/O this containment exists to avoid.
            try
            {
                if (Input.GetKeyDown(Plugin.Cfg.MenuKey.Value))
                    _menuUI.Toggle();
            }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Menu toggle failed", e, ref _menuFailures);
            }

            try { Net.PollEvents(); }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Net.PollEvents failed", e, ref _pollFailures);
            }

            // BuildSteps runs at the end of Awake; if anything before it threw, the pipeline was never
            // built. Bail instead of throwing an uncaught NullReferenceException every single frame —
            // that would be the exact failure mode this per-step containment exists to remove.
            if (_steps == null) return;

            _dt = Time.deltaTime;
            for (int i = 0; i < _steps.Length; i++)
            {
                var step = _steps[i];
                try { step.Run(); }
                catch (System.Exception e) { ReportStepFailure(step, e); }
            }
        }

        /// <summary>A broken subsystem usually throws every single frame — log the first few in full,
        /// then only occasionally, so the log stays readable instead of becoming one stack trace.</summary>
        private static void ReportStepFailure(SyncStep step, System.Exception e)
        {
            Plugin.Logger.ReportError("[Coop] " + step.Name + " failed", e, ref step.Failures);
        }

        private void OnGameMessage(MsgType type, INetMessage msg, LiteNetLib.NetPeer fromPeer)
        {
            switch (type)
            {
                case MsgType.PlayerState:
                    Players.OnPlayerState((PlayerStateMsg)msg, fromPeer);
                    break;
                case MsgType.BoatState:
                    Boats.OnBoatState((BoatStateMsg)msg, fromPeer);
                    break;
                case MsgType.EnvState:
                    Env.OnEnvState((EnvStateMsg)msg, fromPeer);
                    break;
                case MsgType.ControlState:
                    Controls.OnControlState((ControlStateMsg)msg, fromPeer);
                    break;
                case MsgType.AnchorState:
                    Anchor.OnAnchorState((AnchorStateMsg)msg, fromPeer);
                    break;
                case MsgType.MooringState:
                    Mooring.OnMooringState((MooringStateMsg)msg, fromPeer);
                    break;
                case MsgType.BoatDamageState:
                    Damage.OnDamageState((BoatDamageStateMsg)msg, fromPeer);
                    break;
                case MsgType.MooringRequest:
                    Mooring.OnMooringRequest((MooringRequestMsg)msg, fromPeer);
                    break;
                case MsgType.SteerRequest:
                    Controls.OnSteerRequest((SteerRequestMsg)msg, fromPeer);
                    break;
                case MsgType.ControlRequest:
                    Controls.OnControlRequest((ControlRequestMsg)msg, fromPeer);
                    break;
                case MsgType.ControlEvent:
                    Interactions.OnControlEvent((ControlEventMsg)msg, fromPeer);
                    break;
                case MsgType.HoldRequest:
                    Interactions.OnHoldRequest((HoldRequestMsg)msg, fromPeer);
                    break;
                case MsgType.DamageRequest:
                    Damage.OnDamageRequest((DamageRequestMsg)msg, fromPeer);
                    break;
                case MsgType.PushRequest:
                    Interactions.OnPushRequest((PushRequestMsg)msg, fromPeer);
                    break;
                case MsgType.LightState:
                    Lights.OnLightState((LightStateMsg)msg, fromPeer);
                    break;
                case MsgType.LightRequest:
                    Lights.OnLightRequest((LightRequestMsg)msg, fromPeer);
                    break;
                case MsgType.ItemState:
                    Items.OnItemState((ItemStateMsg)msg, fromPeer);
                    break;
                case MsgType.ItemRequest:
                    Items.OnItemRequest((ItemRequestMsg)msg, fromPeer);
                    break;
                case MsgType.SpawnObject:
                    Items.OnSpawnObject((SpawnObjectMsg)msg, fromPeer);
                    break;
                case MsgType.DespawnObject:
                    Items.OnDespawnObject((DespawnObjectMsg)msg, fromPeer);
                    break;
                case MsgType.ItemExtra:
                    Items.OnItemExtraState((ItemExtraStateMsg)msg, fromPeer);
                    break;
                case MsgType.WindRequest:
                    WindTotem.OnWindRequest((WindRequestMsg)msg, fromPeer);
                    break;
                case MsgType.FishCatch:
                    Items.OnFishCatch((FishCatchMsg)msg, fromPeer);
                    break;
                case MsgType.RodState:
                    Items.OnRodState((RodStateMsg)msg, fromPeer);
                    break;
                case MsgType.WavePhases:
                    CrestWater.OnWavePhases((WavePhasesMsg)msg);
                    break;
                case MsgType.NpcBoatState:
                    NpcBoats.OnNpcBoatState((NpcBoatStateMsg)msg, fromPeer);
                    break;
                case MsgType.StormState:
                    Storms.OnStormState((StormStateMsg)msg, fromPeer);
                    break;
                case MsgType.SleepState:
                    Sleep.OnSleepState((SleepStateMsg)msg, fromPeer);
                    break;
                case MsgType.MissionJournal:
                    Missions.OnMissionJournal((MissionJournalMsg)msg, fromPeer);
                    break;
                case MsgType.MissionReward:
                    Missions.OnMissionReward((MissionRewardMsg)msg, fromPeer);
                    break;
                case MsgType.MissionAccept:
                    Missions.OnMissionAccept((MissionAcceptMsg)msg, fromPeer);
                    break;
                case MsgType.MissionAbandon:
                    Missions.OnMissionAbandon((MissionAbandonMsg)msg, fromPeer);
                    break;
                case MsgType.BoatPurchase:
                    Shipyard.OnBoatPurchase((BoatPurchaseMsg)msg, fromPeer);
                    break;
                case MsgType.AvatarChange:
                    HandleAvatarChange((AvatarChangeMsg)msg, fromPeer);
                    break;
                case MsgType.SaveSnapshotBegin:
                    SaveTransfer.OnBegin((SaveSnapshotBeginMsg)msg);
                    break;
                case MsgType.SaveSnapshotChunk:
                    SaveTransfer.OnChunk((SaveSnapshotChunkMsg)msg);
                    break;
                case MsgType.SaveSnapshotEnd:
                    SaveTransfer.OnEnd((SaveSnapshotEndMsg)msg);
                    break;
                case MsgType.ClientWorldLoaded:
                    if (Net.Role == Role.Host)
                    {
                        uint netId = Net.PlayerNetIdForPeer(fromPeer);
                        Plugin.Logger.LogInfo("[Coop] Client NetId=" + netId + " loaded world: " +
                                              (((ClientWorldLoadedMsg)msg).Ok ? "ok" : "with error"));
                        Pause.Release(netId);
                    }
                    break;
            }
        }

        /// <summary>True while one join owns the host: from taking the queue slot until the transfer
        /// coroutine finishes. See <see cref="JoinInFlight"/> for the other half of the window.</summary>
        private bool _streamingSave;
        private float _streamingSaveDeadline;

        /// <summary>
        /// Ownership token for the queue slot. A coroutine may still be running long after the slot was
        /// force-released (teardown, timeout) — without this, its <c>finally</c> would land later and
        /// clear the slot belonging to a *different*, still-active join, letting two of them run the
        /// "save the world while the clock may be stopped" path at once.
        /// </summary>
        private int _streamingSaveEpoch;

        /// <summary>Ceiling on how long one join may hold the queue. Generous: the inner routine can
        /// legitimately spend 15 s waiting for a save window plus 10 s for the write, then transfer.</summary>
        private const float StreamSaveTimeoutSec = 60f;

        /// <summary>
        /// Is a join still occupying the host? True while the save is being produced/sent, and then
        /// while the join-freeze is up.
        ///
        /// The freeze half matters as much as the transfer half, and used to be missing: the flag was
        /// dropped the instant the bytes went out, but <see cref="Pause"/> stays held until that client
        /// reports <c>ClientWorldLoaded</c> — up to 120 s. A second joiner sailed straight through the
        /// queue and called <c>SaveGame</c> with <c>timeScale == 0</c>, which is the very deadlock this
        /// serialization exists to prevent. <see cref="JoinPause"/> already owns that window (it has its
        /// own timeout, is refcounted per client and is cleared on teardown), so asking it is both
        /// correct and free of new lifetime state.
        ///
        /// The transfer half self-expires: a coroutine killed mid-flight (scene teardown, the object
        /// going away) may never run its <c>finally</c>, and a flag stuck true used to mean every future
        /// join in the process hung forever on the wait below — silently, with logging off.
        /// </summary>
        private bool JoinInFlight()
        {
            if (_streamingSave && Time.realtimeSinceStartup >= _streamingSaveDeadline)
            {
                Plugin.Logger.LogWarning("[Coop] Save-stream slot was never released within " +
                                         StreamSaveTimeoutSec + " s - clearing it so joins keep working");
                ResetJoinStreaming();
            }
            return _streamingSave || (Pause != null && Pause.Active);
        }

        /// <summary>Teardown: drop the queue slot so a later session does not inherit a stuck join.</summary>
        private void ResetJoinStreaming()
        {
            _streamingSave = false;
            _streamingSaveDeadline = 0f;
            _streamingSaveEpoch++;
        }

        /// <summary>
        /// Serializes joins. With MaxClients &gt; 1 two clients can hand-shake moments apart, and the two
        /// coroutines then interleave: client A takes the join-freeze (<c>timeScale = 0</c>) while
        /// client B is still waiting for a save window and asks the game to save — the exact "save while
        /// the clock is stopped" deadlock the ordering fix below was meant to remove. B would burn its
        /// 15 s + 10 s waits and then the pause's 120 s safety timeout. One at a time.
        ///
        /// <c>yield return null</c> resumes on the next frame regardless of <c>timeScale</c>, so this
        /// wait still progresses while the host is frozen for the client ahead in the queue.
        /// </summary>
        private IEnumerator StreamSaveToClient(LiteNetLib.NetPeer peer, uint netId)
        {
            if (JoinInFlight())
                Plugin.Logger.LogInfo("[Coop] NetId=" + netId + " is queued behind another join");
            while (JoinInFlight())
            {
                // A peer that gives up while queued must not keep the next one waiting.
                if (peer == null || peer.ConnectionState != LiteNetLib.ConnectionState.Connected)
                {
                    Plugin.Logger.LogWarning("[Coop] Client NetId=" + netId + " left while queued for the world transfer");
                    Pause.Release(netId);
                    yield break;
                }
                yield return null;
            }

            _streamingSave = true;
            _streamingSaveDeadline = Time.realtimeSinceStartup + StreamSaveTimeoutSec;
            int epoch = ++_streamingSaveEpoch;
            try { yield return StreamSaveToClientInner(peer, netId, epoch); }
            finally { if (_streamingSaveEpoch == epoch) _streamingSave = false; }
        }

        /// <summary>
        /// Host side: when a client finishes the handshake, save the host's world fresh (so the client
        /// gets the up-to-date economy/objects/position), then stream the save file to that client.
        ///
        /// Order matters. The join-freeze (<see cref="JoinPause"/>) stops the host's clock outright, so
        /// it must be taken only AFTER the forced save has finished writing: the game's own save path
        /// runs as a coroutine, and asking it to complete while <c>Time.timeScale == 0</c> risks it
        /// never finishing — burning the save-window and save-busy waits below and then the pause's own
        /// 120 s safety timeout. Freezing right before the bytes go out still covers the window that
        /// actually matters (snapshot on the wire → client in the world).
        /// </summary>
        private IEnumerator StreamSaveToClientInner(LiteNetLib.NetPeer peer, uint netId, int epoch)
        {
            // Give the handshake a frame to settle.
            yield return null;

            if (!GameState.playing)
            {
                // Without a loaded world SaveSlots.currentSlot points at an arbitrary slot —
                // never stream that to a client.
                Plugin.Logger.LogError("[Coop] Host is not in-game (save not loaded) - world was not sent to client. " +
                                       "Load a save before accepting clients.");
                Notice("Client rejected: this host has no world loaded. Load a save, then host again.");
                Pause.Release(netId);
                yield break;
            }

            if (Plugin.Cfg.ForceHostSaveOnJoin.Value && SaveLoadManager.instance != null)
            {
                // SaveGame silently refuses while busy / in bed / in shipyard / not ready — wait for a
                // window where it can run, then verify it really started (DoSaveGame flips its private
                // 'busy' flag synchronously inside the SaveGame call).
                bool started = false;
                for (float t = 0f; !started && t < 15f; t += Time.unscaledDeltaTime)
                {
                    if (SaveLoadManager.readyToSave && !SaveTransferSync.HostSaveBusy() &&
                        !GameState.inBed && !GameState.currentShipyard)
                    {
                        try { SaveLoadManager.instance.SaveGame(compressed: true); }
                        catch (System.Exception e)
                        {
                            Plugin.Logger.LogWarning("[Coop] Forced host save failed: " + e.Message);
                            break;
                        }
                        started = SaveTransferSync.HostSaveBusy();
                    }
                    if (!started) yield return null;
                }

                if (started)
                {
                    // Wait for DoSaveGame to finish writing the file (timeout guards a stuck save).
                    for (float t = 0f; SaveTransferSync.HostSaveBusy() && t < 10f; t += Time.unscaledDeltaTime)
                        yield return null;
                    yield return new WaitForEndOfFrame();
                }
                else
                {
                    Plugin.Logger.LogWarning("[Coop] Timed out waiting for a fresh save window - " +
                                             "client will receive the last save from disk");
                }
            }

            byte[] bytes = SaveTransferSync.ReadHostSaveBytes();
            // Length check matters as much as null: SendSaveTo silently returns on an empty array, and
            // with the pause taken just below that would freeze the host until the 120 s safety timeout,
            // because no client would ever report ClientWorldLoaded.
            if (bytes == null || bytes.Length == 0)
            {
                Plugin.Logger.LogError("[Coop] No host save available to send to client (" +
                                       (bytes == null ? "unreadable" : "empty file") + ")");
                Notice("Could not read this host's save file - the world was not sent to the client.");
                Pause.Release(netId);
                yield break;
            }
            if (peer == null || peer.ConnectionState != LiteNetLib.ConnectionState.Connected)
            {
                Plugin.Logger.LogWarning("[Coop] Client disconnected before save transfer");
                Pause.Release(netId);
                yield break;
            }
            // Commit point. Everything above is read-only; below we freeze the host and put bytes on the
            // wire. If the session was torn down or our slot was force-released while we waited for the
            // save (up to 25 s of yields), do neither — a freeze taken here would have no client left to
            // release it and would sit until the 120 s safety timeout.
            if (_streamingSaveEpoch != epoch)
            {
                Plugin.Logger.LogWarning("[Coop] Save transfer for NetId=" + netId +
                                         " was superseded before it could start - dropping it");
                Pause.Release(netId);
                yield break;
            }

            // The snapshot is on disk and about to go out — freeze now so items/anchor/moorings/waves
            // still match it by the time the client is standing in the world.
            if (Plugin.Cfg.PauseHostOnJoin.Value && GameState.playing)
                Pause.Hold(netId);

            SaveTransfer.SendSaveTo(peer, bytes);
        }

        private void HandleAvatarChange(AvatarChangeMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            Plugin.Logger.LogInfo("[Coop] AvatarChange NetId=" + msg.NetId + " -> '" + msg.BundleFile + "'");
            Players.ApplyAvatarChange(msg.NetId, msg.BundleFile);
        }

        private CoopLog.Repeat _guiFailures;

        private void OnGUI()
        {
            // IMGUI runs this several times per frame; an escaping exception leaves Unity's GUI layout
            // stack unbalanced and cascades into unrelated "GUI Error" spam, so contain it here too.
            try
            {
                if (HostPause != null && HostPause.Frozen) DrawHostPausedBanner();
                if (_menuUI != null) _menuUI.Draw();
                if (_overlayVisible) _overlay.Draw();
                if (Plugin.Cfg.EnableDebugPanel.Value) _debugPanel.Draw();
                if (_avatarUI != null) _avatarUI.Draw();
            }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] OnGUI failed", e, ref _guiFailures);
            }
        }

        private static GUIStyle _pausedBanner;

        /// <summary>
        /// Пока хост на паузе, клиент не может ходить (<see cref="HostPauseSync"/>). Без надписи это
        /// неотличимо от зависшей игры, поэтому баннер рисуется всегда — не в оверлее и не в меню,
        /// которые по умолчанию закрыты.
        /// </summary>
        private void DrawHostPausedBanner()
        {
            if (_pausedBanner == null)
                _pausedBanner = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
            const float w = 360f, h = 34f;
            GUI.Label(new Rect((Screen.width - w) * 0.5f, 24f, w, h),
                      "Host paused the game", _pausedBanner);
        }

        private void OnDestroy()
        {
            SaveClientProfileBeforeStop("destroy");
            ResetJoinStreaming();
            Missions?.Clear();
            Sleep?.Clear();
            Shop?.Clear();
            WindTotem?.Clear();
            Interactions?.Clear();
            Items?.Clear();
            Lights?.Clear();
            Damage?.Clear();
            Mooring?.Clear();
            Anchor?.Clear();
            Controls?.Clear();
            Env?.Clear();
            CrestWater?.Clear();
            NpcBoats?.Clear();
            Boats?.Clear();
            Players?.Clear();
            Pause?.Clear();
            HostPause?.Clear();
            Net?.Stop();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            SaveClientProfileBeforeStop("quit");
            Net?.Stop();
        }

        private void SaveClientProfileBeforeStop(string reason)
        {
            if (_clientProfileSavedOnShutdown) return;
            try
            {
                if (Net == null || Net.Role != Role.Client || Net.State != LinkState.Connected) return;
                if (CoopProfile.SaveFromGame())
                {
                    _clientProfileSavedOnShutdown = true;
                    Plugin.Logger.LogInfo("[Coop] Client profile saved before session stop: " + reason);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[Coop] Failed to save client profile before stop: " + e.Message);
            }
        }

        public void ToggleAvatarMenu()
        {
            if (_avatarUI == null) _avatarUI = new AvatarSelectUI(AvatarCatalog.CurrentSelection);
            _avatarUI.Visible = !_avatarUI.Visible;
            if (_avatarUI.Visible) AvatarCatalog.Scan();
        }

        public void ToggleDebugPanel()
        {
            if (!Plugin.Cfg.EnableDebugPanel.Value) return;
            _debugPanel.Visible = !_debugPanel.Visible;
        }

        public void CloseCompanionMenus()
        {
            if (_avatarUI != null) _avatarUI.Visible = false;
            if (_debugPanel != null) _debugPanel.Visible = false;
        }

        public void StartHostSession(int port)
        {
            Plugin.Logger.LogInfo("[Coop] Starting host via UI");
            // A notice describes one past attempt; carrying it into a new session tells the player to
            // fix something that is no longer true.
            ClearNotice();
            ResetJoinStreaming();
            Net.StartHost(port);
        }

        public void StartClientSession(string ip, int port)
        {
            Plugin.Logger.LogInfo("[Coop] Joining via UI to " + ip);
            ClearNotice();
            _clientProfileSavedOnShutdown = false;
            Net.StartClient(ip, port);
        }

        public void DisconnectSession(string reason)
        {
            Plugin.Logger.LogInfo("[Coop] Disconnect via UI: " + reason);
            // Persist the guest's character before tearing the session down, so its money/reputation survive.
            SaveClientProfileBeforeStop("disconnect:" + reason);
            SaveTransfer.Reset();
            Pause.Clear();
            // An in-flight StreamSaveToClient is now pointless (its peer is going away) and must not
            // leave the queue slot held for the next session.
            ResetJoinStreaming();
            Net.Stop();
            Missions.Clear();
            Sleep.Clear();
            Shop.Clear();
            WindTotem.Clear();
            Interactions.Clear();
            Items.Clear();
            Lights.Clear();
            Damage.Clear();
            Mooring.Clear();
            Anchor.Clear();
            Controls.Clear();
            Env.Clear();
            HostPause.Clear();
            CrestWater.Clear();
            NpcBoats.Clear();
            Boats.Clear();
            Players.Clear();
        }

    }
}
