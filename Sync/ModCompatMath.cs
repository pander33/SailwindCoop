using System;
using System.Collections.Generic;
using System.Text;

namespace SailwindCoop.Sync
{
    /// <summary>Unity-free view of one TentandPlank TarpState record (filled by reflection in TentandPlankCompat).</summary>
    internal struct TarpRecordView
    {
        public int Ship;
        public float Wear;
        public float[] Corners;
        public float[] Turns;
    }

    /// <summary>Pure comparisons for TentandPlank tarp snapshots (kept Unity-free for RuntimeSmoke).</summary>
    internal static class TarpSnapshotMath
    {
        public const float CornerTolerance = 0.05f;   // metres
        public const float WearTolerance = 2f;        // health points; wear drifts slightly between machines
        public const float TurnDotMin = 0.999f;       // |q·q'|, so q and -q match

        public static bool FitsPayload(string payload, int maxBytes) =>
            payload == null || Encoding.UTF8.GetByteCount(payload) <= maxBytes;

        public static bool SameRecord(TarpRecordView a, TarpRecordView b)
        {
            if (a.Ship != b.Ship) return false;
            if (Math.Abs(a.Wear - b.Wear) > WearTolerance) return false;
            if (a.Corners == null || b.Corners == null || a.Corners.Length != b.Corners.Length) return false;
            for (int i = 0; i < a.Corners.Length; i++)
                if (!(Math.Abs(a.Corners[i] - b.Corners[i]) <= CornerTolerance)) return false;
            if (a.Turns == null || b.Turns == null || a.Turns.Length != b.Turns.Length || a.Turns.Length % 4 != 0) return false;
            for (int q = 0; q < a.Turns.Length; q += 4)
            {
                float dot = a.Turns[q] * b.Turns[q] + a.Turns[q + 1] * b.Turns[q + 1] +
                            a.Turns[q + 2] * b.Turns[q + 2] + a.Turns[q + 3] * b.Turns[q + 3];
                if (!(Math.Abs(dot) >= TurnDotMin)) return false;
            }
            return true;
        }

        /// <summary>Order-free set equality within tolerance (each record matched once).</summary>
        public static bool SameSet(IList<TarpRecordView> a, IList<TarpRecordView> b)
        {
            int na = a?.Count ?? 0, nb = b?.Count ?? 0;
            if (na != nb) return false;
            if (na == 0) return true;
            var used = new bool[nb];
            for (int i = 0; i < na; i++)
            {
                bool matched = false;
                for (int j = 0; j < nb; j++)
                {
                    if (used[j] || !SameRecord(a[i], b[j])) continue;
                    used[j] = matched = true;
                    break;
                }
                if (!matched) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Host clock for the crew while the host's Photo Mode scrubs its own sun: the last normal sample is
    /// advanced at the last observed rate (hours per scaled second), rolling the day over at midnight.
    /// </summary>
    internal static class PhotoModeClock
    {
        public static void Extrapolate(int day, float global, float local, float rate, float elapsed,
            out int outDay, out float outGlobal, out float outLocal)
        {
            double advance = Math.Max(0.0, rate) * Math.Max(0.0, elapsed);
            if (double.IsNaN(advance) || double.IsInfinity(advance)) advance = 0.0;
            double absolute = day * 24.0 + global + advance;
            outDay = (int)Math.Floor(absolute / 24.0);
            outGlobal = (float)(absolute - outDay * 24.0);
            if (outGlobal >= 24f) { outGlobal = 0f; outDay++; }
            if (outGlobal < 0f) outGlobal = 0f;
            double l = (local + advance) % 24.0;
            if (l < 0.0) l += 24.0;
            outLocal = (float)l;
            if (outLocal >= 24f) outLocal = 0f;
        }

        /// <summary>Rate between two normal samples; false when the sample is unusable (pause, jump, scrub).</summary>
        public static bool SampleRate(float previousGlobal, float global, float scaledSeconds, out float rate)
        {
            rate = 0f;
            if (!(scaledSeconds > 0.05f)) return false;
            float delta = global - previousGlobal;
            if (delta < -12f) delta += 24f;   // midnight wrap
            float r = delta / scaledSeconds;
            if (!(r >= 0f && r < 1f)) return false;
            rate = r;
            return true;
        }
    }
}
