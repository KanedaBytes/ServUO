// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// EconomyFixtures.cs - the transaction contract (ECONOMY.md), proved from [CoreSmoke.
//
// Run from CoreSmoke.Finish after NamedBotFixtures, in HaulFixtures' shape: one Expect per claim,
// real mobiles on Map.Internal, a catch at the driver, cleanup in a finally. What is under test is
// BotTrade itself - Quote and Execute - driven through BotWorkDelivery.SettleTrade, the same call
// the arrival makes, without a destination or the twelve-tile buyer sweep.
//
// THE BOOKS ARE KEPT WHILE TESTING, for both ledgers. Every ore unit a fixture puts in a pack is
// counted in as mined and every one it removes leaves through a recorded loss; every coin a fixture
// adds enters as starting gold and every one it takes leaves as a `fixture` loss
// (BotGoldLedger.SetPurseForFixture). A [CoreSmoke run cannot knock Bots.Gold or
// Bots.Conservation out of balance, and each fixture checks that the ledger's belief about its own
// bots still matches what they hold.

using System;
using System.Collections.Generic;

using Server.Accounting;
using Server.Items;
using Server.Misc;

namespace Server.Custom
{
    public static class EconomyFixtures
    {
        private const string FixtureReason = "fixture";
        private const string FixtureAccount = "GGFixtureEconomyBot";

        private const int Medium = 0x19B8;

        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- economy fixtures --");

            bool passed = true;

            int logins = NamedBots.Logins;
            int logouts = NamedBots.Logouts;
            int created = NamedBots.CharactersCreated;
            int accounts = NamedBots.AccountsCreated;

            try
            {
                passed &= FixtureAtomicExchange(report);
                passed &= FixtureRollbackRestoresEverything(report);
                passed &= FixtureOnlyWhatItCanPayFor(report);
                passed &= FixtureThrowawayPurseLostAtPurge(report);
                passed &= FixtureNamedGoldSurvivesTheSession(report);
                passed &= FixtureColouredOreCountedAndPriced(report);
                passed &= FixtureColouredLogs(report);
                passed &= FixtureNewbornPurse(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }
            finally
            {
                BotTrade.FailAfterStep = 0;

                NamedBots.Logins = logins;
                NamedBots.Logouts = logouts;
                NamedBots.CharactersCreated = created;
                NamedBots.AccountsCreated = accounts;

                RemoveAccount(FixtureAccount);
            }

            return passed;
        }

        // ---- 1. goods and gold move together ----

        /// <summary>
        /// Ten medium iron ore to a smith with a fresh purse. Worth 60 half-gold (10 x 2 half-ingots
        /// x 3gp), price ceil(60 x 125 / 200) = 38. Both sides of both custodies move, both ledgers
        /// hear it, and the journal line says who, what, how much, where and on what basis.
        /// </summary>
        private static bool FixtureAtomicExchange(List<string> report)
        {
            PlayerBot miner = null;
            PlayerBot smith = null;

            try
            {
                miner = Make(BotClass.Miner);
                smith = Make(BotClass.Smith);
                ClearYield(miner);

                CrafterProfile profile = CrafterProfiles.For(smith);

                PutOre(miner, typeof(IronOre), Medium, 10);

                int minerGold = BotGoldLedger.PackGold(miner);
                int smithGold = BotGoldLedger.PackGold(smith);
                int ingots = smith.Backpack.GetAmount(typeof(IronIngot), false);
                long paid = BotGoldLedger.Paid;
                long received = BotGoldLedger.Received;
                int accepted0 = BotGoodsLedger.Accepted;

                BotTradeQuote quote;
                int accepted = BotWorkDelivery.SettleTrade(miner, smith, profile, "fixture-forge", out quote);
                string line = BotTrade.Journal.Last ?? "";

                bool ok = Expect(report,
                    accepted == 10 && quote.Price == 38,
                    "ten medium iron ore sell for 38 gold - 3gp an ingot, one ingot a unit, 125%, rounded up once",
                    String.Format("accepted {0} at {1} gold, where 10 at 38 was expected", accepted, quote.Price));

                ok &= Expect(report,
                    BotGoldLedger.PackGold(miner) == minerGold + 38 && BotGoldLedger.PackGold(smith) == smithGold - 38,
                    "the gold moved from the smith's pack to the miner's, all of it and only it",
                    String.Format("miner {0} -> {1}, smith {2} -> {3}", minerGold, BotGoldLedger.PackGold(miner),
                        smithGold, BotGoldLedger.PackGold(smith)));

                ok &= Expect(report,
                    BotHaul.Offered(miner, typeof(BaseOre)) == 0
                        && smith.Backpack.GetAmount(typeof(IronIngot), false) == ingots + 10,
                    "and in the same step the ore left the miner and ten ingots reached the smith",
                    String.Format("miner still offers {0} ore; smith ingots {1} -> {2}",
                        BotHaul.Offered(miner, typeof(BaseOre)), ingots, smith.Backpack.GetAmount(typeof(IronIngot), false)));

                ok &= Expect(report,
                    BotGoldLedger.Paid - paid == 38 && BotGoldLedger.Received - received == 38
                        && BotGoodsLedger.Accepted - accepted0 == 10,
                    "both ledgers were told: 38 paid, 38 received, 10 units accepted",
                    String.Format("paid +{0}, received +{1}, accepted +{2}", BotGoldLedger.Paid - paid,
                        BotGoldLedger.Received - received, BotGoodsLedger.Accepted - accepted0));

                ok &= Expect(report,
                    line.Contains("\"outcome\":\"completed\"") && line.Contains("\"price\":38")
                        && line.Contains("\"name\":" + Json.Quote(miner.Name)) && line.Contains("\"name\":" + Json.Quote(smith.Name))
                        && line.Contains("\"type\":\"IronOre\"") && line.Contains("\"units\":10")
                        && line.Contains("\"destination\":\"fixture-forge\"") && line.Contains("\"basis\":")
                        && line.Contains("\"utc\":") && line.Contains("\"generation\":"),
                    "the journal records who, what, how much, where, when and the price basis",
                    "the journal line was missing a field: " + line);

                ok &= Balanced(report, miner, smith);

                return ok;
            }
            finally
            {
                Unmake(miner);
                Unmake(smith);
            }
        }

        // ---- 2. or not at all ----

        /// <summary>
        /// The same trade, failed on purpose after each step (BotTrade.FailAfterStep): the buyer's
        /// gold debited, the goods staged, the product credited, the seller paid. Each time every
        /// step before is undone and the world is exactly as it was - ore, gold, ingots and books.
        /// </summary>
        private static bool FixtureRollbackRestoresEverything(List<string> report)
        {
            PlayerBot miner = null;
            PlayerBot smith = null;
            bool ok = true;

            try
            {
                miner = Make(BotClass.Miner);
                smith = Make(BotClass.Smith);
                ClearYield(miner);

                CrafterProfile profile = CrafterProfiles.For(smith);

                // Two stacks, so step 3 has to divide one of them: 7 accepted of a 10 pile.
                PutOre(miner, typeof(IronOre), Medium, 10);
                BotGoldLedger.SetPurseForFixture(smith, 27); // 7 units cost ceil(42 x 1.25 / 2) = 27

                for (int step = 2; step <= 5; step++)
                {
                    int ore = BotHaul.Offered(miner, typeof(BaseOre));
                    int minerGold = BotGoldLedger.PackGold(miner);
                    int smithGold = BotGoldLedger.PackGold(smith);
                    int ingots = smith.Backpack.GetAmount(typeof(IronIngot), false);
                    long paid = BotGoldLedger.Paid;
                    int accepted0 = BotGoodsLedger.Accepted;

                    BotTrade.FailAfterStep = step;

                    BotTradeQuote quote;
                    int accepted;

                    try
                    {
                        accepted = BotWorkDelivery.SettleTrade(miner, smith, profile, "fixture-forge", out quote);
                    }
                    finally
                    {
                        BotTrade.FailAfterStep = 0;
                    }

                    string line = BotTrade.Journal.Last ?? "";

                    ok &= Expect(report,
                        accepted == 0 && quote.Accepted == 7 && line.Contains("\"outcome\":\"rolled-back\"")
                            && BotHaul.Offered(miner, typeof(BaseOre)) == ore
                            && BotGoldLedger.PackGold(miner) == minerGold && BotGoldLedger.PackGold(smith) == smithGold
                            && smith.Backpack.GetAmount(typeof(IronIngot), false) == ingots
                            && BotGoldLedger.Paid == paid && BotGoodsLedger.Accepted == accepted0,
                        "a failure after step " + step + " rolls back: ore, both purses, the ingots and the books as they were",
                        String.Format("after step {0}: accepted {1} (quoted {2}), ore {3} -> {4}, miner gold {5} -> {6}, "
                            + "smith gold {7} -> {8}, ingots {9} -> {10}; {11}",
                            step, accepted, quote.Accepted, ore, BotHaul.Offered(miner, typeof(BaseOre)),
                            minerGold, BotGoldLedger.PackGold(miner), smithGold, BotGoldLedger.PackGold(smith),
                            ingots, smith.Backpack.GetAmount(typeof(IronIngot), false), line));
                }

                ok &= Balanced(report, miner, smith);

                // And with nothing forcing a failure, the same quote goes through.
                BotTradeQuote real;
                int done = BotWorkDelivery.SettleTrade(miner, smith, profile, "fixture-forge", out real);

                ok &= Expect(report,
                    done == 7 && BotGoldLedger.PackGold(smith) == 0 && BotHaul.Offered(miner, typeof(BaseOre)) == 3,
                    "and the same trade then completes: 7 units for all 27 gold, 3 left with the miner",
                    String.Format("completed {0}; smith holds {1} gold; miner offers {2}",
                        done, BotGoldLedger.PackGold(smith), BotHaul.Offered(miner, typeof(BaseOre))));

                return ok;
            }
            finally
            {
                BotTrade.FailAfterStep = 0;
                Unmake(miner);
                Unmake(smith);
            }
        }

        // ---- 3. the buyer takes only what it can pay for ----

        /// <summary>
        /// A smith with 12 gold, offered ten medium iron ore at 6 half-gold a unit: three units cost
        /// ceil(18 x 1.25 / 2) = 12 and four would cost 15, so it takes three, the rest stays with the
        /// miner, and the trade says the purse is what stopped it. With no gold at all it takes
        /// nothing, the whole load stays, and the refusal is journalled.
        /// </summary>
        private static bool FixtureOnlyWhatItCanPayFor(List<string> report)
        {
            PlayerBot miner = null;
            PlayerBot smith = null;

            try
            {
                miner = Make(BotClass.Miner);
                smith = Make(BotClass.Smith);
                ClearYield(miner);

                CrafterProfile profile = CrafterProfiles.For(smith);

                PutOre(miner, typeof(IronOre), Medium, 10);
                BotGoldLedger.SetPurseForFixture(smith, 12);

                int minerGold = BotGoldLedger.PackGold(miner);

                BotTradeQuote quote;
                int accepted = BotWorkDelivery.SettleTrade(miner, smith, profile, "fixture-forge", out quote);

                bool ok = Expect(report,
                    accepted == 3 && quote.Limit == BotTradeLimit.Gold && quote.Price == 12,
                    "a buyer with 12 gold takes the three units 12 gold buys, and says its purse stopped it",
                    String.Format("accepted {0} for {1}, limited by {2}", accepted, quote.Price, quote.Limit));

                ok &= Expect(report,
                    BotHaul.Offered(miner, typeof(BaseOre)) == 7 && BotGoldLedger.PackGold(miner) == minerGold + 12
                        && BotGoldLedger.PackGold(smith) == 0,
                    "the seven it could not pay for stay in the miner's pack, and the 12 gold is the miner's",
                    String.Format("miner offers {0}, holds {1} gold; smith holds {2}",
                        BotHaul.Offered(miner, typeof(BaseOre)), BotGoldLedger.PackGold(miner), BotGoldLedger.PackGold(smith)));

                long refusedBefore = BotGoldLedger.TradesRefusedGold;

                accepted = BotWorkDelivery.SettleTrade(miner, smith, profile, "fixture-forge", out quote);
                string line = BotTrade.Journal.Last ?? "";

                ok &= Expect(report,
                    accepted == 0 && quote.Limit == BotTradeLimit.Gold && BotHaul.Offered(miner, typeof(BaseOre)) == 7
                        && BotGoldLedger.TradesRefusedGold == refusedBefore + 1
                        && line.Contains("\"outcome\":\"refused\"") && line.Contains("\"limit\":\"gold\""),
                    "a buyer with no gold takes nothing, the hauler keeps the whole load, and the refusal is recorded",
                    String.Format("accepted {0}, limit {1}, miner offers {2}; {3}",
                        accepted, quote.Limit, BotHaul.Offered(miner, typeof(BaseOre)), line));

                ok &= Expect(report,
                    quote.FullPrice == 27,
                    "and it knows what the load would have cost - 27 gold, the number its haggle_broke line says",
                    "the full price was " + quote.FullPrice + " rather than 27");

                ok &= Balanced(report, miner, smith);

                return ok;
            }
            finally
            {
                Unmake(miner);
                Unmake(smith);
            }
        }

        // ---- 4. a throwaway's purse is lost at the purge, and written down ----

        /// <summary>
        /// A bot as the purge meets it: its purse came out of the world save, so nothing THIS boot
        /// counted it (TrackedGold is transient), and StartingGoldRemaining read back from v4. The
        /// purge counts it in as opening; the delete writes it down as a boot-purge loss - a
        /// throwaway's, and all of it the unspent starting purse.
        /// </summary>
        private static bool FixtureThrowawayPurseLostAtPurge(List<string> report)
        {
            PlayerBot bot = null;

            try
            {
                bot = Make(BotClass.Warrior);

                // Its own purse leaves on the books first, so what follows is the fixture's arithmetic.
                BotGoldLedger.SetPurseForFixture(bot, 0);

                bot.Backpack.DropItem(new Gold(10000));
                bot.TrackedGold = 0;
                bot.StartingGoldRemaining = 10000;

                long opening = BotGoldLedger.Opening;
                long lostThrowaway = BotGoldLedger.LostThrowaway;
                long lostNamed = BotGoldLedger.LostNamed;

                BotStartupPurge.Prepare(bot);

                bool ok = Expect(report,
                    BotGoldLedger.Opening - opening == 10000,
                    "the purge counts the inherited purse in before it destroys it",
                    "opening rose by " + (BotGoldLedger.Opening - opening) + " rather than 10000");

                string name = bot.Name;

                bot.Delete();

                string record = BotGoldLedger.LastRecord ?? "";

                ok &= Expect(report,
                    BotGoldLedger.LostThrowaway - lostThrowaway == 10000 && BotGoldLedger.LostNamed == lostNamed,
                    "the delete records 10,000 lost, against the throwaways and not the named cast",
                    String.Format("throwaway lost +{0}, named lost +{1}",
                        BotGoldLedger.LostThrowaway - lostThrowaway, BotGoldLedger.LostNamed - lostNamed));

                ok &= Expect(report,
                    record.Contains("\"why\":\"" + BotGoodsLedger.ReasonBootPurge + "\"") && record.Contains("\"amount\":10000")
                        && record.Contains("\"named\":false") && record.Contains("\"starting\":10000")
                        && record.Contains("\"bot\":" + Json.Quote(name)),
                    "the record names the bot, the amount, the reason, and that it was the starting purse",
                    "the loss record was missing a field: " + record);

                return ok;
            }
            finally
            {
                Unmake(bot);
            }
        }

        // ---- 5. a named bot's gold survives the session boundary ----

        /// <summary>
        /// A named bot logs in at its post, logs out, and logs in again: the purse is the same coin
        /// count throughout, the ledger holds it offline while it is away, and nothing is lost. The
        /// world-save half is proved live (save, shutdown, boot, [BotNamed).
        /// </summary>
        private static bool FixtureNamedGoldSurvivesTheSession(List<string> report)
        {
            PlayerBot bot = null;

            try
            {
                var entry = new BotRosterEntry { Name = "Fixturepurse", Class = "Warrior", Home = "britain", Post = "brit-bank" };
                BotClass cls;

                if (BotRoster.TryParseClass(entry.Class, out cls))
                {
                    entry.ParsedClass = cls;
                }

                entry.RenamedFrom = FixtureAccount;

                bot = PlayerBot.CreateNamed(entry);
                bot.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);

                string error;
                Account account = NamedBots.EnsureAccount(FixtureAccount, out error);

                if (account == null)
                {
                    return Expect(report, false, "", "the fixture account could not be made: " + error);
                }

                account[0] = bot;

                bool ok = Expect(report,
                    BotGoldLedger.PackGold(bot) == BotSystem.Store.Economy.StartingGold && bot.StartingGoldGranted,
                    "a named bot is born with the starting purse too",
                    "a new named bot holds " + BotGoldLedger.PackGold(bot) + " gold");

                long lost = BotGoldLedger.Lost;

                ok &= Expect(report,
                    NamedBots.LogIn(bot, entry, "fixture"),
                    "it logs in",
                    "the named bot did not log in");

                long before = BotGoldLedger.GoldOf(bot);

                ok &= Expect(report,
                    NamedBots.LogOut(bot, "fixture") && !bot.Deleted && BotGoldLedger.IsOffline(bot)
                        && BotGoldLedger.GoldOf(bot) == before && bot.TrackedGold == before,
                    "logged out, its purse is intact and the gold ledger holds it offline",
                    String.Format("offline {0}, gold {1} (was {2}), tracked {3}",
                        BotGoldLedger.IsOffline(bot), BotGoldLedger.GoldOf(bot), before, bot.TrackedGold));

                ok &= Expect(report,
                    NamedBots.LogIn(bot, entry, "fixture") && !BotGoldLedger.IsOffline(bot)
                        && BotGoldLedger.GoldOf(bot) == before && BotGoldLedger.Lost == lost,
                    "logged back in, it has every coin and nothing was written down as lost",
                    String.Format("gold {0} (was {1}), lost +{2}", BotGoldLedger.GoldOf(bot), before, BotGoldLedger.Lost - lost));

                return ok;
            }
            finally
            {
                if (bot != null && !bot.Deleted)
                {
                    bot.DeletionReason = NamedBots.FixtureReason;
                    bot.Delete();
                }
            }
        }

        // ---- 6. coloured ore is carried, counted and priced by colour ----

        /// <summary>
        /// 4 large dull copper, 2 medium valorite and 3 small iron. Nine units carried, nine offered.
        /// Worth, in half-gold: 4 x 4 x 3 x 2 = 96, 2 x 2 x 3 x 39 = 468, one pair of small iron
        /// 1 x 2 x 3 x 1 = 6 - 570 - so 357 gold. The smith receives 8 dull copper ingots, 2 valorite
        /// and 1 iron; the odd small ore stays with the miner. A lost valorite pile is recorded by name.
        /// </summary>
        private static bool FixtureColouredOreCountedAndPriced(List<string> report)
        {
            PlayerBot miner = null;
            PlayerBot smith = null;

            try
            {
                miner = Make(BotClass.Miner);
                smith = Make(BotClass.Smith);
                ClearYield(miner);

                CrafterProfile profile = CrafterProfiles.For(smith);

                PutOre(miner, typeof(DullCopperOre), BotPrice.LargeOre, 4);
                PutOre(miner, typeof(ValoriteOre), Medium, 2);
                PutOre(miner, typeof(IronOre), BotPrice.SmallOre, 3);

                bool ok = Expect(report,
                    GathererBehavior.Carried(miner) == 9 && BotHaul.Offered(miner, typeof(BaseOre)) == 9,
                    "every colour is carried and offered - the pack-full check counts all nine",
                    String.Format("carried {0}, offered {1}", GathererBehavior.Carried(miner), BotHaul.Offered(miner, typeof(BaseOre))));

                int dull = smith.Backpack.GetAmount(typeof(DullCopperIngot), false);
                int valorite = smith.Backpack.GetAmount(typeof(ValoriteIngot), false);
                int iron = smith.Backpack.GetAmount(typeof(IronIngot), false);
                int smithGold = BotGoldLedger.PackGold(smith);

                BotTradeQuote quote;
                int accepted = BotWorkDelivery.SettleTrade(miner, smith, profile, "fixture-forge", out quote);

                ok &= Expect(report,
                    accepted == 8 && quote.WorthHalf == 570 && quote.Price == 357 && quote.Limit == BotTradeLimit.Pairs,
                    "priced by colour and pile: worth 570 half-gold, 357 gold, eight units - the odd small ore is not one",
                    String.Format("accepted {0}, worth {1}, price {2}, limit {3}", accepted, quote.WorthHalf, quote.Price, quote.Limit));

                ok &= Expect(report,
                    smith.Backpack.GetAmount(typeof(DullCopperIngot), false) == dull + 8
                        && smith.Backpack.GetAmount(typeof(ValoriteIngot), false) == valorite + 2
                        && smith.Backpack.GetAmount(typeof(IronIngot), false) == iron + 1
                        && BotGoldLedger.PackGold(smith) == smithGold - 357,
                    "each colour refines into its own ingot at the stock smelt ratio: 8 dull copper, 2 valorite, 1 iron",
                    String.Format("dull copper +{0}, valorite +{1}, iron +{2}, smith paid {3}",
                        smith.Backpack.GetAmount(typeof(DullCopperIngot), false) - dull,
                        smith.Backpack.GetAmount(typeof(ValoriteIngot), false) - valorite,
                        smith.Backpack.GetAmount(typeof(IronIngot), false) - iron,
                        smithGold - BotGoldLedger.PackGold(smith)));

                ok &= Expect(report,
                    BotHaul.InContainer(miner.Backpack, typeof(IronOre)) == 1 && BotHaul.Offered(miner, typeof(BaseOre)) == 1,
                    "and the one small iron ore the smelt cannot take alone stays with the miner",
                    "the miner offers " + BotHaul.Offered(miner, typeof(BaseOre)) + " ore afterwards");

                ok &= Balanced(report, miner, smith);

                // The leftover small ore goes first, on the books, so the next record can only be
                // the valorite's.
                ClearYield(miner);

                PutOre(miner, typeof(ValoriteOre), Medium, 3);
                BotGoodsLedger.NoteContainerLoss(miner, miner.Backpack, FixtureReason);
                string record = BotGoodsLedger.LastRecord ?? "";
                BotHaul.ConsumeFrom(miner.Backpack, typeof(BaseOre), Int32.MaxValue);

                ok &= Expect(report,
                    record.Contains("\"item\":\"ValoriteOre\"") && record.Contains("\"amount\":3"),
                    "a loss is recorded by concrete type - the journal names ValoriteOre, not the family",
                    "the loss record named something else: " + record);

                return ok;
            }
            finally
            {
                Unmake(miner);
                Unmake(smith);
            }
        }

        /// <summary>Five oak logs: 5 x 2 x 1gp x 3 = 30 half-gold, 19 gold, five oak boards.</summary>
        private static bool FixtureColouredLogs(List<string> report)
        {
            PlayerBot lumberjack = null;
            PlayerBot carpenter = null;

            try
            {
                lumberjack = Make(BotClass.Lumberjack);
                carpenter = Make(BotClass.Carpenter);
                ClearYield(lumberjack);

                CrafterProfile profile = CrafterProfiles.For(carpenter);
                int oak = carpenter.Backpack.GetAmount(typeof(OakBoard), false);

                var logs = new OakLog(5);
                lumberjack.Backpack.DropItem(logs);
                BotGoodsLedger.NoteMined(lumberjack, 5);

                BotTradeQuote quote;
                int accepted = BotWorkDelivery.SettleTrade(lumberjack, carpenter, profile, "fixture-shop", out quote);

                bool ok = Expect(report,
                    accepted == 5 && quote.Price == 19 && carpenter.Backpack.GetAmount(typeof(OakBoard), false) == oak + 5,
                    "five oak logs sell for 19 gold and become five oak boards",
                    String.Format("accepted {0} for {1}, oak boards +{2}", accepted, quote.Price,
                        carpenter.Backpack.GetAmount(typeof(OakBoard), false) - oak));

                ok &= Balanced(report, lumberjack, carpenter);

                return ok;
            }
            finally
            {
                Unmake(lumberjack);
                Unmake(carpenter);
            }
        }

        // ---- 7. every bot is born with the purse, and it does not bury it ----

        private static bool FixtureNewbornPurse(List<string> report)
        {
            int expected = BotSystem.Store.Economy.StartingGold;
            var wrong = new List<string>();
            var heavy = new List<string>();

            foreach (BotClass cls in Enum.GetValues(typeof(BotClass)))
            {
                PlayerBot bot = null;

                try
                {
                    bot = Make(cls);

                    if (BotGoldLedger.PackGold(bot) != expected || bot.TrackedGold != expected)
                    {
                        wrong.Add(String.Format("{0} holds {1} (tracked {2})", cls, BotGoldLedger.PackGold(bot), bot.TrackedGold));
                    }

                    if (WeightOverloading.IsOverloaded(bot))
                    {
                        heavy.Add(String.Format("{0} {1}/{2}", cls, Mobile.BodyWeight + bot.TotalWeight, bot.MaxWeight));
                    }
                }
                finally
                {
                    Unmake(bot);
                }
            }

            bool ok = Expect(report,
                wrong.Count == 0,
                "a bot of every class is born holding exactly " + expected + " gold, on the books",
                "purses were wrong: " + String.Join("; ", wrong.ToArray()));

            ok &= Expect(report,
                heavy.Count == 0,
                "and no newborn is overloaded by it",
                "overloaded at birth: " + String.Join("; ", heavy.ToArray()));

            return ok;
        }

        // ---- scaffolding ----

        /// <summary>The ledger's belief about these bots matches what they really hold, gold and goods.</summary>
        private static bool Balanced(List<string> report, params PlayerBot[] bots)
        {
            var off = new List<string>();

            foreach (PlayerBot bot in bots)
            {
                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                if (BotGoldLedger.GoldOf(bot) != bot.TrackedGold)
                {
                    off.Add(String.Format("{0} gold {1} tracked {2}", bot.Name, BotGoldLedger.GoldOf(bot), bot.TrackedGold));
                }

                if (BotHaul.Custody(bot) != bot.TrackedCustody)
                {
                    off.Add(String.Format("{0} goods {1} tracked {2}", bot.Name, BotHaul.Custody(bot), bot.TrackedCustody));
                }
            }

            return Expect(report,
                off.Count == 0,
                "both ledgers still agree with what these bots hold",
                "a ledger disagrees: " + String.Join("; ", off.ToArray()));
        }

        private static PlayerBot Make(BotClass cls)
        {
            var bot = new PlayerBot(cls, BotSkillTier.Journeyman);

            bot.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);

            return bot;
        }

        private static void Unmake(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.DeletionReason = FixtureReason;
            bot.Delete();
        }

        /// <summary>Empty a gatherer of its spawn stash, on the books: a recorded loss, then the units.</summary>
        private static void ClearYield(PlayerBot bot)
        {
            Type family = BotHaul.FamilyOf(BotHarvest.YieldFor(bot.Class));

            BotGoodsLedger.NoteContainerLoss(bot, bot.Backpack, FixtureReason);
            BotHaul.ConsumeFrom(bot.Backpack, family, Int32.MaxValue);
        }

        /// <summary>A pile of this ore at this graphic, counted in as mined.</summary>
        private static void PutOre(PlayerBot bot, Type ore, int itemId, int amount)
        {
            var pile = (Item)Activator.CreateInstance(ore);

            pile.ItemID = itemId;
            pile.Amount = amount;

            bot.Backpack.DropItem(pile);
            BotGoodsLedger.NoteMined(bot, amount);
        }

        private static void RemoveAccount(string username)
        {
            var account = Accounts.GetAccount(username) as Account;

            if (account == null)
            {
                return;
            }

            for (int i = 0; i < account.Length; i++)
            {
                account[i] = null;
            }

            Accounts.Remove(username);
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
