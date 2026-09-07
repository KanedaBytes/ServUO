using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Where a bot decides to go next.
    ///
    /// The weights come from Data/Custom/bots.json (see BotDestinationConfig); this is the roll
    /// itself, plus the census the health check needs.
    ///
    /// GAME THREAD ONLY - it reads the navigation graph.
    /// </summary>
    public static class BotDestinations
    {
        /// <summary>
        /// Pick a destination for this bot, or null when nothing is eligible.
        ///
        /// <paramref name="avoidId"/> is the place it has just left. A bot that walks out of the
        /// bank and immediately picks the bank again looks broken in a way that a bot going
        /// somewhere else does not, and with 19 eligible destinations the re-roll is cheap.
        /// </summary>
        public static NavDestination Pick(PlayerBot bot, string avoidId)
        {
            if (bot == null || bot.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }

            List<NavDestination> candidates = Nav.Destinations(bot.Map, null, null);

            if (candidates.Count == 0)
            {
                return null;
            }

            BotDestinationConfig config = BotSystem.Store.Destinations;

            var weights = new double[candidates.Count];

            // -1 means "not measured": only work sites are routed, and only when they still have
            // a weight worth spending an A* on.
            var routeTiles = new int[candidates.Count];

            for (int i = 0; i < routeTiles.Length; i++)
            {
                routeTiles[i] = -1;
            }

            // Why a candidate could not be routed to, when it could not.
            var noRoute = new string[candidates.Count];

            double total = 0.0;

            for (int i = 0; i < candidates.Count; i++)
            {
                NavDestination candidate = candidates[i];

                // A gatherer walking a load home is on an errand, and its weights come from
                // somewhere else entirely - see HaulWeightFor.
                double weight = HaulWeightFor(bot, candidate);
                bool hauling = weight >= 0.0;

                if (!hauling)
                {
                    weight = config.WeightFor(candidate, bot.Class);
                }

                // A work site that failed validation at load is not a destination at all. Left
                // in, a Miner would pick the unreachable face every tick, fail to route, and
                // stand still - which from the outside is a broken walker, not a bad coordinate.
                if (BotWorkSites.IsExcluded(candidate.Id))
                {
                    weight = 0.0;
                }

                // Just-left gets a heavy discount rather than an exclusion: on a graph with one
                // eligible destination, excluding it would strand the bot entirely.
                if (avoidId != null && Insensitive.Equals(candidate.Id, avoidId))
                {
                    weight *= 0.05;
                }

                // A destination short of its standing crowd pulls harder. CAPPED at four times,
                // because an uncapped multiplier on a large floor makes one bank the only place
                // anybody goes - the shard would empty into it.
                //
                // NOT WHILE HAULING, and this one cost two probe runs to find. The floor exists to
                // draw LOITERERS, so that a bank looks busy; a miner with a pack full of ore is not
                // a loiterer. Applied to it, an empty bank's fourfold boost turned 2.0 into 8.0 and
                // pulled roughly a third of all deliveries away from a forge with a dry smith
                // standing at it - which reads, from outside, as a hand-over that does not work.
                if (weight > 0.0 && !hauling)
                {
                    int shortfall = BotCrowds.Shortfall(candidate);

                    if (shortfall > 0)
                    {
                        weight *= 1.0 + Math.Min(shortfall, 3);
                    }
                }

                if (weight > 0.0)
                {
                    // And the mirror: a work site that is filling up pulls less, reaching zero at
                    // capacity. Linear rather than a cliff, so a half-full face is half as
                    // attractive rather than equally attractive right up to the last slot.
                    if (BotWorkSites.IsWorkType(candidate.Type))
                    {
                        weight *= BotWorkSites.VacancyFactor(candidate);

                        // And how far it actually is to walk. Nothing here considered distance
                        // at all, so a Miner at the west gate weighed the face across the map
                        // exactly as heavily as the one it was standing beside.
                        int tiles;
                        string why;

                        weight *= DistanceFactor(bot, candidate, config, out tiles, out why);
                        routeTiles[i] = tiles;
                        noRoute[i] = why;
                    }
                }

                if (weight < 0.0)
                {
                    weight = 0.0;
                }

                weights[i] = weight;
                total += weight;
            }

            if (total <= 0.0)
            {
                // Every candidate scored zero. Upstream's original answer here was a uniform roll
                // over the whole catalogue, which quietly handed back the very places the weights
                // had just excluded - the one path that could still walk a bot somewhere it was
                // configured never to go. Return nothing instead and let the caller try again.
                return null;
            }

            double roll = Utility.RandomDouble() * total;

            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= weights[i];

                if (roll <= 0.0)
                {
                    LogChoice(bot, candidates, weights, routeTiles, noRoute, i);
                    return candidates[i];
                }
            }

            LogChoice(bot, candidates, weights, routeTiles, noRoute, candidates.Count - 1);

            return candidates[candidates.Count - 1];
        }

        /// <summary>
        /// How much a site's distance discounts it: 1.0 underfoot, 0.5 at the configured
        /// half-distance, never zero for anywhere it can actually walk to.
        ///
        /// Measured along the road with the router the bot will itself use, so a site on the far
        /// side of a wall is far even when it is close. A site it cannot route to at all scores
        /// zero and drops out - which is also what keeps an island out of the running, though the
        /// place to FIX an island is the load warning, not here.
        /// </summary>
        private static double DistanceFactor(
            PlayerBot bot, NavDestination candidate, BotDestinationConfig config,
            out int tiles, out string why)
        {
            tiles = -1;
            why = null;

            NavRoute route;
            string error;

            if (!Nav.TryRouteFrom(bot.Location, bot.Map, candidate.Id, bot, out route, out error)
                || route == null)
            {
                // Carried through to the log rather than flattened to "unreachable". They are
                // different faults with different fixes: an island is authoring, an exclusive
                // arrival already reserved is a busy forge and rights itself, and a bot standing
                // off the graph is the walker. One word for all three sent the reader to the
                // wrong one.
                why = error;
                return 0.0;
            }

            tiles = RouteTiles(route);

            double half = config.DistanceHalfTiles;

            if (half <= 0.0)
            {
                return 1.0;
            }

            return 1.0 / (1.0 + (tiles / half));
        }

        /// <summary>
        /// The length of a route in tiles, summed hop by hop.
        ///
        /// Not the step count, which counts a twelve-tile hop and a one-tile hop the same, and not
        /// NavRoute.Cost, which is weighted by road tags for the SEARCH and so is not a distance
        /// at all - a road hop costs 0.9 of what it measures.
        /// </summary>
        private static int RouteTiles(NavRoute route)
        {
            if (route == null || route.Steps == null || route.Steps.Count < 2)
            {
                return 0;
            }

            int tiles = 0;

            for (int i = 1; i < route.Steps.Count; i++)
            {
                Point3D a = route.Steps[i - 1].Point;
                Point3D b = route.Steps[i].Point;

                tiles += Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            }

            return tiles;
        }

        /// <summary>
        /// Records which site was chosen and what it was chosen over.
        ///
        /// A weighted roll is invisible from outside: a Miner walking past the face beside it to
        /// one across the map is either a bad weight, a full site, or a fair roll that happened to
        /// land, and nothing in the log told them apart. Only work sites are listed - the town
        /// destinations are a different question and would bury the one being asked.
        /// </summary>
        private static void LogChoice(
            PlayerBot bot, List<NavDestination> candidates, double[] weights, int[] tiles,
            string[] noRoute, int chosen)
        {
            var parts = new List<string>();
            bool anyInPlay = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (!BotWorkSites.IsWorkType(candidates[i].Type))
                {
                    continue;
                }

                if (weights[i] > 0.0)
                {
                    anyInPlay = true;
                }

                string detail;

                if (tiles[i] >= 0)
                {
                    detail = String.Format(" {0}t", tiles[i]);
                }
                else if (!String.IsNullOrEmpty(noRoute[i]))
                {
                    detail = String.Format(" no route: {0}", noRoute[i]);
                }
                else
                {
                    detail = "";
                }

                parts.Add(String.Format(
                    "{0}{1} w{2:0.00}{3}",
                    i == chosen ? "*" : "",
                    candidates[i].Id,
                    weights[i],
                    detail));
            }

            // Logged whenever a site was genuinely in the running, not only when one won. "Why
            // did it walk past the face beside it" is the same question as "why did it pick the
            // far one", and answering only the second leaves the first invisible.
            if (!anyInPlay || parts.Count == 0)
            {
                return;
            }

            string line = String.Join(", ", parts.ToArray());

            if (!BotWorkSites.IsWorkType(candidates[chosen].Type))
            {
                line += String.Format(" - chose '{0}' instead", candidates[chosen].Id);
            }

            BotLog.Note(bot, BotLogKind.Route, "site choice: {0}", line);
        }

        /// <summary>
        /// The weights a gatherer uses while it is carrying a load home, or -1 for "not hauling".
        ///
        /// Upstream's override, kept including its numbers. A miner with a pack full of ore wants
        /// a smith, a lumberjack wants a carpenter, either will settle for a bank, and neither is
        /// interested in a tavern until the load is off its back. Without this the bot walks out
        /// of the mine and picks the mine again, because the mine is what its class weights love.
        ///
        /// It deliberately does NOT consult byType/byTag at all: hauling is a different errand
        /// from living in the town, and blending the two tables would let a Miner's 10.0 site
        /// weight outvote the delivery it is actually on.
        /// </summary>
        private static double HaulWeightFor(PlayerBot bot, NavDestination candidate)
        {
            if (!bot.HaulPending || !BotClassHelper.IsGatherer(bot.Class))
            {
                return -1.0;
            }

            Type raw = BotHarvest.YieldFor(bot.Class);

            // A station whose trade buys what this bot is carrying. Exclusion is not tested here:
            // the caller zeroes an excluded destination whatever this returns.
            foreach (CrafterProfile profile in CrafterProfiles.All)
            {
                if (raw == null || profile.RawGood != raw)
                {
                    continue;
                }

                if (!Insensitive.Equals(candidate.Type, profile.StationType))
                {
                    continue;
                }

                if (profile.StationTag != null && !candidate.HasTag(profile.StationTag))
                {
                    continue;
                }

                // Upstream's 9.0 against the bank's 2.0 is a station this trade COULD sell to.
                // Whether anybody is actually there was not a question their weights asked, and
                // it is the question that decides whether the haul turns into anything: a load
                // left at an empty forge is a load in a bank box with extra steps.
                //
                // So a station with a crafter of the right trade standing at it outbids the bank
                // outright rather than by four to one. This is a deliberate improvement on
                // upstream, and a small one - it changes which of two sensible destinations a
                // laden miner picks, and nothing else. It also makes the difference between the
                // work probe proving the hand-over and proving it four times in five.
                return IsStaffed(candidate, profile) ? 20.0 : 9.0;
            }

            if (Insensitive.Equals(candidate.Type, "bank"))
            {
                // The bank is the FALLBACK, not a competitor. Upstream's 2.0 against a station's
                // 9.0 still sent about one load in eleven to a bank while a dry smith stood at the
                // forge waiting for it - which is not a miner making a choice, it is a miner
                // getting it wrong. If somebody is actually at the bench, go to the bench.
                //
                // With nobody there the bank is exactly right, and this returns to 2.0: ore in a
                // bank box is ore kept, and it is what a player would have done.
                return AnyStaffedStation(bot, raw) ? 0.02 : 2.0;
            }

            return 0.02;
        }

        /// <summary>Is a crafter of the trade that buys this good working anywhere on the facet?</summary>
        private static bool AnyStaffedStation(PlayerBot bot, Type raw)
        {
            if (raw == null)
            {
                return false;
            }

            foreach (CrafterBehavior crafter in CrafterBehavior.Live())
            {
                if (crafter.RawGood == raw && !BotWorkSites.IsExcluded(crafter.DestinationId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Is a crafter of this trade actually working at this station right now?</summary>
        private static bool IsStaffed(NavDestination destination, CrafterProfile profile)
        {
            foreach (CrafterBehavior crafter in CrafterBehavior.Live())
            {
                if (crafter.RawGood == profile.RawGood
                    && Insensitive.Equals(crafter.DestinationId, destination.Id))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Classes with no destination they are allowed to visit on the loaded graph.
        ///
        /// A class weighted to zero everywhere is a bot that can never travel - it will ask for a
        /// destination every tick, get nothing, and stand still for ever looking like a bug in the
        /// walker rather than a line in a config file. Reported by Bots.Population.
        /// </summary>
        public static List<BotClass> StarvedClasses(Map map)
        {
            var starved = new List<BotClass>();

            if (map == null || map == Map.Internal)
            {
                return starved;
            }

            List<NavDestination> candidates = Nav.Destinations(map, null, null);

            if (candidates.Count == 0)
            {
                // No destinations at all is a navigation-data problem, not a weighting one, and
                // Nav.Data already reports it. Saying every class is starved would be seventeen
                // lines of noise about one missing file.
                return starved;
            }

            BotDestinationConfig config = BotSystem.Store.Destinations;

            foreach (BotClass cls in BotClassHelper.Rollable())
            {
                bool any = false;

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (config.WeightFor(candidates[i], cls) > 0.0)
                    {
                        any = true;
                        break;
                    }
                }

                if (!any)
                {
                    starved.Add(cls);
                }
            }

            return starved;
        }

        /// <summary>How many destinations on this facet any class may visit at all.</summary>
        public static int EligibleCount(Map map)
        {
            if (map == null || map == Map.Internal)
            {
                return 0;
            }

            List<NavDestination> candidates = Nav.Destinations(map, null, null);
            BotDestinationConfig config = BotSystem.Store.Destinations;

            int count = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                foreach (BotClass cls in BotClassHelper.Rollable())
                {
                    if (config.WeightFor(candidates[i], cls) > 0.0)
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }
    }
}
