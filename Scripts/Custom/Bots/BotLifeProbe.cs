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

        /// <summary>
        /// Lifecycle passes a hand switch must survive untouched.
        ///
        /// Two, and the cadence below is what makes two SAFE to assert. The phase clamps installed
        /// for a probe run are 15-30 seconds and the roller runs every 5, so two passes is ten
        /// seconds - comfortably inside even the shortest phase a bot can roll. At the old
        /// ten-second cadence two passes was twenty seconds, past the fifteen-second floor, and a
        /// bot rolling on the second pass would have been entirely legal: the assertion would have
        /// failed a correct system often enough to stop meaning anything.
        /// </summary>
        public const int HandSwitchPasses = 2;

        private static HealthResult _last;
        private static bool _running;

        // The hand-switch assertion. A hand switch that is quietly rolled away seconds later is
        // indistinguishable from one that never happened, which is exactly what it was before
        // PhaseStartedAt was set - and nothing exercised that path, so it shipped broken.
        private static PlayerBot _switched;
        private static string _switchedTo;
        private static int _switchedAtPass;
        private static DateTime _switchedAt;
        private static bool _switchSampled;
        private static bool _switchHeld;
        private static string _switchNote;

        /// <summary>True while this probe is mid-run. Read by [BotSmoke to advance the chain.</summary>
        public static bool IsRunning
        {
            get { return _running; }
        }

        private static BotProbeClock _clock;
        private static Timer _watch;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5.0);

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
                // Five, not ten. The hand-switch assertion needs at least HandSwitchPasses passes
                // to fall inside the shortest phase clamp above, and at ten seconds it did not.
                // Asking the roller more often also helps the churn bar it was already tuned for.
                BotLifecycle.IntervalOverride = TimeSpan.FromSeconds(5.0);

                BotLifecycle.ResetTransitions();

                for (int i = 0; i < BotCount; i++)
                {
                    var bot = new PlayerBot();

                    bots.Add(bot);
                    bot.MoveToWorld(location, map);

                    bot.SetBehavior(BotBehaviors.Create("Traveler"), "probe setup");

                    // Backdate the phase so the first roll is not a full clamp away, and stagger
                    // it so twelve bots do not all transition on the same pass and hit the budget.
                    bot.PhaseStartedAt = CustomTime.Now - TimeSpan.FromSeconds(Utility.Random(20));
                }

                // ONE BOT IS HAND-SWITCHED, through the real [BotBehavior code path.
                //
                // Idle deliberately: it takes no visit window, so the phase clock is the only
                // thing protecting it, and the phase clock is precisely what the fault broke. A
                // behaviour with a visit window would be skipped by the roller outright and the
                // assertion would pass without testing anything.
                _switched = null;
                _switchSampled = false;
                _switchHeld = false;
                _switchNote = "never sampled";

                string switchMessage;

                if (BotCommands.TryHandSwitch(bots[0], "Idle", out switchMessage))
                {
                    _switched = bots[0];
                    _switchedTo = "Idle";
                    _switchedAtPass = BotLifecycle.PassCount;
                    _switchedAt = CustomTime.Now;
                }
                else
                {
                    _switchNote = "the switch was refused: " + switchMessage;
                }

                Log.Info(
                    "Life probe: {0} bot(s) on accelerated phases ({1:0}s window), {2} hand-switched to Idle.",
                    bots.Count,
                    Window.TotalSeconds,
                    _switched == null ? "nobody" : _switched.Name);

                List<PlayerBot> captured = bots;

                // Poll for the churn bar rather than sleeping the window out. The bar is the pass
                // condition the report already applies, so an early exit can only skip waiting - it
                // cannot pass a run the full report would fail.
                _clock = new BotProbeClock(Window);

                _watch = Timer.DelayCall(PollInterval, PollInterval, 0, () =>
                {
                    SampleHandSwitch();

                    // Both bars, not either. Churn can be satisfied inside twenty seconds, and an
                    // early exit on churn alone would end the run before the hand-switched bot had
                    // been looked at even once - which is how a probe grows an assertion that is
                    // never actually evaluated.
                    if ((!Churned(captured) || !_switchSampled) && !_clock.Expired)
                    {
                        return;
                    }

                    Report(captured, map);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Life probe threw during setup.");
                Finish(bots, HealthResult.Fail("threw during setup: " + ex.Message));
            }
        }

        /// <summary>
        /// Have enough bots visibly done something yet?
        ///
        /// The same ChurnPercent bar Report applies, asked early. Teleports and wedging are NOT
        /// tested here on purpose: they are things that can still go wrong later in the window, and
        /// exiting on the churn bar alone would stop watching for them. That is a deliberate
        /// trade - the probe is primarily a churn test - and it is why the timeout still exists.
        /// </summary>
        /// <summary>
        /// Did the hand-switched bot keep the brain it was given?
        ///
        /// Sampled once, the moment HandSwitchPasses lifecycle passes have run since the switch -
        /// not on a wall-clock delay, because the question is about the ROLLER and a wall-clock
        /// answer would really be about Custom.BotLifecycleSeconds.
        ///
        /// The fault this exists for: setting Behavior alone left PhaseStartedAt untouched, so the
        /// roller's next pass asked "is this bot's phase over?", got yes, and rolled the switch
        /// away within seconds. Every [BotBehavior appeared to work and then quietly undid itself,
        /// and nothing exercised the path.
        /// </summary>
        private static void SampleHandSwitch()
        {
            if (_switchSampled || _switched == null)
            {
                return;
            }

            if (_switched.Deleted)
            {
                _switchSampled = true;
                _switchHeld = false;
                _switchNote = "the hand-switched bot was deleted mid-run";
                return;
            }

            int passes = BotLifecycle.PassCount - _switchedAtPass;

            if (passes < HandSwitchPasses)
            {
                return;
            }

            string now = _switched.Behavior == null ? "nothing" : _switched.Behavior.SerializableName;

            _switchSampled = true;
            _switchHeld = Insensitive.Equals(now, _switchedTo);

            // The elapsed seconds are in the note on purpose. The first run of this assertion
            // failed reporting only "2 pass(es) later", which reads as a fault and was not one:
            // the passes were 36 seconds apart because IntervalOverride had not displaced the
            // cadence already in flight, and a 30-second phase had legitimately expired between
            // them. Without the clock in the message there was nothing to tell those two apart.
            double seconds = (CustomTime.Now - _switchedAt).TotalSeconds;

            _switchNote = _switchHeld
                ? String.Format(
                    "{0} held {1} through {2} lifecycle pass(es) over {3:0}s",
                    _switched.Name, _switchedTo, passes, seconds)
                : String.Format(
                    "{0} was switched to {1} by hand and the roller had it as {2} {3} pass(es) ({4:0}s) later",
                    _switched.Name, _switchedTo, now, passes, seconds);
        }

        private static bool Churned(List<PlayerBot> bots)
        {
            int alive = 0;
            int changed = 0;

            foreach (PlayerBot bot in bots)
            {
                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                alive++;

                if (bot.BehaviorChanges > 1)
                {
                    changed++;
                }
            }

            return alive > 0 && (changed * 100) / alive >= ChurnPercent;
        }

        private static void Report(List<PlayerBot> bots, Map map)
        {
            // WRAPPED SO Finish ALWAYS RUNS - this is a real leak, not a hypothetical one.
            //
            // Everything below runs before the single Finish at the end: walker and rung
            // accessors, BotCrowds.BelowFloor, list indexing, string formatting. A throw anywhere
            // in it used to skip Finish entirely, which left _running true for the life of the
            // process AND left BotLifecycle.Override and IntervalOverride installed - a shard
            // stuck on a ten-second lifecycle cadence and 15-30s phase clamps, silently, until
            // restart.
            //
            // That is exactly the failure BotLifecycle's own comment claims to prevent: "Held in
            // memory rather than written to bots.json so a probe that dies mid-run cannot leave
            // the shard permanently accelerated." The in-memory half was done; the always-restore
            // half was not.
            //
            // catch, not a bare finally: a logged failure becomes a red health check somebody
            // sees, where a rethrow into a Timer callback becomes a console line nobody reads.
            // The finally is belt to that brace, in case Finish itself throws.
            try
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
                            "{0} never changed behaviour ({1} as {2}, a {3})",
                            bot.Name,
                            bot.BehaviorChanges,
                            bot.Behavior == null ? "nothing" : bot.Behavior.SerializableName,
                            BotClassHelper.DisplayName(bot.Class));

                        // A bot that spent the window wedged had no chance to change: a Traveler
                        // declines to transition mid-walk, and it never finished the walk. That is the
                        // ladder's business, not the lifecycle's, so it is a note rather than a fault.
                        if (wedged)
                        {
                            notes.Add(line + ", but it was wedged the whole time");
                            continue;
                        }

                        // Nor has a bot stalled when it is three minutes into a four-minute walk.
                        //
                        // Step 7e put work sites out in the wilderness, and the round trip to one is
                        // longer than this probe's entire window - by design, because a mine is
                        // supposed to be a journey. A Miner that spent the window walking to one has
                        // done exactly what was asked of it and simply has not arrived yet.
                        //
                        // This is the SECOND time this probe's proxy for "did something" has had to be
                        // corrected, and in the same direction both times. It first asked the phase
                        // clock, which does not move on an arrival handoff, and reported busy bots as
                        // idle. Brain changes fixed that and are still the right measure for a bot
                        // living in town; they are the wrong one for a bot on a long errand, because
                        // the errand IS the behaviour.
                        string errand = LongErrand(traveler);

                        if (errand != null)
                        {
                            notes.Add(line + ", but it was walking to " + errand);
                            continue;
                        }

                        unchanged.Add(line);
                    }
                }

                // THE HAND SWITCH. A fault here is a real one: it means a behaviour given by hand
                // does not survive contact with the roller, which makes [BotBehavior useless for
                // watching any behaviour at all - and being unable to hold a bot still is how the
                // Gatherer walk-in faults stayed hidden for a session.
                if (!_switchSampled)
                {
                    problems.Add("the hand switch was never sampled - " + _switchNote);
                }
                else if (!_switchHeld)
                {
                    problems.Add("a hand switch did not survive the roller - " + _switchNote);
                }
                else
                {
                    notes.Add(_switchNote);
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

                string clock = _clock == null ? "?" : _clock.Describe();

                string summary = String.Format(
                    "in {0} - {1} bot(s), {2} brain change(s), {3} lifecycle transition(s) [{4}], "
                    + "{5} destination(s) below floor",
                    clock,
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
            catch (Exception ex)
            {
                Log.Error(ex, "Probe threw while reporting.");
                Finish(bots, HealthResult.Fail("threw while reporting: " + ex.Message));
            }
            finally
            {
                // Unconditional, and after the catch: whatever happened above, the shard
                // does not stay accelerated.
                BotLifecycle.Override = null;
                BotLifecycle.IntervalOverride = null;
            }
        }

        /// <summary>
        /// The name of the work site this Traveler is walking to, or null.
        ///
        /// Deliberately narrow: only a WORK destination counts, not any long walk. "Still walking"
        /// is the normal steady state for a Traveler and excusing it wholesale would blunt the
        /// probe to nothing - the walk probe learned that one already. Work sites are the specific
        /// thing this shard added that takes longer to reach than the probe runs for.
        /// </summary>
        private static string LongErrand(TravelerBehavior traveler)
        {
            if (traveler == null || !traveler.IsTravelling)
            {
                return null;
            }

            NavDestination destination = traveler.DestinationId == null
                ? null
                : Nav.Destination(traveler.DestinationId);

            if (destination == null || !BotWorkSites.IsWorkType(destination.Type))
            {
                return null;
            }

            return destination.Name;
        }

        private static void Finish(List<PlayerBot> bots, HealthResult result)
        {
            if (_watch != null)
            {
                _watch.Stop();
                _watch = null;
            }

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
