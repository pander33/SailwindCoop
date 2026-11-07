using System;
using System.Collections.Generic;
using SailwindCoop.Sync;

internal static class PlayerPlaybackTests
{
    private struct Arrival
    {
        internal long At;
        internal long Tick;
    }

    internal static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("remote playback keeps the configured LAN delay", () =>
        {
            var clock = new PlayerPlaybackClock();
            for (long now = 0; now < 5000; now += 10)
            {
                if (now % 50 == 0) clock.Observe(now - 10, now, now);
                assert(clock.Advance(now, now, 100) == now - 100, "added delay on a stable LAN");
            }
        });
        test("remote playback buffers direct and relayed streams independently", () =>
        {
            CheckPath(150, 50, assert);
            CheckPath(250, 80, assert);
            CheckPath(500, 120, assert);
        });
        test("remote playback ignores stale arrivals and absorbs clock jumps", () =>
        {
            var clock = new PlayerPlaybackClock();
            clock.Observe(750, 1000, 1000);
            long initial = clock.Advance(1000, 1000, 100);
            clock.Observe(500, 1000, 1000);
            clock.Observe(750, 1000, 1000);
            assert(clock.Advance(1000, 1000, 100) == initial, "playback changed without elapsed time");
            for (int i = 1; i <= 200; i++)
            {
                long local = 1000 + i * 10;
                long server = local + (i < 100 ? -150 : 150);
                long tick = clock.Advance(server, local, 100);
                assert(tick - initial >= 7 && tick - initial <= 12, "clock correction jumped or rewound playback");
                initial = tick;
            }
        });
        test("remote playback releases extra delay after the link recovers", () =>
        {
            var clock = new PlayerPlaybackClock();
            clock.Observe(500, 1000, 1000);
            long tick = clock.Advance(1000, 1000, 100);
            assert(1000 - tick >= 500, "first packet did not seed the delay");
            for (long now = 1010; now <= 25000; now += 10)
            {
                if (now % 50 == 0) clock.Observe(now - 10, now, now);
                tick = clock.Advance(now, now, 100);
            }
            assert(25000 - tick <= 110, "playback stayed far behind after recovery");
        });
    }

    private static void CheckPath(int baseDelay, int jitter, Action<bool, string> assert)
    {
        var arrivals = new List<Arrival>();
        var random = new Random(27);
        for (long tick = 0; tick < 30000; tick += 50)
        {
            if (random.Next(100) < 8 || tick % 4300 == 0 || tick % 4300 == 50) continue;
            arrivals.Add(new Arrival { At = tick + baseDelay + random.Next(jitter + 1), Tick = tick });
        }
        arrivals.Sort((a, b) => a.At.CompareTo(b.At));

        var clock = new PlayerPlaybackClock();
        int next = 0, fixedUnderruns = 0, adaptiveUnderruns = 0, frames = 0;
        long newest = -1;
        for (long now = 0; now < 30000; now += 10)
        {
            while (next < arrivals.Count && arrivals[next].At <= now)
            {
                long tick = arrivals[next++].Tick;
                clock.Observe(tick, now, now);
                newest = Math.Max(newest, tick);
            }
            if (newest < 0) continue;
            long renderTick = clock.Advance(now, now, 100);
            if (now < 3000) continue;
            frames++;
            if (now - 100 > newest) fixedUnderruns++;
            if (renderTick > newest) adaptiveUnderruns++;
        }
        Console.WriteLine($"    Pose delay {baseDelay} + 0..{jitter} ms: fixed {fixedUnderruns}/{frames}, adaptive {adaptiveUnderruns}/{frames} underruns");
        assert(fixedUnderruns == frames && adaptiveUnderruns < frames / 20, "adaptive buffer did not cover the tested link");
    }
}
