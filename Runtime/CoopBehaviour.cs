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
            Players.Tick(Time.deltaTime);
            Players.ApplyRemotes();
        }

        private void OnGameMessage(MsgType type, INetMessage msg, LiteNetLib.NetPeer fromPeer)
        {
            switch (type)
            {
                case MsgType.PlayerState:
                    Players.OnPlayerState((PlayerStateMsg)msg, fromPeer);
                    break;
            }
        }

        private void OnGUI()
        {
            if (_overlayVisible) _overlay.Draw();
        }

        private void OnDestroy()
        {
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
                Players.Clear();
            }
        }
    }
}
