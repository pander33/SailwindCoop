using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Lightweight IMGUI diagnostics: role, link state, RTT, clock offset, peers,
    /// registry size, last error. On from Stage 0 so every later step is observable.
    /// Toggle with the Overlay hotkey (default F8).
    /// </summary>
    public sealed class DebugOverlay
    {
        private readonly CoopNet _net;
        private GUIStyle _box;
        private GUIStyle _label;

        public DebugOverlay(CoopNet net) { _net = net; }

        public void Draw()
        {
            EnsureStyles();

            const float w = 320f, h = 212f;
            var rect = new Rect(12, 12, w, h);
            GUI.Box(rect, "Sailwind Co-op " + Plugin.Version, _box);

            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 26, w - 20, h - 34));

            Line("Роль", _net.Role.ToString());
            Line("Состояние", StateText(_net.State));

            if (_net.Role == Role.Client || _net.State == LinkState.Connected)
            {
                Line("RTT", _net.Clock.HasSample ? _net.Clock.RttMs.ToString("0") + " мс" : "—");
                Line("Смещение часов", _net.Clock.HasSample ? _net.Clock.OffsetMs.ToString("0") + " мс" : "—");
            }

            if (_net.Role == Role.Host)
                Line("Клиентов", _net.PeerCount.ToString());

            Line("Объектов (NetId)", CountAll().ToString());

            var coop = CoopBehaviour.Instance;
            if (coop != null && coop.Players != null && _net.State == LinkState.Connected)
            {
                Line("Аватары / игрок", coop.Players.RemoteCount + " / " +
                     (coop.Players.LocalPlayerFound ? "найден" : "—"));
                float d = coop.Players.NearestRemoteDistance;
                if (d >= 0f) Line("До аватара", d.ToString("0.0") + " м");
            }

            if (!string.IsNullOrEmpty(_net.LastError))
                Line("Ошибка", _net.LastError);

            GUILayout.EndArea();
        }

        private int CountAll()
        {
            int n = 0;
            foreach (var _ in _net.Registry.All) n++;
            return n;
        }

        private static string StateText(LinkState s)
        {
            switch (s)
            {
                case LinkState.Idle: return "не активно (F9 хост / F10 подключиться)";
                case LinkState.Connecting: return "подключение...";
                case LinkState.Handshaking: return "рукопожатие...";
                case LinkState.Connected: return "соединено";
                case LinkState.Rejected: return "отклонено хостом";
                case LinkState.Failed: return "сбой";
                default: return s.ToString();
            }
        }

        private void Line(string key, string val)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(key + ":", _label, GUILayout.Width(130));
            GUILayout.Label(val, _label);
            GUILayout.EndHorizontal();
        }

        private void EnsureStyles()
        {
            if (_box == null)
            {
                _box = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontStyle = FontStyle.Bold,
                    fontSize = 13,
                };
                _box.normal.textColor = Color.white;
            }
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
                _label.normal.textColor = Color.white;
            }
        }
    }
}
