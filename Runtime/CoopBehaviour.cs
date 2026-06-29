using HarmonyLib;
using SailwindCoop.Avatar;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// F5 — the single MonoBehaviour that owns the network loop. Pumps
    /// <see cref="CoopNet.PollEvents"/> on the Unity main thread, handles hotkeys
    /// to start/stop, and hosts the diagnostic overlay. Stage 1+ Sync components
    /// are driven from here too.
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

        private DebugOverlay _overlay;
        private bool _overlayVisible = true;
        private DebugPanel _debugPanel;
        private AvatarSelectUI _avatarUI;
        private Harmony _harmony;

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

            // F3 — intercept the game's interaction layer so a client's clicks reach the host.
            _harmony = new Harmony(Plugin.Guid);
            try { InteractionPatches.Apply(_harmony); MooringPatches.Apply(_harmony); BoatDamagePatches.Apply(_harmony); LightPatches.Apply(_harmony); ItemPatches.Apply(_harmony); ShopPatches.Apply(_harmony); SavePatches.Apply(_harmony); SleepPatches.Apply(_harmony); MissionPatches.Apply(_harmony); ShipyardPatches.Apply(_harmony); }
            catch (System.Exception e) { Plugin.Logger.LogError("[Coop] Не удалось применить Harmony-патчи: " + e); }

            Net.OnAccepted += ack =>
                Plugin.Logger.LogInfo("[Coop] Подключение принято, NetId=" + ack.AssignedNetId);
            Net.OnClientReady += s =>
            {
                Plugin.Logger.LogInfo("[Coop] Клиент готов: " + s.PlayerName +
                                      ", avatar=" + (string.IsNullOrEmpty(s.SelectedAvatar) ? "(default)" : s.SelectedAvatar));
                // Remember the bundle file this client wants; used when their first PlayerState arrives.
                Players.RegisterRemoteAvatarFile(s.PlayerNetId, s.SelectedAvatar);
            };
            Net.OnGameMessage += OnGameMessage;
            Net.OnPlayerLeft += netId =>
            {
                Players.RemoveRemote(netId);
                Damage.ClearRemoteActor(netId);
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
        }

        private void Update()
        {
            HandleHotkeys();
            Net.PollEvents();
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
            }
        }

        private void HandleAvatarChange(AvatarChangeMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            Plugin.Logger.LogInfo("[Coop] AvatarChange NetId=" + msg.NetId + " -> '" + msg.BundleFile + "'");
            Players.ApplyAvatarChange(msg.NetId, msg.BundleFile);
        }

        private void OnGUI()
        {
            if (_overlayVisible) _overlay.Draw();
            if (Plugin.Cfg.EnableDebugPanel.Value) _debugPanel.Draw();
            if (_avatarUI != null) _avatarUI.Draw();
        }

        private void OnDestroy()
        {
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
            Net?.Stop();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            Net?.Stop();
        }

        private void HandleHotkeys()
        {
            var cfg = Plugin.Cfg;

            if (Input.GetKeyDown(cfg.AvatarSelectKey.Value))
            {
                if (_avatarUI == null) _avatarUI = new AvatarSelectUI(AvatarCatalog.CurrentSelection);
                _avatarUI.Visible = !_avatarUI.Visible;
                if (_avatarUI.Visible) AvatarCatalog.Scan();   // refresh the list every time it opens
            }

            if (Input.GetKeyDown(cfg.OverlayKey.Value))
                _overlayVisible = !_overlayVisible;

            // Live gate: read the config each frame so it can be flipped in-game without a restart.
            if (cfg.EnableDebugPanel.Value)
            {
                if (Input.GetKeyDown(cfg.DebugPanelKey.Value))
                    _debugPanel.Visible = !_debugPanel.Visible;
            }
            else if (_debugPanel.Visible)
            {
                _debugPanel.Visible = false;   // disabled while open → hide; re-enable starts hidden
            }

            if (Input.GetKeyDown(cfg.HostKey.Value))
            {
                Plugin.Logger.LogInfo("[Coop] Старт хоста по хоткею");
                Net.StartHost(cfg.Port.Value);
            }

            if (Input.GetKeyDown(cfg.JoinKey.Value))
            {
                Plugin.Logger.LogInfo("[Coop] Подключение по хоткею к " + cfg.JoinIp.Value);
                Net.StartClient(cfg.JoinIp.Value, cfg.Port.Value);
            }

            if (Input.GetKeyDown(cfg.DisconnectKey.Value))
            {
                Plugin.Logger.LogInfo("[Coop] Отключение по хоткею");
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
}
