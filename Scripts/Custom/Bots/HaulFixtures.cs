// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// HaulFixtures.cs - the four things REVIEW.md F5 says a delivery must do, proved from [CoreSmoke.
//
// Run from CoreSmoke.Finish, after the bridge fixtures, in the shape BridgeFixtures.cs and
// SaveIntegrity.cs already use: one Expect per claim, a catch at the driver, cleanup in a finally.
//
// WHAT IS DIFFERENT HERE, AND WHY. Those two suites build a scratch directory; these need real
// mobiles, because the thing under test is items moving between containers. So each fixture makes
// its own bots, asserts, and deletes them in a finally - on Map.Internal, which is where a mobile
// with nowhere to be belongs and is what keeps a smoke test off the live map.
//
// Nothing here goes through TravelerBehavior, a NavDestination or the twelve-tile buyer sweep.
// That half of TryDeliver decides WHERE a bot is standing and was never what F5 was about;
// BotWorkDelivery.Settle is the half that decides how much moves, and it is what these drive.
// The receiver is the real CrafterStock.Add with the real StockCap, so the partial acceptance
// these assert on is the one a full bench actually produces.
//
// THE BOOKS ARE KEPT WHILE TESTING. Every unit a fixture puts into a pack is counted in through
// BotGoodsLedger, and every unit it destroys leaves through a recorded loss - so a [CoreSmoke run
// cannot itself knock Bots.Conservation out of balance. A fixture that cheated there would be
// testing the ledger by breaking it.

using System;
using System.Collections.Generic;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public static class HaulFixtures
    {
        /// <summary>The reason a fixture's own bots carry, so its records are obvious in the journal.</summary>
        private const string FixtureReason = "fixture";

        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- haul conservation fixtures --");

            bool passed = true;

            try
            {
                passed &= FixturePartialAcceptance(report);
                passed &= FixtureBankRefusalKeepsTheLoad(report);
                passed &= FixtureInterruptedHandover(report);
                passed &= FixtureLogoutSkipsACarryingBot(report);
                passed &= FixtureRestartLossIsRecorded(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }

            return passed;
        }

        // ---- 1. the receiver takes what it can, and the rest stays in the pack ----

        /// <summary>
        /// Rule 1. A bench five units from its cap takes five of a sixty-unit load, and the other
        /// fifty-five are still in the miner's pack afterwards.
        ///
        /// This is the case that used to destroy the load: TakeYield deleted all sixty to count
        /// them, CrafterStock.Add returned 5, and the remaining 55 were re-minted as a brand new
        /// stack in the bank box - or deleted outright when the box refused it.
        /// </summary>
        private static bool FixturePartialAcceptance(List<string> report)
        {
            PlayerBot miner = null;
            PlayerBot smith = null;

            try
            {
                miner = Make(BotClass.Miner);
                smith = Make(BotClass.Smith);

                Type raw = BotHarvest.YieldFor(BotClass.Miner);
                CrafterProfile profile = CrafterProfiles.For(smith);

                if (raw == null || profile == null)
                {
                    return Expect(report, false, "", "the fixture could not resolve a miner's yield or a smith's profile");
                }

                // Fill the bench to five short of its cap, whatever it happened to spawn holding.
                int room = CrafterStock.StockCap - CrafterStock.Count(smith, profile);
                CrafterStock.Add(smith, profile, Math.Max(0, room - 5));

                int before = Load(miner, raw, 60);
                int deliveredBefore = BotWorkSites.Delivered;

                int offered;
                int accepted = BotWorkDelivery.Settle(
                    miner, raw, amount => CrafterStock.Add(smith, profile, amount), "fixture-forge", out offered);

                bool ok = Expect(report,
                    offered == before + 60,
                    "the whole load is offered, not the part a receiver is expected to want",
                    "offered " + offered + " of a pack holding " + (before + 60));

                ok &= Expect(report,
                    accepted == 5,
                    "a bench five short of StockCap accepts five units and says so",
                    "the bench accepted " + accepted + " rather than 5");

                ok &= Expect(report,
                    BotHaul.Offered(miner, raw) == offered - 5,
                    "the remainder stays in the hauler's pack - nothing is deleted to make the numbers match",
                    "the pack holds " + BotHaul.Offered(miner, raw) + " where " + (offered - 5) + " was expected");

                ok &= Expect(report,
                    CrafterStock.Count(smith, profile) == CrafterStock.StockCap,
                    "the five that moved really arrived, as refined stock at the cap",
                    "the bench holds " + CrafterStock.Count(smith, profile) + " of " + CrafterStock.StockCap);

                ok &= Expect(report,
                    BotWorkSites.Delivered - deliveredBefore == 5,
                    "the delivery counter measures what was ACCEPTED, not what was offered",
                    "Delivered rose by " + (BotWorkSites.Delivered - deliveredBefore) + " rather than 5");

                return ok;
            }
            finally
            {
                Unmake(miner);
                Unmake(smith);
            }
        }

        // ---- 2. a bank box that will not take it does not get to destroy it ----

        /// <summary>
        /// Rule 1 again, on the branch the finding named explicitly: "the bank fallback constructs
        /// a replacement stack and deletes that too if TryDropItem fails."
        ///
        /// The refusal is a real one: one item in the box and a cap of one, so Container.CheckHold
        /// turns the drop away on TotalItems exactly as it does for a box a player has filled.
        /// A bank box has no weight limit at all (BankBox.DefaultMaxWeight is 0), so the item cap
        /// is the only way it ever says no.
        /// </summary>
        private static bool FixtureBankRefusalKeepsTheLoad(List<string> report)
        {
            PlayerBot miner = null;

            try
            {
                miner = Make(BotClass.Miner);

                Type raw = BotHarvest.YieldFor(BotClass.Miner);
                BankBox box = miner.BankBox;

                if (raw == null || box == null)
                {
                    return Expect(report, false, "", "the fixture could not open a bank box");
                }

                box.DropItem(new Bandage());
                box.MaxItems = 1;

                int offered0 = Load(miner, raw, 20);
                int lostBefore = BotGoodsLedger.Lost;

                int offered;
                int accepted = BotWorkDelivery.Settle(miner, raw, null, "fixture-bank", out offered);

                bool ok = Expect(report,
                    accepted == 0,
                    "a full bank box accepts nothing and reports nothing",
                    "the full box somehow accepted " + accepted);

                ok &= Expect(report,
                    BotHaul.Offered(miner, raw) == offered0 + 20,
                    "a refused bank drop leaves the whole load in the pack",
                    "the pack holds " + BotHaul.Offered(miner, raw) + " where " + (offered0 + 20) + " was expected");

                ok &= Expect(report,
                    BotGoodsLedger.Lost == lostBefore,
                    "a refusal destroys nothing, so there is nothing to record",
                    "the refusal recorded " + (BotGoodsLedger.Lost - lostBefore) + " lost unit(s)");

                return ok;
            }
            finally
            {
                Unmake(miner);
            }
        }

        // ---- 3. an interrupted hand-over leaves animal and load together ----

        /// <summary>
        /// Rule 2. Delivery used to release the beast five lines before it had found a buyer, and
        /// BotPackAnimals.Release deletes it with whatever is still in the panniers. A hand-over
        /// nobody accepted must leave the pair as it found them.
        ///
        /// Also asserts the other half, because a rule that never releases would pass the first
        /// assertion and be useless: an empty beast IS released.
        /// </summary>
        private static bool FixtureInterruptedHandover(List<string> report)
        {
            PlayerBot miner = null;

            try
            {
                miner = Make(BotClass.Miner);

                Type raw = BotHarvest.YieldFor(BotClass.Miner);
                BaseCreature beast = BotPackAnimals.SpawnFor(miner);

                if (raw == null || beast == null || beast.Backpack == null)
                {
                    return Expect(report, false, "", "the fixture could not give the miner a pack animal");
                }

                // Into the PANNIERS, which is the container delivery used to empty and the beast
                // used to take with it. Nothing in the live tree loads them - see the seam note
                // in the Bots README - so a fixture is the only place this can be exercised.
                Item cargo = (Item)Activator.CreateInstance(raw);
                cargo.Amount = 12;
                beast.Backpack.DropItem(cargo);
                BotGoodsLedger.NoteMined(miner, 12);

                int releasedBefore = BotPackAnimals.Released;

                int offered;
                int accepted = BotWorkDelivery.Settle(miner, raw, amount => 0, "fixture-forge", out offered);

                bool ok = Expect(report,
                    accepted == 0 && offered >= 12,
                    "the panniers are part of what is offered, and a receiver that wants none takes none",
                    "offered " + offered + ", accepted " + accepted);

                ok &= Expect(report,
                    miner.PackAnimal == beast && !beast.Deleted,
                    "an interrupted hand-over leaves the animal where it was",
                    "the beast was released under a refused hand-over");

                ok &= Expect(report,
                    BotHaul.InContainer(beast.Backpack, raw) == 12,
                    "and leaves the load on it - animal and load stay together",
                    "the panniers hold " + BotHaul.InContainer(beast.Backpack, raw) + " where 12 was expected");

                ok &= Expect(report,
                    BotPackAnimals.Released == releasedBefore,
                    "so nothing counts as released",
                    "Released rose by " + (BotPackAnimals.Released - releasedBefore));

                // Now empty the panniers by settling into the bank box, and the beast may go.
                BotWorkDelivery.Settle(miner, raw, null, "fixture-bank", out offered);

                ok &= Expect(report,
                    miner.PackAnimal == null && beast.Deleted,
                    "a completed hand-over releases the beast, which is the other half of the rule",
                    "the beast survived a hand-over that emptied its panniers");

                return ok;
            }
            finally
            {
                Unmake(miner);
            }
        }

        // ---- 4. a logout waits for the hand-over ----

        /// <summary>
        /// Rule 3, both halves.
        ///
        /// CanLogoutNow already refused a laden bot and still does. The hole was AFTER the
        /// decision: BeginLogout sets LoggingOut and schedules a delete that re-checked nothing
        /// but Deleted, so a gatherer whose shift ended inside the three-to-six-second beat was
        /// deleted on top of a load it had picked up while saying goodbye.
        /// </summary>
        private static bool FixtureLogoutSkipsACarryingBot(List<string> report)
        {
            PlayerBot miner = null;

            try
            {
                miner = Make(BotClass.Miner);

                miner.HaulPending = false;

                bool ok = Expect(report,
                    BotSession.CanLogoutNow(miner),
                    "an idle-handed bot may log out",
                    "the guard refused a bot that is carrying nothing, so it proves nothing about the one that is");

                miner.HaulPending = true;

                ok &= Expect(report,
                    !BotSession.CanLogoutNow(miner),
                    "a bot carrying an undelivered load is never chosen for logout",
                    "the guard let a laden bot go");

                // The beat. LoggingOut is already set, the bot has since shouldered a load, and
                // the delayed half of the logout is about to run.
                miner.LoggingOut = true;
                int deferredBefore = BotSession.LogoutsDeferred;

                BotSession.FinishLogout(miner);

                ok &= Expect(report,
                    !miner.Deleted,
                    "a bot that picks up a load between 'gtg' and vanishing is not deleted on top of it",
                    "the delayed logout deleted a carrying bot");

                ok &= Expect(report,
                    !miner.LoggingOut && BotSession.LogoutsDeferred == deferredBefore + 1,
                    "it stands down instead, and the next pass will catch it once the hand-over is done",
                    "the bot was left pinned in LoggingOut, which nothing may draft from");

                // And the beat still ends in a delete when the load is gone.
                miner.HaulPending = false;
                miner.LoggingOut = true;

                BotSession.FinishLogout(miner);

                ok &= Expect(report,
                    miner.Deleted,
                    "an unladen bot still leaves, so the deferral is a wait and not a reprieve",
                    "the logout never completed for a bot carrying nothing");

                return ok;
            }
            finally
            {
                Unmake(miner);
            }
        }

        // ---- 5. a load lost to a restart is written down ----

        /// <summary>
        /// Rule 4. BotStartupPurge.Prepare is the real wiring - it counts what the bot was holding
        /// in as this boot's opening balance and names the reason - and the delete that follows is
        /// the one the sweep performs.
        ///
        /// Purge() itself is not called: it deletes every bot in the world, which is not something
        /// [CoreSmoke may do on a live shard. The sweep's own loop is two lines over this method,
        /// and the live save/restart in the session's acceptance is what proves it end to end.
        /// </summary>
        private static bool FixtureRestartLossIsRecorded(List<string> report)
        {
            PlayerBot miner = null;

            try
            {
                miner = Make(BotClass.Miner);

                Type raw = BotHarvest.YieldFor(BotClass.Miner);

                // Clear whatever the spawn stash gave it, on the books and in the pack, so the
                // split this asserts on is the fixture's own arithmetic and not EquipmentTable's
                // dice. Recorded as a loss like anything else - see Unmake.
                BotGoodsLedger.NoteContainerLoss(miner, miner.Backpack, FixtureReason);
                BotHaul.ConsumeFrom(miner.Backpack, raw, Int32.MaxValue);

                // Three units of stash and four of haul, and the record must say which was which.
                Put(miner, raw, 3);
                BotGoodsLedger.NoteSeeded(miner, 3);

                Put(miner, raw, 4);
                BotGoodsLedger.NoteMined(miner, 4);

                int lostBefore = BotGoodsLedger.Lost;
                int stashBefore = BotGoodsLedger.LostStash;
                int haulBefore = BotGoodsLedger.LostHaul;
                int openingBefore = BotGoodsLedger.Opening;
                int recordsBefore = BotGoodsLedger.Records;

                BotStartupPurge.Prepare(miner);

                bool ok = Expect(report,
                    BotGoodsLedger.Opening - openingBefore == 7,
                    "the purge counts what a boot inherited before it destroys it, so the books can balance",
                    "the opening balance rose by " + (BotGoodsLedger.Opening - openingBefore) + " rather than 7");

                string name = miner.Name;

                miner.Delete();

                string record = BotGoodsLedger.LastRecord ?? String.Empty;

                ok &= Expect(report,
                    BotGoodsLedger.Lost - lostBefore == 7 && BotGoodsLedger.Records > recordsBefore,
                    "a bot destroyed holding a load records the loss rather than losing it quietly",
                    "Lost rose by " + (BotGoodsLedger.Lost - lostBefore) + " over "
                        + (BotGoodsLedger.Records - recordsBefore) + " record(s), where 7 over at least 1 was expected");

                ok &= Expect(report,
                    BotGoodsLedger.LostStash - stashBefore == 3 && BotGoodsLedger.LostHaul - haulBefore == 4,
                    "and splits the spawn stash from the haul, so a lost delivery is never hidden under a restart",
                    "the split was " + (BotGoodsLedger.LostStash - stashBefore) + " stash and "
                        + (BotGoodsLedger.LostHaul - haulBefore) + " haul, where 3 and 4 was expected");

                ok &= Expect(report,
                    record.Contains("\"why\":\"" + BotGoodsLedger.ReasonBootPurge + "\"")
                        && record.Contains("\"bot\":\"" + name + "\"")
                        && record.Contains("\"item\":\"" + raw.Name + "\"")
                        && record.Contains("\"amount\":7")
                        && record.Contains("\"x\":")
                        && record.Contains("\"y\":"),
                    "the record names the item, the amount, the bot, where it was and why it went",
                    "the record was missing one of item/amount/bot/place/why: " + record);

                return ok;
            }
            finally
            {
                Unmake(miner);
            }
        }

        // ---- scaffolding ----

        /// <summary>
        /// A bot on Map.Internal, which is where a mobile with nowhere to be belongs.
        ///
        /// The constructor rolls an outfit, which for a gatherer includes the spawn stash - and
        /// that stash goes through BotGoodsLedger.NoteSeeded like any other, so a fixture bot
        /// starts life on the books exactly as a spawned one does.
        /// </summary>
        private static PlayerBot Make(BotClass cls)
        {
            var bot = new PlayerBot(cls, BotSkillTier.Journeyman);

            bot.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);

            return bot;
        }

        /// <summary>
        /// Delete a fixture's bot, naming the reason so its leftovers are obvious in the journal.
        ///
        /// Deliberately NOT a quiet delete. Whatever a fixture leaves in a pack really is
        /// destroyed here, and a suite that exempted itself from the rule it is testing would be
        /// the one place the books were allowed to be wrong.
        /// </summary>
        private static void Unmake(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.DeletionReason = FixtureReason;
            bot.Delete();
        }

        /// <summary>
        /// Put a load in the pack and count it in as mined. Returns what the pack held BEFORE,
        /// so a fixture can assert in relative terms rather than assuming an empty bot.
        ///
        /// The amount counted in is what actually landed, not what was asked for: a pack that
        /// refused the drop would otherwise put the ledger out by the difference, and a suite
        /// that cannot keep its own books has no business asserting on anybody else's.
        /// </summary>
        private static int Load(PlayerBot bot, Type raw, int amount)
        {
            int before = BotHaul.Offered(bot, raw);

            Put(bot, raw, amount);

            BotGoodsLedger.NoteMined(bot, BotHaul.Offered(bot, raw) - before);

            return before;
        }

        /// <summary>A stack of its own in the pack, told to nobody. The caller counts it in.</summary>
        private static void Put(PlayerBot bot, Type raw, int amount)
        {
            var stack = (Item)Activator.CreateInstance(raw);

            stack.Amount = amount;

            bot.Backpack.DropItem(stack);
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
