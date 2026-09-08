// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotWorkProbe.cs — one full working-class cycle, end to end. Reports as Bots.Shift.
//
// The assertion is the whole loop, not a piece of it: a Miner goes out, mines
// real ore off a real rock face, walks the load back to town, hands it to a
// Smith, and the Smith turns it into at least one crafted item. Every one of
// those steps is somewhere the session could be quietly broken while every
// individual part looks fine — a site with no ore, a corridor that does not
// route, a delivery that matches the wrong trade, a forge the bot cannot reach
// from its arrival tile — and none of them would show up in a unit check.
//
// It runs on an ACCELERATED CLAMP, installed in memory only, exactly as
// BotLifeProbe installs its phase clamps: a real shift is four to eight minutes
// out and a real crafter settles in for three to six hours, which is correct for
// a shard and useless for a probe.
//
// The Smith is placed AT the forge rather than walked there. That half of the
// journey is the Traveler's, which Bots.Travel already proves; what this probe
// is for is the work and the hand-over, and spending two minutes of the window
// on a walk that is covered elsewhere would only make it flakier.

using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    public static class BotWorkProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How long the whole cycle gets.
        ///
        /// Generous on purpose: it contains a real walk out of the mountains and back through
        /// town, which is a couple of hundred tiles of NavWalker. A probe that failed on a slow
        /// walk would be reporting the wrong thing.
        /// </summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(420.0);

        /// <summary>
        /// The shift the miner works before it shoulders the load.
        ///
        /// Long enough to bring home a USABLE load, which is a higher bar than bringing home any
        /// load at all. Mining yields one ore per swing against a four-second cadence, and the
        /// cheapest thing a smith makes costs three ingots - so a thirty-second shift delivered two
        /// ore and the smith then failed thirty-five crafts in a row for want of a third.
        /// </summary>
        public static readonly TimeSpan Shift = TimeSpan.FromSeconds(120.0);

        private static HealthResult _last;
        private static bool _running;

        /// <summary>True while this probe is mid-run. Read by [BotSmoke to advance the chain.</summary>
        public static bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>How long this run has taken, against its timeout. Reported either way.</summary>
        private static BotProbeClock _clock;

        /// <summary>The poll that watches for the assertion; stopped by Finish.</summary>
        private static Timer _watch;

        /// <summary>How often to ask whether the cycle has finished.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.0);

        /// <summary>Set by the clock-in check: was the miner inside its site's zone and working?</summary>
        private static bool _clockedInZone;

        /// <summary>
        /// The gatherer that had to WALK to the site, kept so its counters survive the handoff.
        ///
        /// Held as the behaviour rather than the bot because the bot's brain is replaced when the
        /// shift ends, and Swings lives on the brain. The detached instance keeps its counts,
        /// which is exactly what the assertion needs to read afterwards.
        /// </summary>
        private static GathererBehavior _walkIn;

        private static string _clockInNote;

        /// <summary>
        /// A flag of its own, so this probe can be run without the six minutes of walk, lifecycle
        /// and chat probes that precede it in [BotSmoke.
        ///
        /// The same reason Custom.NavAuditOnStart exists: ServUO's console cannot invoke a staff
        /// command, so a headless run needs a switch. It spawns real mobiles, so like the rest it
        /// waits for ServerStarted rather than firing during Initialize.
        /// </summary>
        public static void Initialize()
        {
            if (!Config.Get("Custom.BotWorkProbeOnStart", false))
            {
                return;
            }

            EventSink.ServerStarted += () =>
                Timer.DelayCall(TimeSpan.FromSeconds(5.0), () => Run(Map.Trammel, new Point3D(1434, 1690, 0)));
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
                Log.Warn("Work probe already running; ignoring the second request.");
                return;
            }

            var bots = new List<PlayerBot>();

            try
            {
                _running = true;

                // Cleared per run, not just per boot: a second [BotSmoke in one session would
                // otherwise assert against the previous run's walking miner and pass on its
                // swings.
                _walkIn = null;

                NavDestination site = FirstUsable(map, new BotStation("mine", null));

                // The forge NEAREST THE MINE BY ROAD, not the first one in the file. With one
                // forge on the graph the two were the same thing; with four, file order chose a
                // bench and the miner's roll chose another, and the probe asserted on whichever
                // the dice preferred. The smith goes where a laden miner will most want to go,
                // and the assertion below is that the ore reached THAT smith.
                NavDestination forge = site == null
                    ? null
                    : NearestByRoad(site, BotWorkSites.Available(map, BotClassHelper.StationFor(BotClass.Smith)));

                if (site == null || forge == null)
                {
                    Finish(bots, HealthResult.Fail(String.Format(
                        "cannot run: {0}",
                        site == null ? "no usable mine on the graph" : "no usable forge on the graph")));
                    return;
                }

                // Where the walking miner starts. The bank by preference, because it is the place
                // a bot genuinely rolls into and the far end of a real commute; the invoker's own
                // tile if this shard has no bank on the graph, which still tests the walk-in.
                NavDestination town = Nav.NearestDestination(location, map, "bank", Int32.MaxValue);
                Point3D start = town == null ? location : town.Location;

                // The smith first, and standing at the forge, so it is already working when the
                // ore arrives.
                var smith = new PlayerBot(BotClass.Smith, BotSkillTier.Grandmaster);
                bots.Add(smith);

                // STRIP ITS STARTER INGOTS. EquipmentTable seeds a fresh smith with 40-90 of
                // them, which is right for a shard and fatal for this assertion: a smith that
                // could already work would make something whether or not the delivery ever
                // arrived, and the probe would pass on a broken hand-over. Dry, the only ingots
                // it can ever have are the ones the miner brings.
                //
                // Deleting the items rather than calling ConsumeTotal, and that is not a style
                // preference: ConsumeTotal is all-or-nothing, so asking it for Int32.MaxValue
                // ingots consumes NOTHING and returns false. The first version of this probe did
                // exactly that, left the smith fully stocked, and duly reported ten items made
                // from ore that had nothing to do with them.
                StripStock(smith);

                Point3D bench;

                if (!Nav.TryPickArrival(forge.Id, smith, out bench))
                {
                    bench = forge.Location;
                }

                smith.MoveToWorld(bench, map);

                var crafter = new CrafterBehavior();
                crafter.DestinationId = forge.Id;

                // Comfortably PAST the report, not level with it. Setting the visit to exactly
                // Window is a race the behaviour tick wins about half the time: CheckVisitExpired
                // swaps the smith back to a Traveler moments before Report looks at it, and the
                // probe reports "the smith stopped being a Crafter mid-probe" about its own clock.
                crafter.VisitExpiresAt = CustomTime.Now + Window + TimeSpan.FromMinutes(2.0);
                smith.SetBehavior(crafter, "probe setup");

                // The miner starts at the site, works a short shift, and then walks the load in
                // under its own steam - the real EndShift path, not a rigged one.
                var miner = new PlayerBot(BotClass.Miner, BotSkillTier.Grandmaster);
                bots.Add(miner);

                Point3D face;

                if (!Nav.TryPickArrival(site.Id, miner, out face))
                {
                    face = site.Location;
                }

                // STRIP ITS STARTER ORE - the assertion this probe was missing.
                //
                // EquipmentTable spawns every Miner with 3-15 IronOre as "a working stash from the
                // last shift". Left in place, the bot hauls its spawn kit to the forge, the smith
                // smelts it and crafts, and the probe reports a full working cycle having mined
                // precisely nothing. That is exactly how it passed against sites with no rock on
                // them. Dry, the only ore that can reach the smith is ore this miner dug.
                StripYield(miner);

                miner.MoveToWorld(face, map);

                var gatherer = new GathererBehavior();
                gatherer.DestinationId = site.Id;
                gatherer.VisitExpiresAt = CustomTime.Now + Shift;
                miner.SetBehavior(gatherer, "probe setup");

                // A SECOND MINER, SPAWNED AT THE BANK, which is the case the probe never covered.
                //
                // The one above is placed directly ON a picked arrival tile, so it clocks in on
                // its first tick and never exercises the walk-in at all. Everything that went
                // wrong with the walk-in - a hand switch that left DestinationId null, a clock-in
                // test weaker than the one that keeps a bot clocked in, and a give-up clock
                // shorter than the walker's own recovery ladder - lived entirely in the path this
                // probe could not reach. A miner that starts in town has to walk out, get inside
                // the zone, find rock and swing before any of it counts.
                var walker = new PlayerBot(BotClass.Miner, BotSkillTier.Grandmaster);
                bots.Add(walker);

                StripYield(walker);
                walker.MoveToWorld(start, map);

                _walkIn = new GathererBehavior();
                _walkIn.DestinationId = site.Id;
                _walkIn.VisitExpiresAt = CustomTime.Now + Window;
                walker.SetBehavior(_walkIn, "probe setup");

                Log.Info(
                    "Work probe: a Miner at '{0}', a Miner walking there from {1},{2}, and a Smith at '{3}', {4:0}s window.",
                    site.Id,
                    start.X,
                    start.Y,
                    forge.Id,
                    Window.TotalSeconds);

                List<PlayerBot> captured = bots;
                int madeBefore = crafter.Made;
                int minedBefore = BotWorkSites.Mined;

                _clockedInZone = false;
                _clockInNote = "never checked";

                // Sampled early, while the shift is still running: by the time Report fires the
                // gatherer has become a Traveler and there is nothing left to ask.
                Timer.DelayCall(TimeSpan.FromSeconds(12.0), () => CheckClockIn(miner, site));

                // POLL, do not sleep. The window is a timeout now: the moment the miner has
                // mined, hauled and handed over and the smith has made something, there is nothing
                // left to learn by waiting, and the run that first passed had done all of it about
                // two minutes into a seven-minute window.
                _clock = new BotProbeClock(Window);

                _watch = Timer.DelayCall(PollInterval, PollInterval, 0, () =>
                {
                    bool done = Satisfied(captured, madeBefore, minedBefore);

                    if (!done && !_clock.Expired)
                    {
                        return;
                    }

                    Report(captured, madeBefore, minedBefore);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Work probe threw during setup.");
                Finish(bots, HealthResult.Fail("threw during setup: " + ex.Message));
            }
        }

        /// <summary>Empty a gatherer of the good it gathers, so only real work can produce any.</summary>
        private static void StripYield(PlayerBot bot)
        {
            Type yield = BotHarvest.YieldFor(bot.Class);

            if (bot.Backpack == null || yield == null)
            {
                return;
            }

            var doomed = new List<Item>();

            foreach (Item item in bot.Backpack.Items)
            {
                if (item.GetType() == yield)
                {
                    doomed.Add(item);
                }
            }

            foreach (Item item in doomed)
            {
                item.Delete();
            }
        }

        /// <summary>Empty a crafter of everything its trade consumes, so only a delivery can restart it.</summary>
        private static void StripStock(PlayerBot bot)
        {
            CrafterProfile profile = CrafterProfiles.For(bot.Class);

            if (bot.Backpack == null || profile == null)
            {
                return;
            }

            var doomed = new List<Item>();

            foreach (Item item in bot.Backpack.Items)
            {
                foreach (Type material in profile.Materials)
                {
                    if (item.GetType() == material)
                    {
                        doomed.Add(item);
                        break;
                    }
                }
            }

            foreach (Item item in doomed)
            {
                item.Delete();
            }
        }

        /// <summary>
        /// Is every assertion already true? Then there is nothing left to wait for.
        ///
        /// Deliberately the SAME conditions Report checks, so an early exit can never pass a run
        /// that the full report would have failed - it only skips the waiting. Anything Report
        /// judges that is not here (a crafter blocked, the smith off its station) simply means the
        /// probe runs to timeout and fails there, which is the safe direction.
        /// </summary>
        private static bool Satisfied(List<PlayerBot> bots, int madeBefore, int minedBefore)
        {
            PlayerBot miner = Find(bots, BotClass.Miner);
            PlayerBot smith = Find(bots, BotClass.Smith);

            if (miner == null || miner.Deleted || smith == null || smith.Deleted)
            {
                return false;
            }

            var crafter = smith.Behavior as CrafterBehavior;

            return _clockedInZone
                && BotWorkSites.Mined > minedBefore
                && !miner.HaulPending
                && crafter != null
                && crafter.Received > 0
                && crafter.Made > madeBefore
                // The walking miner is part of the bar, not a bonus. Without it here the probe
                // exits the moment the face-spawned miner finishes its cycle - about two minutes
                // in - and the bot that is still walking out of town is never looked at.
                && _walkIn != null
                && _walkIn.Swings > 0;
        }

        /// <summary>
        /// Was the miner actually inside its site's work zone, working?
        ///
        /// Clock-in now requires the zone AND something in harvest reach, so this is really a
        /// check that the gate did its job - but asserting it explicitly is what stops the probe
        /// passing again on a bot that stood on a bridge, which is where the last one was found.
        /// </summary>
        private static void CheckClockIn(PlayerBot miner, NavDestination site)
        {
            if (miner == null || miner.Deleted)
            {
                _clockInNote = "the miner was gone before it could be checked";
                return;
            }

            var gatherer = miner.Behavior as GathererBehavior;

            if (gatherer == null)
            {
                _clockInNote = "the miner stopped being a Gatherer within 12s (it is a "
                    + miner.Behavior.SerializableName + ")";
                return;
            }

            bool inZone = false;

            foreach (NavZone zone in Nav.ZonesAt(miner.Location, miner.Map))
            {
                if (zone.HasTag("mine"))
                {
                    inZone = true;
                    break;
                }
            }

            _clockedInZone = inZone && gatherer.IsWorking;

            _clockInNote = String.Format(
                "at {0}: inZone {1}, working {2}",
                miner.Location,
                inZone ? "yes" : "NO",
                gatherer.IsWorking ? "yes" : "NO");
        }

        private static NavDestination FirstUsable(Map map, BotStation station)
        {
            List<NavDestination> usable = BotWorkSites.Available(map, station);

            return usable.Count == 0 ? null : usable[0];
        }

        /// <summary>
        /// The candidate with the shortest road from a site, measured with the router the bot
        /// will itself use - the same reason BotDestinations.DistanceFactor is measured along
        /// the road. Falls back to the first candidate when nothing routes, so a broken graph
        /// still runs the probe and fails it for the real reason.
        /// </summary>
        private static NavDestination NearestByRoad(NavDestination from, List<NavDestination> candidates)
        {
            NavDestination best = null;
            int bestTiles = Int32.MaxValue;

            foreach (NavDestination candidate in candidates)
            {
                NavRoute route;
                string error;

                if (!Nav.TryRoute(from.Id, candidate.Id, out route, out error) || route == null)
                {
                    continue;
                }

                int tiles = BotMovement.RouteTiles(route);

                if (tiles < bestTiles)
                {
                    bestTiles = tiles;
                    best = candidate;
                }
            }

            if (best == null && candidates.Count > 0)
            {
                best = candidates[0];
            }

            return best;
        }

        private static void Report(List<PlayerBot> bots, int madeBefore, int minedBefore)
        {
            try
            {
                PlayerBot miner = Find(bots, BotClass.Miner);
                PlayerBot smith = Find(bots, BotClass.Smith);

                var problems = new List<string>();
                var notes = new List<string>();

                if (miner == null || miner.Deleted)
                {
                    problems.Add("the miner did not survive the window");
                }
                else
                {
                    // The gatherer has become a Traveler by now, so its swing count is gone with
                    // it. What survives is the evidence: the ore is out of the pack and the
                    // delivery ledger moved.
                    if (miner.HaulPending)
                    {
                        problems.Add("the miner is still carrying its load - it never reached a delivery point");
                    }

                    notes.Add("miner ended as " + miner.Behavior.SerializableName);
                }

                // THE ASSERTION THAT WAS MISSING. Delivery proves the walk; only this proves the
                // work. A miner that mined nothing can still deliver, because it spawns holding
                // ore - so without this the probe passes on sites that have no rock at all.
                int mined = BotWorkSites.Mined - minedBefore;

                if (mined <= 0)
                {
                    problems.Add("the miner mined NOTHING - no swing produced ore");
                }
                else
                {
                    notes.Add(String.Format("{0} unit(s) actually mined", mined));
                }

                if (!_clockedInZone)
                {
                    problems.Add("the miner was not working inside its site zone - " + _clockInNote);
                }
                else
                {
                    notes.Add("clocked in inside the zone");
                }

                // THE WALK-IN, which is the half this probe could not see before. A gatherer that
                // starts in town has to route out, get inside the zone, find something in reach
                // and swing at it. Swinging is the proof: arriving is not enough, because a bot
                // standing on a thin tile at the edge of the face arrives perfectly well and then
                // mines nothing for the length of its shift.
                if (_walkIn == null)
                {
                    problems.Add("the walking miner was never created");
                }
                else if (_walkIn.Swings <= 0)
                {
                    problems.Add(String.Format(
                        "the miner that had to walk to the site never swung - it was {0}",
                        _walkIn.IsWorking ? "clocked in but idle" : "never clocked in"));
                }
                else
                {
                    notes.Add(String.Format(
                        "the walking miner reached the site and swung {0} time(s), {1} mined",
                        _walkIn.Swings,
                        _walkIn.Mined));
                }

                // ONE CLOCK-IN PER SHIFT. The probe used to sample a boolean twelve seconds in
                // and call that the answer, which cannot tell one clock-in from eight - and eight
                // is what was happening. A gatherer steps itself off the face by design, loses
                // reach, walks back, and every round trip re-announced a shift that had never
                // ended. The README recorded this as fixed while the log in the same session
                // showed a bot clocking in four times.
                // Only the "more than once" half is reported here. Never clocking in at all is
                // already the branch above's fault to name, and one fault should not be two
                // problems in the report.
                if (_walkIn != null && _walkIn.ClockIns > 1)
                {
                    problems.Add(String.Format(
                        "the walking miner clocked in {0} times in one shift - a shift is one clock-in",
                        _walkIn.ClockIns));
                }
                else if (_walkIn != null && _walkIn.ClockIns == 1)
                {
                    notes.Add("clocked in once, as a shift should");
                }

                if (smith == null || smith.Deleted)
                {
                    problems.Add("the smith did not survive the window");
                }
                else
                {
                    var crafter = smith.Behavior as CrafterBehavior;

                    if (crafter == null)
                    {
                        problems.Add("the smith stopped being a Crafter mid-probe");
                    }
                    else
                    {
                        // THE HAND-OVER IS TO THIS SMITH, not to anybody. "A delivery happened"
                        // was true of a load banked at an empty Trinsic forge, and with three
                        // unstaffed forges on the graph that is where one haul in three went.
                        // The rule under test is that a laden miner goes to the bench with
                        // somebody at it; the proof is that this bench's stock rose.
                        if (crafter.Received <= 0)
                        {
                            problems.Add(BotWorkSites.Deliveries == 0
                                ? "no load was delivered anywhere"
                                : String.Format(
                                    "the ore never reached the smith at '{0}' - {1} load(s) went "
                                    + "somewhere else (an empty bench or the bank)",
                                    crafter.DestinationId,
                                    BotWorkSites.Deliveries));
                        }
                        else
                        {
                            notes.Add(String.Format(
                                "the smith at '{0}' took {1} unit(s) over {2} hand-over(s)",
                                crafter.DestinationId,
                                crafter.Received,
                                crafter.Deliveries));
                        }

                        if (crafter.Blocked != null)
                        {
                            problems.Add("the smith could not work: " + crafter.Blocked);
                        }

                        // And that it was at a REAL station when it did - a validated forge whose
                        // arrival tile it is actually standing on, not merely somewhere that
                        // happened to have an anvil.
                        if (!OnStationArrival(smith, crafter.DestinationId))
                        {
                            problems.Add("the smith crafted away from a validated station arrival tile");
                        }
                        else
                        {
                            notes.Add("smith was on a validated station tile");
                        }

                        if (crafter.IsFull)
                        {
                            // Information, not a fault. A bench that filled up is a crafter that
                            // worked until it ran out of room, which is the loop succeeding.
                            notes.Add("its bench filled up");
                        }

                        if (crafter.Made - madeBefore <= 0)
                        {
                            problems.Add(String.Format(
                                "the smith made nothing in {0:0}s ({1} attempt(s)){2}",
                                Window.TotalSeconds,
                                crafter.Attempts,
                                crafter.IsDry ? " - it never received any ore" : ""));
                        }
                        else
                        {
                            notes.Add(String.Format(
                                "the smith made {0} item(s) in {1} attempt(s)",
                                crafter.Made - madeBefore,
                                crafter.Attempts));
                        }
                    }
                }

                var summary = new StringBuilder(256);

                summary.Append(String.Join(", ", notes.ToArray()));

                if (problems.Count > 0)
                {
                    Finish(bots, HealthResult.Fail(String.Format(
                        "in {0} - {1} problem(s): {2}. {3}",
                        _clock.Describe(),
                        problems.Count,
                        String.Join("; ", problems.ToArray()),
                        summary)));

                    return;
                }

                Finish(bots, HealthResult.Ok(String.Format(
                    "in {0} - a full cycle completed - {1}", _clock.Describe(), summary)));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Work probe threw while reporting.");
                Finish(bots, HealthResult.Fail("threw while reporting: " + ex.Message));
            }
        }

        private static bool OnStationArrival(PlayerBot bot, string destinationId)
        {
            if (bot == null || destinationId == null || BotWorkSites.IsExcluded(destinationId))
            {
                return false;
            }

            NavDestination station = Nav.Destination(destinationId);

            if (station == null || station.ArrivalList == null)
            {
                return false;
            }

            foreach (NavArrival arrival in station.ArrivalList)
            {
                if (bot.X == arrival.X && bot.Y == arrival.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private static PlayerBot Find(List<PlayerBot> bots, BotClass cls)
        {
            foreach (PlayerBot bot in bots)
            {
                if (bot != null && bot.Class == cls)
                {
                    return bot;
                }
            }

            return null;
        }

        private static void Finish(List<PlayerBot> bots, HealthResult result)
        {
            // First, and before anything that can throw: a poll left running would keep reporting
            // over the top of this one for the life of the process.
            if (_watch != null)
            {
                _watch.Stop();
                _watch = null;
            }

            _last = result;
            _running = false;

            if (result.Status == HealthStatus.Ok)
            {
                Log.Info("Bot work probe PASSED - {0}", result.Detail);
            }
            else if (result.Status == HealthStatus.Warn)
            {
                Log.Warn("Bot work probe - {0}", result.Detail);
            }
            else
            {
                Log.Error("Bot work probe FAILED - {0}", result.Detail);
            }

            if (bots == null)
            {
                return;
            }

            for (int i = 0; i < bots.Count; i++)
            {
                if (bots[i] != null && !bots[i].Deleted)
                {
                    // Release the beast explicitly rather than relying on the reaper: the probe
                    // should leave the world exactly as it found it, not ten seconds later.
                    BotPackAnimals.Release(bots[i]);
                    bots[i].Delete();
                }
            }
        }
    }
}
