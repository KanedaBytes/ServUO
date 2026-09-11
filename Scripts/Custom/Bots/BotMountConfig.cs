// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotMountConfig.cs - who owns a horse, and who stays on it.
//
// WHAT IS UPSTREAM'S AND WHAT IS OURS, because the two halves of this file have different
// provenance and the difference matters when somebody next reads their code.
//
// OWNERSHIP IS THEIRS, VERBATIM. A flat 70% roll at spawn, gated by class and nothing else
// (uo-offline PlayerBot.cs:823-837). The only thing that moves here is WHERE the number lives:
// it was `BotMovement.MountChance`, a const, and it is now `ownChance`.
//
// THERE IS NO TIER OR WEALTH COMPONENT UPSTREAM AND NONE IN THEIR DATA. `BotMountHelper` never
// reads `SkillTier`, never reads `Gold`, and `playerbots/data/` carries no mount field at all -
// their whole mount system is 166 lines of C# literals. And their stables is not an economy to
// translate either: the "stabled" horse is `Delete()`d and the "claimed" one is
// `Activator.CreateInstance`d fresh and free (TravelerBehavior.cs:2411-2425), so there is no
// purchase to defer to 7f. A real one belongs with seams 11 and 12, where the gold is.
//
// SO `staysMounted` IS OURS, and it is here rather than in code for the same reason `life.idle`
// is: it is a disposition you judge by LOOKING at a bank, and a rebuild between each look is what
// stops anybody looking twice.
//
// WHY A DISPOSITION AT ALL, rather than upstream's "never dismount on arrival". Because upstream's
// answer and ours were BOTH wrong here, in opposite directions. Theirs leaves every bank sitter in
// the saddle, which is what `BotMovement.Settle` was written to avoid - "a bank full of horses is
// not what a bank looks like". Ours dismounted everybody, and DELETED THE HORSE, and nothing ever
// re-mounted: measured over a fifteen-minute window, 70% of bots own a horse at birth and 7.1% of
// travelling bots still had one, with 0.0% mounted at arrival at every single tier. The first bank
// visit put a bot on foot for the rest of its session. A disposition drawn at birth gives a mixed
// bank - some riders, some on foot - which is what a bank actually looked like.

using System;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// Whether a bot stays in the saddle where it stops. Rolled once at birth, and persisted with
    /// the rest of its identity.
    /// </summary>
    public enum MountDisposition : byte
    {
        /// <summary>Gets off where it stops. The horse waits beside it.</summary>
        Dismounts = 0,

        /// <summary>Stays mounted, bar the occasional arrival that rolls otherwise.</summary>
        Rides = 1
    }

    /// <summary>The tier-and-trait curve behind <see cref="MountDisposition"/>.</summary>
    public class BotStaysMountedConfig : IValidatableConfig
    {
        /// <summary>The chance a Novice with no Wealthy trait stays mounted.</summary>
        [JsonProperty("tierFloor")]
        public double TierFloor { get; set; }

        /// <summary>How much of the chance the tier ladder contributes, floor to Grandmaster.</summary>
        [JsonProperty("tierSpan")]
        public double TierSpan { get; set; }

        /// <summary>What the Wealthy trait adds on top.</summary>
        [JsonProperty("wealthyBonus")]
        public double WealthyBonus { get; set; }

        [JsonConstructor]
        public BotStaysMountedConfig()
        {
            TierFloor = 0.10;
            TierSpan = 0.75;
            WealthyBonus = 0.20;
        }

        public void Validate(ConfigErrors errors)
        {
            Probability(errors, "mounts.staysMounted.tierFloor", TierFloor);
            Probability(errors, "mounts.staysMounted.tierSpan", TierSpan);
            Probability(errors, "mounts.staysMounted.wealthyBonus", WealthyBonus);
        }

        private static void Probability(ConfigErrors errors, string where, double value)
        {
            if (value < 0.0 || value > 1.0)
            {
                errors.Add("{0} is {1}; it is a probability and must be between 0 and 1.", where, value);
            }
        }
    }

    public class BotMountConfig : IValidatableConfig
    {
        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        /// <summary>
        /// The share of eligible bots that own a mount at all. uo-offline `PlayerBot.cs:832`.
        ///
        /// Eligibility is class, and stays in code (`BotMovement.CanOwnMount`): a Fisherman works
        /// the dock edge on foot and a gatherer's animal is the unrideable pack beast, which are
        /// facts about those classes rather than dials.
        /// </summary>
        [JsonProperty("ownChance")]
        public double OwnChance { get; set; }

        /// <summary>The curve that decides a bot's disposition at birth. Ours, not upstream's.</summary>
        [JsonProperty("staysMounted")]
        public BotStaysMountedConfig StaysMounted { get; set; }

        /// <summary>
        /// The chance a bot that RIDES gets off anyway on any given arrival.
        ///
        /// "A small chance for the rest", and it is only small because nothing is destroyed: the
        /// horse waits and the bot re-mounts when it leaves, so this is a per-visit coin flip
        /// rather than an attrition rate. With a destructive dismount the same number would have
        /// converged the whole fleet onto its feet within a session, which is exactly what the
        /// measured 7.1% was.
        /// </summary>
        [JsonProperty("dismountOnArrivalChance")]
        public double DismountOnArrivalChance { get; set; }

        [JsonConstructor]
        public BotMountConfig()
        {
            OwnChance = 0.70;
            StaysMounted = new BotStaysMountedConfig();
            DismountOnArrivalChance = 0.15;
        }

        public void Validate(ConfigErrors errors)
        {
            if (OwnChance < 0.0 || OwnChance > 1.0)
            {
                errors.Add("mounts.ownChance is {0}; it is a probability and must be between 0 and 1.", OwnChance);
            }

            if (DismountOnArrivalChance < 0.0 || DismountOnArrivalChance > 1.0)
            {
                errors.Add(
                    "mounts.dismountOnArrivalChance is {0}; it is a probability and must be between 0 and 1.",
                    DismountOnArrivalChance);
            }

            // A present-but-null subsection is absent, not a fault - BotStore.Validate's rule.
            if (StaysMounted == null)
            {
                StaysMounted = new BotStaysMountedConfig();
            }

            StaysMounted.Validate(errors);
        }

        /// <summary>
        /// The chance this bot's disposition comes out Rides.
        ///
        /// Tier through `BotSkillTierHelper.Fraction`, which is deliberately the ONE place the tier
        /// ladder becomes a number (`BotSkillTier.cs:95-98`) - re-deriving it here would be a second
        /// scale to keep in step with the first. Wealthy on top, and this is the FIRST runtime
        /// consumer of `HasTrait` outside BotPersonality: until now the six traits were folded into
        /// the four tendency doubles at birth and then never read again, and Wealthy's only effect
        /// in the whole tree was `BankerTendency *= 1.3`.
        /// </summary>
        public double StaysMountedChance(BotSkillTier tier, BotPersonality personality)
        {
            BotStaysMountedConfig curve = StaysMounted ?? new BotStaysMountedConfig();

            double chance = curve.TierFloor
                + (curve.TierSpan * BotSkillTierHelper.Fraction(tier));

            if (personality.HasTrait(PersonalityTrait.Wealthy))
            {
                chance += curve.WealthyBonus;
            }

            return chance < 0.0 ? 0.0 : (chance > 1.0 ? 1.0 : chance);
        }

        /// <summary>Draw a disposition for a bot being born.</summary>
        public MountDisposition Roll(BotSkillTier tier, BotPersonality personality)
        {
            return Utility.RandomDouble() < StaysMountedChance(tier, personality)
                ? MountDisposition.Rides
                : MountDisposition.Dismounts;
        }
    }
}
