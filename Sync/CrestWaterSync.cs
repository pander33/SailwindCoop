using System;
using System.Reflection;
using Crest;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Приводит воду клиента к воде хоста.
    ///
    /// Факт, стоивший нескольких неверных заходов: **воду в Sailwind рисует Crest, а не класс
    /// <c>Ocean</c> из Assembly-CSharp**. `Ocean` в сборке есть, но в сцене не инстанцируется —
    /// `Ocean.Singleton` в игре равен null, поэтому прежние патчи `OceanPatches` (фаза, спектр,
    /// амплитуда) не выполнялись ни разу и удалены. Плавучесть и поверхность идут через
    /// <c>OceanHeight.GetHeight</c> -> <c>SampleHeightHelper</c> -> Crest.
    ///
    /// Расходятся ровно две вещи, и обе чинятся здесь.
    ///
    /// **1. Время.** Высота у Гершнера — <c>sin(k·x + _phases[i] + w_i·CurrentTime)</c>, а
    /// <c>OceanRenderer.CurrentTime</c> по умолчанию просто <c>Time.time</c>, у хоста и клиента ничем
    /// не связанный. Crest предусмотрел этот случай: поле <c>OceanRenderer._timeProvider</c> с
    /// подсказкой «can be used to ... provide server time». Кладём туда <c>TimeProviderCustom</c> —
    /// **штатный компонент самого Crest**, не свой.
    ///
    /// Часы клиента при этом **интегрируются локально**, а не приравниваются присланному значению:
    ///
    ///     step = Time.deltaTime * hostTimeScale
    ///     slew = clamp(err, -0.1*step, +0.1*step)
    ///     _time += step + slew;  err -= slew
    ///
    /// Это важно по двум причинам. Во-первых, <c>step + slew ≥ 0.9*step</c>, то есть время
    /// **монотонно по построению** — а <c>ShapeGerstnerBatched.UpdateData</c> начинается с
    /// <c>if (_lastUpdateTime >= CurrentTime) return;</c>, и шаг назад заморозил бы пересчёт формы
    /// волны, оставив фазу ехать. Во-вторых, ошибка гасится не быстрее 10 % за кадр, поэтому джиттер
    /// канала и подстройка сетевых часов не попадают в поверхность. Прыжок разрешён только при
    /// расхождении больше <see cref="SnapSec"/> — это уже не джиттер, а другой мир.
    ///
    /// **2. Форма волны.** Веса и направление задаёт <c>OceanUpdaterCrest</c>, и это свободно бегущий
    /// автомат: <c>currentMult += lerpRateInertial * Time.deltaTime</c>, на переполнении меняются
    /// местами <c>wavesUp</c>/<c>wavesDown</c> и переписывается <c>_windDirectionAngle</c>. Цикл
    /// стартует с загрузки сцены, клиент грузится позже — у одного набор волн растёт, у другого тот же
    /// набор затухает: волны идут с разных сторон и разной высоты.
    ///
    /// Синхронизируем **входы цикла** (<c>currentMult</c>, <c>wavesUp</c>, <c>targetInertiaAngle</c>),
    /// а апдейтер клиента **оставляем работать** — он пересчитает веса сам. Так спектр продолжает
    /// отвечать на погоду, и поверхность не дёргается ступеньками, как при записи готовых весов
    /// 4 раза в секунду. (Предыдущая здешняя попытка глушила апдейтер и писала выходы — она давала
    /// пульсацию и в игре была хуже.)
    /// </summary>
    public sealed class CrestWaterSync
    {
        /// <summary>Расхождение, после которого часы не подтягиваются, а прыгают (с).</summary>
        private const float SnapSec = 3f;

        /// <summary>Доля шага, которую разрешено отдать под догонку за кадр.</summary>
        private const float SlewFraction = 0.1f;

        private static readonly FieldInfo TimeProviderField =
            typeof(OceanRenderer).GetField("_timeProvider", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo CurrentMultField =
            typeof(OceanUpdaterCrest).GetField("currentMult", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo WavesUpField =
            typeof(OceanUpdaterCrest).GetField("wavesUp", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo WavesDownField =
            typeof(OceanUpdaterCrest).GetField("wavesDown", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo TargetInertiaAngleField =
            typeof(OceanUpdaterCrest).GetField("targetInertiaAngle", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo WindWavesField =
            typeof(OceanUpdaterCrest).GetField("windWaves", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo InertiaWavesField =
            typeof(OceanUpdaterCrest).GetField("inertiaWaves", BindingFlags.NonPublic | BindingFlags.Instance);

        // --- часы ---------------------------------------------------------------------
        private TimeProviderCustom _provider;
        private OceanRenderer _patched;       // куда поставили провайдер (снимать надо там же)
        private TimeProviderBase _previous;   // что там было до нас
        private bool _installed;
        private bool _failed;
        private float _slewError;
        private float _hostTimeScale = 1f;
        private bool _haveHostTime;

        // --- форма волны --------------------------------------------------------------
        private OceanUpdaterCrest _updater;
        private bool _wavesFailed;
        private bool _directionSeeded;

        /// <summary>Нужен только ради <c>Clock.ServerTick</c> — см. поправку на возраст снапшота.</summary>
        private Net.CoopNet _net;

        private Runtime.CoopLog.Repeat _installReport;
        private Runtime.CoopLog.Repeat _removeReport;
        private Runtime.CoopLog.Repeat _wavesReport;

        /// <summary>Диагностика: ведёт ли Crest время хоста прямо сейчас.</summary>
        public bool Installed => _installed;

        /// <summary>Диагностика: остаток расхождения часов с хостом (с).</summary>
        public float SlewError => _slewError;

        // =================================================================================
        // Часы
        // =================================================================================

        /// <summary>
        /// Клиент, раз в кадр. Провайдер живёт, только пока есть чему следовать: вне сессии Crest
        /// должен вернуться к собственному <c>Time.time</c>, иначе вода замрёт в одиночной игре.
        /// </summary>
        public void Tick()
        {
            if (_failed) return;
            try
            {
                if (!_haveHostTime) { Remove(); return; }

                var ocean = OceanRenderer.Instance;
                if (ocean == null) { Remove(); return; }

                // Сцена могла перезагрузиться — тогда это ДРУГОЙ OceanRenderer, и снимать провайдер
                // надо со старого (он уже мёртв), а ставить на новый.
                if (_installed && _patched != ocean) Remove();
                if (!_installed) Install(ocean);
                if (_installed) AdvanceClock();
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.ReportError("[CrestWaterSync] Could not drive Crest time",
                                          e.Message, ref _installReport);
            }
        }

        private void Install(OceanRenderer ocean)
        {
            if (TimeProviderField == null)
            {
                _failed = true;
                Plugin.Logger.LogWarning("[CrestWaterSync] OceanRenderer._timeProvider not found - " +
                                         "client water keeps its own time and the boat will ride the " +
                                         "wrong wave");
                Runtime.CoopBehaviour.Notice("This build of the game hides Crest's time provider - the " +
                                             "client's waves cannot be matched to the host's.");
                return;
            }

            // Компонент вешаем на сам объект океана: он живёт и умирает вместе со сценой, отдельный
            // DontDestroyOnLoad-объект пришлось бы вычищать руками при каждой перезагрузке мира.
            if (_provider == null || _provider.gameObject != ocean.gameObject)
                _provider = ocean.gameObject.AddComponent<TimeProviderCustom>();

            // Стартуем с текущего времени океана, а не с нуля: иначе первый же кадр даст скачок фазы
            // на десятки секунд.
            _provider._time = ocean.CurrentTime;
            _provider._deltaTime = Time.deltaTime;
            _slewError = 0f;

            _previous = (TimeProviderBase)TimeProviderField.GetValue(ocean);
            TimeProviderField.SetValue(ocean, _provider);
            _patched = ocean;
            _installed = true;
            Plugin.Logger.LogInfo("[CrestWaterSync] Crest is now on host time" +
                                  (_previous != null ? " (replaced " + _previous.GetType().Name + ")" : ""));
        }

        /// <summary>
        /// Один шаг часов воды. Монотонность — не украшение: см. <c>UpdateData</c> в комментарии
        /// класса. Шаг масштабируется хостовым <c>timeScale</c>, поэтому пауза хоста (JoinPause или
        /// его меню) останавливает воду клиента, а пауза самого клиента — нет.
        /// </summary>
        private void AdvanceClock()
        {
            float step = Time.deltaTime * _hostTimeScale;
            if (step < 0f) step = 0f;
            float slew = Mathf.Clamp(_slewError, -SlewFraction * step, SlewFraction * step);
            _provider._time += step + slew;
            _slewError -= slew;
            _provider._deltaTime = step;
        }

        /// <summary>Вернуть Crest его собственное время. Безопасно вызывать повторно.</summary>
        public void Remove()
        {
            if (!_installed) return;
            _installed = false;
            try
            {
                // _patched мог быть уничтожен вместе со сценой — тогда восстанавливать нечего.
                if (_patched != null && TimeProviderField != null)
                    TimeProviderField.SetValue(_patched, _previous);
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[CrestWaterSync] Could not restore Crest's own time provider",
                                          e.Message, ref _removeReport);
            }
            _patched = null;
            _previous = null;
            _slewError = 0f;
        }

        // =================================================================================
        // Форма волны
        // =================================================================================

        private OceanUpdaterCrest Updater()
        {
            if (_wavesFailed) return null;
            if (CurrentMultField == null || WavesUpField == null || WavesDownField == null ||
                TargetInertiaAngleField == null || WindWavesField == null || InertiaWavesField == null)
            {
                _wavesFailed = true;
                Plugin.Logger.LogWarning("[CrestWaterSync] OceanUpdaterCrest fields not found - " +
                                         "wave size and direction stay per-machine");
                return null;
            }
            if (_updater == null)
            {
                _updater = UnityEngine.Object.FindObjectOfType<OceanUpdaterCrest>();
                _directionSeeded = false;   // новый апдейтер — направление ещё не задавали
            }
            return _updater;
        }

        /// <summary>Хост: снять время океана и состояние волнового цикла в снапшот.</summary>
        public void Capture(Net.EnvStateMsg msg)
        {
            try
            {
                var ocean = OceanRenderer.Instance;
                var updater = Updater();
                if (ocean == null || updater == null) return;

                msg.HasCrest = true;
                msg.CrestOceanTime = ocean.CurrentTime;
                msg.CrestCurrentMult = (float)CurrentMultField.GetValue(updater);
                msg.CrestWavesUp = (byte)Mathf.Clamp((int)WavesUpField.GetValue(updater), 0, 1);
                msg.CrestTargetInertiaAngle = (float)TargetInertiaAngleField.GetValue(updater);
                var windWaves = (ShapeGerstnerBatched)WindWavesField.GetValue(updater);
                msg.CrestWindWavesWeight = windWaves != null ? windWaves._weight : 0f;
            }
            catch (Exception e)
            {
                msg.HasCrest = false;
                Plugin.Logger.ReportError("[CrestWaterSync] Could not capture Crest state",
                                          e.Message, ref _wavesReport);
            }
        }

        /// <summary>Клиент: принять снапшот.</summary>
        public void ApplyRemote(Net.CoopNet net, Net.EnvStateMsg msg)
        {
            _net = net;
            _hostTimeScale = msg.HostTimeScale;
            if (!msg.HasCrest) return;
            _haveHostTime = true;

            // Часы. CrestOceanTime снят хостом в момент ОТПРАВКИ, а доехал он за полпути RTT и
            // пролежит до следующего снапшота (4 Гц). Догонять голое значение — значит сойтись к
            // «времени хоста в прошлом» и застыть там: замер показал устойчивое отставание 0,71 с
            // при slewError = 0, то есть часы честно сошлись к устаревшей цели. Добавляем возраст
            // снапшота по сетевым часам (msg.Tick снят хостом в том же кадре), и цель становится
            // «время хоста СЕЙЧАС». Это поправка только к цели — ни на монотонность, ни на скорость
            // подтягивания она не влияет.
            if (_installed && _provider != null)
            {
                float age = (_net != null ? (_net.Clock.ServerTick - msg.Tick) : 0L) * 0.001f;
                if (age < 0f) age = 0f;
                if (age > 1f) age = 1f;   // разрыв связи не должен разгонять воду
                float err = (msg.CrestOceanTime + age * _hostTimeScale) - _provider._time;
                if (Mathf.Abs(err) > SnapSec)
                {
                    _provider._time = msg.CrestOceanTime;
                    _slewError = 0f;
                }
                else _slewError = err;
            }

            var updater = Updater();
            if (updater == null) return;
            try
            {
                // Апдейтер клиента НЕ глушим: пусть считает веса сам из синхронного состояния цикла.
                int hostUp = msg.CrestWavesUp == 0 ? 0 : 1;
                TargetInertiaAngleField.SetValue(updater, msg.CrestTargetInertiaAngle);

                int localUp = (int)WavesUpField.GetValue(updater);
                bool flipped = localUp != hostUp;
                if (flipped)
                {
                    WavesUpField.SetValue(updater, hostUp);
                    WavesDownField.SetValue(updater, 1 - hostUp);
                }
                // Направление пишем на переключении набора (ваниль делает это в DCTInertiaNewCycle,
                // когда вес набора равен нулю, — там смена не видна) и один раз при первом снапшоте,
                // иначе до первого переключения клиент гнал бы волну в свою сторону.
                if (flipped || !_directionSeeded)
                {
                    var inertiaWaves = (ShapeGerstnerBatched[])InertiaWavesField.GetValue(updater);
                    if (inertiaWaves != null && inertiaWaves.Length > hostUp && inertiaWaves[hostUp] != null)
                        inertiaWaves[hostUp]._windDirectionAngle = -msg.CrestTargetInertiaAngle;
                    _directionSeeded = true;
                }

                CurrentMultField.SetValue(updater, msg.CrestCurrentMult);

                // Вес ветровых волн — не присваиванием: снапшоты идут 4 Гц, а жёсткая запись видна
                // как ступенька на поверхности. MoveTowards ограничивает скорость изменения.
                var windWaves = (ShapeGerstnerBatched)WindWavesField.GetValue(updater);
                if (windWaves != null)
                    windWaves._weight = Mathf.MoveTowards(windWaves._weight, msg.CrestWindWavesWeight, 0.25f);
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[CrestWaterSync] Could not apply Crest state",
                                          e.Message, ref _wavesReport);
            }
        }

        // =================================================================================
        // Положение волны в пространстве (_phases)
        //
        // Измерено дампами с обеих машин 2026-08-13. Два инерционных набора расходились ВО ВСЕХ
        // 112 фазах, до 5,8 рад из 2π, при полностью совпавших _wavelengths и _angleDegs. Третий
        // набор — ветровые волны, у которых _windDirectionAngle всегда 0, — совпал побитово.
        // Этот контраст и есть доказательство: компенсация плавающего начала координат, которую
        // Crest складывает в _phases при каждом сдвиге, зависит от направления волн НА МОМЕНТ
        // сдвига. Хост набирает смещение постепенно за плавание при поворачивающем направлении,
        // клиент проходит то же расстояние одним залпом instantShifting при одном направлении.
        //
        // Ничто производное это не чинит — расхождение в истории, а не в текущих значениях.
        // Клиент перенимает массив хоста целиком. Подробнее: Net.WavePhasesMsg.
        // =================================================================================

        /// <summary>Как часто хост переотправляет фазы. Между отправками локальные сдвиги начала
        /// координат снова слегка разводят массивы, но сдвиг — событие редкое, и направление волн
        /// теперь синхронно, так что расхождение за интервал мало.</summary>
        private const float PhaseResendSec = 5f;

        private float _phaseSendStamp = float.NegativeInfinity;
        private Runtime.CoopLog.Repeat _phaseReport;

        /// <summary>Наборы волн в фиксированном порядке — он же порядок в сообщении.</summary>
        private ShapeGerstnerBatched[] PhaseSets(OceanUpdaterCrest updater)
        {
            var wind = (ShapeGerstnerBatched)WindWavesField.GetValue(updater);
            var inertia = (ShapeGerstnerBatched[])InertiaWavesField.GetValue(updater);
            if (inertia == null || inertia.Length < 2) return null;
            return new[] { wind, inertia[0], inertia[1] };
        }

        /// <summary>Хост, раз в кадр: переотправить фазы, если пора.</summary>
        public void TickHost(Net.CoopNet net)
        {
            if (net == null || net.State != Net.LinkState.Connected) return;
            if (Time.unscaledTime - _phaseSendStamp < PhaseResendSec) return;
            var updater = Updater();
            if (updater == null) return;
            _phaseSendStamp = Time.unscaledTime;
            try
            {
                var sets = PhaseSets(updater);
                if (sets == null) return;
                var msg = new Net.WavePhasesMsg { Sets = new float[sets.Length][] };
                for (int i = 0; i < sets.Length; i++)
                    msg.Sets[i] = sets[i] != null ? sets[i]._phases : null;
                net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[CrestWaterSync] Could not send wave phases",
                                          e.Message, ref _phaseReport);
            }
        }

        /// <summary>Клиент: принять фазы хоста.</summary>
        public void OnWavePhases(Net.WavePhasesMsg msg)
        {
            if (msg == null || msg.Sets == null) return;
            var updater = Updater();
            if (updater == null) return;
            try
            {
                var sets = PhaseSets(updater);
                if (sets == null) return;
                for (int i = 0; i < sets.Length && i < msg.Sets.Length; i++)
                {
                    var set = sets[i];
                    var src = msg.Sets[i];
                    if (set == null || src == null || src.Length == 0) continue;
                    // Длина равна _componentsPerOctave*14 и берётся из сцены, одинаковой у обеих
                    // машин. Не совпала — значит предположение неверно, и писать чужой массив
                    // опаснее, чем оставить свой.
                    if (set._phases == null || set._phases.Length != src.Length) continue;
                    Array.Copy(src, set._phases, src.Length);
                }
                PhasesApplied = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[CrestWaterSync] Could not apply wave phases",
                                          e.Message, ref _phaseReport);
            }
        }

        /// <summary>Диагностика: перенимал ли клиент фазы хоста хоть раз.</summary>
        public bool PhasesApplied { get; private set; }

        // =================================================================================
        // Покадровый замер высоты воды (для дампа)
        //
        // SampleHeightHelper.Sample() ставит запрос в очередь и отдаёт результат лишь на следующем
        // кадре. Дамп, опрашивавший девять свежих точек разом, получал false и печатал нули —
        // первые дампы поэтому были бесполезны по воде. Поэтому опрашиваем те же точки каждый
        // кадр, а дамп печатает последний готовый результат.
        // =================================================================================

        /// <summary>Шаг сетки замера, м.</summary>
        private const float ProbeStep = 20f;

        private readonly SampleHeightHelper[] _probeHelpers = new SampleHeightHelper[9];
        private readonly float[] _probeHeights = new float[9];
        private readonly bool[] _probeValid = new bool[9];
        private Vector3 _probeCenter;

        /// <summary>Высоты воды в сетке 3x3 вокруг последнего центра (индекс = (ix+1)*3 + (iz+1)).</summary>
        public float ProbeHeight(int i) => i >= 0 && i < 9 ? _probeHeights[i] : 0f;
        public bool ProbeValid(int i) => i >= 0 && i < 9 && _probeValid[i];
        public Vector3 ProbeCenter => _probeCenter;

        /// <summary>Раз в кадр на ОБЕИХ ролях: обновить сетку замера вокруг <paramref name="center"/>.</summary>
        public void TickProbe(Vector3 center)
        {
            _probeCenter = center;
            try
            {
                for (int ix = -1; ix <= 1; ix++)
                    for (int iz = -1; iz <= 1; iz++)
                    {
                        int i = (ix + 1) * 3 + (iz + 1);
                        if (_probeHelpers[i] == null) _probeHelpers[i] = new SampleHeightHelper();
                        var at = center + new Vector3(ix * ProbeStep, 0f, iz * ProbeStep);
                        _probeHelpers[i].Init(at, 0.2f);
                        float h;
                        // Первый кадр для новой точки вернёт false — это нормально, значение
                        // появится на следующем; прошлое при этом не затираем.
                        if (_probeHelpers[i].Sample(out h)) { _probeHeights[i] = h; _probeValid[i] = true; }
                    }
            }
            catch { /* диагностика не имеет права ронять кадр */ }
        }

        /// <summary>Полный сброс на отключении: провайдер снят, состояние забыто.</summary>
        public void Clear()
        {
            Remove();
            if (_provider != null)
            {
                UnityEngine.Object.Destroy(_provider);
                _provider = null;
            }
            _updater = null;
            _directionSeeded = false;
            _wavesFailed = false;
            _failed = false;
            _haveHostTime = false;
            _hostTimeScale = 1f;
            _slewError = 0f;
            for (int i = 0; i < 9; i++) { _probeValid[i] = false; _probeHeights[i] = 0f; }
        }
    }
}
