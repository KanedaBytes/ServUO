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

using System;
using System.Collections.Generic;

using Server.Items;

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
            bot.HaulPending = false;

            if (Insensitive.Equals(destination.Type, "bank"))
            {
                // Counted, not refused. A bank is a real delivery point when nothing is staffed -
                // that is what the 2.0 weight in BotDestinations.HaulWeightFor is for. It is only
                // a fault when a bench WAS available, and the work probe is the thing that knows.
                BotWorkSites.NoteBankDelivery();
            }

            int hauled = TakeYield(bot.Backpack, raw) + TakeYield(BotPackAnimals.PanniersOf(bot), raw);

            // The beast's work is done either way. Upstream walked it to a stables from here when
            // one was in range and deleted it when none was; there is no stables destination on
            // this graph, so this is their no-stables path. See the seam note in BotPackAnimal.
            BotPackAnimals.Release(bot);

            if (hauled <= 0)
            {
                return false;
            }

            CrafterBehavior buyer = FindBuyer(bot, raw);

            if (buyer == null)
            {
                // No trade buyer here. The load goes into the bank box rather than evaporating:
                // it is the bot's property, and a bank is where a player would have put it.
                Bank(bot, raw, hauled);
                Say(bot, "gather_deliver");
                BotWorkSites.NoteDelivery(hauled);

                Log.Debug("{0} banked {1} {2} at '{3}'.", bot.Name, hauled, raw.Name, destination.Id);

                return true;
            }

            PlayerBot crafter = OwnerOf(buyer);
            int accepted = buyer.Accept(crafter, hauled);

            if (accepted < hauled)
            {
                // The buyer's stock cap turned some of it away. Bank the remainder rather than
                // destroying it - CrafterStock.Add returning short is a real answer, not an error.
                Bank(bot, raw, hauled - accepted);
            }

            BotWorkSites.NoteDelivery(hauled);

            PlayScene(bot, crafter);

            Log.Debug(
                "{0} delivered {1} {2} to {3} at '{4}' ({5} accepted).",
                bot.Name,
                hauled,
                raw.Name,
                crafter == null ? "a crafter" : crafter.Name,
                destination.Id,
                accepted);

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

        /// <summary>Take every unit of a good out of a container and return how much there was.</summary>
        private static int TakeYield(Container container, Type raw)
        {
            if (container == null)
            {
                return 0;
            }

            var taken = new List<Item>();
            int amount = 0;

            foreach (Item item in container.Items)
            {
                if (item != null && !item.Deleted && item.GetType() == raw)
                {
                    amount += item.Amount;
                    taken.Add(item);
                }
            }

            foreach (Item item in taken)
            {
                item.Delete();
            }

            return amount;
        }

        /// <summary>
        /// Put an unsold load in the bank box.
        ///
        /// Mobile.BankBox exists on every Mobile, so a bot has one without anything being wired
        /// up for it. This is the only place in the session that touches banking at all, and it
        /// is storage rather than money: no balance is read and none is written.
        /// </summary>
        private static void Bank(PlayerBot bot, Type raw, int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            BankBox box = bot.BankBox;
            Item stock = Activator.CreateInstance(raw) as Item;

            if (stock == null)
            {
                return;
            }

            if (stock.Stackable)
            {
                stock.Amount = amount;
            }

            if (box == null || !box.TryDropItem(bot, stock, false))
            {
                stock.Delete();
            }
        }

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
