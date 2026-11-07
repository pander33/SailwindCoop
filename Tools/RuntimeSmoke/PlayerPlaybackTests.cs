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

    private sealed class InstrumentSample
    {
        internal uint Revision;
        internal long Tick;
    }

    internal static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("instrument drop final cannot cross a subsequent root revision with a retained acknowledgement", () =>
        {
            var epoch = new InstrumentRequestEpoch();
            epoch.Observe(11, 2, 40);
            assert(epoch.Accept(10, 11, 2, 40), "the immediate pre-ack drop final was lost");
            epoch.Observe(12, 2, 40); // Host picks up; the old acknowledgement stays in the root state.
            assert(!epoch.Accept(10, 12, 2, 40), "old guest final overwrote a later host pickup");
            assert(epoch.Accept(12, 12, 3, 1), "current revision was treated as an eligibility check");
            epoch.Observe(12, 3, 1); // A root action need not change any semantic field.
            assert(epoch.Accept(11, 12, 3, 1), "new acknowledgement without a semantic change was lost");
            assert(!epoch.Accept(11, 12, 2, 40), "superseded acknowledgement remained active");
            epoch.Observe(uint.MaxValue, 3, 2);
            epoch.Observe(1, 3, 2);
            assert(!epoch.Accept(uint.MaxValue, 1, 3, 2), "revision wrap reopened a previous handoff");
        });
        test("instrument playback stays continuous across slower and faster owners and fallback", () =>
        {
            var clock = new PlayerPlaybackClock(90);
            long tick = clock.AdvanceTowards(900, 1000);
            assert(tick == 900, "initial owner clock was not adopted");
            for (long now = 1016; now <= 4200; now += 16)
            {
                if (now == 1608) clock.Reset(preservePosition: true);
                long target = now - (now < 2600 ? 300 : 100);
                long next = clock.AdvanceTowards(target, now);
                assert(next - tick >= 12 && next - tick <= 19, "owner switch jumped or rewound instrument time");
                tick = next;
            }
            clock.Observe(4000, 4200, 4200);
            long fallback = clock.Advance(4200, 4200, 90);
            assert(fallback >= tick && fallback - tick <= 19, "fallback abandoned the continuous clock");
            clock.Reset();
            assert(clock.AdvanceTowards(100, 5000) == 100, "local pickup retained a previous playback lifecycle");
        });
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
        test("sparse NPC movement starts with a full snapshot interval of reserve", () =>
        {
            var clock = new PlayerPlaybackClock(225);
            long newest = 0;
            for (long now = 160; now < 3000; now += 10)
            {
                if ((now - 160) % 200 == 0)
                {
                    newest = now - 160;
                    clock.Observe(newest, now, now);
                }
                assert(clock.Advance(now, now, 225) <= newest, "5 Hz stream outran a known sample");
            }
        });
        test("an early instrument revision leaves the live pose history available until its item catches up", () =>
        {
            var pending = new InstrumentRevisionQueue<InstrumentSample>(sample => sample.Revision);
            var live = new SnapshotBuffer<InstrumentSample>(sample => sample.Tick, 32);
            live.Push(new InstrumentSample { Revision = uint.MaxValue, Tick = 100 });
            pending.Enqueue(new InstrumentSample { Revision = 1, Tick = 150 });
            pending.Enqueue(new InstrumentSample { Revision = 1, Tick = 200 });
            pending.Enqueue(new InstrumentSample { Revision = 2, Tick = 250 });
            assert(!pending.TryTake(uint.MaxValue, out _) && live[0].Tick == 100, "future instrument state interrupted playback");
            while (pending.TryTake(1, out var sample)) live.Push(sample);
            assert(live.Count == 3 && live[1].Tick == 150 && live[2].Tick == 200, "confirmation lost the current interpolation samples");
            assert(!pending.TryTake(1, out _) && pending.TryTake(2, out var next) && next.Tick == 250, "newer revision did not wait");
            pending.Enqueue(new InstrumentSample { Revision = 3, Tick = 300 });
            pending.Enqueue(new InstrumentSample { Revision = 4, Tick = 350 });
            assert(pending.TryTake(4, out var latest) && latest.Tick == 350, "superseded item revision blocked the current one");
        });
        test("a reordered sample moves the next support boundary only before playback reaches it", () =>
        {
            var boat = new SnapshotBuffer<long>(tick => tick, 2);
            boat.Push(150);
            assert(PoseSupportOrder.CanPrepend(100, 75, 50, boat[0]) && boat.TryPrepend(100) && boat[0] == 100 && boat[1] == 150,
                "first deck pose was lost when the second arrived first");
            assert(!PoseSupportOrder.CanPrepend(100, 120, 50, 150), "already played time moved backwards");
            assert(!PoseSupportOrder.CanPrepend(100, 75, 110, 150), "new support cut off a confirmed shore sample");
            assert(!boat.TryPrepend(90) && boat[0] == 100, "late pose displaced data from a full buffer");
        });
        test("a missing intermediate support fits only inside the unplayed gap", () =>
        {
            // Shore at 50 and boat B at 150 arrived before the sole boat A sample at 100.
            assert(PoseSupportOrder.CanPrepend(100, 75, 50, 150), "intermediate boat was rejected");
            assert(!PoseSupportOrder.CanPrepend(100, 100, 50, 150), "already rendered transition was reopened");
            assert(!PoseSupportOrder.CanPrepend(100, 75, 100, 150), "intermediate boat displaced a confirmed support");
            assert(!PoseSupportOrder.CanPrepend(150, 75, 50, 150), "duplicate boundary created another support");
        });
        test("hinges resume smoothly after loss on their own stream and rejoin the hull clock", () =>
        {
            var playback = new HingePlayback();
            assert(playback.Advance(-75, 0, 150) == -75, "steady playback did not follow the hull");
            long tick = -75;
            for (long now = 160; now <= 1400; now += 10)
            {
                long newest = now < 350 ? 0 : (now - 150) / 50 * 50;
                long next = playback.Advance(now - 225, newest, now);
                assert(next >= tick && next - tick <= 14, "hinge playback jumped when its packets resumed");
                if (now >= 230 && now < 350) assert(next == 0, "hinge advanced beyond the known rotation");
                if (now == 350) assert(next > 0 && next < now - 225, "resumption did not replay the missing interval");
                tick = next;
            }
            assert(tick == 1400 - 225, "hinge remained behind the hull after recovery");

            playback.Clear();
            assert(playback.Advance(100, 100, 150) == 100, "clear retained the previous hull time");
            playback.MarkRest(100, 5000);
            assert(playback.Advance(5000, 5050, 5100) == 5000, "idle stream tried to replay a stationary interval");
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
