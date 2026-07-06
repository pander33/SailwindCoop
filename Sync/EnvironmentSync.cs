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
        private float _sendTimer;
        private bool _windDisabled;     // client: did we turn off the local Wind sim?
        private bool _wavesDisabled;    // client: did we turn off the local WavesInertia sim?
        private WavesInertia _waves;    // cached; no singleton on the engine type

        // --- волновые часы (клиент) -----------------------------------------------------
        // Фаза FFT-океана — чистая функция Time.time (Ocean.calcComplex: sqrt(g·k)·t·speed),
        // а Time.time у хоста и клиента не связаны — гребни никогда не совпадают, и
        // хост-авторитетная лодка «ныряет» под клиентскую воду. Клиент ведёт оценку
        // ХОСТОВОГО Time.time (база из EnvState + экстраполяция с хостовым timeScale) и
        // подменяет ею аргумент времени в Ocean.calcComplex (см. OceanPatches). Пока хост
        // на JoinPause (timeScale=0), часы стоят — вода клиента заморожена в фазе хоста
        // и размораживается вместе с ним.
        private float _waveTimeBase;     // WaveTime из последнего снапшота
        private float _waveBaseLocal;    // Time.unscaledTime в момент приёма снапшота
        private float _hostTimeScale;    // Time.timeScale хоста из снапшота
        private bool _waveBaseValid;
        private float _waveClock;        // сглаженные часы, которые читает префикс

        /// <summary>Волновые часы для Ocean.calcComplex (читается Harmony-префиксом; писать только с главного потока).</summary>
        public static float WaveClock;
        /// <summary>Пока true — префикс подменяет время волн на <see cref="WaveClock"/>.</summary>
        public static volatile bool WaveClockActive;
        /// <summary>Диагностика для оверлея: рассинхрон часов с целью (сек) и хостовый timeScale.</summary>
        public float WaveClockError { get; private set; }
        public float HostTimeScale => _hostTimeScale;
        public bool WaveClockValid => _waveBaseValid;

        /// <summary>Environment snapshot rate (Hz). Slow — env changes gradually.</summary>
        public float EnvHz = 4f;

        public EnvironmentSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: capture and broadcast
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role == Role.Client) { TickWaveClock(); return; }
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

            // Волновые часы: хост остаётся на ванильном Time.time, клиент повторяет его.
            msg.WaveTime = Time.time;
            msg.HostTimeScale = Time.timeScale;

            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.Unreliable);
        }

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

            // Волновые часы: ре-базируем оценку хостового Time.time. Экстраполяция между
            // снапшотами идёт по НЕмасштабируемому локальному времени × хостовый timeScale,
            // поэтому пауза самого клиента (его меню) волну общего мира не останавливает.
            _waveTimeBase = msg.WaveTime;
            _waveBaseLocal = Time.unscaledTime;
            _hostTimeScale = msg.HostTimeScale;
            _waveBaseValid = true;
        }

        /// <summary>
        /// Клиент, раз в кадр (главный поток): ведём сглаженные волновые часы к цели
        /// «база + прошло_локально × хостовый timeScale». Снапшоты приходят 4 Гц по UDP —
        /// без сглаживания каждое ре-базирование дёргало бы поверхность.
        /// </summary>
        private void TickWaveClock()
        {
            if (!_waveBaseValid || _net.State != LinkState.Connected)
            {
                WaveClockActive = false;
                WaveClockError = 0f;
                return;
            }

            float target = _waveTimeBase + (Time.unscaledTime - _waveBaseLocal) * _hostTimeScale;
            float err = target - _waveClock;
            if (Mathf.Abs(err) > 1f)
                _waveClock = target;                                        // первый захват / лаг-спайк
            else
                _waveClock += Time.unscaledDeltaTime * _hostTimeScale + err * 0.1f; // плавная догонка

            WaveClockError = err;
            WaveClock = _waveClock;
            WaveClockActive = true;
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

            // Волновые часы отпускаем — океан возвращается к локальному Time.time
            // (одноразовый скачок фазы при отключении допустим).
            _waveBaseValid = false;
            WaveClockActive = false;
            WaveClockError = 0f;
        }
    }

    /// <summary>
    /// Harmony-патчи океана (см. волновые часы в <see cref="EnvironmentSync"/>):
    /// 1) <c>Ocean.calcComplex(float time, …)</c> — у подключённого клиента подменяем время волн
    ///    хостовыми часами: фаза (и буфер высот, который читает и меш, и Buoyancy) совпадает с хостом.
    /// 2) <c>Ocean.InitWaveGenerator</c> — спектр h02 ваниль заполняет гауссовым шумом от
    ///    UnityEngine.Random: у каждого запуска СВОЯ поверхность даже при одинаковом времени.
    ///    Подкладываем детерминированную таблицу (фиксированный сид) и включаем ванильный режим
    ///    useMyRandom — у хоста и клиента одинаковая форма волн (мод стоит у обоих по требованию
    ///    протокола; для одиночки это лишь фиксированный, визуально неотличимый шумовой паттерн).
    /// </summary>
    public static class OceanPatches
    {
        private const int GaussSeed = 727272; // одинаков у всех сборок мода

        public static void Apply(Harmony harmony)
        {
            bool phase = TryPatch(harmony, "calcComplex", nameof(PreCalcComplex));
            bool table = TryPatch(harmony, "InitWaveGenerator", nameof(PreInitWaveGenerator));
            Plugin.Logger.LogInfo("[OceanPatches] Ocean patches: phase=" + phase + " table=" + table);
        }

        private static bool TryPatch(Harmony harmony, string method, string prefixName)
        {
            try
            {
                var mi = typeof(Ocean).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) return false;
                var prefix = new HarmonyMethod(typeof(OceanPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[OceanPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        // Только чтение статики — никаких Unity API (на случай вызова не с главного потока).
        private static void PreCalcComplex(ref float time)
        {
            try
            {
                if (EnvironmentSync.WaveClockActive)
                    time = EnvironmentSync.WaveClock;
            }
            catch { }
        }

        // Детерминированный Бокс–Мюллер вместо UnityEngine.Random — h02 одинаков у всех экземпляров.
        private static void PreInitWaveGenerator(Ocean __instance, bool skip, ref bool useMyRandom)
        {
            try
            {
                if (skip) return; // спектр не перегенерируется — таблица не нужна
                int n = __instance.width * __instance.height;
                if (n <= 0) return;
                if (__instance.gaussRandom1 == null || __instance.gaussRandom1.Length != n)
                    __instance.gaussRandom1 = new float[n];
                if (__instance.gaussRandom2 == null || __instance.gaussRandom2.Length != n)
                    __instance.gaussRandom2 = new float[n];
                var rng = new System.Random(GaussSeed);
                for (int i = 0; i < n; i++)
                {
                    __instance.gaussRandom1[i] = Gaussian(rng);
                    __instance.gaussRandom2[i] = Gaussian(rng);
                }
                useMyRandom = true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[OceanPatches] PreInitWaveGenerator: " + e.Message); }
        }

        private static float Gaussian(System.Random rng)
        {
            // Та же формула, что в ванильном Ocean.GaussianRnd, но от детерминированного ГПСЧ.
            double u = rng.NextDouble();
            double v = rng.NextDouble();
            if (u < 0.01) u = 0.01;
            return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
        }
    }
}
