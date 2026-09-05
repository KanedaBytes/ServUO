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
            passed &= RunPersistenceCheck(report, from);

            RunHealthChecks(report);

            report.Add(passed ? "===== RESULT: PASS =====" : "===== RESULT: FAIL =====");

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

            // ...and to the invoker, when there is one.
            if (from != null && !from.Deleted)
            {
                foreach (string line in report)
                {
                    from.SendMessage(passed ? 0x40 : 0x25, line);
                }
            }
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

        private static void RunHealthChecks(List<string> report)
        {
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
            }

            report.Add("  worst status: " + HealthCheck.Worst(results));
        }
    }
}
