using System;
using System.Collections.Generic;
using SailwindCoop.Sync;

internal static class ModCompatTests
{
    private static TarpRecordView Tarp(int ship, float wear, float x, float[] turn = null) => new TarpRecordView
    {
        Ship = ship,
        Wear = wear,
        Corners = new[] { x, 0f, 0f, x + 2f, 0f, 0f, x + 2f, 0f, 2f },
        Turns = turn ?? new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f },
    };

    internal static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("tarp snapshots match order-free within tolerance and treat q and -q alike", () =>
        {
            var a = new List<TarpRecordView> { Tarp(-1, 10f, 0f), Tarp(40, 20f, 5f) };
            var b = new List<TarpRecordView> { Tarp(40, 21.5f, 5.03f), Tarp(-1, 10f, 0.01f) };
            assert(TarpSnapshotMath.SameSet(a, b), "reordered tarps with float noise did not match");

            var flipped = Tarp(-1, 10f, 0f, new[] { 0f, 0f, 0f, -1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, -1f });
            assert(TarpSnapshotMath.SameRecord(Tarp(-1, 10f, 0f), flipped), "negated quaternion treated as a different turn");

            assert(!TarpSnapshotMath.SameSet(a, new List<TarpRecordView> { Tarp(-1, 10f, 0f) }), "missing tarp matched");
            assert(!TarpSnapshotMath.SameSet(a, new List<TarpRecordView> { Tarp(-1, 10f, 0f), Tarp(50, 20f, 5f) }), "other ship matched");
            assert(!TarpSnapshotMath.SameSet(a, new List<TarpRecordView> { Tarp(-1, 10f, 0f), Tarp(40, 20f, 5.2f) }), "moved corner matched");
            assert(!TarpSnapshotMath.SameSet(a, new List<TarpRecordView> { Tarp(-1, 10f, 0f), Tarp(40, 25f, 5f) }), "repaired tarp matched");
            assert(!TarpSnapshotMath.SameSet(new List<TarpRecordView> { Tarp(-1, 10f, 0f), Tarp(-1, 10f, 0f) },
                new List<TarpRecordView> { Tarp(-1, 10f, 0f), Tarp(40, 20f, 5f) }), "one record matched twice");
            var nan = Tarp(-1, 10f, 0f); nan.Corners[0] = float.NaN;
            assert(!TarpSnapshotMath.SameRecord(nan, nan), "NaN corner matched");
            assert(TarpSnapshotMath.SameSet(new List<TarpRecordView>(), null), "two empty tarp sets differ");
        });

        test("tarp payload cap counts UTF-8 bytes, not characters", () =>
        {
            assert(TarpSnapshotMath.FitsPayload(null, 10) && TarpSnapshotMath.FitsPayload("0123456789", 10), "payload at the cap rejected");
            assert(!TarpSnapshotMath.FitsPayload("01234567890", 10), "payload over the cap accepted");
            assert(!TarpSnapshotMath.FitsPayload(new string('\u044f', 6), 10), "multi-byte payload measured in characters");
        });

        test("photo mode clock keeps running and rolls the day over at midnight", () =>
        {
            PhotoModeClock.Extrapolate(3, 10f, 11f, 0.01f, 100f, out int day, out float global, out float local);
            assert(day == 3 && Math.Abs(global - 11f) < 1e-4f && Math.Abs(local - 12f) < 1e-4f, "clock did not advance at the sampled rate");
            PhotoModeClock.Extrapolate(3, 23.5f, 23.9f, 0.01f, 100f, out day, out global, out local);
            assert(day == 4 && Math.Abs(global - 0.5f) < 1e-3f && Math.Abs(local - 0.9f) < 1e-3f, "midnight did not roll the day");
            PhotoModeClock.Extrapolate(3, 10f, 10f, -1f, 100f, out day, out global, out _);
            assert(day == 3 && global == 10f, "negative rate moved the clock back");
            PhotoModeClock.Extrapolate(3, 10f, 10f, float.NaN, 100f, out day, out global, out _);
            assert(day == 3 && global == 10f, "NaN rate corrupted the clock");

            assert(PhotoModeClock.SampleRate(23.9f, 0.1f, 20f, out float rate) && Math.Abs(rate - 0.01f) < 1e-4f, "midnight wrap broke the rate sample");
            assert(!PhotoModeClock.SampleRate(10f, 10.5f, 0.01f, out _), "too short a sample accepted");
            assert(!PhotoModeClock.SampleRate(10f, 5f, 10f, out _), "backwards scrub accepted as a rate");
        });
    }
}
