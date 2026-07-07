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

        private GUIStyle _window;
        private GUIStyle _title;
        private GUIStyle _label;
        private GUIStyle _muted;
        private GUIStyle _button;
        private GUIStyle _dangerButton;
        private GUIStyle _smallButton;
        private GUIStyle _textField;
        private GUIStyle _pill;
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

            if (_visible)
            {
                ApplyCursorState();
                DrawWindow();
            }
        }

        private void DrawWindow()
        {
            float w = 430f;
            float h = 382f;
            float x = Mathf.Clamp(Screen.width - w - 18f, 10f, Screen.width - w - 10f);
            float y = 60f;
            var rect = new Rect(x, y, w, h);

            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _backdropTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x + 8f, rect.y + 8f, rect.width, rect.height), _shadowTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), _borderTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(rect, _windowTex, ScaleMode.StretchToFill);
            GUI.Box(rect, GUIContent.none, _window);
            GUILayout.BeginArea(new Rect(x + 14f, y + 12f, w - 28f, h - 24f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Sailwind Co-op", _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label(StateText(), _pill, GUILayout.Width(128f), GUILayout.Height(24f));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            DrawIdentity();
            GUILayout.Space(8f);
            DrawConnection();
            GUILayout.Space(10f);
            DrawActions();
            GUILayout.Space(10f);
            DrawTools();
            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(_net.LastError))
                GUILayout.Label(_net.LastError, _muted);
            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status, _muted);

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
        }

        private void DrawConnection()
        {
            GUILayout.Label("Connection", _label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host IP", _muted, GUILayout.Width(72f));
            _joinIp = GUILayout.TextField(_joinIp, _textField, GUILayout.Height(26f));
            GUILayout.Label("Port", _muted, GUILayout.Width(38f));
            _port = GUILayout.TextField(_port, _textField, GUILayout.Width(66f), GUILayout.Height(26f));
            GUILayout.EndHorizontal();

            if (_net.Role == Role.Client || _net.State == LinkState.Connecting || _net.State == LinkState.Handshaking)
                GUILayout.Label("Target: " + _joinIp + ":" + _port, _muted);
            else if (_net.Role == Role.Host)
                GUILayout.Label("Hosting on port " + Plugin.Cfg.Port.Value + "; clients: " + _net.PeerCount, _muted);
            else
                GUILayout.Label("Load a world, then host a session or join a host.", _muted);
        }

        private void DrawActions()
        {
            GUILayout.BeginHorizontal();
            bool busy = _net.State == LinkState.Connecting || _net.State == LinkState.Handshaking;
            GUI.enabled = !busy;
            if (GUILayout.Button("Host", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                StartHost();
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
            GUILayout.Label("Tools", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Avatar", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                AvatarCatalog.Scan();
                _coop.ToggleAvatarMenu();
            }
            if (GUILayout.Button(_coop.OverlayVisible ? "Hide Status" : "Show Status", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                _coop.OverlayVisible = !_coop.OverlayVisible;

            GUI.enabled = Plugin.Cfg.EnableDebugPanel.Value;
            if (GUILayout.Button("Debug", _button, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
                _coop.ToggleDebugPanel();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!Plugin.Cfg.EnableDebugPanel.Value)
                GUILayout.Label("Debug tools are disabled in public mode.", _muted);

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
            _coop.StartHostSession(port);
            _status = "Host started";
        }

        private void Join()
        {
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
