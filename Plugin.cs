using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.IO;
using UnityEngine;

namespace SailwindCoop
{
    /// <summary>
    /// BepInEx entry point. Reads config, then attaches the persistent
    /// <see cref="Runtime.CoopBehaviour"/> that owns the network loop.
    /// </summary>
    [BepInPlugin(Guid, "Sailwind LAN Co-op", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.sailwind.coop";
        public const string Version = "0.1.5";

        internal static Plugin Instance { get; private set; }

        /// <summary>Gated log sink — silent unless the user switches logging on (F8 → Logging).
        /// Same method names as <see cref="ManualLogSource"/>, so call sites are unchanged.</summary>
        internal new static Runtime.CoopLog Logger { get; private set; }

        internal static CoopConfig Cfg { get; private set; }
        internal static string AvatarBundlePath => Path.Combine(Path.GetDirectoryName(Instance.Info.Location), "avatar.bundle");

        private void Awake()
        {
            Instance = this;
            Cfg = new CoopConfig(Config);
            // Off unless the user opted in: a normal session writes nothing to LogOutput.log.
            // BepInEx still records that this plugin loaded, so its presence stays visible.
            Logger = new Runtime.CoopLog(base.Logger, Cfg.EnableLogging.Value);
            // Honour the setting when it is changed outside the menu — editing the .cfg while the game
            // runs (BepInEx re-reads it) or via ConfigurationManager. Without this a user could follow
            // the config description, flip it, reproduce the bug, and still get an empty log.
            Cfg.EnableLogging.SettingChanged += (_, __) => Logger.Enabled = Cfg.EnableLogging.Value;
            Avatar.AvatarCatalog.Initialize();

            Logger.LogInfo("Sailwind LAN Co-op " + Version + " loading...");

            var go = new GameObject("SailwindCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runtime.CoopBehaviour>();

            Logger.LogInfo("Sailwind LAN Co-op ready. Use the in-game Co-op menu.");
        }
    }

    /// <summary>Typed wrapper over the BepInEx config file.</summary>
    public sealed class CoopConfig
    {
        public readonly ConfigEntry<string> ListenIp;
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<string> JoinIp;
        public readonly ConfigEntry<string> PlayerName;
        public readonly ConfigEntry<int> SnapshotHz;
        public readonly ConfigEntry<float> InterpDelayMs;
        public readonly ConfigEntry<float> AvatarVerticalOffset;
        public readonly ConfigEntry<float> HostAvatarVerticalOffset;

        // Server (host) tuning.
        public readonly ConfigEntry<int> MaxClients;
        public readonly ConfigEntry<int> DisconnectTimeoutMs;
        public readonly ConfigEntry<int> UpdateTimeMs;
        public readonly ConfigEntry<int> PingIntervalMs;

        // Client connection tuning.
        public readonly ConfigEntry<int> ConnectAttempts;
        public readonly ConfigEntry<int> ReconnectDelayMs;

        // Save sharing (host streams its world to the joining client; client overlays its profile).
        public readonly ConfigEntry<int> CoopSaveSlot;
        public readonly ConfigEntry<bool> ForceHostSaveOnJoin;
        public readonly ConfigEntry<bool> PauseHostOnJoin;

        // Debug.
        public readonly ConfigEntry<bool> EnableLogging;
        public readonly ConfigEntry<bool> EnableDebugPanel;
        public readonly ConfigEntry<KeyCode> MenuKey;

        public CoopConfig(ConfigFile c)
        {
            Port = c.Bind("Network", "Port", 7777, "Host UDP port.");
            ListenIp = c.Bind("Network", "ListenIp", "0.0.0.0", "IP/interface the host listens on (0.0.0.0 = all interfaces). Applied when starting the host: a specific address accepts connections only on that interface.");
            JoinIp = c.Bind("Network", "JoinIp", "127.0.0.1", "Host IP for the client to join.");
            PlayerName = c.Bind("Network", "PlayerName", "Player", "Displayed player name.");
            SnapshotHz = c.Bind("Network", "SnapshotHz", 20, "State snapshot send rate (Hz), Stage 1+.");
            InterpDelayMs = c.Bind("Network", "InterpDelayMs", 100f, "Interpolation buffer delay (ms), Stage 1+.");
            AvatarVerticalOffset = c.Bind("Avatar", "VerticalOffset", -0.6f, "Vertical offset of the visual bundle model relative to the networked player position. Negative values move the model down.");
            HostAvatarVerticalOffset = c.Bind("Avatar", "HostVerticalOffset", -0.6f, "Vertical offset of the host visual bundle model. Separate because the host root pose in Sailwind is usually higher than the client pose.");

            MaxClients = c.Bind("Server", "MaxClients", 4, "Maximum number of clients connected to the host at once (1 = single guest only). Applied on incoming connections.");
            DisconnectTimeoutMs = c.Bind("Server", "DisconnectTimeoutMs", 5000, "Timeout (ms) without packets from a peer before it is considered disconnected.");
            UpdateTimeMs = c.Bind("Server", "UpdateTimeMs", 15, "Internal network manager update interval (ms). Lower = more frequent polling/sending, higher CPU load.");
            PingIntervalMs = c.Bind("Server", "PingIntervalMs", 1000, "Ping interval (ms) for latency estimation and connection keepalive.");

            ConnectAttempts = c.Bind("Client", "ConnectAttempts", 10, "How many times the client tries to reach the host before reporting a connection error.");
            ReconnectDelayMs = c.Bind("Client", "ReconnectDelayMs", 500, "Delay (ms) between client connection attempts.");

            CoopSaveSlot = c.Bind("Save", "CoopSaveSlot", 5, "Save slot (0..5) where the client writes the received host world and loads from it. WARNING: the local save in this slot on the client is overwritten. Join from the main menu.");
            ForceHostSaveOnJoin = c.Bind("Save", "ForceHostSaveOnJoin", true, "When a client joins, the host makes a fresh save so the client receives the current world (economy/objects/position). Disable to send the latest autosave without forcing a save.");
            PauseHostOnJoin = c.Bind("Save", "PauseHostOnJoin", true, "While the client loads the host world, the host world is paused (timeScale=0, like the settings menu) so items/anchor/moorings/waves match the snapshot on the client. The pause is lifted when the client reports loaded, disconnects, or after a 120 s timeout.");

            EnableLogging = c.Bind("Debug", "EnableLogging", false, "Write this mod's diagnostics to BepInEx/LogOutput.log. Off by default: a normal session stays silent and costs no disk I/O. Hard errors are still written even when this is off, but only a handful of lines - just enough to show that something broke. Toggle in-game from the co-op menu (F8 -> Logging); turn it on BEFORE reproducing a problem, otherwise the log will contain nothing useful about the mod.");
            EnableDebugPanel = c.Bind("Debug", "EnableDebugPanel", false, "Developer/test panel for gold/spawn/reputation/world tools. Keep false for public builds.");
            MenuKey = c.Bind("UI", "MenuKey", KeyCode.F8, "Show/hide the co-op menu.");
        }
    }
}
