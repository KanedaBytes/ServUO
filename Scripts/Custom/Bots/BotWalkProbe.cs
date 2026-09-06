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
                Log.Warn("Walk probe already running; ignoring the second request.");
                return;
            }

            var bots = new List<PlayerBot>();

            try
            {
                _running = true;

                List<NavDestination> destinations = Nav.Destinations(map, null, null);

                if (destinations.Count < 2)
                {
                    Finish(bots, HealthResult.Warn(String.Format(
                        "skipped: {0} destination(s) on {1}, need at least 2 to converge and disperse",
                        destinations.Count,
                        map)));
                    return;
                }

                for (int i = 0; i < BotCount; i++)
                {
                    var bot = new PlayerBot();

                    bots.Add(bot);
                    bot.MoveToWorld(location, map);

                    // Traveler rather than Idle: this is the behaviour under test, and attaching
                    // it here means the probe exercises the real code path rather than a rig.
                    bot.Behavior = BotBehaviors.Create("Traveler");
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

                Timer.DelayCall(ConvergeWindow, () => Disperse(captured, destinations, target));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Walk probe threw during setup.");
                Finish(bots, HealthResult.Fail("threw during setup: " + ex.Message));
            }
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
                    "{0} teleport(s) - the ladder ran out on {1} live bot(s). {2}",
                    teleports,
                    alive,
                    rungs));
            }
            else if (stuck > 0)
            {
                result = HealthResult.Warn(String.Format(
                    "{0} of {1} bot(s) mid-recovery when the window closed ({2} travelling). {3}",
                    stuck,
                    alive,
                    travelling,
                    rungs));
            }
            else
            {
                result = HealthResult.Ok(String.Format(
                    "{0} bot(s) walked, none stuck, no teleports ({1} still travelling, which is normal). {2}",
                    alive,
                    travelling,
                    rungs));
            }

            Finish(bots, result);
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
                bot.Behavior = BotBehaviors.Create("Traveler");

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
