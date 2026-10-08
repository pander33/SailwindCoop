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
        public ChartSync Charts { get; private set; }
        public DirtSync Dirt { get; private set; }
        public InteractionSync Interactions { get; private set; }
        public WindTotemSync WindTotem { get; private set; }
        public HouseDoorSync HouseDoors { get; private set; }
        public BoatTeleport Teleport { get; private set; }
        public ShopSync Shop { get; private set; }
        public WeatherStormSync Storms { get; private set; }
        public SleepSync Sleep { get; private set; }
        public MissionSync Missions { get; private set; }
        public WalletSync Wallet { get; private set; }
        public DiceSync Dice { get; private set; }
        public ShipyardSync Shipyard { get; private set; }
        public SaveTransferSync SaveTransfer { get; private set; }
        public ModSync Mods { get; private set; }
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
        private bool _clientCoopWorldLoaded;

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

            Net.PlayerGuid = CoopIdentity.Load(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "SailwindCoop")).ToString("N");
            Dice = new DiceSync(Net) { AllowStakes = Plugin.Cfg.DiceStakes.Value };

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
            new BoatAuthority(Net);
            Anchor = new AnchorSync(Net);
            Mooring = new MooringSync(Net);
            Damage = new BoatDamageSync(Net);
            Lights = new LightSync(Net);
            Items = new ItemSync(Net);
            Charts = new ChartSync(Net);
            Dirt = new DirtSync(Net);
            Interactions = new InteractionSync(Net);
            WindTotem = new WindTotemSync(Net);
            HouseDoors = new HouseDoorSync(Net);
            Teleport = new BoatTeleport(Net, Players)
            {
                Done = (ok, text) =>
                {
                    if (_menuUI == null) return;
                    _menuUI.Status = text;
                    if (ok) _menuUI.Visible = false;
                },
            };
            Shop = new ShopSync(Net);
            Storms = new WeatherStormSync(Net);
            Sleep = new SleepSync(Net);
            Missions = new MissionSync(Net);
            Wallet = new WalletSync(Net);
            Shipyard = new ShipyardSync(Net);
            Shipyard.Dispatch = OnGameMessage;
            Shipyard.RebuildHull = id => { ShipyardSync.ReleaseControls(id); Controls.InvalidateHull(id); Anchor.InvalidateHull(id); Mooring.InvalidateHull(id); Damage.InvalidateHull(id); Interactions.InvalidateHull(id); Dice.InvalidateHull(id); };
            SaveTransfer = new SaveTransferSync(Net) { CoopSlot = Plugin.Cfg.CoopSaveSlot.Value };
            SaveTransfer.OnSaveLoaded += () => _clientCoopWorldLoaded = true;
            Mods = new ModSync(Net)
            {
                // Stream the host's world to the client once it answered the mod check, so it loads
                // into our world. The join-freeze is taken INSIDE that coroutine, after the save is on
                // disk — freezing first would mean asking the game to save while its own clock is stopped.
                HostProceed = (peer, netId) => StartCoroutine(StreamSaveToClient(peer, netId)),
                ClientLeave = reason => DisconnectSession(reason),
                NeedsAttention = () => { if (_menuUI != null) _menuUI.Visible = true; },
            };
            Pause = new JoinPause();
            HostPause = new HostPauseSync(Net);

            // F3 — intercept the game's interaction layer so a client's clicks reach the host.
            _harmony = new Harmony(Plugin.Guid);
            // Both are otherwise silent: an unanswered request used to freeze its object for the session,
            // and an oversized snapshot used to fail every send.
            ItemStateGate.Expired = request =>
            {
                if (Net.Role == Role.Client && Plugin.Logger.ShouldReport(ref _gateExpiries))
                    Plugin.Logger.LogWarning("[Coop] role=Client request #" + request + " got no host reply within " +
                        ItemStateGate.PendingTimeoutMs + " ms; host state is accepted again (occurrence #" + _gateExpiries + ")");
            };
            PeerExt.Oversized = (type, bytes) =>
            {
                if (Plugin.Logger.ShouldReport(ref _oversizedPackets))
                    Plugin.Logger.LogWarning("[Coop] role=" + Net.Role + " " + type + " is " + bytes +
                        " bytes, above one unreliable datagram; sent reliably (occurrence #" + _oversizedPackets + ")");
            };
            // One set per call: a game update that removes a type faults only the set that names it.
            // Domain names match the ones each set reports itself.
            System.Action<string> patchFault = text => Plugin.Logger.LogError("[Coop] role=initializing " + text);
            PatchHealth.Install("Input origin", () => InputScopePatches.Apply(_harmony), patchFault, required: true);
            PatchHealth.Install("Special item visuals", () => ItemInstrumentPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Charts", () => ChartPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Dirt textures", () => DirtPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Shipyard refit", () => ShipyardRefitPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Wind orb carry", () => OrbCarryPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("House doors", () => HouseDoorPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Instrument poses", () => InstrumentPosePatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Anchor", () => AnchorPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Interactions", () => InteractionPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Mooring", () => MooringPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Damage", () => BoatDamagePatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Lights", () => LightPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Items", () => ItemPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Item results", () => ItemOperationPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Item simulation", () => ItemSimulationPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Shop", () => ShopPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Save", () => SavePatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Dice save lifecycle", () => DiceSavePatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Sleep", () => SleepPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Missions", () => MissionPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("Shipyard", () => ShipyardPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("NpcBoat", () => NpcBoatPatches.Apply(_harmony), patchFault);
            PatchHealth.Install("BoatActivity", () => BoatActivityPatches.Apply(_harmony), patchFault);

            PatchGuard.Run(() => {
                var signatures = InteractionActionCatalog.Inspect(typeof(GoPointerButton).Assembly);
                var actions = ItemActionCatalog.Inspect(typeof(ShipItem).Assembly);
                PatchHealth.Report("Input signatures", signatures);
                PatchHealth.Report("Item actions", actions);
                Plugin.Logger.LogInfo("[Coop] Input signatures: " + signatures.Detail);
                Plugin.Logger.LogInfo("[Coop] Item action coverage: " + actions.Detail);
            }, e => {
                PatchHealth.Set("Input signatures", PatchHealthState.Failed, e.Message);
                PatchHealth.Set("Item actions", PatchHealthState.Failed, "catalog inspection failed");
                Plugin.Logger.LogWarning("[Coop] role=initializing action catalog: " + e);
            });

            Net.OnAccepted += ack =>
                Plugin.Logger.LogInfo("[Coop] Connection accepted, NetId=" + ack.AssignedNetId);
            Net.OnClientReady += s =>
            {
                Plugin.Logger.LogInfo("[Coop] Client ready: " + s.PlayerName +
                                      ", avatar=" + (string.IsNullOrEmpty(s.SelectedAvatar) ? "(default)" : s.SelectedAvatar));
                // Remember the bundle file this client wants; used when their first PlayerState arrives.
                Players.RegisterRemoteAvatarFile(s.PlayerNetId, s.SelectedAvatar);
                // The world transfer starts when the client answers the mod list (Mods.HostProceed).
                Mods.OnClientReady(s);
            };
            Net.OnGameMessage += OnGameMessage;
            _notifications = new CoopNotifications();
            Net.OnGameplayNotice += msg => _notifications.Add(msg, Net);
            Net.OnMemberGone += netId => Players.ForgetRemote(netId);
            Net.OnPlayerLeft += netId =>
            {
                Players.RemoveRemote(netId);
                Dice.PlayerLeft(netId);
                Items.ClearRemoteActor(netId);
                Anchor.ClearRemoteActor(netId);
                Mooring.ClearRemoteActor(netId);
                WindTotem.ClearRemoteActor(netId);
                Damage.ClearRemoteActor(netId);
                Sleep.ClearRemoteActor(netId);
                Wallet.ClearRemoteActor(netId);
                BoatAuthority.Instance?.ClearActor(netId);
                Mods.ClearRemoteActor(netId);
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
        private float _rosterTimer;
        private CoopNotifications _notifications;
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
                // Touches no game object: file chunks for a joining client and the download watchdog.
                new SyncStep("Mods.Tick", () => Mods.Tick()),
                new SyncStep("Pause.Tick", () => Pause.Tick()),
                new SyncStep("Shipyard.Tick", () => Shipyard.Tick(_dt)),
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
                new SyncStep("Sleep.Tick", () => Sleep.Tick(Time.unscaledDeltaTime)),
                new SyncStep("Missions.Tick", () => Missions.Tick(_dt)),
                new SyncStep("Wallet.Tick", () => Wallet.Tick()),
                new SyncStep("Controls.Tick", () => Controls.Tick(_dt)),
                new SyncStep("Controls.ApplyClient", () => Controls.ApplyClient(_dt)),
                new SyncStep("Anchor.Tick", () => Anchor.Tick(_dt)),
                new SyncStep("Anchor.ApplyRemote", () => Anchor.ApplyRemote()),
                new SyncStep("Mooring.Tick", () => Mooring.Tick(_dt)),
                new SyncStep("Damage.Tick", () => Damage.Tick(_dt)),
                new SyncStep("Lights.Tick", () => Lights.Tick(_dt)),
                new SyncStep("Items.Tick", () => Items.Tick(_dt)),
                new SyncStep("Items.ApplyRemote", () => Items.ApplyRemote()),
                new SyncStep("Charts.Tick", () => Charts.Tick(_dt)),
                new SyncStep("Dirt.Tick", () => Dirt.Tick(_dt)),
                new SyncStep("WindTotem.Tick", () => WindTotem.Tick(_dt)),
                new SyncStep("HouseDoors.Tick", () => HouseDoors.Tick(_dt)),
                new SyncStep("Interactions.Tick", () => Interactions.Tick(_dt)),
                // Before Players.Tick, so the new place goes out in this frame's pose.
                new SyncStep("Teleport.Tick", () => Teleport.Tick()),
                new SyncStep("Players.Tick", () => Players.Tick(_dt)),
                new SyncStep("Players.ApplyRemotes", () => Players.ApplyRemotes()),
                new SyncStep("Dice.Tick", () => Dice.Tick()),
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

            try
            {
                bool emotesAllowed = EmoteWheelAllowed();
                EmoteId picked = _emoteWheel.Update(Plugin.Cfg.EmoteKey.Value, emotesAllowed);
                if (picked == EmoteId.DiceTable) Dice.Input.BeginPlacement();
                else if (picked != EmoteId.None) Players.StartEmote(picked);

                // Подсказка о колесе — один раз за всё время, в первой сессии, где колесо доступно.
                if (emotesAllowed && !Plugin.Cfg.EmoteHintShown.Value && _notifications != null)
                {
                    _notifications.Add("Hold " + Plugin.Cfg.EmoteKey.Value + " to open the gesture wheel", 10f);
                    Plugin.Cfg.EmoteHintShown.Value = true;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Emote wheel failed", e, ref _emoteFailures);
            }

            try
            {
                bool handAllowed = EmoteWheelAllowed() && !_emoteWheel.IsOpen && Players.RemoteCount > 0 && !Wallet.Shared;
                _moneyHand.Update(Plugin.Cfg.GiveKey.Value, handAllowed, Net, Wallet, Players);
                if (handAllowed && WalletSync.Ready && !Plugin.Cfg.GiveHintShown.Value && _notifications != null)
                {
                    _notifications.Add("Look at a crewmate next to you and hold " + Plugin.Cfg.GiveKey.Value + " to hand them money", 10f);
                    Plugin.Cfg.GiveHintShown.Value = true;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Money hand failed", e, ref _moneyHandFailures);
            }

            try { TickSteamJoin(); }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Steam join request failed", e, ref _steamJoinFailures);
            }

            try { using (InteractionContext.Begin(InteractionSource.RemoteApply)) Net.PollEvents(); }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Net.PollEvents failed", e, ref _pollFailures);
            }

            // Connection lost in the host's world: save the guest profile while that world is loaded.
            if (_clientCoopWorldLoaded && Net.State != LinkState.Connected)
            {
                SaveClientProfileBeforeStop("connection lost");
                _clientCoopWorldLoaded = false;
            }

            if (Net.Role == Role.Host && Net.State == LinkState.Connected)
            {
                _rosterTimer += Time.unscaledDeltaTime;
                if (_rosterTimer >= 2f)
                {
                    _rosterTimer = 0f;
                    Net.BroadcastRoster(Players.LocalBoatIndex);
                }
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

        private int _gateExpiries, _oversizedPackets;

        private void OnGameMessage(MsgType type, INetMessage msg, LiteNetLib.NetPeer fromPeer)
        {
            if (Shipyard.Defer(type, msg, fromPeer)) return;
            switch (type)
            {
                case MsgType.DiceRequest:
                case MsgType.DiceState:
                case MsgType.DiceResult:
                case MsgType.DiceBaseline:
                case MsgType.DiceJournal:
                    Dice.Receive(msg, fromPeer); break;
                case MsgType.ModManifest:
                    Mods.OnManifest((ModManifestMsg)msg); break;
                case MsgType.ModSyncResult:
                    Mods.OnResult((ModSyncResultMsg)msg, fromPeer); break;
#if !THUNDERSTORE
                case MsgType.ModFileRequest:
                    Mods.OnFileRequest((ModFileRequestMsg)msg, fromPeer); break;
                case MsgType.ModFileChunk:
                    Mods.OnFileChunk((ModFileChunkMsg)msg); break;
                case MsgType.ModFileEnd:
                    Mods.OnFileEnd((ModFileEndMsg)msg); break;
#endif
                case MsgType.PlayerState:
                    Players.OnPlayerState((PlayerStateMsg)msg, fromPeer);
                    break;
                case MsgType.SessionRoster:
                    // Handled in CoopNet.
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
                case MsgType.AnchorRequest:
                    Anchor.OnAnchorRequest((AnchorRequestMsg)msg, fromPeer);
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
                case MsgType.HatchSnapshot:
                    Interactions.OnHatchSnapshot((HatchSnapshotMsg)msg, fromPeer);
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
                case MsgType.ItemOperationRequest:
                    Items.OnOperationRequest((ItemOperationRequestMsg)msg, fromPeer); break;
                case MsgType.ItemOperationResult:
                    Items.OnOperationResult((ItemOperationResultMsg)msg, fromPeer); break;
                case MsgType.WheelLockRequest:
                    Controls.OnWheelLockRequest((WheelLockRequestMsg)msg, fromPeer); break;
                case MsgType.MooringCarryRequest:
                    Mooring.OnCarryRequest((MooringCarryRequestMsg)msg, fromPeer); break;
                case MsgType.MooringCarryState:
                    Mooring.OnCarryState((MooringCarryStateMsg)msg, fromPeer); break;
                case MsgType.InstrumentRequest:
                    Items.OnInstrumentRequest((InstrumentRequestMsg)msg, fromPeer); break;
                case MsgType.InstrumentState:
                    Items.OnInstrumentState((InstrumentStateMsg)msg, fromPeer); break;
                case MsgType.OrbRequest:
                    WindTotem.OnOrbRequest((OrbRequestMsg)msg, fromPeer); break;
                case MsgType.HouseDoor:
                    HouseDoors.OnHouseDoor((HouseDoorMsg)msg, fromPeer); break;
                case MsgType.OrbState:
                    WindTotem.OnOrbState((OrbStateMsg)msg, fromPeer); break;
                case MsgType.DirtRequest:
                    Dirt.OnRequest((DirtRequestMsg)msg, fromPeer); break;
                case MsgType.DirtState:
                    Dirt.OnState((DirtStateMsg)msg, fromPeer); break;
                case MsgType.ChartRequest:
                    Charts.OnRequest((ChartRequestMsg)msg, fromPeer); break;
                case MsgType.ChartState:
                    Charts.OnState((ChartStateMsg)msg, fromPeer); break;
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
                case MsgType.SleepRequest:
                    Sleep.OnSleepRequest((SleepRequestMsg)msg, fromPeer);
                    break;
                case MsgType.SleepPresence:
                    Sleep.OnSleepPresence((SleepPresenceMsg)msg, fromPeer);
                    break;
                case MsgType.MissionJournal:
                    Missions.OnMissionJournal((MissionJournalMsg)msg, fromPeer);
                    break;
                case MsgType.MissionReward:
                    Missions.OnMissionReward((MissionRewardMsg)msg, fromPeer);
                    break;
                case MsgType.MoneyTransfer:
                    Wallet.OnMoneyTransfer((MoneyTransferMsg)msg, fromPeer);
                    break;
                case MsgType.MoneyOffer:
                    Wallet.OnMoneyOffer((MoneyOfferMsg)msg, fromPeer);
                    break;
                case MsgType.WalletState:
                    Wallet.OnWalletState((WalletStateMsg)msg);
                    break;
                case MsgType.WalletDelta:
                    Wallet.OnWalletDelta((WalletDeltaMsg)msg, fromPeer);
                    break;
                case MsgType.ShopTaken:
                    Shop.OnShopTaken((ShopTakenMsg)msg, fromPeer);
                    break;
                case MsgType.MissionAccept:
                    Missions.OnMissionAccept((MissionAcceptMsg)msg, fromPeer);
                    break;
                case MsgType.MissionAbandon:
                    Missions.OnMissionAbandon((MissionAbandonMsg)msg, fromPeer);
                    break;
                case MsgType.ResyncRequest:
                    if (Net.Role == Role.Host && Net.PlayerNetIdForPeer(fromPeer) != 0)
                    {
                        var resync = (ResyncRequestMsg)msg;
                        switch (resync.Domain)
                        {
                            case ResyncDomain.Controls: Controls.Resync(resync.BoatIndex); break;
                            case ResyncDomain.Anchor: Anchor.Resync(resync.BoatIndex); break;
                            case ResyncDomain.Mooring: Mooring.Resync(resync.BoatIndex); break;
                            case ResyncDomain.Damage: Damage.Resync(resync.BoatIndex); break;
                            case ResyncDomain.World: Storms.Resync(); WindTotem.ResyncOrbs(); Items.ResyncInstruments(); break;
                        }
                    }
                    break;
                case MsgType.MissionDeliver:
                    Missions.OnMissionDeliver((MissionDeliverMsg)msg, fromPeer);
                    break;
                case MsgType.MissionDeliverResult:
                    Missions.OnMissionDeliverResult((MissionDeliverResultMsg)msg, fromPeer);
                    break;
                case MsgType.RefitRequest:
                    Shipyard.OnRefitRequest((RefitRequestMsg)msg, fromPeer); break;
                case MsgType.RefitState:
                    Shipyard.OnRefitState((RefitStateMsg)msg, fromPeer); break;
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
                        Net.SetMemberState(netId, ((ClientWorldLoadedMsg)msg).Ok
                            ? MemberJoinState.Ready
                            : MemberJoinState.Failed);
                        if (((ClientWorldLoadedMsg)msg).Ok && netId != 0)
                        {
                            Shipyard.SendBaseline(fromPeer);
                            Interactions.SendInitialHatches(fromPeer);
                            Sleep.SendBaseline(fromPeer);
                            Missions.SendBaseline(fromPeer);
                            HouseDoors.SendBaseline(fromPeer);
                            Dice.SendBaseline(netId);
                            Net.BroadcastNotice(GameplayNoticeKind.PlayerReady, netId);
                        }
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
            Net.SetMemberState(netId, MemberJoinState.Queued);
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
            Net.SetMemberState(netId, MemberJoinState.ReceivingWorld);
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
                Net.SetMemberState(netId, MemberJoinState.Failed);
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
                Net.SetMemberState(netId, MemberJoinState.Failed);
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
            Net.SetMemberState(netId, MemberJoinState.LoadingWorld);
        }

        private void HandleAvatarChange(AvatarChangeMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            Plugin.Logger.LogInfo("[Coop] AvatarChange NetId=" + msg.NetId + " -> '" + msg.BundleFile + "'");
            Players.ApplyAvatarChange(msg.NetId, msg.BundleFile);
        }

        private CoopLog.Repeat _guiFailures;
        private CoopLog.Repeat _emoteFailures;
        private readonly EmoteWheelUI _emoteWheel = new EmoteWheelUI();
        private CoopLog.Repeat _moneyHandFailures;
        private readonly MoneyHandUI _moneyHand = new MoneyHandUI();

        /// <summary>
        /// Колесо жестов доступно только в сессии, в загруженном мире и вне других меню. Уже
        /// открытое колесо само выставляет inCursorMenu, поэтому для него этот флаг не считается.
        /// </summary>
        private bool EmoteWheelAllowed()
        {
            if (Net == null || Net.State != LinkState.Connected || Players == null) return false;
            if (!GameState.playing || GameState.currentlyLoading || GameState.inBed != null) return false;
            if (_menuUI != null && _menuUI.Visible) return false;
            return _emoteWheel.IsOpen || !GameState.inCursorMenu;
        }

        private void OnGUI()
        {
            // IMGUI runs this several times per frame; an escaping exception leaves Unity's GUI layout
            // stack unbalanced and cascades into unrelated "GUI Error" spam, so contain it here too.
            try
            {
                if (HostPause != null && HostPause.Frozen) DrawHostPausedBanner();
                if (_menuUI != null) _menuUI.Draw();
                Dice?.Input.DrawHint();
                _emoteWheel.Draw();
                _moneyHand.Draw(_emoteWheel);
                if (_notifications != null) _notifications.Draw();
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
            Mods?.Reset();
            Shipyard?.Clear();
            Missions?.Clear();
            Wallet?.Clear();
            Sleep?.Clear();
            Shop?.Clear();
            WindTotem?.Clear();
            Interactions?.Clear();
            Items?.Clear();
            Charts?.Clear();
            Dirt?.Clear();
            Lights?.Clear();
            Damage?.Clear();
            Mooring?.Clear();
            Anchor?.Clear();
            Controls?.Clear();
            Env?.Clear();
            Storms?.Clear();
            CrestWater?.Clear();
            NpcBoats?.Clear();
            Boats?.Clear();
            Players?.Clear();
            Pause?.Clear();
            HostPause?.Clear();
            Net?.Stop();
            SteamLink.Shutdown();
            _notifications?.Clear();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            SaveClientProfileBeforeStop("quit");
            Net?.Stop();
            SteamLink.Shutdown();
        }

        private void SaveClientProfileBeforeStop(string reason)
        {
            if (_clientProfileSavedOnShutdown) return;
            try
            {
                if (!_clientCoopWorldLoaded) return;
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

        private static bool SessionBlocked()
        {
            if (PatchHealth.Blocker == null) return false;
            Plugin.Logger.LogError("[Coop] role=None session refused: required patch set absent: " + PatchHealth.Blocker);
            Notice("Co-op is unavailable with this game version: " + PatchHealth.Blocker + ". See BepInEx/LogOutput.log.");
            return true;
        }

        /// <summary>Mods downloaded in this run are not loaded yet: a new join would only offer them
        /// again. The menu shows the restart notice; this keeps every join path behind it.</summary>
        private bool RestartPending()
        {
#if !THUNDERSTORE
            if (!Mods.RestartRequired) return false;
            Plugin.Logger.LogWarning("[Coop] role=None join refused: mods were installed, the game must be restarted first");
            if (_menuUI != null) _menuUI.Visible = true;
            return true;
#else
            return false;
#endif
        }

        private static void NoticeFaultedPatchSets()
        {
            if (PatchHealth.FaultedSets != null)
                Notice("Not synced in this session (patch failed): " + PatchHealth.FaultedSets + ". See BepInEx/LogOutput.log.");
        }

        public void StartHostSession(int port, bool steam = false)
        {
            if (SessionBlocked()) return;
            Plugin.Logger.LogInfo("[Coop] Starting host via UI" + (steam ? " (Steam + LAN)" : ""));
            TeardownSession("start-host", saveClientProfile: true);
            // A notice describes one past attempt; carrying it into a new session tells the player to
            // fix something that is no longer true.
            ClearNotice();
            if (steam) Net.StartSteamHost(port, Plugin.Cfg.SteamFriendsOnly.Value);
            else
            Net.StartHost(port);
            if (Net.Role == Role.Host) Mods.BeginHost();
            NoticeFaultedPatchSets();
        }

        public void StartClientSession(string ip, int port)
        {
            if (SessionBlocked() || RestartPending()) return;
            if (LeaveWorldFirst(() => StartClientSession(ip, port))) return;
            Plugin.Logger.LogInfo("[Coop] Joining via UI to " + ip);
            TeardownSession("start-client", saveClientProfile: true);
            ClearNotice();
            _clientProfileSavedOnShutdown = false;
            _clientCoopWorldLoaded = false;
            Mods.BeginClient();
            Net.StartClient(ip, port);
            NoticeFaultedPatchSets();
        }

        // "Join Game" in the Steam friends list, or an accepted Steam invite. Steam gives the host's id
        // to a running game through a callback, and to a game it starts as command line arguments.
        private const float SteamStartDelay = 3f;      // let the game finish its own start-up first
        private const float SteamJoinPatience = 120f;  // how long a request waits for the title screen
        private bool _steamStartChecked;
        private ulong _steamJoinHost;
        private float _steamJoinSince;
        private float _steamJoinNextTry;
        private CoopLog.Repeat _steamJoinFailures;

        private void TickSteamJoin()
        {
            float now = Time.realtimeSinceStartup;
            if (!_steamStartChecked && now >= SteamStartDelay)
            {
                _steamStartChecked = true;
                ulong launched = SteamJoinLink.Parse(System.Environment.GetCommandLineArgs());
                if (launched != 0UL)
                {
                    Plugin.Logger.LogInfo("[Coop] Started by Steam to join " + launched);
                    _steamJoinHost = launched;
                    _steamJoinSince = now;
                }
                // Steam can deliver a join request only to a game that talks to it. A player who uses
                // the Steam mode gets that from the start, not from the first opening of the menu.
                if (launched != 0UL || Plugin.Cfg.UseSteam.Value) SteamLink.EnsureReady();
            }

            ulong asked = SteamLink.TakeJoinRequest();
            if (asked != 0UL)
            {
                _steamJoinHost = asked;
                _steamJoinSince = now;
                _steamJoinNextTry = 0f;
            }
            if (_steamJoinHost == 0UL || now < _steamJoinNextTry) return;
            _steamJoinNextTry = now + 1f;

            ulong host = _steamJoinHost;
            if (Net.Role == Role.Host)
            {
                _steamJoinHost = 0UL;
                Notice("You are hosting. Stop the session (" + Plugin.Cfg.MenuKey.Value + ") before joining a friend through Steam.");
                return;
            }
            // From a loaded world the join leaves it first; the request waits for the title screen below.
            if (GameState.playing && !GameState.currentlyLoading && !_leavingWorld)
            {
                _steamJoinSince = now;
                LeaveWorldFirst(null);
                return;
            }
            if (_leavingWorld || GameState.currentlyLoading) return;
            // A game started by Steam is still on its way to the title screen.
            if (UnityEngine.Object.FindObjectOfType<StartMenu>() == null)
            {
                if (now - _steamJoinSince > SteamJoinPatience)
                {
                    _steamJoinHost = 0UL;
                    Notice("The Steam join request expired before the main menu appeared. Join from the co-op menu.");
                }
                return;
            }

            _steamJoinHost = 0UL;
            if (!SteamLink.EnsureReady())
            {
                Notice("Cannot join through Steam: " + SteamLink.Error);
                return;
            }
            Plugin.Cfg.UseSteam.Value = true;
            Plugin.Cfg.SteamJoinId.Value = host.ToString();
            // The same rule as the menu's Join: an untouched default name becomes the Steam name.
            string persona = SteamLink.MyName;
            if (Net.PlayerName == "Player" && !string.IsNullOrWhiteSpace(persona)) Net.PlayerName = persona.Trim();
            Plugin.Logger.LogInfo("[Coop] Joining " + host + " on a request from Steam");
            StartSteamClientSession(host);
            // Not started when co-op is blocked or a restart is pending; that case has its own notice.
            if (_menuUI != null && Net.Role == Role.Client) _menuUI.ShowSteamJoin(host);
        }

        public void StartSteamClientSession(ulong hostSteamId)
        {
            if (SessionBlocked() || RestartPending()) return;
            if (LeaveWorldFirst(() => StartSteamClientSession(hostSteamId))) return;
            Plugin.Logger.LogInfo("[Coop] Joining via UI over Steam to " + hostSteamId);
            TeardownSession("start-client", saveClientProfile: true);
            ClearNotice();
            _clientProfileSavedOnShutdown = false;
            _clientCoopWorldLoaded = false;
            Mods.BeginClient();
            Net.StartSteamClient(hostSteamId);
            NoticeFaultedPatchSets();
        }

        public void ReconnectSession(string ip, int port) => StartClientSession(ip, port);

        // The game has no way back to its title screen, and the host's world is loaded through that
        // screen. A join from a loaded world therefore starts the game's scenes over, exactly as the
        // game starts them itself, and repeats the join from the title screen.
        private bool _leavingWorld;

        /// <summary>True when the join was put off until the title screen is back.</summary>
        private bool LeaveWorldFirst(System.Action join)
        {
            if (_leavingWorld) return true;
            if (!GameState.playing && !GameState.currentlyLoading) return false;
            if (GameState.currentlyLoading)
            {
                Notice("The world is still loading. Join when it has finished.");
                return true;
            }
            _leavingWorld = true;
            StartCoroutine(LeaveWorld(join));
            return true;
        }

        private IEnumerator LeaveWorld(System.Action join)
        {
            // What can throw sits in methods of its own: a coroutine cannot yield inside a try block.
            if (!Guarded("Leaving the world", PrepareToLeaveWorld)) { _leavingWorld = false; yield break; }

            // An own world is saved first; the save starts at the end of this frame.
            yield return null;
            for (float t = 0f; t < 10f && SaveTransferSync.HostSaveBusy(); t += Time.unscaledDeltaTime) yield return null;

            if (!Guarded("Returning to the title screen", ReloadGameScenes)) { _leavingWorld = false; yield break; }

            bool title = false;
            for (float t = 0f; t < 90f && !title; t += Time.unscaledDeltaTime)
            {
                yield return null;
                title = UnityEngine.Object.FindObjectOfType<StartMenu>() != null;
            }
            _leavingWorld = false;
            if (!title)
            {
                Notice("Could not return to the title screen. Restart the game, then join.");
                Plugin.Logger.LogError("[Coop] The title screen did not appear within 90 s after leaving the world");
                yield break;
            }
            Plugin.Logger.LogInfo("[Coop] Back on the title screen, joining");
            ClearNotice();
            if (join != null) Guarded("Join after leaving the world", join);
        }

        private static bool Guarded(string what, System.Action action)
        {
            try { action(); return true; }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError("[Coop] " + what + " failed: " + e);
                Notice(what + " failed: " + e.Message);
                return false;
            }
        }

        private void PrepareToLeaveWorld()
        {
            Plugin.Logger.LogInfo("[Coop] Leaving the loaded world to join a session");
            TeardownSession("leave-world", saveClientProfile: true);
            Notice("Leaving this world to join...");
            // The co-op slot holds a copy of a host's world and is rewritten by the join; an own world is kept.
            if (SaveSlots.currentSlot != Mathf.Clamp(Plugin.Cfg.CoopSaveSlot.Value, 0, 5) && SaveLoadManager.instance != null)
                SaveLoadManager.instance.SaveGame(compressed: true);
        }

        // The game sets these while a world runs and never clears them, because it never leaves a world.
        private void ReloadGameScenes()
        {
            Time.timeScale = 1f;
            GameState.playing = GameState.justStarted = GameState.currentlyLoading = GameState.loadingBoatLocalItems = false;
            GameState.changingStartRegion = GameState.recovering = false;
            GameState.loadingScenes = 0;
            GameState.inBed = null;
            GameState.sleeping = GameState.eyesFullyClosed = GameState.justWokeUp = GameState.sleepingInTavern = GameState.waitingForShift = false;
            GameState.currentBoat = GameState.lastBoat = GameState.lastOwnedBoat = null;
            GameState.indoors = GameState.onRatlines = GameState.clickedRatlines = GameState.holdingSunCompass = false;
            GameState.currentShipyard = null; GameState.shipyardUpdatingOrder = false;
            GameState.lastVisitedPort = null; GameState.currentHouse = null;
            GameState.inPortMissionList = GameState.wasInSettingsMenu = false;
            GameState.loadedCargoIntoCart = GameState.unloadedCargoFromCart = false;
            GameState.inCursorMenu = _menuUI != null && _menuUI.Visible;
            BoatCamera.on = false;
            Dice.WorldChanging();
            // Scene 0 is the game's own first scene; it loads the sea, the title screen comes with it.
            UnityEngine.SceneManagement.SceneManager.LoadScene(0);
        }

        public void DisconnectSession(string reason)
        {
            Plugin.Logger.LogInfo("[Coop] Disconnect via UI: " + reason);
            TeardownSession("disconnect:" + reason, saveClientProfile: true);
        }

        private void TeardownSession(string reason, bool saveClientProfile)
        {
            if (saveClientProfile)
                SaveClientProfileBeforeStop(reason);
            SaveTransfer.Reset();
            Mods.Reset();
            Pause.Clear();
            // An in-flight StreamSaveToClient is now pointless (its peer is going away) and must not
            // leave the queue slot held for the next session.
            ResetJoinStreaming();
            Net.Stop();
            Dice.Clear();
            _notifications?.Clear();
            Shipyard.Clear();
            Missions.Clear();
            Wallet.Clear();
            Sleep.Clear();
            Shop.Clear();
            WindTotem.Clear();
            HouseDoors.Clear();
            Interactions.Clear();
            Items.Clear();
            Charts.Clear();
            Dirt.Clear();
            Lights.Clear();
            Damage.Clear();
            Mooring.Clear();
            Anchor.Clear();
            Controls.Clear();
            Env.Clear();
            Storms.Clear();
            HostPause.Clear();
            CrestWater.Clear();
            NpcBoats.Clear();
            Boats.Clear();
            Players.Clear();
            _rosterTimer = 0f;
        }

    }
}
