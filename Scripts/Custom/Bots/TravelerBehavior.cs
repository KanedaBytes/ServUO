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

        private TravelState _state;
        private NavWalker _walker;
        private string _destinationId;
        private string _destinationName;
        private string _lastDestinationId;
        private long _lingerUntil;

        public override string SerializableName
        {
            get { return "Traveler"; }
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

        /// <summary>The walker steering this bot, or null. The smoke reads its rung counts.</summary>
        public NavWalker Walker
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
                        _state = TravelState.Choosing;
                    }

                    return;
                }

                case TravelState.Lingering:
                {
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

                return false;
            }

            if (_walker == null)
            {
                _walker = new NavWalker(bot);
                _walker.Arrived = OnArrived;
            }

            _destinationId = destination.Id;
            _destinationName = destination.Name;

            // Set before Follow, cleared before Stop - the DailyLifeTownsfolk order. This flag is
            // the entire stand-aside mechanism: BotAI.DoActionWander reads it and declines to
            // issue a step, so the wander and the walker do not fight over the move gate.
            bot.Commuting = true;

            _state = TravelState.Walking;

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

            _lastDestinationId = _destinationId;
            _state = TravelState.Lingering;

            int seconds = Utility.RandomMinMax((int)MinLinger.TotalSeconds, (int)MaxLinger.TotalSeconds);

            _lingerUntil = Core.TickCount + (seconds * 1000L);
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
        /// Let the walker go entirely. Only on detach, where the behaviour itself is going away
        /// and there is nothing left to reuse it for.
        /// </summary>
        private void Discard(PlayerBot bot)
        {
            Release(bot);

            if (_walker != null)
            {
                _walker.Arrived = null;
                _walker = null;
            }
        }
    }
}
