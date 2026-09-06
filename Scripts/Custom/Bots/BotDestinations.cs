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
                    return candidates[i];
                }
            }

            return candidates[candidates.Count - 1];
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
                return 2.0;
            }

            return 0.02;
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
