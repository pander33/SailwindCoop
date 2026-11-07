using System;
using System.Collections.Generic;
using SailwindCoop.Sync;

internal static class SupportHistoryTests
{
    private sealed class Sample
    {
        internal long Tick;
        internal string Support, Details;
    }
    private static SupportHistory<Sample> History() => new SupportHistory<Sample>(s => s.Tick, (a, b) => a.Support == b.Support);
    internal static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("support topology and full payload survive all arrival permutations including A B A", () =>
        {
            int[][] orders = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
                new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            foreach (string[] supports in new[] { new[] { "shore", "boatA", "boatA" },
                new[] { "shore", "boatA", "boatB" }, new[] { "boatA", "boatB", "boatA" } })
            foreach (var order in orders)
            {
                var history = History();
                foreach (int index in order)
                    assert(history.Push(new Sample { Tick = 50 + 50 * index, Support = supports[index], Details = "hands/head:" + index }), "unplayed sample rejected");
                assert(history.Count == 3, "arrival order changed sample count");
                for (int i = 0; i < 3; i++)
                    assert(history[i].Tick == 50 + 50 * i && history[i].Support == supports[i] && history[i].Details == "hands/head:" + i,
                        "source support or animation details were lost");
                assert(history.EndOfRun(0) == 0 && history.EndOfRun(1) == (supports[1] == supports[2] ? 2 : 1),
                    "late intermediate support did not split the original run");
            }
        });
        test("support history preserves the played boundary and rejects conflicting equal ticks", () =>
        {
            var history = History();
            history.Push(new Sample { Tick = 50, Support = "shore" });
            history.Push(new Sample { Tick = 150, Support = "shore" });
            history.Prune(75);
            assert(history.Push(new Sample { Tick = 100, Support = "boat" }), "unplayed excursion was rejected");
            history.Prune(110);
            assert(history.Count == 2 && history[0].Tick == 100, "pruning removed the interpolation anchor");
            assert(!history.Push(new Sample { Tick = 90, Support = "shore" }), "already displayed topology was rewritten");
            assert(!history.Push(new Sample { Tick = 150, Support = "boat" }), "equal tick changed support ambiguously");
            assert(history.Push(new Sample { Tick = 150, Support = "shore", Details = "final" }) && history[1].Details == "final",
                "same-frame final sample was not replaced");
            history.Prune(80);
            assert(history.PlayedTick == 110, "older caller rewound the playback boundary");
        });
        test("a stalled support history accepts fresh shore poses after overflow and stays bounded", () =>
        {
            var history = History();
            for (int i = 0; i < 128; i++) assert(history.Push(new Sample { Tick = i, Support = "boat" }), "capacity shortened unexpectedly");
            for (int i = 128; i < 384; i++)
            {
                assert(history.Push(new Sample { Tick = i, Support = "shore" }), "stalled boat history rejected the fresh shore pose");
                assert(history.Count <= 128 && history[history.Count - 1].Tick == i, "latest pose was lost or memory grew without bound");
                if (i == 128)
                {
                    assert(history.Restarted && history.Count == 1 && history[0].Support == "shore", "stale support survived restart");
                    assert(!history.Push(new Sample { Tick = 50, Support = "boat" }), "an old packet immediately resurrected the discarded support");
                }
            }
            assert(!history.Push(new Sample { Tick = 50, Support = "boat" }), "an obsolete packet replaced the recovered shore pose");
            history.Clear();
            assert(history.Count == 0 && history.PlayedTick == long.MinValue && history.Push(new Sample { Tick = 1, Support = "new owner" }),
                "drop or refit retained the previous playback epoch");
        });
        test("each observer decides historical usefulness using its own playback boundary", () =>
        {
            var host = History(); var observer = History();
            foreach (var history in new[] { host, observer })
            {
                history.Push(new Sample { Tick = 50, Support = "shore" });
                history.Push(new Sample { Tick = 150, Support = "boat" });
            }
            host.Prune(120); observer.Prune(75);
            var late = new Sample { Tick = 100, Support = "boat" };
            assert(!host.Push(late) && observer.Push(late), "one playback boundary was imposed on both peers");
            assert(observer.EndOfRun(1) == 2 && observer[1].Tick == 100, "observer could not refine its unplayed crossing");
        });
    }
}
