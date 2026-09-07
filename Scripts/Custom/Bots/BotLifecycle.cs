// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotLifecycle.cs — when a bot's phase runs out, it decides to do something
// else.
//
// This is the difference between a bot with a schedule and a bot with a life.
// Without it a Traveler travels for ever; with it, a cautious bot drifts toward
// banks, a restless one keeps moving, and a homebody settles.
//
// It runs INSIDE BotTickManager's existing pass over LiveRegistry, on its own
// slower accumulator. One scan, two cadences: a second timer would mean a second
// walk of a registry that also holds every daily-life actor.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class BotLifecycle
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// The behaviours a lifecycle roll may choose, paired with the personality weight that
        /// wants them.
        ///
        /// BankSitter and Shopper are deliberately NOT here. They are places, not dispositions -
        /// a bot becomes one by ARRIVING somewhere, through the Traveler's handoff. Rolling one
        /// out of nowhere would teleport the concept: a bank sitter sitting in a field.
        ///
        /// AdventurerTendency has no entry because no Adventurer behaviour exists yet. The roll
        /// renormalises over what is actually registered, so that weight starts meaning something
        /// the session it lands rather than needing a migration.
        /// </summary>
        private static readonly string[] RollTargets = { "Traveler", "Idle" };

        /// <summary>Transitions allowed per pass, so a fleet never re-brains itself all at once.</summary>
        public const int MaxTransitionsPerPass = 5;

        /// <summary>
        /// The floor every weight is raised to before the current-behaviour discount.
        ///
        /// Order matters and is upstream's: floor FIRST, then halve. A bot with zero tendency for
        /// what it is already doing ends on half the floor rather than on nothing, so it can still
        /// be picked. Reversing the two would silently change the distribution.
        /// </summary>
        private const double WeightFloor = 0.01;

        /// <summary>Whether the roller is running. [BotLifecycle on|off.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Transitions since boot, by behaviour name. The probe prints these.</summary>
        private static readonly Dictionary<string, int> _transitions =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static long _nextPass;

        /// <summary>
        /// A faster cadence for the probe's run, or null for the configured one.
        ///
        /// Short phase clamps alone are not enough to make the lifecycle testable: at the
        /// production 60-second cadence a three-minute window contains three passes, and with a
        /// per-pass transition budget most bots never get asked. The probe shortens both.
        /// </summary>
        /// <remarks>
        /// Setting this brings the NEXT pass forward, rather than letting the cadence already in
        /// flight run its course first.
        ///
        /// Without that, installing a five-second override does nothing for up to a minute,
        /// because _nextPass is still holding the deadline the previous sixty-second cadence set.
        /// A probe that then counts passes is counting something unbounded: the life probe's
        /// hand-switch assertion sampled at "two passes" and got a bot thirty-six seconds older
        /// than it expected, past a thirty-second phase, and reported a correct roll as a fault.
        /// An override that does not take effect until the thing it is overriding has finished is
        /// not really an override.
        /// </remarks>
        public static TimeSpan? IntervalOverride
        {
            get { return _intervalOverride; }
            set
            {
                _intervalOverride = value;

                if (value != null)
                {
                    _nextPass = Core.TickCount + (long)value.Value.TotalMilliseconds;
                }
            }
        }

        private static TimeSpan? _intervalOverride;

        public static TimeSpan Interval
        {
            get
            {
                return IntervalOverride
                    ?? TimeSpan.FromSeconds(Config.Get("Custom.BotLifecycleSeconds", 60.0));
            }
        }

        /// <summary>
        /// A temporary override on the lifecycle's pace, for the probe.
        ///
        /// Production ships no phase clamps at all, so a phase is the bot's own 15-360 minutes and
        /// nothing would transition inside a three-minute test. The probe installs short clamps and
        /// a fast cadence for its run and clears them afterwards. Held in memory rather than
        /// written to bots.json so a probe that dies mid-run cannot leave the shard permanently
        /// accelerated.
        /// </summary>
        public static BotLifeConfig Override { get; set; }

        /// <summary>The config actually in force: the probe's override, else the loaded one.</summary>
        public static BotLifeConfig Config_
        {
            get { return Override ?? BotSystem.Store.Life; }
        }

        public static IDictionary<string, int> Transitions
        {
            get { return _transitions; }
        }

        public static int TotalTransitions
        {
            get
            {
                int total = 0;

                foreach (int count in _transitions.Values)
                {
                    total += count;
                }

                return total;
            }
        }

        /// <summary>
        /// Passes that have actually RUN since boot - not calls to Pass, which is invited on every
        /// bot tick and returns immediately until its own cadence is due.
        ///
        /// Counted because "did the roller leave this bot alone?" has to be asked in passes rather
        /// than in seconds. A probe asserting over a wall-clock window is really asserting about
        /// Custom.BotLifecycleSeconds, and would start passing for the wrong reason the moment
        /// somebody slowed the cadence down.
        /// </summary>
        public static int PassCount { get; private set; }

        public static void ResetTransitions()
        {
            _transitions.Clear();
        }

        /// <summary>"Traveler 14, Idle 3" - the transition tally, for the probe and [BotInfo.</summary>
        public static string DescribeTransitions()
        {
            if (_transitions.Count == 0)
            {
                return "none";
            }

            var parts = new List<string>();

            foreach (var entry in _transitions)
            {
                parts.Add(String.Format("{0} {1}", entry.Key, entry.Value));
            }

            parts.Sort(StringComparer.Ordinal);

            return String.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Called once per behaviour tick with the bots already gathered. Returns immediately
        /// unless its own interval has come round.
        /// </summary>
        public static void Pass(IList<PlayerBot> bots)
        {
            if (!Enabled || bots == null)
            {
                return;
            }

            // Wraparound-safe: compare by subtraction, never a < b (CLAUDE.md section 15).
            if (Core.TickCount - _nextPass < 0)
            {
                return;
            }

            _nextPass = Core.TickCount + (long)Interval.TotalMilliseconds;

            PassCount++;

            int transitions = 0;

            for (int i = 0; i < bots.Count; i++)
            {
                PlayerBot bot = bots[i];

                // Re-check: an earlier transition in this same pass may have deleted it.
                if (bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }

                // First sight. Give it a personality and let it serve a full first phase rather
                // than transitioning the instant it was born.
                if (!bot.Personality.IsAssigned)
                {
                    bot.Personality = BotPersonality.RollRandom();
                    bot.PhaseStartedAt = CustomTime.Now;

                    Log.Debug("{0} assigned a personality: {1}", bot.Name, bot.Personality);
                    continue;
                }

                PlayerBotBehavior behaviour = bot.Behavior;

                if (behaviour == null)
                {
                    continue;
                }

                // A visit ends on its own clock, not this one. Interrupting a bot mid-shop would
                // make the visit machinery pointless and the bot look like it changed its mind for
                // no reason.
                if (behaviour.VisitExpiresAt != null)
                {
                    continue;
                }

                TimeSpan phase = Config_.PhaseLength(
                    behaviour.SerializableName, bot.Personality.AveragePhaseDuration);

                bool due = bot.TransitionPending
                    || CustomTime.Now - bot.PhaseStartedAt >= phase;

                if (!due)
                {
                    continue;
                }

                // The behaviour may decline - a Traveler halfway down a street says no. REMEMBER
                // that it was due rather than dropping it: on a graph with long routes a Traveler
                // is mid-walk most of the time, and a roller that simply skipped a busy bot would
                // pass over the same bot for ever and never move it at all.
                if (!behaviour.CanTransition(bot))
                {
                    bot.TransitionPending = true;
                    continue;
                }

                bot.TransitionPending = false;

                if (transitions >= MaxTransitionsPerPass)
                {
                    continue;
                }

                try
                {
                    if (Transition(bot, behaviour.SerializableName))
                    {
                        transitions++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Lifecycle transition failed for {0}.", bot.Name);
                }
            }
        }

        private static bool Transition(PlayerBot bot, string current)
        {
            string target = PickNext(bot, current);

            // The phase clock restarts either way. Re-picking what it is already doing is a
            // decision too - "I want to keep doing this" - and it should buy a fresh phase rather
            // than being re-asked every pass.
            bot.PhaseStartedAt = CustomTime.Now;

            if (Insensitive.Equals(target, current))
            {
                Log.Debug("{0} continues as {1}.", bot.Name, current);
                return false;
            }

            bot.SetBehavior(BotBehaviors.Create(target), "lifecycle roll");

            int count;
            _transitions.TryGetValue(target, out count);
            _transitions[target] = count + 1;

            Log.Debug("{0}: {1} -> {2}.", bot.Name, current, target);

            return true;
        }

        /// <summary>
        /// Weighted roll over the registered lifecycle behaviours, biased toward change.
        /// </summary>
        private static string PickNext(PlayerBot bot, string current)
        {
            BotPersonality personality = bot.Personality;

            var weights = new double[RollTargets.Length];
            double total = 0.0;

            for (int i = 0; i < RollTargets.Length; i++)
            {
                double weight = TendencyFor(personality, RollTargets[i]);

                // Floor first...
                if (weight < WeightFloor)
                {
                    weight = WeightFloor;
                }

                // ...then discount what it is already doing, so the roll leans toward change.
                if (Insensitive.Equals(RollTargets[i], current))
                {
                    weight *= 0.5;
                }

                weights[i] = weight;
                total += weight;
            }

            if (total <= 0.0)
            {
                // Unreachable with the floor in place, and handled anyway: a bot with no brain at
                // all is worse than a bot standing still, and a crash here would take the pass down
                // with it.
                Log.Warn(
                    "{0} has no usable behaviour weights; falling back to Idle.",
                    bot.Name);

                return "Idle";
            }

            double roll = Utility.RandomDouble() * total;

            for (int i = 0; i < RollTargets.Length; i++)
            {
                roll -= weights[i];

                if (roll <= 0.0)
                {
                    return RollTargets[i];
                }
            }

            return RollTargets[RollTargets.Length - 1];
        }

        private static double TendencyFor(BotPersonality personality, string behaviour)
        {
            if (Insensitive.Equals(behaviour, "Traveler"))
            {
                return personality.TravelerTendency;
            }

            if (Insensitive.Equals(behaviour, "Idle"))
            {
                return personality.IdleTendency;
            }

            // A registered roll target with no tendency behind it yet. Neutral rather than zero:
            // it should be reachable, just not preferred.
            return 0.5;
        }
    }
}
