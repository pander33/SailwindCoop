using System;
using BepInEx.Logging;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Master gate for everything this mod writes to <c>BepInEx/LogOutput.log</c>.
    /// <b>Off by default</b> — a normal session is silent; logging is turned on from the co-op menu
    /// (F8 → Logging) when something needs diagnosing, and the choice persists in the config file.
    ///
    /// <para>Why a gate at all: several log sites sit on the per-packet path. A handler that throws on
    /// every inbound snapshot would otherwise dump a full stack trace ~100 times a second with four
    /// guests connected, and the resulting disk I/O is itself a frame hitch — the very thing the
    /// exception containment around it exists to prevent. Sites that can repeat per packet must ALSO
    /// throttle themselves (<see cref="ReportError"/> / <see cref="ShouldReport"/>); the gate alone is
    /// not enough once a user has switched logging on.</para>
    ///
    /// <para><b>Errors are the one exception to the gate.</b> This mod wraps every per-frame step, every
    /// Harmony patch and the whole receive path in try/catch. Before, an escaping exception at least
    /// reached <c>LogOutput.log</c> through BepInEx's own Unity log listener; now it is caught and handed
    /// to this class, so gating errors too would make a hard failure — Harmony patches not applying, a
    /// subsystem throwing every frame — produce literally no trace anywhere. The mod would load, connect,
    /// silently ignore every interaction, and the log would look identical to a healthy session. So
    /// <see cref="LogError"/>/<see cref="LogFatal"/> always reach the sink, under a small global budget
    /// (<see cref="SilentErrorBurst"/> lines, then one per <see cref="SilentErrorIntervalSec"/>) that keeps
    /// the "silent session" promise honest: a broken build writes a handful of lines, not a log file.
    /// Everything else — info, messages, warnings — stays fully gated.</para>
    ///
    /// <para>Diagnostics a REMOTE peer can trigger (malformed payloads, unknown message types) must stay
    /// on the gated path via <see cref="ShouldReport"/>: a spoofed UDP stream on the port must never be
    /// able to make a user who asked for silence write to disk.</para>
    ///
    /// <para>This deliberately mirrors <see cref="ManualLogSource"/>'s method names so the ~250
    /// existing <c>Plugin.Logger.LogX(...)</c> call sites keep compiling untouched. Note that the
    /// argument (usually a concatenated string) is still built by the caller even when logging is off —
    /// acceptable because the sites that repeat are throttled before they build anything.</para>
    /// </summary>
    public sealed class CoopLog
    {
        /// <summary>Errors emitted verbatim while logging is off, before the interval throttle kicks in.</summary>
        private const int SilentErrorBurst = 10;

        /// <summary>Minimum gap between errors once the burst above is used up (logging off only).</summary>
        private const double SilentErrorIntervalSec = 30.0;

        private readonly ManualLogSource _sink;
        private bool _enabled;

        /// <summary>Bumped every time logging is switched ON; resets every <see cref="Repeat"/> counter
        /// so the first occurrences after switching on are always reported. See <see cref="Repeat"/>.</summary>
        private int _generation;

        private int _silentErrors;
        private DateTime _nextSilentErrorAt = DateTime.MinValue;

        public CoopLog(ManualLogSource sink, bool enabled)
        {
            _sink = sink;
            _enabled = enabled;
        }

        /// <summary>Master switch. False = this mod writes nothing except bounded error reports.</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (value && !_enabled) _generation++;
                _enabled = value;
            }
        }

        public void LogInfo(object data) { if (_enabled && _sink != null) _sink.LogInfo(data); }
        public void LogMessage(object data) { if (_enabled && _sink != null) _sink.LogMessage(data); }
        public void LogWarning(object data) { if (_enabled && _sink != null) _sink.LogWarning(data); }
        public void LogDebug(object data) { if (_enabled && _sink != null) _sink.LogDebug(data); }

        /// <summary>Ungated (see the class remarks). Bounded by the silent-mode budget.</summary>
        public void LogError(object data) { Emit(LogLevel.Error, data); }

        /// <summary>Ungated (see the class remarks). Bounded by the silent-mode budget.</summary>
        public void LogFatal(object data) { Emit(LogLevel.Fatal, data); }

        /// <summary>
        /// Per-call-site occurrence counter for <see cref="ReportError"/>. A struct rather than a plain
        /// <c>int</c> so it can carry the generation stamp: a site failing since session start would
        /// otherwise run its counter into the thousands while logging was off, and the moment the user
        /// switched logging on "count &lt;= 3" would already be false — minutes of apparent silence right
        /// after being told to turn logging on and reproduce. The stamp resets the count on that switch.
        /// </summary>
        public struct Repeat
        {
            internal int Count;
            internal int Gen;

            /// <summary>How many times this site has fired since logging was last switched on.</summary>
            public int Occurrences { get { return Count; } }
        }

        /// <summary>
        /// Report a repeating internal failure. Builds the message only when it will actually be written,
        /// so a subsystem throwing every frame costs nothing while suppressed. Reaches the log even with
        /// logging off (bounded) — a silent hard failure is indistinguishable from "the mod isn't there".
        /// </summary>
        public void ReportError(string what, object detail, ref Repeat r)
        {
            if (r.Gen != _generation) { r.Gen = _generation; r.Count = 0; }
            r.Count++;

            if (_enabled)
            {
                if (r.Count > 3 && r.Count % 300 != 0) return;
                if (_sink == null) return;
                _sink.Log(LogLevel.Error, what + " (occurrence #" + r.Count + "): " + detail);
                return;
            }

            if (_sink == null || !AllowWhileOff()) return;
            _sink.Log(LogLevel.Error, what + " (occurrence #" + r.Count + "): " + detail);
            NoteBudgetExhausted();
        }

        /// <summary>
        /// Rate limiter for a GATED site that can fire every frame or every packet — in practice the
        /// diagnostics a remote peer can provoke, which must never write to disk while the user has
        /// logging off. Increments <paramref name="counter"/> and returns true only for the first few
        /// occurrences and then rarely. Call this BEFORE building the message string.
        ///
        /// Suppressed occurrences are deliberately not counted: counting them meant that after the user
        /// finally switched logging on, the first line appeared only at the next multiple of 300.
        /// For internal failures prefer <see cref="ReportError"/>, which keeps that property AND still
        /// reports while logging is off.
        /// </summary>
        public bool ShouldReport(ref int counter)
        {
            if (!_enabled) return false;
            counter++;
            return counter <= 3 || counter % 300 == 0;
        }

        private void Emit(LogLevel level, object data)
        {
            if (_sink == null) return;
            if (_enabled) { _sink.Log(level, data); return; }
            if (!AllowWhileOff()) return;
            _sink.Log(level, data);
            NoteBudgetExhausted();
        }

        /// <summary>
        /// Budget for the logging-off path. Uses <see cref="DateTime"/> rather than <c>Time.realtimeSinceStartup</c>
        /// on purpose: a logger that throws because it was touched off the main thread would defeat every
        /// containment block that calls it.
        /// </summary>
        private bool AllowWhileOff()
        {
            _silentErrors++;
            if (_silentErrors <= SilentErrorBurst) return true;

            DateTime now = DateTime.UtcNow;
            if (now < _nextSilentErrorAt) return false;
            _nextSilentErrorAt = now.AddSeconds(SilentErrorIntervalSec);
            return true;
        }

        /// <summary>Tell the reader once why the errors below this line are sparse.</summary>
        private void NoteBudgetExhausted()
        {
            if (_silentErrors != SilentErrorBurst || _sink == null) return;
            _sink.Log(LogLevel.Warning,
                      "[Coop] Further errors are rate-limited because logging is off. Turn it on in the " +
                      "co-op menu (F8 -> Logging) and reproduce the problem for the full picture.");
        }
    }
}
