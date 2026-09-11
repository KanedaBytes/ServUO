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
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

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

                // Home bias - "regulars emerge for free", upstream's words at
                // DestinationCatalog.cs:216-223 and its number. Applied to the haul roll as well,
                // exactly as theirs is: a laden miner from Britain prefers Britain's forge over
                // Trinsic's the same way it prefers Britain's bank. This is the WHOLE of what a
                // home does. There is no distance term for a town destination and no rule that
                // sends a bot home; a Trinsic resident standing in Britain rolls Trinsic's bank at
                // 2.5 and Britain's at 1, and walks whichever the dice pick.
                if (weight > 0.0 && bot.HomeTown != null && candidate.HasTag(bot.HomeTown))
                {
                    weight *= config.HomeBias;
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

            tiles = BotMovement.RouteTiles(route);

            double half = config.DistanceHalfTiles;

            if (half <= 0.0)
            {
                return 1.0;
            }

            return 1.0 / (1.0 + (tiles / half));
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

            // A HAUL ROLL WAS INVISIBLE HERE, and that is why "where did the ore go" could only be
            // answered by guessing. This filter was work sites only - a mine or a lumber camp -
            // and a laden gatherer rolls forges and banks, so the one decision that ends an errand
            // logged nothing at all. While hauling, the delivery points in the running are what
            // matter, and the zero-weight remainder is left out so the line stays one line.
            bool hauling = bot != null && bot.HaulPending;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (hauling)
                {
                    // A DELIVERY POINT THAT SCORED ZERO IS THE MOST INTERESTING ROW HERE, and the
                    // first cut of this filter threw it away. A laden miner rolled a forge 257
                    // tiles off and the line did not contain 'brit-forge' at all - it had been
                    // zeroed, by an exclusion or by DistanceFactor's route check failing, and the
                    // `why` that says which was dropped with it. So delivery points are listed
                    // whatever they weigh; only the non-delivery remainder, which is all zeros by
                    // construction now, is left out to keep the line to one line.
                    bool delivery = BotWorkSites.IsWorkType(candidates[i].Type)
                        || Insensitive.Equals(candidates[i].Type, "bank");

                    if (!delivery && weights[i] <= 0.0)
                    {
                        continue;
                    }
                }
                else if (!BotWorkSites.IsWorkType(candidates[i].Type))
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

            if (hauling)
            {
                // On the console as well as in the event log, because a haul that goes to the
                // wrong town is diagnosed from a probe run's console days later, and botlog.json
                // only exists while the live map is on. One line per completed shift, which is
                // rare enough to afford.
                Log.Info(
                    "{0} is hauling to '{1}', {2} tile(s) away: {3}",
                    bot.Name,
                    candidates[chosen].Id,
                    Math.Max(
                        Math.Abs(candidates[chosen].Location.X - bot.X),
                        Math.Abs(candidates[chosen].Location.Y - bot.Y)),
                    line);

                BotLog.Note(bot, BotLogKind.Route, "haul choice: {0}", line);
                return;
            }

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
                //
                // And the other half of the same rule, which took a second town to notice: once
                // somebody IS at a bench, an EMPTY bench of the same trade weighs what the bank
                // weighs, because that is what it is - a load in a bank box with extra steps.
                // Left at 9, the three unstaffed forges the Trinsic adopt brought in outbid the
                // one staffed forge between them about one haul in three, and the work probe
                // became a lottery about which forge the ore went to rather than a test of the
                // hand-over. Upstream never met this case: its spawner pins a smith at every
                // forge, so every forge is staffed by construction.
                //
                // "Staffed" is read live, from a crafter clocked in NOW - never from data. A
                // Trinsic forge becomes a delivery point the moment a smith settles at it, and
                // stops being one when the smith walks away.
                if (IsStaffed(candidate, profile))
                {
                    // AND THE NEAREST STAFFED BENCH, not any staffed bench. Staffing alone was
                    // the whole test, so every staffed forge on the facet returned 20.0 and a
                    // laden miner picked between them by dice. Measured: a miner that filled its
                    // pack at 'brit-mine-north' (1449,1521) walked 255 tiles to 'britain-forge'
                    // past 'brit-forge' 36 tiles away, both staffed, both weighing the same - and
                    // the work probe read that as a hand-over that never happened, because the
                    // smith it was watching was the one that got walked past.
                    //
                    // Distance is deliberately NOT a general term in this table - a bot choosing
                    // where to spend its evening should not be pulled towards whatever is closest,
                    // and the comment on HomeBias above says so. A haul is the exception and the
                    // reason is the load: the ore is already in the pack, every tile of the walk
                    // is a chance for the recovery ladder to fire, and the bench that gets it is
                    // interchangeable. Nearest is not a preference here, it is the whole errand.
                    return Insensitive.Equals(candidate.Id, NearestStaffedStationId(bot, raw))
                        ? 20.0
                        : 0.5;
                }

                if (AnyStaffedStation(bot, raw))
                {
                    return 0.02;
                }

                // AND THE SAME NEAREST RULE WITH NOBODY THERE, which is the half that was missing.
                //
                // The rule above was added because a miner walked 255 tiles past a forge 36 tiles
                // away, and it was added to the STAFFED branch only. So the moment nothing is
                // staffed - which is exactly when a laden miner most needs a sensible fallback -
                // distance dropped out of the roll entirely and all four forges weighed 9.0 apiece.
                //
                // Measured on this graph from 'brit-mine-north': a 59.8% chance of setting off for
                // a forge that is not the nearest, and a 19.9% chance of 'trinsic-forge' - ONE
                // THOUSAND ONE HUNDRED AND TWENTY-FOUR TILES away, in another town, with a forge at
                // 36 and a bank at 169. At a bot's pace that walk is about four minutes of pure
                // stepping, which is how a miner ends a seven-minute probe still carrying its load.
                // Home bias makes it worse rather than better: a Trinsic-resident miner multiplies
                // Trinsic's forge by 2.5 and heads home across the map with its ore.
                // 0.01 for the others, which is a FALLBACK RATHER THAN A CHOICE, and the number
                // was measured rather than picked. At 0.1 it was still about one roll in thirty,
                // and one roll in thirty is one probe run in five: of five runs on the first cut
                // of this fix, the four that rolled 'brit-forge' at ~30 tiles all delivered and
                // the one that rolled 'brit-forge-south' at 245 failed. Distance is the whole
                // outcome, so a second choice has to be rare enough that nobody ever sees it.
                //
                // Not zero, because the nearest bench could be excluded or unroutable and a laden
                // bot with no positive candidate anywhere would have nothing to walk to at all.
                // 0.01 against 9.0 is about one in nine hundred: an escape hatch, not an option.
                return Insensitive.Equals(candidate.Id, NearestStationId(bot, profile))
                    ? 9.0
                    : 0.01;
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
                //
                // Nearest, for the station branch's reason: 'trinsic-bank' is 1307 tiles from
                // 'brit-mine-north' and 'brit-bank' is 169, and they weighed the same.
                if (AnyStaffedStation(bot, raw))
                {
                    return 0.02;
                }

                return Insensitive.Equals(candidate.Id, NearestDeliveryBankId(bot)) ? 2.0 : 0.01;
            }

            // NOT A DELIVERY POINT AT ALL, AND 0.02 IS NOT THE SAME AS NEVER.
            //
            // Fifty-seven of this graph's sixty-five destinations are neither a forge nor a bank,
            // and 0.02 apiece aggregates to 1.14 against the nearest staffed bench's 20.0 - a
            // 5.4% chance per roll that a miner with a pack full of ore sets off for a tavern.
            // Thirty-two of those fifty-seven are shops, where the 0.8 handoff then commits it to
            // a two-to-six minute shopping visit with the ore still on its back: 2.4% per roll of
            // the errand being abandoned outright rather than merely detoured.
            //
            // The intent was always "effectively never" - the header says hauling is a different
            // errand from living in the town - and a small weight times a large number of
            // candidates is not "effectively never", it is a slow leak. Zero is what was meant.
            // It cannot strand a bot: four forges and four banks are always positive, and the
            // caller reads any value >= 0 as a haul weight, so this stays distinct from the -1.0
            // that means "not hauling, use the ordinary table".
            return 0.0;
        }

        /// <summary>
        /// The nearest station of this trade, staffed or not, by the destination's own tile.
        ///
        /// The sibling of NearestStaffedStationId, for the case where nobody is at any bench. That
        /// one walks the live crafters because staffing is a fact about bots; this walks the graph,
        /// because with nobody working there is no bot to ask and the question is only about where
        /// the benches are.
        /// </summary>
        private static string NearestStationId(PlayerBot bot, CrafterProfile profile)
        {
            if (bot == null || profile == null)
            {
                return null;
            }

            return NearestId(bot, Nav.Destinations(bot.Map, profile.StationType, profile.StationTag));
        }

        /// <summary>The nearest bank, for a haul that has no bench to go to.</summary>
        private static string NearestDeliveryBankId(PlayerBot bot)
        {
            return bot == null ? null : NearestId(bot, Nav.Destinations(bot.Map, "bank", null));
        }

        /// <summary>
        /// Nearest by Chebyshev, skipping anything the work-site validator threw out.
        ///
        /// Straight line rather than walked road, deliberately and for NearestStaffedStationId's
        /// reason: this decides between benches that are tens of tiles apart and ones that are a
        /// thousand, and an A* per candidate per roll would cost far more than the answer is worth.
        /// </summary>
        private static string NearestId(PlayerBot bot, List<NavDestination> among)
        {
            string nearest = null;
            int best = Int32.MaxValue;

            for (int i = 0; among != null && i < among.Count; i++)
            {
                NavDestination candidate = among[i];

                if (candidate == null || BotWorkSites.IsExcluded(candidate.Id))
                {
                    continue;
                }

                int distance = Math.Max(
                    Math.Abs(candidate.Location.X - bot.X),
                    Math.Abs(candidate.Location.Y - bot.Y));

                if (distance < best)
                {
                    best = distance;
                    nearest = candidate.Id;
                }
            }

            return nearest;
        }

        /// <summary>
        /// The staffed station of this trade closest to the bot, by the DESTINATION's tile.
        ///
        /// The destination rather than the crafter, because the destination is what the bot walks
        /// to; a smith that has wandered a few tiles off its bench does not move the forge.
        ///
        /// Ties go to the first seen, which is fine: two staffed benches the same distance away
        /// are the same errand, and the roll below still has to pick one of them.
        /// </summary>
        private static string NearestStaffedStationId(PlayerBot bot, Type raw)
        {
            if (bot == null || raw == null)
            {
                return null;
            }

            string nearest = null;
            int best = Int32.MaxValue;

            foreach (CrafterBehavior crafter in CrafterBehavior.Live())
            {
                if (crafter.RawGood != raw
                    || !crafter.IsAtStation
                    || BotWorkSites.IsExcluded(crafter.DestinationId))
                {
                    continue;
                }

                NavDestination station = Nav.Destination(crafter.DestinationId);

                if (station == null)
                {
                    continue;
                }

                int distance = Math.Max(
                    Math.Abs(station.Location.X - bot.X),
                    Math.Abs(station.Location.Y - bot.Y));

                if (distance < best)
                {
                    best = distance;
                    nearest = crafter.DestinationId;
                }
            }

            return nearest;
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
                if (crafter.RawGood == raw
                    && crafter.IsAtStation
                    && !BotWorkSites.IsExcluded(crafter.DestinationId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Is a crafter of this trade actually working at this station right now?
        ///
        /// "Right now" is IsAtStation, not merely wearing the Crafter brain: a smith ten hops
        /// from the forge is a smith on the road, and a miner sent to meet it there would find
        /// an empty bench. Live() alone counted the walkers, and its own summary said otherwise.
        /// </summary>
        private static bool IsStaffed(NavDestination destination, CrafterProfile profile)
        {
            foreach (CrafterBehavior crafter in CrafterBehavior.Live())
            {
                if (crafter.RawGood == profile.RawGood
                    && crafter.IsAtStation
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
