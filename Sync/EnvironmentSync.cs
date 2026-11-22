using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 1 — global environment (host -> client): wind, sun/time-of-day, moon.
    /// These are simple global singletons, so they don't need interpolation; we send
    /// a few snapshots a second and the client writes them straight onto the engine.
    ///
    /// Wind is the one subtlety: <c>Wind.Update</c> drifts <c>currentWind</c> toward its
    /// own random targets and would fight us between snapshots, so on the client we
    /// disable the Wind component and drive its static fields directly (restored on
    /// disconnect). The sun advances smoothly on its own; we just re-anchor its time
    /// each snapshot, which stays imperceptible since the sun moves slowly.
    ///
    /// Weather/storms are deliberately out of scope here — see <see cref="EnvStateMsg"/>.
    /// </summary>
    public sealed class EnvironmentSync
    {
        private readonly CoopNet _net;
        private float _sendStamp = float.NegativeInfinity;
        private float _receivedStamp = float.NegativeInfinity;
        private uint _pauseRevision;
        private long _snapshotTick = long.MinValue;
        private float _scaledSinceSend, _realSinceSend;
        private bool _windDisabled;     // client: did we turn off the local Wind sim?
        private bool _wavesDisabled;    // client: did we turn off the local WavesInertia sim?
        private WavesInertia _waves;    // cached; no singleton on the engine type

        /// <summary>Часы и форма воды живут в <see cref="CrestWaterSync"/> — это состояние среды,
        /// поэтому едет оно в этом же снапшоте, но вся логика там.</summary>
        public CrestWaterSync Crest;

        /// <summary>Host simulation rate from the last snapshot, including capped frames during shared sleep.</summary>
        public float HostTimeScale { get; private set; } = 1f;
        /// <summary>Пришёл ли хоть один снапшот среды — до него <see cref="HostTimeScale"/> вымышлен.</summary>
        public bool HasHostState { get; private set; }
        internal float SecondsSinceHostState => Time.unscaledTime - _receivedStamp;
        // The slowest supported snapshot interval is two seconds. Allow one missed sample.
        internal bool HasRecentHostState => SecondsSinceHostState < 5f;

        /// <summary>Environment snapshot rate (Hz). Slow — env changes gradually.</summary>
        public float EnvHz = 4f;

        public EnvironmentSync(CoopNet net) { _net = net; }

        internal static void PatchSun(Harmony harmony)
        {
            var sunPaused = AccessTools.DeclaredMethod(typeof(Sun), "SunPaused", Type.EmptyTypes);
            if (sunPaused == null) throw new MissingMethodException(typeof(Sun).Name, "SunPaused");
            harmony.Patch(sunPaused, postfix: new HarmonyMethod(typeof(EnvironmentSync), nameof(HoldSun)));
        }

        private static void HoldSun(ref bool __result)
        {
            try { __result |= Runtime.CoopBehaviour.Instance?.HostPause?.Frozen == true; }
            catch (Exception e) { Plugin.Logger.LogWarning("[EnvironmentSync] Could not hold sun: " + e.Message); }
        }

        // Client: wind moved from the value at the last snapshot to the new one over one interval.
        private const float WindSnapDegrees = 90f;
        private bool _haveWindTarget;
        private float _windT = 1f;
        private Vector3 _windFrom, _windTo, _baseFrom, _baseTo;
        private Quaternion _rotFrom = Quaternion.identity, _rotTo = Quaternion.identity;

        private void SmoothWind(float dt)
        {
            if (!_haveWindTarget) return;
            _windT = Mathf.Min(1f, _windT + dt * Mathf.Max(1f, EnvHz));
            global::Wind.currentWind = Vector3.Lerp(_windFrom, _windTo, _windT);
            global::Wind.currentBaseWind = Vector3.Lerp(_baseFrom, _baseTo, _windT);
            global::Wind.windRotation = Quaternion.Slerp(_rotFrom, _rotTo, _windT);
        }

        // -----------------------------------------------------------------
        // Host: capture and broadcast
        // -----------------------------------------------------------------

        public void Tick(float dt, uint pauseRevision)
        {
            if (_net.Role == Role.Client) { SmoothWind(dt); if (Crest != null) Crest.Tick(); return; }
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;

            _scaledSinceSend += dt;
            _realSinceSend += Time.unscaledDeltaTime;

            // ВАЖНО: интервал по НЕмасштабируемому времени, а не по dt.
            // CoopBehaviour передаёт сюда Time.deltaTime, а он на паузе хоста равен нулю — таймер
            // переставал накапливаться, снапшоты не уходили вовсе, и клиент никогда не узнавал
            // HostTimeScale = 0. Его волновые часы продолжали ехать на последнем известном
            // масштабе 1.0, вода шла при стоящем хосте, а на снятии паузы прилетал скачок фазы.
            // Именно этот снапшот и обязан ходить во время паузы: он её и объявляет.
            float interval = 1f / Mathf.Max(0.5f, EnvHz);
            if (Time.unscaledTime - _sendStamp < interval) return;
            _sendStamp = Time.unscaledTime;

            var msg = new EnvStateMsg
            {
                Tick = _net.Clock.ServerTick,
                PauseRevision = pauseRevision,
                Wind = global::Wind.currentWind,
                BaseWind = global::Wind.currentBaseWind,
                WindRot = global::Wind.windRotation,
                Day = GameState.day,
            };

            var sun = Sun.sun;
            if (sun != null)
            {
                if (FreeCamCompat.IsPhotoModeOpen && _hasNormalSunTime)
                {
                    // Photo Mode may scrub the host's sun; keep the crew's clock (and day) running at the last
                    // observed rate instead of freezing it until the host closes Photo Mode.
                    PhotoModeClock.Extrapolate(_lastNormalDay, _lastNormalGlobalTime, _lastNormalLocalTime, _sunRate,
                        Time.time - _lastNormalStamp, out int day, out float global, out float local);
                    msg.Day = day;
                    msg.GlobalTime = global;
                    msg.LocalTime = local;
                }
                else
                {
                    msg.GlobalTime = sun.globalTime;
                    msg.LocalTime = sun.localTime;
                    float now = Time.time;
                    if (_hasNormalSunTime && PhotoModeClock.SampleRate(_lastNormalGlobalTime, sun.globalTime, now - _lastNormalStamp, out float rate))
                        _sunRate = _sunRate <= 0f ? rate : Mathf.Lerp(_sunRate, rate, 0.3f);
                    _lastNormalDay = GameState.day;
                    _lastNormalGlobalTime = sun.globalTime;
                    _lastNormalLocalTime = sun.localTime;
                    _lastNormalStamp = now;
                    _hasNormalSunTime = true;
                }
                msg.Timescale = sun.timescale;
            }

            var moon = Moon.instance;
            if (moon != null) msg.MoonPhase = moon.currentPhase;

            var waves = FindWaves();
            if (waves != null)
            {
                msg.HasWaves = true;
                msg.WavesRot = waves.transform.rotation;
                msg.WavesInertia = waves.currentInertia;
                msg.WavesMagnitude = waves.currentMagnitude;
            }

            // Use the whole sample window so a single short or long frame does not bias the rate.
            // Zero stays reserved for an actual pause. Other consumers also use this rate to
            // advance the host's ocean clock between snapshots.
            msg.HostTimeScale = SleepSync.Instance?.IsWarpingWorld == true
                ? SharedSleepTiming.EffectiveScale(Time.timeScale, _scaledSinceSend, _realSinceSend)
                : Time.timeScale;
            _scaledSinceSend = _realSinceSend = 0f;

            if (Crest != null) Crest.Capture(msg);

            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.Unreliable);
        }

        private float _lastNormalGlobalTime, _lastNormalLocalTime, _lastNormalStamp, _sunRate;
        private int _lastNormalDay;
        private bool _hasNormalSunTime;

        private WavesInertia FindWaves()
        {
            if (_waves == null) _waves = UnityEngine.Object.FindObjectOfType<WavesInertia>();
            return _waves;
        }

        // -----------------------------------------------------------------
        // Client: apply
        // -----------------------------------------------------------------

        public void OnEnvState(EnvStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client || msg.PauseRevision < _pauseRevision ||
                msg.Tick <= _snapshotTick) return;
            _pauseRevision = msg.PauseRevision;
            _snapshotTick = msg.Tick;

            // Wind: take over from the local simulation so it stops fighting us.
            var wind = global::Wind.instance;
            if (wind != null && wind.enabled)
            {
                wind.enabled = false;
                _windDisabled = true;
            }
            // The snapshot is a target reached over one snapshot interval (SmoothWind): set at once,
            // the wind stepped four times a second, seen in the cloth, the flags and the sound.
            // The first snapshot and a large turn (a load, a storm front) are taken at once.
            bool snap = !_haveWindTarget ||
                        Vector3.Angle(global::Wind.currentWind, msg.Wind) > WindSnapDegrees;
            _windFrom = global::Wind.currentWind; _windTo = msg.Wind;
            _baseFrom = global::Wind.currentBaseWind; _baseTo = msg.BaseWind;
            _rotFrom = global::Wind.windRotation; _rotTo = msg.WindRot;
            _windT = snap ? 1f : 0f;
            _haveWindTarget = true;
            if (snap) SmoothWind(0f);

            // Time / sun: re-anchor; the client sun keeps advancing between snapshots.
            var sun = Sun.sun;
            if (sun != null)
            {
                if (!FreeCamCompat.IsPhotoModeOpen)
                {
                    sun.globalTime = msg.GlobalTime;
                    sun.localTime = msg.LocalTime;
                }
                sun.timescale = msg.Timescale;
            }
            GameState.day = msg.Day;

            // Moon phase (harmless even if Moon.Update recomputes it from the synced time).
            var moon = Moon.instance;
            if (moon != null) moon.currentPhase = msg.MoonPhase;

            // Waves: same takeover as the wind — WavesInertia.Update drifts toward the local wind
            // and would fight the snapshots. Sea state must match or the host-authoritative boat
            // visibly sinks under (or floats above) the client's water.
            if (msg.HasWaves)
            {
                var waves = FindWaves();
                if (waves != null)
                {
                    if (waves.enabled)
                    {
                        waves.enabled = false;
                        _wavesDisabled = true;
                    }
                    waves.LoadInertia(msg.WavesRot, msg.WavesInertia, msg.WavesMagnitude);
                }
            }

            HostTimeScale = msg.HostTimeScale;
            HasHostState = true;
            _receivedStamp = Time.unscaledTime;
            if (Crest != null) Crest.ApplyRemote(_net, msg);

        }

        internal void OnPauseState(PauseStateMsg msg)
        {
            // A newer environment sample may arrive ahead of the reliable packet. Its measured
            // simulation rate takes precedence over the transition's instantaneous rate.
            if (msg.Revision < _pauseRevision || (msg.Revision == _pauseRevision && HasHostState)) return;
            _pauseRevision = msg.Revision;
            HostTimeScale = msg.HostTimeScale;
            HasHostState = true;
            _receivedStamp = Time.unscaledTime;
            Crest?.SetHostTimeScale(HostTimeScale);
        }

        public void Clear()
        {
            // Hand the wind back to the local simulation for single-player.
            if (_windDisabled)
            {
                var wind = global::Wind.instance;
                if (wind != null) wind.enabled = true;
                _windDisabled = false;
            }
            if (_wavesDisabled)
            {
                if (_waves != null) _waves.enabled = true;
                _wavesDisabled = false;
            }
            _waves = null;
            _sendStamp = float.NegativeInfinity;
            _receivedStamp = float.NegativeInfinity;
            _pauseRevision = 0;
            _snapshotTick = long.MinValue;
            _scaledSinceSend = _realSinceSend = 0f;
            _haveWindTarget = false;
            _windT = 1f;
            _hasNormalSunTime = false;
            _sunRate = 0f;

            // Снимок хоста больше не действителен. Без сброса сессия, оборвавшаяся на паузе хоста,
            // оставила бы HostTimeScale = 0 навсегда: следующий клиент был бы заморожен
            // (HostPauseSync) ещё до первого снапшота, а SleepSync считал бы восстановление сил по
            // масштабу чужой, давно закрытой игры.
            HasHostState = false;
            HostTimeScale = 1f;

        }
    }

}
