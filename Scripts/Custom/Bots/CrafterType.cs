// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// CrafterType.cs — the artisan subtype carried by a Crafter-lineage bot.
//
// The legacy BotClass.Crafter was one class with seven subtypes. It was narrowed
// to three, and the surviving subtypes were promoted to real BotClass values.
// This enum stays because a bot still carries a spec byte, and because the
// migration path from an old value has to land somewhere valid.

using System;

namespace Server.Custom
{
    public enum CrafterType : byte
    {
        Smith = 0,      // forge - armour and weapons
        Tailor = 1,     // tailor shop - cloth and leather
        Fisherman = 2,  // dock - fish and rare sea finds
    }

    public static class CrafterTypeHelper
    {
        // Smiths lead (the iconic forge crafter), tailors next, fishermen rarest -
        // docks are fewer and farther-flung.
        private static readonly (CrafterType type, int weight)[] Weights =
        {
            (CrafterType.Smith, 45),
            (CrafterType.Tailor, 30),
            (CrafterType.Fisherman, 25),
        };

        public static CrafterType RollRandom()
        {
            int roll = Utility.Random(100);
            int accumulated = 0;

            foreach (var (type, weight) in Weights)
            {
                accumulated += weight;

                if (roll < accumulated)
                {
                    return type;
                }
            }

            return CrafterType.Smith;
        }

        /// <summary>
        /// Remap a spec byte from the upstream pre-rebuild layout onto the current three.
        ///
        /// The old layout was Smith=0 Tailor=1 Tinker=2 Inscriptionist=3 Carpenter=4
        /// Bowcrafter=5 Fisherman=6. Smith and Tailor keep their slots, the old Fisherman
        /// (6) maps to the new one (2), and the four retired subtypes re-roll so no bot
        /// ends up holding a meaningless spec.
        ///
        /// Kept even though this shard has never written a bot save: it costs four lines,
        /// and it is the only record of what those bytes used to mean.
        /// </summary>
        public static CrafterType MigrateLegacyByte(byte raw)
        {
            return raw switch
            {
                0 => CrafterType.Smith,
                1 => CrafterType.Tailor,
                6 => CrafterType.Fisherman,
                _ => RollRandom(),
            };
        }

        public static string DisplayName(CrafterType type)
        {
            return type switch
            {
                CrafterType.Smith => "Blacksmith",
                CrafterType.Tailor => "Tailor",
                CrafterType.Fisherman => "Fisherman",
                _ => "Crafter",
            };
        }

        // SEAM 1 of 5 (see Scripts/Custom/Bots/README.md): StationFor is not ported,
        // for the same reason as BotClassHelper.StationFor - it returned the upstream
        // DestinationType enum. It returns with the Crafter behaviour, as a nav
        // destination type string.

        /// <summary>The BotClass a given subtype corresponds to, now that they are real classes.</summary>
        public static BotClass ToBotClass(CrafterType type)
        {
            return type switch
            {
                CrafterType.Smith => BotClass.Smith,
                CrafterType.Tailor => BotClass.Tailor,
                CrafterType.Fisherman => BotClass.Fisherman,
                _ => BotClass.Smith,
            };
        }
    }
}
