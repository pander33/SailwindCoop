using System;
using System.Globalization;
using System.IO;
using System.Text;
using Crest;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Пишет снимок состояния воды в файл — по кнопке из меню F8, на каждой машине свой.
    ///
    /// Высоты воды берутся из покадрового замера в <see cref="Sync.CrestWaterSync"/>, а не
    /// запрашиваются прямо здесь: <c>SampleHeightHelper.Sample()</c> отдаёт результат лишь на кадр
    /// позже запроса, и разовый опрос из дампа честно печатал нули.
    ///
    /// Почему файл, а не строки в оверлее: сравнивать надо ДЕСЯТКИ величин у двух машин
    /// одновременно, и половина из них — массивы (<c>_phases</c>, длины волн, углы). В оверлее это
    /// не помещается, читается по одной строке и приводит к тому, что сравниваются числа, снятые в
    /// разные моменты. Файл снимается за один кадр, целиком, и его можно просто продиффить.
    ///
    /// Формат нарочно плоский <c>ключ = значение</c> с инвариантной точкой, чтобы `diff` двух
    /// дампов сразу показывал расходящиеся строки. Числа не округляются сверх нужного: разница в
    /// третьем знаке — это уже сантиметры волны.
    /// </summary>
    public static class WaterDump
    {
        /// <summary>Куда лёг последний дамп — показывается в меню.</summary>
        public static string LastPath = "";

        /// <summary>
        /// Снять дамп. Возвращает короткое сообщение для меню (путь или причина отказа).
        /// Не бросает: это диагностика, и падать она права не имеет.
        /// </summary>
        public static string Write(CoopBehaviour coop)
        {
            try
            {
                var sb = new StringBuilder(8192);
                var ci = CultureInfo.InvariantCulture;

                var net = coop != null ? coop.Net : null;
                string role = net != null ? net.Role.ToString() : "none";

                sb.Append("# SailwindCoop water dump\n");
                Kv(sb, "taken.utc", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", ci));
                Kv(sb, "mod.version", Plugin.Version);
                Kv(sb, "protocol.version", Protocol.Version.ToString(ci));
                Kv(sb, "role", role);
                if (net != null)
                {
                    Kv(sb, "net.state", net.State.ToString());
                    Kv(sb, "net.rtt.ms", F(net.Clock.RttMs));
                    Kv(sb, "net.clockOffset.ms", F(net.Clock.OffsetMs));
                    Kv(sb, "net.serverTick.ms", net.Clock.ServerTick.ToString(ci));
                }

                // --- время -------------------------------------------------------------
                sb.Append('\n');
                Kv(sb, "unity.Time.time", F(Time.time));
                Kv(sb, "unity.Time.unscaledTime", F(Time.unscaledTime));
                Kv(sb, "unity.Time.timeScale", F(Time.timeScale));
                if (coop != null && coop.Env != null)
                {
                    Kv(sb, "env.hasHostState", coop.Env.HasHostState ? "1" : "0");
                    Kv(sb, "env.host.timeScale", F(coop.Env.HostTimeScale));
                }
                if (coop != null && coop.CrestWater != null)
                {
                    Kv(sb, "water.timeProvider.installed", coop.CrestWater.Installed ? "1" : "0");
                    Kv(sb, "water.clock.slewError", F(coop.CrestWater.SlewError));
                    Kv(sb, "water.phases.adopted", coop.CrestWater.PhasesApplied ? "1" : "0");
                }

                // --- начало координат --------------------------------------------------
                sb.Append('\n');
                var fo = FloatingOriginManager.instance;
                if (fo != null)
                {
                    Kv(sb, "origin.offset.x", F(fo.outCurrentOffset.x));
                    Kv(sb, "origin.offset.z", F(fo.outCurrentOffset.z));
                }
                else Kv(sb, "origin", "null");

                // --- океан -------------------------------------------------------------
                sb.Append('\n');
                var ocean = OceanRenderer.Instance;
                if (ocean == null)
                {
                    Kv(sb, "crest", "no OceanRenderer");
                }
                else
                {
                    Kv(sb, "crest.currentTime", F(ocean.CurrentTime));
                    Kv(sb, "crest.deltaTime", F(ocean.DeltaTime));
                    Kv(sb, "crest.root.y", F(ocean.transform.position.y));
                }

                // --- ветер / волновая инерция -----------------------------------------
                sb.Append('\n');
                Kv(sb, "wind.current", V(global::Wind.currentWind));
                Kv(sb, "wind.base", V(global::Wind.currentBaseWind));
                Kv(sb, "wind.magnitude", F(global::Wind.currentWind.magnitude));
                var inertia = UnityEngine.Object.FindObjectOfType<WavesInertia>();
                if (inertia != null)
                {
                    Kv(sb, "wavesInertia.currentInertia", F(inertia.currentInertia));
                    Kv(sb, "wavesInertia.currentMagnitude", F(inertia.currentMagnitude));
                    Kv(sb, "wavesInertia.eulerY", F(inertia.transform.eulerAngles.y));
                    Kv(sb, "wavesInertia.enabled", inertia.enabled ? "1" : "0");
                }
                Kv(sb, "gameState.distanceToLand", F(GameState.distanceToLand));

                // --- лодка и вода под ней ---------------------------------------------
                // Плавающее начало координат у хоста и клиента совпадает (проверено замером), но
                // печатаем и локальную, и реальную позицию: если разойдётся origin, это сразу видно,
                // и сравнивать надо будет реальные координаты.
                sb.Append('\n');
                Transform boat = coop != null && coop.Players != null ? coop.Players.LocalBoat : null;
                Transform probe = boat;
                if (probe == null && coop != null && coop.Players != null)
                    probe = coop.Players.ResolveLocalPlayerBodyNow();
                Kv(sb, "probe.kind", boat != null ? "boat" : (probe != null ? "player" : "none"));
                if (probe != null)
                {
                    Vector3 p = probe.position;
                    Kv(sb, "probe.local", V(p));
                    if (Sync.CoordSpace.Ready) Kv(sb, "probe.real", V(Sync.CoordSpace.LocalToReal(p)));
                }

                // Высоты берём из покадрового замера CrestWaterSync, а НЕ спрашиваем здесь:
                // SampleHeightHelper.Sample() отдаёт результат лишь на кадр позже запроса, поэтому
                // разовый опрос из дампа возвращал false и печатал нули (так и вышло в первых дампах).
                var cw = coop != null ? coop.CrestWater : null;
                if (cw != null)
                {
                    Kv(sb, "water.probeCenter", V(cw.ProbeCenter));
                    for (int ix = -1; ix <= 1; ix++)
                        for (int iz = -1; iz <= 1; iz++)
                        {
                            int i = (ix + 1) * 3 + (iz + 1);
                            string key = "water.grid[" + ix + "," + iz + "]";
                            Kv(sb, key, cw.ProbeValid(i) ? F(cw.ProbeHeight(i)) : "no sample");
                        }
                    if (probe != null && cw.ProbeValid(4))
                        Kv(sb, "freeboard", F(probe.position.y - cw.ProbeHeight(4)));
                }

                // --- наборы волн Гершнера ---------------------------------------------
                sb.Append('\n');
                var updater = UnityEngine.Object.FindObjectOfType<OceanUpdaterCrest>();
                Kv(sb, "oceanUpdaterCrest.found", updater != null ? "1" : "0");
                if (updater != null) Kv(sb, "oceanUpdaterCrest.enabled", updater.enabled ? "1" : "0");

                var sets = UnityEngine.Object.FindObjectsOfType<ShapeGerstnerBatched>();
                Kv(sb, "gerstner.count", sets != null ? sets.Length.ToString(ci) : "0");
                if (sets != null)
                {
                    // Порядок FindObjectsOfType не гарантирован — сортируем по имени, иначе два
                    // дампа окажутся несравнимыми по чисто случайной причине.
                    Array.Sort(sets, (a, b) => string.CompareOrdinal(Name(a), Name(b)));
                    for (int i = 0; i < sets.Length; i++) DumpGerstner(sb, "gerstner[" + i + "]", sets[i]);
                }

                string dir = Path.Combine(BepInEx.Paths.GameRootPath, "debug");
                Directory.CreateDirectory(dir);
                string file = "water-" + role.ToLowerInvariant() + "-" +
                              DateTime.Now.ToString("HHmmss", ci) + ".txt";
                string path = Path.Combine(dir, file);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                LastPath = path;
                Plugin.Logger.LogInfo("[WaterDump] " + path);
                return "Water dump: debug/" + file;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[WaterDump] failed: " + e.Message);
                return "Water dump failed: " + e.Message;
            }
        }

        private static void DumpGerstner(StringBuilder sb, string k, ShapeGerstnerBatched g)
        {
            var ci = CultureInfo.InvariantCulture;
            if (g == null) { Kv(sb, k, "null"); return; }
            sb.Append('\n');
            Kv(sb, k + ".name", Name(g));
            Kv(sb, k + ".enabled", g.enabled ? "1" : "0");
            Kv(sb, k + ".weight", F(g._weight));
            Kv(sb, k + ".windDirectionAngle", F(g._windDirectionAngle));
            Kv(sb, k + ".randomSeed", g._randomSeed.ToString(ci));
            Kv(sb, k + ".componentsPerOctave", g._componentsPerOctave.ToString(ci));
            Kv(sb, k + ".spectrum", g._spectrum != null ? g._spectrum.name : "null");
            // Полные массивы, а не контрольная сумма: сумма говорит только «не совпало», а массивы
            // показывают, ЧЕМ именно — сдвигом фаз (компенсация начала координат), другими длинами
            // волн (спектр) или другими углами.
            Arr(sb, k + ".phases", g._phases);
            Arr(sb, k + ".wavelengths", g._wavelengths);
            Arr(sb, k + ".amplitudes", g._amplitudes);
            Arr(sb, k + ".angleDegs", g._angleDegs);
        }

        private static string Name(ShapeGerstnerBatched g)
        {
            try { return g != null && g.gameObject != null ? g.gameObject.name : "?"; }
            catch { return "?"; }
        }

        private static void Arr(StringBuilder sb, string key, float[] a)
        {
            if (a == null) { Kv(sb, key, "null"); return; }
            Kv(sb, key + ".length", a.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < a.Length; i++)
                Kv(sb, key + "[" + i + "]", F(a[i]));
        }

        private static void Kv(StringBuilder sb, string k, string v)
        {
            sb.Append(k).Append(" = ").Append(v).Append('\n');
        }

        private static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
        private static string F(float v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => F(v.x) + " " + F(v.y) + " " + F(v.z);
    }
}
