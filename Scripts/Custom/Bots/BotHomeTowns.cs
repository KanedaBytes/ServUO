// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotHomeTowns.cs — which town a bot calls home.
//
// uo-offline's BotHomeCities (BotEconomy.cs:34-83): one entry per distinct
// city in the destination catalog, weighted by how many destinations that
// city has, rolled once per bot at creation. Big towns get more residents.
//
// The one seam: upstream's destinations carry a `City` column. Ours carry
// tags, and a town here is a tag named in bots.json (`destinations.towns`).
// A destination belongs to a town when it carries that tag; the adopt step
// already writes `britain` and `trinsic`, and the editor writes whatever an
// author types. Nothing else about the rule changed.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// GAME THREAD ONLY - it reads the navigation store.
    /// </summary>
    public static class BotHomeTowns
    {
        /// <summary>
        /// Roll a home for a new bot: a configured town, weighted by its destination count, or
        /// null when no configured town has a destination on the graph at all.
        /// </summary>
        public static string Roll()
        {
            List<KeyValuePair<string, int>> towns = Census();

            int total = 0;

            foreach (KeyValuePair<string, int> town in towns)
            {
                total += town.Value;
            }

            if (total <= 0)
            {
                return null;
            }

            int roll = Utility.Random(total);

            foreach (KeyValuePair<string, int> town in towns)
            {
                roll -= town.Value;

                if (roll < 0)
                {
                    return town.Key;
                }
            }

            return towns[towns.Count - 1].Key;
        }

        /// <summary>
        /// Every configured town with the number of destinations carrying its tag, in the order
        /// bots.json lists them. Towns with nothing on the graph are kept at zero so a report can
        /// name them; Roll skips them.
        /// </summary>
        public static List<KeyValuePair<string, int>> Census()
        {
            var census = new List<KeyValuePair<string, int>>();

            string[] towns = BotSystem.Store.Destinations.Towns;

            if (towns == null || towns.Length == 0)
            {
                return census;
            }

            foreach (string town in towns)
            {
                int count = 0;

                foreach (NavDestination destination in NavigationSystem.Store.Destinations)
                {
                    if (destination.HasTag(town))
                    {
                        count++;
                    }
                }

                census.Add(new KeyValuePair<string, int>(town, count));
            }

            return census;
        }

        /// <summary>The configured town this destination belongs to, or null.</summary>
        public static string TownOf(NavDestination destination)
        {
            if (destination == null)
            {
                return null;
            }

            string[] towns = BotSystem.Store.Destinations.Towns;

            if (towns == null)
            {
                return null;
            }

            for (int i = 0; i < towns.Length; i++)
            {
                if (destination.HasTag(towns[i]))
                {
                    return towns[i];
                }
            }

            return null;
        }

        /// <summary>The configured town whose tag is nearest to a point on the graph, or null.</summary>
        public static string Nearest(Point3D location, Map map)
        {
            string[] towns = BotSystem.Store.Destinations.Towns;

            if (towns == null || map == null || map == Map.Internal)
            {
                return null;
            }

            string best = null;
            int bestDistance = Int32.MaxValue;

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                string town = TownOf(destination);

                if (town == null)
                {
                    continue;
                }

                int distance = Math.Max(
                    Math.Abs(destination.X - location.X),
                    Math.Abs(destination.Y - location.Y));

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = town;
                }
            }

            return best;
        }

        /// <summary>Is this a configured town name? Case-insensitive, for the [SpawnBot argument.</summary>
        public static bool TryParse(string value, out string town)
        {
            town = null;

            string[] towns = BotSystem.Store.Destinations.Towns;

            if (String.IsNullOrEmpty(value) || towns == null)
            {
                return false;
            }

            for (int i = 0; i < towns.Length; i++)
            {
                if (Insensitive.Equals(towns[i], value))
                {
                    town = towns[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Residents whose home has no station of their kind, for Bots.Population.
        ///
        /// A report, not a rule: upstream has no fallback for this case and neither does this.
        /// The bias applies where a home destination exists and is silent where none does, so a
        /// Trinsic-home Miner rolls exactly as a Britain one would. This line exists so that the
        /// count is visible rather than inferred from bots walking the road every day.
        /// </summary>
        public static List<string> StationlessResidents(Map map)
        {
            var lines = new List<string>();

            if (map == null || map == Map.Internal)
            {
                return lines;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted || bot.HomeTown == null)
                {
                    continue;
                }

                BotStation station = BotClassHelper.StationFor(bot);

                if (station.IsNone)
                {
                    station = BotWorkSites.SiteFor(bot.TradeClass);
                }

                if (station.IsNone)
                {
                    continue;
                }

                bool atHome = false;

                foreach (NavDestination destination in BotWorkSites.Available(map, station))
                {
                    if (destination.HasTag(bot.HomeTown))
                    {
                        atHome = true;
                        break;
                    }
                }

                if (atHome)
                {
                    continue;
                }

                string key = String.Format(
                    "{0} in {1}", BotClassHelper.DisplayName(bot.TradeClass), bot.HomeTown);

                int count;
                counts.TryGetValue(key, out count);
                counts[key] = count + 1;
            }

            foreach (KeyValuePair<string, int> pair in counts)
            {
                lines.Add(String.Format("{0} {1}", pair.Value, pair.Key));
            }

            lines.Sort(StringComparer.Ordinal);

            return lines;
        }
    }
}
