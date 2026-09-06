// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotSkillTier.cs — how experienced a bot is.
//
// Combined with BotClass, drives:
//   - equipment quality (low tier = cheap gear, high tier = best in slot, hued)
//   - the paperdoll title ("Grandmaster Swordsman", "Novice Mage")
//
// Distribution is bell-curved: most bots are mid-tier, very few are Grandmasters.
// Rolled once at creation and persisted for the bot's life.
//
// The tier is a SHAPE, not a number. What an "Expert" actually scores comes from
// BotSkillTemplate, which scales every value off the configured caps - see
// BotCaps and Data/Custom/bots.json. Nothing here knows what 100 means.

using System;

namespace Server.Custom
{
    public enum BotSkillTier : byte
    {
        Novice = 0,
        Apprentice = 1,
        Journeyman = 2,
        Adept = 3,
        Expert = 4,
        Master = 5,
        Grandmaster = 6,
    }

    public static class BotSkillTierHelper
    {
        /// <summary>The highest tier, as an int. The scale every tier fraction is taken against.</summary>
        public const int MaxRank = (int)BotSkillTier.Grandmaster;

        // Bell-curve weights, summing to 100.
        private static readonly (BotSkillTier tier, int weight)[] TierWeights =
        {
            (BotSkillTier.Novice, 15),
            (BotSkillTier.Apprentice, 20),
            (BotSkillTier.Journeyman, 20),
            (BotSkillTier.Adept, 20),
            (BotSkillTier.Expert, 15),
            (BotSkillTier.Master, 8),
            (BotSkillTier.Grandmaster, 2),
        };

        public static BotSkillTier RollRandom()
        {
            int roll = Utility.Random(100);
            int accumulated = 0;

            foreach (var (tier, weight) in TierWeights)
            {
                accumulated += weight;

                if (roll < accumulated)
                {
                    return tier;
                }
            }

            return BotSkillTier.Journeyman;
        }

        public static string DisplayName(BotSkillTier tier)
        {
            return tier switch
            {
                BotSkillTier.Novice => "Novice",
                BotSkillTier.Apprentice => "Apprentice",
                BotSkillTier.Journeyman => "Journeyman",
                BotSkillTier.Adept => "Adept",
                BotSkillTier.Expert => "Expert",
                BotSkillTier.Master => "Master",
                BotSkillTier.Grandmaster => "Grandmaster",
                _ => "Apprentice",
            };
        }

        /// <summary>Tier as an int, so equipment rolls can ask "is this good enough for hued gear?".</summary>
        public static int Rank(BotSkillTier tier)
        {
            return (int)tier;
        }

        /// <summary>
        /// Where this tier sits on the 0..1 scale from Novice to Grandmaster. The single place
        /// the tier ladder is turned into a number; skill and stat targets both scale off it.
        /// </summary>
        public static double Fraction(BotSkillTier tier)
        {
            return Rank(tier) / (double)MaxRank;
        }

        public static bool TryParse(string value, out BotSkillTier tier)
        {
            tier = BotSkillTier.Journeyman;

            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            // A bare rank number is accepted too, so [SpawnBot mage 6 works alongside
            // [SpawnBot mage grandmaster.
            int rank;

            if (Int32.TryParse(value, out rank))
            {
                if (rank < 0 || rank > MaxRank)
                {
                    return false;
                }

                tier = (BotSkillTier)rank;
                return true;
            }

            foreach (BotSkillTier candidate in Enum.GetValues(typeof(BotSkillTier)))
            {
                if (Insensitive.Equals(candidate.ToString(), value))
                {
                    tier = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
