// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotClass.cs — the character "class" of a PlayerBot.
//
// Not tied to behaviour: a Mage-class bot might be a bank sitter today and an
// adventurer tomorrow. The class determines:
//   - what equipment it wears (EquipmentTable)
//   - what its paperdoll title says ("Grandmaster Mage")
//   - which skills it has at all (BotSkillTemplate)
//
// Rolled once at creation and persisted for the bot's life.

using System;

namespace Server.Custom
{
    public enum BotClass : byte
    {
        Warrior = 0,
        Mage = 1,
        Fencer = 2,
        Archer = 3,
        Tamer = 4,
        Crafter = 5,  // LEGACY - no longer rolled; migrates to Smith/Tailor/Fisherman
        Healer = 6,
        Thief = 7,
        Bard = 8,
        Ranger = 9,

        // Artisan classes - each replaced a former Crafter subtype. They station at,
        // and "work", a specific destination once the crafting layer lands.
        Smith = 10,
        Tailor = 11,
        Fisherman = 12,

        // Gatherer classes. They work wilderness resource spots, fill their packs and
        // haul the load back to town. They carry real tools that double as weapons, so
        // a lumberjack defends itself with its axe.
        Lumberjack = 13,
        Miner = 14,

        // The two remaining classic templates. The Treasure Hunter digs wilderness
        // chests and clears the guardians with spells; the Merchant is the town mule -
        // banks, shops, appraises, never fights.
        TreasureHunter = 15,
        Merchant = 16,

        // Fourth artisan. Buys the lumberjacks' hauls and turns them into staves,
        // furniture and instruments.
        Carpenter = 17,
    }

    public static class BotClassHelper
    {
        // Class roll weights - must sum to 100. Heavier on the common combat classes,
        // lighter on the specialists. The retired Crafter share (12) is split across
        // the four artisans.
        private static readonly (BotClass cls, int weight)[] ClassWeights =
        {
            (BotClass.Warrior, 14),
            (BotClass.Mage, 14),
            (BotClass.Fencer, 11),
            (BotClass.Archer, 11),
            (BotClass.Smith, 4),
            (BotClass.Tailor, 3),
            (BotClass.Carpenter, 2),
            (BotClass.Fisherman, 4),
            (BotClass.Healer, 7),
            (BotClass.Bard, 7),
            (BotClass.Ranger, 6),
            (BotClass.Tamer, 5),
            (BotClass.Thief, 3),
            (BotClass.Lumberjack, 3),
            (BotClass.Miner, 2),
            (BotClass.TreasureHunter, 2),
            (BotClass.Merchant, 2),
        };

        public static BotClass RollRandom()
        {
            int roll = Utility.Random(100);
            int accumulated = 0;

            foreach (var (cls, weight) in ClassWeights)
            {
                accumulated += weight;

                if (roll < accumulated)
                {
                    return cls;
                }
            }

            return BotClass.Warrior;
        }

        /// <summary>The noun in the paperdoll title - "the Grandmaster <b>Swordsman</b>".</summary>
        public static string DisplayName(BotClass cls)
        {
            return cls switch
            {
                BotClass.Warrior => "Swordsman",
                BotClass.Mage => "Mage",
                BotClass.Fencer => "Fencer",
                BotClass.Archer => "Archer",
                BotClass.Tamer => "Tamer",
                BotClass.Crafter => "Crafter",
                BotClass.Healer => "Healer",
                BotClass.Thief => "Thief",
                BotClass.Bard => "Bard",
                BotClass.Ranger => "Ranger",
                BotClass.Smith => "Blacksmith",
                BotClass.Tailor => "Tailor",
                BotClass.Fisherman => "Fisherman",
                BotClass.Carpenter => "Carpenter",
                BotClass.Lumberjack => "Lumberjack",
                BotClass.Miner => "Miner",
                BotClass.TreasureHunter => "Treasure Hunter",
                BotClass.Merchant => "Merchant",
                _ => "Wanderer",
            };
        }

        // SEAM 1 of 5 (see Scripts/Custom/Bots/README.md): StationFor is not ported.
        // It returned the upstream DestinationType enum, which is their travel layer's;
        // ours is the free-token `type` string on a NavDestination. It returns with the
        // Traveler/Crafter behaviours, as a nav destination type string.

        /// <summary>
        /// Artisans station at a town fixture and work it. Distinct from gatherers, who
        /// run a loop out to a wilderness spot and haul the load back.
        /// </summary>
        public static bool IsArtisan(BotClass cls)
        {
            return cls is BotClass.Smith or BotClass.Tailor or BotClass.Fisherman or BotClass.Carpenter;
        }

        public static bool IsGatherer(BotClass cls)
        {
            return cls is BotClass.Lumberjack or BotClass.Miner;
        }

        /// <summary>
        /// Every class the roll can produce. Crafter is excluded: it is a legacy value kept
        /// only so an old serialized byte still resolves.
        /// </summary>
        public static BotClass[] Rollable()
        {
            var rollable = new BotClass[ClassWeights.Length];

            for (int i = 0; i < ClassWeights.Length; i++)
            {
                rollable[i] = ClassWeights[i].cls;
            }

            return rollable;
        }

        public static bool TryParse(string value, out BotClass cls)
        {
            cls = BotClass.Warrior;

            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (BotClass candidate in Enum.GetValues(typeof(BotClass)))
            {
                if (Insensitive.Equals(candidate.ToString(), value))
                {
                    cls = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
