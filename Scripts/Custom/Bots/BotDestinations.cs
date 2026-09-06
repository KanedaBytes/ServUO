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

                double weight = config.WeightFor(candidate, bot.Class);

                // Just-left gets a heavy discount rather than an exclusion: on a graph with one
                // eligible destination, excluding it would strand the bot entirely.
                if (avoidId != null && Insensitive.Equals(candidate.Id, avoidId))
                {
                    weight *= 0.05;
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
