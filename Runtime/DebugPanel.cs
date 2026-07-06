using System;
using System.Collections.Generic;
using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// F7 — dev-only test harness. Creates hard-to-reach co-op scenarios on demand (gold,
    /// item spawn, reputation, time/storm/teleport) so the unverified features in
    /// <c>TEST_CHECKLIST.md</c> can be exercised without grinding to the right place/items.
    ///
    /// Pure setup tool — it changes NO wire format. A host item spawn goes through the vanilla
    /// path (<c>Instantiate(PrefabsDirectory.directory[i])</c> + <c>SaveablePrefab.RegisterToSave</c>),
    /// which <see cref="Sync.ItemSync"/> picks up via its periodic diff and broadcasts as a normal
    /// SpawnObject — exactly like <c>ShipItemCrate.UnsealCrate</c>. Time/storm ride
    /// <see cref="Sync.EnvironmentSync"/>. Gold and reputation are per-machine statics, so each side
    /// sets its own (matching the separate-money model): give gold on the client to test "client buys
    /// with own wallet", raise reputation on the client to unlock mission offers there, etc.
    ///
    /// Every engine call is wrapped in try/catch with a Russian log, like the rest of the mod.
    /// </summary>
    public sealed class DebugPanel
    {
        private readonly CoopNet _net;
        public bool Visible;

        private GUIStyle _box;
        private GUIStyle _label;

        private string _goldAmount = "10000";
        private string _itemFilter = "";
        private Vector2 _scroll;
        private string _status = "—";

        // Cached (prefabIndex, name) of every spawnable ShipItem prefab.
        private List<KeyValuePair<int, string>> _prefabs;

        // Reflection / lookup caches.
        private PlayerEmbarkerNew _emb;
        private FieldInfo _fStorms;

        public DebugPanel(CoopNet net) { _net = net; }

        public void Draw()
        {
            if (!Visible) return;
            EnsureStyles();

            const float w = 380f, h = 820f;
            var rect = new Rect(Screen.width - w - 12, 12, w, h);
            GUI.Box(rect, "Debug Panel (F7) - Test Scenarios", _box);

            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 28, w - 20, h - 36));

            string role = _net.State == LinkState.Connected ? _net.Role.ToString() : "offline";
            GUILayout.Label("Role: " + role + "   ·   " + _status, _label);
            GUILayout.Space(4);

            DrawGold();
            GUILayout.Space(6);
            DrawReputation();
            GUILayout.Space(6);
            DrawWorld();
            GUILayout.Space(6);
            DrawIslandTeleport();
            GUILayout.Space(6);
            DrawItemSpawn();

            GUILayout.EndArea();
        }

        // -----------------------------------------------------------------
        // 1. Gold — local (each machine sets its own wallet)
        // -----------------------------------------------------------------

        private void DrawGold()
        {
            GUILayout.Label("- Gold (local) -", _label);
            GUILayout.Label("Current: " + CurrentGoldText(), _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1000")) GiveGold(1000);
            if (GUILayout.Button("+10000")) GiveGold(10000);
            _goldAmount = GUILayout.TextField(_goldAmount, GUILayout.Width(80));
            if (GUILayout.Button("Give") && int.TryParse(_goldAmount, out int amt)) GiveGold(amt);
            GUILayout.EndHorizontal();
        }

        private static string CurrentGoldText()
        {
            try
            {
                if (PlayerGold.currency != null && PlayerGold.currency.Length > 0)
                {
                    var parts = new string[PlayerGold.currency.Length];
                    for (int i = 0; i < PlayerGold.currency.Length; i++)
                        parts[i] = PlayerGold.GetCurrencySymbol(i) + PlayerGold.currency[i];
                    return string.Join(" ", parts);
                }
                return "gold=" + PlayerGold.gold;
            }
            catch (Exception e) { return "?(" + e.Message + ")"; }
        }

        private void GiveGold(int amount)
        {
            try
            {
                PlayerGold.gold += amount;
                if (PlayerGold.currency != null)
                    for (int i = 0; i < PlayerGold.currency.Length; i++)
                        PlayerGold.currency[i] += amount;
                _status = "given " + amount + " gold";
                Plugin.Logger.LogInfo("[DebugPanel] +" + amount + " gold (local)");
            }
            catch (Exception e) { _status = "gold: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] gold: " + e); }
        }

        // -----------------------------------------------------------------
        // 2. Reputation — local (unlocks mission offers in the office)
        // -----------------------------------------------------------------

        private void DrawReputation()
        {
            GUILayout.Label("- Reputation (local) -", _label);
            GUILayout.BeginHorizontal();
            GiveRepButton("Al'Ankh", PortRegion.alankh);
            GiveRepButton("Emerald", PortRegion.emerald);
            GiveRepButton("Aestrin", PortRegion.medi);
            GUILayout.EndHorizontal();
        }

        private void GiveRepButton(string title, PortRegion region)
        {
            int lvl;
            try { lvl = PlayerReputation.GetRepLevel(region); } catch { lvl = -1; }
            if (GUILayout.Button(title + " +100 (lvl " + lvl + ")"))
            {
                try
                {
                    PlayerReputation.ChangeReputation(100, region);
                    _status = "+100 rep " + title;
                    Plugin.Logger.LogInfo("[DebugPanel] +100 reputation " + region + " (local)");
                }
                catch (Exception e) { _status = "rep: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] rep: " + e); }
            }
        }

        // -----------------------------------------------------------------
        // 3. World — host-only (replicates via EnvironmentSync / BoatSync)
        // -----------------------------------------------------------------

        private void DrawWorld()
        {
            GUILayout.Label("- World: time / storm / teleport (host) -", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1 hour")) AdvanceTime(1f);
            if (GUILayout.Button("+1 day")) AdvanceTime(24f);
            if (GUILayout.Button("Storm here")) ForceStorm();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Teleport to nearest port (experimental)")) TeleportNearestPort();
        }

        private bool HostGateFail()
        {
            if (_net.State == LinkState.Connected && _net.Role != Role.Host)
            {
                _status = "host only (this is replicated)";
                return true;
            }
            return false;
        }

        private void AdvanceTime(float hours)
        {
            if (HostGateFail()) return;
            try
            {
                var sun = Sun.sun;
                if (sun != null)
                {
                    sun.globalTime += hours;
                    sun.localTime += hours;
                    while (sun.localTime >= 24f) sun.localTime -= 24f;
                }
                if (hours >= 24f) GameState.day += Mathf.RoundToInt(hours / 24f);
                _status = "+" + hours + " h";
                Plugin.Logger.LogInfo("[DebugPanel] time +" + hours + " h");
            }
            catch (Exception e) { _status = "time: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] time: " + e); }
        }

        private void ForceStorm()
        {
            if (HostGateFail()) return;
            try
            {
                var ws = WeatherStorms.instance;
                if (ws == null) { _status = "WeatherStorms missing"; return; }
                if (_fStorms == null)
                    _fStorms = typeof(WeatherStorms).GetField("storms", BindingFlags.NonPublic | BindingFlags.Instance);
                var storms = _fStorms != null ? _fStorms.GetValue(ws) as WanderingStorm[] : null;
                if (storms == null || storms.Length == 0) { _status = "no storms in scene"; return; }

                Vector3 player = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                WanderingStorm nearest = null;
                float best = float.MaxValue;
                foreach (var s in storms)
                {
                    if (s == null) continue;
                    float d = Vector3.Distance(s.transform.position, player);
                    if (d < best) { best = d; nearest = s; }
                }
                if (nearest == null) { _status = "storms list is empty"; return; }
                nearest.active = true;
                nearest.transform.position = player;
                _status = "storm moved to player";
                Plugin.Logger.LogInfo("[DebugPanel] storm '" + nearest.name + "' moved to player");
            }
            catch (Exception e) { _status = "storm: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] storm: " + e); }
        }

        private void TeleportNearestPort()
        {
            if (HostGateFail()) return;
            try
            {
                var emb = Embarker();
                Transform boat = emb != null ? emb.debugOutCurrentBoat : null;
                Vector3 player = Camera.main != null ? Camera.main.transform.position
                                : (emb != null && emb.playerObserver != null ? emb.playerObserver.position : Vector3.zero);

                var markets = UnityEngine.Object.FindObjectsOfType<IslandMarket>();
                IslandMarket nearest = null;
                float best = float.MaxValue;
                foreach (var m in markets)
                {
                    if (m == null) continue;
                    float d = Vector3.Distance(m.transform.position, player);
                    if (d < best) { best = d; nearest = m; }
                }
                if (nearest == null) { _status = "port not found (far ports not loaded)"; return; }
                if (boat == null) { _status = "no boat to teleport"; return; }

                // Move the whole boat so the player (its child) ends up next to the port. Far islands
                // aren't streamed in under the floating origin, so this only reaches a loaded port.
                Vector3 target = nearest.transform.position + Vector3.up * 2f;
                boat.position += (target - player);
                _status = "boat -> port " + SafePortName(nearest) + " (" + best.ToString("0") + " m)";
                Plugin.Logger.LogInfo("[DebugPanel] boat teleported to port " + SafePortName(nearest));
            }
            catch (Exception e) { _status = "teleport: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] teleport: " + e); }
        }

        private static string SafePortName(IslandMarket m)
        {
            try { return m.GetPortName(); } catch { return m.name; }
        }

        // -----------------------------------------------------------------
        // 3.5 Телепорт по островам — через ванильные RecoveryPort (точка спавна игрока на причале +
        // выверенная позиция лодки GetBoatPos, всё в shifting-пространстве). Хост двигает ЛОДКУ
        // (рецепт ванильного Recovery: отшвартовать, поднять якорь, обнулить скорость, поставить в
        // boatPos); игрок на палубе едет вместе с ней — он ходит по статичной walk-копии, которую
        // телепорт визуальной лодки не трогает. Пеший игрок переносится на причал (любая роль —
        // PlayerSync стримит real-space позу). Клиент на лодке сам телепортироваться не может —
        // лодка хост-авторитетна.
        // -----------------------------------------------------------------

        private List<RecoveryPort> _recoveryPorts;
        private Vector2 _islandScroll;
        private FieldInfo _fEmbarked;

        private void DrawIslandTeleport()
        {
            GUILayout.Label("- Island teleport (host moves the boat) -", _label);
            if (_recoveryPorts == null) RefreshRecoveryPorts();
            if (GUILayout.Button("Refresh port list (" + (_recoveryPorts != null ? _recoveryPorts.Count : 0) + ")"))
                RefreshRecoveryPorts();

            if (_recoveryPorts == null || _recoveryPorts.Count == 0)
            {
                GUILayout.Label("Recovery ports not found (world still loading?)", _label);
                return;
            }

            _islandScroll = GUILayout.BeginScrollView(_islandScroll, GUILayout.Height(140));
            foreach (var rp in _recoveryPorts)
            {
                if (rp == null) continue;
                if (GUILayout.Button(RecoveryPortName(rp)))
                    TeleportToIsland(rp);
            }
            GUILayout.EndScrollView();
        }

        private void RefreshRecoveryPorts()
        {
            try
            {
                _recoveryPorts = new List<RecoveryPort>(UnityEngine.Object.FindObjectsOfType<RecoveryPort>());
                _recoveryPorts.Sort((a, b) => string.CompareOrdinal(RecoveryPortName(a), RecoveryPortName(b)));
            }
            catch (Exception e)
            {
                _recoveryPorts = new List<RecoveryPort>();
                Plugin.Logger.LogWarning("[DebugPanel] recovery port list: " + e.Message);
            }
        }

        private static string RecoveryPortName(RecoveryPort rp)
        {
            try
            {
                if (rp != null && rp.parentPort != null)
                {
                    string n = rp.parentPort.GetPortName();
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            return rp != null ? rp.name : "?";
        }

        private void TeleportToIsland(RecoveryPort rp)
        {
            try
            {
                if (rp == null) return;
                bool hostAuthority = _net.State != LinkState.Connected || _net.Role == Role.Host;
                bool embarked = IsEmbarked();

                if (!hostAuthority && embarked)
                {
                    _status = "client on boat: host teleports";
                    return;
                }

                if (hostAuthority)
                {
                    Transform boat = GameState.lastOwnedBoat != null ? GameState.lastOwnedBoat : GameState.currentBoat;
                    if (boat != null)
                    {
                        try
                        {
                            var ropes = boat.GetComponent<BoatMooringRopes>();
                            if (ropes != null)
                            {
                                ropes.UnmoorAllRopes();
                                var anchor = ropes.GetAnchorController();
                                if (anchor != null) anchor.ResetAnchor();
                            }
                            var rb = boat.GetComponent<Rigidbody>();
                            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                            boat.position = rp.GetBoatPos();
                            if (rp.boatPos != null) boat.rotation = rp.boatPos.rotation;
                        }
                        catch (Exception be) { Plugin.Logger.LogWarning("[DebugPanel] boat teleport: " + be.Message); }
                    }
                }

                if (!embarked)
                {
                    // Пеший игрок → точка спавна recovery-порта. charController — как ванильный
                    // дебаг-телепорт Port.teleportPlayer (ovrController не трогаем: тип из Oculus.VR,
                    // сборка не в референсах).
                    Vector3 pos = rp.transform.position + Vector3.up * 1f;
                    if (Refs.charController != null)
                        Refs.charController.transform.position = pos;
                    if (Refs.observerMirror != null)
                    {
                        Refs.observerMirror.transform.position = pos;
                        Refs.observerMirror.transform.rotation = rp.transform.rotation;
                    }
                }

                _status = "teleport -> " + RecoveryPortName(rp) + (embarked ? " (on boat)" : " (on foot)");
                Plugin.Logger.LogInfo("[DebugPanel] teleport -> " + RecoveryPortName(rp) +
                                      " embarked=" + embarked + " hostAuthority=" + hostAuthority);
            }
            catch (Exception e)
            {
                _status = "teleport: " + e.Message;
                Plugin.Logger.LogWarning("[DebugPanel] teleport to island: " + e);
            }
        }

        private bool IsEmbarked()
        {
            try
            {
                var emb = Embarker();
                if (emb == null) return false;
                if (_fEmbarked == null)
                    _fEmbarked = typeof(PlayerEmbarkerNew).GetField("embarked", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fEmbarked != null && (bool)_fEmbarked.GetValue(emb);
            }
            catch { return false; }
        }

        // -----------------------------------------------------------------
        // 4. Item spawn — host (the new saveable auto-replicates via ItemSync)
        // -----------------------------------------------------------------

        private void DrawItemSpawn()
        {
            GUILayout.Label("- Item Spawn (host) -", _label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("filter:", _label, GUILayout.Width(50));
            _itemFilter = GUILayout.TextField(_itemFilter, GUILayout.Width(180));
            if (GUILayout.Button("Reset", GUILayout.Width(60))) _itemFilter = "";
            GUILayout.EndHorizontal();

            EnsurePrefabs();
            string flt = _itemFilter != null ? _itemFilter.ToLowerInvariant() : "";

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(220));
            int shown = 0;
            foreach (var kv in _prefabs)
            {
                if (flt.Length > 0 && kv.Value.ToLowerInvariant().IndexOf(flt, StringComparison.Ordinal) < 0) continue;
                if (++shown > 60) { GUILayout.Label("... narrow the filter (>60 matches)", _label); break; }
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Value, _label);
                if (GUILayout.Button("Spawn", GUILayout.Width(70))) SpawnItem(kv.Key);
                GUILayout.EndHorizontal();
            }
            if (shown == 0) GUILayout.Label("no matches", _label);
            GUILayout.EndScrollView();
        }

        private void EnsurePrefabs()
        {
            if (_prefabs != null) return;
            _prefabs = new List<KeyValuePair<int, string>>();
            try
            {
                var dir = PrefabsDirectory.instance;
                if (dir != null && dir.shipItems != null)
                {
                    for (int i = 0; i < dir.shipItems.Length; i++)
                    {
                        var si = dir.shipItems[i];
                        if (si != null) _prefabs.Add(new KeyValuePair<int, string>(i, si.name));
                    }
                }
                Plugin.Logger.LogInfo("[DebugPanel] item catalog: " + _prefabs.Count);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DebugPanel] catalog: " + e.Message); }
        }

        private void SpawnItem(int prefabIndex)
        {
            if (HostGateFail()) return;
            try
            {
                var dir = PrefabsDirectory.instance;
                if (dir == null || dir.directory == null || prefabIndex < 0 || prefabIndex >= dir.directory.Length)
                { _status = "missing prefab #" + prefabIndex; return; }
                var prefab = dir.directory[prefabIndex];
                if (prefab == null) { _status = "prefab #" + prefabIndex + " is empty"; return; }

                var emb = Embarker();
                Vector3 pos; Quaternion rot;
                if (emb != null && emb.playerObserver != null)
                {
                    var t = emb.playerObserver;
                    pos = t.position + t.forward * 1.5f + Vector3.up * 0.5f;
                    rot = t.rotation;
                }
                else { pos = Vector3.zero; rot = Quaternion.identity; }

                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                var sp = go.GetComponent<SaveablePrefab>();
                if (sp != null) sp.RegisterToSave();
                var item = go.GetComponent<ShipItem>();
                if (item != null)
                {
                    item.sold = true;                 // a fresh-spawned item is "owned", like crate contents
                    try { item.OnLoad(); }            // run the vanilla post-load init (crate inventory, value, …)
                    catch (Exception le) { Plugin.Logger.LogWarning("[DebugPanel] OnLoad '" + prefab.name + "': " + le.Message); }
                }
                _status = "spawned " + prefab.name;
                Plugin.Logger.LogInfo("[DebugPanel] spawned '" + prefab.name + "' idx=" + prefabIndex);
            }
            catch (Exception e) { _status = "spawn: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] spawn: " + e); }
        }

        private PlayerEmbarkerNew Embarker()
        {
            if (_emb == null) _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            return _emb;
        }

        // -----------------------------------------------------------------

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
