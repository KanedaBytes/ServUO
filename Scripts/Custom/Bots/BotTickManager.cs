// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// The behaviour tick: one timer, every live bot's brain.
    ///
    /// It does not move anything. NavWalker has driven movement on its own shared timer since 4a
    /// and still does; this makes decisions and watches for stranded walkers.
    ///
    /// NOT PlayerBot.OnThink. That is where the party check lives, correctly, because an
    /// invitation can only arrive when somebody is standing next to the bot. But OnThink runs off
    /// the AI timer, and PlayerRangeSensitive stops that entirely when no player is in the sector
    /// (BaseAI.cs:3072-3082) - a bot would stop deciding the moment nobody was watching, which is
    /// the opposite of what a populated world needs. A shared timer is immune, which is the same
    /// reason NavWalker has one.
    /// </summary>
    public static class BotTickManager
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private static Timer _timer;

        private static readonly List<PlayerBot> _scratch = new List<PlayerBot>();

        private static int _plansLeft;

        // Counters, reset each tick, reported by Bots.Population. They are the difference between
        // "the bots are standing still" and knowing WHY they are standing still.
        private static int _noDestination;
        private static int _noRoute;
        private static int _abandoned;

        public static int NoDestinationLastTick { get; private set; }

        public static int NoRouteLastTick { get; private set; }

        /// <summary>Walks that ended without an arrival since boot. See the Traveler's watchdog.</summary>
        public static int AbandonedTotal { get; private set; }

        /// <summary>
        /// Gatherers that reached their walk-in deadline and left since boot.
        ///
        /// Counted since boot rather than per tick because it is rare and cumulative - one of
        /// these is a bad afternoon for one bot, a dozen is a site nobody can get into. Until
        /// now this failure incremented nothing at all and logged only at Debug, so a work site
        /// that no bot could enter was indistinguishable from one nobody had chosen.
        /// </summary>
        public static int GaveUpTotal { get; private set; }

        public static TimeSpan Interval
        {
            get { return TimeSpan.FromSeconds(Config.Get("Custom.BotTickSeconds", 2.0)); }
        }

        /// <summary>
        /// How many bots may plan a route in one tick.
        ///
        /// The budget is on PLANNING, not on ticking. Ticking a bot is a switch on an enum;
        /// picking a destination and building a route walks the graph. Everything else about a
        /// journey is already paid for by NavWalker's own timer.
        /// </summary>
        public static int PlansPerTick
        {
            get { return Config.Get("Custom.BotPlansPerTick", 8); }
        }

        /// <summary>
        /// IDEMPOTENT, BECAUSE IT IS CALLED TWICE AND RAN THE WHOLE BOT LAYER AT DOUBLE SPEED.
        ///
        /// `ScriptCompiler.Invoke("Initialize")` reflects over every type in every loaded assembly
        /// and calls every public static parameterless `Initialize` (ScriptCompiler.cs:87), so this
        /// one is called automatically - and `BotSystem.Initialize` ALSO calls it by name. The old
        /// body assigned `_timer` unconditionally, which overwrites the handle and leaves the first
        /// timer running: two repeating timers, both calling OnTick, for the life of the process.
        ///
        /// Measured on the shipped build before the guard: **60 passes in 60 seconds against a
        /// `Custom.BotTickSeconds` of 2.0**, where one timer gives 30. Corroborated independently
        /// by a [CoreSmoke reading "over 124 pass(es)" about 126 seconds after boot, where 63 was
        /// expected. Neither ordering rescues it - whichever call lands first, the second makes a
        /// second timer - so the guard has to be here rather than in the caller.
        ///
        /// What it cost is not the CPU, which is two hundredths of the budget either way. It is
        /// that EVERY cadence this layer measures in passes was doubled: the planning allowance is
        /// reset per pass, so `Custom.BotPlansPerTick` was really twice what it said, and any
        /// figure quoted "per pass" or per tick was against half the wall-clock interval it named.
        /// Elapsed-time deadlines - visit windows, phase clocks, sessions - are unaffected, because
        /// they compare CustomTime rather than counting passes.
        /// </summary>
        public static void Initialize()
        {
            _initCalls++;

            if (_timer != null)
            {
                return;
            }

            _timersStarted++;
            _timer = Timer.DelayCall(Interval, Interval, OnTick);
        }

        // ---- the guard on all of the above ----
        //
        // The defect this layer's numbers were all measured against was invisible: two timers,
        // both working perfectly, and every per-pass figure quoted at half the wall clock it
        // named. Nothing in the shard could have told anyone. The guard above makes it impossible
        // to start twice; these make it impossible for it to happen again in some other way and go
        // unnoticed - a second caller constructing its own Timer, a config change nobody applied,
        // a timer that quietly died.
        //
        // TWO FACTS, because either alone can be fooled. The counters answer "how many timers were
        // started", which catches the duplicate directly; the observed cadence answers "how often
        // does a pass actually happen", which catches anything at all that makes the answer differ
        // from Custom.BotTickSeconds - including a timer this class never made.

        private static int _initCalls;
        private static int _timersStarted;

        private static DateTime _cadenceSince;
        private static long _cadencePasses;

        /// <summary>
        /// How the behaviour ticker is actually running, against how it is configured.
        ///
        /// Registered as Bots.Cadence, so it reports through [CoreSmoke and health.json.
        ///
        /// The bands are wide at the bottom and tight at the top on purpose. A pass is SKIPPED
        /// while the world is saving or loading, so a shard that has just saved legitimately reads
        /// a little under its configured rate and a lower band that was tight would cry wolf after
        /// every autosave. There is no corresponding reason to be over: nothing skips a pass into
        /// existence, so anything meaningfully above the configured rate is a second ticker, which
        /// is the thing being guarded against and is a failure, not a warning. 1.5x rather than
        /// 2.0x so a doubled cadence is caught even if one of the two timers is stalling.
        ///
        /// Measured from the FIRST pass rather than from boot, and not counting that pass: n
        /// passes span n-1 intervals, and over a short window that off-by-one is worth more than
        /// the bands are.
        /// </summary>
        public static HealthResult BuildHealthResult()
        {
            if (_timersStarted != 1)
            {
                return HealthResult.Fail(String.Format(
                    "{0} behaviour timer(s) started from {1} Initialize call(s) - exactly one is "
                    + "correct, and two runs the whole bot layer at double cadence",
                    _timersStarted,
                    _initCalls));
            }

            if (_timer == null)
            {
                return HealthResult.Fail("the behaviour timer is gone - no bot has a brain");
            }

            double interval = Interval.TotalSeconds;

            if (interval <= 0.0)
            {
                return HealthResult.Fail("Custom.BotTickSeconds is " + interval);
            }

            if (_cadencePasses <= 0)
            {
                return HealthResult.Ok(String.Format(
                    "1 timer from {0} Initialize call(s); no pass measured yet", _initCalls));
            }

            double elapsed = (DateTime.UtcNow - _cadenceSince).TotalSeconds;
            double expected = elapsed / interval;

            string measured = String.Format(
                "1 timer from {0} Initialize call(s); {1} pass(es) in {2:0}s = {3:0.000}/s against "
                + "the configured {4:0.000}/s",
                _initCalls,
                _cadencePasses,
                elapsed,
                elapsed > 0.0 ? _cadencePasses / elapsed : 0.0,
                1.0 / interval);

            // Too little to say anything with. A handful of passes over a few seconds is noise,
            // and reporting a band breach from it would train everyone to ignore this line.
            if (elapsed < 60.0 || _cadencePasses < 10)
            {
                return HealthResult.Ok(measured + " (warming up)");
            }

            double ratio = _cadencePasses / expected;

            if (ratio >= 1.5 || ratio <= 0.5)
            {
                return HealthResult.Fail(String.Format(
                    "{0} - {1:0.00}x the configured rate. Over is a second ticker; under is a "
                    + "timer that is not running.",
                    measured,
                    ratio));
            }

            if (ratio > 1.2 || ratio < 0.8)
            {
                return HealthResult.Warn(String.Format(
                    "{0} - {1:0.00}x the configured rate", measured, ratio));
            }

            return HealthResult.Ok(String.Format("{0} ({1:0.00}x)", measured, ratio));
        }

        /// <summary>Called by a behaviour before it plans. False means "not this tick".</summary>
        public static bool TryTakePlan()
        {
            if (_plansLeft <= 0)
            {
                return false;
            }

            _plansLeft--;

            return true;
        }

        public static void NoteNoDestination()
        {
            _noDestination++;
        }

        public static void NoteNoRoute()
        {
            _noRoute++;
        }

        public static void NoteAbandoned()
        {
            _abandoned++;
            AbandonedTotal++;
        }

        public static void NoteGaveUp()
        {
            GaveUpTotal++;
        }

        private static void OnTick()
        {
            // The world is frozen during a save and half-built during a load; a brain that picked
            // a destination in either would be reasoning about a world that is not there yet.
            // LoopQueue and NavWalker.DriveAll both skip on the same condition.
            if (World.Loading || World.Saving)
            {
                return;
            }

            long startedAt = Core.TickCount;

            // The cadence window opens on the first pass and that pass is not counted: see
            // BuildHealthResult. Its own counter rather than _passes, which ResetCost() zeroes
            // whenever a probe wants a fresh cost reading - a guard that a probe can reset is a
            // guard that is off for as long as anybody is measuring.
            if (_cadenceSince == DateTime.MinValue)
            {
                _cadenceSince = DateTime.UtcNow;
            }
            else
            {
                _cadencePasses++;
            }

            _plansLeft = PlansPerTick;
            _noDestination = 0;
            _noRoute = 0;
            _abandoned = 0;

            // Snapshot first. A Tick can delete a bot (or spawn one), and LiveRegistry.Snapshot
            // already returns a pruned copy rather than the live list.
            _scratch.Clear();

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                // LiveRegistry IS NOT A BOT REGISTRY. It also holds DailyLifeTownsfolk,
                // DailyLifePatron, DailyLifeWatchman, the six GG* vendors and OldMarta - and
                // today those OUTNUMBER the bots. `as` and skip, never a cast: a hard cast would
                // throw on Perrin and take every bot's brain down with the tick.
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }

                _scratch.Add(bot);
            }

            int live = _scratch.Count;

            // The step census's denominator, taken BEFORE the brains run: the seconds just elapsed
            // belong to the phase each bot was holding during them, and a behaviour swapped by the
            // loop below would otherwise be credited with the interval it did not live through.
            // Rides this pass for BotLifecycle's reason - one scan of a registry that also holds
            // every daily-life actor, at another cadence.
            try
            {
                BotStepCensus.Observe(_scratch);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The step census pass faulted.");
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                PlayerBot bot = _scratch[i];

                // Re-check: an earlier bot's Tick may have deleted this one.
                if (bot.Deleted)
                {
                    continue;
                }

                PlayerBotBehavior behavior = bot.Behavior;

                if (behavior == null)
                {
                    continue;
                }

                try
                {
                    behavior.Tick(bot);
                }
                catch (Exception ex)
                {
                    // One faulting brain must not stop the others, and it must not be silent.
                    Log.Error(ex, "{0}'s {1} behaviour faulted.", bot.Name, behavior.SerializableName);
                }
            }

            // The lifecycle rides this same pass on its own slower accumulator. One scan, two
            // cadences: a second timer would mean a second walk of a registry that also holds
            // every daily-life actor.
            try
            {
                BotLifecycle.Pass(_scratch);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The bot lifecycle pass faulted.");
            }

            // And so does the session curve, for the same reason and at a third cadence. Upstream
            // gives it a timer of its own and sweeps World.Mobiles from it every 60 seconds
            // (uo-offline BotSessionManager.cs:134) - which is the walk CLAUDE.md section 15
            // exists to forbid, and the scan is already in hand here.
            try
            {
                BotSession.Pass(_scratch);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The bot session pass faulted.");
            }

            _scratch.Clear();

            NoDestinationLastTick = _noDestination;
            NoRouteLastTick = _noRoute;

            Measure(Core.TickCount - startedAt, live);
        }

        // ---- what the population costs, per tick ----
        //
        // This exists so a population target is raised on a number rather than on nerve. The whole
        // per-tick cost of the bot layer is this method: the LiveRegistry sweep, every behaviour's
        // Tick, the lifecycle pass and the session pass. Bots.Recipe and Bots.Population report the
        // mean and the max against the Custom.BotTickSeconds budget beside the live bot count, so
        // "61 bots, 8.4 ms mean / 19 ms max of 2000 ms" is the answer to "can we run more?".
        //
        // Upstream measures nothing and ships a target of 1600 behind a comment reading "with <500
        // bots this is trivially cheap" (BehaviorTickManager.cs) - two numbers that cannot both be
        // considered opinions about the same code.

        private static double _passMeanMs;
        private static long _passMaxMs;
        private static int _passes;
        private static int _passBots;

        /// <summary>Mean milliseconds per pass since boot, or since the last reset.</summary>
        public static double PassMeanMs
        {
            get { return _passMeanMs; }
        }

        /// <summary>The worst pass since boot, in milliseconds.</summary>
        public static long PassMaxMs
        {
            get { return _passMaxMs; }
        }

        /// <summary>How many bots the last measured pass covered.</summary>
        public static int PassBots
        {
            get { return _passBots; }
        }

        public static int PassCount
        {
            get { return _passes; }
        }

        /// <summary>Start the cost measurement again - the probe does this before it measures.</summary>
        public static void ResetCost()
        {
            _passMeanMs = 0.0;
            _passMaxMs = 0;
            _passes = 0;
        }

        /// <summary>
        /// A running mean rather than a stored series: the question is "what does a pass cost at
        /// this population", and a mean plus a max answers it in two numbers that cost nothing to
        /// keep. Wraparound-safe by construction - the caller subtracts two TickCounts.
        /// </summary>
        private static void Measure(long elapsedMs, int bots)
        {
            if (elapsedMs < 0)
            {
                return;
            }

            _passes++;
            _passBots = bots;
            _passMeanMs += (elapsedMs - _passMeanMs) / _passes;

            if (elapsedMs > _passMaxMs)
            {
                _passMaxMs = elapsedMs;
            }
        }

        /// <summary>Mean and max against the configured tick budget, for a health line.</summary>
        public static string DescribeCost()
        {
            return String.Format(
                "tick {0:0.0} ms mean / {1} ms max of {2:0} ms over {3} pass(es) at {4} bot(s)",
                _passMeanMs,
                _passMaxMs,
                Interval.TotalMilliseconds,
                _passes,
                _passBots);
        }
    }
}
