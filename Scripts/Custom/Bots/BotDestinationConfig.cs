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
