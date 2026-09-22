// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotWorkDelivery.cs — the hand-over at the end of a haul.
//
// A gatherer walks its load to town, and this is what happens when it gets
// there: the ore comes out of the pack and the panniers, and if a crafter of the
// matching trade is working within twelve tiles, it goes into that crafter's
// stock. Otherwise it goes to the bank box, which is the "no buyer today"
// ending rather than a failure.
//
// Upstream's DeliverMaterials, with the money taken out.
//
// Theirs paid the gatherer 2-4gp per unit, spent from the CRAFTER'S purse, and
// refused the sale outright when the crafter was broke. That is the economy, and
// the economy is 7f. So the load moves and the gold does not, which the brief
// asked for in those words - "the goods go in the pack and the ledger, nothing
// changes hands for gold". The scene still plays, because the scene is what
// makes it visible: the gatherer says its line, the crafter answers, the trade
// closes. Watching two bots meet in a smithy and hand over a day's ore is the
// entire point of the session, and none of it needs coin to read correctly.
//
// SEAM, restored by the economy session (7f): CrafterStock.SpendGold on the
// buyer, Gold into the seller's pack, and the refusal when the buyer cannot pay.
//
// -----------------------------------------------------------------------------
// CONSERVATION, 22 September 2026 (REVIEW.md F5).
//
// This hook used to report a delivery it had already destroyed. In order: it
// cleared HaulPending, DELETED every matching item in the pack and the panniers
// to count them, deleted the pack beast with whatever was still aboard, and only
// then went looking for somebody to give the load to. A smith at
// CrafterStock.StockCap, or a bank box that refused the drop, meant the load had
// gone - and NoteDelivery(hauled) recorded the whole of it as delivered anyway.
//
// The four rules it now keeps, Sean's, for deliveries only:
//
//   1. Settle only what the receiver accepts. The remainder stays in the pack.
//      Nothing is deleted to make the numbers match.
//   2. Release the pack animal after the hand-over, never before. An interrupted
//      hand-over leaves animal and load together.
//   3. A bot carrying an undelivered load is never chosen for logout
//      (BotSession.CanLogoutNow, and the re-check in BeginLogout).
//   4. A load lost anyway is written down - BotGoodsLedger.
//
// The primitives are in BotHaul; the books are in BotGoodsLedger and are read by
// the Bots.Conservation health check.
// -----------------------------------------------------------------------------

using System;

namespace Server.Custom
{
    public static class BotWorkDelivery
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>How far a gatherer will look for somebody to hand the load to.</summary>
        public const int BuyerRange = 12;

        /// <summary>
        /// Hand over the load, if this bot is carrying one and this is somewhere to leave it.
        ///
        /// Returns true when something actually moved. Called from the Traveler's arrival, BEFORE
        /// the handoff roll: arriving is what completes the errand, and whether the bot then
        /// stays is a separate question.
        /// </summary>
        public static bool TryDeliver(PlayerBot bot, NavDestination destination)
        {
            if (bot == null || bot.Deleted || destination == null)
            {
                return false;
            }

            if (!bot.HaulPending || !BotClassHelper.IsGatherer(bot.Class))
            {
                return false;
            }

            Type raw = BotHarvest.YieldFor(bot.Class);

            if (raw == null)
            {
                bot.HaulPending = false;
                return false;
            }

            if (!IsDeliveryPoint(destination, raw))
            {
                return false;
            }

            // AND THE BOT HAS TO BE AT ONE OF ITS ARRIVALS, not merely near the place.
            //
            // This was implicit until arrivals became places. An arrival used to be an exact tile,
            // so "arrived" and "standing on the delivery point" were the same fact and the caller's
            // own arrival test was enough. With range 2 on every non-work-site arrival, a bot now
            // counts as arrived from two tiles out - and DeliverIfWithinReach was always a bare
            // twelve-tile radius round the destination centre, which is most of a town block.
            //
            // Together those meant a laden miner could hand its ore over somewhere it was only
            // passing. The load is supposed to reach a bench somebody is working at; dropping it
            // near the door of the right building is the same lost load as dropping it anywhere.
            if (!AtArrival(bot, destination))
            {
                return false;
            }

            // The flag clears whatever happens next. A bot that reached a delivery point with an
            // empty pack has finished its errand just as much as one that reached it full, and
            // leaving the flag set would keep it hauling nothing round the town for ever.
            //
            // AND THAT IS STILL TRUE WHEN A REMAINDER IS KEPT. The errand was "take this to a
            // delivery point"; the point has been reached and the receiver has said how much it
            // wants. What is left rides along and is offered again at the next hand-over, which
            // is what the flag being clear lets the destination roll decide. The alternative -
            // a sticky flag - would pin a bot to a bench that is full for as long as it is full.
            bot.HaulPending = false;

            CrafterBehavior buyer = FindBuyer(bot, raw);
            PlayerBot crafter = buyer == null ? null : OwnerOf(buyer);

            Func<int, int> receiver = buyer == null
                ? null
                : new Func<int, int>(amount => buyer.Accept(crafter, amount));

            int offered;
            int accepted = Settle(bot, raw, receiver, destination.Id, out offered);

            if (offered <= 0)
            {
                // Arrived empty. The errand is over and nothing settled.
                return false;
            }

            if (accepted <= 0)
            {
                Log.Debug(
                    "{0} offered {1} {2} at '{3}' and none of it was taken; the load stays in the pack.",
                    bot.Name,
                    offered,
                    raw.Name,
                    destination.Id);

                return false;
            }

            if (buyer == null)
            {
                Say(bot, "gather_deliver");

                Log.Debug(
                    "{0} banked {1} of {2} {3} at '{4}'.",
                    bot.Name,
                    accepted,
                    offered,
                    raw.Name,
                    destination.Id);

                return true;
            }

            PlayScene(bot, crafter);

            Log.Debug(
                "{0} delivered {1} of {2} {3} to {4} at '{5}'.",
                bot.Name,
                accepted,
                offered,
                raw.Name,
                crafter == null ? "a crafter" : crafter.Name,
                destination.Id);

            return true;
        }

        /// <summary>
        /// Is this somewhere a haul can be left?
        ///
        /// A bank always is. A station is, when its trade is the one that buys this good — so a
        /// lumberjack's logs go to the carpenter's shop and not to the forge, which is the whole
        /// reason CrafterProfile carries RawGood.
        /// </summary>
        /// <summary>
        /// Is the bot standing at one of this destination's arrival points?
        ///
        /// Each arrival's OWN range, not a single number: a forge authors 2 because
        /// DefBlacksmithy needs the fixtures in reach, and a bank authors 2 because a counter is a
        /// place. Using the widest of them, or a constant, would put the loose rule back.
        ///
        /// A destination with no arrivals at all falls back to its own tile. That is the honest
        /// answer for a record which has not said where to stand, and Nav.Data already warns about
        /// it separately.
        /// </summary>
        private static bool AtArrival(PlayerBot bot, NavDestination destination)
        {
            if (destination.ArrivalList == null || destination.ArrivalList.Count == 0)
            {
                return bot.InRange(destination.Location, 1);
            }

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                // Plus one, the apron: the stand-tile sweep and the walker's own free-tile shift
                // may both settle a bot on the ring just outside the authored range, and a bot
                // that walked all the way there should not be refused on the last tile.
                if (bot.InRange(arrival.Location, arrival.Range + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDeliveryPoint(NavDestination destination, Type raw)
        {
            if (Insensitive.Equals(destination.Type, "bank"))
            {
                return true;
            }

            foreach (CrafterProfile profile in CrafterProfiles.All)
            {
                if (profile.RawGood != raw)
                {
                    continue;
                }

                if (!Insensitive.Equals(destination.Type, profile.StationType))
                {
                    continue;
                }

                if (profile.StationTag == null || destination.HasTag(profile.StationTag))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A working crafter nearby whose trade wants this good.
        ///
        /// A bounded GetMobilesInRange, not a World.Mobiles sweep, and the same shape
        /// FaceNearestPerson already uses (CLAUDE.md section 15). The enumerable is pooled and
        /// must be freed, which is why the loop is wrapped.
        /// </summary>
        private static CrafterBehavior FindBuyer(PlayerBot seller, Type raw)
        {
            Map map = seller.Map;

            if (map == null || map == Map.Internal)
            {
                return null;
            }

            IPooledEnumerable eable = map.GetMobilesInRange(seller.Location, BuyerRange);

            try
            {
                foreach (Mobile mobile in eable)
                {
                    var other = mobile as PlayerBot;

                    if (other == null || other == seller || other.Deleted || !other.Alive)
                    {
                        continue;
                    }

                    var crafter = other.Behavior as CrafterBehavior;

                    if (crafter != null && crafter.RawGood == raw)
                    {
                        return crafter;
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            return null;
        }

        /// <summary>
        /// Which bot is wearing this brain.
        ///
        /// A behaviour does not hold a back-reference to its bot — every hook is handed one
        /// instead — so the owner is found the same way everything else here finds a bot: through
        /// LiveRegistry, by identity of the brain.
        /// </summary>
        private static PlayerBot OwnerOf(PlayerBotBehavior behavior)
        {
            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot != null && ReferenceEquals(bot.Behavior, behavior))
                {
                    return bot;
                }
            }

            return null;
        }

        /// <summary>
        /// The hand-over itself: what is on offer, what the receiver takes, and what stays.
        /// Returns the accepted amount and reports what was offered.
        ///
        /// SEPARATE FROM TryDeliver ON PURPOSE. Everything above this in TryDeliver is about
        /// WHERE the bot is standing - the destination's type, its arrivals, the twelve-tile
        /// buyer sweep - and none of it is what REVIEW.md F5 was about. This is, so this is the
        /// piece HaulFixtures drives: a bot, a load, a receiver, and the arithmetic that decides
        /// how much moves. Internal for that reason and no other.
        ///
        /// The receiver is a function of "how much is on offer" to "how much I will take", which
        /// is the shape CrafterBehavior.Accept and CrafterStock.Add already have. A NULL receiver
        /// means the bot's own bank box, which is what a delivery point with nobody working it
        /// comes to.
        /// </summary>
        internal static int Settle(
            PlayerBot bot, Type raw, Func<int, int> receiver, string destinationId, out int offered)
        {
            // COUNTED, NOT TAKEN. This is the whole of REVIEW.md F5 in one line: the load is
            // still in the pack and the panniers while the receiver decides, so a refusal costs
            // nothing and a partial acceptance leaves the rest exactly where it was.
            offered = BotHaul.Offered(bot, raw);

            if (offered <= 0)
            {
                // Nothing to hand over. The errand is done and the beast's work with it.
                BotHaul.ReleaseIfEmpty(bot);
                return 0;
            }

            int accepted;

            if (receiver == null)
            {
                // No trade buyer here, so the bank box is the receiver: it is the bot's own
                // property, and a bank is where a player would have put it. The actual stacks
                // move - hue, name and weight with them - and whatever the box will not hold
                // stays in the pack rather than being deleted to make the count come out.
                //
                // Banking is NOT an exit from custody, which is why there is no NoteAccepted
                // here: the units are still the bot's, standing in a different container.
                accepted = BotHaul.MoveToBank(bot, raw, offered);

                if (accepted > 0)
                {
                    // Counted, not refused. A bank is a real delivery point when nothing is
                    // staffed - that is what the 2.0 weight in BotDestinations.HaulWeightFor is
                    // for. It is only a fault when a bench WAS available, and the work probe is
                    // the thing that knows.
                    //
                    // On the SETTLEMENT now, not on the destination's type: a no-buyer bank-box
                    // fallback at a forge is a bank delivery, and arriving at a bank with an
                    // empty pack is not one.
                    BotWorkSites.NoteBankDelivery();
                }
            }
            else
            {
                // Clamped, because everything downstream trusts this number: a receiver that
                // over-reported would have Consume take more than was offered.
                accepted = Math.Max(0, Math.Min(offered, receiver(offered)));

                // Exactly what was taken, and not a unit more. CrafterStock.Add turns raw into
                // refined stock in the buyer's pack, so these units really are spent; the rest
                // of the load never moved.
                //
                // The books are told what was actually REMOVED, not what the receiver claimed.
                // The two cannot differ today - `accepted` is clamped to what Offered counted a
                // moment earlier, and nothing runs in between - but a receiver that reached into
                // the seller's pack would break that, and the ledger would then be wrong rather
                // than loud. It is loud instead.
                int consumed = BotHaul.Consume(bot, raw, accepted);

                if (consumed != accepted)
                {
                    Log.Warn(
                        "{0} was told {1} {2} had been accepted but only {3} could be taken out of the pack.",
                        bot.Name,
                        accepted,
                        raw.Name,
                        consumed);
                }

                BotGoodsLedger.NoteAccepted(bot, consumed);
            }

            BotGoodsLedger.NoteHandover(bot, destinationId, offered, accepted);
            BotWorkSites.NoteDelivery(accepted);

            // RULE 2, AND IT IS THE REASON THIS LINE IS DOWN HERE. Release used to run before the
            // buyer had even been looked for, and BotPackAnimals.Release deletes the beast with
            // whatever is still in the panniers - so a hand-over that was then refused lost the
            // pannier half of the load as well. An interrupted hand-over now leaves animal and
            // load together and the pair walks on.
            //
            // Upstream disposes of the beast in its CALLER, after DeliverMaterials returns
            // (uo-offline TravelerBehavior.cs:2484-2508): to a stables when one is in reach, and
            // loose when none is. There is no stables destination on this graph, so this is their
            // no-stables path with their ordering. See the seam note in BotPackAnimal.
            BotHaul.ReleaseIfEmpty(bot);

            return accepted;
        }

        // TakeYield and Bank are GONE, and their removal is the repair (REVIEW.md F5).
        //
        // TakeYield counted a load by deleting it, so the units had already stopped existing by
        // the time anything was asked whether it wanted them. Bank then built a REPLACEMENT stack
        // with Activator.CreateInstance - losing hue, name, weight and loot type off the originals
        // - and deleted that too when the box would not take it.
        //
        // BotHaul.Offered, BotHaul.Consume and BotHaul.MoveToBank are what those two became:
        // count without taking, remove exactly what was accepted, and move the real items.

        /// <summary>
        /// The hand-over, said out loud.
        ///
        /// Upstream's BotScene sequenced four lines on a shared clock; there is no scene runner
        /// here, so the same shape is built from delayed calls. The delays are theirs and they
        /// are what makes it read as two people rather than two packets: an answer that arrives
        /// in the same tick as the question is the loudest tell there is.
        /// </summary>
        private static void PlayScene(PlayerBot seller, PlayerBot buyer)
        {
            Say(seller, "gather_deliver");

            if (buyer == null || buyer.Deleted)
            {
                return;
            }

            Timer.DelayCall(TimeSpan.FromSeconds(2.0), () => Say(buyer, "craft_buy"));
            Timer.DelayCall(TimeSpan.FromSeconds(3.5), () => Say(seller, "trade_close"));
        }

        private static void Say(PlayerBot bot, string category)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            string line = ChatLibrary.PickRandom(category);

            if (String.IsNullOrEmpty(line))
            {
                return;
            }

            var context = new ChatTokenContext { Bot = bot };
            string resolved;

            if (ChatTokens.TryResolve(line, context, out resolved))
            {
                bot.Say(resolved);
                ChatLibrary.NoteSpoken(resolved);
            }
        }
    }
}
