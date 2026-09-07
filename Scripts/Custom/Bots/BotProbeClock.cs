// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotProbeClock.cs — how long a probe took, against how long it was allowed.
//
// Every probe here used to sleep its whole window through a one-shot
// Timer.DelayCall(Window, Report), whether or not the thing it was testing had
// finished in the first thirty seconds. Measured across three consecutive
// [BotSmoke chains, each one ran its configured window to within two seconds:
//
//   walk  45+45s  ->  90 / 92 / 92
//   life  180s    -> 181 / 183 / 181
//   chat  60+30s  ->  92 / 92 / 90
//   work  420s    -> 424 / 426 / 423
//
// 789 seconds of window per chain, of which the work probe is 54% - and in the
// run that passed, the smith had crafted its item about two minutes in.
//
// So probes poll now, and report the moment their assertion is satisfied; the
// window became a TIMEOUT rather than a sleep. A knob to divide the world's
// clocks - NavWalker's move delay, the harvest swing interval, the craft delay -
// was considered and rejected: it would have saved nothing on its own, because
// the window was the cost rather than the work, and it would have made the
// probes test timings the shard does not run. Production timings are the point.
//
// This class is the shared half: the start tick, the timeout, and one
// Describe() so five probes cannot drift into five different time formats. It
// is also why every report now carries an elapsed-of-timeout clause - a probe
// that passes in 2m04s and later passes in 6m50s has regressed, and the old
// reports could not show it.

using System;

namespace Server.Custom
{
    public sealed class BotProbeClock
    {
        private readonly long _started;
        private readonly TimeSpan _timeout;

        public BotProbeClock(TimeSpan timeout)
        {
            _started = Core.TickCount;
            _timeout = timeout;
        }

        public TimeSpan Timeout
        {
            get { return _timeout; }
        }

        public TimeSpan Elapsed
        {
            get { return TimeSpan.FromMilliseconds(Core.TickCount - _started); }
        }

        /// <summary>Wraparound-safe, per CLAUDE.md section 15: compared by subtraction.</summary>
        public bool Expired
        {
            get { return Core.TickCount - (_started + (long)_timeout.TotalMilliseconds) >= 0; }
        }

        /// <summary>"2m04s of 7m00s" — the clause every probe report ends with.</summary>
        public string Describe()
        {
            return String.Format("{0} of {1}", Format(Elapsed), Format(_timeout));
        }

        public static string Format(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            int total = (int)span.TotalSeconds;

            return total < 60
                ? String.Format("{0}s", total)
                : String.Format("{0}m{1:00}s", total / 60, total % 60);
        }
    }
}
