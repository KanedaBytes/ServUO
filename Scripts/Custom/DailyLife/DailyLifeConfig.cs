using System;
using System.Collections.Generic;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// Root of Data/Custom/britain-daily-life.json.
    ///
    /// EVERY LOCATION IN THIS FILE IS A NAV ID, including the clock anchor. There are no raw
    /// coordinates anywhere; a place is named, and where it is lives in navigation.json.
    ///
    /// Validate() reports STRUCTURAL problems only, and those fail the load, keeping the live
    /// config. A dangling NAV id is not fatal: it is collected when the indexes are built and
    /// reported by the DailyLife.Config health check, so one typo cannot take the town offline.
    /// </summary>
    public class DailyLifeStore : IValidatableConfig
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        /// <summary>
        /// Nav id whose location is fed to Clock.GetTime. A destination or a waypoint.
        /// </summary>
        [JsonProperty("anchor")]
        public string Anchor { get; set; }

        [JsonProperty("tavern")]
        public TavernConfig Tavern { get; set; }

        /// <summary>Lines tavern patrons say. Free text, so a real JSON array, not tokens.</summary>
        [JsonProperty("chatter")]
        public List<string> Chatter { get; set; }

        /// <summary>Lines the night watch calls out.</summary>
        [JsonProperty("watchChatter")]
        public List<string> WatchChatter { get; set; }

        [JsonProperty("watch")]
        public List<WatchPostConfig> Watch { get; set; }

        [JsonProperty("townsfolk")]
        public List<TownsfolkConfig> Townsfolk { get; set; }

        [JsonProperty("shopkeepers")]
        public List<ShopkeeperConfig> Shopkeepers { get; set; }

        [JsonConstructor]
        public DailyLifeStore()
        {
            SchemaVersion = DailyLifeSystem.SchemaVersion;
            Chatter = new List<string>();
            WatchChatter = new List<string>();
            Watch = new List<WatchPostConfig>();
            Townsfolk = new List<TownsfolkConfig>();
            Shopkeepers = new List<ShopkeeperConfig>();
        }

        public void Validate(ConfigErrors errors)
        {
            if (SchemaVersion > DailyLifeSystem.SchemaVersion)
            {
                errors.Add(
                    "schemaVersion {0} is newer than this build understands ({1}).",
                    SchemaVersion,
                    DailyLifeSystem.SchemaVersion);
            }

            if (!NavIds.IsValid(Anchor))
            {
                errors.Add("'anchor' must be a nav id (found '{0}')", Anchor);
            }

            ValidateTavern(errors);
            ValidateWatch(errors);
            ValidateTownsfolk(errors);
            ValidateShopkeepers(errors);
        }

        private void ValidateTavern(ConfigErrors errors)
        {
            if (Tavern == null)
            {
                errors.Add("'tavern' section is missing");
                return;
            }

            if (!NavIds.IsValid(Tavern.Destination))
            {
                errors.Add("tavern.destination must be a nav id (found '{0}')", Tavern.Destination);
            }

            if (Tavern.PatronCount < 0)
            {
                errors.Add("tavern.patronCount must not be negative (found {0})", Tavern.PatronCount);
            }
        }

        private void ValidateWatch(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Watch.Count; i++)
            {
                WatchPostConfig post = Watch[i];
                string where = String.Format("watch[{0}]", i);

                if (post == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, post.Id, seen);

                bool hasRoute = !String.IsNullOrWhiteSpace(post.Route);
                bool hasDestinations = NavIds.Split(post.Destinations).Length > 0;

                // Exactly one. Both would be ambiguous; neither is a post that does nothing.
                if (hasRoute == hasDestinations)
                {
                    errors.Add(
                        "{0} must have exactly one of 'route' or 'destinations' (found {1})",
                        where,
                        hasRoute ? "both" : "neither");
                }

                if (hasRoute && !NavIds.IsValid(post.Route))
                {
                    errors.Add("{0} route '{1}' is not a valid nav id", where, post.Route);
                }

                ValidateTokens(errors, where + " destinations", post.Destinations);
            }
        }

        private void ValidateTownsfolk(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Townsfolk.Count; i++)
            {
                TownsfolkConfig entry = Townsfolk[i];
                string where = String.Format("townsfolk[{0}]", i);

                if (entry == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, entry.Id, seen);

                if (!NavIds.IsValid(entry.Route))
                {
                    errors.Add("{0} route '{1}' is not a valid nav id", where, entry.Route);
                }

                if (!entry.BodyIsKnown)
                {
                    errors.Add(
                        "{0} body '{1}'; expected male, female or random", where, entry.Body);
                }
            }
        }

        private void ValidateShopkeepers(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spawners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Shopkeepers.Count; i++)
            {
                ShopkeeperConfig shop = Shopkeepers[i];
                string where = String.Format("shopkeepers[{0}]", i);

                if (shop == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, shop.Id, seen);

                // The spawner name is the identity at runtime, so two entries naming one spawner
                // would fight over the same vendor and the loser would never come home.
                if (String.IsNullOrWhiteSpace(shop.Spawner))
                {
                    errors.Add("{0} has no spawner name", where);
                }
                else if (!spawners.Add(shop.Spawner))
                {
                    errors.Add("{0} duplicates the spawner '{1}'", where, shop.Spawner);
                }

                if (String.IsNullOrWhiteSpace(shop.StockSpawner))
                {
                    errors.Add("{0} has no stockSpawner name", where);
                }

                if (!NavIds.IsValid(shop.Shop))
                {
                    errors.Add("{0} shop '{1}' is not a valid nav id", where, shop.Shop);
                }

                if (!NavIds.IsValid(shop.Home))
                {
                    errors.Add("{0} home '{1}' is not a valid nav id", where, shop.Home);
                }

                ValidateVendorType(errors, where, shop.Vendor);
            }
        }

        /// <summary>
        /// Vendors are matched at runtime by their spawner, but the type name still has to
        /// resolve - a typo produces a shopkeeper that never exists and never logs.
        /// </summary>
        private static void ValidateVendorType(ConfigErrors errors, string where, string typeName)
        {
            if (String.IsNullOrWhiteSpace(typeName))
            {
                errors.Add("{0} has an entry with no vendor type name", where);
                return;
            }

            Type type = ScriptCompiler.FindTypeByName(typeName);

            if (type == null)
            {
                errors.Add("{0} '{1}' is not a known type", where, typeName);
                return;
            }

            if (!typeof(Server.Mobiles.BaseVendor).IsAssignableFrom(type))
            {
                errors.Add("{0} '{1}' is not a BaseVendor", where, typeName);
            }
        }

        private static void ValidateId(ConfigErrors errors, string where, string id, HashSet<string> seen)
        {
            if (!NavIds.IsValid(id))
            {
                errors.Add("{0} has an invalid or missing id '{1}'", where, id);
                return;
            }

            if (!seen.Add(id))
            {
                errors.Add("{0} duplicates the id '{1}'", where, id);
            }
        }

        private static void ValidateTokens(ConfigErrors errors, string where, string tokens)
        {
            string offender;

            if (!NavIds.AllValid(tokens, out offender))
            {
                errors.Add("{0} contains the invalid token '{1}'", where, offender);
            }
        }
    }

    public class TavernConfig
    {
        [JsonProperty("destination")]
        public string Destination { get; set; }

        [JsonProperty("patronCount")]
        public int PatronCount { get; set; }

        [JsonConstructor]
        public TavernConfig()
        {
            PatronCount = 4;
        }
    }

    /// <summary>
    /// One night-watch post. Exactly one of Route or Destinations.
    ///
    /// A route id is an authored patrol line. One destination is a stationary post standing at
    /// an exclusive arrival point. Two or more is a patrol routed between them, looping.
    /// </summary>
    public class WatchPostConfig
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("route", NullValueHandling = NullValueHandling.Ignore)]
        public string Route { get; set; }

        [JsonProperty("destinations", NullValueHandling = NullValueHandling.Ignore)]
        public string Destinations { get; set; }

        [JsonIgnore]
        public string[] DestinationList { get; private set; }

        [JsonIgnore]
        public bool IsPatrolRoute
        {
            get { return !String.IsNullOrWhiteSpace(Route); }
        }

        public void Bind()
        {
            DestinationList = NavIds.Split(Destinations);
        }

        public override string ToString()
        {
            return String.Format("{0} ({1})", Id, IsPatrolRoute ? Route : Destinations);
        }
    }

    public class TownsfolkConfig
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)]
        public string Title { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }

        [JsonProperty("route")]
        public string Route { get; set; }

        [JsonConstructor]
        public TownsfolkConfig()
        {
            Body = "random";
        }

        [JsonIgnore]
        public bool BodyIsKnown
        {
            get
            {
                return Insensitive.Equals(Body, "male")
                    || Insensitive.Equals(Body, "female")
                    || Insensitive.Equals(Body, "random");
            }
        }

        public override string ToString()
        {
            return String.Format("{0} '{1}' on {2}", Id, Name, Route);
        }
    }

    /// <summary>
    /// One managed shopkeeper.
    ///
    /// Keyed by SPAWNER NAME, not vendor type: two bakers must be able to have different homes,
    /// and the ModernUO original's "find a vendor of this type within 8 tiles of the shop" could
    /// not tell them apart - nor find one that had walked home.
    /// </summary>
    public class ShopkeeperConfig
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>The GG_ spawner that produces this vendor. The runtime identity.</summary>
        [JsonProperty("spawner")]
        public string Spawner { get; set; }

        /// <summary>The upstream spawner this replaces. What [GG_MigrateVendors disables.</summary>
        [JsonProperty("stockSpawner")]
        public string StockSpawner { get; set; }

        [JsonProperty("vendor")]
        public string Vendor { get; set; }

        [JsonProperty("shop")]
        public string Shop { get; set; }

        [JsonProperty("home")]
        public string Home { get; set; }

        /// <summary>False for a shop that stays open after dark, like the healer.</summary>
        [JsonProperty("closes")]
        public bool Closes { get; set; }

        [JsonConstructor]
        public ShopkeeperConfig()
        {
            Closes = true;
        }

        public override string ToString()
        {
            return String.Format("{0} ({1}: {2} -> {3})", Id, Vendor, Shop, Home);
        }
    }
}
