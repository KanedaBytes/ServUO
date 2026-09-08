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
    /// </summary>
    public class BotDestinationConfig : IValidatableConfig
    {
        [JsonProperty("byType")]
        public Dictionary<string, Dictionary<string, double>> ByType { get; set; }

        [JsonProperty("byTag")]
        public Dictionary<string, Dictionary<string, double>> ByTag { get; set; }

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
