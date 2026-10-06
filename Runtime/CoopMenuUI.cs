using SailwindCoop.Avatar;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Mouse-driven in-game menu for the co-op session.
    /// </summary>
    public sealed class CoopMenuUI
    {
        private const float ButtonWidth = 116f;
        private const float ButtonHeight = 30f;

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
        private uint _kickConfirmNetId;
        private float _kickConfirmUntil;
        private Vector2 _scroll;
        private Sync.ModSyncView _modView;
        private bool _modSharing;
        private bool _restartPending;
        private string _restartMods = "";

        // Steam section. Everything the layout depends on is snapshotted once per Layout pass.
        private const int MaxFriendRows = 8;
        private bool _steamModeWanted;
        private bool _steamMode;
        private bool _steamInitTried;
        private bool _steamReady;
        private bool _steamFriendsOnly;
        private string _steamError = "";
        private string _steamSelf = "";
        private ulong _steamMyId;
        private string _steamJoinId;
        private SteamFriendInfo[] _friends = new SteamFriendInfo[0];
        private float _friendsRefreshAt;

        private GUIStyle _window;
        private GUIStyle _title;
        private GUIStyle _label;
        private GUIStyle _muted;
        private GUIStyle _button;
        private GUIStyle _dangerButton;
        private GUIStyle _smallButton;
        private GUIStyle _textField;
        private GUIStyle _pill;
        private GUIStyle _crewCell;
        private GUIStyle _alertBox;
        private GUIStyle _alertTitle;
        private GUIStyle _alertText;
        private GUIStyle _backdrop;
        private Texture2D _backdropTex;
        private Texture2D _shadowTex;
        private Texture2D _windowTex;
        private Texture2D _borderTex;

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
                bool pending = _coop.Mods.RestartRequired;
                if (pending && !_restartPending) _scroll = Vector2.zero; // the banner sits at the top
                _restartPending = pending;
                _restartMods = _coop.Mods.InstalledNames ?? "";
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
        }

        /// <summary>The same notice while the menu is closed, so it cannot be dismissed by accident.</summary>
        private void DrawRestartReminder()
        {
            float w = Mathf.Min(560f, Screen.width - 20f);
            var rect = new Rect((Screen.width - w) * 0.5f, 18f, w, 58f);
            GUI.Box(rect, GUIContent.none, _alertBox);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 20f, 24f), "RESTART THE GAME TO JOIN", _alertTitle);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 30f, rect.width - 20f, 22f),
                      "Mods from the host are installed. Quit and start the game again (" + Plugin.Cfg.MenuKey.Value +
                      " for details).", _alertText);
        }

        private void DrawWindow()
        {
            float w = 430f;
            float h = Mathf.Min(680f, Screen.height - 80f);
            float x = Mathf.Clamp(Screen.width - w - 18f, 10f, Screen.width - w - 10f);
            float y = 60f;
            var rect = new Rect(x, y, w, h);

            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _backdropTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x + 8f, rect.y + 8f, rect.width, rect.height), _shadowTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), _borderTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(rect, _windowTex, ScaleMode.StretchToFill);
            GUI.Box(rect, GUIContent.none, _window);
            GUILayout.BeginArea(new Rect(x + 14f, y + 12f, w - 28f, h - 24f));
            _scroll = GUILayout.BeginScrollView(_scroll, false, true);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Sailwind Co-op", _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label(StateText(), _pill, GUILayout.Width(128f), GUILayout.Height(24f));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            if (_restartPending)
            {
                DrawRestartBanner();
                GUILayout.Space(8f);
            }
            DrawIdentity();
            GUILayout.Space(8f);
            DrawConnection();
            GUILayout.Space(8f);
            DrawMods();
            GUILayout.Space(8f);
            DrawCrew();
            GUILayout.Space(10f);
            DrawActions();
            GUILayout.Space(10f);
            DrawTools();
            GUILayout.FlexibleSpace();

            // Emitted unconditionally (empty string when unset). A Label that appears only when its text
            // is non-empty changes the control count between the Layout pass and the event pass that set
            // it — Unity's "Getting control N's position in a group with only M controls", thrown here
            // between BeginArea and EndArea, which leaves the GUI clip stack unbalanced for that frame.
            GUILayout.Label(_net.LastError ?? "", _muted);
            GUILayout.Label(_status ?? "", _muted);
            // Shown even with logging switched off — this is the only surface for an actionable failure.
            GUILayout.Label(CoopBehaviour.LastNotice ?? "", _muted);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawIdentity()
        {
            GUILayout.Label("Player", _label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", _muted, GUILayout.Width(72f));
            _playerName = GUILayout.TextField(_playerName, _textField, GUILayout.Height(26f));
            if (GUILayout.Button("Apply", _smallButton, GUILayout.Width(92f), GUILayout.Height(26f)))
            {
                string name = string.IsNullOrWhiteSpace(_playerName) ? "Player" : _playerName.Trim();
                Plugin.Cfg.PlayerName.Value = name;
                _net.PlayerName = name;
                _playerName = name;
                _status = "Player name updated";
            }
            GUILayout.EndHorizontal();

            bool canReconnect = _net.HasConnectedSuccessfully &&
                                (_net.State == LinkState.Idle || _net.State == LinkState.Failed || _net.State == LinkState.Rejected);
            GUILayout.BeginHorizontal();
            GUI.enabled = canReconnect && !_restartPending;
            if (GUILayout.Button("Reconnect", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                if (GameState.playing)
                    _status = "Return to the main menu before reconnecting";
                else if (_steamMode)
                    JoinSteamFromField();
                else if (TryApplyConnectionFields(out int reconnectPort))
                {
                    _coop.ReconnectSession(_joinIp.Trim(), reconnectPort);
                    _status = "Reconnecting to " + _joinIp.Trim() + ":" + reconnectPort;
                }
            }
            GUI.enabled = _net.Role == Role.Host;
            if (GUILayout.Button(_net.AcceptingClients ? "Lock session" : "Open session", _button,
                                 GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                _net.SetAcceptingClients(!_net.AcceptingClients);
                _status = _net.AcceptingClients ? "Session open" : "Session locked";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawMods()
        {
            // Snapshot once per layout pass: the phase changes from network packets and from the
            // buttons below, and the control count must not differ between Layout and Repaint.
            if (Event.current.type == EventType.Layout)
            {
                _modView = _coop.Mods.BuildView();
                _modSharing = Plugin.Cfg.ShareMods.Value;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Mods", _label);
            GUILayout.FlexibleSpace();
            GUI.enabled = _net.Role != Role.Client;
            if (GUILayout.Button(_modSharing ? "Sharing: ON" : "Sharing: off", _smallButton,
                                 GUILayout.Width(ButtonWidth), GUILayout.Height(22f)))
            {
                Plugin.Cfg.ShareMods.Value = !_modSharing;
                _status = !_modSharing
                    ? "Joining players can now download your mods"
                    : "Joining players can no longer download your mods";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(_modView.Text ?? "", _muted);
            if (!_modView.Deciding) return;

            GUILayout.BeginHorizontal();
            GUI.enabled = _modView.CanDownload;
            if (GUILayout.Button(_modView.DownloadLabel, _button, GUILayout.Width(ButtonWidth + 44f), GUILayout.Height(ButtonHeight)))
                _coop.Mods.Download();
            GUI.enabled = true;
            if (GUILayout.Button("Join anyway", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                _coop.Mods.JoinAnyway();
            if (GUILayout.Button("Cancel", _dangerButton, GUILayout.Width(ButtonWidth - 20f), GUILayout.Height(ButtonHeight)))
            {
                _coop.Mods.Cancel();
                _status = "Join cancelled";
            }
            GUILayout.EndHorizontal();
        }

        private void DrawCrew()
        {
            // Row count must not change between Layout and Repaint.
            if (Event.current.type == EventType.Layout)
                _drawRoster = _net.RosterSnapshot;

            GUILayout.Label("Crew", _label);
            if (_drawRoster.Length == 0)
            {
                GUILayout.Label(_net.Role == Role.None ? "No active session." : "Waiting for crew state...", _muted);
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Player", _crewCell, GUILayout.Width(116f));
            GUILayout.Label("Status", _crewCell, GUILayout.Width(72f));
            GUILayout.Label("Ping", _crewCell, GUILayout.Width(64f));
            GUILayout.Label("Location", _crewCell, GUILayout.Width(72f));
            GUILayout.EndHorizontal();

            for (int i = 0; i < _drawRoster.Length; i++)
            {
                SessionMemberInfo member = _drawRoster[i];
                GUILayout.BeginHorizontal();
                string role = member.IsHost ? " (host)" : "";
                GUILayout.Label(member.Name + role, _crewCell, GUILayout.Width(116f));
                GUILayout.Label(MemberStateText(member.State), _crewCell, GUILayout.Width(72f));
                GUILayout.Label(PingText(member), _crewCell, GUILayout.Width(64f));
                GUILayout.Label(BoatText(member.BoatIndex), _crewCell, GUILayout.Width(72f));

                bool canKick = _net.Role == Role.Host && !member.IsHost;
                if (canKick)
                {
                    bool confirming = _kickConfirmNetId == member.NetId && Time.realtimeSinceStartup < _kickConfirmUntil;
                    if (GUILayout.Button(confirming ? "Confirm" : "Kick", confirming ? _dangerButton : _smallButton,
                                         GUILayout.Width(58f), GUILayout.Height(22f)))
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
                GUILayout.EndHorizontal();
            }
        }

        private static string PingText(SessionMemberInfo member)
        {
            if (member.IsHost) return "local";
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

        private void DrawConnection()
        {
            // The mode, Steam's state and the friends list decide which controls exist below, so they
            // are read once per Layout pass and stay fixed for the Repaint that follows.
            if (Event.current.type == EventType.Layout)
                RefreshSteamView();

            bool idle = _net.Role == Role.None && _net.State != LinkState.Connecting && _net.State != LinkState.Handshaking;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Connection", _label);
            GUILayout.FlexibleSpace();
            GUI.enabled = idle;
            if (GUILayout.Button(_steamMode ? "LAN" : "[ LAN ]", _smallButton, GUILayout.Width(76f), GUILayout.Height(22f)))
                SetSteamMode(false);
            if (GUILayout.Button(_steamMode ? "[ Steam ]" : "Steam", _smallButton, GUILayout.Width(76f), GUILayout.Height(22f)))
                SetSteamMode(true);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!_steamMode)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Host IP", _muted, GUILayout.Width(72f));
                _joinIp = GUILayout.TextField(_joinIp, _textField, GUILayout.Height(26f));
                GUILayout.Label("Port", _muted, GUILayout.Width(38f));
                _port = GUILayout.TextField(_port, _textField, GUILayout.Width(66f), GUILayout.Height(26f));
                GUILayout.EndHorizontal();
            }
            else if (!_steamReady)
            {
                GUILayout.Label(string.IsNullOrEmpty(_steamError) ? "Starting Steam..." : _steamError, _muted);
                if (GUILayout.Button("Retry Steam", _smallButton, GUILayout.Width(ButtonWidth), GUILayout.Height(22f)))
                    _steamInitTried = false;
            }
            else
            {
                DrawSteam(idle);
            }

            if (_net.Role == Role.Client || _net.State == LinkState.Connecting || _net.State == LinkState.Handshaking)
                GUILayout.Label(_net.OverSteam ? "Target: " + _net.HostLabel + " over Steam (" + _net.TransportStatus + ")"
                                               : "Target: " + _joinIp + ":" + _port, _muted);
            else if (_net.Role == Role.Host)
                GUILayout.Label("Hosting on port " + Plugin.Cfg.Port.Value +
                                (_net.OverSteam ? " and over Steam (" + _net.TransportStatus + ")" : "") +
                                "; clients: " + _net.PeerCount, _muted);
            else
                GUILayout.Label(PatchHealth.Blocker != null ? "Co-op is unavailable: " + PatchHealth.Blocker
                    : PatchHealth.FaultedSets != null ? "Will not sync (patch failed): " + PatchHealth.FaultedSets
                    : "Load a world, then host a session or join a host.", _muted);
        }

        private void RefreshSteamView()
        {
            _steamMode = _steamModeWanted;
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

        private void SetSteamMode(bool steam)
        {
            _steamModeWanted = steam;
            Plugin.Cfg.UseSteam.Value = steam;
            if (steam) _steamInitTried = false;
        }

        private void DrawSteam(bool idle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("You: " + _steamSelf + " (" + _steamMyId + ")", _muted);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy ID", _smallButton, GUILayout.Width(76f), GUILayout.Height(22f)))
            {
                GUIUtility.systemCopyBuffer = _steamMyId.ToString();
                _status = "Your Steam ID is in the clipboard";
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Host ID", _muted, GUILayout.Width(72f));
            _steamJoinId = GUILayout.TextField(_steamJoinId, 20, _textField, GUILayout.Height(26f));
            GUI.enabled = idle;
            if (GUILayout.Button(_steamFriendsOnly ? "Friends only" : "Anyone", _smallButton,
                                 GUILayout.Width(ButtonWidth), GUILayout.Height(26f)))
            {
                Plugin.Cfg.SteamFriendsOnly.Value = !_steamFriendsOnly;
                _status = !_steamFriendsOnly ? "Hosting accepts Steam friends only"
                                             : "Hosting accepts anyone who knows your Steam ID";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(_friends.Length == 0
                ? "No Steam friends are in Sailwind right now. A friend not listed here can still be joined by Host ID."
                : "Steam friends in Sailwind:", _muted);
            int rows = Mathf.Min(_friends.Length, MaxFriendRows);
            for (int i = 0; i < rows; i++)
            {
                SteamFriendInfo friend = _friends[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(friend.Name, _crewCell, GUILayout.Width(170f));
                GUILayout.Label(!friend.Hosting ? "in game" : friend.SameVersion ? "hosting" : "hosting, other version",
                                _crewCell, GUILayout.Width(130f));
                GUI.enabled = idle && PatchHealth.Blocker == null && !_restartPending;
                if (GUILayout.Button("Join", _smallButton, GUILayout.Width(58f), GUILayout.Height(22f)))
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
            string text = (_steamJoinId ?? "").Trim();
            if (!ulong.TryParse(text, out ulong hostId) || hostId == 0UL)
            {
                _status = "Enter the Steam ID of the host (17 digits) or pick a friend";
                return;
            }
            if (!TryApplyConnectionFields(out _)) return;
            Plugin.Cfg.SteamJoinId.Value = text;
            _coop.StartSteamClientSession(hostId);
            _status = "Connecting over Steam to " + (string.IsNullOrEmpty(_net.HostLabel) ? text : _net.HostLabel);
        }

        private void DrawActions()
        {
            GUILayout.BeginHorizontal();
            bool busy = _net.State == LinkState.Connecting || _net.State == LinkState.Handshaking;
            GUI.enabled = !busy && PatchHealth.Blocker == null;
            if (GUILayout.Button("Host", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                StartHost();
            GUI.enabled = GUI.enabled && !_restartPending;
            if (GUILayout.Button("Join", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                Join();
            GUI.enabled = _net.State != LinkState.Idle || _net.Role != Role.None;
            if (GUILayout.Button("Disconnect", _dangerButton, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                _coop.DisconnectSession("menu");
                _status = "Session stopped";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawTools()
        {
            // Snapshot both flags ONCE, before any control is emitted, and render the whole section from
            // them. Either can change between the Layout and Repaint passes (the Logging button below
            // flips one; ConfigurationManager can flip the other), and a control count that differs
            // between the two passes is Unity's "Getting control N's position in a group with only M
            // controls" — thrown between BeginArea and EndArea, leaving the GUI clip stack unbalanced.
            bool debugTools = Plugin.Cfg.EnableDebugPanel.Value;

            GUILayout.Label("Tools", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Avatar", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                AvatarCatalog.Scan();
                _coop.ToggleAvatarMenu();
            }
            if (GUILayout.Button(_coop.OverlayVisible ? "Hide Status" : "Show Status", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                _coop.OverlayVisible = !_coop.OverlayVisible;

            GUI.enabled = debugTools;
            if (GUILayout.Button("Debug", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                _coop.ToggleDebugPanel();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Water dump: press on BOTH machines within a second or two, then diff the two files.
            // Deliberately not gated behind debug tools - it only reads state and writes one text
            // file, and it is the thing we ask a player to do when a boat looks wrong in the water.
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Dump water state", _button,
                                 GUILayout.Width(ButtonWidth * 2f + 6f), GUILayout.Height(ButtonHeight)))
                _status = WaterDump.Write(_coop);
            if (GUILayout.Button("Export report", _button,
                                 GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                _status = CoopReport.Write(_coop);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Same rule as debugTools above: a button only returns true on the event pass, never on the
            // Layout pass, so flipping this mid-draw and branching on it would emit different control
            // counts in the two passes. The new value is picked up on the next pass, which sees a
            // consistent Layout/Repaint pair.
            bool logging = Plugin.Logger.Enabled;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(logging ? "Logging: ON" : "Logging: off", _button,
                                 GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                bool next = !logging;
                Plugin.Logger.Enabled = next;
                Plugin.Cfg.EnableLogging.Value = next;   // persists to the config file
                _status = next
                    ? "Logging ON - reproduce the problem, then send BepInEx/LogOutput.log"
                    : "Logging off";
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Always emitted (constant control count); only the text depends on the state.
            GUILayout.Label(debugTools
                ? "Debug tools are enabled."
                : "Debug tools are disabled in public mode.", _muted);

            // Always emitted (constant control count); only the text depends on the state.
            GUILayout.Label(logging
                ? "Logging is ON. Reproduce the problem, then send BepInEx/LogOutput.log."
                : "Logging is off. Turn it on BEFORE reproducing a problem - nothing is written to the log until you do.",
                _muted);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Model: " + AvatarCatalog.DisplayNameFor(AvatarCatalog.CurrentSelection), _muted);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", _smallButton, GUILayout.Width(82f), GUILayout.Height(26f)))
                SetVisible(false);
            GUILayout.EndHorizontal();
        }

        private void StartHost()
        {
            if (!TryApplyConnectionFields(out int port)) return;
            bool steam = _steamMode && _steamReady;
            _coop.StartHostSession(port, steam);
            _status = steam ? "Host started (Steam + LAN)" : "Host started";
        }

        private void Join()
        {
            if (_steamMode)
            {
                JoinSteamFromField();
                return;
            }
            if (!TryApplyConnectionFields(out int port)) return;
            _coop.StartClientSession(_joinIp.Trim(), port);
            _status = "Connecting to " + _joinIp.Trim() + ":" + port;
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
                case LinkState.Idle: return "offline";
                case LinkState.Connecting: return "connecting";
                case LinkState.Handshaking: return "handshake";
                case LinkState.Connected: return _net.Role == Role.Host ? "host" : "client";
                case LinkState.Rejected: return "rejected";
                case LinkState.Failed: return "failed";
                default: return _net.State.ToString();
            }
        }

        private void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            if (!visible)
                _coop.CloseCompanionMenus();
            _visible = visible;
            ApplyCursorState();
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
            if (_window != null) return;

            _backdropTex = MakeBg(new Color(0f, 0f, 0f, 0.62f));
            _shadowTex = MakeBg(new Color(0f, 0f, 0f, 0.72f));
            _windowTex = MakeBg(new Color(0.025f, 0.022f, 0.018f, 0.98f));
            _borderTex = MakeBg(new Color(0.62f, 0.48f, 0.30f, 0.95f));
            var warm = MakeBg(new Color(0.32f, 0.24f, 0.16f, 0.96f));
            var warmHover = MakeBg(new Color(0.42f, 0.31f, 0.20f, 0.98f));
            var red = MakeBg(new Color(0.40f, 0.14f, 0.11f, 0.96f));
            var redHover = MakeBg(new Color(0.52f, 0.18f, 0.14f, 0.98f));
            var field = MakeBg(new Color(0.08f, 0.075f, 0.06f, 0.95f));
            var pill = MakeBg(new Color(0.13f, 0.20f, 0.18f, 0.95f));

            _window = new GUIStyle(GUI.skin.box)
            {
                normal = { background = null },
                border = new RectOffset(8, 8, 8, 8),
                padding = new RectOffset(12, 12, 12, 12)
            };
            _backdrop = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _backdropTex },
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0)
            };
            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.96f, 0.88f, 0.72f) }
            };
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.92f, 0.84f, 0.68f) }
            };
            _muted = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.74f, 0.70f, 0.62f) }
            };
            _crewCell = new GUIStyle(_muted)
            {
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { background = warm, textColor = new Color(0.98f, 0.92f, 0.80f) },
                hover = { background = warmHover, textColor = Color.white },
                active = { background = warmHover, textColor = Color.white }
            };
            _dangerButton = new GUIStyle(_button)
            {
                normal = { background = red, textColor = new Color(1f, 0.88f, 0.82f) },
                hover = { background = redHover, textColor = Color.white },
                active = { background = redHover, textColor = Color.white }
            };
            _smallButton = new GUIStyle(_button) { fontSize = 12 };
            _alertBox = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeBg(new Color(0.46f, 0.30f, 0.04f, 0.97f)) },
                border = new RectOffset(0, 0, 0, 0),
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
            _textField = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13,
                normal = { background = field, textColor = Color.white },
                focused = { background = field, textColor = Color.white }
            };
            _pill = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = pill, textColor = new Color(0.74f, 1f, 0.84f) }
            };
        }
    }
}
