// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotEconomyConfig.cs - bots.json `economy`: the starting purse and the price of a haul.
//
// NOTHING HERE IS UPSTREAM'S, and that is the point. uo-offline prices a haul with
// `hauled * Utility.RandomMinMax(2, 4)` (BotEconomy.cs:314) and starts a bot with a tier-scaled
// 40-1400 gold (EquipmentTable.cs:54-67); no file under playerbots/data sets a price at all. Sean's
// rule (23 September 2026) is that gold's source and sink are the stock NPC shopkeepers at player
// prices, so every number below is read off a stock file and the section's own `comment` says
// which. ECONOMY.md section 3 is the derivation in full.
//
// WHAT IS DATA AND WHAT IS CODE. The prices, the ladders, the margin and the purse are data -
// they are judgements, and a shard owner may want a different one. The SMELT RATIO is code
// (BotPrice below), because it is not a judgement: it is what BaseOre.OnDoubleClick does to a
// pile of that graphic (Ore.cs:385-398), and a config that disagreed with it would price a load
// at one yield and hand the buyer another.

using System;
using System.Collections.Generic;

using Newtonsoft.Json;

using Server.Items;

namespace Server.Custom
{
    /// <summary>The ore half of the price: the NPC's ingot price and the colour ladder.</summary>
    public class BotOrePriceConfig : IValidatableConfig
    {
        /// <summary>
        /// What an NPC blacksmith actually pays a player for one plain IronIngot. No NPC buys ore,
        /// so the ingot it smelts into is the base. SBBlacksmith lists 4 (SBBlacksmith.cs:139), but
        /// with the vendor economy on the price paid is max(1, (int)(buy x .75)) against the buy
        /// entry of 5 (SBBlacksmith.cs:34, GenericSell.cs:40-56) - which is 3.
        /// </summary>
        [JsonProperty("ingotPrice")]
        public int IngotPrice { get; set; }

        /// <summary>
        /// CraftResource name -> multiple of the plain price. The stock community collection's
        /// ingot ladder (Bonnie.cs:70-78), the only per-colour number anywhere in stock.
        /// </summary>
        [JsonProperty("ladder", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, int> Ladder { get; set; }

        [JsonConstructor]
        public BotOrePriceConfig()
        {
            IngotPrice = 3;
            Ladder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Iron", 1 }, { "DullCopper", 2 }, { "ShadowIron", 4 }, { "Copper", 8 }, { "Bronze", 12 },
                { "Gold", 18 }, { "Agapite", 24 }, { "Verite", 31 }, { "Valorite", 39 }
            };
        }

        public void Validate(ConfigErrors errors)
        {
            if (IngotPrice <= 0)
            {
                errors.Add("economy.ore.ingotPrice is {0}; it must be a positive number of gold.", IngotPrice);
            }

            BotPrice.ValidateLadder(errors, "economy.ore.ladder", Ladder, CraftResourceType.Metal);
        }
    }

    /// <summary>The wood half: the NPC's log price and the colour ladder.</summary>
    public class BotWoodPriceConfig : IValidatableConfig
    {
        /// <summary>What an NPC carpenter pays a player for one plain Log (SBCarpenter.cs:101).</summary>
        [JsonProperty("logPrice")]
        public int LogPrice { get; set; }

        /// <summary>
        /// CraftResource name -> multiple of the plain price. The stock community collection's
        /// board ladder (Zorda.cs:69-75); one log cuts to one board of its own wood.
        /// </summary>
        [JsonProperty("ladder", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, int> Ladder { get; set; }

        [JsonConstructor]
        public BotWoodPriceConfig()
        {
            LogPrice = 1;
            Ladder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "RegularWood", 1 }, { "OakWood", 3 }, { "AshWood", 6 }, { "YewWood", 9 },
                { "Heartwood", 12 }, { "Bloodwood", 24 }, { "Frostwood", 48 }
            };
        }

        public void Validate(ConfigErrors errors)
        {
            if (LogPrice <= 0)
            {
                errors.Add("economy.wood.logPrice is {0}; it must be a positive number of gold.", LogPrice);
            }

            BotPrice.ValidateLadder(errors, "economy.wood.ladder", Ladder, CraftResourceType.Wood);
        }
    }

    public class BotEconomyConfig : IValidatableConfig
    {
        /// <summary>
        /// The largest purse that is still one Gold stack (Item.WillStack refuses past 60,000,
        /// Server/Item.cs:2132). A purse split over two stacks would still be counted correctly,
        /// but it is one more thing to be true, and nothing wants a purse that size.
        /// </summary>
        public const int MaxStartingGold = 60000;

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        /// <summary>What every bot, named or throwaway, is born holding. Sean, 23 September 2026.</summary>
        [JsonProperty("startingGold")]
        public int StartingGold { get; set; }

        /// <summary>
        /// The buyer pays this percentage of the load's worth at NPC prices, rounded up once per
        /// trade. At or a little above the NPC price, so the walk is worth it.
        /// </summary>
        [JsonProperty("marginPercent")]
        public int MarginPercent { get; set; }

        [JsonProperty("ore")]
        public BotOrePriceConfig Ore { get; set; }

        [JsonProperty("wood")]
        public BotWoodPriceConfig Wood { get; set; }

        [JsonConstructor]
        public BotEconomyConfig()
        {
            StartingGold = 10000;
            MarginPercent = 125;
            Ore = new BotOrePriceConfig();
            Wood = new BotWoodPriceConfig();
        }

        public void Validate(ConfigErrors errors)
        {
            if (StartingGold < 0 || StartingGold > MaxStartingGold)
            {
                errors.Add(
                    "economy.startingGold is {0}; it must be between 0 and {1}, one stack of gold.",
                    StartingGold,
                    MaxStartingGold);
            }

            // Below 100 the buyer pays LESS than the NPC would, and the walk is not worth it -
            // which is the one thing Sean's rule says the margin is for.
            if (MarginPercent < 100 || MarginPercent > 1000)
            {
                errors.Add(
                    "economy.marginPercent is {0}; it must be between 100 (the NPC price) and 1000.",
                    MarginPercent);
            }

            if (Ore == null)
            {
                Ore = new BotOrePriceConfig();
            }

            Ore.Validate(errors);

            if (Wood == null)
            {
                Wood = new BotWoodPriceConfig();
            }

            Wood.Validate(errors);
        }
    }

    /// <summary>
    /// The arithmetic of a price, in integers. Worth is counted in HALF-GOLD, because the smallest
    /// ore pile smelts to half an ingot a unit; the price is rounded up once, over the whole load.
    /// </summary>
    public static class BotPrice
    {
        public const int LargeOre = 0x19B9;
        public const int SmallOre = 0x19B7;

        /// <summary>
        /// Half-ingots one ore unit of this graphic smelts to, as BaseOre does it (Ore.cs:385-398):
        /// a large pile two ingots a unit, a small one half, anything else one.
        /// </summary>
        public static int HalfIngotsFor(int itemId)
        {
            if (itemId == LargeOre)
            {
                return 4;
            }

            if (itemId == SmallOre)
            {
                return 1;
            }

            return 2;
        }

        /// <summary>
        /// The smallest number of units of this stack that can change hands. Two for a small ore
        /// pile, because the smelt takes them in pairs and leaves an odd one (Ore.cs:387-390);
        /// one for anything else.
        /// </summary>
        public static int StepFor(Item stack)
        {
            return stack is BaseOre && stack.ItemID == SmallOre ? 2 : 1;
        }

        /// <summary>Products one step of this stack becomes: ingots for ore, boards for logs.</summary>
        public static int ProductsPerStep(Item stack)
        {
            if (stack is BaseOre)
            {
                return (HalfIngotsFor(stack.ItemID) * StepFor(stack)) / 2;
            }

            return 1;
        }

        /// <summary>What one step of this stack is worth at the NPC price, in half-gold.</summary>
        public static long WorthHalfPerStep(BotEconomyConfig economy, Item stack)
        {
            var ore = stack as BaseOre;

            if (ore != null)
            {
                return (long)HalfIngotsFor(ore.ItemID) * StepFor(ore) * economy.Ore.IngotPrice
                    * Rung(economy.Ore.Ladder, ore.Resource);
            }

            var log = stack as BaseLog;

            if (log != null)
            {
                return 2L * economy.Wood.LogPrice * Rung(economy.Wood.Ladder, log.Resource);
            }

            return 0;
        }

        /// <summary>The price of a load worth this much: the margin on it, rounded up once.</summary>
        public static int PriceFor(BotEconomyConfig economy, long worthHalf)
        {
            if (worthHalf <= 0)
            {
                return 0;
            }

            long price = ((worthHalf * economy.MarginPercent) + 199) / 200;

            return price > Int32.MaxValue ? Int32.MaxValue : (int)price;
        }

        /// <summary>
        /// The refined good one unit of this raw stack becomes, in its own colour: the ingot for an
        /// ore (CraftResourceInfo.ResourceTypes[0]), the board for a log ([1]) - the stock table in
        /// ResourceInfo.cs, read rather than constructing an item just to ask its type.
        /// </summary>
        public static Type ProductTypeFor(Item stack)
        {
            var ore = stack as BaseOre;

            if (ore != null)
            {
                CraftResourceInfo info = CraftResources.GetInfo(ore.Resource);

                return info == null || info.ResourceTypes.Length < 1 ? null : info.ResourceTypes[0];
            }

            var log = stack as BaseLog;

            if (log != null)
            {
                CraftResourceInfo info = CraftResources.GetInfo(log.Resource);

                return info == null || info.ResourceTypes.Length < 2 ? null : info.ResourceTypes[1];
            }

            return null;
        }

        private static int Rung(Dictionary<string, int> ladder, CraftResource resource)
        {
            int rung;

            return ladder != null && ladder.TryGetValue(resource.ToString(), out rung) ? rung : 1;
        }

        internal static void ValidateLadder(
            ConfigErrors errors, string where, Dictionary<string, int> ladder, CraftResourceType kind)
        {
            if (ladder == null)
            {
                errors.Add("{0} is missing; it must give every {1} resource a rung.", where, kind);
                return;
            }

            foreach (var entry in ladder)
            {
                CraftResource resource;

                if (!Enum.TryParse(entry.Key, true, out resource) || CraftResources.GetType(resource) != kind)
                {
                    errors.Add("{0} names '{1}', which is not a {2} CraftResource.", where, entry.Key, kind);
                    continue;
                }

                if (entry.Value <= 0)
                {
                    errors.Add("{0}.{1} is {2}; a rung must be positive.", where, entry.Key, entry.Value);
                }
            }

            // Complete, so a colour a miner can bring home is never priced by a silent default.
            foreach (CraftResource resource in Enum.GetValues(typeof(CraftResource)))
            {
                if (CraftResources.GetType(resource) == kind && !ladder.ContainsKey(resource.ToString()))
                {
                    errors.Add("{0} has no rung for {1}.", where, resource);
                }
            }
        }
    }
}
