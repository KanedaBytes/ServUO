using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Twelve bots, an accelerated clock, and three minutes to prove they have lives.
    ///
    /// Production ships NO phase clamps, so a phase is whatever BotPersonality rolled - fifteen
    /// minutes to six hours - and nothing would transition inside a test. The probe installs short
    /// clamps and a fast cadence for its own run and takes them away afterwards. They live in
    /// memory on BotLifecycle rather than in bots.json, so a probe that dies mid-run cannot leave
    /// the shard permanently accelerated.
    ///
    /// Twelve rather than five, because the assertion is about churn - every bot changing behaviour
    /// at least once - and a dozen gives the bank floor something to compete with.
    /// </summary>
    public static class BotLifeProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const int BotCount = 12;

        /// <summary>How long the probe watches before reporting.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(180.0);

        /// <summary>
        /// What share of the fleet must visibly change behaviour for the run to pass.
        ///
        /// A FLEET threshold, not "every bot", and the difference is not laziness. The roll is
        /// weighted by personality over the behaviours that exist, and this session has exactly
        /// two lifecycle targets - Traveler and Idle - with IdleTendency deliberately scaled to a
        /// third of the others. A strongly travel-inclined bot re-picking Traveler six times in
        /// three minutes is not a fault; it is the weighting doing precisely what it says. With
        /// twelve bots, one or two such runs are expected, and an every-bot assertion would fail
        /// on a correct system often enough to stop meaning anything.
        ///
        /// It gets stricter on its own as behaviours are added: more roll targets means fewer
        /// same-behaviour re-picks. Revisit the number when the Adventurer lands.
        /// </summary>
        public const int ChurnPercent = 75;

        private static HealthResult _last;
        private static bool _running;

        public static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                return HealthResult.Ok("not run this boot (runs with [BotSmoke)");
            }

            return _last;
        }

        public static void Run(Map map, Point3D location)
        {
            if (_running)
            {
                Log.Warn("Life probe already running; ignoring the second request.");
                return;
            }

            var bots = new List<PlayerBot>();

            try
            {
                _running = true;

                // Short phases and a fast roller, for this run only.
                var accelerated = new BotLifeConfig();

                accelerated.Crowds = BotSystem.Store.Life.Crowds;
                accelerated.Handoff = BotSystem.Store.Life.Handoff;

                foreach (string name in BotBehaviors.Names())
                {
                    accelerated.Phases[name] = new BotPhaseClamp
                    {
                        MinSeconds = 15,
                        MaxSeconds = 30
                    };
                }

                BotLifecycle.Override = accelerated;

                // Short phases alone are not enough. At the production 60-second cadence a
                // three-minute window holds three passes, and with a per-pass budget most bots
                // never get asked at all - the first run of this probe read that as ten bots
                // refusing to live, when it was really the roller barely being invited to run.
                BotLifecycle.IntervalOverride = TimeSpan.FromSeconds(10.0);

                BotLifecycle.ResetTransitions();

                for (int i = 0; i < BotCount; i++)
                {
                    var bot = new PlayerBot();

                    bots.Add(bot);
                    bot.MoveToWorld(location, map);

                    bot.Behavior = BotBehaviors.Create("Traveler");

                    // Backdate the phase so the first roll is not a full clamp away, and stagger
                    // it so twelve bots do not all transition on the same pass and hit the budget.
                    bot.PhaseStartedAt = CustomTime.Now - TimeSpan.FromSeconds(Utility.Random(20));
                }

                Log.Info(
                    "Life probe: {0} bot(s) on accelerated phases ({1:0}s window).",
                    bots.Count,
                    Window.TotalSeconds);

                List<PlayerBot> captured = bots;

                Timer.DelayCall(Window, () => Report(captured, map));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Life probe threw during setup.");
                Finish(bots, HealthResult.Fail("threw during setup: " + ex.Message));
            }
        }

        private static void Report(List<PlayerBot> bots, Map map)
        {
            int alive = 0;
            int stuck = 0;
            int teleports = 0;

            // Two lists, because they mean different things. A bot that never changed while it was
            // FREE to is the lifecycle failing to reach it - the thing this probe exists to catch.
            // A bot that never changed while wedged mid-journey is the recovery ladder still
            // working on it, which is information, exactly as it is in the walk probe.
            var problems = new List<string>();
            var notes = new List<string>();
            var unchanged = new List<string>();

            for (int i = 0; i < bots.Count; i++)
            {
                PlayerBot bot = bots[i];

                if (bot.Deleted)
                {
                    continue;
                }

                alive++;

                // Stuck is the walker's own answer, not a guess from outside.
                var traveler = bot.Behavior as TravelerBehavior;
                bool wedged = false;

                if (traveler != null && traveler.Walker != null)
                {
                    teleports += traveler.Walker.RungsFired(StuckRung.Teleport);

                    if (traveler.Walker.Active && traveler.Walker.CurrentRung != StuckRung.None)
                    {
                        wedged = true;
                        stuck++;
                        notes.Add(String.Format(
                            "{0} mid-recovery at rung {1}", bot.Name, traveler.Walker.CurrentRung));
                    }
                }

                // Every bot must have DONE something.
                //
                // Counted as real brain changes, not as phase-clock age. The clock deliberately
                // does not move on an arrival handoff, so a bot that went Traveler -> BankSitter ->
                // Traveler has visibly changed behaviour twice while its phase clock never moved.
                // An earlier version of this probe asked the clock and reported ten such bots as
                // having "never left its first phase" while they were plainly getting on with it.
                if (bot.BehaviorChanges <= 1)
                {
                    string line = String.Format(
                        "{0} never changed behaviour ({1} as {2})",
                        bot.Name,
                        bot.BehaviorChanges,
                        bot.Behavior == null ? "nothing" : bot.Behavior.SerializableName);

                    // A bot that spent the window wedged had no chance to change: a Traveler
                    // declines to transition mid-walk, and it never finished the walk. That is the
                    // ladder's business, not the lifecycle's, so it is a note rather than a fault.
                    if (wedged)
                    {
                        notes.Add(line + ", but it was wedged the whole time");
                    }
                    else
                    {
                        unchanged.Add(line);
                    }
                }
            }

            List<string> belowFloor = BotCrowds.BelowFloor(map);

            string transitions = BotLifecycle.DescribeTransitions();

            int changes = 0;

            for (int i = 0; i < bots.Count; i++)
            {
                if (!bots[i].Deleted)
                {
                    changes += bots[i].BehaviorChanges;
                }
            }

            string summary = String.Format(
                "{0} bot(s), {1} brain change(s), {2} lifecycle transition(s) [{3}], "
                + "{4} destination(s) below floor",
                alive,
                changes,
                BotLifecycle.TotalTransitions,
                transitions,
                belowFloor.Count);

            foreach (string note in notes)
            {
                Log.Info("  {0}", note);
            }

            foreach (string line in unchanged)
            {
                Log.Info("  {0}", line);
            }

            HealthResult result;

            if (teleports > 0)
            {
                // The same verdict the walk probe gives, for the same reason: reaching the top rung
                // means the ladder ran out and a mobile was moved.
                result = HealthResult.Fail(String.Format(
                    "{0} teleport(s) - the recovery ladder ran out. {1}",
                    teleports,
                    summary));
            }
            else if (alive > 0 && (alive - unchanged.Count) * 100 < alive * ChurnPercent)
            {
                result = HealthResult.Fail(String.Format(
                    "only {0} of {1} bot(s) changed behaviour, below the {2}% the lifecycle should "
                    + "produce. First: {3}. {4}",
                    alive - unchanged.Count,
                    alive,
                    ChurnPercent,
                    unchanged[0],
                    summary));

                foreach (string line in unchanged)
                {
                    Log.Error("  {0}", line);
                }
            }
            else if (problems.Count > 0)
            {
                result = HealthResult.Fail(String.Format(
                    "{0} problem(s). First: {1}. {2}",
                    problems.Count,
                    problems[0],
                    summary));

                foreach (string problem in problems)
                {
                    Log.Error("  {0}", problem);
                }
            }
            else if (stuck > 0)
            {
                result = HealthResult.Warn(String.Format(
                    "{0} bot(s) mid-recovery when the window closed, no teleports. {1}",
                    stuck,
                    summary));
            }
            else if (belowFloor.Count > 0)
            {
                // A warning rather than a failure: the floor is a pull, not a guarantee, and on a
                // graph with one bank and twelve wandering bots it can legitimately be short at
                // any given instant.
                result = HealthResult.Warn(String.Format(
                    "the crowd floor is not met ({0}). {1}",
                    String.Join(", ", belowFloor.ToArray()),
                    summary));
            }
            else
            {
                result = HealthResult.Ok(summary);
            }

            Finish(bots, result);
        }

        private static void Finish(List<PlayerBot> bots, HealthResult result)
        {
            _last = result;
            _running = false;

            // Always, and before anything else can fail: leaving the shard accelerated would be a
            // far worse outcome than a failed probe.
            BotLifecycle.Override = null;
            BotLifecycle.IntervalOverride = null;

            if (result.Status == HealthStatus.Ok)
            {
                Log.Info("Bot life probe PASSED - {0}", result.Detail);
            }
            else if (result.Status == HealthStatus.Warn)
            {
                Log.Warn("Bot life probe - {0}", result.Detail);
            }
            else
            {
                Log.Error("Bot life probe FAILED - {0}", result.Detail);
            }

            if (bots == null)
            {
                return;
            }

            for (int i = 0; i < bots.Count; i++)
            {
                PlayerBot bot = bots[i];

                if (bot != null && !bot.Deleted)
                {
                    bot.Delete();
                }
            }
        }
    }
}
