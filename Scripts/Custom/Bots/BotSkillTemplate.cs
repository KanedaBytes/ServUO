// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotSkillTemplate.cs — coherent seven-skill character templates per class.
//
// The upstream file was written for T2A and said so in its own numbers: a 225
// stat total with each stat at 100, a 700 skill cap, and a Grandmaster primary
// hardcoded at 99.0. This shard is EJ, and the port's rule is that no cap is
// ever a literal here.
//
// What survives verbatim is the SHAPE - which skills a class has, which of them
// leads, and the relative standing of primary / secondary / utility. That shape
// is era-independent and is the part worth having. Every number it turns into
// comes from BotCaps, which resolves from Config/PlayerCaps.cfg unless
// Data/Custom/bots.json deliberately overrides it.
//
// Class is the SET of skills; tier is the LEVEL they sit at. The primary leads
// so that the paperdoll title reads from it. Utility skills sit at half the
// primary, which on any era's scale is the difference between "can Recall" and
// "is a mage".

using System;

namespace Server.Custom
{
    /// <summary>
    /// The skills a class advances. The first slot is the primary - the one the paperdoll title
    /// comes from. Secondaries track just below it; utilities sit at half scale.
    /// </summary>
    public readonly struct SkillTemplate
    {
        public readonly SkillName Primary;
        public readonly SkillName[] Secondary;
        public readonly SkillName[] Utility;

        public SkillTemplate(SkillName primary, SkillName[] secondary, SkillName[] utility = null)
        {
            Primary = primary;
            Secondary = secondary ?? Array.Empty<SkillName>();
            Utility = utility ?? Array.Empty<SkillName>();
        }

        /// <summary>Total number of skills the template touches. Checked against the skill total cap.</summary>
        public int Count
        {
            get { return 1 + Secondary.Length + Utility.Length; }
        }
    }

    public static class BotSkillTemplates
    {
        /// <summary>
        /// A Mage-class bot whose weapon skill rolled at least this high is a tank mage: it gets
        /// a real weapon at creation and holds its ground in melee. Expressed as a fraction of the
        /// skill cap rather than the upstream's literal 45.0, so it means the same thing on any
        /// era's scale.
        /// </summary>
        public const double TankWeaponFraction = 0.45;

        // ---- Stat profiles ----
        //
        // Upstream stored these as absolute T2A triples - (100, 100, 25) and so on. Here they are
        // PROPORTIONS, which is what they always really were: a dexxer is "strong, fast, dim", and
        // that is true whatever the cap. Resolve() turns a shape into numbers against the caps,
        // so raising TotalStatCap raises every bot without touching this table.
        private static readonly StatShape ShapeDexxer = new StatShape(100, 100, 25);   // pure dexxer
        private static readonly StatShape ShapeTankMage = new StatShape(100, 25, 100); // tank mage
        private static readonly StatShape ShapeArcher = new StatShape(90, 100, 35);
        private static readonly StatShape ShapeCaster = new StatShape(80, 45, 100);
        private static readonly StatShape ShapeHybrid = new StatShape(90, 45, 90);
        private static readonly StatShape ShapeSmith = new StatShape(100, 45, 80);
        private static readonly StatShape ShapeTailor = new StatShape(80, 70, 75);
        private static readonly StatShape ShapeThief = new StatShape(60, 100, 65);
        private static readonly StatShape ShapeMiner = new StatShape(100, 80, 45);

        /// <summary>
        /// The relative weighting of Str, Dex and Int for a class. Absolute magnitude is
        /// meaningless; only the ratios are used.
        /// </summary>
        private readonly struct StatShape
        {
            public readonly int Str;
            public readonly int Dex;
            public readonly int Int;

            public StatShape(int str, int dex, int intel)
            {
                Str = str;
                Dex = dex;
                Int = intel;
            }

            public int Sum
            {
                get { return Str + Dex + Int; }
            }
        }

        // -------------------------------------------------------------------
        // Templates
        // -------------------------------------------------------------------

        /// <summary>
        /// Rolled, not fetched - the Mage class picks a tank-mage weapon variant per bot and the
        /// Bard rolls its seventh skill. Called once at creation; the result is baked into the
        /// mobile's skills.
        /// </summary>
        public static SkillTemplate RollTemplate(BotClass cls)
        {
            return cls switch
            {
                // Pure dexxer - bandages and potions do the healing, a little Magery covers
                // Recall and Cure.
                BotClass.Warrior => new SkillTemplate(
                    SkillName.Swords,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Wrestling, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Mage => RollMageTemplate(),

                BotClass.Fencer => new SkillTemplate(
                    SkillName.Fencing,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Wrestling, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Archer => new SkillTemplate(
                    SkillName.Archery,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Tracking, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Tamer => new SkillTemplate(
                    SkillName.AnimalTaming,
                    new[] { SkillName.AnimalLore, SkillName.Veterinary, SkillName.Magery,
                            SkillName.Meditation, SkillName.MagicResist, SkillName.Wrestling }),

                // The smith carried real Magery and Meditation - recall runs to the mines and
                // back to the forge.
                BotClass.Crafter or BotClass.Smith => new SkillTemplate(
                    SkillName.Blacksmith,
                    new[] { SkillName.Mining, SkillName.Tinkering, SkillName.Magery,
                            SkillName.Meditation, SkillName.Lumberjacking, SkillName.Tailoring }),

                BotClass.Tailor => new SkillTemplate(
                    SkillName.Tailoring,
                    new[] { SkillName.ArmsLore, SkillName.Cooking, SkillName.Tinkering,
                            SkillName.Camping, SkillName.MagicResist }),

                BotClass.Carpenter => new SkillTemplate(
                    SkillName.Carpentry,
                    new[] { SkillName.Lumberjacking, SkillName.Tinkering, SkillName.Magery,
                            SkillName.Meditation, SkillName.Musicianship, SkillName.ArmsLore }),

                // Treasure maps and sea serpents - the fighting fisherman.
                BotClass.Fisherman => new SkillTemplate(
                    SkillName.Fishing,
                    new[] { SkillName.Magery, SkillName.Meditation, SkillName.MagicResist,
                            SkillName.Hiding, SkillName.Camping, SkillName.Healing }),

                BotClass.Healer => new SkillTemplate(
                    SkillName.Healing,
                    new[] { SkillName.Anatomy, SkillName.Veterinary, SkillName.SpiritSpeak,
                            SkillName.Magery, SkillName.MagicResist }),

                BotClass.Thief => new SkillTemplate(
                    SkillName.Stealing,
                    new[] { SkillName.Snooping, SkillName.Hiding, SkillName.Stealth,
                            SkillName.Lockpicking, SkillName.MagicResist }),

                BotClass.Bard => RollBardTemplate(),

                BotClass.Ranger => new SkillTemplate(
                    SkillName.Archery,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Tracking,
                            SkillName.Camping, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                // Lumberjacking stays primary so the paperdoll and the gatherer identity read
                // Lumberjack, but the full dexxer fighting line rides underneath.
                BotClass.Lumberjack => new SkillTemplate(
                    SkillName.Lumberjacking,
                    new[] { SkillName.Swords, SkillName.Tactics, SkillName.Anatomy,
                            SkillName.Healing, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Miner => new SkillTemplate(
                    SkillName.Mining,
                    new[] { SkillName.Swords, SkillName.Tactics, SkillName.Camping,
                            SkillName.ArmsLore, SkillName.MagicResist }),

                // Decode, dig, pick the chest, pull the traps - and clear the guardians with
                // real Magery. No weapon line; spells were the digger's defence.
                BotClass.TreasureHunter => new SkillTemplate(
                    SkillName.Cartography,
                    new[] { SkillName.Lockpicking, SkillName.Magery, SkillName.Meditation,
                            SkillName.DetectHidden, SkillName.RemoveTrap, SkillName.Hiding }),

                // The mule. Item ID's own skill title is "Merchant". Appraises everything,
                // fights nothing.
                BotClass.Merchant => new SkillTemplate(
                    SkillName.ItemID,
                    new[] { SkillName.TasteID, SkillName.Magery, SkillName.Meditation,
                            SkillName.ArmsLore, SkillName.Hiding, SkillName.Camping }),

                _ => new SkillTemplate(
                    SkillName.Swords,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Wrestling, SkillName.MagicResist },
                    new[] { SkillName.Magery }),
            };
        }

        // Two bard builds, both Provocation-led (the money skill). 55% the classic, with Eval Int
        // rounding it out; 45% the peace bard, trading Eval for Peacemaking. The seventh skill
        // rolls Hiding or Wrestling either way.
        private static SkillTemplate RollBardTemplate()
        {
            SkillName seventh = Utility.RandomBool() ? SkillName.Hiding : SkillName.Wrestling;

            return Utility.RandomDouble() < 0.55
                ? new SkillTemplate(
                    SkillName.Provocation,
                    new[] { SkillName.Musicianship, SkillName.Magery, SkillName.Meditation,
                            SkillName.EvalInt, SkillName.MagicResist, seventh })
                : new SkillTemplate(
                    SkillName.Provocation,
                    new[] { SkillName.Musicianship, SkillName.Peacemaking, SkillName.Magery,
                            SkillName.Meditation, SkillName.MagicResist, seventh });
        }

        // The Mage class rolls its variant per bot:
        //   40% hally mage  (Swords)
        //   20% mace tank   (Macing)  - maces wrecked armour and stamina
        //   15% fencer tank (Fencing) - spear and war fork speed
        //   25% scribe mage           - pure caster with Inscription
        private static SkillTemplate RollMageTemplate()
        {
            int roll = Utility.Random(100);

            if (roll < 75)
            {
                SkillName weapon = roll < 40 ? SkillName.Swords
                    : roll < 60 ? SkillName.Macing
                    : SkillName.Fencing;

                return new SkillTemplate(
                    SkillName.Magery,
                    new[] { SkillName.EvalInt, SkillName.Meditation, SkillName.Wrestling,
                            SkillName.MagicResist, weapon, SkillName.Tactics });
            }

            return new SkillTemplate(
                SkillName.Magery,
                new[] { SkillName.EvalInt, SkillName.Meditation, SkillName.Wrestling,
                        SkillName.Inscribe, SkillName.MagicResist });
        }

        // -------------------------------------------------------------------
        // Tier to numbers. Everything below is scaled off BotCaps.
        // -------------------------------------------------------------------

        /// <summary>
        /// Where the primary skill lands for a tier, as a fraction of the per-skill cap.
        ///
        /// Upstream had these as absolute values from 30.0 to 99.0 against a cap of 100, so the
        /// fractions below are exactly its curve, re-expressed. Grandmaster is 0.99 rather than
        /// 1.00 for the same reason it was 99.0 there: a bot that has literally maxed a skill
        /// reads as a GM template rather than a character.
        /// </summary>
        private static double PrimaryFraction(BotSkillTier tier)
        {
            return tier switch
            {
                BotSkillTier.Novice => 0.30,
                BotSkillTier.Apprentice => 0.45,
                BotSkillTier.Journeyman => 0.60,
                BotSkillTier.Adept => 0.72,
                BotSkillTier.Expert => 0.82,
                BotSkillTier.Master => 0.90,
                BotSkillTier.Grandmaster => 0.99,
                _ => 0.50,
            };
        }

        public static double PrimarySkillTarget(BotSkillTier tier, BotCaps caps)
        {
            return PrimaryFraction(tier) * caps.SkillCap;
        }

        /// <summary>
        /// Secondaries sit just below the primary so the title comes from the primary - but the
        /// gap scales, so it stays one notch rather than becoming trivial on a high-cap shard.
        /// (Upstream subtracted a flat 4.0 against a cap of 100.)
        /// </summary>
        public static double SecondarySkillTarget(BotSkillTier tier, BotCaps caps)
        {
            return Math.Max(0.0, PrimarySkillTarget(tier, caps) - (0.04 * caps.SkillCap));
        }

        /// <summary>
        /// Utility skills run at half scale. On a 100 cap that puts a Grandmaster dexxer's Magery
        /// near 50 - reliable Recall, never Gate - which is the point of the tier.
        /// </summary>
        public static double UtilitySkillTarget(BotSkillTier tier, BotCaps caps)
        {
            return PrimarySkillTarget(tier, caps) * 0.5;
        }

        /// <summary>The skill value above which a Mage counts as a tank mage and gets a real weapon.</summary>
        public static double TankWeaponSkillMin(BotCaps caps)
        {
            return TankWeaponFraction * caps.SkillCap;
        }

        /// <summary>
        /// The Magery below which a bot has no business carrying travel reagents.
        ///
        /// Upstream this lived on MagicTravel as a flat 40.0 - the level at which a utility-magery
        /// dexxer can actually cast Recall. MagicTravel is the travel layer and is not ported yet,
        /// so the threshold lives here, expressed as a fraction of the skill cap like every other
        /// number in this file.
        /// </summary>
        public static double TravelMageryMin(BotCaps caps)
        {
            return 0.40 * caps.SkillCap;
        }

        /// <summary>Jitter of 3-10% of the skill cap, sign random, so no two bots are identical.</summary>
        public static double RollJitter(BotCaps caps)
        {
            double magnitude = Utility.RandomMinMax(3, 10) / 100.0 * caps.SkillCap;

            return Utility.RandomBool() ? magnitude : -magnitude;
        }

        /// <summary>
        /// Stat targets for a class and tier, resolved against the caps.
        ///
        /// The class shape sets the ratios; the tier sets how much of the budget is spent, rising
        /// from 60% at Novice to 100% at Grandmaster. The result is then clamped so no single stat
        /// exceeds StatCap and the three together never exceed StatTotal - which is the guarantee
        /// the boot-time audit checks, and the reason this returns rather than trusting the table.
        /// </summary>
        public static (int str, int dex, int intel) StatTargets(BotClass cls, BotSkillTier tier, BotCaps caps)
        {
            StatShape shape = cls switch
            {
                BotClass.Warrior => ShapeDexxer,
                BotClass.Mage => ShapeTankMage,
                BotClass.Fencer => ShapeDexxer,
                BotClass.Archer => ShapeArcher,
                BotClass.Tamer => ShapeCaster,
                BotClass.Crafter => ShapeSmith,
                BotClass.Smith => ShapeSmith,
                BotClass.Tailor => ShapeTailor,
                BotClass.Carpenter => ShapeSmith,
                BotClass.Fisherman => ShapeHybrid,
                BotClass.Healer => ShapeCaster,
                BotClass.Thief => ShapeThief,
                BotClass.Bard => ShapeCaster,
                BotClass.Ranger => ShapeArcher,
                BotClass.Lumberjack => ShapeDexxer,
                BotClass.Miner => ShapeMiner,
                BotClass.TreasureHunter => ShapeHybrid,
                BotClass.Merchant => ShapeCaster,
                _ => ShapeDexxer,
            };

            // Novice spends 60% of the stat budget, Grandmaster all of it.
            double spend = 0.60 + (0.40 * BotSkillTierHelper.Fraction(tier));
            double budget = caps.StatTotal * spend;

            int sum = shape.Sum;

            if (sum <= 0)
            {
                sum = 1;
            }

            int str = (int)Math.Round(budget * shape.Str / sum);
            int dex = (int)Math.Round(budget * shape.Dex / sum);
            int intel = (int)Math.Round(budget * shape.Int / sum);

            // A stat may not stand above the individual cap even if its share of the budget would
            // put it there. Clamping low so nothing is left at zero: a mobile with 0 Int cannot
            // hold mana and a mobile with 0 Str is dead on arrival.
            str = Clamp(str, 10, caps.StatCap);
            dex = Clamp(dex, 10, caps.StatCap);
            intel = Clamp(intel, 10, caps.StatCap);

            // Rounding three shares of a budget can push the total one or two over. Shave the
            // largest stat until it fits, rather than rescaling - which would move all three and
            // lose the class shape for the sake of a rounding error.
            while (str + dex + intel > caps.StatTotal)
            {
                if (str >= dex && str >= intel && str > 10)
                {
                    str--;
                }
                else if (dex >= intel && dex > 10)
                {
                    dex--;
                }
                else if (intel > 10)
                {
                    intel--;
                }
                else
                {
                    // All three are at the floor and still over budget, which means the configured
                    // statTotal is below 30. Nothing sensible left to do; the config validator
                    // and the health check both report it.
                    break;
                }
            }

            return (str, dex, intel);
        }

        private static int Clamp(int value, int low, int high)
        {
            if (value < low)
            {
                return low;
            }

            return value > high ? high : value;
        }
    }
}
