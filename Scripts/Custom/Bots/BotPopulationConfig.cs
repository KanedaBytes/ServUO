// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPopulationConfig.cs — the "population" block of Data/Custom/bots.json.
//
// Upstream keeps every number here in code: BotPopulation.TargetCount is a
// static field (uo-offline BotPopulation.cs:150) and the per-city weights are a
// CityRegion[] literal (GenerateBotsCommand.cs:49-68). Both live here instead,
// for the reason every other number in this file does - a shard's population is
// a thing an operator tunes and measures, and a rebuild is not a tuning loop.
//
// bots.json rather than Config/Custom.cfg because this is structured data with a
// failure contract: BotSystem keeps the last good config on any load error, so a
// bad edit cannot leave the population half-applied. Only the on/off switch and
// the tick cadence are .cfg keys, which is where BotTickSeconds and
// BotLifecycleSeconds already are.

using System;
using System.Collections.Generic;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// How many bots the world holds, how they are split between towns, and how the day's curve
    /// moves that target about.
    /// </summary>
    public class BotPopulationConfig : IValidatableConfig
    {
        /// <summary>Upstream's own bounds on [SetBotPopulation (uo-offline SetBotPopulationCommand.cs:21-22).</summary>
        public const int MaxTarget = 5000;

        /// <summary>
        /// The default, and deliberately not upstream's 1600.
        ///
        /// This graph has 115 arrival points in total - Britain 88, Trinsic 27 - and bots.json's own
        /// rule is that a site's capacity IS its authored arrival count. 60 fills every pinned role
        /// (about 40 of them) and leaves a roaming remainder, on a 2-second LiveRegistry tick on
        /// net48. The port survey's instruction for this number was "start at a fraction of it and
        /// measure", and Bots.Recipe reports the tick cost beside the live count so the next value
        /// is arithmetic rather than a guess.
        /// </summary>
        public const int DefaultTarget = 60;

        /// <summary>
        /// uo-offline's HourCurve, verbatim (BotSessionManager.cs:51-57): the fraction of the target
        /// online at each LOCAL hour. Dead at 05:00, packed at 19:00-20:00.
        /// </summary>
        private static readonly double[] DefaultCurve =
        {
            0.55, 0.40, 0.30, 0.22, 0.18, 0.15,   // 00-05  night collapse
            0.20, 0.30, 0.40, 0.50, 0.55, 0.60,   // 06-11  morning climb
            0.65, 0.65, 0.65, 0.70, 0.75, 0.85,   // 12-17  afternoon
            0.95, 1.00, 1.00, 0.95, 0.85, 0.70    // 18-23  evening peak
        };

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        /// <summary>The world's live bot target at the curve's peak.</summary>
        [JsonProperty("target")]
        public int Target { get; set; }

        /// <summary>
        /// Each town's share of the target. EMPTY MEANS DERIVED, which is the intended state.
        ///
        /// Left empty, a town's share is its share of the graph's destinations - which is the rule
        /// BotHomeTowns.Roll already uses to decide where a bot is born. Writing the numbers down
        /// separately would be two statements of the same fact, free to drift; deriving it means a
        /// bot is born where the population is because both read the same graph. Upstream writes
        /// them down twice and they have drifted: its city weights bear no relation to its
        /// destination counts.
        /// </summary>
        [JsonProperty("perTown")]
        public Dictionary<string, double> PerTown { get; set; }

        /// <summary>
        /// Bots per roaming spawner. Upstream's is 12 (BotPopulation.cs:168) against a target of
        /// 1600; 6 against 60 keeps the same "many small spawners with room to place" property its
        /// comment argues for, which is what stops a spawner abandoning bots it cannot fit.
        /// </summary>
        [JsonProperty("perSpawner")]
        public int PerSpawner { get; set; }

        /// <summary>
        /// A roaming spawner's home range in tiles. Upstream uses 18 for roamers and 3 for the
        /// pinned crowds (BotPopulation.cs:173, GenerateBotsCommand.cs:306); the pinned figure is
        /// not configurable here because a pinned spawner's job is to put a bot ON its arrival
        /// point, and a range is what would stop it.
        /// </summary>
        [JsonProperty("spawnerRange")]
        public int SpawnerRange { get; set; }

        /// <summary>Respawn window in minutes, [min, max]. Upstream's is 5 to 15 (GenerateBotsCommand.cs:526-527).</summary>
        [JsonProperty("respawnMinutes")]
        public double[] RespawnMinutes { get; set; }

        /// <summary>Twenty-four fractions of Target, one per local hour. Absent means upstream's.</summary>
        [JsonProperty("curve", NullValueHandling = NullValueHandling.Ignore)]
        public double[] Curve { get; set; }

        /// <summary>How long a bot plays before saying goodbye, in minutes, [min, max].</summary>
        [JsonProperty("sessionMinutes")]
        public int[] SessionMinutes { get; set; }

        /// <summary>
        /// Which behaviour each kind of recipe slot seeds.
        ///
        /// Four keys: "bank", "shop", "station", "roam". Changing one is how an operator says "let
        /// shops seed travellers instead of shoppers" without a rebuild; a value naming no
        /// behaviour is a validation error rather than a silent Idle.
        /// </summary>
        [JsonProperty("roles")]
        public Dictionary<string, string> Roles { get; set; }

        public const string RoleBank = "bank";
        public const string RoleShop = "shop";
        public const string RoleStation = "station";
        public const string RoleRoam = "roam";

        private static readonly string[] RoleKeys = { RoleBank, RoleShop, RoleStation, RoleRoam };

        private static readonly string[] RoleDefaults = { "BankSitter", "Shopper", "Crafter", "Traveler" };

        [JsonConstructor]
        public BotPopulationConfig()
        {
            Target = DefaultTarget;
            PerTown = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            PerSpawner = 6;
            SpawnerRange = 12;
            RespawnMinutes = new[] { 5.0, 15.0 };
            SessionMinutes = new[] { 60, 240 };
            Roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < RoleKeys.Length; i++)
            {
                Roles[RoleKeys[i]] = RoleDefaults[i];
            }
        }

        // ---- reading ----

        /// <summary>The behaviour a recipe slot seeds, falling back to the built-in default.</summary>
        public string RoleFor(string slot)
        {
            string behaviour;

            if (Roles != null && slot != null && Roles.TryGetValue(slot, out behaviour)
                && !String.IsNullOrWhiteSpace(behaviour))
            {
                return behaviour.Trim();
            }

            for (int i = 0; i < RoleKeys.Length; i++)
            {
                if (Insensitive.Equals(RoleKeys[i], slot))
                {
                    return RoleDefaults[i];
                }
            }

            return "Traveler";
        }

        /// <summary>The curve's multiplier at a local hour, clamped into the day.</summary>
        public double CurveAt(int hour)
        {
            // A FLAT CURVE UNDER THE MEASUREMENT PROFILE. The curve's whole job is to make 05:00
            // quieter than 19:00, which is exactly what a measurement must not have: a window that
            // straddles a shoulder of it measures the clock as much as the roads.
            if (BotMeasurementProfile.Enabled)
            {
                return 1.0;
            }

            double[] curve = Curve != null && Curve.Length == 24 ? Curve : DefaultCurve;

            if (hour < 0)
            {
                hour = 0;
            }
            else if (hour > 23)
            {
                hour = 23;
            }

            return curve[hour];
        }

        public TimeSpan RespawnMin
        {
            get { return TimeSpan.FromMinutes(RespawnMinutes != null && RespawnMinutes.Length > 0 ? RespawnMinutes[0] : 5.0); }
        }

        public TimeSpan RespawnMax
        {
            get { return TimeSpan.FromMinutes(RespawnMinutes != null && RespawnMinutes.Length > 1 ? RespawnMinutes[1] : 15.0); }
        }

        public int SessionMinMinutes
        {
            get { return SessionMinutes != null && SessionMinutes.Length > 0 ? SessionMinutes[0] : 60; }
        }

        public int SessionMaxMinutes
        {
            get { return SessionMinutes != null && SessionMinutes.Length > 1 ? SessionMinutes[1] : 240; }
        }

        /// <summary>
        /// Each town's share of the target, normalised to sum to 1.
        ///
        /// `counts` is how many destinations each town has, which is the derived default's whole
        /// basis. An explicit perTown entry wins for the towns that name one; a town the file does
        /// not mention still gets its derived share, so half-specifying is allowed and means what
        /// it looks like it means.
        /// </summary>
        public Dictionary<string, double> Shares(IList<string> towns, IDictionary<string, int> counts)
        {
            var shares = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (towns == null || towns.Count == 0)
            {
                return shares;
            }

            double total = 0.0;

            foreach (string town in towns)
            {
                double weight;

                if (PerTown == null || !PerTown.TryGetValue(town, out weight))
                {
                    int count;
                    weight = counts != null && counts.TryGetValue(town, out count) ? count : 0.0;
                }

                if (weight < 0.0)
                {
                    weight = 0.0;
                }

                shares[town] = weight;
                total += weight;
            }

            if (total <= 0.0)
            {
                // Nothing to go on - an even split beats every bot in one town.
                foreach (string town in towns)
                {
                    shares[town] = 1.0 / towns.Count;
                }

                return shares;
            }

            foreach (string town in towns)
            {
                shares[town] = shares[town] / total;
            }

            return shares;
        }

        // ---- validation ----

        public void Validate(ConfigErrors errors)
        {
            if (Target < 0 || Target > MaxTarget)
            {
                errors.Add("population.target {0} is outside 0..{1}.", Target, MaxTarget);
            }

            if (PerSpawner < 1)
            {
                errors.Add("population.perSpawner must be at least 1; it is {0}.", PerSpawner);
            }

            if (SpawnerRange < 0)
            {
                errors.Add("population.spawnerRange cannot be negative; it is {0}.", SpawnerRange);
            }

            ValidateWindow(errors, "population.respawnMinutes", RespawnMinutes);

            if (SessionMinutes == null || SessionMinutes.Length != 2)
            {
                errors.Add("population.sessionMinutes must be [min, max].");
            }
            else if (SessionMinutes[0] < 1 || SessionMinutes[1] < SessionMinutes[0])
            {
                errors.Add(
                    "population.sessionMinutes [{0}, {1}] is not an ascending pair of positive minutes.",
                    SessionMinutes[0],
                    SessionMinutes[1]);
            }

            if (Curve != null && Curve.Length != 24)
            {
                errors.Add("population.curve has {0} value(s); it needs one per hour, so 24.", Curve.Length);
            }
            else if (Curve != null)
            {
                for (int hour = 0; hour < Curve.Length; hour++)
                {
                    if (Curve[hour] < 0.0 || Curve[hour] > 1.0)
                    {
                        errors.Add(
                            "population.curve[{0}] is {1}; it is a fraction of the target and must be 0..1.",
                            hour,
                            Curve[hour]);
                    }
                }
            }

            if (PerTown != null)
            {
                foreach (var entry in PerTown)
                {
                    if (entry.Value < 0.0)
                    {
                        errors.Add("population.perTown['{0}'] is negative.", entry.Key);
                    }
                }
            }

            if (Roles != null)
            {
                foreach (var entry in Roles)
                {
                    if (Array.IndexOf(RoleKeys, entry.Key.ToLowerInvariant()) < 0)
                    {
                        errors.Add(
                            "population.roles has no slot '{0}'. The slots are {1}.",
                            entry.Key,
                            String.Join(", ", RoleKeys));
                    }

                    // A behaviour name that answers to nothing would fall back to Idle at spawn
                    // time and produce a population that stands still - the exact failure this
                    // config is meant to make impossible to reach by accident.
                    if (!BotBehaviors.IsKnown(entry.Value))
                    {
                        errors.Add(
                            "population.roles['{0}'] names behaviour '{1}', which does not exist. Known: {2}.",
                            entry.Key,
                            entry.Value,
                            String.Join(", ", BotBehaviors.Names()));
                    }
                }
            }
        }

        /// <summary>
        /// Towns named in perTown that are not towns. Reported by Bots.Population rather than
        /// failing the load, exactly as the destination-weight keys are: a stale town name does
        /// nothing, and doing nothing quietly is the part worth saying out loud.
        /// </summary>
        public List<string> UnknownTowns(IList<string> towns)
        {
            var unknown = new List<string>();

            if (PerTown == null)
            {
                return unknown;
            }

            foreach (var entry in PerTown)
            {
                bool known = false;

                if (towns != null)
                {
                    foreach (string town in towns)
                    {
                        if (Insensitive.Equals(town, entry.Key))
                        {
                            known = true;
                            break;
                        }
                    }
                }

                if (!known)
                {
                    unknown.Add(entry.Key);
                }
            }

            unknown.Sort(StringComparer.Ordinal);

            return unknown;
        }

        private static void ValidateWindow(ConfigErrors errors, string name, double[] window)
        {
            if (window == null || window.Length != 2)
            {
                errors.Add("{0} must be [min, max].", name);
                return;
            }

            if (window[0] <= 0.0 || window[1] < window[0])
            {
                errors.Add("{0} [{1}, {2}] is not an ascending pair of positive minutes.", name, window[0], window[1]);
            }
        }
    }
}
