using System.Reflection;
using SailwindCoop.Avatar;
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

            const float w = 360f, h = 800f;
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
            Line("Координаты", CoordText());

            var coop = CoopBehaviour.Instance;
            if (coop != null && coop.Players != null && _net.State == LinkState.Connected)
            {
                Line("Аватары / игрок", coop.Players.RemoteCount + " / " +
                     (coop.Players.LocalPlayerFound ? "найден" : "—"));
                float d = coop.Players.NearestRemoteDistance;
                if (d >= 0f) Line("До аватара", d.ToString("0.0") + " м");
                //Line("Анимация", coop.Players.NearestRemoteAnim);
                //Line("Присед", coop.Players.LocalCrouchText);
                Line("Модель", AvatarCatalog.CurrentSelection + " (" + AvatarCatalog.Entries.Count + " бандлов)");

                if (coop.Boats != null)
                {
                    if (_net.Role == Role.Host)
                        Line("Корабли", coop.Boats.BoatCount + " шт" +
                             (coop.Boats.BoatNetId != 0 ? ", первый NetId=" + coop.Boats.BoatNetId : ""));
                    else
                        Line("Корабли", coop.Boats.IsSlaving ? "ведомых " + coop.Boats.BoatCount : "ждёт лодку");
                }

                Line("Среда", EnvText());

                if (coop.Controls != null)
                {
                    //Line("Управление", coop.Controls.RopeCount + " тросов, " +
                    //     coop.Controls.WinchCount + " леб., " +
                    //     coop.Controls.NodeCount + " узлов");
                    //Line("Руль", coop.Controls.SteeringText);
                    Line("ControlRequest", coop.Controls.LastControlRequestText);
                }

                if (coop.Anchor != null)
                    //Line("Якорь", coop.Anchor.AnchorText);

                if (coop.Mooring != null)
                    //Line("Швартовы", coop.Mooring.MooringText);

                if (coop.Damage != null)
                    Line("Повреждения", coop.Damage.DamageText);

                if (coop.Lights != null)
                    Line("Свет", coop.Lights.LightText);

                if (coop.Items != null)
                    Line("Предметы", coop.Items.ItemText);

                if (coop.Shop != null)
                    Line("Магазин", coop.Shop.ShopText);

                if (coop.Sleep != null)
                    Line("Сон", coop.Sleep.SleepText);

                if (coop.Missions != null)
                    Line("Миссии", coop.Missions.MissionText);

                if (coop.WindTotem != null && coop.WindTotem.Active)
                    Line("Ветр.тотем", "сила " + coop.WindTotem.LastWind.magnitude.ToString("0.0"));

                if (coop.Interactions != null)
                    Line("Событие", coop.Interactions.ButtonCount + " кн · " + coop.Interactions.LastEventText);

                Line("Прицел", InteractText());
            }

            if (!string.IsNullOrEmpty(_net.LastError))
                Line("Ошибка", _net.LastError);

            GUILayout.EndArea();
        }

        // --- interaction diagnostics (Stage 2 debug) ---
        private static GoPointer _gp;
        private static FieldInfo _fPointed, _fHit, _fSticky;
        private static PlayerEmbarkerNew _emb;
        private static FieldInfo _fEmbarked;

        /// <summary>
        /// What the local player's GoPointer currently sees: the button it's targeting and
        /// the collider its ray hit. Aim at the same winch on host vs client and compare:
        /// a different/empty hit on the client points at a raycast/coordinate problem; a hit
        /// with no button points at button gating; a button that won't activate points at us.
        /// </summary>
        private static string InteractText()
        {
            try
            {
                if (_gp == null) _gp = Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return "GoPointer не найден";

                var tp = typeof(GoPointer);
                if (_fPointed == null) _fPointed = tp.GetField("pointedAtButton", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fHit == null) _fHit = tp.GetField("hit", BindingFlags.NonPublic | BindingFlags.Instance);

                var btn = _fPointed != null ? _fPointed.GetValue(_gp) as GoPointerButton : null;
                string btnTxt = btn != null
                    ? (string.IsNullOrEmpty(btn.lookText) ? btn.name : btn.lookText)
                    : "—";

                string hitName = "—";
                if (_fHit != null && _fHit.GetValue(_gp) is RaycastHit rh && rh.collider != null)
                    hitName = rh.collider.name;

                if (_fSticky == null) _fSticky = tp.GetField("stickyClickedButton", BindingFlags.NonPublic | BindingFlags.Instance);
                var held = _fSticky != null ? _fSticky.GetValue(_gp) as GoPointerButton : null;
                string heldTxt = held != null ? "ДА" : "—";

                if (_emb == null) _emb = Object.FindObjectOfType<PlayerEmbarkerNew>();
                string emb = "?";
                if (_emb != null)
                {
                    if (_fEmbarked == null) _fEmbarked = typeof(PlayerEmbarkerNew).GetField("embarked", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fEmbarked != null) emb = ((bool)_fEmbarked.GetValue(_emb)).ToString();
                }

                return "кн:" + btnTxt + " держит:" + heldTxt + " emb:" + emb;
            }
            catch (System.Exception e)
            {
                return "err " + e.Message;
            }
        }

        /// <summary>
        /// Широта/долгота локального игрока в градусах глобуса игры. Ваниль сама считает их через
        /// FloatingOriginManager.GetGlobeCoords (1° = 9000 юнитов реального пространства): x = долгота
        /// (E/W), z = широта (N/S) — тот же формат, что в описании миссий («36 N, 4 E»).
        /// </summary>
        private static string CoordText()
        {
            try
            {
                var fom = FloatingOriginManager.instance;
                var obs = Refs.observerMirror;
                if (fom == null || obs == null) return "—";
                Vector3 g = fom.GetGlobeCoords(obs.transform);
                string ns = g.z < 0f ? "S" : "N";
                string ew = g.x < 0f ? "W" : "E";
                return Mathf.Abs(g.z).ToString("0.00") + "° " + ns + ", " + Mathf.Abs(g.x).ToString("0.00") + "° " + ew;
            }
            catch (System.Exception e)
            {
                return "err " + e.Message;
            }
        }

        /// <summary>Live env readout (wind speed + time of day) — compare both windows to verify parity.</summary>
        private static string EnvText()
        {
            float windMag = Wind.currentWind.magnitude;
            string t = Sun.sun != null ? Sun.sun.localTime.ToString("0.00") : "—";
            return "ветер " + windMag.ToString("0.0") + ", t " + t;
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
