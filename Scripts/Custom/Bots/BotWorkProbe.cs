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

        /// <summary>The shift the miner works before it shoulders the load.</summary>
        public static readonly TimeSpan Shift = TimeSpan.FromSeconds(30.0);

        private static HealthResult _last;
        private static bool _running;

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

                NavDestination site = FirstUsable(map, new BotStation("mine", null));
                NavDestination forge = FirstUsable(map, BotClassHelper.StationFor(BotClass.Smith));

                if (site == null || forge == null)
                {
                    Finish(bots, HealthResult.Fail(String.Format(
                        "cannot run: {0}",
                        site == null ? "no usable mine on the graph" : "no usable forge on the graph")));
                    return;
                }

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
                smith.Behavior = crafter;

                // The miner starts at the site, works a short shift, and then walks the load in
                // under its own steam - the real EndShift path, not a rigged one.
                var miner = new PlayerBot(BotClass.Miner, BotSkillTier.Grandmaster);
                bots.Add(miner);

                Point3D face;

                if (!Nav.TryPickArrival(site.Id, miner, out face))
                {
                    face = site.Location;
                }

                miner.MoveToWorld(face, map);

                var gatherer = new GathererBehavior();
                gatherer.DestinationId = site.Id;
                gatherer.VisitExpiresAt = CustomTime.Now + Shift;
                miner.Behavior = gatherer;

                Log.Info(
                    "Work probe: a Miner at '{0}' and a Smith at '{1}', {2:0}s window.",
                    site.Id,
                    forge.Id,
                    Window.TotalSeconds);

                List<PlayerBot> captured = bots;
                int madeBefore = crafter.Made;

                Timer.DelayCall(Window, () => Report(captured, madeBefore));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Work probe threw during setup.");
                Finish(bots, HealthResult.Fail("threw during setup: " + ex.Message));
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

        private static NavDestination FirstUsable(Map map, BotStation station)
        {
            List<NavDestination> usable = BotWorkSites.Available(map, station);

            return usable.Count == 0 ? null : usable[0];
        }

        private static void Report(List<PlayerBot> bots, int madeBefore)
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

                if (BotWorkSites.Deliveries == 0)
                {
                    problems.Add("no load was delivered");
                }
                else
                {
                    notes.Add(String.Format(
                        "{0} load(s), {1} unit(s) delivered",
                        BotWorkSites.Deliveries,
                        BotWorkSites.Delivered));
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
                        if (crafter.Blocked != null)
                        {
                            problems.Add("the smith could not work: " + crafter.Blocked);
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
                        "{0} problem(s): {1}. {2}",
                        problems.Count,
                        String.Join("; ", problems.ToArray()),
                        summary)));

                    return;
                }

                Finish(bots, HealthResult.Ok("a full cycle completed - " + summary));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Work probe threw while reporting.");
                Finish(bots, HealthResult.Fail("threw while reporting: " + ex.Message));
            }
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
