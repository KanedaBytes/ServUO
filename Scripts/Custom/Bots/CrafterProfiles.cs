// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// CrafterProfiles.cs — what each artisan class makes, out of what, with which tool.
//
// The DATA half of the crafting layer; CrafterBehavior is the engine and
// CrafterStock is the pack accounting. Upstream split it the same way.
//
// WHAT CHANGED IN TRANSLATION, and it is the whole point of the file:
//
// Upstream's bands were `Func<PlayerBot, Item>` factories — a production roll
// picked one and called `new Dagger()`. There was no craft system involved at
// all: two dice decided whether anything was made, a third decided the band, and
// the maker's mark was stamped on by hand. Materials were consumed as a flat
// count with no relationship to the item.
//
// Here a band is a list of ITEM TYPES, and the type is looked up in ServUO's real
// CraftSystem. So the bands stay exactly as upstream curated them — they are a
// menu of what this trade plausibly turns out — and everything downstream of
// picking one is the engine's: the skill check, the resource consumption, the
// exceptional roll, the maker's mark, the failure. All 28 types below were
// verified present in DefBlacksmithy / DefTailoring / DefCarpentry, and Bind()
// re-checks it at boot so a shard that prunes a craft list hears about it rather
// than quietly making nothing.

using System;
using System.Collections.Generic;

using Server.Engines.Craft;
using Server.Items;

namespace Server.Custom
{
    public sealed class CrafterProfile
    {
        public BotClass Type { get; set; }

        /// <summary>Humanoid animation index for a work swing.</summary>
        public int Action { get; set; }

        /// <summary>Work sound, or 0 for silent.</summary>
        public int Sound { get; set; }

        /// <summary>The tool this trade uses. Never null: CanCraft refuses a null tool with 1044038.</summary>
        public Type ToolType { get; set; }

        /// <summary>True when the tool is held in a hand rather than carried in the pack.</summary>
        public bool ToolIsEquipped { get; set; }

        public Type[] Common { get; set; }

        public Type[] Minor { get; set; }

        public Type[] Rare { get; set; }

        /// <summary>Items a freshly-spawned artisan starts with, so it is not empty-handed.</summary>
        public Func<Item>[] StarterProps { get; set; }

        /// <summary>What this trade consumes. Counted and delivered against, in this order.</summary>
        public Type[] Materials { get; set; }

        /// <summary>Turns a delivered quantity into stock: ore becomes ingots, logs become boards.</summary>
        public Func<int, Item> MakeMaterial { get; set; }

        /// <summary>The gatherer good this trade buys. Null means no gatherer hauls it.</summary>
        public Type RawGood { get; set; }

        /// <summary>What the bot calls its materials out loud — the {mat} token.</summary>
        public string MaterialNoun { get; set; }

        /// <summary>The nav destination type this class stations at, and the tag that narrows it.</summary>
        public string StationType { get; set; }

        public string StationTag { get; set; }

        public CraftSystem CraftSystem { get; set; }

        public CrafterProfile()
        {
            Action = 9;
            MaterialNoun = "materials";
            Common = new Type[0];
            Minor = new Type[0];
            Rare = new Type[0];
            StarterProps = new Func<Item>[0];
            Materials = new Type[0];
        }
    }

    public static class CrafterProfiles
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private static readonly Dictionary<BotClass, CrafterProfile> _profiles =
            new Dictionary<BotClass, CrafterProfile>();

        static CrafterProfiles()
        {
            // SMITH — the iconic forge crafter. Action 9 is the one-handed swing, 0x2A the anvil
            // ring. The only trade whose station is load-bearing: DefBlacksmithy.CanCraft wants a
            // forge AND an anvil within two tiles, with line of sight to both.
            _profiles[BotClass.Smith] = new CrafterProfile
            {
                Type = BotClass.Smith,
                Action = 9,
                Sound = 0x2A,
                ToolType = typeof(SmithHammer),
                ToolIsEquipped = true,
                Common = new[] { typeof(Dagger), typeof(Mace), typeof(RingmailGloves), typeof(PlateGorget) },
                Minor = new[] { typeof(Katana), typeof(Broadsword), typeof(PlateGloves) },
                Rare = new[] { typeof(Halberd), typeof(Bardiche), typeof(PlateChest) },
                StarterProps = new Func<Item>[]
                {
                    () => new Dagger(),
                    () => new RingmailGloves(),
                    () => new IronIngot(Utility.RandomMinMax(40, 90)),
                },
                Materials = new[] { typeof(IronIngot) },
                MakeMaterial = amount => new IronIngot(amount),
                RawGood = typeof(IronOre),
                MaterialNoun = "iron ingots",
                StationType = "forge",
                StationTag = null,
            };

            // TAILOR — 0x248 is the scissors snip. No hand-held tool: the kit rides in the pack,
            // which BaseTool.CheckAccessible is perfectly happy with (it wants RootParent, not a
            // layer). DefTailoring.CanCraft has no fixture requirement at all, so a tailor works
            // wherever it is standing.
            _profiles[BotClass.Tailor] = new CrafterProfile
            {
                Type = BotClass.Tailor,
                Action = 9,
                Sound = 0x248,
                ToolType = typeof(SewingKit),
                ToolIsEquipped = false,
                Common = new[] { typeof(Shirt), typeof(ShortPants), typeof(LongPants), typeof(Cap), typeof(Bandana) },
                Minor = new[] { typeof(FancyShirt), typeof(FloppyHat), typeof(LeafChest) },
                Rare = new[] { typeof(WizardsHat), typeof(HideChest) },
                StarterProps = new Func<Item>[]
                {
                    () => new Shirt(),
                    () => new Cap(),
                    () => new Scissors(),
                    () => new Cloth(Utility.RandomMinMax(40, 80)),
                    () => new Leather(Utility.RandomMinMax(15, 35)),
                },
                Materials = new[] { typeof(Cloth), typeof(Leather) },
                MakeMaterial = amount => new Cloth(amount),

                // Upstream's note, and it still holds: no gatherer hauls cloth. A tailor works
                // from its starter bolt until the economy session gives it a supplier.
                RawGood = null,
                MaterialNoun = "cloth",
                StationType = "shop",
                StationTag = "tailoring",
            };

            // CARPENTER — 0x23D is the saw rasp. Buys the lumberjacks' hauls.
            _profiles[BotClass.Carpenter] = new CrafterProfile
            {
                Type = BotClass.Carpenter,
                Action = 9,
                Sound = 0x23D,
                ToolType = typeof(Saw),
                ToolIsEquipped = false,
                Common = new[] { typeof(Club), typeof(QuarterStaff), typeof(GnarledStaff) },
                Minor = new[] { typeof(Stool), typeof(WoodenChair), typeof(WoodenBox) },
                Rare = new[] { typeof(Lute), typeof(LapHarp) },
                StarterProps = new Func<Item>[]
                {
                    () => new Saw(),
                    () => new SmoothingPlane(),
                    () => new Club(),
                    () => new Board(Utility.RandomMinMax(40, 90)),
                },
                Materials = new[] { typeof(Board) },
                MakeMaterial = amount => new Board(amount),
                RawGood = typeof(Log),
                MaterialNoun = "boards",
                StationType = "shop",
                StationTag = "woodworking",
            };

            // FISHERMAN — SEAM: not a Crafter this session, deliberately.
            //
            // Everything about it differs from the other three. It has no station in the graph
            // (there is no `dock` destination), it consumes no stock, and its production is not a
            // craft at all: upstream drove the real Fishing.System, walking to the water's edge
            // first and casting only when open water was directly adjacent, which needs their
            // IsWet / HasStandableStatic dockside-plank scan to tell a pier from the sea.
            //
            // BotClassHelper.StationFor still answers `dock` for it, so the missing station is
            // REPORTED by BotWorkSites rather than being silently absent.
            //
            // Restored by: the dock session. Britain's waterfront landmarks are The Oaken Oar
            // (1424,1747, the dockside tavern) and Customs (1480,1746, on the docks themselves).
        }

        /// <summary>
        /// The profile this BOT works to, resolving the legacy Crafter class through its sub-type.
        ///
        /// Same reason as BotClassHelper.StationFor(PlayerBot): the profiles are keyed on the real
        /// trade classes and BotClass.Crafter is not one of them, so asking by Class alone hands a
        /// Crafter-class smith a null profile and it stands at the bench with "no trade".
        /// </summary>
        public static CrafterProfile For(PlayerBot bot)
        {
            return bot == null ? null : For(bot.TradeClass);
        }

        /// <summary>The profile for a class, or null when that class is not a stationed artisan.</summary>
        public static CrafterProfile For(BotClass cls)
        {
            CrafterProfile profile;

            return _profiles.TryGetValue(cls, out profile) ? profile : null;
        }

        public static IEnumerable<CrafterProfile> All
        {
            get { return _profiles.Values; }
        }

        /// <summary>
        /// Bind each profile to its CraftSystem and drop any band entry the system does not know.
        ///
        /// Called from BotSystem.Initialize, never from the static constructor: CraftContext
        /// builds the eleven CraftSystem singletons in its own Configure, and reaching for one
        /// before that has run would construct a second, parallel copy of it.
        ///
        /// A dropped entry is a WARNING, not a failure. A shard that prunes an item out of
        /// DefCarpentry has not broken the bot layer — the carpenter simply makes one fewer
        /// thing — but a band that silently made nothing would look exactly like a broken
        /// behaviour from the outside.
        /// </summary>
        public static void Bind()
        {
            _profiles[BotClass.Smith].CraftSystem = DefBlacksmithy.CraftSystem;
            _profiles[BotClass.Tailor].CraftSystem = DefTailoring.CraftSystem;
            _profiles[BotClass.Carpenter].CraftSystem = DefCarpentry.CraftSystem;

            foreach (CrafterProfile profile in _profiles.Values)
            {
                profile.Common = Keep(profile, profile.Common);
                profile.Minor = Keep(profile, profile.Minor);
                profile.Rare = Keep(profile, profile.Rare);

                if (profile.Common.Length == 0)
                {
                    Log.Warn(
                        "{0} has no makeable items left in its common band; it will stand at its station making nothing.",
                        BotClassHelper.DisplayName(profile.Type));
                }
            }
        }

        private static Type[] Keep(CrafterProfile profile, Type[] band)
        {
            var kept = new List<Type>(band.Length);

            foreach (Type type in band)
            {
                if (profile.CraftSystem != null && profile.CraftSystem.CraftItems.SearchFor(type) != null)
                {
                    kept.Add(type);
                    continue;
                }

                Log.Warn(
                    "{0} cannot make {1}: no such entry in {2}. Dropped from its bands.",
                    BotClassHelper.DisplayName(profile.Type),
                    type.Name,
                    profile.CraftSystem == null ? "(no craft system)" : profile.CraftSystem.GetType().Name);
            }

            return kept.ToArray();
        }

        /// <summary>
        /// Which band a bot of this tier attempts, upstream's weights exactly.
        ///
        /// This is the ONE roll kept from their production engine, and it survives for a reason
        /// the real craft system cannot cover: ServUO decides whether a given attempt succeeds,
        /// but nothing in it decides that a Grandmaster is the one who attempts a halberd at all.
        /// Their MakeChance roll is gone — the skill check replaces it, and does it properly.
        /// </summary>
        public static Type[] PickBand(CrafterProfile profile, BotSkillTier tier)
        {
            int common, minor, rare;

            switch (tier)
            {
                case BotSkillTier.Adept: common = 96; minor = 4; rare = 0; break;
                case BotSkillTier.Expert: common = 93; minor = 7; rare = 0; break;
                case BotSkillTier.Master: common = 91; minor = 8; rare = 1; break;
                case BotSkillTier.Grandmaster: common = 90; minor = 8; rare = 2; break;
                default: common = 100; minor = 0; rare = 0; break;
            }

            int roll = Utility.Random(common + minor + rare);

            Type[] band = roll < common ? profile.Common
                        : roll < common + minor ? profile.Minor
                        : profile.Rare;

            // Upstream's fallback: an empty band falls back to common rather than making nothing.
            return band.Length > 0 ? band : profile.Common;
        }
    }
}
