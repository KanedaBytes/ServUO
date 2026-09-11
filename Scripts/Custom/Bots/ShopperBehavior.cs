// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// ShopperBehavior.cs — browsing.
//
// A bot moves between the arrival points of ONE shop, pauses at each as if
// looking at something, and leaves when its visit lapses.
//
// UPSTREAM'S SHOPPER DOES NOT MOVE AT ALL. It is rotation-only by explicit
// design - "No movement = no wall-grinding" - because it leaned on a zone check
// to guarantee it was already standing in the right place, and because their bot
// was a PlayerMobile with no walker to lean on. The tell is that their file still
// carries Home, HomeMap and VendorSpeakRange fields that are written and never
// read: vestiges of a walking version that was removed.
//
// This is that version. The hops are two or three tiles between authored arrival
// points, they go through NavWalker like every other walk on this shard, and a
// shopper wedged behind a counter gets the recovery ladder for free. What made
// movement dangerous for them is what step 4a and 7b already solved here.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public class ShopperBehavior : PlayerBotBehavior
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>How long a shopper stands at one spot before moving to the next.</summary>
        public static readonly TimeSpan MinPause = TimeSpan.FromSeconds(8.0);

        public static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(20.0);

        /// <summary>
        /// The lines that a real player used to open a vendor.
        ///
        /// "vendor buy" appears twice because it was overwhelmingly the common one - a duplicate
        /// entry is how this corpus weights anything, and it is upstream's own convention.
        /// </summary>
        private static readonly string[] VendorTriggers =
        {
            "vendor buy",
            "vendor buy",
            "vendor sell",
            "vendor view",
            "show me your wares",
            "i'd like to see what you have",
            "let me see your goods",
        };

        /// <summary>How long between vendor trigger lines. Its own cadence, not the chat one.</summary>
        private static readonly TimeSpan MinVendorLine = TimeSpan.FromSeconds(12.0);

        private static readonly TimeSpan MaxVendorLine = TimeSpan.FromSeconds(28.0);

        private string _destinationId;
        private NavWalker _walker;
        private long _pauseUntil;
        private long _nextVendorLine;
        private bool _walking;

        public ShopperBehavior()
        {
            ChatCategories = new[] { ChatLibrary.Shopping, ChatLibrary.SmallTalk };
            ChatChance = 0.18;
            MinChatCooldown = TimeSpan.FromSeconds(20.0);
            MaxChatCooldown = TimeSpan.FromSeconds(50.0);
        }

        /// <summary>The walker steering this bot mid-journey, or null. See PlayerBotBehavior.</summary>
        public override NavWalker Walker
        {
            get { return _walker; }
        }

        public override string SerializableName
        {
            get { return "Shopper"; }
        }

        /// <summary>The shop being browsed.</summary>
        public string DestinationId
        {
            get { return _destinationId; }
            set { _destinationId = value; }
        }

        /// <summary>Where a {place} token resolves from while this bot is browsing.</summary>
        public override string CurrentDestinationId
        {
            get { return _destinationId; }
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            return _destinationId == null ? "shopping" : "shopping at " + _destinationId;
        }

        /// <summary>
        /// A shopper mid-hop is not interrupted, so it cannot be left standing between two
        /// counters by a lifecycle roll. The visit clock ends it instead.
        /// </summary>
        public override bool CanTransition(PlayerBot bot)
        {
            return !_walking;
        }

        public override void OnAttached(PlayerBot bot)
        {
            // Upstream walks into shops rather than running (TravelerBehavior.cs:2038), so the
            // pace drops either way; whether the bot gets off to browse is its disposition.
            BotMovement.Settle(bot, mustDismount: false);

            Pause();
        }

        public override void OnDetached(PlayerBot bot)
        {
            Release(bot);

            if (_walker != null)
            {
                _walker.Arrived = null;
                _walker.RungFired = null;
                _walker = null;
            }
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            if (CheckVisitExpired(bot))
            {
                return;
            }

            // Browsing chatter, on the ordinary ambient cadence.
            TrySpeak(bot);

            // The vendor trigger, on its own clock. Upstream's shopper does not walk, so THIS is
            // its shopping; here it runs alongside the walking rather than instead of it, and the
            // two together are what browsing looks like from outside.
            //
            // Deliberately a bare Say, as upstream has it: these are typed commands, not chatter,
            // so they skip the capitalisation roll and the ambient cooldown entirely. They are
            // also safe to say near a real vendor - Mobile.Say never reaches a listener's
            // OnSpeech, only client speech packets do. See PlayerBot.OnSpeech.
            if (Core.TickCount - _nextVendorLine >= 0 && IsPlayerNearby(bot))
            {
                ScheduleNextVendorLine();

                string trigger = VendorTriggers[Utility.Random(VendorTriggers.Length)];

                bot.Say(trigger);

                bot.SpeechLines++;
                ChatLibrary.NoteSpoken(trigger);
            }

            if (_walking)
            {
                // The same watchdog the Traveler carries, for the same reason: NavWalker calls
                // Stop rather than Finish when a mobile is deleted or lands off-facet, so a walk
                // can end without ever raising Arrived.
                if (_walker == null || !_walker.Active)
                {
                    Release(bot);
                    Pause();
                }

                return;
            }

            // Wraparound-safe: compare by subtraction, never a < b.
            if (Core.TickCount - _pauseUntil < 0)
            {
                // LOOKING OVER THE GOODS, which is upstream's whole idle texture and the half of
                // their shopper we had not ported. Theirs turns to a random facing 15% of a tick
                // (uo-offline ShopperBehavior.cs:113-122) and does nothing else, because it does not
                // move at all; ours walks between the shop's arrival points, so it needs this for
                // the eight to twenty seconds between hops.
                //
                // It only reads as texture now that the stock wander is gone: before the fidget fix
                // a paused shopper was taking 21 steps a bot-minute of BaseAI.WalkRandomInHome, and
                // a turn added to that was invisible. A turn is not a step and the step census does
                // not count it - Direction is not Location.
                if (Utility.RandomDouble() < BotLifecycle.Config_.Idle.ShopperTurnChance)
                {
                    bot.Direction = (Direction)Utility.Random(8);
                }

                return;
            }

            MoveToAnotherSpot(bot);
        }

        private void Pause()
        {
            int seconds = Utility.RandomMinMax((int)MinPause.TotalSeconds, (int)MaxPause.TotalSeconds);

            _pauseUntil = Core.TickCount + (seconds * 1000L);
        }

        private void ScheduleNextVendorLine()
        {
            int seconds = Utility.RandomMinMax(
                (int)MinVendorLine.TotalSeconds,
                (int)MaxVendorLine.TotalSeconds);

            _nextVendorLine = Core.TickCount + (seconds * 1000L);
        }

        /// <summary>
        /// Walk to a different arrival point of the same shop.
        ///
        /// Deliberately the same destination: a shopper browses ONE shop. Leaving is the visit
        /// expiring, not wandering off to the next building.
        /// </summary>
        private void MoveToAnotherSpot(PlayerBot bot)
        {
            NavDestination destination = _destinationId == null ? null : Nav.Destination(_destinationId);

            if (destination == null)
            {
                // Nothing to browse. Stand still rather than guess - the visit will end shortly.
                Pause();
                return;
            }

            IList<NavArrival> arrivals = destination.ArrivalList;

            if (arrivals == null || arrivals.Count < 1)
            {
                // No spot at all. There is nowhere to stand, which is a fact about the data
                // rather than a fault: browse in place.
                Pause();
                return;
            }

            // ONE SPOT IS STILL A BROWSE, and this gate used to be `< 2`, which pinned half the
            // shops on the facet. Sixteen of the thirty-two shop destinations have exactly one
            // arrival, so once the seed started setting DestinationId at all those bots would have
            // gone straight back to standing still - the same symptom, arrived at one step later.
            //
            // Nav.TryPickArrival scatters: NavArrivals.Scatter offsets by Custom.NavArrivalScatter
            // (2) around the chosen arrival and validates the candidate for standing, so a
            // single-arrival shop browses a 5x5 box rather than a pair of tiles. None of the
            // sixteen is `exact` or `exclusive`, which are the two flags that suppress the scatter,
            // and NavArrivals.Eligible only drops an arrival when it is BOTH exclusive and
            // occupied - so the pick can never fall through to the destination CENTRE, which for a
            // shop is as likely to be a counter as a floor.
            //
            // Roughly a third of picks land within one tile and are dropped by the already-there
            // guard below; those simply pause again, which is a shopper pausing rather than a
            // shopper stuck.

            Point3D spot;
            int range;

            if (!Nav.TryPickArrival(_destinationId, bot, out spot, out range))
            {
                Pause();
                return;
            }

            // Already there. Do not build a route to the tile the bot is standing on - MovementPath
            // returns nothing for an adjacent goal and the walk would end instantly.
            if (bot.InRange(spot, 1))
            {
                Pause();
                return;
            }

            var steps = new List<NavStep>();
            steps.Add(new NavStep(spot, destination.Map, null, NavStepKind.Arrival, range));

            var route = new NavRoute(steps, 0.0);

            if (_walker == null)
            {
                _walker = new NavWalker(bot);
                _walker.Arrived = OnArrived;
                LogWalker(bot, _walker);
            }

            bot.Commuting = true;
            _walking = true;

            _walker.Follow(route);
        }

        private void OnArrived(NavWalker walker)
        {
            var bot = walker.Mobile as PlayerBot;

            _walking = false;

            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.Commuting = false;

            Pause();
        }

        private void Release(PlayerBot bot)
        {
            _walking = false;

            if (_walker != null)
            {
                _walker.Stop();
            }

            if (bot != null)
            {
                bot.Commuting = false;
            }
        }
    }
}
