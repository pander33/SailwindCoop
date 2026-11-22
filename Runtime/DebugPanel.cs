using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Dev-only test harness. Creates hard-to-reach co-op scenarios on demand (gold,
    /// item spawn, reputation, player needs, time/storm/teleport) without grinding to the right place/items.
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
        private Vector2 _panelScroll;
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

            const float w = 380f;
            float h = Mathf.Min(900f, Screen.height - 24f);
            var rect = new Rect(12, 12, w, h);
            GUI.Box(rect, "Debug Panel - Test Scenarios", _box);

            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 28, w - 20, h - 36));

            string role = _net.State == LinkState.Connected ? _net.Role.ToString() : "offline";
            GUILayout.Label("Role: " + role + "   ·   " + _status, _label);
            GUILayout.Space(4);

            _panelScroll = GUILayout.BeginScrollView(_panelScroll);
            DrawNeeds();
            GUILayout.Space(6);
            DrawGold();
            GUILayout.Space(6);
            DrawReputation();
            GUILayout.Space(6);
            DrawWorld();
            GUILayout.Space(6);
            DrawIslandTeleport();
            GUILayout.Space(6);
            DrawLinkSimulation();
            GUILayout.Space(6);
            DrawBoatBodies();
            DrawItemSpawn();
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private void DrawNeeds()
        {
            GUILayout.Label("- Player needs (local) -", _label);
            if (!GameState.playing || GameState.currentlyLoading || PlayerNeeds.instance == null)
            {
                GUILayout.Label("Load a game to change player needs.", _label);
                return;
            }

            DrawNeedSlider("Food", ref PlayerNeeds.food);
            DrawNeedSlider("Sleep", ref PlayerNeeds.sleep);
            DrawNeedSlider("Water", ref PlayerNeeds.water);
        }

        private void DrawNeedSlider(string name, ref float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(50));
            float shown = Mathf.Clamp(value, 0f, 100f);
            float selected = GUILayout.HorizontalSlider(shown, 0f, 100f, GUILayout.Width(190));
            if (selected != shown && !float.IsNaN(selected))
                value = Mathf.Round(selected);
            GUILayout.Label(value.ToString("0") + "%", _label);
            GUILayout.EndHorizontal();
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
                // debugOutCurrentBoat is the hull; the rigidbody and the saved position belong to its
                // parent, and moving the hull alone leaves them behind.
                Transform boat = BoatRoot(emb != null ? emb.debugOutCurrentBoat : null);
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
                ReleaseBoat(boat);
                boat.position += (target - player);
                _status = "boat -> port " + SafePortName(nearest) + " (" + best.ToString("0") + " m)";
                Plugin.Logger.LogInfo("[DebugPanel] boat teleported to port " + SafePortName(nearest));
            }
            catch (Exception e) { _status = "teleport: " + e.Message; Plugin.Logger.LogWarning("[DebugPanel] teleport: " + e); }
        }

        /// <summary>The object that carries the boat's rigidbody: the hull's parent when
        /// <paramref name="boat"/> is the hull (<c>GameState.currentBoat</c>,
        /// <c>debugOutCurrentBoat</c>).</summary>
        private static Transform BoatRoot(Transform boat)
        {
            Rigidbody body = boat != null ? boat.GetComponentInParent<Rigidbody>() : null;
            return body != null ? body.transform : boat;
        }

        /// <summary>The game's recovery recipe before a boat is moved: cast off, raise the anchor,
        /// stop. <paramref name="boat"/> is the boat root, the object with the rigidbody.</summary>
        private static void ReleaseBoat(Transform boat)
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
        // Лодка переносится так же, как это делает сама игра (Recovery.RecoverBoat,
        // DebugBoatTeleporter): под GameState.recovering, по этапам с ожиданием шагов физики,
        // в конце швартуется к причалу. Перенос одним вызовом вне recovering опрокидывал лодку
        // у хоста после остановки мира.
        // -----------------------------------------------------------------

        private List<RecoveryPort> _recoveryPorts;
        private Vector2 _islandScroll;
        private FieldInfo _fEmbarked;

        private void DrawIslandTeleport()
        {
            GUILayout.Label("- Island teleport (host moves the boat) -", _label);
            // The world was loaded again: the objects of the old list are gone.
            if (_recoveryPorts == null || (_recoveryPorts.Count > 0 && _recoveryPorts[0] == null)) RefreshRecoveryPorts();
            int count = (_recoveryPorts != null ? _recoveryPorts.Count : 0) + (_barePorts != null ? _barePorts.Count : 0);
            if (GUILayout.Button("Refresh port list (" + count + ")"))
                RefreshRecoveryPorts();

            if (count == 0)
            {
                GUILayout.Label("Recovery ports not found (world still loading?)", _label);
                return;
            }

            _islandScroll = GUILayout.BeginScrollView(_islandScroll, GUILayout.Height(140));
            foreach (var rp in _recoveryPorts)
            {
                if (rp == null) continue;
                // The game has switched this recovery point off; the place itself is still there.
                string off = rp.gameObject.activeInHierarchy ? "" : " (off in game)";
                if (GUILayout.Button(RecoveryPortName(rp) + off))
                    TeleportToIsland(rp);
            }
            foreach (var port in _barePorts)
            {
                if (port == null) continue;
                if (GUILayout.Button(SafePortName(port) + " (player only)"))
                    TeleportPlayerToPort(port);
            }
            GUILayout.EndScrollView();
        }

        // Ports the game gives no recovery point: there is no place to put a boat at.
        private List<Port> _barePorts = new List<Port>();

        /// <summary>
        /// One entry per port of the game. A recovery point that the game has switched off is
        /// listed too, so inactive objects are searched; a <c>RecoveryPort</c> without its port or
        /// its boat place (the game has such leftovers on the port objects) is not a place to go.
        /// </summary>
        private void RefreshRecoveryPorts()
        {
            _recoveryPorts = new List<RecoveryPort>();
            _barePorts = new List<Port>();
            try
            {
                var byPort = new Dictionary<Port, RecoveryPort>();
                foreach (var rp in Resources.FindObjectsOfTypeAll<RecoveryPort>())
                {
                    // Not an asset: an object of a loaded scene.
                    if (rp == null || !rp.gameObject.scene.IsValid() || rp.parentPort == null || rp.boatPos == null) continue;
                    if (byPort.TryGetValue(rp.parentPort, out var known) && (known.gameObject.activeInHierarchy || !rp.gameObject.activeInHierarchy))
                        continue;
                    byPort[rp.parentPort] = rp;
                }
                _recoveryPorts.AddRange(byPort.Values);
                _recoveryPorts.Sort((a, b) => string.CompareOrdinal(RecoveryPortName(a), RecoveryPortName(b)));

                if (Port.ports != null)
                    foreach (var port in Port.ports)
                        if (port != null && !byPort.ContainsKey(port)) _barePorts.Add(port);
                _barePorts.Sort((a, b) => string.CompareOrdinal(SafePortName(a), SafePortName(b)));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[DebugPanel] recovery port list: " + e.Message);
            }
        }

        private static string SafePortName(Port port)
        {
            try
            {
                string n = port.GetPortName();
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return port != null ? port.name : "?";
        }

        private static FieldInfo _fHorizonHeight;

        /// <summary>
        /// How far below its place the game holds the island of <paramref name="t"/> right now.
        /// A far island is sunk under the horizon (<c>IslandHorizon.ApplyNewHorizon</c>) with
        /// everything parented to it and comes up when the player is near. A recovery point
        /// usually leaves the island on Start, one that never started and the ports do not.
        /// </summary>
        private static float HorizonLift(Transform t)
        {
            try
            {
                var horizon = t != null ? t.GetComponentInParent<IslandHorizon>() : null;
                if (horizon == null) return 0f;
                if (_fHorizonHeight == null)
                    _fHorizonHeight = typeof(IslandHorizon).GetField("initialHeight", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fHorizonHeight == null) return 0f;
                return (float)_fHorizonHeight.GetValue(horizon) - horizon.transform.localPosition.y;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[DebugPanel] island height: " + e.Message);
                return 0f;
            }
        }

        /// <summary>A port without a recovery point: only a player on foot can be taken there.</summary>
        private void TeleportPlayerToPort(Port port)
        {
            try
            {
                if (IsEmbarked())
                {
                    _status = SafePortName(port) + ": no boat place in the game, go ashore first";
                    return;
                }
                MovePlayerTo(port.transform, turn: false);
                _status = "teleport -> " + SafePortName(port) + " (on foot)";
                Plugin.Logger.LogInfo("[DebugPanel] teleport -> " + SafePortName(port) + " (port without a recovery point, on foot)");
            }
            catch (Exception e)
            {
                _status = "teleport: " + e.Message;
                Plugin.Logger.LogWarning("[DebugPanel] teleport to port: " + e);
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

                // lastOwnedBoat is the boat root already, currentBoat is the hull under it.
                Transform boat = hostAuthority
                    ? BoatRoot(GameState.lastOwnedBoat != null ? GameState.lastOwnedBoat : GameState.currentBoat)
                    : null;
                var coop = CoopBehaviour.Instance;
                if (boat != null && coop != null)
                {
                    if (_boatTeleport || GameState.recovering)
                    {
                        _status = "teleport: a recovery or a teleport is still going on";
                        return;
                    }
                    coop.StartCoroutine(TeleportBoatLikeRecovery(coop, rp, boat, embarked));
                }
                else if (!embarked) MovePlayerToPort(rp);

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

        /// <summary>A player on foot goes to the port's spawn point. charController, like the
        /// game's own debug teleport <c>Port.teleportPlayer</c> (ovrController is an Oculus.VR
        /// type, and that assembly is not referenced).</summary>
        private static void MovePlayerToPort(RecoveryPort rp) => MovePlayerTo(rp.transform, turn: true);

        private static void MovePlayerTo(Transform place, bool turn)
        {
            Vector3 pos = place.position + Vector3.up * (HorizonLift(place) + 1f);
            if (Refs.charController != null)
                Refs.charController.transform.position = pos;
            if (Refs.observerMirror != null)
            {
                Refs.observerMirror.transform.position = pos;
                if (turn) Refs.observerMirror.transform.rotation = place.rotation;
            }
        }

        private bool _boatTeleport;

        // Longer than any island takes to load; the wait must not hold the recovery mode for ever.
        private const float SceneWaitSec = 30f;

        /// <summary>
        /// The game's own way to move a boat to a port (<c>Recovery.RecoverBoat</c>,
        /// <c>DebugBoatTeleporter.TeleportBoat</c>): under <c>GameState.recovering</c>, in stages
        /// with physics steps between them, moored at the end. Under that flag the game shifts
        /// the origin instantly by itself and <c>BoatPhysicsSwitcher</c> stays out of a stopped
        /// world. The end is what <c>RecoveryPatches</c> does after the game's recovery: the
        /// host's origin, the boats' state, the 1.5 s hold. The flag is dropped only after the
        /// hold, as the game drops it seconds after the boat has arrived.
        /// </summary>
        private IEnumerator TeleportBoatLikeRecovery(CoopBehaviour coop, RecoveryPort rp, Transform boat, bool embarked)
        {
            _boatTeleport = true;
            GameState.recovering = true;
            // A recovery of the game started meanwhile takes the flag over: it is then the game's
            // to clear, and this teleport must leave the boat to it.
            int recovery = Sync.RecoveryPatches.Generation;
            try
            {
                var ropes = boat.GetComponent<BoatMooringRopes>();
                ReleaseBoat(boat);
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();
                // The recovery watchdog, or a session torn down, has ended the mode: leave the boat.
                if (!GameState.recovering || recovery != Sync.RecoveryPatches.Generation || boat == null || rp == null) yield break;

                if (!embarked) MovePlayerToPort(rp);
                boat.position = rp.GetBoatPos() + Vector3.up * HorizonLift(rp.boatPos);
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();
                if (!GameState.recovering || recovery != Sync.RecoveryPatches.Generation || boat == null || rp == null) yield break;

                if (rp.boatPos != null) boat.rotation = rp.boatPos.rotation;
                // A port without its moorings must not end the teleport half way: the rest
                // (the origin, the resend, the hold) is still owed.
                try
                {
                    if (ropes != null && rp.mooringFront != null) ropes.MoorClosestRope(rp.mooringFront);
                    if (ropes != null && rp.mooringBack != null) ropes.MoorClosestRope(rp.mooringBack);
                }
                catch (Exception e) { Plugin.Logger.LogWarning("[DebugPanel] boat teleport, mooring: " + e.Message); }
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();

                // The island under the boat, as the game's recovery waits for it.
                float until = Time.realtimeSinceStartup + SceneWaitSec;
                while (GameState.loadingScenes > 0 && Time.realtimeSinceStartup < until)
                    yield return new WaitForEndOfFrame();
                if (!GameState.recovering || recovery != Sync.RecoveryPatches.Generation) yield break;

                Sync.RecoveryPatches.SettleMovedBoat();
                while (coop.Pause != null && coop.Pause.Settling) yield return null;
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                Plugin.Logger.LogInfo("[DebugPanel] boat teleport done: " + RecoveryPortName(rp));
            }
            finally
            {
                _boatTeleport = false;
                if (recovery == Sync.RecoveryPatches.Generation) GameState.recovering = false;
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
        // Link simulation — this machine as a LAN client (delay, jitter and loss below LiteNetLib)
        // -----------------------------------------------------------------

        private void DrawLinkSimulation()
        {
            GUILayout.Label("- Bad link (LAN client) -", _label);
            bool on = GUILayout.Toggle(LinkSimulation.Enabled, " Simulate on the next Join");
            if (on != LinkSimulation.Enabled) LinkSimulation.Enabled = on;
            LinkSimulation.DelayMs = SimSlider("delay", LinkSimulation.DelayMs, 500, " ms each way");
            LinkSimulation.JitterMs = SimSlider("jitter", LinkSimulation.JitterMs, 200, " ms");
            LinkSimulation.LossPercent = SimSlider("loss", LinkSimulation.LossPercent, 30, " %");
            string state;
            if (_net.LinkSimulated)
                state = "active: " + LinkSimulation.Forwarded + " passed, " + LinkSimulation.Dropped +
                        " lost, RTT " + _net.RttMs.ToString("F0") + " ms";
            else if (_net.State == LinkState.Connected)
                state = _net.Role == Role.Host ? "not on a host: enable it on the joining copy"
                                               : "this session joined without it: rejoin to apply";
            else
                state = on ? "starts with the next LAN Join" : "off";
            GUILayout.Label(state, _label);
        }

        // -----------------------------------------------------------------
        // Boat bodies — a client places the body (the hull's parent in game 0.39), kinematic
        // -----------------------------------------------------------------

        private readonly List<string> _boatLines = new List<string>();

        private void DrawBoatBodies()
        {
            var boats = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Boats : null;
            if (boats == null || _net.State != LinkState.Connected || _net.Role != Role.Client) return;
            GUILayout.Label("- Boat bodies (client) -", _label);
            _boatLines.Clear();
            try { boats.DescribeClientBoats(_boatLines); }
            catch (Exception e) { _boatLines.Add("unavailable: " + e.Message); }
            if (_boatLines.Count == 0) GUILayout.Label("no boat placed yet", _label);
            foreach (string line in _boatLines) GUILayout.Label(line, _label);
            GUILayout.Label("own damage ignored: " + Sync.BoatSync.IgnoredImpacts + " hits, " +
                            Sync.BoatSync.IgnoredOverflows + " water", _label);
            GUILayout.Space(6);
        }

        private int SimSlider(string name, int value, int max, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(50));
            int result = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, 0f, max, GUILayout.Width(190)));
            GUILayout.Label(result + unit, _label);
            GUILayout.EndHorizontal();
            return result;
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

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(200));
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
                // A Good prefab carries missionIndex 0: without this the crate belongs to mission slot 0
                // (delivery text, cannot be sold). IslandMarket.SpawnGood does the same for a bought good.
                var good = go.GetComponent<Good>();
                if (good != null) good.RegisterAsMissionless();
                var sp = go.GetComponent<SaveablePrefab>();
                if (sp != null) sp.RegisterToSave();
                var item = go.GetComponent<ShipItem>();
                if (item != null)
                {
                    SailwindCoop.Sync.ItemSync.DisableTemplateMeshCollider(item);
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
