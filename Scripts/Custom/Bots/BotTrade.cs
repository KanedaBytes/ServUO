// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotTrade.cs - THE one path by which goods and gold change hands. ECONOMY.md is its contract.
//
// A trade is one atomic step: the goods leave the seller, the gold leaves the buyer, the product
// lands with the buyer, the gold lands with the seller, and the trade is written down - or nothing
// happens at all. Every later 7f feature goes through here; a feature that moves gold between two
// parties any other way is a bug in that feature.
//
// TWO HALVES, deliberately apart:
//
//   Quote    reads, and changes nothing. What the seller has (every colour of the good), what the
//            buyer has room for, what the buyer's gold covers at the price - and so how much it
//            takes. The F5 rule, extended to gold: accepted = min(offered, room, affordable), and
//            the remainder stays with the seller, in the stacks it was already in.
//   Execute  moves exactly that, validating first and recording an undo for every step, so a
//            refusal or an exception at any step unwinds everything before it in reverse. The one
//            irreversible step - deleting the staged goods - comes last and cannot fail.
//
// UPSTREAM (uo-offline BotEconomy.DeliverMaterials, BotEconomy.cs:273-351) deletes the whole haul
// before it has found a buyer, pays `hauled x RandomMinMax(2,4)` from the crafter's pack, mints the
// gatherer's gold, refines one-for-one past the stock cap, and pays the gatherer anyway - out of
// nowhere - when there is no buyer or the buyer is broke (:340-344). Every one of those is a named
// seam in ECONOMY.md section 5. What we kept is theirs: the buyer spends PACK gold
// (CrafterStock.SpendGold, CrafterStock.cs:110-118), and the price is for the whole load.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Items;

namespace Server.Custom
{
    public enum BotTradeOutcome
    {
        /// <summary>Everything moved.</summary>
        Completed,

        /// <summary>Nothing moved, by decision: no gold, no room, nothing on offer, a stale quote.</summary>
        Refused,

        /// <summary>Something failed part-way and every step was undone. Nothing moved.</summary>
        RolledBack
    }

    /// <summary>What cut a trade short, or stopped it.</summary>
    public enum BotTradeLimit
    {
        /// <summary>The whole offer was taken.</summary>
        None,

        /// <summary>The buyer's gold ran out.</summary>
        Gold,

        /// <summary>The buyer's bench reached StockCap.</summary>
        Room,

        /// <summary>An odd small ore unit, which the smelt cannot take alone (Ore.cs:387-390).</summary>
        Pairs,

        /// <summary>The seller had nothing of the good.</summary>
        Nothing,

        /// <summary>A stack with no product to become - a type outside the stock resource tables.</summary>
        NoProduct,

        /// <summary>The world changed between Quote and Execute.</summary>
        Stale
    }

    /// <summary>One stack of the seller's, and how much of it the buyer takes.</summary>
    public sealed class BotTradeLine
    {
        public Item Stack { get; internal set; }
        public Container Origin { get; internal set; }
        public int Units { get; internal set; }
        public Type Product { get; internal set; }
        public int Products { get; internal set; }
        public long WorthHalf { get; internal set; }

        /// <summary>Type and graphic are read at quote time: the stack itself is deleted at commit.</summary>
        public string TypeName { get; internal set; }
        public int ItemId { get; internal set; }
    }

    public sealed class BotTradeQuote
    {
        public BotTradeQuote()
        {
            Lines = new List<BotTradeLine>();
        }

        public PlayerBot Seller { get; internal set; }
        public PlayerBot Buyer { get; internal set; }
        public string DestinationId { get; internal set; }

        /// <summary>The family on offer: BaseOre or BaseLog.</summary>
        public Type Good { get; internal set; }

        public List<BotTradeLine> Lines { get; private set; }

        public int Offered { get; internal set; }
        public int Accepted { get; internal set; }
        public int Products { get; internal set; }
        public long WorthHalf { get; internal set; }
        public int Price { get; internal set; }

        /// <summary>What the WHOLE offer would have cost - the number a broke buyer could not cover.</summary>
        public int FullPrice { get; internal set; }

        public int Room { get; internal set; }
        public int BuyerGold { get; internal set; }
        public BotTradeLimit Limit { get; internal set; }
    }

    public static class BotTrade
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string JournalPath = "Data/Live/trade-journal.jsonl";

        public static readonly BotJournalFile Journal = new BotJournalFile(JournalPath, BotGoodsLedger.MaxBytes);

        /// <summary>
        /// FIXTURE SEAM, and nothing else sets it: after this step completes, Execute behaves as if
        /// the next one had failed, so EconomyFixtures can prove each undo restores the world
        /// exactly. Zero is off. Steps: 2 debit buyer, 3 stage goods, 4 credit products, 5 credit
        /// seller.
        /// </summary>
        internal static int FailAfterStep;

        private static int _sequence;

        // ---- the quote ----

        /// <summary>
        /// What would move if this seller sold this buyer everything of the profile's raw good it
        /// has on offer (pack, then panniers). Changes nothing.
        /// </summary>
        public static BotTradeQuote Quote(PlayerBot seller, PlayerBot buyer, CrafterProfile profile, string destinationId)
        {
            var quote = new BotTradeQuote
            {
                Seller = seller,
                Buyer = buyer,
                DestinationId = destinationId,
                Good = profile == null ? null : BotHaul.FamilyOf(profile.RawGood),
                Limit = BotTradeLimit.None
            };

            if (seller == null || buyer == null || profile == null || quote.Good == null)
            {
                quote.Limit = BotTradeLimit.Nothing;
                return quote;
            }

            BotEconomyConfig economy = BotSystem.Store.Economy;
            List<Item> stacks = BotHaul.StacksOf(seller, quote.Good);

            int gold = BotGoldLedger.PackGold(buyer);
            int room = CrafterStock.Room(buyer, profile);

            quote.BuyerGold = gold;
            quote.Room = room;

            long allWorth = 0;
            long worth = 0;
            int products = 0;
            bool cutByGold = false, cutByRoom = false, cutByPairs = false, noProduct = false;

            foreach (Item stack in stacks)
            {
                quote.Offered += stack.Amount;

                Type product = BotPrice.ProductTypeFor(stack);
                long perStep = BotPrice.WorthHalfPerStep(economy, stack);
                int step = BotPrice.StepFor(stack);
                int perStepProducts = BotPrice.ProductsPerStep(stack);

                if (product == null || perStep <= 0 || perStepProducts <= 0)
                {
                    noProduct = true;
                    continue;
                }

                long steps = stack.Amount / step;

                allWorth += steps * perStep;

                if (stack.Amount % step != 0)
                {
                    cutByPairs = true;
                }

                // Room, in products.
                long byRoom = (room - products) / perStepProducts;

                // Gold: ceil((worth + k x perStep) x margin / 200) <= gold, which for integers is
                // (worth + k x perStep) x margin <= 200 x gold.
                long headroom = (200L * gold) - (worth * economy.MarginPercent);
                long byGold = headroom < 0 ? 0 : headroom / (perStep * economy.MarginPercent);

                long take = Math.Min(steps, Math.Min(byRoom, byGold));

                if (take < 0)
                {
                    take = 0;
                }

                if (take < steps)
                {
                    if (byGold < steps && byGold <= byRoom)
                    {
                        cutByGold = true;
                    }
                    else
                    {
                        cutByRoom = true;
                    }
                }

                if (take <= 0)
                {
                    continue;
                }

                quote.Lines.Add(new BotTradeLine
                {
                    Stack = stack,
                    Origin = stack.Parent as Container,
                    Units = (int)(take * step),
                    Product = product,
                    Products = (int)(take * perStepProducts),
                    WorthHalf = take * perStep,
                    TypeName = stack.GetType().Name,
                    ItemId = stack.ItemID
                });

                worth += take * perStep;
                products += (int)(take * perStepProducts);
            }

            foreach (BotTradeLine line in quote.Lines)
            {
                quote.Accepted += line.Units;
            }

            quote.Products = products;
            quote.WorthHalf = worth;
            quote.Price = BotPrice.PriceFor(economy, worth);
            quote.FullPrice = BotPrice.PriceFor(economy, allWorth);

            if (quote.Offered <= 0)
            {
                quote.Limit = BotTradeLimit.Nothing;
            }
            else if (cutByGold)
            {
                quote.Limit = BotTradeLimit.Gold;
            }
            else if (cutByRoom)
            {
                quote.Limit = BotTradeLimit.Room;
            }
            else if (cutByPairs && quote.Accepted < quote.Offered)
            {
                quote.Limit = BotTradeLimit.Pairs;
            }
            else if (noProduct && quote.Accepted < quote.Offered)
            {
                quote.Limit = BotTradeLimit.NoProduct;
            }

            return quote;
        }

        // ---- the exchange ----

        /// <summary>
        /// Move exactly what the quote says, atomically, and write it down. Game thread only.
        /// A quote that accepts nothing is a refusal, and is journalled as one.
        /// </summary>
        public static BotTradeOutcome Execute(BotTradeQuote quote)
        {
            if (quote == null)
            {
                return BotTradeOutcome.Refused;
            }

            PlayerBot seller = quote.Seller;
            PlayerBot buyer = quote.Buyer;

            int sellerBefore = BotGoldLedger.PackGold(seller);
            int buyerBefore = BotGoldLedger.PackGold(buyer);

            if (quote.Accepted <= 0 || quote.Price <= 0)
            {
                return Finish(quote, BotTradeOutcome.Refused, quote.Limit, null, sellerBefore, buyerBefore);
            }

            // 1. VALIDATE, and nothing has moved yet. The quote was read a moment ago on the same
            // thread, so a mismatch here is a caller that let the world run in between.
            string stale = Validate(quote);

            if (stale != null)
            {
                return Finish(quote, BotTradeOutcome.Refused, BotTradeLimit.Stale, stale, sellerBefore, buyerBefore);
            }

            var undo = new List<Action>();
            var staged = new List<Item>();
            string failure = null;
            int step = 1;

            try
            {
                // 2. DEBIT THE BUYER. ConsumeTotal counts first and takes only if all of it is there
                // (Container.cs:893+), so this step is all-or-nothing on its own.
                step = 2;

                if (!buyer.Backpack.ConsumeTotal(typeof(Gold), quote.Price, true))
                {
                    throw new TradeFailed("the buyer's pack would not give up " + quote.Price + " gold");
                }

                int price = quote.Price;
                Container buyerPack = buyer.Backpack;

                undo.Add(() => buyerPack.DropItem(new Gold(price)));
                FailIf(step);

                // 3. STAGE THE GOODS: exactly the accepted units, taken off the seller onto
                // Map.Internal as the items they already are. A part stack is divided with the
                // engine's own split (Mobile.LiftItemDupe, Server/Mobile.cs:4697), which leaves the
                // remainder beside it with every property copied.
                step = 3;

                foreach (BotTradeLine line in quote.Lines)
                {
                    Item stack = line.Stack;
                    Container origin = line.Origin;

                    if (line.Units < stack.Amount && Mobile.LiftItemDupe(stack, line.Units) == null)
                    {
                        throw new TradeFailed(line.TypeName + " cannot be divided");
                    }

                    stack.Internalize();
                    staged.Add(stack);

                    Item moved = stack;

                    undo.Add(() =>
                    {
                        // Merges back into the remainder when there is one; a refusal still puts it
                        // in the container, because DropItem never refuses.
                        if (!origin.TryDropItem(seller, moved, false))
                        {
                            origin.DropItem(moved);
                        }

                        staged.Remove(moved);
                    });
                }

                FailIf(step);

                // 4. CREDIT THE PRODUCT to the buyer: each colour its own ingot or board.
                // TryDropItem, never AddToBackpack - that drops at the buyer's feet on a refusal.
                step = 4;

                foreach (KeyValuePair<Type, int> made in ProductsOf(quote))
                {
                    var product = (Item)Activator.CreateInstance(made.Key);

                    product.Amount = made.Value;

                    if (!buyerPack.TryDropItem(buyer, product, false))
                    {
                        product.Delete();
                        throw new TradeFailed("the buyer's pack would not hold " + made.Value + " " + made.Key.Name);
                    }

                    Type type = made.Key;
                    int count = made.Value;

                    undo.Add(() => buyerPack.ConsumeTotal(type, count, true));
                }

                FailIf(step);

                // 5. CREDIT THE SELLER. The last step that can refuse.
                step = 5;

                Container sellerPack = seller.Backpack;
                var coin = new Gold(price);

                if (!sellerPack.TryDropItem(seller, coin, false))
                {
                    coin.Delete();
                    throw new TradeFailed("the seller's pack would not hold " + price + " gold");
                }

                undo.Add(() => sellerPack.ConsumeTotal(typeof(Gold), price, true));
                FailIf(step);
            }
            catch (Exception ex)
            {
                failure = ex is TradeFailed
                    ? ex.Message
                    : String.Format("step {0} threw {1}: {2}", step, ex.GetType().Name, ex.Message);

                Unwind(undo, quote, failure);
            }

            if (failure != null)
            {
                return Finish(quote, BotTradeOutcome.RolledBack, quote.Limit, failure, sellerBefore, buyerBefore);
            }

            // 6. COMMIT. The only irreversible step, and it cannot fail: the staged goods are
            // deleted because they became the product in the buyer's pack.
            foreach (Item item in staged)
            {
                item.Delete();
            }

            // 7. THE BOOKS, in the same tick as the goods and the gold.
            BotGoldLedger.NotePaid(buyer, quote.Price);
            BotGoldLedger.NoteReceived(seller, quote.Price);
            BotGoodsLedger.NoteAccepted(seller, quote.Accepted);

            return Finish(quote, BotTradeOutcome.Completed, quote.Limit, null, sellerBefore, buyerBefore);
        }

        private static string Validate(BotTradeQuote quote)
        {
            if (quote.Seller == null || quote.Seller.Deleted || quote.Seller.Backpack == null)
            {
                return "the seller is gone or has no pack";
            }

            if (quote.Buyer == null || quote.Buyer.Deleted || quote.Buyer.Backpack == null)
            {
                return "the buyer is gone or has no pack";
            }

            foreach (BotTradeLine line in quote.Lines)
            {
                if (line.Stack == null || line.Stack.Deleted || line.Origin == null
                    || line.Stack.Parent != line.Origin || line.Stack.Amount < line.Units)
                {
                    return "a quoted stack of " + line.TypeName + " is no longer where the quote found it";
                }
            }

            if (BotGoldLedger.PackGold(quote.Buyer) < quote.Price)
            {
                return "the buyer no longer holds the price";
            }

            return null;
        }

        private static void FailIf(int step)
        {
            if (FailAfterStep == step)
            {
                throw new TradeFailed("fixture failure after step " + step);
            }
        }

        /// <summary>Every step so far, undone in reverse. An undo that throws is logged and the rest still run.</summary>
        private static void Unwind(List<Action> undo, BotTradeQuote quote, string failure)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    undo[i]();
                }
                catch (Exception ex)
                {
                    // Loud, because the census will now find the difference and fail a ledger - and
                    // this line is what says where to look.
                    Log.Error(ex, "A trade between {0} and {1} could not undo step {2} after '{3}'.",
                        quote.Seller == null ? "?" : quote.Seller.Name,
                        quote.Buyer == null ? "?" : quote.Buyer.Name,
                        i + 2,
                        failure);
                }
            }
        }

        private static Dictionary<Type, int> ProductsOf(BotTradeQuote quote)
        {
            var made = new Dictionary<Type, int>();

            foreach (BotTradeLine line in quote.Lines)
            {
                int count;

                made.TryGetValue(line.Product, out count);
                made[line.Product] = count + line.Products;
            }

            return made;
        }

        // ---- the record ----

        private static BotTradeOutcome Finish(
            BotTradeQuote quote, BotTradeOutcome outcome, BotTradeLimit limit, string reason, int sellerBefore, int buyerBefore)
        {
            BotGoldLedger.NoteTrade(outcome, limit);

            _sequence++;

            PlayerBot seller = quote.Seller;
            PlayerBot buyer = quote.Buyer;
            bool completed = outcome == BotTradeOutcome.Completed;

            var line = new StringBuilder(640);

            line.Append('{');
            line.Append("\"utc\":").Append(Json.Quote(DateTime.UtcNow.ToString("o")));
            line.Append(",\"bootId\":").Append(Json.Quote(PersistenceGeneration.BootId ?? ""));
            line.Append(",\"generation\":").Append(PersistenceGeneration.Current);
            line.Append(",\"trade\":").Append(_sequence);
            line.Append(",\"outcome\":").Append(Json.Quote(OutcomeName(outcome)));
            line.Append(",\"limit\":").Append(Json.Quote(limit.ToString().ToLowerInvariant()));

            if (reason != null)
            {
                line.Append(",\"reason\":").Append(Json.Quote(reason));
            }

            line.Append(",\"seller\":");
            Party(line, seller);
            line.Append(",\"buyer\":");
            Party(line, buyer);

            line.Append(",\"destination\":").Append(Json.Quote(quote.DestinationId ?? ""));
            line.Append(",\"map\":").Append(Json.Quote(seller == null || seller.Map == null ? "none" : seller.Map.Name));
            line.Append(",\"x\":").Append(seller == null ? 0 : seller.X);
            line.Append(",\"y\":").Append(seller == null ? 0 : seller.Y);

            line.Append(",\"goods\":[");

            for (int i = 0; i < quote.Lines.Count; i++)
            {
                BotTradeLine each = quote.Lines[i];

                line.Append(i == 0 ? "" : ",");
                line.Append("{\"type\":").Append(Json.Quote(each.TypeName));
                line.Append(",\"itemId\":").Append(Json.Quote(String.Format("0x{0:X}", each.ItemId)));
                line.Append(",\"units\":").Append(each.Units);
                line.Append(",\"product\":").Append(Json.Quote(each.Product == null ? "" : each.Product.Name));
                line.Append(",\"count\":").Append(each.Products);
                line.Append('}');
            }

            line.Append(']');
            line.Append(",\"offered\":").Append(quote.Offered);
            line.Append(",\"accepted\":").Append(completed ? quote.Accepted : 0);
            line.Append(",\"room\":").Append(quote.Room);
            line.Append(",\"worthHalf\":").Append(quote.WorthHalf);
            line.Append(",\"marginPercent\":").Append(BotSystem.Store.Economy.MarginPercent);
            line.Append(",\"price\":").Append(completed ? quote.Price : 0);
            line.Append(",\"quoted\":").Append(quote.Price);
            line.Append(",\"fullPrice\":").Append(quote.FullPrice);
            line.Append(",\"basis\":").Append(Json.Quote(Basis()));
            line.Append(",\"sellerGold\":[").Append(sellerBefore).Append(',').Append(BotGoldLedger.PackGold(seller)).Append(']');
            line.Append(",\"buyerGold\":[").Append(buyerBefore).Append(',').Append(BotGoldLedger.PackGold(buyer)).Append(']');
            line.Append('}');

            Journal.Append(line.ToString());

            if (outcome == BotTradeOutcome.RolledBack)
            {
                Log.Warn("A trade between {0} and {1} rolled back: {2}. Nothing moved.",
                    seller == null ? "?" : seller.Name,
                    buyer == null ? "?" : buyer.Name,
                    reason);
            }

            return outcome;
        }

        private static void Party(StringBuilder line, PlayerBot bot)
        {
            if (bot == null)
            {
                line.Append("null");
                return;
            }

            line.Append("{\"name\":").Append(Json.Quote(bot.Name));
            line.Append(",\"serial\":").Append(Json.Quote(bot.Serial.ToString()));
            line.Append(",\"class\":").Append(Json.Quote(bot.Class.ToString()));
            line.Append(",\"named\":").Append(bot.IsNamed ? "true" : "false");
            line.Append('}');
        }

        private static string Basis()
        {
            BotEconomyConfig economy = BotSystem.Store.Economy;

            return String.Format(
                "npc ingot {0}gp x smelt ratio x ore ladder, npc log {1}gp x wood ladder; margin {2}%, rounded up once",
                economy.Ore.IngotPrice,
                economy.Wood.LogPrice,
                economy.MarginPercent);
        }

        public static string OutcomeName(BotTradeOutcome outcome)
        {
            switch (outcome)
            {
                case BotTradeOutcome.Completed:
                    return "completed";
                case BotTradeOutcome.RolledBack:
                    return "rolled-back";
                default:
                    return "refused";
            }
        }

        /// <summary>A step refused. Caught in Execute and turned into a roll-back, never thrown out of it.</summary>
        private sealed class TradeFailed : Exception
        {
            public TradeFailed(string message)
                : base(message)
            {
            }
        }
    }
}
