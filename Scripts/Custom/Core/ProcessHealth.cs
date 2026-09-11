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

            return String.Format(
                CultureInfo.InvariantCulture,
                "{0:F1} cycles/s now, {1:F1} mean; {2} managed{3}; {4}",
                Core.CyclesPerSecond,
                Core.AverageCPS,
                Megabytes(ManagedBytes),
                working > 0 ? ", " + Megabytes(working) + " working set" : "",
                DescribeSaves());
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
