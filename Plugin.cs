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
        public const string Version = "0.5.1";
        /// <summary>Which of the two builds this is; see the Edition property in SailwindCoop.csproj.</summary>
#if THUNDERSTORE
        public const string Edition = "Thunderstore";
#else
        public const string Edition = "Full";
#endif

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

            Logger.LogInfo("Sailwind LAN Co-op " + Version + " (" + Edition + " edition) loading...");

            var go = new GameObject("SailwindCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            // On a game version the mod was not built for the component cannot be created, and
            // nothing but Player.log would say so.
            if (!Runtime.StartupNotice.Start(go, base.Logger)) return;

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

        // Server (host) tuning.
        public readonly ConfigEntry<int> MaxClients;
        public readonly ConfigEntry<bool> AnnounceOnLan;
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

        // Mod sharing (the host lists its mods before the world transfer; a client may download them).
        // The download entries do not exist in the Thunderstore edition: the feature is absent.
#if !THUNDERSTORE
        public readonly ConfigEntry<bool> ShareMods;
        public readonly ConfigEntry<bool> AllowModDownload;
#endif
        public readonly ConfigEntry<string> ModSyncExclude;

        // Steam transport (an alternative to typing an IP address; the LAN port keeps working).
        public readonly ConfigEntry<bool> UseSteam;
        public readonly ConfigEntry<bool> SteamFriendsOnly;
        public readonly ConfigEntry<string> SteamJoinId;
        public readonly ConfigEntry<string> LastJoinAddress;
        public readonly ConfigEntry<string> LastJoinSteamId;
        public readonly ConfigEntry<int> LastJoinPort;
        public readonly ConfigEntry<bool> LastJoinWasSteam;
        public readonly ConfigEntry<string> LastJoinPlayerName;
        public readonly ConfigEntry<bool> TransportChosen;

        // Debug.
        public readonly ConfigEntry<bool> EnableLogging;
        public readonly ConfigEntry<bool> EnableDebugPanel;
        public readonly ConfigEntry<KeyCode> MenuKey;
        public readonly ConfigEntry<KeyCode> EmoteKey;
        public readonly ConfigEntry<bool> EmoteHintShown;
        public readonly ConfigEntry<bool> SharedWallet;
        public readonly ConfigEntry<KeyCode> GiveKey;
        public readonly ConfigEntry<bool> DiceStakes;
        public readonly ConfigEntry<bool> DiceLitMaterials;
        public readonly ConfigEntry<bool> DiceCloseUp;
        public readonly ConfigEntry<KeyCode> DiceViewKey;
        public readonly ConfigEntry<bool> GiveHintShown;

        public CoopConfig(ConfigFile c)
        {
            // A config written by an earlier version already holds a transport: it is kept, and only a
            // first start picks one. Read before Bind adds the key. An unreadable file counts as
            // "chosen", the answer that changes nothing for the player.
            bool hadTransport = true;
            try
            {
                hadTransport = File.Exists(c.ConfigFilePath) && System.Text.RegularExpressions.Regex.IsMatch(
                    File.ReadAllText(c.ConfigFilePath), @"(?m)^\s*UseSteam\s*=");
            }
            catch (System.Exception) { }
            Port = c.Bind("Network", "Port", 7777, "Host UDP port.");
            ListenIp = c.Bind("Network", "ListenIp", "0.0.0.0", "IP/interface the host listens on (0.0.0.0 = all interfaces). Applied when starting the host: a specific address accepts connections only on that interface.");
            JoinIp = c.Bind("Network", "JoinIp", "127.0.0.1", "Host for the client to join: an IP address or a host name, optionally with :port.");
            PlayerName = c.Bind("Network", "PlayerName", "Player", "Displayed player name.");
            SnapshotHz = c.Bind("Network", "SnapshotHz", 20, "State snapshot send rate (Hz), Stage 1+.");
            InterpDelayMs = c.Bind("Network", "InterpDelayMs", 100f, "Interpolation buffer delay (ms), Stage 1+.");

            MaxClients = c.Bind("Server", "MaxClients", 4, "Maximum number of clients connected to the host at once (1 = single guest only). Applied on incoming connections.");
            AnnounceOnLan = c.Bind("Server", "AnnounceOnLan", true, "The host answers players of the local network who look for a game, so it appears in their co-op menu. Off = the game is joined by address only. Applied when starting the host.");
            DisconnectTimeoutMs = c.Bind("Server", "DisconnectTimeoutMs", 5000, "Timeout (ms) without packets from a peer before it is considered disconnected.");
            UpdateTimeMs = c.Bind("Server", "UpdateTimeMs", 15, "Internal network manager update interval (ms). Lower = more frequent polling/sending, higher CPU load.");
            PingIntervalMs = c.Bind("Server", "PingIntervalMs", 1000, "Ping interval (ms) for latency estimation and connection keepalive.");

            ConnectAttempts = c.Bind("Client", "ConnectAttempts", 10, "How many times the client tries to reach the host before reporting a connection error.");
            ReconnectDelayMs = c.Bind("Client", "ReconnectDelayMs", 500, "Delay (ms) between client connection attempts.");

            CoopSaveSlot = c.Bind("Save", "CoopSaveSlot", 5, "Save slot (0..5) where the client writes the received host world and loads from it. WARNING: the local save in this slot on the client is overwritten. Join from the main menu.");
            ForceHostSaveOnJoin = c.Bind("Save", "ForceHostSaveOnJoin", true, "When a client joins, the host makes a fresh save so the client receives the current world (economy/objects/position). Disable to send the latest autosave without forcing a save.");
            PauseHostOnJoin = c.Bind("Save", "PauseHostOnJoin", true, "While the client loads the host world, the host world is paused (timeScale=0, like the settings menu) so items/anchor/moorings/waves match the snapshot on the client. The pause is lifted when the client reports loaded, disconnects, or after a 120 s timeout.");

#if !THUNDERSTORE
            ShareMods = c.Bind("Mods", "ShareMods", true, "Host: let joining players download the mods they lack straight from this PC. On by default; toggle it in the co-op menu (F8 -> Mods -> Sharing). When off, joining players are still told which of your mods they are missing, but must install them themselves. Share only mods whose authors allow redistribution.");
            AllowModDownload = c.Bind("Mods", "AllowModDownload", true, "Client: allow downloading missing mods from a host that offers them. Nothing is ever installed without a click in the co-op menu, and the game must be restarted afterwards. Mods run code on your PC - download only from a host you trust.");
#endif
            ModSyncExclude = c.Bind("Mods", "ModSyncExclude", "gravydevsupreme.xunity.autotranslator,gravydevsupreme.xunity.resourceredirector", "Comma-separated plugin GUIDs that are never listed, compared or shared (personal mods such as a UI translator, or mods only the host needs). A folder containing an excluded plugin is skipped whole. Applied on both host and client.");

            UseSteam = c.Bind("Steam", "UseSteam", false, "The co-op menu opens in Steam mode: host for Steam friends and join them without an IP address. Steam must be running and own Sailwind. Switch it in the co-op menu (F8 -> Connection). With Steam/FriendsOnly off a Steam host also accepts LAN clients on its UDP port.");
            TransportChosen = c.Bind("UI", "TransportChosen", hadTransport, "A transport preference has been selected. Existing Steam/UseSteam configurations are respected.");
            SteamFriendsOnly = c.Bind("Steam", "FriendsOnly", false, "Steam host: accept only players on your Steam friends list, and nobody through the LAN port. Off by default: anyone can join, by your Steam ID and on the LAN port. A host started in LAN mode is not affected. A config written by an earlier version keeps its saved value.");
            SteamJoinId = c.Bind("Steam", "JoinId", "", "SteamID64 of the host joined last (17 digits). Filled in from the co-op menu.");
            LastJoinAddress = c.Bind("Join", "LastAddress", "", "Last successfully joined LAN host (IP address or host name). Set by the mod only after the world is ready.");
            LastJoinSteamId = c.Bind("Join", "LastSteamId", "", "Steam ID of the last successfully joined host.");
            LastJoinPort = c.Bind("Join", "LastPort", 0, "Port of the last successfully joined LAN host.");
            LastJoinWasSteam = c.Bind("Join", "LastWasSteam", false, "Transport of the last successfully joined host.");
            LastJoinPlayerName = c.Bind("Join", "LastPlayerName", "", "Steam display name of the last successfully joined Steam host, shown on the Join again button.");
            EnableLogging = c.Bind("Debug", "EnableLogging", false, "Write this mod's diagnostics to BepInEx/LogOutput.log. Off by default: a normal session stays silent and costs no disk I/O. Hard errors are still written even when this is off, but only a handful of lines - just enough to show that something broke. Toggle in-game from the co-op menu (F8 -> Logging); turn it on BEFORE reproducing a problem, otherwise the log will contain nothing useful about the mod.");
            EnableDebugPanel = c.Bind("Debug", "EnableDebugPanel", false, "Developer/test panel for gold/spawn/reputation/world tools. Keep false for public builds.");
            MenuKey = c.Bind("UI", "MenuKey", KeyCode.F8, "Show/hide the co-op menu.");
            EmoteKey = c.Bind("UI", "EmoteKey", KeyCode.G, "Hold to open the emote wheel, move the mouse to a gesture and release. Works only in a co-op session. Change it if the key is bound to something else in the game.");
            SharedWallet = c.Bind("Economy", "SharedWallet", false, "Host: the whole crew uses the host's money. Every purchase, sale and reward of any player changes the host's wallet, and everyone sees the same balance. Guests' own money stays in their profiles and comes back when they leave or when this is turned off. Off: every player has a personal wallet and mission rewards are divided equally. Toggle it in the co-op menu (F8) while hosting.");
            GiveKey = c.Bind("UI", "GiveKey", KeyCode.H, "Look at a crewmate standing next to you and hold this key to hold out money: the mouse wheel changes the amount, the middle mouse button changes the currency. The other player looks at you and presses the same key to take it. Change it if the key is bound to something else in the game.");
            DiceStakes = c.Bind("Dice", "AllowStakes", true, "Host: allow players at a dice table to agree on a stake in the lobby (mouse wheel on the score board). Enabled by default; turn off to disable stakes. When the party ends, every loser pays the stake to the winner from a personal wallet. Not available with the shared wallet.");
            DiceLitMaterials = c.Bind("Dice", "LitMaterials", true, "The dice table uses a material of the game that reacts to light. Turn it off if the table looks wrong: it then uses a plain material that does not react to light.");
            DiceCloseUp = c.Bind("Dice", "CloseUp", true, "Sitting down at a dice table brings the table close: it fills the screen, the cursor is free and the table is played with the mouse buttons. The other players see no difference. Turn off to open the close view only with the key (UI/DiceViewKey).");
            DiceViewKey = c.Bind("UI", "DiceViewKey", KeyCode.T, "Look at a dice table and press this key for the close view of it; press it again to step back. Change it if the key is bound to something else in the game.");
            GiveHintShown = c.Bind("UI", "GiveHintShown", false, "The one-time on-screen hint about handing money over has been shown. Set to false to see it again.");
            EmoteHintShown = c.Bind("UI", "EmoteHintShown", false, "The one-time on-screen hint about the emote wheel has been shown. Set to false to see it again in the next session.");
        }
    }
}
