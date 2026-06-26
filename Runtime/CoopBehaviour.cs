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

        private DebugOverlay _overlay;
        private bool _overlayVisible = true;

        private void Awake()
        {
            Instance = this;
            Net = new CoopNet(m => Plugin.Logger.LogInfo(m))
            {
                ModVersion = Plugin.Version,
                PlayerName = Plugin.Cfg.PlayerName.Value,
                // Stage 1 will replace this with the host's loaded save identity.
                WorldIdProvider = () => "",
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

            Net.OnAccepted += ack =>
                Plugin.Logger.LogInfo("[Coop] Подключение принято, NetId=" + ack.AssignedNetId);
            Net.OnClientReady += s =>
                Plugin.Logger.LogInfo("[Coop] Клиент готов: " + s.PlayerName);
            Net.OnGameMessage += OnGameMessage;
            Net.OnPlayerLeft += netId => Players.RemoveRemote(netId);

            _overlay = new DebugOverlay(Net);
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
            Controls.Tick(dt);
            Controls.ApplyClient(dt);
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
                case MsgType.ControlRequest:
                    Controls.OnControlRequest((ControlRequestMsg)msg, fromPeer);
                    break;
            }
        }

        private void OnGUI()
        {
            if (_overlayVisible) _overlay.Draw();
        }

        private void OnDestroy()
        {
            Controls?.Clear();
            Env?.Clear();
            Boats?.Clear();
            Players?.Clear();
            Net?.Stop();
        }

        private void OnApplicationQuit()
        {
            Net?.Stop();
        }

        private void HandleHotkeys()
        {
            var cfg = Plugin.Cfg;

            if (Input.GetKeyDown(cfg.OverlayKey.Value))
                _overlayVisible = !_overlayVisible;

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
                Controls.Clear();
                Env.Clear();
                Boats.Clear();
                Players.Clear();
            }
        }
    }
}
