using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// AI-корабли (<c>NPCBoatController</c>) — host-authoritative, как и лодка игрока.
    ///
    /// Раньше их не синхронизировали вовсе, и у каждой машины ходил свой флот: гость видел пустое
    /// море там, где хост встречал торговца. Теперь пары «поза + входы ИИ» едут снапшотами, а клиент
    /// ведёт корпус через <see cref="NetTransform"/>.
    ///
    /// Три решения, которые стоит понимать, прежде чем это править:
    ///
    /// 1. **Появления не передаются.** NPC-лодки — фиксированные объекты сцены, не рантайм-спавн;
    ///    подробности и идентификатор — в <see cref="NpcBoatLocator"/>.
    ///
    /// 2. **Паруса не передаются.** Их крутит <c>NPCBoatController.Update</c> из трёх полей ИИ, и мы
    ///    синхронизируем эти входы, оставляя расчёт локальной симуляции клиента — иначе вместо
    ///    плавного набора парусов будут ступеньки с частотой снапшотов. Тот же приём, что в
    ///    <see cref="CrestWaterSync"/>.
    ///
    /// 3. **Радиус активации — часть фичи, а не оптимизация.** <c>BoatHorizon.DistanceCheck</c>
    ///    считает расстояние до ЛОКАЛЬНОГО игрока, а <c>NPCBoatController.FixedUpdate</c> и
    ///    <c>Update</c> целиком выходят при <c>!closeToPlayer</c>. ИИ живёт только у хоста, поэтому
    ///    корабль, далёкий от хоста, но близкий к гостю, у хоста заморожен — и гость смотрит на
    ///    статую, сколько бы снапшотов ни ходило. Расширяет радиус <see cref="NpcBoatPatches"/>.
    /// </summary>
    public sealed class NpcBoatSync
    {
        public static NpcBoatSync Instance { get; private set; }

        private readonly CoopNet _net;

        /// <summary>Частота снапшотов. Корабли ИИ тихоходны, 5 Гц с интерполяцией хватает.</summary>
        public float SendHz = 5f;

        /// <summary>Пороги отправки: ниже них движение неразличимо, а канал занимать незачем.</summary>
        private const float PosEpsilon = 0.1f;
        private const float RotEpsilonDeg = 1f;

        /// <summary>Даже неподвижный корабль подтверждает себя раз в секунду — чтобы поздно вошедший
        /// клиент получил его позу, не дожидаясь, пока тот тронется.</summary>
        private const float KeepaliveSec = 1f;

        private float _sendStamp = float.NegativeInfinity;
        private int _rotateStart;   // с какого индекса начинать обход, чтобы никто не голодал

        private sealed class HostEntry
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public int Target = int.MinValue;
            public int Dock = int.MinValue;
            public float Stamp = float.NegativeInfinity;
        }

        private readonly Dictionary<int, HostEntry> _sent = new Dictionary<int, HostEntry>();
        private readonly List<NpcBoatStateMsg.Entry> _batch =
            new List<NpcBoatStateMsg.Entry>(NpcBoatStateMsg.MaxPerPacket);

        private sealed class ClientBoat
        {
            public NPCBoatController Controller;
            public Rigidbody Rb;
            public bool PrevKinematic;
            public RigidbodyInterpolation PrevInterp;
            public bool Engaged;
            public readonly NetTransform Net = new NetTransform();
            public int Target = int.MinValue;
            public int Dock = int.MinValue;
        }

        private readonly Dictionary<int, ClientBoat> _client = new Dictionary<int, ClientBoat>();
        private int _engagedLogged = -1;
        private Runtime.CoopLog.Repeat _hostReport;
        private Runtime.CoopLog.Repeat _clientReport;

        public NpcBoatSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>Сколько кораблей клиент сейчас ведёт по сети. Для оверлея и дампа.</summary>
        public int SlavedCount => _client.Count;

        // -----------------------------------------------------------------
        // Хост: сбор и рассылка
        // -----------------------------------------------------------------

        public void TickHost()
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;

            // На паузе хоста мир стоит, ИИ не считается и клиент всё равно заморожен
            // (см. HostPauseSync) — гнать keepalive в это время незачем.
            if (Time.timeScale <= 0.0001f) return;

            float interval = 1f / Mathf.Max(1f, SendHz);
            if (Time.unscaledTime - _sendStamp < interval) return;
            _sendStamp = Time.unscaledTime;

            try
            {
                var boats = NpcBoatLocator.FindBoats();
                if (boats.Count == 0) return;

                _batch.Clear();
                int scanned = 0;
                int i = _rotateStart >= boats.Count ? 0 : _rotateStart;
                while (scanned < boats.Count && _batch.Count < NpcBoatStateMsg.MaxPerPacket)
                {
                    var c = boats[i];
                    if (c != null && NeedsSend(i, c)) _batch.Add(Capture(i, c));

                    i++;
                    if (i >= boats.Count) i = 0;
                    scanned++;
                }
                // Продолжить со следующего за последним осмотренным, а не с начала: иначе при полной
                // пачке хвост списка не отправлялся бы никогда.
                _rotateStart = i;

                if (_batch.Count == 0) return;

                _net.Broadcast(new NpcBoatStateMsg
                {
                    Tick = _net.Clock.ServerTick,
                    Boats = _batch.ToArray(),
                }, LiteNetLib.DeliveryMethod.Unreliable);
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[NpcBoatSync] Host capture failed", e.Message, ref _hostReport);
            }
        }

        private bool NeedsSend(int index, NPCBoatController c)
        {
            HostEntry prev;
            if (!_sent.TryGetValue(index, out prev)) return true;
            if (Time.unscaledTime - prev.Stamp >= KeepaliveSec) return true;
            if (c.currentTargetIndex != prev.Target || c.currentDockIndex != prev.Dock) return true;

            Transform t = c.transform;
            if ((t.position - prev.Pos).sqrMagnitude > PosEpsilon * PosEpsilon) return true;
            if (Quaternion.Angle(t.rotation, prev.Rot) > RotEpsilonDeg) return true;
            return false;
        }

        private NpcBoatStateMsg.Entry Capture(int index, NPCBoatController c)
        {
            Transform t = c.transform;
            var rb = c.GetComponent<Rigidbody>();

            HostEntry prev;
            if (!_sent.TryGetValue(index, out prev)) { prev = new HostEntry(); _sent[index] = prev; }
            prev.Pos = t.position;
            prev.Rot = t.rotation;
            prev.Target = c.currentTargetIndex;
            prev.Dock = c.currentDockIndex;
            prev.Stamp = Time.unscaledTime;

            return new NpcBoatStateMsg.Entry
            {
                Index = (ushort)index,
                RealPos = CoordSpace.LocalToReal(t.position),
                Rot = t.rotation,
                // Скорость — величина разностная, сдвиг плавающего начала координат в неё не входит,
                // поэтому переводить её в реальное пространство не нужно.
                RealVel = rb != null && !rb.isKinematic ? rb.velocity : Vector3.zero,
                TargetIndex = ToShort(c.currentTargetIndex),
                DockIndex = ToShort(c.currentDockIndex),
                ParkedTimer = c.parkedTimer,
            };
        }

        private static short ToShort(int v)
        {
            if (v < short.MinValue) return short.MinValue;
            if (v > short.MaxValue) return short.MaxValue;
            return (short)v;
        }

        // -----------------------------------------------------------------
        // Клиент: приём и применение
        // -----------------------------------------------------------------

        public void OnNpcBoatState(NpcBoatStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client || msg == null || msg.Boats == null) return;

            try
            {
                for (int i = 0; i < msg.Boats.Length; i++)
                {
                    var e = msg.Boats[i];
                    var cb = Engage(e.Index);
                    if (cb == null) continue;

                    cb.Net.Push(msg.Tick, e.RealPos, e.Rot, e.RealVel);
                    MirrorAi(cb, e);
                }

                if (_engagedLogged != _client.Count)
                {
                    _engagedLogged = _client.Count;
                    Plugin.Logger.LogInfo("[NpcBoatSync] slaving " + _client.Count + " AI boats" +
                                          (NpcBoatLocator.UsingFallback ? " (hierarchy numbering)" : ""));
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[NpcBoatSync] Could not apply AI boat state", e.Message,
                                          ref _clientReport);
            }
        }

        /// <summary>
        /// Переводит лодку под сетевое ведение: физику глушим, дальше её двигают только снапшоты.
        /// <c>NPCBoatController</c> при этом НЕ отключаем — его <c>FixedUpdate</c> на кинематическом
        /// теле становится пустышкой сам собой, а <c>Update</c> нужен: именно он анимирует паруса из
        /// зеркалимых нами полей ИИ.
        /// </summary>
        private ClientBoat Engage(int index)
        {
            ClientBoat cb;
            if (_client.TryGetValue(index, out cb))
            {
                if (cb.Controller == null) { _client.Remove(index); }   // сцену выгрузили
                else return cb;
            }

            var c = NpcBoatLocator.FindByIndex(index);
            if (c == null) return null;

            cb = new ClientBoat { Controller = c, Rb = c.GetComponent<Rigidbody>() };
            if (cb.Rb != null)
            {
                cb.PrevKinematic = cb.Rb.isKinematic;
                cb.PrevInterp = cb.Rb.interpolation;
                cb.Rb.isKinematic = true;
                cb.Rb.interpolation = RigidbodyInterpolation.None;
            }
            cb.Engaged = true;
            _client[index] = cb;
            return cb;
        }

        /// <summary>
        /// Переносит входы ИИ. Путевые точки резолвим только при смене индекса: <c>GetWaypointTransform</c>
        /// сам отдаёт null для -1, так что «нет цели» и «не пришвартован» выражаются штатно.
        /// </summary>
        private void MirrorAi(ClientBoat cb, NpcBoatStateMsg.Entry e)
        {
            var c = cb.Controller;
            if (c == null) return;

            c.parkedTimer = e.ParkedTimer;

            if (cb.Target != e.TargetIndex)
            {
                cb.Target = e.TargetIndex;
                c.currentTargetIndex = e.TargetIndex;
                c.currentTarget = Waypoint(e.TargetIndex);
            }
            if (cb.Dock != e.DockIndex)
            {
                cb.Dock = e.DockIndex;
                c.currentDockIndex = e.DockIndex;
                c.currentDock = Waypoint(e.DockIndex);
            }
        }

        private static Transform Waypoint(int index)
        {
            try
            {
                var mgr = NPCBoatWaypointManager.instance;
                return mgr != null ? mgr.GetWaypointTransform(index) : null;
            }
            catch { return null; }
        }

        /// <summary>Ведём корпуса. Отдельным шагом кадра, потому что снапшоты приходят реже кадров.</summary>
        public void ApplyRemote()
        {
            if (_net.Role != Role.Client || _client.Count == 0) return;
            if (!CoordSpace.Ready) return;

            long now = _net.Clock.ServerTick;
            foreach (var cb in _client.Values)
            {
                if (cb.Controller == null || !cb.Net.HasData) continue;
                cb.Net.Apply(cb.Controller.transform, now);
            }
        }

        /// <summary>Ведём ли мы эту лодку по сети — спрашивают патчи в <see cref="NpcBoatPatches"/>.</summary>
        public bool IsSlaved(Transform boatRoot)
        {
            if (boatRoot == null || _client.Count == 0) return false;
            foreach (var cb in _client.Values)
                if (cb.Controller != null && cb.Controller.transform == boatRoot) return true;
            return false;
        }

        public void Clear()
        {
            foreach (var cb in _client.Values)
            {
                if (!cb.Engaged || cb.Rb == null) continue;
                try
                {
                    cb.Rb.isKinematic = cb.PrevKinematic;
                    cb.Rb.interpolation = cb.PrevInterp;
                }
                catch { }
            }
            _client.Clear();
            _sent.Clear();
            _batch.Clear();
            _rotateStart = 0;
            _sendStamp = float.NegativeInfinity;
            _engagedLogged = -1;
            NpcBoatLocator.Invalidate();
        }
    }

    /// <summary>
    /// Два патча вокруг <c>BoatHorizon</c>, без которых синхронизация AI-кораблей работает наполовину.
    ///
    /// <b>Хост, <c>DistanceCheck</c>.</b> Ванильный радиус активации меряется до локального игрока, а
    /// ИИ крутится только у хоста. Корабль в 1500 единицах от хоста, но в 50 от гостя, заморожен —
    /// гость видит статую. Постфикс расширяет радиус на всех игроков сразу.
    ///
    /// <b>Клиент, <c>UpdateKinematic</c>.</b> Ваниль каждый кадр возвращает <c>isKinematic = false</c>
    /// ближним лодкам. Для ведомого корпуса это означает, что физика начнёт бороться со снапшотами.
    /// Префикс пропускает вызов для тех лодок, которые ведём мы.
    ///
    /// Оба обработчика глотают свои исключения: бросок отсюда уйдёт в <c>Update</c> движка и сломает
    /// не только нас.
    /// </summary>
    public static class NpcBoatPatches
    {
        /// <summary>Ванильный радиус из <c>BoatHorizon.DistanceCheck</c>.</summary>
        private const float ActivationRadius = 1000f;

        private static FieldInfo _cooldownField;
        private static readonly List<Vector3> _remotePositions = new List<Vector3>(8);
        private static Runtime.CoopLog.Repeat _patchReport;

        public static void Apply(Harmony harmony)
        {
            int ok = 0;
            ok += TryPatch(harmony, "DistanceCheck", nameof(PostDistanceCheck), postfix: true) ? 1 : 0;
            ok += TryPatch(harmony, "UpdateKinematic", nameof(PreUpdateKinematic), postfix: false) ? 1 : 0;

            try
            {
                _cooldownField = AccessTools.Field(typeof(BoatHorizon), "updateCooldown");
            }
            catch { _cooldownField = null; }

            Plugin.Logger.LogInfo("[NpcBoatPatches] BoatHorizon patches: " + ok + "/2");
            Runtime.PatchHealth.Report("NpcBoat", ok, 2);
        }

        private static bool TryPatch(Harmony harmony, string method, string handler, bool postfix)
        {
            try
            {
                var mi = AccessTools.Method(typeof(BoatHorizon), method);
                if (mi == null) return false;
                var hm = new HarmonyMethod(typeof(NpcBoatPatches).GetMethod(
                    handler, BindingFlags.Static | BindingFlags.NonPublic));
                if (postfix) harmony.Patch(mi, postfix: hm);
                else harmony.Patch(mi, prefix: hm);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[NpcBoatPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static void PostDistanceCheck(BoatHorizon __instance)
        {
            try
            {
                if (__instance == null || __instance.closeToPlayer) return;

                var coop = Runtime.CoopBehaviour.Instance;
                if (coop == null || coop.Net == null) return;
                if (coop.Net.Role != Role.Host || coop.Net.State != LinkState.Connected) return;
                if (coop.Players == null) return;

                _remotePositions.Clear();
                coop.Players.CollectRemotePositions(_remotePositions);
                if (_remotePositions.Count == 0) return;

                Vector3 at = __instance.transform.position;
                for (int i = 0; i < _remotePositions.Count; i++)
                {
                    if ((_remotePositions[i] - at).sqrMagnitude > ActivationRadius * ActivationRadius)
                        continue;

                    __instance.closeToPlayer = true;
                    // Ваниль, признав лодку далёкой, ставит паузу 10-20 с до следующей проверки. Раз мы
                    // её только что оживили, проверять надо снова каждый кадр — иначе она останется
                    // «близкой» ещё до двадцати секунд после того, как гость уплывёт.
                    if (_cooldownField != null) _cooldownField.SetValue(__instance, 0f);
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[NpcBoatPatches] DistanceCheck postfix failed", e.Message,
                                          ref _patchReport);
            }
        }

        private static bool PreUpdateKinematic(BoatHorizon __instance)
        {
            try
            {
                var sync = NpcBoatSync.Instance;
                if (sync == null || __instance == null) return true;
                // BoatHorizon висит на дочернем объекте: корпус с Rigidbody — его родитель.
                return !sync.IsSlaved(__instance.transform.parent);
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[NpcBoatPatches] UpdateKinematic prefix failed", e.Message,
                                          ref _patchReport);
                return true;
            }
        }
    }
}
