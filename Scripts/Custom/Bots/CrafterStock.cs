// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// CrafterStock.cs — an artisan's raw materials, counted in its real backpack.
//
// Ported close to upstream, minus the money. Their GoldOnHand / SpendGold pair
// backed two things this session deliberately does not do: buying a restock over
// the counter when the shop ran dry, and paying a gatherer for a haul. Both are
// the economy session (7f). What is left is the honest half — how much stock is
// in the pack, taking some out, putting some in — and that half is what the
// craft system and the delivery hook actually need.
//
// The consume path is BELT AND BRACES with ServUO's own. CraftItem.ConsumeRes
// does the real consuming when an item is made; Count is what the behaviour
// checks BEFORE attempting, so a dry crafter says so and stops swinging instead
// of failing a craft every cycle. Consume itself is used only by the delivery
// hook's mirror image and by tests.

using System;

using Server.Items;

namespace Server.Custom
{
    public static class CrafterStock
    {
        /// <summary>
        /// Upstream's cap on how much raw material an artisan hoards.
        ///
        /// It is not a pack-weight limit — the engine has one of those — it is a believability
        /// limit. A smith standing at a forge with nine hundred ingots is a warehouse, not a
        /// person, and the gatherers would keep feeding it for ever.
        /// </summary>
        public const int StockCap = 250;

        /// <summary>How much of this trade's material is in the pack, across every material type.</summary>
        public static int Count(PlayerBot bot, CrafterProfile profile)
        {
            Container pack = bot == null ? null : bot.Backpack;

            if (pack == null || profile == null || profile.Materials.Length == 0)
            {
                return 0;
            }

            return pack.GetAmount(profile.Materials, false);
        }

        /// <summary>
        /// Take material out of the pack. All or nothing.
        ///
        /// Upstream's shape kept deliberately: a partial consume would leave the pack short and
        /// the caller believing it had spent nothing.
        /// </summary>
        public static bool Consume(PlayerBot bot, CrafterProfile profile, int amount)
        {
            Container pack = bot == null ? null : bot.Backpack;

            if (pack == null || profile == null || amount <= 0)
            {
                return false;
            }

            if (Count(bot, profile) < amount)
            {
                return false;
            }

            // Greedy, in the profile's own order: a tailor spends cloth before leather.
            foreach (Type type in profile.Materials)
            {
                if (amount <= 0)
                {
                    break;
                }

                int have = pack.GetAmount(type, false);

                if (have <= 0)
                {
                    continue;
                }

                int take = Math.Min(have, amount);

                pack.ConsumeTotal(type, take, false);
                amount -= take;
            }

            return true;
        }

        /// <summary>
        /// Put material into the pack, up to StockCap. Returns how much was actually accepted.
        ///
        /// The return value is not decoration: the delivery hook reports it, and a gatherer that
        /// hauled sixty ore to a smith already holding two hundred and forty ingots needs to know
        /// only ten of them landed.
        /// </summary>
        public static int Add(PlayerBot bot, CrafterProfile profile, int amount)
        {
            if (bot == null || bot.Deleted || profile == null || profile.MakeMaterial == null || amount <= 0)
            {
                return 0;
            }

            int room = StockCap - Count(bot, profile);

            if (room <= 0)
            {
                return 0;
            }

            int accepted = Math.Min(room, amount);
            Item stock = profile.MakeMaterial(accepted);

            if (stock == null)
            {
                return 0;
            }

            if (!bot.AddToBackpack(stock))
            {
                stock.Delete();
                return 0;
            }

            return accepted;
        }
    }
}
