// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// MountFixtures.cs - a mount goes with its bot, proved from [CoreSmoke.
//
// The defect these guard: bots died at the Britain graveyard, the engine dropped their horses on
// the corpse tile before OnDeath could release them (MountItem.OnParentDeath does Rider = null
// inside Mobile.Kill), and the riderless FrenziedOstards killed every bot arriving next. See
// PlayerBot.OwnedMount and PlayerBot.OnBeforeDeath.
//
// HaulFixtures' shape: real mobiles on Map.Internal, one Expect per claim, deleted in a finally.
// The mounts are built by hand for HaulFixtures.Harness's reason - TryMount refuses Map.Internal,
// because in production a horse with no map is a stray waiting to happen - and handed to the bot
// through BotMovement.Issue, which is the one call TryMount makes to record ownership.
//
// THE ENGINE-DROPPED CASE IS BUILT, NOT PROVOKED. After MountItem.OnParentDeath the horse is
// riderless, uncontrolled and not parked; the bot has no Mount and no HeldMount. That is exactly a
// horse Issued to a bot and then left alone, which is what the third fixture builds. Killing a bot
// for real would put a corpse and a one-second delete timer into a synchronous smoke run.

using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    public static class MountFixtures
    {
        private const string FixtureReason = "fixture";

        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- mount ownership fixtures --");

            bool passed = true;

            try
            {
                passed &= FixtureWaitingMountGoesWithItsBot(report);
                passed &= FixtureDroppedMountGoesWithItsBot(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }

            return passed;
        }

        /// <summary>
        /// Two bots, each with a horse waiting beside it. Deleting the first takes its horse and
        /// leaves nothing behind; the second bot's horse is still standing, still its, and is not
        /// something the boot sweep would call a stray.
        /// </summary>
        private static bool FixtureWaitingMountGoesWithItsBot(List<string> report)
        {
            PlayerBot gone = null;
            PlayerBot living = null;
            BaseMount goneHorse = null;
            BaseMount livingHorse = null;

            try
            {
                gone = Make();
                living = Make();

                goneHorse = Park(gone);
                livingHorse = Park(living);

                int releasedBefore = BotMovement.MountsReleased;

                Unmake(gone);

                bool ok = Expect(report,
                    goneHorse.Deleted,
                    "deleting a bot with a waiting mount leaves no mount behind",
                    "the waiting horse outlived its deleted owner");

                ok &= Expect(report,
                    BotMovement.MountsReleased == releasedBefore + 1,
                    "and it is counted as released with its rider",
                    "MountsReleased rose by " + (BotMovement.MountsReleased - releasedBefore));

                ok &= Expect(report,
                    !livingHorse.Deleted && living.HeldMount == livingHorse && living.OwnedMount == livingHorse,
                    "a living bot's waiting mount is untouched",
                    "the other bot's waiting horse was disturbed");

                ok &= Expect(report,
                    !BotMovement.IsFormerBotMount(livingHorse),
                    "and the boot sweep would not call it a stray",
                    "IsFormerBotMount says yes to a horse with a living owner");

                return ok;
            }
            finally
            {
                Unmake(gone);
                Unmake(living);
                Discard(goneHorse);
                Discard(livingHorse);
            }
        }

        /// <summary>
        /// The graveyard case: the engine has already taken the horse off the rider, so the bot has
        /// no Mount and no HeldMount - only OwnedMount still names it. Deleting the bot takes it.
        /// </summary>
        private static bool FixtureDroppedMountGoesWithItsBot(List<string> report)
        {
            PlayerBot rider = null;
            BaseMount horse = null;

            try
            {
                rider = Make();

                horse = new FrenziedOstard();
                horse.MoveToWorld(rider.Location, rider.Map);

                BotMovement.Issue(rider, horse);

                bool ok = Expect(report,
                    rider.Mount == null && rider.HeldMount == null && rider.OwnedMount == horse,
                    "a dropped mount is named by nothing but OwnedMount",
                    "the fixture did not build the dropped state");

                Unmake(rider);

                ok &= Expect(report,
                    horse.Deleted,
                    "and deleting its bot still takes it - no ownerless ostard left behind",
                    "the dropped ostard outlived its owner, which is the graveyard");

                return ok;
            }
            finally
            {
                Unmake(rider);
                Discard(horse);
            }
        }

        private static PlayerBot Make()
        {
            var bot = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);

            bot.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);

            return bot;
        }

        /// <summary>A horse waiting beside the bot, in the state BotMovement.Park leaves one.</summary>
        private static BaseMount Park(PlayerBot bot)
        {
            BaseMount horse = new Horse();

            horse.MoveToWorld(bot.Location, bot.Map);
            horse.SetControlMaster(bot);
            horse.ControlTarget = null;
            horse.ControlOrder = OrderType.Stay;

            bot.HeldMount = horse;
            BotMovement.Issue(bot, horse);

            return horse;
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

        private static void Discard(BaseMount horse)
        {
            if (horse != null && !horse.Deleted)
            {
                horse.Delete();
            }
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
