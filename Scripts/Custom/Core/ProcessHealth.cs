// ProcessHealth.cs - what the process itself is costing.
//
// WHY THIS EXISTS
// ---------------
// Nothing in Scripts/Custom reported a single one of these numbers, and every one of them is
// wanted by the same two things: the editor's Admin panel, which is the place somebody looks when
// the shard feels slow, and the population scale test, which has to say what raising the bot target
// costs before anybody raises it.
//
// The engine has them all and shows them in exactly one place - AdminGump, which needs a client
// (`Scripts/Gumps/AdminGump.cs:269,303`). `Core.CyclesPerSecond` and `Core.AverageCPS`
// (`Server/Main.cs:274-278`) are the main loop's own rate; `GC.GetTotalMemory` is managed heap and
// `WorkingSet64` is what the OS thinks the process has, which are different questions and both
// worth having.
//
// GC COLLECTIONS AND THREADS were added for the population scale ramp, which asks for both per
// window and could find neither: GC.CollectionCount appears nowhere else in this tree, and the only
// thread count in it is Server/Serialization.cs's save workers, which is a different question and
// is not exposed. Both are counted over a window rather than since boot - see ResetCounters.
//
// THE SAVE DURATION IS TIMED HERE RATHER THAN READ FROM ANYWHERE, because there is nowhere to read
// it from: World.Save prints its own line and keeps no number. It matters more than it looks -
// World.Save runs SYNCHRONOUSLY on the core thread and the Timer thread idles through it
// (CLAUDE.md section 4), so the save duration is the length of the longest freeze the shard has,
// and it is the figure most likely to move when the population does.

using System;
using System.Diagnostics;
using System.Globalization;

namespace Server.Custom
{
    public static class ProcessHealth
    {
        /// <summary>How long the last completed world save took, or null before the first one.</summary>
        public static TimeSpan? LastSave { get; private set; }

        /// <summary>The longest save this boot, which is the figure a budget is set against.</summary>
        public static TimeSpan? WorstSave { get; private set; }

        public static int Saves { get; private set; }

        private static Stopwatch _saving;

        // ---- the window counters ----
        //
        // GC AND THREADS ARE COUNTED OVER A WINDOW, NOT SINCE BOOT, and that is the whole reason
        // they are fields rather than a pair of bare reads. A ten-minute measurement window wants
        // the collections that happened IN IT; a since-boot total read at the end of the fourth
        // window is dominated by the first three and says nothing about any of them.
        //
        // THERE IS NO GC PAUSE TIME HERE AND THERE CANNOT BE. GC.GetTotalPauseDuration() is .NET 7
        // and later; this tree is net48 (CLAUDE.md section 1). The other route is the ".NET CLR
        // Memory" performance counter, which is Windows-only and so is barred by section 15. So
        // the honest instrument is the COUNT, and the observable pause is LoopCost's max - a
        // blocking gen2 lands in the loop period as one enormous sample, which is exactly the
        // quantity a stall is felt as. Reporting a count as though it were a duration would be
        // worse than reporting neither.
        private static int _gc0, _gc1, _gc2;
        private static long _countersOpened;

        /// <summary>Open a fresh counter window. Called by the world-census token's `reset`.</summary>
        public static void ResetCounters()
        {
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
            _countersOpened = Stopwatch.GetTimestamp();
        }

        /// <summary>Seconds since the counter window opened, or 0 before the first reset.</summary>
        public static double CounterWindowSeconds
        {
            get
            {
                return _countersOpened == 0L
                    ? 0.0
                    : (Stopwatch.GetTimestamp() - _countersOpened) / (double)Stopwatch.Frequency;
            }
        }

        /// <summary>
        /// Managed threads the OS reports for this process. Zero when it cannot be read.
        ///
        /// The same try/catch WorkingSetBytes carries, for the same reason: Mono on a locked-down
        /// host can refuse the Process query, and a zero reads as "not available" rather than as
        /// a shard with no threads.
        /// </summary>
        public static int ThreadCount
        {
            get
            {
                try
                {
                    using (Process self = Process.GetCurrentProcess())
                    {
                        return self.Threads.Count;
                    }
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>Collections in this window, as "gen0/gen1/gen2 over Ns", or since boot.</summary>
        public static string DescribeCollections()
        {
            int g0 = GC.CollectionCount(0) - _gc0;
            int g1 = GC.CollectionCount(1) - _gc1;
            int g2 = GC.CollectionCount(2) - _gc2;

            return String.Format(
                CultureInfo.InvariantCulture,
                _countersOpened == 0L
                    ? "gc {0}/{1}/{2} since boot"
                    : "gc {0}/{1}/{2} over {3:F0}s",
                g0, g1, g2, CounterWindowSeconds);
        }

        public static void Configure()
        {
            // BeforeWorldSave to AfterWorldSave brackets the whole freeze - NetState.Pause, the
            // strategy, and the resume - rather than just the strategy. The freeze is the thing
            // being measured, so the wider bracket is the honest one.
            EventSink.BeforeWorldSave += OnBefore;
            EventSink.AfterWorldSave += OnAfter;

            HealthCheck.Register("Core.Process", BuildHealthResult);
        }

        /// <summary>Managed heap, in bytes. `false` so this never forces a collection to answer.</summary>
        public static long ManagedBytes
        {
            get { return GC.GetTotalMemory(false); }
        }

        /// <summary>What the OS has given the process, in bytes. Zero if it cannot be read.</summary>
        public static long WorkingSetBytes
        {
            get
            {
                try
                {
                    using (Process self = Process.GetCurrentProcess())
                    {
                        return self.WorkingSet64;
                    }
                }
                catch
                {
                    // Mono on a locked-down host can refuse this. A zero reads as "not available"
                    // beside a managed figure that is always there.
                    return 0L;
                }
            }
        }

        /// <summary>One line, in the form every other health check writes.</summary>
        public static string Describe()
        {
            long working = WorkingSetBytes;

            // The loop clause rides here rather than on a check of its own: it is the same
            // question CyclesPerSecond answers as a mean, and a reader comparing the two should
            // not have to find them in different places. LoopCost.Describe says "off" when the
            // sampler is not running, which is the shipping state.
            int threads = ThreadCount;

            return String.Format(
                CultureInfo.InvariantCulture,
                "{0:F1} cycles/s now, {1:F1} mean; {2} managed{3}; {4}; {5}{6}; {7}",
                Core.CyclesPerSecond,
                Core.AverageCPS,
                Megabytes(ManagedBytes),
                working > 0 ? ", " + Megabytes(working) + " working set" : "",
                DescribeSaves(),
                DescribeCollections(),
                threads > 0 ? String.Format(CultureInfo.InvariantCulture, "; {0} thread(s)", threads) : "",
                LoopCost.Describe());
        }

        public static string DescribeSaves()
        {
            if (Saves == 0 || LastSave == null)
            {
                return "no world save yet this boot";
            }

            return String.Format(
                CultureInfo.InvariantCulture,
                "{0} save(s), last {1:F2}s, worst {2:F2}s",
                Saves, LastSave.Value.TotalSeconds,
                WorstSave == null ? 0.0 : WorstSave.Value.TotalSeconds);
        }

        private static string Megabytes(long bytes)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0:F0} MB", bytes / 1048576.0);
        }

        private static void OnBefore(BeforeWorldSaveEventArgs e)
        {
            _saving = Stopwatch.StartNew();
        }

        private static void OnAfter(AfterWorldSaveEventArgs e)
        {
            if (_saving == null)
            {
                return;
            }

            _saving.Stop();

            LastSave = _saving.Elapsed;
            Saves++;

            if (WorstSave == null || _saving.Elapsed > WorstSave.Value)
            {
                WorstSave = _saving.Elapsed;
            }

            _saving = null;
        }

        /// <summary>
        /// Always Ok. This reports rather than judges: there is no threshold here that is right for
        /// both an idle shard and a sixty-bot one, and a check that guesses at one would either cry
        /// wolf or say nothing, which are the only two ways a health check fails.
        /// </summary>
        private static HealthResult BuildHealthResult()
        {
            return HealthResult.Ok(Describe());
        }
    }
}
