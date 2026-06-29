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

            const float w = 380f, h = 660f;
            var rect = new Rect(Screen.width - w - 12, 12, w, h);
            GUI.Box(rect, "Дебаг-панель (F7) — тест-сценарии", _box);

            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 28, w - 20, h - 36));

            string role = _net.State == LinkState.Connected ? _net.Role.ToString() : "оффлайн";
            GUILayout.Label("Роль: " + role + "   ·   " + _status, _label);
            GUILayout.Space(4);

            DrawGold();
            GUILayout.Space(6);
            DrawReputation();
            GUILayout.Space(6);
            DrawWorld();
            GUILayout.Space(6);
            DrawItemSpawn();

            GUILayout.EndArea();
        }

        // -----------------------------------------------------------------
        // 1. Gold — local (each machine sets its own wallet)
        // -----------------------------------------------------------------

        private void DrawGold()
        {
            GUILayout.Label("— Золото (локально) —", _label);
            GUILayout.Label("Текущее: " + CurrentGoldText(), _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1000")) GiveGold(1000);
            if (GUILayout.Button("+10000")) GiveGold(10000);
            _goldAmount = GUILayout.TextField(_goldAmount, GUILayout.Width(80));
            if (GUILayout.Button("Выдать") && int.TryParse(_goldAmount, out int amt)) GiveGold(amt);
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
                _status = "выдано " + amount + " золота";
                Plugin.Logger.LogInfo("[DebugPanel] +" + amount + " золота (локально)");
            }
            catch (Exception e) { _status = "золото: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] золото: " + e); }
        }

        // -----------------------------------------------------------------
        // 2. Reputation — local (unlocks mission offers in the office)
        // -----------------------------------------------------------------

        private void DrawReputation()
        {
            GUILayout.Label("— Репутация (локально) —", _label);
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
            if (GUILayout.Button(title + " +100 (ур." + lvl + ")"))
            {
                try
                {
                    PlayerReputation.ChangeReputation(100, region);
                    _status = "+100 реп " + title;
                    Plugin.Logger.LogInfo("[DebugPanel] +100 репутации " + region + " (локально)");
                }
                catch (Exception e) { _status = "реп: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] реп: " + e); }
            }
        }

        // -----------------------------------------------------------------
        // 3. World — host-only (replicates via EnvironmentSync / BoatSync)
        // -----------------------------------------------------------------

        private void DrawWorld()
        {
            GUILayout.Label("— Мир: время / шторм / телепорт (хост) —", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1 час")) AdvanceTime(1f);
            if (GUILayout.Button("+1 день")) AdvanceTime(24f);
            if (GUILayout.Button("Шторм здесь")) ForceStorm();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Телепорт к ближайшему порту (эксперим.)")) TeleportNearestPort();
        }

        private bool HostGateFail()
        {
            if (_net.State == LinkState.Connected && _net.Role != Role.Host)
            {
                _status = "только хост (это реплицируется)";
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
                _status = "+" + hours + " ч";
                Plugin.Logger.LogInfo("[DebugPanel] время +" + hours + " ч");
            }
            catch (Exception e) { _status = "время: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] время: " + e); }
        }

        private void ForceStorm()
        {
            if (HostGateFail()) return;
            try
            {
                var ws = WeatherStorms.instance;
                if (ws == null) { _status = "нет WeatherStorms"; return; }
                if (_fStorms == null)
                    _fStorms = typeof(WeatherStorms).GetField("storms", BindingFlags.NonPublic | BindingFlags.Instance);
                var storms = _fStorms != null ? _fStorms.GetValue(ws) as WanderingStorm[] : null;
                if (storms == null || storms.Length == 0) { _status = "штормов нет в сцене"; return; }

                Vector3 player = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                WanderingStorm nearest = null;
                float best = float.MaxValue;
                foreach (var s in storms)
                {
                    if (s == null) continue;
                    float d = Vector3.Distance(s.transform.position, player);
                    if (d < best) { best = d; nearest = s; }
                }
                if (nearest == null) { _status = "штормы пустые"; return; }
                nearest.active = true;
                nearest.transform.position = player;
                _status = "шторм перемещён к игроку";
                Plugin.Logger.LogInfo("[DebugPanel] шторм '" + nearest.name + "' к игроку");
            }
            catch (Exception e) { _status = "шторм: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] шторм: " + e); }
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
                if (nearest == null) { _status = "порт не найден (далёкие не загружены)"; return; }
                if (boat == null) { _status = "нет лодки для телепорта"; return; }

                // Move the whole boat so the player (its child) ends up next to the port. Far islands
                // aren't streamed in under the floating origin, so this only reaches a loaded port.
                Vector3 target = nearest.transform.position + Vector3.up * 2f;
                boat.position += (target - player);
                _status = "лодка → порт " + SafePortName(nearest) + " (" + best.ToString("0") + " м)";
                Plugin.Logger.LogInfo("[DebugPanel] телепорт лодки к порту " + SafePortName(nearest));
            }
            catch (Exception e) { _status = "телепорт: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] телепорт: " + e); }
        }

        private static string SafePortName(IslandMarket m)
        {
            try { return m.GetPortName(); } catch { return m.name; }
        }

        // -----------------------------------------------------------------
        // 4. Item spawn — host (the new saveable auto-replicates via ItemSync)
        // -----------------------------------------------------------------

        private void DrawItemSpawn()
        {
            GUILayout.Label("— Спавн предмета (хост) —", _label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("фильтр:", _label, GUILayout.Width(50));
            _itemFilter = GUILayout.TextField(_itemFilter, GUILayout.Width(180));
            if (GUILayout.Button("Сброс", GUILayout.Width(60))) _itemFilter = "";
            GUILayout.EndHorizontal();

            EnsurePrefabs();
            string flt = _itemFilter != null ? _itemFilter.ToLowerInvariant() : "";

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(220));
            int shown = 0;
            foreach (var kv in _prefabs)
            {
                if (flt.Length > 0 && kv.Value.ToLowerInvariant().IndexOf(flt, StringComparison.Ordinal) < 0) continue;
                if (++shown > 60) { GUILayout.Label("… уточни фильтр (>60 совпадений)", _label); break; }
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Value, _label);
                if (GUILayout.Button("Спавн", GUILayout.Width(70))) SpawnItem(kv.Key);
                GUILayout.EndHorizontal();
            }
            if (shown == 0) GUILayout.Label("нет совпадений", _label);
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
                Plugin.Logger.LogInfo("[DebugPanel] каталог предметов: " + _prefabs.Count);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DebugPanel] каталог: " + e.Message); }
        }

        private void SpawnItem(int prefabIndex)
        {
            if (HostGateFail()) return;
            try
            {
                var dir = PrefabsDirectory.instance;
                if (dir == null || dir.directory == null || prefabIndex < 0 || prefabIndex >= dir.directory.Length)
                { _status = "нет префаба #" + prefabIndex; return; }
                var prefab = dir.directory[prefabIndex];
                if (prefab == null) { _status = "префаб #" + prefabIndex + " пуст"; return; }

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
                _status = "заспавнен " + prefab.name;
                Plugin.Logger.LogInfo("[DebugPanel] спавн '" + prefab.name + "' idx=" + prefabIndex);
            }
            catch (Exception e) { _status = "спавн: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] спавн: " + e); }
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
