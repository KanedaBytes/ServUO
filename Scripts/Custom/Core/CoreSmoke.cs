using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Server.Commands;

namespace Server.Custom
{
    /// <summary>Persisted state for the smoke test. Proves CustomPersistence round-trips.</summary>
    public sealed class CoreSmokeStore : CustomPersistence
    {
        public static CoreSmokeStore Instance { get; private set; }

        /// <summary>Times [CoreSmoke has been run, across restarts.</summary>
        public int RunCount { get; set; }

        public DateTime LastRunUtc { get; set; }

        public string LastRunBy { get; set; }

        /// <summary>The value read from disk at boot, kept so the report can prove persistence.</summary>
        public int RunCountAtBoot { get; private set; }

        public CoreSmokeStore()
            : base("CoreSmoke", 0)
        {
            LastRunBy = String.Empty;
        }

        /// <summary>
        /// Constructed from CoreSmoke.Configure(), deliberately not from a static Configure() on
        /// this type: that would hide CustomPersistence.Configure() (which hooks the world
        /// events) and give one type two unrelated Configure meanings.
        /// </summary>
        internal static void Create()
        {
            if (Instance == null)
            {
                Instance = new CoreSmokeStore();
            }
        }

        protected override void Reset()
        {
            RunCount = 0;
            LastRunUtc = DateTime.MinValue;
            LastRunBy = String.Empty;
        }

        protected override void SerializeCore(GenericWriter writer)
        {
            writer.Write(RunCount);
            writer.Write(LastRunUtc);
            writer.Write(LastRunBy);
        }

        protected override void DeserializeCore(GenericReader reader, int version)
        {
            switch (version)
            {
                case 0:
                    {
                        RunCount = reader.ReadInt();
                        LastRunUtc = reader.ReadDateTime();

                        // ReadString legitimately returns null for a null that was written.
                        LastRunBy = reader.ReadString() ?? String.Empty;
                    }
                    break;
            }

            RunCountAtBoot = RunCount;
        }
    }

    /// <summary>Sample config exercised by the smoke test. Shows the required conventions.</summary>
    public sealed class CoreSmokeConfig : IValidatableConfig
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonIgnore]
        public Map Map { get; private set; }

        public static CoreSmokeConfig CreateDefault()
        {
            return new CoreSmokeConfig
            {
                Label = "core smoke test",
                MapName = "Trammel",
                X = 1475,
                Y = 1645
            };
        }

        public void Validate(ConfigErrors errors)
        {
            if (String.IsNullOrWhiteSpace(Label))
            {
                errors.Add("'label' must not be empty.");
            }

            Map map;

            if (!JsonConfig.TryParseMap(MapName, out map))
            {
                errors.Add("'map' is not a known facet: '{0}'. Valid: {1}", MapName, JsonConfig.ValidMapNames());
            }
            else
            {
                Map = map;
            }

            if (X < 0 || Y < 0)
            {
                errors.Add("'x' and 'y' must not be negative (got {0},{1}).", X, Y);
            }
        }
    }

    /// <summary>
    /// [CoreSmoke - exercises the four Custom/Core shims with no gameplay involved, and reports
    /// every registered HealthCheck.
    ///
    /// This is a permanent diagnostic, not scaffolding: it is the post-upstream-merge health
    /// check for the foundation layer. As more systems are ported they register their own
    /// checks with HealthCheck rather than growing this command.
    /// </summary>
    public static class CoreSmoke
    {
        private const string ConfigPath = "Data/Custom/core-smoke.json";

        private static readonly CustomLogger Log = CustomLogger.For("CoreSmoke");

        public static void Configure()
        {
            // Must exist before World.Load so CustomPersistence loads it. Every Configure()
            // runs before World.Load, so ordering against CustomPersistence.Configure() is
            // irrelevant here.
            CoreSmokeStore.Create();
        }

        public static void Initialize()
        {
            CommandSystem.Register("CoreSmoke", AccessLevel.Administrator, CoreSmoke_OnCommand);

            // ServUO's console handler (Scripts/Misc/ConsoleCommands.cs) only routes a fixed set
            // of words and never forwards '['-prefixed commands to CommandSystem, so there is no
            // way to run a staff command headlessly. This opt-in flag makes the smoke test
            // runnable without a client - useful for verifying a build over SSH or in CI.
            // Config: Custom.CoreSmokeOnStart (default false).
            if (Config.Get("Custom.CoreSmokeOnStart", false))
            {
                EventSink.ServerStarted += () => Run(null);
            }
        }

        [Usage("CoreSmoke")]
        [Description("Exercises the Custom/Core foundations (loop queue, JSON config, logger, persistence) and reports shard health.")]
        private static void CoreSmoke_OnCommand(CommandEventArgs e)
        {
            Run(e.Mobile);
        }

        /// <summary>
        /// Runs the smoke test. <paramref name="from"/> may be null when run from startup.
        ///
        /// Two-phase by necessity: the loop-queue probes can only be verified once the drain
        /// timer has actually ticked, and that cannot happen while this method is still running
        /// on the game thread. Phase two reports.
        /// </summary>
        public static void Run(Mobile from)
        {
            var report = new List<string>();
            bool passed = true;

            report.Add("===== [CoreSmoke =====");

            passed &= RunLoggerCheck(report);

            var probe = new LoopProbe();
            passed &= StartLoopQueueProbes(report, probe);

            // Let the real drain timer do its job. Anything shorter than a few ticks would be
            // testing our own impatience rather than the queue.
            Timer.DelayCall(TimeSpan.FromMilliseconds(750.0), () => Finish(from, report, probe, passed));
        }

        private static void Finish(Mobile from, List<string> report, LoopProbe probe, bool passed)
        {
            passed &= CompleteLoopQueueProbes(report, probe);
            passed &= RunJsonConfigCheck(report);
            passed &= RunCrossingCheck(report);
            passed &= RunRouteCacheCheck(report);
            passed &= RunPersistenceCheck(report, from);

            int failing, warning;
            RunHealthChecks(report, out failing, out warning);

            // Spell out what the verdict does and does not cover. PASS means the four Custom/Core
            // shims work; it says nothing about the systems built on them, and a report that read
            // "worst status: Fail" two lines above "RESULT: PASS" invited exactly that confusion.
            report.Add(String.Format(
                "===== RESULT: {0} (foundations){1} =====",
                passed ? "PASS" : "FAIL",
                DescribeHealth(failing, warning)));

            // To the console, so the post-restart check can be done without logging in...
            foreach (string line in report)
            {
                if (passed)
                {
                    Log.Info(line);
                }
                else
                {
                    Log.Warn(line);
                }
            }

            // ...and to the invoker, when there is one. As a gump: a smoke report is thirty-odd
            // lines and the verdict is on the last one, which is exactly the line the journal
            // loses first.
            if (from != null && !from.Deleted)
            {
                CommandReport.Send(from, passed ? "[CoreSmoke - PASSED" : "[CoreSmoke - FAILED", report);
            }
        }

        /// <summary>
        /// Bridges and other crossings a corridor has to be able to walk.
        ///
        /// A REGRESSION TEST, and it exists because of one bug. `NavWalker.ResolveZ` used to probe
        /// its hint Z once and then fall back to `map.GetAverageZ`, which is correct on open ground
        /// and wrong on anything built: a bridge deck is a chain of statics whose Z changes tile by
        /// tile, so the tile after a Z 6 deck tile is at 7, the single probe fails, and the answer
        /// comes back as the river bed underneath. The Britain-Trinsic crossing was unwalkable to
        /// every flood and audit we own for that reason alone, and an adopt of the road south left
        /// 372 waypoints unreachable because of it.
        ///
        /// Nothing cheaper catches it. The audit paths our own authored edges and there is no
        /// authored edge over that bridge; the unit tests have no map. So the check is here, in the
        /// heavyweight headless verifier, driving the same flood the corridor tool drives.
        ///
        /// Coordinates rather than waypoint ids on purpose: these crossings are not in the graph,
        /// and the point is to know the ground can be walked BEFORE anybody authors a road over it.
        ///
        /// The second bug it guards is the one the first fix left: Trinsic's canal bridges stand
        /// on wooden ramps that rise five Z per tile, one more than the window climbed, so the
        /// flood stopped at the foot of all four. And the pier: the reference stores the water's
        /// Z for the deck waypoint, thirteen below the planks, and nothing near it was standable
        /// until the seed scan. THE PIER ROW CARRIES ITS STALE Z ON PURPOSE - from Z 0 the drop
        /// window finds the deck and the row would pass without exercising anything.
        /// </summary>
        private static bool RunCrossingCheck(List<string> report)
        {
            report.Add("-- nav crossings --");

            // from-x, from-y, from-z, to-x, to-y, to-z, what it is. The Z is the hint a waypoint
            // record would carry, which for the pier is the wrong one.
            int[][] crossings =
            {
                new[] { 1473, 2159, 0, 1484, 2158, 0 },
                new[] { 1396, 1748, 0, 1410, 1734, 0 },
                new[] { 1504, 1707, 0, 1487, 1714, 0 },
                new[] { 1537, 1630, 0, 1527, 1629, 0 },
                new[] { 1966, 2818, 0, 1980, 2818, 0 },
                new[] { 1954, 2781, 10, 1981, 2780, 11 },
                new[] { 1943, 2827, 10, 1945, 2844, 15 },
                new[] { 1867, 2779, 0, 1886, 2780, 0 },
                new[] { 2054, 2855, 0, 2069, 2856, -15 },
            };

            string[] names =
            {
                "Britain-Trinsic bridge",
                "Britain west bridge",
                "Britain road bridge",
                "Britain east bridge",
                "Trinsic canal bridge east (wp-176 -> wp-173)",
                "Trinsic canal bridge centre (wp-181 -> wp-192)",
                "Trinsic canal bridge south (wp-166 -> wp-167)",
                "Trinsic canal bridge west (trin2 -> wp-179)",
                "Trinsic south pier (wp-197 -> wp-990, stored at the water Z)",
            };

            bool ok = true;

            for (int i = 0; i < crossings.Length; i++)
            {
                int[] pair = crossings[i];
                var from = new Point3D(pair[0], pair[1], pair[2]);
                var to = new Point3D(pair[3], pair[4], pair[5]);

                List<Point3D> path;
                string error;

                if (NavCorridor.TryPath(Map.Trammel, from, to, out path, out error))
                {
                    report.Add(String.Format(
                        "  ok: {0} walks in {1} tile(s)", names[i], path.Count));
                    continue;
                }

                ok = false;
                report.Add(String.Format("  FAIL: {0} - {1}", names[i], error ?? "no route"));
            }

            // The pier deck by itself: a waypoint stored at the water's Z must resolve onto the
            // planks, not stay in the water. -2 is the plank top (0x07CB at -3, height 1).
            var pier = new Point3D(2069, 2856, -15);
            int deck = NavWalker.ResolveZ(Map.Trammel, pier);

            if (deck == -2)
            {
                report.Add("  ok: pier deck resolves from the water Z -15 to -2");
            }
            else
            {
                ok = false;
                report.Add(String.Format(
                    "  FAIL: pier deck resolved from the water Z -15 to {0}, expected -2", deck));
            }

            // And the same question over WATER: the Britain-Trinsic bridge deck at 1473,2159 stands
            // 21 above the river bed its reference waypoint stores. A deck over water has no ground
            // storey, so the roof ceiling below must not apply here.
            var span = new Point3D(1473, 2159, -15);
            int spanDeck = NavWalker.ResolveZ(Map.Trammel, span);

            if (spanDeck == 6)
            {
                report.Add("  ok: bridge deck resolves from the river bed Z -15 to 6");
            }
            else
            {
                ok = false;
                report.Add(String.Format(
                    "  FAIL: bridge deck resolved from the river bed Z -15 to {0}, expected 6", spanDeck));
            }

            // A shop floor blocked by its counter (1882,2805: boards at 10 under a counter, stone
            // roof at 34) must NOT resolve onto the roof. Nothing stands there, so the seed form
            // answers false and ResolveZ falls back to the land, 0, as it always did.
            var counter = new Point3D(1882, 2805, 0);
            int roofless;
            bool stood = NavWalker.TryResolveZ(Map.Trammel, counter, out roofless);

            if (!stood && NavWalker.ResolveZ(Map.Trammel, counter) == 0)
            {
                report.Add("  ok: a blocked shop floor does not resolve onto its roof");
            }
            else
            {
                ok = false;
                report.Add(String.Format(
                    "  FAIL: blocked shop floor resolved to {0} (standable={1}), expected the land at 0",
                    roofless, stood));
            }

            // Two records on one tile (uo-wp-140 and uo-honor-trail-1): a road of no length is
            // one point, not a failure.
            var same = new Point3D(1824, 2843, 0);
            List<Point3D> single;
            string sameError;

            if (NavCorridor.TryPath(Map.Trammel, same, same, out single, out sameError) && single.Count == 1)
            {
                report.Add("  ok: a same-tile pair walks as a one-point path");
            }
            else
            {
                ok = false;
                report.Add(String.Format(
                    "  FAIL: same-tile pair - {0}", sameError ?? String.Format("{0} point(s)", single.Count)));
            }

            return ok;
        }

        /// <summary>
        /// The route cache answers for what the fleet has learned, and forgets what it has
        /// forgotten.
        ///
        /// A REGRESSION TEST for REVIEW.md F4. Edge penalties are read inside NavGraph.Search and
        /// a cache hit returns before Search runs, so a warm route kept crossing an edge after
        /// that edge was struck, and kept detouring around one after the strike had expired.
        /// NavEdgeHealth existed, ran, logged - and changed nobody's route for as long as somebody
        /// else's journey kept the entry warm.
        ///
        /// ON A GRAPH OF ITS OWN, four waypoints wide, rather than on the live one. The assertion
        /// is "a strike moves the route", and on real data that needs a pair of towns whose second-
        /// best road stays second-best through every edit anyone makes to navigation.json - which
        /// is a test that breaks for reasons having nothing to do with the thing under test. Here
        /// the alternative is arithmetic: A-B-C costs 16, A-D-C costs 24, and two strikes on A-B
        /// price it at 28.
        ///
        /// The ids are prefixed so they cannot collide with a real edge in the shared health
        /// table, and the strike is expired rather than cleared at the end, so what the last leg
        /// exercises is the ORDINARY expiry path and not a test-only shortcut.
        /// </summary>
        private static bool RunRouteCacheCheck(List<string> report)
        {
            report.Add("-- navigation route cache --");

            const string A = "smoke-cache-a";
            const string B = "smoke-cache-b";
            const string C = "smoke-cache-c";
            const string D = "smoke-cache-d";

            var store = new NavigationStore
            {
                Waypoints =
                {
                    new NavWaypoint(A, Map.Trammel, new Point3D(1000, 1000, 0)),
                    new NavWaypoint(B, Map.Trammel, new Point3D(1008, 1000, 0)),
                    new NavWaypoint(C, Map.Trammel, new Point3D(1016, 1000, 0)),
                    new NavWaypoint(D, Map.Trammel, new Point3D(1008, 1012, 0))
                },
                Edges =
                {
                    new NavEdge(A, B),
                    new NavEdge(B, C),
                    new NavEdge(A, D),
                    new NavEdge(D, C)
                }
            };

            var graph = new NavGraph();
            graph.Build(store, new Dictionary<string, double>(), 12);

            bool ok = true;

            try
            {
                NavRoute first, warm, struck, recovered;
                string error;

                if (!graph.TryFindPath(A, C, 512, 300, out first, out error))
                {
                    report.Add("  FAIL: the synthetic graph did not route at all - " + error);
                    return false;
                }

                ok &= Leg(report, "the cheap road is chosen cold", first, B);

                // The cache is doing something, proven by identity rather than asserted. Without
                // this line every assertion below would also pass against a cache that was never
                // consulted, which is the one way this test could quietly stop testing anything.
                graph.TryFindPath(A, C, 512, 300, out warm, out error);

                if (ReferenceEquals(first, warm))
                {
                    report.Add("  ok: the second lookup is served from the cache");
                }
                else
                {
                    ok = false;
                    report.Add("  FAIL: the second lookup re-searched - the cache is not being used, "
                        + "so nothing below is a test of invalidation");
                }

                // Two strikes: 1 + 0.75 x 2 = 2.5, which prices the 8-tile A-B leg at 20 and the
                // whole direct road at 28 against the detour's 24.
                NavEdgeHealth.Strike(A, B);
                NavEdgeHealth.Strike(A, B);

                graph.TryFindPath(A, C, 512, 300, out struck, out error);

                ok &= Leg(report, "a strike reroutes a WARM route", struck, D);

                NavEdgeHealth.ExpireNow(A, B);

                graph.TryFindPath(A, C, 512, 300, out recovered, out error);

                ok &= Leg(report, "the expiry brings the cheap road back", recovered, B);
            }
            catch (Exception ex)
            {
                ok = false;
                report.Add("  FAIL: " + ex.Message);
            }
            finally
            {
                // Whatever happened above, the shared table goes back to what it was: these ids
                // are not real edges and a leftover strike would sit in [NavEdges for a quarter of
                // an hour claiming otherwise.
                NavEdgeHealth.ExpireNow(A, B);
                NavEdgeHealth.Sweep();
            }

            return ok;
        }

        /// <summary>Asserts the middle waypoint of a three-step synthetic route.</summary>
        private static bool Leg(List<string> report, string what, NavRoute route, string expected)
        {
            string actual = route != null && route.Count == 3 ? route.Steps[1].WaypointId : null;

            if (String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                report.Add(String.Format("  ok: {0} (via {1})", what, expected));
                return true;
            }

            report.Add(String.Format(
                "  FAIL: {0} - expected a route via {1}, got {2}",
                what,
                expected,
                route == null ? "no route" : DescribeRoute(route)));

            return false;
        }

        private static string DescribeRoute(NavRoute route)
        {
            var ids = new List<string>();

            for (int i = 0; i < route.Steps.Count; i++)
            {
                ids.Add(route.Steps[i].WaypointId ?? "?");
            }

            return String.Join(" -> ", ids);
        }

        private static bool RunLoggerCheck(List<string> report)
        {
            report.Add("-- logger --");

            try
            {
                var log = CustomLogger.For("CoreSmoke.Logger");

                log.Debug("Debug level (visible only with -debug; Core.Debug={0}).", Core.Debug);
                log.Info("Info level.");
                log.Warn("Warn level.");
                log.Error("Error level (expected - this is the smoke test).");

                // A stray brace must not blow up the formatter.
                log.Info("Braces in a message are safe: {not a placeholder}");

                report.Add("  ok: four levels emitted; Core.Debug=" + Core.Debug);
                return true;
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: " + ex.Message);
                return false;
            }
        }

        /// <summary>Carries probe results from phase one to phase two.</summary>
        private sealed class LoopProbe
        {
            public volatile bool BackgroundRan;
            public volatile int MobileCount = -1;
            public volatile int RanOnThreadId = -1;
            public volatile bool WaitCompleted;
            public volatile bool WaitOk;
            public volatile int WaitValue = -1;
            public volatile string WaitError;
            public long FaultedBefore;
        }

        /// <summary>
        /// Phase one: post the probes from real background threads and return. Nothing is
        /// verified here - the drain timer has to tick first, and it cannot while we hold the
        /// game thread.
        /// </summary>
        private static bool StartLoopQueueProbes(List<string> report, LoopProbe probe)
        {
            report.Add("-- loop queue --");

            if (!LoopQueue.Running)
            {
                report.Add("  FAIL: drain timer is not running.");
                return false;
            }

            try
            {
                probe.FaultedBefore = LoopQueue.TotalFaulted;

                // 1. A background thread posts work that touches world state.
                var poster = new Thread(
                    () => LoopQueue.Post(
                        () =>
                        {
                            probe.MobileCount = World.Mobiles.Count;
                            probe.RanOnThreadId = Thread.CurrentThread.ManagedThreadId;
                            probe.BackgroundRan = true;
                        }))
                {
                    Name = "CoreSmoke Poster",
                    IsBackground = true
                };

                poster.Start();

                // 2. A background thread does a full request/response round-trip. This is the
                //    real admin-API path: it blocks off-thread until the game thread drains it.
                //    Deliberately not joined - joining here would block the very thread that has
                //    to run the work.
                var waiter = new Thread(
                    () =>
                    {
                        int value;
                        string error;

                        probe.WaitOk = LoopQueue.TryPostAndWait(
                            () => World.Items.Count, TimeSpan.FromSeconds(10.0), out value, out error);

                        probe.WaitValue = value;
                        probe.WaitError = error;
                        probe.WaitCompleted = true;
                    })
                {
                    Name = "CoreSmoke Waiter",
                    IsBackground = true
                };

                waiter.Start();

                // 3. A faulting callback must be caught, counted, and must not kill the loop.
                LoopQueue.Post(() => { throw new InvalidOperationException("Deliberate CoreSmoke fault."); });

                report.Add("  posted 3 probes from background threads; verifying after the drain timer ticks...");
                return true;
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: " + ex.Message);
                return false;
            }
        }

        /// <summary>Phase two: the drain timer has run, so the probes can be judged.</summary>
        private static bool CompleteLoopQueueProbes(List<string> report, LoopProbe probe)
        {
            bool ok = true;

            if (probe.BackgroundRan)
            {
                report.Add(
                    String.Format(
                        "  ok: cross-thread Post ran on the game thread (mobiles={0}, thread={1}).",
                        probe.MobileCount,
                        probe.RanOnThreadId));
            }
            else
            {
                report.Add("  FAIL: cross-thread Post never executed.");
                ok = false;
            }

            if (!probe.WaitCompleted)
            {
                report.Add("  FAIL: TryPostAndWait never returned.");
                ok = false;
            }
            else if (probe.WaitOk)
            {
                report.Add("  ok: cross-thread TryPostAndWait returned items=" + probe.WaitValue + ".");
            }
            else
            {
                report.Add("  FAIL: TryPostAndWait: " + probe.WaitError);
                ok = false;
            }

            if (LoopQueue.TotalFaulted > probe.FaultedBefore)
            {
                report.Add("  ok: faulting callback was caught and counted; loop still running.");

                // Our own probe fault is expected, so clear it from the health baseline.
                // Otherwise running this diagnostic would leave the shard reporting Warn forever.
                LoopQueue.AcknowledgeFaults();
            }
            else
            {
                report.Add("  FAIL: faulting callback was not counted; faulted=" + LoopQueue.TotalFaulted);
                ok = false;
            }

            report.Add(
                String.Format(
                    "  stats: queued={0} processed={1} faulted={2} peak={3} budget={4}",
                    LoopQueue.QueuedCount,
                    LoopQueue.TotalProcessed,
                    LoopQueue.TotalFaulted,
                    LoopQueue.MaxObservedDepth,
                    LoopQueue.Budget));

            return ok;
        }

        private static bool RunJsonConfigCheck(List<string> report)
        {
            report.Add("-- json config --");

            bool ok = true;

            try
            {
                string fullPath = JsonConfig.Resolve(ConfigPath);

                if (!File.Exists(fullPath))
                {
                    string writeError;

                    if (!JsonConfig.TrySave(ConfigPath, CoreSmokeConfig.CreateDefault(), out writeError))
                    {
                        report.Add("  FAIL: could not seed " + ConfigPath + ": " + writeError);
                        return false;
                    }

                    report.Add("  seeded " + ConfigPath);
                }

                // Round-trip.
                CoreSmokeConfig loaded;
                IList<string> errors;

                if (JsonConfig.TryLoad(ConfigPath, out loaded, out errors))
                {
                    report.Add(
                        String.Format(
                            "  ok: loaded label='{0}' map={1} at {2},{3}",
                            loaded.Label,
                            loaded.Map,
                            loaded.X,
                            loaded.Y));
                }
                else
                {
                    foreach (string error in errors)
                    {
                        report.Add("  FAIL: " + error);
                    }

                    ok = false;
                }

                // The compact layout is what keeps an editor save to a minimal diff: a container
                // of scalars stays on one line, so an array of flat objects renders one object
                // per line. Assert it, because every JSON config the editor round-trips depends
                // on it and a regression here would be invisible until a diff exploded.
                var nested = JToken.Parse(
                    "{\"zones\":[{\"name\":\"a\",\"x\":1},{\"name\":\"b\",\"x\":2}],\"flat\":{\"k\":1}}");

                string compact = JsonConfig.SerializeCompact(nested);
                string[] lines = compact.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

                bool layoutOk = lines.Length == 7
                    && lines[2].Trim() == "{\"name\":\"a\",\"x\":1},"
                    && lines[3].Trim() == "{\"name\":\"b\",\"x\":2}"
                    && lines[5].Trim() == "\"flat\": {\"k\":1}";

                if (layoutOk)
                {
                    report.Add("  ok: compact writer emits one object per line (" + lines.Length + " lines).");
                }
                else
                {
                    report.Add("  FAIL: compact layout changed - the shard editor would produce huge diffs.");

                    for (int i = 0; i < lines.Length; i++)
                    {
                        report.Add("    [" + i + "] " + lines[i]);
                    }

                    ok = false;
                }

                // Failure case 1: an unknown key must be a collected error, not silence.
                ok &= ExpectLoadFailure(
                    report,
                    "misspelled key",
                    "{\"label\":\"x\",\"map\":\"Trammel\",\"x\":1,\"y\":2,\"labell\":\"typo\"}");

                // Failure case 2: a bad facet must be caught by TryParseMap, NOT surface as an
                // ArgumentException out of Map.Parse.
                ok &= ExpectLoadFailure(
                    report,
                    "invalid map name",
                    "{\"label\":\"x\",\"map\":\"Sosaria\",\"x\":1,\"y\":2}");
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: unexpected " + ex.GetType().Name + ": " + ex.Message);
                ok = false;
            }

            return ok;
        }

        /// <summary>
        /// Writes a deliberately bad config to a scratch file and asserts TryLoad reports it as
        /// collected errors rather than throwing.
        /// </summary>
        private static bool ExpectLoadFailure(List<string> report, string label, string json)
        {
            const string scratchPath = "Data/Custom/core-smoke-invalid.json";
            string fullPath = JsonConfig.Resolve(scratchPath);

            try
            {
                string directory = Path.GetDirectoryName(fullPath);

                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, json);

                CoreSmokeConfig bad;
                IList<string> errors;

                if (JsonConfig.TryLoad(scratchPath, out bad, out errors))
                {
                    report.Add("  FAIL: " + label + " was accepted but should have been rejected.");
                    return false;
                }

                string first = errors.Count > 0 ? errors[0] : "(no detail)";

                if (first.Length > 110)
                {
                    first = first.Substring(0, 110) + "...";
                }

                report.Add("  ok: " + label + " rejected with " + errors.Count + " error(s): " + first);
                return true;
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: " + label + " threw instead of collecting: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }
                catch
                {
                    // Scratch file; a leftover is harmless.
                }
            }
        }

        private static bool RunPersistenceCheck(List<string> report, Mobile from)
        {
            report.Add("-- persistence --");

            CoreSmokeStore store = CoreSmokeStore.Instance;

            if (store == null)
            {
                report.Add("  FAIL: CoreSmokeStore was never constructed.");
                return false;
            }

            if (store.IsDegraded)
            {
                report.Add("  FAIL: store is DEGRADED - " + store.DegradedReason);
                report.Add("  Saving is disabled for this store. Restore " + store.FilePath + " from Backups/.");
                return false;
            }

            report.Add("  RunCount read from disk at boot: " + store.RunCountAtBoot);
            report.Add(
                store.LastRunUtc == DateTime.MinValue
                    ? "  no previous run recorded (first boot)"
                    : "  last run " + store.LastRunUtc.ToString("u") + " by " + store.LastRunBy);

            store.RunCount++;
            store.LastRunUtc = DateTime.UtcNow;
            store.LastRunBy = from != null ? from.Name : "console";

            report.Add("  RunCount now " + store.RunCount + " (in memory).");
            report.Add("  Run [save, restart, then [CoreSmoke again - boot value should read " + store.RunCount + ".");

            return true;
        }

        private static void RunHealthChecks(List<string> report, out int failing, out int warning)
        {
            failing = 0;
            warning = 0;

            report.Add("-- health checks --");

            List<HealthResult> results = HealthCheck.RunAll();

            if (results.Count == 0)
            {
                report.Add("  (none registered)");
                return;
            }

            foreach (HealthResult result in results)
            {
                report.Add("  " + result);

                if (result.Status == HealthStatus.Fail)
                {
                    failing++;
                }
                else if (result.Status == HealthStatus.Warn)
                {
                    warning++;
                }
            }

            report.Add("  worst status: " + HealthCheck.Worst(results));
        }

        /// <summary>
        /// The health-check tail of the result line.
        ///
        /// The verdict above it is about the FOUNDATIONS only - the logger, the loop queue, the
        /// JSON layer and persistence. Every other system reports through HealthCheck, and one of
        /// those failing does not make the foundations broken. Saying so on the same line is what
        /// stops "worst status: Fail" and "RESULT: PASS" reading as a contradiction.
        /// </summary>
        private static string DescribeHealth(int failing, int warning)
        {
            if (failing == 0 && warning == 0)
            {
                return "; all health checks OK";
            }

            var parts = new List<string>(2);

            if (failing > 0)
            {
                parts.Add(String.Format("{0} health check{1} failing", failing, failing == 1 ? "" : "s"));
            }

            if (warning > 0)
            {
                parts.Add(String.Format(
                    "{0} warning", warning));
            }

            return "; " + String.Join(", ", parts.ToArray()) + " - see above";
        }
    }
}
