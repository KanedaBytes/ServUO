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

        public static void Initialize()
        {
            _timer = Timer.DelayCall(Interval, Interval, OnTick);
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

            _scratch.Clear();

            NoDestinationLastTick = _noDestination;
            NoRouteLastTick = _noRoute;
        }
    }
}
