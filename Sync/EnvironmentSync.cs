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
        private float _sendTimer;
        private bool _windDisabled;     // client: did we turn off the local Wind sim?
        private bool _wavesDisabled;    // client: did we turn off the local WavesInertia sim?
        private WavesInertia _waves;    // cached; no singleton on the engine type

        /// <summary>Environment snapshot rate (Hz). Slow — env changes gradually.</summary>
        public float EnvHz = 4f;

        public EnvironmentSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: capture and broadcast
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;

            float interval = 1f / Mathf.Max(0.5f, EnvHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            var msg = new EnvStateMsg
            {
                Tick = _net.Clock.ServerTick,
                Wind = global::Wind.currentWind,
                BaseWind = global::Wind.currentBaseWind,
                WindRot = global::Wind.windRotation,
                Day = GameState.day,
            };

            var sun = Sun.sun;
            if (sun != null)
            {
                msg.GlobalTime = sun.globalTime;
                msg.LocalTime = sun.localTime;
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

            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.Unreliable);
        }

        private WavesInertia FindWaves()
        {
            if (_waves == null) _waves = Object.FindObjectOfType<WavesInertia>();
            return _waves;
        }

        // -----------------------------------------------------------------
        // Client: apply
        // -----------------------------------------------------------------

        public void OnEnvState(EnvStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;

            // Wind: take over from the local simulation so it stops fighting us.
            var wind = global::Wind.instance;
            if (wind != null && wind.enabled)
            {
                wind.enabled = false;
                _windDisabled = true;
            }
            global::Wind.currentWind = msg.Wind;
            global::Wind.currentBaseWind = msg.BaseWind;
            global::Wind.windRotation = msg.WindRot;

            // Time / sun: re-anchor; the client sun keeps advancing between snapshots.
            var sun = Sun.sun;
            if (sun != null)
            {
                sun.globalTime = msg.GlobalTime;
                sun.localTime = msg.LocalTime;
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
            _sendTimer = 0f;
        }
    }
}
