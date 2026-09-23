// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotHaul.cs - moving a load without destroying it.
//
// REVIEW.md F5. Delivery used to count a haul by DELETING it: TakeYield walked the pack and the
// panniers, summed Amount, deleted every matching item, and handed the caller an int. The caller
// then went looking for somebody to give it to. A smith at CrafterStock.StockCap, or a bank box
// that refused the drop, meant the load had already stopped existing - and the counter said it
// had been delivered.
//
// THE RULE THIS FILE EXISTS TO KEEP: the amount that moves is the amount the receiver took, and
// the remainder stays exactly where it was. Nothing here constructs a replacement stack and
// nothing here deletes a unit. Offered COUNTS; Consume removes precisely what was accepted;
// MoveToBank moves the actual items and leaves behind whatever the box would not hold.
//
// WHY THAT ALSO PRESERVES ITEM IDENTITY. The old Bank() did Activator.CreateInstance(raw) and set
// Amount, which is a new plain stack: hue, name, weight and loot type of the originals were gone.
// Moving the real item keeps all of it, and the one place a stack has to be divided goes through
// Mobile.LiftItemDupe (Server/Mobile.cs:4697), which is the engine's own split and copies every
// one of those properties onto the piece it creates.
//
// UPSTREAM HAS NO ANSWER HERE and this is the named seam. BotEconomy.DeliverMaterials
// (uo-offline BotEconomy.cs:273-345) deletes the haul exactly as ours did; its overflow past the
// stock cap "goes to the shop", which is a sentence rather than a code path. What upstream DOES
// answer is the pack beast - its caller disposes of the animal after the hand-over returns
// (TravelerBehavior.cs:2484-2508), not before it - and ReleaseIfEmpty is that rule.
//
// A GOOD IS A FAMILY, NOT A TYPE (7f-1, 23 September 2026). The harvest system gives a miner the
// ore of the vein it is standing on once its skill reaches that vein's (HarvestSystem.cs:142-143,
// :368; Mining.GetResourceType returns the vein resource's Types[0], Mining.cs:229), and a lumber
// bank's wood the same way: dull copper and oak come home in the same pack as iron and plain logs. Every count
// here used to be `item.GetType() == raw` against IronOre or Log, so a coloured pile was invisible
// - not offered, not banked, not in the books, and not in the pack-full check, which let a miner
// swing on into HarvestSystem's silent delete. Sean: the whole ore family and the whole log family
// are carried, capacity-checked, ledgered and priced by colour. So every count and every move
// below takes a family - BaseOre or BaseLog - and matches by IsAssignableFrom; the class-level
// constants (BotHarvest.YieldFor, CrafterProfile.RawGood) stay the plain types, because they name
// a TRADE, and the family is derived from them (FamilyOf).

using System;
using System.Collections.Generic;

using Server.Items;

namespace Server.Custom
{
    public static class BotHaul
    {
        /// <summary>
        /// Every raw good a bot can be holding: the family of every distinct BotHarvest yield,
        /// which is BaseOre and BaseLog.
        ///
        /// Derived rather than listed so a new gathering class cannot be added without the ledger
        /// learning about it. Refined stock - a smith's ingots, a carpenter's boards - is
        /// deliberately NOT here: it is a different good in a different custody, and it leaves the
        /// raw ledger at the moment a trade turns it into product (BotTrade).
        /// </summary>
        public static IList<Type> TrackedTypes
        {
            get
            {
                if (_tracked == null)
                {
                    var types = new List<Type>();

                    foreach (BotClass cls in Enum.GetValues(typeof(BotClass)))
                    {
                        Type yield = FamilyOf(BotHarvest.YieldFor(cls));

                        if (yield != null && !types.Contains(yield))
                        {
                            types.Add(yield);
                        }
                    }

                    _tracked = types;
                }

                return _tracked;
            }
        }

        private static IList<Type> _tracked;

        /// <summary>
        /// The family a raw good belongs to: BaseOre for any ore, BaseLog for any log, and the
        /// type itself for anything else. Null in, null out.
        /// </summary>
        public static Type FamilyOf(Type raw)
        {
            if (raw == null)
            {
                return null;
            }

            if (typeof(BaseOre).IsAssignableFrom(raw))
            {
                return typeof(BaseOre);
            }

            if (typeof(BaseLog).IsAssignableFrom(raw))
            {
                return typeof(BaseLog);
            }

            return raw;
        }

        /// <summary>Is this item a unit of this good - of the type, or of any type in the family?</summary>
        public static bool IsOf(Item item, Type good)
        {
            return item != null && !item.Deleted && good != null && good.IsAssignableFrom(item.GetType());
        }

        /// <summary>
        /// How many units of each concrete type of this good are in this container, so a record
        /// can name what it was - "ValoriteOre", not "BaseOre". Removes nothing.
        /// </summary>
        public static Dictionary<Type, int> ByType(Container container, Type good)
        {
            var counts = new Dictionary<Type, int>();

            if (container == null || good == null)
            {
                return counts;
            }

            foreach (Item item in container.Items)
            {
                if (!IsOf(item, good))
                {
                    continue;
                }

                int amount;

                counts.TryGetValue(item.GetType(), out amount);
                counts[item.GetType()] = amount + item.Amount;
            }

            return counts;
        }

        /// <summary>
        /// The stacks of this good the bot could hand over, pack first and then the panniers, in
        /// container order - the order a trade quotes and takes them in. Removes nothing.
        /// </summary>
        public static List<Item> StacksOf(PlayerBot bot, Type good)
        {
            var stacks = new List<Item>();

            if (bot == null || bot.Deleted)
            {
                return stacks;
            }

            AddStacks(stacks, bot.Backpack, good);
            AddStacks(stacks, BotPackAnimals.PanniersOf(bot), good);

            return stacks;
        }

        private static void AddStacks(List<Item> stacks, Container container, Type good)
        {
            if (container == null)
            {
                return;
            }

            foreach (Item item in container.Items)
            {
                if (IsOf(item, good) && item.Amount > 0)
                {
                    stacks.Add(item);
                }
            }
        }

        /// <summary>How many units of a good are in this container. Removes nothing.</summary>
        public static int InContainer(Container container, Type raw)
        {
            if (container == null || raw == null)
            {
                return 0;
            }

            int amount = 0;

            foreach (Item item in container.Items)
            {
                if (IsOf(item, raw))
                {
                    amount += item.Amount;
                }
            }

            return amount;
        }

        /// <summary>Every tracked good in this container, summed. Removes nothing.</summary>
        public static int InContainer(Container container)
        {
            int amount = 0;
            IList<Type> tracked = TrackedTypes;

            for (int i = 0; i < tracked.Count; i++)
            {
                amount += InContainer(container, tracked[i]);
            }

            return amount;
        }

        /// <summary>
        /// What this bot can hand over: the pack and the panniers. The bank box is not offered -
        /// a load already in a bank box has been settled, and re-offering it would move the same
        /// units twice.
        /// </summary>
        public static int Offered(PlayerBot bot, Type raw)
        {
            if (bot == null || bot.Deleted)
            {
                return 0;
            }

            return InContainer(bot.Backpack, raw) + InContainer(BotPackAnimals.PanniersOf(bot), raw);
        }

        /// <summary>
        /// Everything this bot holds anywhere: pack, panniers and bank box.
        ///
        /// FindBankNoCreate, never the BankBox property. Mobile.BankBox mints a box on a miss
        /// (Server/Mobile.cs:10511) and this is called from a census that walks every live bot -
        /// minting a thousand bank boxes to count what is in them is the exact mistake
        /// BotOrphans.WriteCensus made, and SHARD.md records what it cost.
        /// </summary>
        public static int Custody(PlayerBot bot, Type raw)
        {
            if (bot == null || bot.Deleted)
            {
                return 0;
            }

            return Offered(bot, raw) + InContainer(bot.FindBankNoCreate(), raw);
        }

        /// <summary>Every tracked good this bot holds anywhere.</summary>
        public static int Custody(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return 0;
            }

            return InContainer(bot.Backpack) + InContainer(BotPackAnimals.PanniersOf(bot))
                + InContainer(bot.FindBankNoCreate());
        }

        /// <summary>
        /// Remove exactly this many units, pack first and then the panniers. Returns how many were
        /// actually removed, which is never more than was asked for.
        ///
        /// This is the ONLY place in the delivery path that destroys a unit, and it runs after a
        /// receiver has said how much it is taking - the units are gone because they became
        /// something else in the receiver's hands (ore into ingots, CrafterStock.Add), not because
        /// counting them was convenient.
        /// </summary>
        public static int Consume(PlayerBot bot, Type raw, int amount)
        {
            if (bot == null || bot.Deleted || raw == null || amount <= 0)
            {
                return 0;
            }

            int taken = ConsumeFrom(bot.Backpack, raw, amount);

            if (taken < amount)
            {
                taken += ConsumeFrom(BotPackAnimals.PanniersOf(bot), raw, amount - taken);
            }

            return taken;
        }

        /// <summary>Remove up to this many units from one container. Returns how many went.</summary>
        public static int ConsumeFrom(Container container, Type raw, int amount)
        {
            if (container == null || raw == null || amount <= 0)
            {
                return 0;
            }

            int taken = 0;

            // A snapshot: Item.Delete detaches from container.Items, and mutating the collection
            // the foreach is walking throws.
            foreach (Item item in new List<Item>(container.Items))
            {
                if (taken >= amount)
                {
                    break;
                }

                if (!IsOf(item, raw))
                {
                    continue;
                }

                int want = amount - taken;

                if (item.Amount <= want)
                {
                    taken += item.Amount;
                    item.Delete();
                }
                else
                {
                    // Part of a stack. Decrementing Amount is what Container.ConsumeTotal does,
                    // and it keeps the item - and everything on it - in place.
                    item.Amount -= want;
                    taken += want;
                }
            }

            return taken;
        }

        /// <summary>
        /// Move up to this many units into the bot's own bank box, as the items they already are.
        /// Returns how many landed.
        ///
        /// A refusal is an answer, not an error: whatever the box will not hold stays exactly
        /// where it was, in the pack or the panniers, and is offered again at the next hand-over.
        /// The old Bank() deleted it.
        /// </summary>
        public static int MoveToBank(PlayerBot bot, Type raw, int amount)
        {
            if (bot == null || bot.Deleted || raw == null || amount <= 0)
            {
                return 0;
            }

            BankBox box = bot.BankBox;

            if (box == null)
            {
                return 0;
            }

            int moved = MoveToBankFrom(bot, box, bot.Backpack, raw, amount);

            if (moved < amount)
            {
                moved += MoveToBankFrom(bot, box, BotPackAnimals.PanniersOf(bot), raw, amount - moved);
            }

            return moved;
        }

        private static int MoveToBankFrom(PlayerBot bot, BankBox box, Container from, Type raw, int amount)
        {
            if (from == null || amount <= 0)
            {
                return 0;
            }

            int moved = 0;

            foreach (Item item in new List<Item>(from.Items))
            {
                if (moved >= amount)
                {
                    break;
                }

                if (!IsOf(item, raw))
                {
                    continue;
                }

                int want = amount - moved;

                if (item.Amount <= want)
                {
                    // READ THE SIZE FIRST. Container.TryDropItem merges into a matching stack
                    // when it finds one (Server/Items/Container.cs), and StackWith DELETES the
                    // item it merged; its Amount happens to survive the delete today, so counting
                    // afterwards works by accident rather than by contract.
                    int size = item.Amount;

                    // The whole stack, moved intact. A refused drop leaves it where it is and
                    // ends the attempt - a box that would not take this stack will not take the
                    // next one either.
                    if (!box.TryDropItem(bot, item, false))
                    {
                        return moved;
                    }

                    moved += size;
                    continue;
                }

                // More than is wanted. LiftItemDupe leaves `item` holding `want` and puts the
                // remainder into a new stack beside it, copying hue, name, weight and loot type.
                Item remainder = Mobile.LiftItemDupe(item, want);

                if (remainder == null)
                {
                    // The type has no parameterless constructor, so it cannot be divided. Offer
                    // the whole stack instead; refusing is still better than destroying it.
                    if (box.TryDropItem(bot, item, false))
                    {
                        moved += item.Amount;
                    }

                    return moved;
                }

                if (box.TryDropItem(bot, item, false))
                {
                    moved += want;
                }
                else
                {
                    // Put the split back. This deletes an Item OBJECT and not a unit: the
                    // remainder absorbs the whole amount first, so the count is unchanged.
                    remainder.Amount += item.Amount;
                    item.Delete();
                }

                return moved;
            }

            return moved;
        }

        /// <summary>
        /// Rule 2: the beast is released once the hand-over has completed, and not before.
        ///
        /// Delivery used to call BotPackAnimals.Release unconditionally, five lines before it had
        /// even looked for a buyer - and Release deletes the beast with whatever is still in the
        /// panniers. An interrupted or refused hand-over now leaves animal and load together, and
        /// the pair walks on to the next delivery point.
        ///
        /// Every tracked good, not just this bot's own yield: a beast carrying somebody else's ore
        /// is still carrying a load.
        /// </summary>
        public static void ReleaseIfEmpty(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            Container panniers = BotPackAnimals.PanniersOf(bot);

            if (panniers != null && InContainer(panniers) > 0)
            {
                return;
            }

            BotPackAnimals.Release(bot);
        }
    }
}
