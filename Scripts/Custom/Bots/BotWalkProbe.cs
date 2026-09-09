using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Five travellers, deliberately made to get in each other's way.
    ///
    /// This exists to exercise the stuck-recovery ladder. A single walker recovers at rung 1
    /// (repath) essentially every time, because A* simply routes around one obstruction - which is
    /// why rungs 2 to 5 went unverified when the ladder shipped. Contention is what reaches them,
    /// so the probe manufactures it: all five are sent to ONE destination at once, so they compete
    /// for the same arrival tiles and stand in each other's paths, and are then dispersed.
    ///
    /// PASS is "nobody still stuck, and no Teleport rung fired". Rungs 2 to 4 firing is
    /// INFORMATION, not failure - a sidestep that worked is the ladder doing its job. The rung
    /// line is printed either way, because a run where nothing escalated is worth knowing about
    /// too: it means the contention did not bite and the deeper rungs are still unproven.
    ///
    /// Asynchronous by nature, so it reports through Bots.Travel when it lands rather than making
    /// the caller wait.
    /// </summary>
    public static class BotWalkProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>Bots in the probe. Five is the number that produces contention in a doorway.</summary>
        public const int BotCount = 5;

        /// <summary>How long the converge phase is given before the bots are dispersed.</summary>
        public static readonly TimeSpan ConvergeWindow = TimeSpan.FromSeconds(45.0);

        /// <summary>How long the disperse phase is given before the probe reports.</summary>
        public static readonly TimeSpan DisperseWindow = TimeSpan.FromSeconds(45.0);

        private static HealthResult _last;

        private static BotProbeClock _clock;
        private static Timer _watch;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.0);
        private static bool _running;

        /// <summary>
        /// Bots that took an arrival handoff but settled outside the arrival's own range.
        ///
        /// The shopper assertion, and the only one in the tree that checks a settled bot is ON its
        /// destination rather than somewhere near it. Arrivals became places for every kind of
        /// destination precisely so a bot could choose its tile inside that circle; a bot that
        /// committed to a shop and stopped six tiles short has taken the handoff without taking
        /// the place, which is the failure the range was meant to end and would otherwise look
        /// exactly like success.
        /// </summary>
        private static readonly List<string> _settledOff = new List<string>();

        /// <summary>
        /// Is this bot standing within its destination's arrival range, and say so if not.
        ///
        /// Measured against every arrival of the destination rather than the one it was sent to:
        /// the walker may shift to a free tile inside the range, and the stand-tile sweep may pick
        /// a different arrival's apron entirely. Either is correct behaviour; being outside all of
        /// them is not.
        /// </summary>
        private static void NoteSettled(PlayerBot bot)
        {
            // Each visit behaviour names its own destination; there is no shared interface for it,
            // and inventing one for a probe would be the probe reshaping the code it tests.
            var shopper = bot.Behavior as ShopperBehavior;
            var sitter = bot.Behavior as BankSitterBehavior;
            var crafter = bot.Behavior as CrafterBehavior;

            string destinationId = shopper != null ? shopper.DestinationId
                : sitter != null ? sitter.DestinationId
                : crafter != null ? crafter.DestinationId
                : null;

            if (String.IsNullOrEmpty(destinationId))
            {
                return;
            }

            NavDestination destination = Nav.Destination(destinationId);

            if (destination == null || destination.ArrivalList == null
                || destination.ArrivalList.Count == 0)
            {
                return;
            }

            // A BANK SITTER IS SUPPOSED TO STAND OFF ITS ARRIVAL, so it is judged by its own rule.
            //
            // PickScatteredHome deliberately puts a sitter up to ScatterRadius tiles from where it
            // arrived, and the comment there says exactly why: an uncontrolled BaseCreature cannot
            // walk through another mobile, so sitters parked ON the arrival points make those tiles
            // unreachable for the next bot - "a bank with a crowd floor" became "a traffic jam".
            // Measuring that scatter against the arrival's range would fail the crowd for working
            // as designed, which is how a probe teaches people to ignore it. Caught on this
            // assertion's first real run, by exactly that case.
            int slack = sitter != null ? BankSitterBehavior.ScatterRadius : 1;

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                // The apron of the circle, not only its inside: the stand-tile sweep is allowed
                // the ring just outside the authored range.
                if (bot.InRange(arrival.Location, arrival.Range + slack))
                {
                    return;
                }
            }

            _settledOff.Add(String.Format(
                "{0} settled at {1},{2} for '{3}', outside every arrival's range",
                bot.Name,
                bot.X,
                bot.Y,
                destinationId));
        }

        /// <summary>True while this probe is mid-run. Read by [BotSmoke to advance the chain.</summary>
        public static bool IsRunning
        {
            get { return _running; }
        }

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
                Log.Warn("Walk probe already running; ignoring the second request.");
                return;
            }

            var bots = new List<PlayerBot>();

            try
            {
                _running = true;
                _settledOff.Clear();

                List<NavDestination> destinations = Nav.Destinations(map, null, null);

                if (destinations.Count < 2)
                {
                    Finish(bots, HealthResult.Warn(String.Format(
                        "skipped: {0} destination(s) on {1}, need at least 2 to converge and disperse",
                        destinations.Count,
                        map)));
                    return;
                }

                // Residents of the town they spawn in, as the life probe's bots are. The
                // constructor rolls a home weighted by town size, and a bot that drew Trinsic
                // would disperse toward a Trinsic destination 1,100 tiles away - "still
                // travelling" when a 45-second disperse window closes, for doing exactly what a
                // resident does. The walk is under test here, not the home roll.
                string home = BotHomeTowns.Nearest(location, map);

                for (int i = 0; i < BotCount; i++)
                {
                    var bot = new PlayerBot();

                    bots.Add(bot);

                    if (home != null)
                    {
                        bot.HomeTown = home;
                    }

                    bot.MoveToWorld(location, map);

                    // Traveler rather than Idle: this is the behaviour under test, and attaching
                    // it here means the probe exercises the real code path rather than a rig.
                    bot.SetBehavior(BotBehaviors.Create("Traveler"), "probe setup");
                }

                // CONVERGE. One destination for all five, chosen for crowding rather than at
                // random: a bank is the busiest tile pattern the graph has, and it is where a real
                // bot crowd will form.
                NavDestination target = PickBusiest(destinations);

                Log.Info(
                    "Walk probe: converging {0} bot(s) on '{1}'.",
                    bots.Count,
                    target.Id);

                int sent = SendAll(bots, target);

                if (sent == 0)
                {
                    Finish(bots, HealthResult.Fail(String.Format(
                        "no bot could route to '{0}' at all", target.Id)));
                    return;
                }

                List<PlayerBot> captured = bots;

                // Measure one of them. The cadence question - does the bot step at the pace it was
                // given - has no probe of its own, and this is the one place five bots are known
                // to be walking with nobody watching. The lines go to the console as information;
                // they never change the verdict.
                AttachPace(bots);

                // The CONVERGE stage can end early - the contention it exists to manufacture has
                // happened once every bot has arrived. The DISPERSE stage that follows cannot, and
                // deliberately does not: "nobody got stuck" is an absence, and absence needs the
                // whole window to establish.
                _clock = new BotProbeClock(ConvergeWindow + DisperseWindow);

                _watch = Timer.DelayCall(PollInterval, PollInterval, 0, () =>
                {
                    if (!AllArrived(captured) && _clock.Elapsed < ConvergeWindow)
                    {
                        return;
                    }

                    if (_watch != null)
                    {
                        _watch.Stop();
                        _watch = null;
                    }

                    Disperse(captured, destinations, target);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Walk probe threw during setup.");
                Finish(bots, HealthResult.Fail("threw during setup: " + ex.Message));
            }
        }

        /// <summary>Has every bot finished its walk to the converge target?</summary>
        private static bool AllArrived(List<PlayerBot> bots)
        {
            foreach (PlayerBot bot in bots)
            {
                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                var traveler = bot.Behavior as TravelerBehavior;

                if (traveler != null && traveler.IsTravelling)
                {
                    return false;
                }
            }

            return true;
        }

        private static void Disperse(
            List<PlayerBot> bots, List<NavDestination> destinations, NavDestination avoid)
        {
            try
            {
                Log.Info("Walk probe: dispersing.");

                int index = 0;

                for (int i = 0; i < bots.Count; i++)
                {
                    PlayerBot bot = bots[i];

                    if (bot.Deleted)
                    {
                        continue;
                    }

                    // A different destination each, skipping the one they all just crowded.
                    NavDestination target = null;

                    for (int attempt = 0; attempt < destinations.Count; attempt++)
                    {
                        NavDestination candidate = destinations[(index++) % destinations.Count];

                        if (candidate != avoid)
                        {
                            target = candidate;
                            break;
                        }
                    }

                    if (target != null)
                    {
                        Send(bot, target);
                    }
                }

                Timer.DelayCall(DisperseWindow, () => Report(bots));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Walk probe threw while dispersing.");
                Finish(bots, HealthResult.Fail("threw while dispersing: " + ex.Message));
            }
        }

        private static void Report(List<PlayerBot> bots)
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
                var counts = new int[NavWalker.RungCount];
                int stuck = 0;
                int alive = 0;
                int travelling = 0;

                for (int i = 0; i < bots.Count; i++)
                {
                    PlayerBot bot = bots[i];

                    if (bot.Deleted)
                    {
                        continue;
                    }

                    alive++;

                    var traveler = bot.Behavior as TravelerBehavior;

                    if (traveler == null)
                    {
                        continue;
                    }

                    NavWalker walker = traveler.Walker;

                    if (walker == null)
                    {
                        continue;
                    }

                    for (int rung = 0; rung < NavWalker.RungCount; rung++)
                    {
                        counts[rung] += walker.RungsFired((StuckRung)rung);
                    }

                    if (traveler.IsTravelling)
                    {
                        travelling++;
                    }

                    // STUCK is the walker's own answer, not a guess from the outside.
                    //
                    // "Still walking when the window closed" is NOT stuck - it is the normal steady
                    // state, because a Traveler that arrives lingers and then departs again of its own
                    // accord. The first version of this probe used that test and reported five stuck
                    // bots that were simply getting on with it.
                    //
                    // A walker above rung None has timed out without getting closer and is mid-
                    // recovery, which is exactly the condition worth failing on.
                    if (walker.Active && walker.CurrentRung != StuckRung.None)
                    {
                        stuck++;
                    }
                }

                string rungs = String.Format(
                    "rungs: repath {0}, sidestep {1}, door {2}, skip {3}, teleport {4}",
                    counts[(int)StuckRung.Repath],
                    counts[(int)StuckRung.Sidestep],
                    counts[(int)StuckRung.Door],
                    counts[(int)StuckRung.SkipWaypoint],
                    counts[(int)StuckRung.Teleport]);

                int teleports = counts[(int)StuckRung.Teleport];

                HealthResult result;

                if (teleports > 0)
                {
                    // Same verdict as production: reaching the top rung means the ladder could not
                    // recover and a mobile was moved. The Warn naming the edge has already been
                    // logged by NavWalker itself.
                    result = HealthResult.Fail(String.Format(
                        "in {0} - {1} teleport(s), the ladder ran out on {2} live bot(s). {3}",
                        _clock.Describe(),
                        teleports,
                        alive,
                        rungs));
                }
                else if (_settledOff.Count > 0)
                {
                    // Above the stuck warning: a bot standing still in the wrong place reads as
                    // success everywhere else in this probe, so if it is not said here it is not
                    // said at all. A bot still mid-recovery is at least visibly unfinished.
                    result = HealthResult.Warn(String.Format(
                        "in {0} - {1} bot(s) took an arrival handoff but settled outside every "
                        + "arrival's range: {2}. {3}",
                        _clock.Describe(),
                        _settledOff.Count,
                        String.Join("; ", _settledOff.ToArray()),
                        rungs));
                }
                else if (stuck > 0)
                {
                    result = HealthResult.Warn(String.Format(
                        "in {0} - {1} of {2} bot(s) mid-recovery when the window closed ({3} travelling). {4}",
                        _clock.Describe(),
                        stuck,
                        alive,
                        travelling,
                        rungs));
                }
                else
                {
                    result = HealthResult.Ok(String.Format(
                        "in {0} - {1} bot(s) walked, none stuck, no teleports ({2} still travelling, "
                        + "which is normal). {3}",
                        _clock.Describe(),
                        alive,
                        travelling,
                        rungs));
                }

                ReportPace();

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

        /// <summary>The sampler on the first bot that set off, and whose it is.</summary>
        private static NavPaceSampler _pace;
        private static NavWalker _paceWalker;
        private static string _paceName;

        private static void AttachPace(List<PlayerBot> bots)
        {
            _pace = null;
            _paceWalker = null;
            _paceName = null;

            for (int i = 0; i < bots.Count; i++)
            {
                var traveler = bots[i].Behavior as TravelerBehavior;
                NavWalker walker = traveler == null ? null : traveler.Walker;

                if (walker == null || !walker.Active || walker.Sampler != null)
                {
                    continue;
                }

                _pace = new NavPaceSampler();
                _paceWalker = walker;
                _paceName = bots[i].Name;
                walker.Sampler = _pace;
                return;
            }
        }

        private static void ReportPace()
        {
            if (_pace == null)
            {
                return;
            }

            if (_paceWalker != null && _paceWalker.Sampler == _pace)
            {
                _paceWalker.Sampler = null;
            }

            List<string> lines = _pace.Report(NavWalker.TickInterval);

            for (int i = 0; i < lines.Count; i++)
            {
                Log.Info("Walk probe pace ({0}): {1}", _paceName, lines[i]);
            }

            _pace = null;
            _paceWalker = null;
        }

        private static NavDestination PickBusiest(List<NavDestination> destinations)
        {
            // A bank if there is one - it is the tile pattern a real crowd forms on. Otherwise
            // whatever the graph offers first, which is still a shared target for all five.
            for (int i = 0; i < destinations.Count; i++)
            {
                if (Insensitive.Equals(destinations[i].Type, "bank"))
                {
                    return destinations[i];
                }
            }

            return destinations[0];
        }

        private static int SendAll(List<PlayerBot> bots, NavDestination target)
        {
            int sent = 0;

            for (int i = 0; i < bots.Count; i++)
            {
                if (Send(bots[i], target))
                {
                    sent++;
                }
            }

            return sent;
        }

        /// <summary>
        /// Drive one bot at one destination, through the same Traveler the world uses.
        ///
        /// The probe does not build its own walker: the point is to test the behaviour, and a rig
        /// with its own walker would test the rig.
        /// </summary>
        private static bool Send(PlayerBot bot, NavDestination target)
        {
            if (bot == null || bot.Deleted)
            {
                return false;
            }

            var traveler = bot.Behavior as TravelerBehavior;

            if (traveler == null)
            {
                // The bot arrived somewhere and STAYED - it is a BankSitter or a Shopper now,
                // which is the arrival handoff working. For the probe's purposes that is a bot
                // that needs putting back on the road: without this the disperse phase silently
                // did nothing for every bot that had committed, and they read as stuck.
                //
                // BEFORE putting it back: is it standing where an arrival-as-a-place says it
                // should be? This is the only assertion in the tree that a shopper or a sitter
                // ends up ON its destination rather than merely near it, and it exists because
                // arrivals became places for every kind of destination. A bot that committed to a
                // shop and settled six tiles away has taken the handoff without taking the tile,
                // which is exactly the failure the range was meant to end.
                NoteSettled(bot);

                bot.SetBehavior(BotBehaviors.Create("Traveler"), "probe setup");

                traveler = bot.Behavior as TravelerBehavior;

                if (traveler == null)
                {
                    return false;
                }
            }

            return traveler.SendTo(bot, target);
        }

        /// <summary>
        /// Record, log, and tear down. Every exit path comes through here.
        ///
        /// Teardown order matters and mirrors ShopScheduleSystem.StopAll: detach the brain first,
        /// which nulls the walker's Arrived and stops it, so a delete cannot fire an arrival for a
        /// journey that never finished.
        /// </summary>
        private static void Finish(List<PlayerBot> bots, HealthResult result)
        {
            if (_watch != null)
            {
                _watch.Stop();
                _watch = null;
            }

            _last = result;
            _running = false;

            if (result.Status == HealthStatus.Ok)
            {
                Log.Info("Bot walk probe PASSED - {0}", result.Detail);
            }
            else if (result.Status == HealthStatus.Warn)
            {
                Log.Warn("Bot walk probe - {0}", result.Detail);
            }
            else
            {
                Log.Error("Bot walk probe FAILED - {0}", result.Detail);
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
