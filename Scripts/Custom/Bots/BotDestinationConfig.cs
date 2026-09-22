// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// How much each class wants each kind of destination.
    ///
    /// Two tables, multiplied:
    ///
    ///     final = byType[type][class ?? "default"]
    ///           * product over the destination's tags of byTag[tag][class ?? "default"]
    ///
    /// A missing table, type or tag contributes 1.0 - absent means "no opinion", not "no". A
    /// weight of **0 excludes**, which is how `home`, `guard` and `work` are kept off the list: a
    /// bot has no business loitering on a guard post, at a crier's station, or inside somebody's
    /// house.
    ///
    /// Two tables rather than one because of what the data actually looks like. Our destination
    /// `type` is a free token and there are only nine of them, but **13 of the 27 destinations are
    /// `shop`** - so type alone cannot tell a forge from a bakery. The discrimination lives in the
    /// tags (`craft` on seven, `food` on three, `magic`, `luxury`, `service`), which is why a Smith
    /// can want a craft shop without wanting every shop.
    ///
    /// Upstream keyed a single table on a 31-member DestinationType enum, which is the same idea
    /// with the discrimination baked into the enum instead. Ours is data.
    ///
    /// A class named in `singleMinded` bypasses both tables: see SingleMinded.
    /// </summary>
    public class BotDestinationConfig : IValidatableConfig
    {
        [JsonProperty("byType")]
        public Dictionary<string, Dictionary<string, double>> ByType { get; set; }

        [JsonProperty("byTag")]
        public Dictionary<string, Dictionary<string, double>> ByTag { get; set; }

        /// <summary>
        /// Classes that want their own work and almost nothing else - upstream's gatherer
        /// override, restored as data (uo-offline DestinationType.cs:338-352 at 7f38c7c: own site
        /// 10.0, bank 0.3, tavern and inn 0.2, everything else 0.02).
        ///
        /// For a class named here the answer is this table and nothing else: a type it names gets
        /// its weight, and every other type gets `otherwise`. The byType and byTag tables are not
        /// consulted - upstream has no tag multiplier, and a Miner's `craft` 2.0 at every smithy
        /// was a large part of why it went shopping instead of mining - with ONE exception: a type
        /// whose byType default is 0 stays 0 unless this table names it. That is the house rule
        /// that home, guard and work are nobody's business, and that forge, mine and lumber belong
        /// to the class that works them, and it holds for a single-minded class too. It is a
        /// deliberate divergence - upstream gives a gatherer 0.02 at a forge - recorded in the
        /// Bots README Deviations table.
        ///
        /// Everything after WeightFor still applies: home bias, the crowd floor, the just-left
        /// discount, and for a work site its vacancy and road distance. Those are ours and
        /// documented, and the distance term is what sends a Minoc Miner to the Minoc face.
        /// </summary>
        [JsonProperty("singleMinded")]
        public Dictionary<string, Dictionary<string, double>> SingleMinded { get; set; }

        /// <summary>The key in a single-minded class's table standing for "every type not named".</summary>
        public const string OtherwiseKey = "otherwise";

        /// <summary>
        /// The route distance, in tiles, at which a work site is half as attractive.
        ///
        /// Weighting had no distance term at all, so a Miner standing at the west gate was
        /// exactly as likely to walk to the far side of the map as to the face it could see. The
        /// term is `weight / (1 + routeTiles / this)`, which prefers the near site without ever
        /// excluding the far one - a shard where everybody mines the nearest rock is as wrong as
        /// one where they ignore distance, and a hyperbola falls off gently enough that a
        /// twice-as-far site still gets picked when it is twice as good.
        ///
        /// Measured along the ROAD, not as the crow flies. The whole reason the west cliff feels
        /// far is that reaching it means walking out of town and round; straight-line distance
        /// would call it close and be wrong about the only thing that matters.
        /// </summary>
        [JsonProperty("distanceHalfTiles")]
        public double DistanceHalfTiles { get; set; }

        /// <summary>
        /// The tags that name a town. A destination belongs to a town when it carries the tag,
        /// and a bot rolls one of these as its home at creation (BotHomeTowns).
        ///
        /// Upstream keeps this as a `City` column on every destination. Ours is a list of tags
        /// because tags are the geography this graph already has - the adopt step writes
        /// `britain` and `trinsic`, and a second column that says the same thing would drift from
        /// the first. Empty means no town anywhere: nobody has a home, and the bias never fires.
        /// </summary>
        [JsonProperty("towns")]
        public string[] Towns { get; set; }

        /// <summary>
        /// How much more a bot wants a destination in its home town. Upstream's 2.5
        /// (DestinationCatalog.cs:216-223), applied to every class, every type, and to the haul
        /// roll as well. Zero or absent falls back to 2.5 rather than switching the bias off,
        /// for the same reason distanceHalfTiles does: an unset value must not silently disable
        /// the rule. To disable it, empty `towns`.
        /// </summary>
        [JsonProperty("homeBias")]
        public double HomeBias { get; set; }

        /// <summary>The key standing for "every class that is not named".</summary>
        public const string DefaultKey = "default";

        [JsonConstructor]
        public BotDestinationConfig()
        {
            ByType = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            ByTag = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            SingleMinded = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        }

        public void Validate(ConfigErrors errors)
        {
            if (ByType == null)
            {
                ByType = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            }

            if (ByTag == null)
            {
                ByTag = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            }

            // Zero or negative would divide the term away or invert it, so an unset value falls
            // back rather than silently disabling the distance preference.
            if (DistanceHalfTiles <= 0.0)
            {
                DistanceHalfTiles = 150.0;
            }

            if (HomeBias <= 0.0)
            {
                HomeBias = 2.5;
            }

            if (Towns == null)
            {
                Towns = new string[0];
            }

            for (int i = 0; i < Towns.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(Towns[i]))
                {
                    errors.Add("destinations.towns has a blank entry at index {0}.", i);
                    continue;
                }

                for (int j = 0; j < i; j++)
                {
                    if (Insensitive.Equals(Towns[i], Towns[j]))
                    {
                        errors.Add("destinations.towns lists '{0}' twice.", Towns[i]);
                        break;
                    }
                }
            }

            Validate(errors, ByType, "destinations.byType");
            Validate(errors, ByTag, "destinations.byTag");

            ValidateSingleMinded(errors);
        }

        /// <summary>
        /// Keyed the other way round from byType - class first, then type - because the table is
        /// one class's whole answer. The class key must be a rollable class (there is no
        /// single-minded "default"), every weight non-negative, and `otherwise` present: a
        /// single-minded class with no answer for the rest of the world would fall back to 1.0
        /// everywhere, which is the opposite of what the table exists to say.
        ///
        /// Rebuilt case-insensitive, because Newtonsoft fills the inner dictionaries with the
        /// default comparer and every lookup here is by a type token the graph may spell in any case.
        /// </summary>
        private void ValidateSingleMinded(ConfigErrors errors)
        {
            var rebuilt = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

            if (SingleMinded != null)
            {
                foreach (var entry in SingleMinded)
                {
                    if (String.IsNullOrWhiteSpace(entry.Key) || !IsRollableClass(entry.Key))
                    {
                        errors.Add(
                            "destinations.singleMinded names '{0}', which is not a bot class. Use one of the 17 rollable classes.",
                            entry.Key);
                        continue;
                    }

                    if (entry.Value == null || !entry.Value.ContainsKey(OtherwiseKey))
                    {
                        errors.Add(
                            "destinations.singleMinded.{0} has no '{1}' weight, so it would say nothing about every type it does not name.",
                            entry.Key,
                            OtherwiseKey);
                        continue;
                    }

                    var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                    foreach (var weight in entry.Value)
                    {
                        if (String.IsNullOrWhiteSpace(weight.Key))
                        {
                            errors.Add("destinations.singleMinded.{0} has a blank type key.", entry.Key);
                            continue;
                        }

                        if (weight.Value < 0.0)
                        {
                            errors.Add(
                                "destinations.singleMinded.{0}.{1} is negative ({2}). Use 0 to exclude.",
                                entry.Key,
                                weight.Key,
                                weight.Value);
                        }

                        weights[weight.Key] = weight.Value;
                    }

                    rebuilt[entry.Key] = weights;
                }
            }

            SingleMinded = rebuilt;
        }

        /// <summary>
        /// A misspelled class name is a load failure, because it is silent otherwise: the class
        /// simply falls through to "default" and the shard runs for ever with a weighting nobody
        /// asked for. The key vocabulary is the 17 rollable classes plus "default".
        /// </summary>
        private static void Validate(
            ConfigErrors errors,
            Dictionary<string, Dictionary<string, double>> table,
            string where)
        {
            foreach (var entry in table)
            {
                if (String.IsNullOrWhiteSpace(entry.Key))
                {
                    errors.Add("{0} has a blank key.", where);
                    continue;
                }

                if (entry.Value == null)
                {
                    errors.Add("{0}.{1} has no weights.", where, entry.Key);
                    continue;
                }

                foreach (var weight in entry.Value)
                {
                    string cls = weight.Key;

                    if (String.IsNullOrWhiteSpace(cls))
                    {
                        errors.Add("{0}.{1} has a blank class key.", where, entry.Key);
                        continue;
                    }

                    if (!Insensitive.Equals(cls, DefaultKey) && !IsRollableClass(cls))
                    {
                        errors.Add(
                            "{0}.{1} names '{2}', which is not a bot class. Use one of the 17 rollable classes, or '{3}'.",
                            where,
                            entry.Key,
                            cls,
                            DefaultKey);
                    }

                    if (weight.Value < 0.0)
                    {
                        errors.Add(
                            "{0}.{1}.{2} is negative ({3}). Use 0 to exclude.",
                            where,
                            entry.Key,
                            cls,
                            weight.Value);
                    }
                }
            }
        }

        private static bool IsRollableClass(string name)
        {
            foreach (BotClass cls in BotClassHelper.Rollable())
            {
                if (Insensitive.Equals(cls.ToString(), name))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Type and tag keys that name nothing in the loaded navigation graph.
        ///
        /// A WARNING, not an error, and deliberately so: a weight for a `dungeon` type is not
        /// wrong on a shard that has not authored a dungeon yet, and failing the load would mean
        /// the weights could never be written before the places they describe. But a key that
        /// matches nothing does nothing, and a silent no-op in a weighting table is how a Smith
        /// ends up wandering into bakeries.
        /// </summary>
        public List<string> UnknownKeys()
        {
            var unknown = new List<string>();

            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NavDestination destination in NavigationSystem.Store.Destinations)
            {
                if (!String.IsNullOrEmpty(destination.Type))
                {
                    types.Add(destination.Type);
                }

                string[] destinationTags = destination.TagList;

                if (destinationTags != null)
                {
                    for (int i = 0; i < destinationTags.Length; i++)
                    {
                        tags.Add(destinationTags[i]);
                    }
                }
            }

            foreach (string key in ByType.Keys)
            {
                if (!types.Contains(key))
                {
                    unknown.Add("byType." + key);
                }
            }

            foreach (string key in ByTag.Keys)
            {
                if (!tags.Contains(key))
                {
                    unknown.Add("byTag." + key);
                }
            }

            // A single-minded class bypasses byType and byTag, so any entry it still has there is
            // a dead key - a number somebody may re-tune for ever to no effect. And a type its own
            // table names that the graph has none of is the same silent no-op as above.
            foreach (var entry in SingleMinded)
            {
                foreach (string key in entry.Value.Keys)
                {
                    if (!Insensitive.Equals(key, OtherwiseKey) && !types.Contains(key))
                    {
                        unknown.Add("singleMinded." + entry.Key + "." + key);
                    }
                }

                AddDeadKeys(unknown, ByType, "byType", entry.Key);
                AddDeadKeys(unknown, ByTag, "byTag", entry.Key);
            }

            // A town nothing is tagged with is a home nobody can be biased toward - the same
            // silent no-op as a weight key that matches nothing.
            if (Towns != null)
            {
                foreach (string town in Towns)
                {
                    if (!String.IsNullOrWhiteSpace(town) && !tags.Contains(town))
                    {
                        unknown.Add("towns." + town);
                    }
                }
            }

            unknown.Sort(StringComparer.Ordinal);

            return unknown;
        }

        private static void AddDeadKeys(
            List<string> unknown,
            Dictionary<string, Dictionary<string, double>> table,
            string where,
            string className)
        {
            foreach (var entry in table)
            {
                if (entry.Value == null)
                {
                    continue;
                }

                foreach (string cls in entry.Value.Keys)
                {
                    if (Insensitive.Equals(cls, className))
                    {
                        unknown.Add(where + "." + entry.Key + "." + cls + " (dead: " + className + " is single-minded)");
                    }
                }
            }
        }

        /// <summary>
        /// The weight this class gives this destination. Zero means never.
        /// </summary>
        public double WeightFor(NavDestination destination, BotClass cls)
        {
            if (destination == null)
            {
                return 0.0;
            }

            string className = cls.ToString();

            Dictionary<string, double> single;

            if (SingleMinded != null && SingleMinded.TryGetValue(className, out single) && single != null)
            {
                return SingleMindedWeight(single, destination.Type);
            }

            double weight = Lookup(ByType, destination.Type, className);

            if (weight <= 0.0)
            {
                return 0.0;
            }

            string[] tags = destination.TagList;

            if (tags != null)
            {
                for (int i = 0; i < tags.Length; i++)
                {
                    weight *= Lookup(ByTag, tags[i], className);

                    if (weight <= 0.0)
                    {
                        return 0.0;
                    }
                }
            }

            return weight;
        }

        /// <summary>
        /// A single-minded class's weight for a type. See SingleMinded for why a type byType
        /// excludes by default stays excluded unless this class's own table names it.
        /// </summary>
        private double SingleMindedWeight(Dictionary<string, double> single, string type)
        {
            double weight;

            if (!String.IsNullOrEmpty(type) && single.TryGetValue(type, out weight))
            {
                return weight;
            }

            if (Lookup(ByType, type, DefaultKey) <= 0.0)
            {
                return 0.0;
            }

            return single.TryGetValue(OtherwiseKey, out weight) ? weight : 0.0;
        }

        /// <summary>
        /// One table's contribution. An absent key, or a present key with neither the class nor a
        /// default, contributes 1.0 - "no opinion", which must not silently mean "no".
        /// </summary>
        private static double Lookup(
            Dictionary<string, Dictionary<string, double>> table, string key, string className)
        {
            if (table == null || String.IsNullOrEmpty(key))
            {
                return 1.0;
            }

            Dictionary<string, double> weights;

            if (!table.TryGetValue(key, out weights) || weights == null)
            {
                return 1.0;
            }

            double weight;

            if (weights.TryGetValue(className, out weight))
            {
                return weight;
            }

            if (weights.TryGetValue(DefaultKey, out weight))
            {
                return weight;
            }

            return 1.0;
        }
    }
}
