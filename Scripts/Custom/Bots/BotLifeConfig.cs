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
    /// Optional clamps on how long a lifecycle phase lasts, per behaviour.
    ///
    /// **Absent is the production case.** With no entry, the duration is whatever
    /// BotPersonality rolled for that bot and nothing here interferes. The clamps exist so a test
    /// run can force churn in minutes rather than hours, and so a shard that wants a faster pulse
    /// can have one without touching code.
    /// </summary>
    public class BotPhaseClamp : IValidatableConfig
    {
        [JsonProperty("minSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinSeconds { get; set; }

        [JsonProperty("maxSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxSeconds { get; set; }

        [JsonConstructor]
        public BotPhaseClamp()
        {
        }

        public void Validate(ConfigErrors errors)
        {
            Validate(errors, "phases");
        }

        public void Validate(ConfigErrors errors, string where)
        {
            if (MinSeconds.HasValue && MinSeconds.Value <= 0)
            {
                errors.Add("{0}.minSeconds must be positive (got {1}).", where, MinSeconds.Value);
            }

            if (MaxSeconds.HasValue && MaxSeconds.Value <= 0)
            {
                errors.Add("{0}.maxSeconds must be positive (got {1}).", where, MaxSeconds.Value);
            }

            if (MinSeconds.HasValue && MaxSeconds.HasValue && MinSeconds.Value > MaxSeconds.Value)
            {
                errors.Add(
                    "{0}.minSeconds ({1}) is greater than maxSeconds ({2}).",
                    where,
                    MinSeconds.Value,
                    MaxSeconds.Value);
            }
        }

        /// <summary>Apply this clamp to a rolled duration.</summary>
        public TimeSpan Apply(TimeSpan duration)
        {
            if (MinSeconds.HasValue && duration < TimeSpan.FromSeconds(MinSeconds.Value))
            {
                duration = TimeSpan.FromSeconds(MinSeconds.Value);
            }

            if (MaxSeconds.HasValue && duration > TimeSpan.FromSeconds(MaxSeconds.Value))
            {
                duration = TimeSpan.FromSeconds(MaxSeconds.Value);
            }

            return duration;
        }
    }

    /// <summary>
    /// What a bot that has ARRIVED does with its feet, and how often it does anything at all.
    ///
    /// These are here rather than as constants because they are the numbers somebody re-tunes by
    /// LOOKING: "do they read as people waiting or as statues" is not a question a test can answer,
    /// and a stop-rebuild-restart between each look is what stops anybody asking it twice. A change
    /// here plus [BotsReload is the whole loop.
    ///
    /// Upstream keeps the same four as C# literals, and ours are theirs: a 3% idle beat
    /// (BankSitterBehavior.cs:330), HomeRadius 1 for the walk-back (:58), a 15% facing shift for
    /// the shopper (ShopperBehavior.cs:113-122), and a 4-9 second turn for an arrived Traveler with
    /// nothing to do (TravelerBehavior.cs:2727-2732). Ours is 5% rather than 3% because our tick is
    /// the same 2 seconds and the beat had already been ported at 5%; it is a feel dial, not a
    /// translation, and it is the one most likely to move.
    ///
    /// WHAT NONE OF THEM CONTROLS is the fidgeting this section was written for. That was
    /// BaseAI.WalkRandomInHome running underneath every behaviour, and it is suppressed in
    /// PlayerBot.CheckIdle rather than tuned - a wander nobody asked for is not a number to lower.
    /// </summary>
    public class BotIdleConfig : IValidatableConfig
    {
        /// <summary>
        /// How far a settled bot may stand from its pinned tile before the engine walks it back.
        ///
        /// Upstream's `HomeRadius = 1` (BankSitterBehavior.cs:58), whose comment is "I got shoved":
        /// the tolerance exists so a sitter nudged one tile does not march back and re-jam the tile
        /// that nudged it. A crafter overrides this to 0 from its own behaviour - its tile was
        /// chosen for crafting reach and one step off is one step too far.
        /// </summary>
        [JsonProperty("sitterTolerance")]
        public int SitterTolerance { get; set; }

        /// <summary>Chance per tick that an idle sitter does something - a gesture or a look.</summary>
        [JsonProperty("beatChance")]
        public double BeatChance { get; set; }

        /// <summary>Of those beats, the share that is the bank-box bend rather than a look.</summary>
        [JsonProperty("bankBoxShare")]
        public double BankBoxShare { get; set; }

        /// <summary>Chance per tick that a paused shopper turns, as if looking over the goods.</summary>
        [JsonProperty("shopperTurnChance")]
        public double ShopperTurnChance { get; set; }

        /// <summary>How long an arrived Traveler with nothing to do waits between turns, [min, max].</summary>
        [JsonProperty("turnSeconds")]
        public double[] TurnSeconds { get; set; }

        [JsonConstructor]
        public BotIdleConfig()
        {
            SitterTolerance = 1;
            BeatChance = 0.05;
            BankBoxShare = 0.35;
            ShopperTurnChance = 0.15;
            TurnSeconds = new[] { 4.0, 9.0 };
        }

        public TimeSpan MinTurn
        {
            get { return TimeSpan.FromSeconds(TurnSeconds != null && TurnSeconds.Length > 0 ? TurnSeconds[0] : 4.0); }
        }

        public TimeSpan MaxTurn
        {
            get { return TimeSpan.FromSeconds(TurnSeconds != null && TurnSeconds.Length > 1 ? TurnSeconds[1] : 9.0); }
        }

        public void Validate(ConfigErrors errors)
        {
            if (SitterTolerance < 0)
            {
                errors.Add("idle.sitterTolerance is {0}; it is a tile distance and cannot be negative.", SitterTolerance);
            }

            Probability(errors, "idle.beatChance", BeatChance);
            Probability(errors, "idle.bankBoxShare", BankBoxShare);
            Probability(errors, "idle.shopperTurnChance", ShopperTurnChance);

            if (TurnSeconds == null || TurnSeconds.Length != 2)
            {
                errors.Add("idle.turnSeconds must be [min, max].");
            }
            else if (TurnSeconds[0] <= 0.0 || TurnSeconds[1] < TurnSeconds[0])
            {
                errors.Add(
                    "idle.turnSeconds is [{0}, {1}]; min must be positive and max at least min.",
                    TurnSeconds[0],
                    TurnSeconds[1]);
            }
        }

        private static void Probability(ConfigErrors errors, string where, double value)
        {
            if (value < 0.0 || value > 1.0)
            {
                errors.Add("{0} is {1}; it is a probability and must be between 0 and 1.", where, value);
            }
        }
    }

    /// <summary>
    /// The lifecycle's data: phase clamps, the standing-crowd floors, and how often an arrival
    /// actually commits to staying.
    /// </summary>
    public class BotLifeConfig : IValidatableConfig
    {
        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        /// <summary>Optional per-behaviour phase clamps. Absent means unclamped.</summary>
        [JsonProperty("phases")]
        public Dictionary<string, BotPhaseClamp> Phases { get; set; }

        /// <summary>
        /// How many bots each destination of a type wants standing at it.
        ///
        /// Upstream had no such thing: their bank crowd was five permanent, lifecycle-exempt bots
        /// held by a spawner at every bank, and their lifecycle teleported extra sitters to a
        /// uniformly random bank with no occupancy check at all. This shard walks instead of
        /// teleporting, so the floor is expressed as a pull - an under-floor destination is simply
        /// somewhere bots want to go - and nobody is ever placed or commandeered.
        /// </summary>
        [JsonProperty("crowds")]
        public Dictionary<string, int> Crowds { get; set; }

        /// <summary>
        /// The chance that arriving at a destination of this type turns into staying there.
        ///
        /// Not every arrival commits, and that is the point. Upstream took a bank handoff 40% of
        /// the time - "many bank visitors just pass through" - and a shop 80%. A crowd where every
        /// arrival stops is a queue; the passers-by are what make it look like a town.
        /// </summary>
        [JsonProperty("handoff")]
        public Dictionary<string, double> Handoff { get; set; }

        /// <summary>
        /// How many bots a work site holds before it stops pulling, keyed by DESTINATION ID.
        ///
        /// The mirror image of Crowds, and deliberately the same shape read the other way: a
        /// floor says "fewer than this and the place pulls harder", a capacity says "more than
        /// this and it stops pulling at all". Keyed by id rather than by type because two faces
        /// of the same rock can hold different numbers of people, which is exactly the case the
        /// west mine presents.
        ///
        /// Absent is the production case: with no entry a site's capacity is its authored
        /// arrival-point count, which is how many bots can physically stand there.
        /// </summary>
        [JsonProperty("capacity")]
        public Dictionary<string, int> Capacity { get; set; }

        /// <summary>
        /// The handoff chance used when a bot arrives at the station its OWN class works,
        /// whatever the destination's type is.
        ///
        /// Upstream used 0.95 for both an artisan reaching its forge and a gatherer reaching its
        /// site, and it needs to override the per-type number rather than sit beside it: a Tailor
        /// arriving at the tailor shop is not a shopper browsing, and the 0.8 shop handoff would
        /// send it away one visit in five for no reason.
        /// </summary>
        [JsonProperty("station")]
        public double Station { get; set; }

        /// <summary>What an arrived bot does with its feet. See BotIdleConfig.</summary>
        [JsonProperty("idle")]
        public BotIdleConfig Idle { get; set; }

        [JsonConstructor]
        public BotLifeConfig()
        {
            Phases = new Dictionary<string, BotPhaseClamp>(StringComparer.OrdinalIgnoreCase);
            Crowds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Handoff = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Capacity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Station = 0.95;
            Idle = new BotIdleConfig();
        }

        public void Validate(ConfigErrors errors)
        {
            if (Phases == null)
            {
                Phases = new Dictionary<string, BotPhaseClamp>(StringComparer.OrdinalIgnoreCase);
            }

            if (Crowds == null)
            {
                Crowds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (Handoff == null)
            {
                Handoff = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            if (Capacity == null)
            {
                Capacity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (Station < 0.0 || Station > 1.0)
            {
                errors.Add("station is {0}; it is a probability and must be between 0 and 1.", Station);
            }

            // A present-but-null `idle` is treated as absent rather than as a fault, which is the
            // rule BotStore.Validate already applies to every section of the file.
            if (Idle == null)
            {
                Idle = new BotIdleConfig();
            }

            Idle.Validate(errors);

            foreach (var entry in Capacity)
            {
                if (entry.Value <= 0)
                {
                    errors.Add("capacity.{0} is {1}; it must be positive.", entry.Key, entry.Value);
                }
            }

            foreach (var entry in Phases)
            {
                if (entry.Value == null)
                {
                    errors.Add("phases.{0} has no clamp values.", entry.Key);
                    continue;
                }

                entry.Value.Validate(errors, "phases." + entry.Key);
            }

            foreach (var entry in Crowds)
            {
                if (entry.Value < 0)
                {
                    errors.Add("crowds.{0} is negative ({1}).", entry.Key, entry.Value);
                }
            }

            foreach (var entry in Handoff)
            {
                if (entry.Value < 0.0 || entry.Value > 1.0)
                {
                    errors.Add(
                        "handoff.{0} is {1}; it is a probability and must be between 0 and 1.",
                        entry.Key,
                        entry.Value);
                }
            }
        }

        /// <summary>The phase length for a behaviour: the bot's own roll, clamped if configured.</summary>
        public TimeSpan PhaseLength(string behaviourName, TimeSpan rolled)
        {
            BotPhaseClamp clamp;

            if (behaviourName != null && Phases.TryGetValue(behaviourName, out clamp) && clamp != null)
            {
                return clamp.Apply(rolled);
            }

            return rolled;
        }

        /// <summary>How many bots this destination type wants standing at it. Zero means no floor.</summary>
        public int FloorFor(string type)
        {
            int floor;

            return type != null && Crowds.TryGetValue(type, out floor) ? floor : 0;
        }

        /// <summary>
        /// The chance an arrival at this type commits. Absent means 0 - a type with no configured
        /// handoff has no behaviour to hand off to, so never staying is the correct default.
        /// </summary>
        public double HandoffChance(string type)
        {
            double chance;

            return type != null && Handoff.TryGetValue(type, out chance) ? chance : 0.0;
        }

        /// <summary>How many bots this site holds, or 0 when it is not configured.</summary>
        public int CapacityFor(string destinationId)
        {
            int capacity;

            return destinationId != null && Capacity.TryGetValue(destinationId, out capacity) ? capacity : 0;
        }

        /// <summary>
        /// Capacity entries naming a destination that is not on the graph.
        ///
        /// Same reasoning as UnknownPhaseKeys: it is not wrong to carry a number for a site a
        /// later session will author, but it does nothing today, and a silent no-op in a tuning
        /// table is worth saying out loud.
        /// </summary>
        public List<string> UnknownCapacityKeys()
        {
            var unknown = new List<string>();

            foreach (string key in Capacity.Keys)
            {
                if (Nav.Destination(key) == null)
                {
                    unknown.Add(key);
                }
            }

            unknown.Sort(StringComparer.Ordinal);

            return unknown;
        }

        /// <summary>
        /// Phase clamps naming a behaviour that is not registered.
        ///
        /// A warning, not an error: a clamp for a behaviour that has not landed yet is a note about
        /// the future rather than a mistake. But it does nothing, and a silent no-op in a tuning
        /// table is worth saying out loud.
        /// </summary>
        public List<string> UnknownPhaseKeys()
        {
            var unknown = new List<string>();

            foreach (string key in Phases.Keys)
            {
                if (!BotBehaviors.IsKnown(key))
                {
                    unknown.Add(key);
                }
            }

            unknown.Sort(StringComparer.Ordinal);

            return unknown;
        }

        /// <summary>
        /// Floors larger than the destination has authored arrival points.
        ///
        /// Arrival picking scatters, so more bots than points can physically stand there - but a
        /// floor above the authored spots means the crowd piles onto scattered offsets rather than
        /// the places somebody chose, which is the kind of thing you want to hear about while you
        /// still remember authoring them.
        /// </summary>
        public List<string> OvercrowdedDestinations()
        {
            var over = new List<string>();

            foreach (NavDestination destination in NavigationSystem.Store.Destinations)
            {
                int floor = FloorFor(destination.Type);

                if (floor <= 0)
                {
                    continue;
                }

                int points = destination.ArrivalList == null ? 0 : destination.ArrivalList.Count;

                if (floor > points)
                {
                    over.Add(String.Format(
                        "{0} wants {1} but has {2} arrival point(s)",
                        destination.Id,
                        floor,
                        points));
                }
            }

            over.Sort(StringComparer.Ordinal);

            return over;
        }
    }
}
