// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// GathererBehavior.cs — a Miner or Lumberjack working a wilderness face.
//
// Attached by the Traveler handoff when a gatherer arrives at its site. The bot
// works the face for a shift, fills its pack (and its beast's panniers), and
// when the shift ends shoulders the load and walks back to town, where the
// Traveler's delivery hook hands it to a crafter of the matching trade.
//
// UPSTREAM'S SHAPE, KEPT WHOLE:
//
//   - the site is an AREA, not a tile, and a bot that clocks in outside it walks
//     itself in first and swings nothing on the way. "No ore comes out of the
//     roadside." If it cannot get inside within 75 seconds it gives up and
//     travels on rather than pretending.
//   - clock-in dismounts and calls up the pack beast.
//   - the shift is the visit window, 4 to 8 minutes. A full pack does NOT end
//     it; only the clock does.
//   - it shuffles along the face as it works.
//   - HaulPending, then Traveler. The behaviour itself hands nothing over.
//
// WHAT IS DIFFERENT: the yield is real. Upstream's AddYield was
// `new IronOre(2..6)` on a 35-second timer with no reference to the ground it
// was standing on — which is why their sites could be arbitrary road waypoints.
// Here BotHarvest drives Mining.System / Lumberjacking.System against real
// tiles, so the ore comes out of an actual rock face, the resource bank actually
// depletes, and a bot with no skill actually fails.
//
// TWO TRANSLATIONS OF THE SAME IDEA:
//
//   PaintedZone  ->  NavZone. Ours are rectangles rather than polygons, which is
//                    what the schema has; NavZone.Contains via Nav.ZonesAt does
//                    the same job PaintedZone.Contains did.
//   PathFollower ->  NavWalker. Their walk-in ran a bare PathFollower on its own
//                    400ms timer; ours goes through the shard's one walker, so
//                    the stuck-recovery ladder covers it like every other walk.

using System;
using System.Collections.Generic;

using Server.Engines.Harvest;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public class GathererBehavior : PlayerBotBehavior
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>Upstream's swing cadence. Each swing is one real harvest attempt.</summary>
        private static readonly TimeSpan SwingInterval = TimeSpan.FromSeconds(4.0);

        /// <summary>How long the walk-in gets before the bot gives up on the site entirely.</summary>
        private static readonly TimeSpan WalkInTimeout = TimeSpan.FromSeconds(75.0);

        /// <summary>Upstream's carry limit, doubled when a beast is along.</summary>
        public const int MaxCarried = 60;

        /// <summary>Chance per swing of shuffling one tile along the face.</summary>
        private const double StepChance = 0.15;

        private static readonly string[] GatherChat = { "gather_talk", ChatLibrary.SmallTalk };

        private NavWalker _walker;
        private NavZone _site;
        private bool _clockedIn;
        private bool _walkingIn;
        private long _walkInDeadline;
        private long _nextSwing;
        private int _swings;

        /// <summary>Which site it was sent to, as a nav destination id. Set by the arrival handoff.</summary>
        public string DestinationId { get; set; }

        public bool IsWorking
        {
            get { return _clockedIn; }
        }

        public int Swings
        {
            get { return _swings; }
        }

        public GathererBehavior()
        {
            ChatCategories = GatherChat;
            ChatChance = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(45.0);
            MaxChatCooldown = TimeSpan.FromSeconds(120.0);
        }

        public override string SerializableName
        {
            get { return "Gatherer"; }
        }

        public override string CurrentDestinationId
        {
            get { return DestinationId; }
        }

        /// <summary>Do not swap a bot out mid walk-in: it is steering a walker.</summary>
        public override bool CanTransition(PlayerBot bot)
        {
            return !_walkingIn;
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);

            _site = ResolveSite(bot);
            _clockedIn = _site == null || Contains(_site, bot.Location);

            if (!_clockedIn)
            {
                _walkInDeadline = Core.TickCount + (long)WalkInTimeout.TotalMilliseconds;
            }
            else
            {
                ClockIn(bot);
            }

            // A gatherer attached directly - by [BotBehavior, or by the probe - stamps its own
            // shift. One reached by an arrival handoff already has the window the handoff gave it.
            if (VisitExpiresAt == null)
            {
                VisitExpiresAt = CustomTime.Now + TimeSpan.FromMinutes(Utility.RandomMinMax(4, 8));
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            base.OnDetached(bot);

            Discard(bot);
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            NavDestination destination = DestinationId == null ? null : Nav.Destination(DestinationId);
            string where = destination == null ? "a work site" : destination.Name;

            if (_walkingIn)
            {
                return String.Format("walking in to {0}", where);
            }

            if (!_clockedIn)
            {
                return String.Format("standing outside {0}", where);
            }

            return String.Format(
                "working {0} - {1} swing(s), carrying {2}/{3}",
                where,
                _swings,
                Carried(bot),
                Capacity(bot));
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal || !bot.Alive)
            {
                Release(bot);
                return;
            }

            // SEAM, restored by the combat session: upstream swapped to a defender Adventurer
            // here - "the tool is a real axe: swap to a defender and fight (the classic UO
            // lumberjack)". There is no Adventurer behaviour on this shard yet, and a bot that
            // kept swinging at a rock while something chewed on it would be worse than one that
            // walks away. So it downs tools and travels, which is at least a decision.
            var threat = bot.Combatant as Mobile;

            if (threat != null && threat.Alive && !threat.Deleted)
            {
                Release(bot);
                bot.HaulPending = Carried(bot) > 0;
                bot.Behavior = BotBehaviors.Create("Traveler");
                return;
            }

            // The shift is the visit window, and only the clock ends it. A full pack does not:
            // upstream's gatherer keeps swinging at a face it cannot carry any more of, which is
            // both what a person does and what keeps it standing somewhere legible.
            if (VisitExpiresAt != null && CustomTime.Now >= VisitExpiresAt.Value)
            {
                EndShift(bot);
                return;
            }

            if (_site != null && !Contains(_site, bot.Location))
            {
                if (_clockedIn)
                {
                    // Shoved out, or wandered out. Walk back in; the shift clock keeps running.
                    _clockedIn = false;
                    _walkInDeadline = Core.TickCount + (long)WalkInTimeout.TotalMilliseconds;
                }

                TickWalkIn(bot);
                return;
            }

            if (!_clockedIn)
            {
                ClockIn(bot);
            }

            TrySpeak(bot);

            if (Core.TickCount - _nextSwing < 0)
            {
                return;
            }

            _nextSwing = Core.TickCount + (long)SwingInterval.TotalMilliseconds + Utility.Random(1500);

            if (Utility.RandomDouble() < StepChance)
            {
                StepAlongTheFace(bot);
            }

            Swing(bot);
        }

        /// <summary>
        /// One harvest attempt, if there is room for what it might produce.
        ///
        /// The capacity check is NOT optional and is the one thing that has no upstream
        /// equivalent. Neither Mining nor Lumberjacking sets PlaceAtFeetIfFull, so
        /// HarvestSystem.Give falls through to SendPackFullTo and item.Delete()
        /// (HarvestSystem.cs:207-210): a bot that keeps swinging with a full pack destroys every
        /// ore it digs, silently, for the rest of the shift.
        /// </summary>
        private void Swing(PlayerBot bot)
        {
            if (Carried(bot) >= Capacity(bot))
            {
                return;
            }

            if (BotHarvest.TrySwing(bot))
            {
                _swings++;
                return;
            }

            // Nothing in reach. Shuffle rather than stand: the face is not uniform, and a bot
            // that mined out the bank under its feet has to move to find the next one.
            StepAlongTheFace(bot);
        }

        private void ClockIn(PlayerBot bot)
        {
            Release(bot);

            _clockedIn = true;
            _nextSwing = Core.TickCount;

            // Mining refuses a mounted digger outright (Mining.cs:501), and a mounted bot plays no
            // swing animation either. Nothing mounts a bot yet; this is the guard for when
            // something does.
            if (bot.Mounted)
            {
                var mount = bot.Mount;

                if (mount != null)
                {
                    mount.Rider = null;
                }
            }

            BotPackAnimals.SpawnFor(bot);
        }

        /// <summary>
        /// Walk into the site through the shard's own walker.
        ///
        /// Upstream ran a bare PathFollower on a private 400ms timer here. Going through NavWalker
        /// instead costs nothing and buys the whole stuck-recovery ladder — which matters more on
        /// a rock face than anywhere else in town, because the approach strip is one tile wide and
        /// two bots arriving at once will contend for it.
        /// </summary>
        private void TickWalkIn(PlayerBot bot)
        {
            if (Core.TickCount - _walkInDeadline >= 0)
            {
                Log.Debug(
                    "{0} could not get inside '{1}'; leaving rather than working outside it.",
                    bot.Name,
                    DestinationId);

                Release(bot);
                bot.Behavior = BotBehaviors.Create("Traveler");
                return;
            }

            if (_walkingIn)
            {
                // The same watchdog every walking behaviour here carries: NavWalker calls Stop()
                // rather than Finish() when a mobile is deleted or lands off-facet, so a
                // behaviour that waited only on the callback would sit here for ever with
                // Commuting still true.
                if (_walker == null || !_walker.Active)
                {
                    BotTickManager.NoteAbandoned();
                    Release(bot);
                }

                return;
            }

            if (!BotTickManager.TryTakePlan())
            {
                return;
            }

            if (String.IsNullOrEmpty(DestinationId))
            {
                BotTickManager.NoteNoDestination();
                return;
            }

            NavRoute route;
            string error;

            if (!Nav.TryRouteFrom(bot.Location, bot.Map, DestinationId, bot, out route, out error))
            {
                BotTickManager.NoteNoRoute();
                return;
            }

            if (_walker == null)
            {
                _walker = new NavWalker(bot);
                _walker.Arrived = OnWalkedIn;
            }

            bot.Commuting = true;
            _walkingIn = true;
            _walker.Follow(route);
        }

        private void OnWalkedIn(NavWalker walker)
        {
            var bot = walker.Mobile as PlayerBot;

            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.Commuting = false;
            _walkingIn = false;

            if (_site == null || Contains(_site, bot.Location))
            {
                ClockIn(bot);
            }
        }

        /// <summary>
        /// Shoulder the load and head for town.
        ///
        /// The behaviour hands nothing over itself — that is the Traveler's delivery hook, which
        /// fires when the bot arrives somewhere that will take the load. All this does is set the
        /// flag that makes the destination roll prefer those places.
        /// </summary>
        private void EndShift(PlayerBot bot)
        {
            Release(bot);

            int carried = Carried(bot);

            bot.HaulPending = carried > 0;

            if (carried > 0)
            {
                string line = ChatLibrary.PickRandom("gather_haul");

                if (!String.IsNullOrEmpty(line) && IsPlayerNearby(bot))
                {
                    bot.Say(line);
                }
            }
            else
            {
                // Nothing to show for it. Send the beast home rather than parading an empty llama
                // back through town.
                BotPackAnimals.Release(bot);
            }

            bot.Behavior = BotBehaviors.Create("Traveler");
        }

        /// <summary>
        /// One step along the rock face, staying inside the site.
        ///
        /// Upstream's, including the fallback for an unpainted site. It is what keeps a bot off
        /// an exhausted resource bank: mining banks are 8x8 and hold 10 to 34 ore, so a bot that
        /// never moved would mine one out in a couple of minutes and then swing at nothing for
        /// the rest of the shift.
        /// </summary>
        private void StepAlongTheFace(PlayerBot bot)
        {
            Direction direction = (Direction)Utility.Random(8);

            if (_site == null)
            {
                // No zone authored for this site - upstream's unpainted case, where a bot simply
                // works where it landed. The reachability guard still applies: without a zone
                // there is nothing else keeping it in range of the graph, so this branch needs it
                // more than the painted one, not less.
                Point3D free = Step(bot.Location, direction);

                if (CanGetHomeFrom(bot, free))
                {
                    bot.Direction = direction;
                    bot.Move(direction);
                }

                return;
            }

            Point3D ahead = Step(bot.Location, direction);

            if (Contains(_site, ahead) && CanGetHomeFrom(bot, ahead))
            {
                bot.Direction = direction;
                bot.Move(direction);
                return;
            }

            // That step would leave the site. Drift back toward its middle instead.
            var centre = new Point3D(
                _site.X + (_site.Width / 2),
                _site.Y + (_site.Height / 2),
                bot.Z);

            Direction inward = bot.GetDirectionTo(centre);

            bot.Direction = inward;

            if (Contains(_site, Step(bot.Location, inward)))
            {
                bot.Move(inward);
            }
        }

        /// <summary>
        /// Could a bot standing here still find its way back?
        ///
        /// Nav.TryRouteFrom refuses to route from anywhere with no waypoint inside the hop cap
        /// (Nav.cs:213), so a tile beyond that is a one-way trip: the bot works its shift happily,
        /// then asks for a way home every tick and is told there is none, for ever. The scout
        /// clamps a site's zone so this cannot arise from the data, and this is the guard that
        /// makes the behaviour correct even when the data is wrong - which is worth having,
        /// because the failure is silent, permanent, and looks exactly like a broken walker.
        ///
        /// Paid only on the 15% shuffle roll, over a graph of about a hundred waypoints.
        /// </summary>
        private static bool CanGetHomeFrom(PlayerBot bot, Point3D point)
        {
            return Nav.NearestWaypoint(point, bot.Map, NavigationSystem.HopMaxTiles) != null;
        }

        private static Point3D Step(Point3D from, Direction direction)
        {
            int x = from.X;
            int y = from.Y;

            switch (direction & Direction.Mask)
            {
                case Direction.North: y--; break;
                case Direction.Right: x++; y--; break;
                case Direction.East: x++; break;
                case Direction.Down: x++; y++; break;
                case Direction.South: y++; break;
                case Direction.Left: x--; y++; break;
                case Direction.West: x--; break;
                case Direction.Up: x--; y--; break;
            }

            return new Point3D(x, y, from.Z);
        }

        /// <summary>
        /// Which zone rectangle is this bot's work face.
        ///
        /// Upstream resolved by destination name and fell back to "the nearest gather area within
        /// 40 tiles" for a behaviour restored from a save with no site name. Ours cannot be
        /// restored from a save — a bot is deleted on load — so the fallback here is only for a
        /// gatherer attached by hand, and it asks the same question: what work zone am I standing
        /// in? A site with no zone authored is not an error; the bot simply works where it lands,
        /// which is upstream's unpainted-site behaviour.
        /// </summary>
        private NavZone ResolveSite(PlayerBot bot)
        {
            string tag = bot.Class == BotClass.Miner ? "mine" : "lumber";

            foreach (NavZone zone in Nav.ZonesAt(bot.Location, bot.Map))
            {
                if (zone.HasTag(tag))
                {
                    return zone;
                }
            }

            NavDestination destination = DestinationId == null ? null : Nav.Destination(DestinationId);

            if (destination == null)
            {
                return null;
            }

            foreach (NavZone zone in Nav.ZonesAt(destination.Location, destination.Map))
            {
                if (zone.HasTag(tag))
                {
                    return zone;
                }
            }

            return null;
        }

        private static bool Contains(NavZone zone, Point3D point)
        {
            return zone != null && zone.Contains(point.X, point.Y);
        }

        /// <summary>How much of its yield the bot is carrying, pack and panniers together.</summary>
        public static int Carried(PlayerBot bot)
        {
            Type yield = BotHarvest.YieldFor(bot.Class);

            if (yield == null)
            {
                return 0;
            }

            int carried = bot.Backpack == null ? 0 : bot.Backpack.GetAmount(yield, false);
            Container panniers = BotPackAnimals.PanniersOf(bot);

            if (panniers != null)
            {
                carried += panniers.GetAmount(yield, false);
            }

            return carried;
        }

        /// <summary>Upstream's limit: sixty on your back, one hundred and twenty with a beast.</summary>
        public static int Capacity(PlayerBot bot)
        {
            return BotPackAnimals.PanniersOf(bot) != null ? MaxCarried * 2 : MaxCarried;
        }

        private void Release(PlayerBot bot)
        {
            _walkingIn = false;

            if (bot != null)
            {
                bot.Commuting = false;
            }

            if (_walker != null)
            {
                _walker.Stop();
            }
        }

        private void Discard(PlayerBot bot)
        {
            Release(bot);

            if (_walker != null)
            {
                _walker.Arrived = null;
                _walker = null;
            }
        }

        /// <summary>Every gatherer currently out or hauling, for Bots.Work.</summary>
        public static List<GathererBehavior> Live()
        {
            var gatherers = new List<GathererBehavior>();

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                var gatherer = bot.Behavior as GathererBehavior;

                if (gatherer != null)
                {
                    gatherers.Add(gatherer);
                }
            }

            return gatherers;
        }
    }
}
