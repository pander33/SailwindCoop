using SailwindCoop.Avatar;
using SailwindCoop.Net;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Mouse-driven in-game menu for the co-op session.
    /// </summary>
    public sealed class CoopMenuUI
    {
        private const float WindowWidth = 460f;
        private const float ButtonWidth = 116f;
        private const float ButtonHeight = 30f;
        private const float FieldHeight = 26f;
        private const float CaptionWidth = 64f;

        private readonly CoopBehaviour _coop;
        private readonly CoopNet _net;

        private bool _visible;
        private bool _cursorCaptured;
        private bool _previousCursorVisible;
        private bool _previousMouseLookEnabled;
        private bool _previousInCursorMenu;
        private CursorLockMode _previousLockState;
        private string _joinIp;
        private string _port;
        private string _playerName;
        private string _status = "";
        private SessionMemberInfo[] _drawRoster = new SessionMemberInfo[0];
        private string _viewUpdate = "";   // a crewmate runs a later mod version
        private uint _kickConfirmNetId;
        private float _kickConfirmUntil;
        private Vector2 _scroll;
        private bool _sharedWallet;
        private Sync.ModSyncView _modView;
#if !THUNDERSTORE
        private bool _modSharing;
#endif
        private bool _debugTools;
        private bool _logging;
#if !THUNDERSTORE
        private bool _restartPending;
        private string _restartMods = "";
#else
        private static readonly bool _restartPending = false;
#endif

        // Steam section. Everything the layout depends on is snapshotted once per Layout pass.
        private const int MaxFriendRows = 8;
        private bool _steamModeWanted;
        private bool _steamMode;
        private bool _steamInitTried;
        private bool _steamReady;
        private bool _steamFriendsOnly;
        private bool _hostingOverSteam;
        private string _steamError = "";
        private string _steamSelf = "";
        private ulong _steamMyId;
        private string _steamJoinId;
        private SteamFriendInfo[] _friends = new SteamFriendInfo[0];
        private float _friendsRefreshAt;

        // What the window is made of is decided once per Layout pass and kept for the Repaint that
        // follows: a control count that differs between the two passes is Unity's "Getting control
        // N's position in a group with only M controls".
        private enum Mode { Offline, Connecting, Hosting, Joined }
        private Mode _mode;
        private bool _settingsOpen;
        private bool _technicalSettingsOpen;
        private bool _viewConfirmWorldJoin;
        private bool _viewInWorld;
        private bool _viewAdvanced;
        private bool _viewLeavesHostCopy;
        private bool _viewReplacesOwnSave;
        private string _viewJoinFailure = "";
        private string _viewSavedTarget = "";
        // Games heard on the local network, snapshotted for the layout: each one is a row with a button.
        private LanHost[] _viewLanHosts = new LanHost[0];

        /// <summary>The join screen of the LAN tab is on screen: only then is the network asked.</summary>
        public bool WantsLanSearch => _visible && _mode == Mode.Offline && !_steamModeWanted && _net.Role == Role.None &&
                                      !_coop.LeavingWorld && !ConfirmWorldJoin;
        /// <summary>The port of the Port field, where a host of this crew most likely listens.</summary>
        public int SearchPort => int.TryParse(_port, out int port) ? port : 0;
        // Snapshotted: finding it is a system call, far too heavy for every IMGUI event.
        private string _viewHostAddress = "";
        private float _hostAddressAt;
        private bool _viewSettings;
        private bool _viewModsPrompt;
        private string _viewError = "";
        private string _viewStatus = "";
        private string _viewNotice = "";
        private float _contentHeight = 420f;
        private float _contentWidth;

        private bool _stylesReady;
        private GUIStyle _title;
        private GUIStyle _caption;
        private GUIStyle _text;
        private GUIStyle _muted;
        private GUIStyle _error;
        private GUIStyle _notice;
        private GUIStyle _card;
        private GUIStyle _button;
        private GUIStyle _primaryButton;
        private GUIStyle _dangerButton;
        private GUIStyle _smallButton;
        private GUIStyle _ghostButton;
        private GUIStyle _tab;
        private GUIStyle _tabOn;
        private GUIStyle _textField;
        private GUIStyle _pillIdle;
        private GUIStyle _pillBusy;
        private GUIStyle _pillGood;
        private GUIStyle _pillBad;
        private GUIStyle _cell;
        private GUIStyle _cellHead;
        private GUIStyle _cellGood;
        private GUIStyle _cellWarn;
        private GUIStyle _cellBad;
        private GUIStyle _row;
        private GUIStyle _rowAlt;
        private GUIStyle _alertBox;
        private GUIStyle _alertTitle;
        private GUIStyle _alertText;
        private Texture2D _backdropTex;
        private Texture2D _shadowTex;
        private Texture2D _windowTex;
        private Texture2D _borderTex;
        private Texture2D _lineTex;

        public CoopMenuUI(CoopBehaviour coop, CoopNet net)
        {
            _coop = coop;
            _net = net;
            _joinIp = Plugin.Cfg.JoinIp.Value;
            _port = Plugin.Cfg.Port.Value.ToString();
            _playerName = Plugin.Cfg.PlayerName.Value;
            _steamModeWanted = Plugin.Cfg.UseSteam.Value;
            _steamJoinId = Plugin.Cfg.SteamJoinId.Value ?? "";
        }

        public bool Visible
        {
            get => _visible;
            set => SetVisible(value);
        }

        /// <summary>The menu's own status line. Not <see cref="CoopBehaviour.Notice"/>: that one holds
        /// a failure the player still has to act on.</summary>
        public string Status { set => _status = value; }
        public bool ConfirmWorldJoin { get; set; }
        public bool JoinConfirmationAccepted { get; set; }

        /// <summary>The host joined last, as "Join again" names it; empty when there is none.</summary>
        private string SavedTargetLabel()
        {
            if (Plugin.Cfg.LastJoinWasSteam.Value)
            {
                if (!JoinPreferences.TryParseSteam(Plugin.Cfg.LastJoinSteamId.Value, _playerName, out JoinTarget steam)) return "";
                string name = Plugin.Cfg.LastJoinPlayerName.Value;
                return string.IsNullOrWhiteSpace(name) ? steam.Label : name.Trim() + " (Steam)";
            }
            // An empty address is "never joined", not the local machine.
            if (string.IsNullOrWhiteSpace(Plugin.Cfg.LastJoinAddress.Value)) return "";
            return JoinPreferences.TryParseLan(Plugin.Cfg.LastJoinAddress.Value, Plugin.Cfg.LastJoinPort.Value.ToString(),
                                               _playerName, out JoinTarget lan) ? lan.Label : "";
        }

        private void RefreshHostAddresses()
        {
            if (_mode != Mode.Hosting || _hostingOverSteam) return;
            if (_viewHostAddress.Length > 0 && Time.realtimeSinceStartup < _hostAddressAt) return;
            _hostAddressAt = Time.realtimeSinceStartup + 3f;
            // The address the system sends from: a UDP socket "connected" to an outside address is
            // given the local address of the default route, and nothing is sent. Used only when no
            // adapter below looks like a real network.
            string routed = "";
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect("8.8.8.8", 65530);
                    var local = probe.LocalEndPoint as IPEndPoint;
                    if (local != null && !IPAddress.IsLoopback(local.Address) && !local.Address.Equals(IPAddress.Any))
                        routed = local.Address.ToString();
                }
            }
            catch (System.Exception) { }   // no route out: a LAN without a gateway, decided below
            // A cable or Wi-Fi adapter with a gateway is a real network; tunnels and virtual switches
            // either are of another type or have no gateway.
            var network = new System.Collections.Generic.List<string>();
            var adapters = new System.Collections.Generic.List<string>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    NetworkInterfaceType type = adapter.NetworkInterfaceType;
                    bool physical = type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.Wireless80211 ||
                                    type == NetworkInterfaceType.GigabitEthernet || type == NetworkInterfaceType.FastEthernetT ||
                                    type == NetworkInterfaceType.FastEthernetFx || type == NetworkInterfaceType.Ethernet3Megabit;
                    bool gateway = false;
                    // Not every runtime reports gateways; without them the adapter is just "another one".
                    try
                    {
                        foreach (GatewayIPAddressInformation hop in properties.GatewayAddresses)
                            if (hop.Address.AddressFamily == AddressFamily.InterNetwork && !hop.Address.Equals(IPAddress.Any)) gateway = true;
                    }
                    catch (System.Exception) { }
                    foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        IPAddress address = unicast.Address;
                        if (IPAddress.IsLoopback(address) || address.AddressFamily != AddressFamily.InterNetwork) continue;
                        (physical && gateway ? network : adapters).Add(address.ToString());
                    }
                }
            }
            catch (System.Exception e)
            {
                // The list is a convenience; the port line above it is always shown.
                Plugin.Logger.LogWarning("[Coop] Network adapters not listed: " + e.Message);
            }
            _viewHostAddress = JoinPreferences.HostAddress(Plugin.Cfg.ListenIp.Value, network, routed, adapters, Plugin.Cfg.Port.Value);
        }

        public void Toggle()
        {
            SetVisible(!_visible);
        }

        public void Draw()
        {
            EnsureStyles();

            // Snapshot once per layout pass: the banner adds controls to the window.
            if (Event.current.type == EventType.Layout)
            {
#if !THUNDERSTORE
                bool pending = _coop.Mods.RestartRequired;
                if (pending && !_restartPending) _scroll = Vector2.zero; // the banner sits at the top
                _restartPending = pending;
                _restartMods = _coop.Mods.InstalledNames ?? "";
#endif
            }

            if (_visible)
            {
                ApplyCursorState();
                DrawWindow();
            }
            else if (_restartPending)
                DrawRestartReminder();
        }

        /// <summary>Mods were downloaded: the join stopped on purpose and nothing else on screen says
        /// why. Large, coloured, and with the one action that moves the player forward.</summary>
        private void DrawRestartBanner()
        {
#if !THUNDERSTORE
            GUILayout.BeginVertical(_alertBox);
            GUILayout.Label("RESTART THE GAME TO JOIN", _alertTitle);
            GUILayout.Label("The host's mods were downloaded: " + _restartMods + ".\n" +
                            "You are NOT connected yet - new mods load only when the game starts.\n\n" +
                            "1. Quit the game.\n2. Start it again.\n3. Open this menu (" + Plugin.Cfg.MenuKey.Value +
                            ") and press Join again.", _alertText);
            GUILayout.Space(4f);
            if (GUILayout.Button("Quit game now", _button, GUILayout.Height(ButtonHeight)))
            {
                Plugin.Logger.LogInfo("[Mods] role=Client quitting from the restart notice");
                Application.Quit();
            }
            GUILayout.EndVertical();
#endif
        }

        /// <summary>The same notice while the menu is closed, so it cannot be dismissed by accident.</summary>
        private void DrawRestartReminder()
        {
#if !THUNDERSTORE
            float w = Mathf.Min(560f, Screen.width - 20f);
            var rect = new Rect((Screen.width - w) * 0.5f, 18f, w, 58f);
            GUI.Box(rect, GUIContent.none, _alertBox);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 20f, 24f), "RESTART THE GAME TO JOIN", _alertTitle);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 30f, rect.width - 20f, 22f),
                      "Mods from the host are installed. Quit and start the game again (" + Plugin.Cfg.MenuKey.Value +
                      " for details).", _alertText);
#endif
        }

        private void Snapshot()
        {
            RefreshSteamView();
            _drawRoster = _net.RosterSnapshot;
            _viewUpdate = _net.NewerModVersion.Length == 0 ? ""
                : ModVersions.Notice(_net.NewerModVersionHolder, _net.NewerModVersion, Plugin.Version);
            _sharedWallet = _net.Role == Role.Host ? Plugin.Cfg.SharedWallet.Value : _coop.Wallet.Shared;
            _modView = _coop.Mods.BuildView();
#if !THUNDERSTORE
            _modSharing = Plugin.Cfg.ShareMods.Value;
#endif
            _debugTools = Plugin.Cfg.EnableDebugPanel.Value;
            _logging = Plugin.Logger.Enabled;

            bool busy = _coop.LeavingWorld || _coop.SavingBeforeJoin || _net.State == LinkState.Connecting || _net.State == LinkState.Handshaking ||
                        (_net.Role == Role.Client && _net.State == LinkState.Connected && !_coop.ClientJoined);
            _mode = busy ? Mode.Connecting
                  : _net.Role == Role.Host ? Mode.Hosting
                  : _net.Role == Role.Client && _net.State == LinkState.Connected ? Mode.Joined
                  : Mode.Offline;
            _viewSettings = _settingsOpen;
            // A joining player has to answer the mod question and watch the download: that part of
            // the mod section belongs to the join, not to the settings.
            _viewModsPrompt = _net.Role != Role.Host && (_modView.Deciding || !string.IsNullOrEmpty(_modView.Text));
            _viewError = _net.LastError ?? "";
            _viewStatus = _status ?? "";
            // Shown even with logging switched off — this is the only surface for an actionable failure.
            _viewNotice = CoopBehaviour.LastNotice ?? "";
            _viewConfirmWorldJoin = ConfirmWorldJoin;
            _viewLeavesHostCopy = _coop.PendingJoinLeavesHostCopy;
            _viewReplacesOwnSave = _coop.PendingJoinReplacesOwnSave;
            _viewJoinFailure = _coop.JoinFailure ?? "";
            _viewSavedTarget = SavedTargetLabel();
            _viewLanHosts = _coop.LanSearch.Running ? _coop.LanSearch.Hosts : new LanHost[0];
            RefreshHostAddresses();
            _viewInWorld = GameState.playing && !GameState.currentlyLoading;
            _viewAdvanced = _technicalSettingsOpen;
        }

        private void DrawWindow()
        {
            float w = Mathf.Min(WindowWidth, Screen.width - 20f);
            float maxHeight = Mathf.Max(200f, Screen.height - 80f);
            if (Event.current.type == EventType.Layout)
            {
                Snapshot();
                // The window is as tall as what it shows; the scroll bar takes its strip only when
                // the content does not fit the screen.
                bool scrolls = _contentHeight + 26f > maxHeight;
                _contentWidth = w - 28f - (scrolls ? 16f : 0f);
            }
            float h = Mathf.Min(_contentHeight + 26f, maxHeight);
            float x = Mathf.Max(10f, Screen.width - w - 18f);
            float y = 60f;
            var rect = new Rect(x, y, w, h);

            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _backdropTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x + 6f, rect.y + 6f, rect.width, rect.height), _shadowTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), _borderTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(rect, _windowTex, ScaleMode.StretchToFill);
            GUILayout.BeginArea(new Rect(x + 14f, y + 12f, w - 28f, h - 24f));
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.BeginVertical(GUILayout.Width(_contentWidth));

            DrawHeader();
            if (_restartPending)
            {
                DrawRestartBanner();
                GUILayout.Space(8f);
            }
            DrawSession();
            if (_viewModsPrompt) DrawModsPrompt();
            DrawCrew();
            DrawTeleport();
            DrawSettings();
            DrawMessages();

            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
                _contentHeight = GUILayoutUtility.GetLastRect().height;
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private float CardInner => _contentWidth - _card.padding.horizontal;

        /// <summary>Width of one of <paramref name="count"/> equal buttons filling a card row.</summary>
        private float Split(int count) => (CardInner - 4f * (count + 1) - 2f) / count;

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sailwind Co-op", _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label(StateText(), StatePill(), GUILayout.Width(104f), GUILayout.Height(22f));
            if (GUILayout.Button("X", _ghostButton, GUILayout.Width(26f), GUILayout.Height(22f)))
                SetVisible(false);
            GUILayout.EndHorizontal();
            Rect line = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(line, _lineTex, ScaleMode.StretchToFill);
            GUILayout.Space(8f);
        }

        private void DrawSession()
        {
            GUILayout.BeginVertical(_card);
            if (_viewConfirmWorldJoin) { DrawWorldJoinConfirmation(); GUILayout.EndVertical(); return; }
            // A failed attempt, not any lost link: a session that ran and then dropped is no failed
            // join, and its reason is in the message lines below.
            if (_mode == Mode.Offline && _viewJoinFailure.Length > 0)
            {
                DrawConnectionFailure();
                GUILayout.EndVertical();
                return;
            }
            switch (_mode)
            {
                case Mode.Offline: DrawSessionOffline(); break;
                case Mode.Connecting: DrawSessionConnecting(); break;
                case Mode.Hosting: DrawSessionHosting(); break;
                default: DrawSessionJoined(); break;
            }
            GUILayout.EndVertical();
        }

        private void DrawSessionOffline()
        {
            // One screen, no steps: every way in is one press from here. Host a game starts at once
            // with the remembered settings; what is rarely changed sits under Advanced.
            bool leaving = _coop.LeavingWorld;
            bool available = PatchHealth.Blocker == null && !leaving;
            bool canJoin = available && !_restartPending;
            bool steamUsable = !_steamMode || _steamReady;

            GUILayout.BeginHorizontal();
            GUILayout.Label("CO-OP", _caption);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("LAN", _steamMode ? _tab : _tabOn, GUILayout.Width(70f), GUILayout.Height(22f))) SetSteamMode(false);
            if (GUILayout.Button("Steam", _steamMode ? _tabOn : _tab, GUILayout.Width(70f), GUILayout.Height(22f))) SetSteamMode(true);
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", _muted, GUILayout.Width(CaptionWidth));
            _playerName = GUILayout.TextField(_playerName, 32, _textField, GUILayout.Height(FieldHeight));
            GUILayout.EndHorizontal();

            GUI.enabled = available && _viewInWorld && steamUsable;
            if (GUILayout.Button(_viewInWorld ? "Host a game" : "Host a game (load a save first)", _primaryButton,
                                 GUILayout.Height(ButtonHeight + 4f)))
                StartHost();
            GUI.enabled = true;

            if (_viewSavedTarget.Length > 0)
            {
                GUI.enabled = canJoin;
                if (GUILayout.Button("Join again: " + _viewSavedTarget, _button, GUILayout.Height(ButtonHeight))) JoinSavedTarget();
                GUI.enabled = true;
            }

            if (_steamMode && !_steamReady)
            {
                GUILayout.Label(string.IsNullOrEmpty(_steamError) ? "Starting Steam..." : _steamError, _muted);
                if (GUILayout.Button("Retry Steam", _smallButton, GUILayout.Width(ButtonWidth), GUILayout.Height(22f))) _steamInitTried = false;
            }
            else if (_steamMode)
            {
                DrawSteam(canJoin);
            }
            else
            {
                DrawLanHosts(canJoin);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Join", _muted, GUILayout.Width(CaptionWidth));
                _joinIp = GUILayout.TextField(_joinIp, _textField, GUILayout.Height(FieldHeight));
                GUI.enabled = canJoin;
                if (GUILayout.Button("Join", _primaryButton, GUILayout.Width(70f), GUILayout.Height(FieldHeight))) Join();
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.Label("Or type the host's address, as shown in the host's menu. Blank means this PC.", _muted);
            }
            // Enter is the Join button: it joins only when the button would.
            if (canJoin && steamUsable && Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                Join();
                Event.current.Use();
            }

            if (leaving) GUILayout.Label("Returning to the title screen to join...", _muted);
            else if (PatchHealth.Blocker != null) GUILayout.Label("Co-op is unavailable: " + PatchHealth.Blocker, _muted);
            else if (PatchHealth.FaultedSets != null) GUILayout.Label("Will not sync (patch failed): " + PatchHealth.FaultedSets, _muted);

            DrawTechnicalSettings();
        }

        private const float LanCrewColumn = 54f, LanAddressColumn = 118f, LanJoinColumn = 62f;

        /// <summary>
        /// The games that answered on the local network, each with its own Join. The list fills and
        /// empties by itself while this screen is open; the address field below stays for a host that
        /// does not answer (another network, a firewall, a build without the search).
        /// </summary>
        private void DrawLanHosts(bool canJoin)
        {
            GUILayout.Label("GAMES ON THIS NETWORK", _caption);
            if (_viewLanHosts.Length == 0)
            {
                GUILayout.Label("Looking for games... none found yet.", _muted);
                GUILayout.Space(4f);
                return;
            }
            for (int i = 0; i < _viewLanHosts.Length; i++)
            {
                LanHost host = _viewLanHosts[i];
                bool sameProtocol = host.ProtocolVersion == Protocol.Version;
                bool full = host.Players >= host.MaxPlayers;
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowAlt : _row);
                GUILayout.Label(string.IsNullOrEmpty(host.HostName) ? "Host" : host.HostName, _cell, GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));
                GUILayout.Label(host.Players + "/" + host.MaxPlayers, full ? _cellWarn : _cell, GUILayout.Width(LanCrewColumn));
                GUILayout.Label(host.Address, _cell, GUILayout.Width(LanAddressColumn));
                GUI.enabled = canJoin && sameProtocol && host.Accepting && !full;
                if (GUILayout.Button(!host.Accepting ? "Locked" : full ? "Full" : "Join", _smallButton,
                                     GUILayout.Width(LanJoinColumn), GUILayout.Height(20f)))
                    JoinLanHost(host);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (!sameProtocol)
                    GUILayout.Label("Cannot join " + (string.IsNullOrEmpty(host.HostName) ? "this host" : host.HostName) + ": " +
                                    ModVersions.ProtocolMismatch(host.ProtocolVersion, Protocol.Version, host.ModVersion) + ".", _notice);
            }
            GUILayout.Space(4f);
        }

        private void JoinLanHost(LanHost host)
        {
            // Through the address field, so the join is remembered and retried like a typed one.
            bool ownPort = int.TryParse(_port, out int fieldPort) && fieldPort == host.Port;
            _joinIp = ownPort ? host.Address : host.Address + ":" + host.Port;
            Join();
        }

        private void DrawTechnicalSettings()
        {
            if (GUILayout.Button(_technicalSettingsOpen ? "Advanced settings (hide)" : "Advanced settings (show)", _ghostButton, GUILayout.Height(25f)))
                _technicalSettingsOpen = !_technicalSettingsOpen;
            if (_viewAdvanced)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", _muted, GUILayout.Width(CaptionWidth));
                _port = GUILayout.TextField(_port, 5, _textField, GUILayout.Width(80f), GUILayout.Height(FieldHeight));
                GUILayout.EndHorizontal();
                if (_steamMode && _steamReady)
                {
                    DrawSteamSelf();
                    DrawSteamAdmission();
                }
                GUILayout.Label("Listen interface: " + Plugin.Cfg.ListenIp.Value + "    Guests: " + Plugin.Cfg.MaxClients.Value +
                                "    Save slot: " + Plugin.Cfg.CoopSaveSlot.Value, _muted);
            }
        }

        private void StartHost()
        {
            if (!TryApplyConnectionFields(out int port)) return;
            _coop.StartHostSession(port, _steamMode && _steamReady);
            // A refused start explains itself in the notice line.
            _status = _net.Role == Role.Host ? "Session opened" : "";
        }

        private void DrawWorldJoinConfirmation()
        {
            // Three different things are left behind, and the text says which: a host's world (not
            // saved, it is the host's), an own world (saved), or an own world sitting in the very
            // slot the join writes into (saved, then replaced).
            GUILayout.Label(_viewLeavesHostCopy ? "LEAVE AND JOIN?" : "SAVE AND JOIN?", _caption);
            GUILayout.Label("Join " + _coop.JoinDestination, _text);
            GUILayout.Label(_viewLeavesHostCopy
                ? "You will leave this host's world. Your character's progress is saved to your guest profile; the world itself belongs to its host and is not saved here."
                : _viewReplacesOwnSave
                    ? "This world is in save slot " + Plugin.Cfg.CoopSaveSlot.Value + ", the slot co-op uses for a host's world. It will be saved and then REPLACED by the host's world. One copy is kept next to the save as a .bak file until the next join."
                    : "Your current world will be saved and closed before connecting. The host's world goes into its own save slot, not over this one.",
                _viewReplacesOwnSave ? _error : _text);
            GUILayout.Label(_viewReplacesOwnSave
                ? "To keep this world, cancel and set Save/CoopSaveSlot in the config to a free slot."
                : "After leaving, this world is not reopened automatically if the connection fails.", _muted);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", _button, GUILayout.Width(Split(2)), GUILayout.Height(ButtonHeight))) _coop.CancelWorldJoin();
            if (GUILayout.Button(_viewLeavesHostCopy ? "Leave and join" : "Save and join", _primaryButton, GUILayout.Width(Split(2)), GUILayout.Height(ButtonHeight)))
            {
                ConfirmWorldJoin = false;
                JoinConfirmationAccepted = true;
            }
            GUILayout.EndHorizontal();
        }

        private void DrawSessionConnecting()
        {
            GUILayout.Label("SESSION", _caption);
            GUILayout.Label(_coop.JoinProgress.Length > 0 ? _coop.JoinProgress :
                _net.OverSteam ? "Connecting to " + _net.HostLabel + " over Steam (" + _net.TransportStatus + ")..."
                               : "Connecting to " + _coop.JoinDestination + "...", _text);
            GUILayout.Space(4f);
            GUI.enabled = !_coop.LeavingWorld && !_coop.SavingBeforeJoin && !GameState.currentlyLoading;
            if (GUILayout.Button("Cancel", _dangerButton, GUILayout.Width(Split(3)), GUILayout.Height(ButtonHeight)))
            {
                _coop.DisconnectSession("menu");
                _status = "Connection cancelled";
            }
            GUI.enabled = true;
            GUILayout.Label("Scene loading may block input. After leaving, cancellation does not restore your previous world.", _muted);
        }

        private void DrawSessionHosting()
        {
            GUILayout.Label("Your session is open", _caption);
            GUILayout.Label("Hosting on port " + Plugin.Cfg.Port.Value + (_net.AcceptingClients ? "" : " - locked"), _text);
            GUILayout.Label((!_net.OverSteam ? "LAN"
                             : _net.SteamFriendsOnly ? "Steam friends only (" + _net.TransportStatus + ")"
                             : "LAN and Steam (" + _net.TransportStatus + ")") +
                            ", guests: " + _net.PeerCount, _muted);
            if (_hostingOverSteam)
            {
                DrawSteamSelf();
                DrawSteamAdmission();
            }
            else
            {
                GUILayout.Label("Your LAN address (local network/VPN only):", _muted);
                GUILayout.BeginHorizontal();
                GUILayout.Label(_viewHostAddress, _text);
                if (GUILayout.Button("Copy address", _smallButton, GUILayout.Width(ButtonWidth), GUILayout.Height(22f)))
                {
                    GUIUtility.systemCopyBuffer = JoinPreferences.EndpointOf(_viewHostAddress);
                    _status = "LAN address copied";
                }
                GUILayout.EndHorizontal();
            }
            DrawWalletMode();
            GUILayout.Label("Guests: " + _net.PeerCount + " / " + Plugin.Cfg.MaxClients.Value, _muted);
            GUILayout.Space(4f);
            float half = Split(2);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_net.AcceptingClients ? "Lock session" : "Open session", _button,
                                 GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
            {
                _net.SetAcceptingClients(!_net.AcceptingClients);
                _status = _net.AcceptingClients ? "Session open" : "Session locked";
            }
            if (GUILayout.Button("End session", _dangerButton, GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
            {
                _coop.DisconnectSession("menu");
                _status = "Session stopped";
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>Host: whose money the crew spends. Both choices are always visible.</summary>
        private void DrawWalletMode()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Money", _text, GUILayout.Width(92f), GUILayout.Height(22f));
            if (GUILayout.Button("Personal", _sharedWallet ? _tab : _tabOn, GUILayout.Width(96f), GUILayout.Height(22f)))
                SetSharedWallet(false);
            if (GUILayout.Button("Shared", _sharedWallet ? _tabOn : _tab, GUILayout.Width(78f), GUILayout.Height(22f)))
                SetSharedWallet(true);
            GUILayout.EndHorizontal();
            GUILayout.Label(_sharedWallet ? "Everyone spends and earns your money; all see one balance."
                                          : "Each player has a wallet; mission rewards are divided equally.", _muted);
        }

        private void SetSharedWallet(bool shared)
        {
            if (Plugin.Cfg.SharedWallet.Value == shared) return;
            Plugin.Cfg.SharedWallet.Value = shared;   // the session follows on its next tick
            _status = shared ? "The crew now uses your money" : "Every player uses a personal wallet again";
        }

        private void DrawSessionJoined()
        {
            GUILayout.Label("SESSION", _caption);
            GUILayout.Label(_net.OverSteam ? "Connected to " + _net.HostLabel + " over Steam"
                                           : "Connected to " + _coop.JoinDestination, _text);
            GUILayout.Label("Joined", _muted);
            GUILayout.Label(_net.OverSteam ? _net.TransportStatus : "LAN", _muted);
            GUILayout.Label(_sharedWallet ? "Money: the crew shares the host's wallet. Your own money is kept for you."
                                          : "Money: personal wallets; mission rewards are divided equally.", _muted);
            GUILayout.Space(4f);
            if (GUILayout.Button("Disconnect", _dangerButton, GUILayout.Width(Split(2)), GUILayout.Height(ButtonHeight)))
            {
                _coop.DisconnectSession("menu");
                _status = "Session stopped";
            }
        }

        private void DrawConnectionFailure()
        {
            GUILayout.Label("COULD NOT JOIN", _caption);
            GUILayout.Label(_viewJoinFailure, _error);
            GUILayout.Label("Host: " + _coop.JoinDestination, _muted);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Back", _button, GUILayout.Width(Split(2)), GUILayout.Height(ButtonHeight)))
            {
                _status = "";
                _coop.DismissJoinFailure();
            }
            if (GUILayout.Button("Retry", _primaryButton, GUILayout.Width(Split(2)), GUILayout.Height(ButtonHeight))) _coop.RetryJoin();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Technical details (show)", _ghostButton, GUILayout.Height(24f))) _technicalSettingsOpen = !_technicalSettingsOpen;
            if (_viewAdvanced)
                GUILayout.Label("State: " + _net.State + "    Transport: " + (_net.OverSteam ? "Steam" : "LAN") +
                                "    Host: " + _net.HostLabel + "    Detail: " + _net.LastDisconnectReason, _muted);
        }

        private void DrawModsPrompt()
        {
            GUILayout.BeginVertical(_card);
            GUILayout.Label("MODS", _caption);
            GUILayout.Label(_modView.Text ?? "", _muted);
            if (_modView.Deciding)
            {
#if !THUNDERSTORE
                float third = Split(3);
                GUILayout.BeginHorizontal();
                GUI.enabled = _modView.CanDownload;
                if (GUILayout.Button(_modView.DownloadLabel, _primaryButton, GUILayout.Width(third), GUILayout.Height(ButtonHeight)))
                    _coop.Mods.Download();
                GUI.enabled = true;
#else
                float third = Split(2);
                GUILayout.BeginHorizontal();
#endif
                if (GUILayout.Button("Join anyway", _button, GUILayout.Width(third), GUILayout.Height(ButtonHeight)))
                    _coop.Mods.JoinAnyway();
                if (GUILayout.Button("Cancel", _dangerButton, GUILayout.Width(third), GUILayout.Height(ButtonHeight)))
                {
                    _coop.Mods.Cancel();
                    _status = "Join cancelled";
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private const float StatusColumn = 76f, PingColumn = 58f, BoatColumn = 62f, KickColumn = 56f;

        private void DrawCrew()
        {
            GUILayout.BeginVertical(_card);
            GUILayout.BeginHorizontal();
            GUILayout.Label("CREW", _caption);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_drawRoster.Length == 0 ? "" : _drawRoster.Length + (_drawRoster.Length == 1 ? " player" : " players"), _muted);
            GUILayout.EndHorizontal();

            if (_drawRoster.Length == 0)
            {
                GUILayout.Label(_net.Role == Role.None ? "No active session." : "Waiting for crew state...", _muted);
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginHorizontal(_row);
            GUILayout.Label("Player", _cellHead, GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));
            GUILayout.Label("Status", _cellHead, GUILayout.Width(StatusColumn));
            GUILayout.Label("Ping", _cellHead, GUILayout.Width(PingColumn));
            GUILayout.Label("Location", _cellHead, GUILayout.Width(BoatColumn));
            GUILayout.Label("", _cellHead, GUILayout.Width(KickColumn));
            GUILayout.EndHorizontal();

            for (int i = 0; i < _drawRoster.Length; i++)
            {
                SessionMemberInfo member = _drawRoster[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowAlt : _row);
                string tag = member.IsHost ? " (host)" : member.NetId == _net.MyNetId ? " (you)" : "";
                GUILayout.Label(member.Name + tag, _cell, GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));
                GUILayout.Label(MemberStateText(member.State), _cell, GUILayout.Width(StatusColumn));
                GUILayout.Label(PingText(member), PingStyle(member), GUILayout.Width(PingColumn));
                GUILayout.Label(BoatText(member.BoatIndex), _cell, GUILayout.Width(BoatColumn));

                bool canKick = _net.Role == Role.Host && !member.IsHost;
                if (canKick)
                {
                    bool confirming = _kickConfirmNetId == member.NetId && Time.realtimeSinceStartup < _kickConfirmUntil;
                    if (GUILayout.Button(confirming ? "Sure?" : "Kick", confirming ? _dangerButton : _smallButton,
                                         GUILayout.Width(KickColumn - 4f), GUILayout.Height(20f)))
                    {
                        if (!confirming)
                        {
                            _kickConfirmNetId = member.NetId;
                            _kickConfirmUntil = Time.realtimeSinceStartup + 4f;
                        }
                        else
                        {
                            _net.DisconnectPlayer(member.NetId, "You were removed by the host");
                            _kickConfirmNetId = 0;
                            _status = member.Name + " removed";
                        }
                    }
                }
                else GUILayout.Label("", _cell, GUILayout.Width(KickColumn));
                GUILayout.EndHorizontal();
            }
            if (_viewUpdate.Length > 0) GUILayout.Label(_viewUpdate, _muted);
            GUILayout.EndVertical();
        }

        private GUIStyle PingStyle(SessionMemberInfo member)
        {
            if (member.IsHost || member.PingMs < 0) return _cell;
            return member.PingMs < 90 ? _cellGood : member.PingMs < 200 ? _cellWarn : _cellBad;
        }

        private static string PingText(SessionMemberInfo member)
        {
            if (member.IsHost) return "host";
            return member.PingMs < 0 ? "—" : member.PingMs + " ms";
        }

        private static string MemberStateText(MemberJoinState state)
        {
            switch (state)
            {
                case MemberJoinState.Handshaking: return "Handshake";
                case MemberJoinState.Queued: return "Waiting";
                case MemberJoinState.ReceivingWorld: return "Receiving";
                case MemberJoinState.LoadingWorld: return "Loading";
                case MemberJoinState.Ready: return "Ready";
                case MemberJoinState.Failed: return "Failed";
                case MemberJoinState.CheckingMods: return "Mods";
                default: return "—";
            }
        }

        private static string BoatText(int boatIndex)
        {
            if (boatIndex < 0 || boatIndex == ushort.MaxValue) return "No boat";
            return "Boat " + boatIndex;
        }

        private void DrawTeleport()
        {
            GUI.enabled = _coop.Teleport.Available;
            if (GUILayout.Button("Teleport to boat", _button, GUILayout.Height(ButtonHeight + 2f)))
            {
                _coop.Teleport.Request();
                _status = "Teleporting to the boat";
            }
            GUI.enabled = true;
            GUILayout.Space(4f);
            GUILayout.Label("Hold " + Plugin.Cfg.EmoteKey.Value + " in a session to open the gesture wheel.", _muted);
            GUILayout.Space(6f);
        }

        private void DrawSettings()
        {
            if (GUILayout.Button(_viewSettings ? "Settings   (hide)" : "Settings   (show)", _ghostButton, GUILayout.Height(26f)))
                _settingsOpen = !_settingsOpen;
            if (!_viewSettings) return;

            GUILayout.Space(4f);
            float half = Split(2);

            GUILayout.BeginVertical(_card);
            GUILayout.Label("PLAYER", _caption);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Model: " + AvatarCatalog.DisplayNameFor(AvatarCatalog.CurrentSelection), _text);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Avatar", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(FieldHeight)))
            {
                AvatarCatalog.Scan();
                _coop.ToggleAvatarMenu();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(_card);
            GUILayout.BeginHorizontal();
            GUILayout.Label("MODS", _caption);
            GUILayout.FlexibleSpace();
#if !THUNDERSTORE
            GUI.enabled = _net.Role != Role.Client;
            if (GUILayout.Button(_modSharing ? "Sharing: ON" : "Sharing: off", _modSharing ? _tabOn : _tab,
                                 GUILayout.Width(ButtonWidth), GUILayout.Height(22f)))
            {
                Plugin.Cfg.ShareMods.Value = !_modSharing;
                _status = !_modSharing
                    ? "Joining players can now download your mods"
                    : "Joining players can no longer download your mods";
            }
            GUI.enabled = true;
#endif
            GUILayout.EndHorizontal();
            GUILayout.Label(string.IsNullOrEmpty(_modView.Text) ? "Mods are compared with the host when you join." : _modView.Text, _muted);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(_card);
            GUILayout.Label("DIAGNOSTICS", _caption);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_coop.OverlayVisible ? "Hide Status" : "Show Status", _button,
                                 GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
                _coop.OverlayVisible = !_coop.OverlayVisible;
            if (GUILayout.Button(_logging ? "Logging: ON" : "Logging: off", _logging ? _primaryButton : _button,
                                 GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
            {
                bool next = !_logging;
                Plugin.Logger.Enabled = next;
                Plugin.Cfg.EnableLogging.Value = next;   // persists to the config file
                _status = next
                    ? "Logging ON - reproduce the problem, then send BepInEx/LogOutput.log"
                    : "Logging off";
            }
            GUILayout.EndHorizontal();

            // Water dump: press on BOTH machines within a second or two, then diff the two files.
            // Deliberately not gated behind debug tools - it only reads state and writes one text
            // file, and it is the thing we ask a player to do when a boat looks wrong in the water.
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export report", _button, GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
                _status = CoopReport.Write(_coop);
            if (GUILayout.Button("Dump water state", _button, GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
                _status = WaterDump.Write(_coop);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = _debugTools;
            if (GUILayout.Button("Debug", _button, GUILayout.Width(half), GUILayout.Height(ButtonHeight)))
                _coop.ToggleDebugPanel();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(_logging
                ? "Logging is ON. Reproduce the problem, then send BepInEx/LogOutput.log."
                : "Logging is off. Turn it on BEFORE reproducing a problem - nothing is written to the log until you do.",
                _muted);
            GUILayout.Label(_debugTools ? "Debug tools are enabled." : "Debug tools are disabled in public mode.", _muted);
            GUILayout.EndVertical();
        }

        private void DrawMessages()
        {
            if (_viewError.Length > 0) GUILayout.Label(_viewError, _error);
            if (_viewNotice.Length > 0) GUILayout.Label(_viewNotice, _notice);
            if (_viewStatus.Length > 0) GUILayout.Label(_viewStatus, _muted);
        }

        private void RefreshSteamView()
        {
            if (!Plugin.Cfg.TransportChosen.Value)
            {
                _steamModeWanted = SteamLink.EnsureReady();
                Plugin.Cfg.UseSteam.Value = _steamModeWanted;
                Plugin.Cfg.TransportChosen.Value = true;
            }
            _steamMode = _steamModeWanted;
            // Snapshotted for the layout: the admission row adds controls to the hosting screen.
            _hostingOverSteam = _net.Role == Role.Host && _net.OverSteam;
            _steamFriendsOnly = Plugin.Cfg.SteamFriendsOnly.Value;
            if (!_steamMode) return;
            // Steam is started only here: on the first frame the menu is shown in Steam mode.
            if (!_steamInitTried)
            {
                _steamInitTried = true;
                SteamLink.EnsureReady();
                _friendsRefreshAt = 0f;
            }
            _steamReady = SteamLink.Ready;
            _steamError = SteamLink.Error;
            _steamFriendsOnly = Plugin.Cfg.SteamFriendsOnly.Value;
            if (!_steamReady || Time.realtimeSinceStartup < _friendsRefreshAt) return;
            _friendsRefreshAt = Time.realtimeSinceStartup + 2f;
            _steamMyId = SteamLink.MyId;
            _steamSelf = SteamLink.MyName;
            _friends = SteamLink.FriendsInGame();
        }

        /// <summary>A join started outside the menu ("Join Game" in Steam): show it as the menu's own.</summary>
        public void ShowSteamJoin(ulong hostId)
        {
            _steamModeWanted = true;
            _steamJoinId = hostId.ToString();
            _status = "Joining through Steam: " + (string.IsNullOrEmpty(_net.HostLabel) ? _steamJoinId : _net.HostLabel);
            Visible = true;
        }

        private void SetSteamMode(bool steam)
        {
            _steamModeWanted = steam;
            Plugin.Cfg.UseSteam.Value = steam;
            Plugin.Cfg.TransportChosen.Value = true;
            if (steam) _steamInitTried = false;
        }

        /// <summary>
        /// Who a Steam host lets in. Both choices are always visible, and the row is drawn before
        /// hosting and during it: the rule applies to a running session at once.
        /// </summary>
        private void DrawSteamAdmission()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Who can join", _muted, GUILayout.Width(CaptionWidth));
            if (GUILayout.Button("Friends only", _steamFriendsOnly ? _tabOn : _tab, GUILayout.Width(96f), GUILayout.Height(22f)))
                SetSteamFriendsOnly(true);
            if (GUILayout.Button("Anyone", _steamFriendsOnly ? _tab : _tabOn, GUILayout.Width(70f), GUILayout.Height(22f)))
                SetSteamFriendsOnly(false);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(_steamFriendsOnly ? "Steam friends only; joining by IP is refused."
                                              : "Anyone who knows your Steam ID, and LAN players by IP.", _muted);
        }

        private void SetSteamFriendsOnly(bool friendsOnly)
        {
            if (_steamFriendsOnly == friendsOnly) return;
            Plugin.Cfg.SteamFriendsOnly.Value = friendsOnly;
            _net.SetSteamFriendsOnly(friendsOnly);   // a running Steam session follows at once
            _status = friendsOnly ? "Hosting accepts Steam friends only; the LAN port is closed"
                                  : "Hosting accepts anyone: by Steam ID and on the LAN port";
        }

        /// <summary>
        /// Our own Steam ID with a copy button. Drawn before hosting and during it: a guest who is
        /// not a friend joins by this number.
        /// </summary>
        private void DrawSteamSelf()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("You: " + _steamSelf + " (" + _steamMyId + ")", _muted);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy Steam ID", _smallButton, GUILayout.Width(110f), GUILayout.Height(22f)))
            {
                GUIUtility.systemCopyBuffer = _steamMyId.ToString();
                _status = "Your Steam ID is in the clipboard";
            }
            GUILayout.EndHorizontal();
        }

        private void DrawSteam(bool canJoin)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host ID", _muted, GUILayout.Width(CaptionWidth));
            _steamJoinId = GUILayout.TextField(_steamJoinId, 20, _textField, GUILayout.Height(FieldHeight));
            GUI.enabled = canJoin;
            if (GUILayout.Button("Join", _primaryButton, GUILayout.Width(70f), GUILayout.Height(FieldHeight))) JoinSteamFromField();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(_friends.Length == 0
                ? "No Steam friends are in Sailwind right now. A friend not listed here can still be joined by Host ID."
                : "Steam friends in Sailwind:", _muted);
            int rows = Mathf.Min(_friends.Length, MaxFriendRows);
            for (int i = 0; i < rows; i++)
            {
                SteamFriendInfo friend = _friends[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowAlt : _row);
                GUILayout.Label(friend.Name, _cell, GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));
                GUILayout.Label(!friend.Hosting ? "in game" : friend.SameVersion ? "hosting" : "hosting, other version",
                                friend.Hosting && friend.SameVersion ? _cellGood : _cell, GUILayout.Width(136f));
                GUI.enabled = friend.Hosting && PatchHealth.Blocker == null && !_restartPending && !_coop.HasPendingWorldJoin;
                if (GUILayout.Button("Join", _smallButton, GUILayout.Width(KickColumn - 4f), GUILayout.Height(20f)))
                {
                    _steamJoinId = friend.Id.ToString();
                    JoinSteamFromField();
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private void JoinSteamFromField()
        {
            if (!JoinPreferences.TryParseSteam(_steamJoinId, _playerName, out JoinTarget target))
            {
                _status = "Enter the Steam ID of the host (17 digits) or pick a friend";
                return;
            }
            ApplyPlayerName(target.PlayerName);
            _steamJoinId = target.Address;
            Plugin.Cfg.SteamJoinId.Value = target.Address;
            ulong hostId = ulong.Parse(target.Address);
            _coop.StartSteamClientSession(hostId);
            _status = "Joining over Steam: " + _coop.JoinDestination;
        }

        private void Join()
        {
            if (_steamMode)
            {
                JoinSteamFromField();
                return;
            }
            if (!JoinPreferences.TryParseLan(_joinIp, _port, _playerName, out JoinTarget target))
            {
                _status = "Enter an IP address or a host name, and a port from 1 to 65535";
                return;
            }
            ApplyPlayerName(target.PlayerName);
            // The Port field is also the port this player hosts on. A port typed into the address
            // belongs to that host alone and stays in the address.
            bool ownPort = int.TryParse(_port, out int fieldPort) && fieldPort == target.Port;
            _joinIp = ownPort ? target.Address : target.Label;
            Plugin.Cfg.JoinIp.Value = _joinIp;
            if (ownPort) Plugin.Cfg.Port.Value = target.Port;
            _coop.StartClientSession(target.Address, target.Port);
            _status = "Joining " + target.Label;
        }

        private void JoinSavedTarget()
        {
            if (Plugin.Cfg.LastJoinWasSteam.Value)
            {
                if (!JoinPreferences.TryParseSteam(Plugin.Cfg.LastJoinSteamId.Value, _playerName, out JoinTarget target))
                {
                    _status = "The saved Steam join target is invalid";
                    return;
                }
                _steamJoinId = target.Address;
                _steamModeWanted = true;
                ApplyPlayerName(target.PlayerName);
                _coop.StartSteamClientSession(ulong.Parse(target.Address));
                _status = "Joining over Steam: " + _coop.JoinDestination;
                return;
            }
            string address = Plugin.Cfg.LastJoinAddress.Value;
            int port = Plugin.Cfg.LastJoinPort.Value;
            if (string.IsNullOrWhiteSpace(address) ||
                !JoinPreferences.TryParseLan(address, port.ToString(), _playerName, out JoinTarget lanTarget))
            {
                _status = "The saved LAN join target is invalid";
                return;
            }
            ApplyPlayerName(lanTarget.PlayerName);
            _coop.StartClientSession(lanTarget.Address, lanTarget.Port);
            _status = "Joining " + lanTarget.Label;
        }

        private void ApplyPlayerName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
            // An untouched default name is replaced by the Steam persona name when Steam is in use.
            if (_steamMode && _steamReady && name == "Player" && !string.IsNullOrWhiteSpace(_steamSelf))
                name = _steamSelf.Trim();
            Plugin.Cfg.PlayerName.Value = name;
            _net.PlayerName = name;
            _playerName = name;
        }

        private bool TryApplyConnectionFields(out int port)
        {
            port = 0;
            string ip = string.IsNullOrWhiteSpace(_joinIp) ? "127.0.0.1" : _joinIp.Trim();
            if (!int.TryParse(_port, out port) || port < 1 || port > 65535)
            {
                _status = "Invalid port";
                return false;
            }

            string name = string.IsNullOrWhiteSpace(_playerName) ? "Player" : _playerName.Trim();
            // An untouched default name is replaced by the Steam persona name when Steam is in use.
            if (_steamMode && _steamReady && name == "Player" && !string.IsNullOrWhiteSpace(_steamSelf))
                name = _steamSelf.Trim();
            Plugin.Cfg.JoinIp.Value = ip;
            Plugin.Cfg.Port.Value = port;
            Plugin.Cfg.PlayerName.Value = name;
            _net.PlayerName = name;
            _joinIp = ip;
            _port = port.ToString();
            _playerName = name;
            return true;
        }

        private string StateText()
        {
            switch (_net.State)
            {
                case LinkState.Idle: return "OFFLINE";
                case LinkState.Connecting: return "CONNECTING";
                case LinkState.Handshaking: return "HANDSHAKE";
                case LinkState.Connected: return _net.Role == Role.Host ? "HOSTING" : "CONNECTED";
                case LinkState.Rejected: return "REJECTED";
                case LinkState.Failed: return "FAILED";
                default: return _net.State.ToString().ToUpperInvariant();
            }
        }

        private GUIStyle StatePill()
        {
            switch (_net.State)
            {
                case LinkState.Connected: return _pillGood;
                case LinkState.Connecting:
                case LinkState.Handshaking: return _pillBusy;
                case LinkState.Rejected:
                case LinkState.Failed: return _pillBad;
                default: return _pillIdle;
            }
        }

        private void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            if (!visible && ConfirmWorldJoin) _coop.CancelWorldJoin();
            if (!visible)
                _coop.CloseCompanionMenus();
            _visible = visible;
            ApplyCursorState();
        }

        /// <summary>The world the menu was opened in is gone: what it remembered about the cursor belongs
        /// to that world. The title screen has a free cursor and no mouse look.</summary>
        public void WorldLeft()
        {
            if (!_cursorCaptured) return;
            _previousCursorVisible = true;
            _previousLockState = CursorLockMode.None;
            _previousMouseLookEnabled = false;
            _previousInCursorMenu = false;
        }

        private void ApplyCursorState()
        {
            if (_visible)
            {
                if (!_cursorCaptured)
                {
                    _previousCursorVisible = Cursor.visible;
                    _previousLockState = Cursor.lockState;
                    _previousMouseLookEnabled = MouseLook.MouseLookIsEnabled();
                    _previousInCursorMenu = GameState.inCursorMenu;
                    _cursorCaptured = true;
                }
                MouseLook.ToggleMouseLookAndCursor(newState: false);
                MouseLook.ToggleMouseLook(newState: false);
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                GameState.inCursorMenu = true;
            }
            else if (_cursorCaptured)
            {
                bool restoreGameplayInput = GameState.playing && !GameState.currentlyLoading;
                if (restoreGameplayInput)
                {
                    MouseLook.ToggleMouseLookAndCursor(newState: true);
                    MouseLook.ToggleMouseLook(newState: true);
                    GameState.inCursorMenu = false;
                }
                else if (_previousCursorVisible || _previousLockState == CursorLockMode.None || _previousInCursorMenu)
                {
                    Cursor.visible = _previousCursorVisible;
                    Cursor.lockState = _previousLockState;
                    GameState.inCursorMenu = _previousInCursorMenu;
                    MouseLook.ToggleMouseLook(_previousMouseLookEnabled);
                }
                else
                {
                    MouseLook.ToggleMouseLookAndCursor(newState: true);
                    MouseLook.ToggleMouseLook(_previousMouseLookEnabled);
                    GameState.inCursorMenu = _previousInCursorMenu;
                }
                _cursorCaptured = false;
            }
        }

        private Texture2D MakeBg(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            var cream = new Color(0.96f, 0.90f, 0.78f);
            var sand = new Color(0.74f, 0.70f, 0.62f);
            var gold = new Color(0.86f, 0.68f, 0.38f);
            var none = new RectOffset(0, 0, 0, 0);

            _backdropTex = MakeBg(new Color(0f, 0f, 0f, 0.40f));
            _shadowTex = MakeBg(new Color(0f, 0f, 0f, 0.55f));
            _windowTex = MakeBg(new Color(0.045f, 0.040f, 0.034f, 0.97f));
            _borderTex = MakeBg(new Color(0.55f, 0.43f, 0.27f, 0.95f));
            _lineTex = MakeBg(new Color(0.55f, 0.43f, 0.27f, 0.55f));
            var card = MakeBg(new Color(0.095f, 0.084f, 0.070f, 0.96f));
            var warm = MakeBg(new Color(0.24f, 0.19f, 0.14f, 0.98f));
            var warmHover = MakeBg(new Color(0.33f, 0.26f, 0.18f, 0.98f));
            var accent = MakeBg(new Color(0.62f, 0.45f, 0.20f, 0.98f));
            var accentHover = MakeBg(new Color(0.74f, 0.55f, 0.26f, 0.98f));
            var red = MakeBg(new Color(0.42f, 0.15f, 0.12f, 0.98f));
            var redHover = MakeBg(new Color(0.55f, 0.20f, 0.15f, 0.98f));
            var ghost = MakeBg(new Color(0.14f, 0.12f, 0.10f, 0.96f));
            var field = MakeBg(new Color(0.02f, 0.02f, 0.018f, 0.95f));
            var fieldFocus = MakeBg(new Color(0.07f, 0.06f, 0.045f, 0.98f));
            var rowAlt = MakeBg(new Color(1f, 1f, 1f, 0.035f));

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = cream }
            };
            _caption = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = gold }
            };
            _text = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = cream }
            };
            _muted = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = sand }
            };
            _error = new GUIStyle(_muted) { normal = { textColor = new Color(1f, 0.56f, 0.48f) } };
            _notice = new GUIStyle(_muted) { normal = { textColor = new Color(1f, 0.82f, 0.42f) } };
            _card = new GUIStyle(GUI.skin.box)
            {
                normal = { background = card },
                border = none,
                padding = new RectOffset(12, 12, 9, 11),
                margin = new RectOffset(0, 0, 0, 8)
            };

            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                border = none,
                normal = { background = warm, textColor = cream },
                hover = { background = warmHover, textColor = Color.white },
                active = { background = warmHover, textColor = Color.white }
            };
            _primaryButton = new GUIStyle(_button)
            {
                normal = { background = accent, textColor = Color.white },
                hover = { background = accentHover, textColor = Color.white },
                active = { background = accentHover, textColor = Color.white }
            };
            _dangerButton = new GUIStyle(_button)
            {
                normal = { background = red, textColor = new Color(1f, 0.88f, 0.82f) },
                hover = { background = redHover, textColor = Color.white },
                active = { background = redHover, textColor = Color.white }
            };
            _smallButton = new GUIStyle(_button) { fontSize = 12 };
            _ghostButton = new GUIStyle(_button)
            {
                fontSize = 12,
                normal = { background = ghost, textColor = sand },
                hover = { background = warm, textColor = cream },
                active = { background = warm, textColor = cream }
            };
            _tab = new GUIStyle(_ghostButton);
            _tabOn = new GUIStyle(_primaryButton) { fontSize = 12 };

            _textField = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13,
                border = none,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(7, 7, 3, 3),
                normal = { background = field, textColor = Color.white },
                hover = { background = field, textColor = Color.white },
                focused = { background = fieldFocus, textColor = Color.white }
            };

            _pillIdle = Pill(new Color(0.20f, 0.19f, 0.17f), sand);
            _pillBusy = Pill(new Color(0.36f, 0.27f, 0.08f), new Color(1f, 0.86f, 0.45f));
            _pillGood = Pill(new Color(0.12f, 0.27f, 0.19f), new Color(0.66f, 1f, 0.78f));
            _pillBad = Pill(new Color(0.38f, 0.13f, 0.11f), new Color(1f, 0.78f, 0.72f));

            _cell = new GUIStyle(_muted)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.MiddleLeft,
                margin = none,
                padding = new RectOffset(4, 4, 3, 3),
                normal = { textColor = cream }
            };
            _cellHead = new GUIStyle(_cell) { fontSize = 11, normal = { textColor = sand } };
            _cellGood = new GUIStyle(_cell) { normal = { textColor = new Color(0.62f, 0.95f, 0.72f) } };
            _cellWarn = new GUIStyle(_cell) { normal = { textColor = new Color(1f, 0.84f, 0.42f) } };
            _cellBad = new GUIStyle(_cell) { normal = { textColor = new Color(1f, 0.58f, 0.50f) } };
            _row = new GUIStyle { padding = new RectOffset(2, 2, 1, 1) };
            _rowAlt = new GUIStyle(_row) { normal = { background = rowAlt } };

            _alertBox = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeBg(new Color(0.46f, 0.30f, 0.04f, 0.97f)) },
                border = none,
                padding = new RectOffset(10, 10, 8, 10)
            };
            _alertTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.90f, 0.35f) }
            };
            _alertText = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
        }

        private GUIStyle Pill(Color background, Color text)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = MakeBg(background), textColor = text }
            };
        }
    }
}
