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
        public EnvironmentSync Env { get; private set; }
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

        private DebugOverlay _overlay;
        private bool _overlayVisible = false;
        private DebugPanel _debugPanel;
        private AvatarSelectUI _avatarUI;
        private CoopMenuUI _menuUI;
        private Harmony _harmony;
        private bool _clientProfileSavedOnShutdown;

        public bool OverlayVisible
        {
            get => _overlayVisible;
            set => _overlayVisible = value;
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
            };

            Env = new EnvironmentSync(Net);
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

            // F3 — intercept the game's interaction layer so a client's clicks reach the host.
            _harmony = new Harmony(Plugin.Guid);
            try { InteractionPatches.Apply(_harmony); MooringPatches.Apply(_harmony); BoatDamagePatches.Apply(_harmony); LightPatches.Apply(_harmony); ItemPatches.Apply(_harmony); ShopPatches.Apply(_harmony); SavePatches.Apply(_harmony); SleepPatches.Apply(_harmony); MissionPatches.Apply(_harmony); ShipyardPatches.Apply(_harmony); OceanPatches.Apply(_harmony); }
            catch (System.Exception e) { Plugin.Logger.LogError("[Coop] Failed to apply Harmony patches: " + e); }

            Net.OnAccepted += ack =>
                Plugin.Logger.LogInfo("[Coop] Connection accepted, NetId=" + ack.AssignedNetId);
            Net.OnClientReady += s =>
            {
                Plugin.Logger.LogInfo("[Coop] Client ready: " + s.PlayerName +
                                      ", avatar=" + (string.IsNullOrEmpty(s.SelectedAvatar) ? "(default)" : s.SelectedAvatar));
                // Remember the bundle file this client wants; used when their first PlayerState arrives.
                Players.RegisterRemoteAvatarFile(s.PlayerNetId, s.SelectedAvatar);
                // Freeze the world so the streamed snapshot stays true until this client is in.
                if (Plugin.Cfg.PauseHostOnJoin.Value && GameState.playing)
                    Pause.Hold(s.PlayerNetId);
                // Stream the host's world to the freshly-joined client so it loads into our world.
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
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.Cfg.MenuKey.Value))
                _menuUI.Toggle();

            Net.PollEvents();
            Pause.Tick();
            float dt = Time.deltaTime;
            // Boat first: the slaved deck moves, then players (children) settle on it.
            Boats.Tick(dt);
            Boats.ApplyRemote();
            Env.Tick(dt);
            Storms.Tick(dt);
            Sleep.Tick(dt);
            Missions.Tick(dt);
            Controls.Tick(dt);
            Controls.ApplyClient(dt);
            Anchor.Tick(dt);
            Anchor.ApplyRemote();
            Mooring.Tick(dt);
            Damage.Tick(dt);
            Lights.Tick(dt);
            Items.Tick(dt);
            Items.ApplyRemote();
            WindTotem.Tick(dt);
            Interactions.Tick(dt);
            Players.Tick(dt);
            Players.ApplyRemotes();
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

        /// <summary>Host side: when a client finishes the handshake, save the host's world fresh (so the
        /// client gets the up-to-date economy/objects/position), then stream the save file to that client.</summary>
        private IEnumerator StreamSaveToClient(LiteNetLib.NetPeer peer, uint netId)
        {
            // Give the handshake a frame to settle.
            yield return null;

            if (!GameState.playing)
            {
                // Without a loaded world SaveSlots.currentSlot points at an arbitrary slot —
                // never stream that to a client.
                Plugin.Logger.LogError("[Coop] Host is not in-game (save not loaded) - world was not sent to client. " +
                                       "Load a save before accepting clients.");
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
            if (bytes == null)
            {
                Plugin.Logger.LogError("[Coop] No host save available to send to client");
                Pause.Release(netId);
                yield break;
            }
            if (peer == null || peer.ConnectionState != LiteNetLib.ConnectionState.Connected)
            {
                Plugin.Logger.LogWarning("[Coop] Client disconnected before save transfer");
                Pause.Release(netId);
                yield break;
            }
            SaveTransfer.SendSaveTo(peer, bytes);
        }

        private void HandleAvatarChange(AvatarChangeMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            Plugin.Logger.LogInfo("[Coop] AvatarChange NetId=" + msg.NetId + " -> '" + msg.BundleFile + "'");
            Players.ApplyAvatarChange(msg.NetId, msg.BundleFile);
        }

        private void OnGUI()
        {
            if (_menuUI != null) _menuUI.Draw();
            if (_overlayVisible) _overlay.Draw();
            if (Plugin.Cfg.EnableDebugPanel.Value) _debugPanel.Draw();
            if (_avatarUI != null) _avatarUI.Draw();
        }

        private void OnDestroy()
        {
            SaveClientProfileBeforeStop("destroy");
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
            Boats?.Clear();
            Players?.Clear();
            Pause?.Clear();
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
            Net.StartHost(port);
        }

        public void StartClientSession(string ip, int port)
        {
            Plugin.Logger.LogInfo("[Coop] Joining via UI to " + ip);
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
            Boats.Clear();
            Players.Clear();
        }

    }
}
