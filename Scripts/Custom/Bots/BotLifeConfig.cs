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

        [JsonConstructor]
        public BotLifeConfig()
        {
            Phases = new Dictionary<string, BotPhaseClamp>(StringComparer.OrdinalIgnoreCase);
            Crowds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Handoff = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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
