using System;

namespace Server.Custom
{
    /// <summary>
    /// The shard clock, read once per millisecond instead of once per call.
    ///
    /// ModernUO has <c>Core.Now</c> — a DateTime the engine refreshes once per game-loop
    /// iteration, so a system that consults the time in a hot path pays for one read rather than
    /// thousands. ServUO has no equivalent (CLAUDE.md section 14), and the ported bot code reads
    /// the clock at 387 sites. Rewriting those to <c>DateTime.UtcNow</c> would put a syscall
    /// behind every one of them.
    ///
    /// This caches on <see cref="Core.TickCount"/> — the engine's monotonic millisecond counter —
    /// so the underlying <c>DateTime.UtcNow</c> is read at most once per millisecond however many
    /// callers ask. Staleness is bounded by one millisecond, which is far below the resolution
    /// anything here cares about: the shortest interval in the bot layer is a 2-second behaviour
    /// tick.
    ///
    /// Caching on TickCount rather than driving it from a repeating Timer is deliberate. A timer
    /// would stop during <c>World.Loading</c> and <c>World.Saving</c> (the Timer Thread idles
    /// while the world is frozen), and a save that takes two seconds would hand every caller a
    /// two-second-old timestamp afterwards. Reading lazily cannot go stale that way, and there is
    /// no timer to register, leak or forget to start.
    ///
    /// GAME THREAD ONLY. The two fields are written without synchronisation because every caller
    /// is on the game thread; a background thread must marshal through
    /// <see cref="LoopQueue.Post"/> like anything else that touches shard state. A torn read here
    /// would be harmless (a stale-by-one-millisecond DateTime) but the discipline is the same one
    /// the rest of Custom/ follows.
    /// </summary>
    public static class CustomTime
    {
        // Int64.MinValue rather than 0: Core.TickCount is Stopwatch-based and starts near zero, so
        // a zero sentinel would skip the first refresh and hand out default(DateTime).
        private static long _tick = Int64.MinValue;
        private static DateTime _now;

        /// <summary>
        /// UTC now, to the millisecond. The direct replacement for ModernUO's <c>Core.Now</c>.
        /// </summary>
        public static DateTime Now
        {
            get
            {
                long tick = Core.TickCount;

                if (tick != _tick)
                {
                    _tick = tick;
                    _now = DateTime.UtcNow;
                }

                return _now;
            }
        }
    }
}
