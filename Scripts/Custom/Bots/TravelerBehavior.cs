// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// TravelerBehavior.cs — a bot goes somewhere, stands about, then goes somewhere
// else.
//
// The feet are NOT here. NavWalker has driven movement on its own shared timer
// since step 4a, and it owns hop failure, the recovery ladder and the arrival
// tolerance. This picks a destination, hands the route over, and decides what to
// do on arrival. Upstream's TravelerBehavior is 3,216 lines because it also owns
// leg walking, stuck recovery, magic travel and the handoff to eight other
// behaviours; almost none of that is this class's job here.
//
// The pattern is DailyLifeTownsfolk's, not ShopScheduleSystem's: PlayerBot
// already clears Home and RangeHome in its constructor, so KeepHomeAligned stays
// false and nothing has to restore Home afterwards.

using System;

namespace Server.Custom
{
    public class TravelerBehavior : PlayerBotBehavior
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How long a bot stands at a destination before choosing another.
        ///
        /// Bounded, always. Upstream had a "Wait" arrival style meaning "indefinitely, until the
        /// lifecycle moves it"; in practice it parked 40% of arrivals for entire sessions and made
        /// their status page read as a stuck-bot epidemic. There is no lifecycle here yet to move
        /// anyone on, which would make an unbounded linger permanent.
        /// </summary>
        public static readonly TimeSpan MinLinger = TimeSpan.FromSeconds(20.0);

        public static readonly TimeSpan MaxLinger = TimeSpan.FromSeconds(90.0);

        private enum TravelState
        {
            Choosing = 0,
            Walking = 1,
            Lingering = 2,
        }

        /// <summary>What a bot talks about on the road.</summary>
        private static readonly string[] AmbientChat = { ChatLibrary.Traveling, ChatLibrary.SmallTalk };

        private TravelState _state;
        private NavWalker _walker;
        private string _destinationId;
        private string _destinationName;
        private string _lastDestinationId;
        private long _lingerUntil;

        /// <summary>When this bot next turns while standing about. See the Lingering case.</summary>
        private long _nextIdleTurn;

        public TravelerBehavior()
        {
            // Quieter and slower than the bank crowd. A bot on the road is walking past you, not
            // standing next to you, and the same cadence would read as a stream of nonsense
            // trailing across town.
            ChatCategories = AmbientChat;
            ChatChance = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(30.0);
            MaxChatCooldown = TimeSpan.FromSeconds(90.0);
        }

        public override string SerializableName
        {
            get { return "Traveler"; }
        }

        /// <summary>Where a {place} token resolves from - where this bot is heading, or standing.</summary>
        public override string CurrentDestinationId
        {
            get { return _destinationId; }
        }

        /// <summary>
        /// What to talk about while standing at a destination.
        ///
        /// SEAM 1, paid off. Upstream keyed this on its 31-member DestinationType enum, which is
        /// the enum this shard deliberately did not port - our destination type is a free token
        /// with nine values, and the discrimination that matters lives in the tags. So a shop is
        /// not one thing here: it is a forge or a bakery depending on what it is tagged, exactly
        /// as the destination weighting already treats it.
        ///
        /// The mapping is coarse on purpose. There are no tavern or forge chat files in the
        /// corpus yet - upstream's own comment says the same of theirs - so this points several
        /// places at small_talk rather than inventing categories with nothing behind them.
        /// </summary>
        private static string[] ArrivalChatFor(NavDestination destination)
        {
            if (destination == null)
            {
                return new[] { ChatLibrary.SmallTalk };
            }

            switch (destination.Type)
            {
                case "bank":
                {
                    // No "wts": a bot stopping at a bank is holding nothing to sell.
                    return new[] { ChatLibrary.BankActions, ChatLibrary.Wtb, ChatLibrary.SmallTalk };
                }

                case "tavern":
                case "inn":
                {
                    // Where you found a group, so lfg belongs here as much as at the bank.
                    return new[] { ChatLibrary.SmallTalk, ChatLibrary.Lfg };
                }

                case "shop":
                {
                    return new[] { ChatLibrary.Wtb, ChatLibrary.Shopping, ChatLibrary.SmallTalk };
                }

                case "forge":
                {
                    return new[] { "craft_talk", ChatLibrary.SmallTalk };
                }

                case "mine":
                case "lumber":
                {
                    // Nobody is passing through a rock face. A traveller standing at one is a
                    // gatherer between shifts, so it talks about the work.
                    return new[] { "gather_talk", ChatLibrary.SmallTalk };
                }

                default:
                {
                    return new[] { ChatLibrary.SmallTalk };
                }
            }
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            switch (_state)
            {
                case TravelState.Walking:
                    return String.Format("walking to {0}", _destinationName ?? _destinationId ?? "somewhere");

                case TravelState.Lingering:
                    return String.Format("at {0}", _destinationName ?? _destinationId ?? "a destination");

                default:
                    return "deciding where to go";
            }
        }

        /// <summary>True while a route is actually being walked. Counted by Bots.Population.</summary>
        public bool IsTravelling
        {
            get { return _state == TravelState.Walking; }
        }

        public bool IsLingering
        {
            get { return _state == TravelState.Lingering; }
        }

        /// <summary>Where this bot is heading, or null. Counted toward a destination's crowd floor.</summary>
        public string DestinationId
        {
            get { return _destinationId; }
        }

        /// <summary>
        /// A Traveler mid-walk is not interrupted by the lifecycle. Abandoning a journey halfway
        /// down a street looks like a bug; finishing it and then rolling looks like a decision.
        /// </summary>
        public override bool CanTransition(PlayerBot bot)
        {
            return _state != TravelState.Walking;
        }

        /// <summary>The walker steering this bot, or null. The smoke reads its rung counts.</summary>
        public override NavWalker Walker
        {
            get { return _walker; }
        }

        public override void OnAttached(PlayerBot bot)
        {
            _state = TravelState.Choosing;
        }

        public override void OnDetached(PlayerBot bot)
        {
            // A behaviour that is no longer attached must not still be steering. Without this a
            // swap leaves the walker running and Commuting set, so the stock wander stays
            // suppressed for a bot nothing is driving - a mobile that stands still for ever.
            Discard(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            switch (_state)
            {
                case TravelState.Walking:
                {
                    // THE WATCHDOG, and the reason this behaviour needs a tick at all.
                    //
                    // NavWalker does not always call Arrived. Its Tick calls Stop() rather than
                    // Finish() when the mobile is deleted or lands off-facet, and DriveAll stops a
                    // faulting walker the same way. A Traveler that waited only on the callback
                    // would sit here for ever with Commuting still true and the stock wander
                    // suppressed - exactly the frozen-NPC failure the recovery ladder removed,
                    // reintroduced one layer up.
                    if (_walker == null || !_walker.Active)
                    {
                        Log.Debug(
                            "{0}'s walk to '{1}' ended without arriving; choosing again.",
                            bot.Name,
                            _destinationId ?? "?");

                        BotTickManager.NoteAbandoned();
                        Release(bot);

                        // A walk that ended short still ends where it ended. uo-offline's
                        // DeliverMaterials runs from the arrival handoff after a drift that
                        // stalled or timed out, and finds its buyer within twelve tiles of
                        // wherever the gatherer stands (BotEconomy.cs:298); a laden miner whose
                        // last step was refused is standing in the smithy with the ore on its
                        // back, and walking off to roll another destination with it would be
                        // the one thing no player ever did. Only within the buyer's reach of
                        // the destination, though: a walk abandoned half a map away has not
                        // arrived anywhere, and banking the load there would be a lie.
                        DeliverIfWithinReach(bot);

                        _state = TravelState.Choosing;

                        return;
                    }

                    // Chatter on the road. The categories are the travelling ones - a bot walking
                    // down a street must not say "still on the road" while standing in a bank,
                    // which is why they are swapped rather than shared with the arrival set.
                    ChatCategories = AmbientChat;
                    TrySpeak(bot);

                    return;
                }

                case TravelState.Lingering:
                {
                    // Standing somewhere, so talk about being there.
                    ChatCategories = ArrivalChatFor(
                        _destinationId == null ? null : Nav.Destination(_destinationId));

                    TrySpeak(bot);

                    // FACE A NEW WAY NOW AND THEN, which is all upstream's arrived Traveler does
                    // with no handoff: "Occasionally turn to face a new direction so the bot reads
                    // as awake. Every 4-9 seconds, not every tick" (uo-offline
                    // TravelerBehavior.cs:2727-2732). There is no movement in theirs and there is
                    // none here.
                    //
                    // This is the phase the step census measured WORST before the fidget fix - 34.3
                    // idle steps per bot-minute, higher than either resident brain - because a
                    // lingering Traveler sets no Home at all, and WalkRandomInHome special-cases a
                    // zero Home into a free untethered WalkRandom (BaseAI.cs:2516, :2542). It is
                    // now still, and a turn is what makes still read as awake rather than as frozen.
                    if (Core.TickCount - _nextIdleTurn >= 0)
                    {
                        bot.Direction = (Direction)Utility.Random(8);
                        ScheduleNextIdleTurn();
                    }

                    // Wraparound-safe: compare by subtraction, never a < b (CLAUDE.md section 15).
                    if (Core.TickCount - _lingerUntil >= 0)
                    {
                        _state = TravelState.Choosing;
                    }

                    return;
                }

                default:
                {
                    TryDepart(bot);
                    return;
                }
            }
        }

        /// <summary>
        /// Send this bot to a named destination now, bypassing the weighted roll.
        ///
        /// For the walk probe, which needs five bots aimed at ONE place to create contention -
        /// something a weighted roll will not do on purpose. It goes through the same routing and
        /// the same walker as an ordinary departure, so what it exercises is the real path.
        /// </summary>
        public bool SendTo(PlayerBot bot, NavDestination destination)
        {
            if (bot == null || bot.Deleted || destination == null)
            {
                return false;
            }

            Release(bot);

            return Depart(bot, destination);
        }

        private void TryDepart(PlayerBot bot)
        {
            // Route planning is the expensive part of this tick, so it is what the budget counts.
            if (!BotTickManager.TryTakePlan())
            {
                return;
            }

            NavDestination destination = BotDestinations.Pick(bot, _lastDestinationId);

            if (destination == null)
            {
                BotTickManager.NoteNoDestination();
                return;
            }

            if (!Depart(bot, destination))
            {
                BotTickManager.NoteNoRoute();
            }
        }

        private bool Depart(PlayerBot bot, NavDestination destination)
        {
            NavRoute route;
            string error;

            // Passing the bot as forMobile is what makes the route end on a PICKED ARRIVAL POINT
            // rather than the destination's centre tile. With five bots converging that is the
            // difference between arriving beside each other and all aiming at one tile.
            if (!Nav.TryRouteFrom(bot.Location, bot.Map, destination.Id, bot, out route, out error))
            {
                // Not an error. The destination may be on another facet, or behind an edge the
                // graph cannot cross today; either way the bot simply picks again next tick.
                Log.Debug("{0} cannot route to '{1}': {2}", bot.Name, destination.Id, error);

                BotLog.Note(bot, BotLogKind.Route, "no route to '{0}': {1}", destination.Id, error);

                return false;
            }

            if (_walker == null)
            {
                _walker = new NavWalker(bot);
                _walker.Arrived = OnArrived;
                LogWalker(bot, _walker);
            }

            _destinationId = destination.Id;
            _destinationName = destination.Name;

            // Set before Follow, cleared before Stop - the DailyLifeTownsfolk order. This flag is
            // the entire stand-aside mechanism: BotAI.DoActionWander reads it and declines to
            // issue a step, so the wander and the walker do not fight over the move gate.
            bot.Commuting = true;

            _state = TravelState.Walking;

            // Run the long ones, walk the short ones - upstream's rule at
            // TravelerBehavior.cs:1927, against its RunThresholdTiles of 25. No mount is granted
            // here: a bot owns its horse from the moment it spawns, and getting one is a trip to
            // the stables, not something that happens because it decided to go somewhere.
            BotMovement.SetPace(
                bot,
                BotMovement.PaceForRoute(route) );

            BotLog.Note(bot, BotLogKind.Route, "departing for '{0}' ({1}), {2} hop(s)",
                destination.Id, destination.Name, route.Count);

            _walker.Follow(route);

            return true;
        }

        private void OnArrived(NavWalker walker)
        {
            var bot = walker.Mobile as PlayerBot;

            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.Commuting = false;

            // Arrived: stop running. Somebody who has got where they were going slows to a walk
            // before they do anything else, and a bot still sprinting on the spot outside a shop
            // is the single most obviously wrong thing about how they used to move.
            BotMovement.SetPace(bot, BotPace.Walk);

            BotLog.Note(bot, BotLogKind.Arrive, "arrived at '{0}' ({1},{2})", _destinationId, bot.X, bot.Y);

            _lastDestinationId = _destinationId;
            _state = TravelState.Lingering;

            // Arriving somewhere worth staying turns the bot into somebody who is there. This
            // swaps the brain, which detaches THIS behaviour - nothing below may touch state.
            if (TryHandoff(bot))
            {
                return;
            }

            int seconds = Utility.RandomMinMax((int)MinLinger.TotalSeconds, (int)MaxLinger.TotalSeconds);

            _lingerUntil = Core.TickCount + (seconds * 1000L);

            ScheduleNextIdleTurn();
        }

        /// <summary>The gap between idle turns, from bots.json life.idle.turnSeconds.</summary>
        private void ScheduleNextIdleTurn()
        {
            BotIdleConfig idle = BotLifecycle.Config_.Idle;

            int seconds = Utility.RandomMinMax(
                (int)idle.MinTurn.TotalSeconds, (int)idle.MaxTurn.TotalSeconds);

            _nextIdleTurn = Core.TickCount + (seconds * 1000L);
        }

        /// <summary>
        /// Become whatever this destination is for, if the roll commits.
        ///
        /// NOT EVERY ARRIVAL COMMITS, and that is the point. Upstream took a bank handoff 40% of
        /// the time - "many bank visitors just pass through" - and a shop 80%. A place where every
        /// arrival stops is a queue; the ones who pass through are what make it look like a town.
        ///
        /// A declined SHOP arrival leaves at once rather than lingering: a bot standing in a
        /// smithy doing nothing makes no sense. A declined BANK arrival is allowed to linger,
        /// because loitering outside a bank is exactly what people did.
        /// </summary>
        private bool TryHandoff(PlayerBot bot)
        {
            NavDestination destination = _destinationId == null ? null : Nav.Destination(_destinationId);

            if (destination == null)
            {
                return false;
            }

            // THE HAUL COMES OFF FIRST, before anything decides whether to stay. Upstream orders
            // it the same way, and the order matters: a miner that arrived at a smithy has done
            // what it came to do whether or not it then loiters, and a handoff roll that declined
            // would otherwise send it away still carrying the ore.
            BotWorkDelivery.TryDeliver(bot, destination);

            string type = destination.Type;

            // A bot at its own station is not a passer-by, and the per-type number is wrong for
            // it: the 0.8 shop handoff would send a Tailor away from the tailor shop one visit in
            // five for no reason. Upstream used a flat 0.95 for a station and so do we.
            bool ownStation = BotWorkSites.IsOwnStation(bot, destination);

            double chance = ownStation
                ? BotLifecycle.Config_.Station
                : BotLifecycle.Config_.HandoffChance(type);

            if (chance <= 0.0)
            {
                return false;
            }

            // An under-floor destination pulls harder: a bot that walked to a bank nobody is
            // standing at should be likelier to stay than one arriving at a full house.
            if (BotCrowds.Shortfall(destination) > 0)
            {
                chance += (1.0 - chance) * 0.5;
            }

            if (Utility.RandomDouble() > chance)
            {
                if (Insensitive.Equals(type, "shop"))
                {
                    // Leave immediately rather than loiter in somebody's shop.
                    _state = TravelState.Choosing;
                }

                return false;
            }

            PlayerBotBehavior visit = BuildVisit(bot, type, destination);

            if (visit == null)
            {
                return false;
            }

            // The windows live in BotBehaviors now, because [BotBehavior has to set the same ones.
            TimeSpan? window = BotBehaviors.VisitWindowFor(visit);

            if (window == null)
            {
                return false;
            }

            int minutes = (int)window.Value.TotalMinutes;

            visit.VisitExpiresAt = CustomTime.Now + window.Value;

            Log.Debug(
                "{0} is staying at '{1}' as a {2} for {3} minute(s).",
                bot.Name,
                destination.Id,
                visit.SerializableName,
                minutes);

            bot.SetBehavior(visit, "arrival handoff");

            return true;
        }

        /// <summary>
        /// The one dispatch point from "arrived somewhere" to "became something".
        ///
        /// Work is checked FIRST, and by the bot's own class rather than by the destination's
        /// type, because the same tailor shop is a station to a Tailor and a shop to everybody
        /// else. Getting that order wrong would put a Tailor to work browsing its own counter.
        /// </summary>
        private static PlayerBotBehavior BuildVisit(PlayerBot bot, string type, NavDestination destination)
        {
            if (BotWorkSites.IsOwnStation(bot, destination))
            {
                if (BotClassHelper.IsGatherer(bot.Class))
                {
                    var gatherer = new GathererBehavior();
                    gatherer.DestinationId = destination.Id;
                    return gatherer;
                }

                if (CrafterProfiles.For(bot) != null)
                {
                    var crafter = new CrafterBehavior();
                    crafter.DestinationId = destination.Id;
                    return crafter;
                }
            }

            if (Insensitive.Equals(type, "bank"))
            {
                var sitter = new BankSitterBehavior();
                sitter.DestinationId = destination.Id;
                return sitter;
            }

            if (Insensitive.Equals(type, "shop"))
            {
                var shopper = new ShopperBehavior();
                shopper.DestinationId = destination.Id;
                return shopper;
            }

            return null;
        }

        /// <summary>
        /// Stop steering, without pretending the journey finished.
        ///
        /// The walker INSTANCE is deliberately kept and reused, as DailyLifeTownsfolk keeps its
        /// own: Follow calls Stop internally, so re-following is safe, and a walker that survives
        /// the journey also carries its recovery history with it. Discarding it on every
        /// destination change threw away the rung counts, which is how the first walk probe
        /// reported zero rungs for bots that had visibly climbed the ladder.
        ///
        /// Arrived stays wired for the same reason. It is only ever raised from Finish, at the end
        /// of a completed route - Stop does not raise it - so a cancelled walk cannot fire an
        /// arrival that did not happen. ShopScheduleSystem nulls it because it throws the walker
        /// away; we do not.
        /// </summary>
        private void Release(PlayerBot bot)
        {
            if (_walker != null)
            {
                _walker.Stop();
            }

            if (bot != null)
            {
                bot.Commuting = false;
            }
        }

        /// <summary>
        /// Hand over a haul from a walk that ended short, when the destination is a delivery
        /// point within the buyer's reach of where the bot stands. See the abandon branch.
        /// </summary>
        private void DeliverIfWithinReach(PlayerBot bot)
        {
            if (bot == null || !bot.HaulPending || _destinationId == null)
            {
                return;
            }

            NavDestination destination = Nav.Destination(_destinationId);

            if (destination == null || destination.Map != bot.Map
                || !bot.InRange(destination.Location, BotWorkDelivery.BuyerRange))
            {
                return;
            }

            if (BotWorkDelivery.TryDeliver(bot, destination))
            {
                BotLog.Note(bot, BotLogKind.Arrive,
                    "delivered at '{0}' from {1},{2}, short of the arrival tile",
                    _destinationId, bot.X, bot.Y);
            }
        }

        /// <summary>
        /// Let the walker go entirely. Only on detach, where the behaviour itself is going away
        /// and there is nothing left to reuse it for.
        /// </summary>
        private void Discard(PlayerBot bot)
        {
            Release(bot);

            if (_walker != null)
            {
                _walker.Arrived = null;
                _walker.RungFired = null;
                _walker = null;
            }
        }
    }
}
