// LoopCost.cs - the whole-game-loop distribution, which nothing in this tree could report.
//
// WHY THIS EXISTS
// ---------------
// REVIEW.md:114 asks for "whole-game-loop p95/p99/max delay" and says in the same breath that the
// existing BotTick stopwatch cannot supply it: that stopwatch times one callback and excludes the
// walker, every other timer, the packet handlers and the delta queues. REVIEW.md:184 repeats the
// point against the docs that overstate it. Core.CyclesPerSecond and Core.AverageCPS
// (Server/Main.cs:274-278) are the only whole-loop numbers the engine keeps, and a mean rate hides
// exactly the thing a scale test is looking for - the one cycle in a thousand that took 200 ms.
//
// So this is that number, and it is a STANDING instrument rather than a session tool: any future
// pathfinder, population or behaviour work reports against it.
//
// HOW IT HOOKS, AND WHY NO UPSTREAM EDIT IS NEEDED
// ------------------------------------------------
// Server/Main.cs:586-588 ends every iteration of the main loop with:
//
//     if (Slice != null)
//     {
//         Slice();
//     }
//
// `public static Slice Slice;` (Main.cs:41) is a plain delegate field and NOTHING IN THE TREE
// SUBSCRIBES TO IT - CLAUDE.md section 4 says so and a grep confirms it: Main.cs is the only file
// that names it. So `Core.Slice += OnSlice` is a free per-cycle hook on the game thread, with no
// upstream edit and nothing to displace. It is deliberately `+=` rather than `=`: if a later
// system wants the same hook, both get it.
//
// WHAT IT MEASURES, STATED HONESTLY
// ---------------------------------
// The gap between consecutive Slice callbacks is the whole cycle PERIOD - `_Signal.WaitOne()` plus
// the body - and this hook cannot split the two, because there is only one hook per cycle and it
// is at the end. So:
//
//   - On a BUSY shard the wait collapses towards zero and the period IS the work. That is the
//     regime a scale test runs in and the regime these numbers are for.
//   - On an IDLE shard the period is mostly the loop blocked on its AutoResetEvent, and a large
//     p99 there means nothing at all.
//   - The MAX is meaningful in both regimes and in only one direction: it is the longest the shard
//     went without completing a cycle, which is the worst stall a client could have felt. A world
//     save shows up here as a single enormous sample, which is correct - World.Save runs
//     synchronously on this thread (CLAUDE.md section 4) and the freeze is real.
//
// This is the same quantity Core.CyclesPerSecond reports as a rate; the value added is the tail.
// Both are printed side by side so the two can be checked against each other.
//
// Stopwatch timestamps rather than Core.TickCount, which is millisecond-resolution and would read
// zero for most cycles on a shard that is keeping up.
//
// OFF BY DEFAULT. Custom.LoopCostSampler, and the shipping value is False.

using System;
using System.Diagnostics;
using System.Globalization;

namespace Server.Custom
{
    public static class LoopCost
    {
        private static readonly CustomLogger Log = CustomLogger.For("Core");

        /// <summary>
        /// Samples held. Sixty thousand is a few minutes of a busy loop and about half a megabyte;
        /// the window wraps rather than growing, so a sampler left on overnight costs the same as
        /// one left on for a minute.
        /// </summary>
        private const int Capacity = 60000;

        private static readonly long[] _samples = new long[Capacity];

        private static int _count;
        private static int _next;
        private static long _last;
        private static long _dropped;

        private static bool _hooked;
        private static long _startedAt;

        public static bool Enabled { get; private set; }

        /// <summary>Cycles seen since the window opened, including any the ring has since overwritten.</summary>
        public static long Cycles { get; private set; }

        public static void Initialize()
        {
            Enabled = Config.Get("Custom.LoopCostSampler", false);

            if (!Enabled)
            {
                return;
            }

            Hook();
            Log.Info("Loop cost sampler on. Window {0} cycle(s).", Capacity);
        }

        /// <summary>
        /// Turn the sampler on or off at runtime, so a measurement window can be opened without a
        /// restart. Returns the state it ended in.
        /// </summary>
        public static bool SetEnabled(bool on)
        {
            if (on == Enabled)
            {
                return Enabled;
            }

            Enabled = on;

            if (on)
            {
                Reset();
                Hook();
            }

            // Unhooking is deliberately not done: Core.Slice is a plain delegate field and
            // removing one subscriber while the loop is between `!= null` and the invoke is a race
            // this file will not take for the sake of a few nanoseconds. OnSlice returns
            // immediately when Enabled is false.
            return Enabled;
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }

            Core.Slice += OnSlice;
            _hooked = true;
            Reset();
        }

        /// <summary>Open a fresh window. The next cycle seeds the clock rather than being counted.</summary>
        public static void Reset()
        {
            _count = 0;
            _next = 0;
            _last = 0L;
            _dropped = 0L;
            Cycles = 0L;
            _startedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Called once per main-loop iteration, on the game thread. Everything in here is a
        /// timestamp, a subtract and an array store; a hook on this path must not allocate and must
        /// not branch on anything expensive.
        /// </summary>
        private static void OnSlice()
        {
            if (!Enabled)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();

            if (_last != 0L)
            {
                long delta = now - _last;

                if (delta < 0L)
                {
                    // Stopwatch is monotonic, so this cannot happen; counted rather than trusted,
                    // because a Mono host with a broken high-resolution timer would otherwise
                    // poison a percentile silently.
                    _dropped++;
                }
                else
                {
                    _samples[_next] = delta;
                    _next = (_next + 1) % Capacity;

                    if (_count < Capacity)
                    {
                        _count++;
                    }

                    Cycles++;
                }
            }

            _last = now;
        }

        /// <summary>
        /// p50 / p95 / p99 / max, in milliseconds, over the window. Sorts a copy on demand - this
        /// runs from a health check every sixty seconds at worst, and keeping the ring sorted would
        /// put the cost on the hot path instead.
        /// </summary>
        public static bool TryPercentiles(
            out int samples, out double p50, out double p95, out double p99, out double max,
            out double mean)
        {
            samples = _count;
            p50 = p95 = p99 = max = mean = 0.0;

            if (_count == 0)
            {
                return false;
            }

            var copy = new long[_count];
            Array.Copy(_samples, copy, _count);
            Array.Sort(copy);

            double perTick = 1000.0 / Stopwatch.Frequency;

            long total = 0L;

            for (int i = 0; i < copy.Length; i++)
            {
                total += copy[i];
            }

            mean = total * perTick / copy.Length;
            p50 = copy[Percentile(copy.Length, 0.50)] * perTick;
            p95 = copy[Percentile(copy.Length, 0.95)] * perTick;
            p99 = copy[Percentile(copy.Length, 0.99)] * perTick;
            max = copy[copy.Length - 1] * perTick;

            return true;
        }

        private static int Percentile(int length, double fraction)
        {
            int index = (int)(length * fraction);

            return index >= length ? length - 1 : index;
        }

        /// <summary>Seconds the current window has been open, for a rate the reader can check.</summary>
        public static double WindowSeconds
        {
            get
            {
                return _startedAt == 0L
                    ? 0.0
                    : (Stopwatch.GetTimestamp() - _startedAt) / (double)Stopwatch.Frequency;
            }
        }

        /// <summary>One clause, appended to Core.Process rather than given a check of its own.</summary>
        public static string Describe()
        {
            if (!Enabled)
            {
                return "loop sampler off";
            }

            int samples;
            double p50, p95, p99, max, mean;

            if (!TryPercentiles(out samples, out p50, out p95, out p99, out max, out mean))
            {
                return "loop sampler on, no samples yet";
            }

            return String.Format(
                CultureInfo.InvariantCulture,
                "loop period over {0} cycle(s) in {1:F0}s: {2:F3} ms mean, p50 {3:F3}, p95 {4:F3}, "
                + "p99 {5:F3}, max {6:F1}{7}",
                samples, WindowSeconds, mean, p50, p95, p99, max,
                _dropped > 0 ? String.Format(" ({0} dropped)", _dropped) : "");
        }
    }
}
