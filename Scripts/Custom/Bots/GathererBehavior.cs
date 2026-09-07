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

        /// <summary>
        /// How long the walk-in gets before the bot gives up on the site entirely.
        ///
        /// Config rather than a literal because it is a race against NavWalker's recovery ladder
        /// (see TickWalkIn), and the ladder's own timings are configurable.
        /// </summary>
        public static TimeSpan WalkInTimeout
        {
            get { return TimeSpan.FromSeconds(Config.Get("Custom.BotWalkInSeconds", 75.0)); }
        }

        /// <summary>
        /// How far to look for a harvestable tile when standing inside the site with none in reach.
        ///
        /// Six, because it has to be able to cross the widest thin edge a scattered arrival can
        /// land on - Custom.NavArrivalScatter is 2 and BotHarvest's own sweep is 5x5 - while
        /// staying small enough that the sweep is a hundred-odd tiles rather than a zone-sized
        /// one, on a path that runs every tick until it succeeds.
        /// </summary>
        private const int SeekRadius = 6;

        /// <summary>Upstream's carry limit, doubled when a beast is along.</summary>
        public const int MaxCarried = 60;

        /// <summary>Chance per swing of shuffling one tile along the face.</summary>
        private const double StepChance = 0.15;

        private static readonly string[] GatherChat = { "gather_talk", ChatLibrary.SmallTalk };

        private NavWalker _walker;
        private NavZone _site;
        private bool _clockedIn;
        private bool _walkingIn;
        private bool _seeking;
        private long _walkInDeadline;
        private long _nextSwing;
        private int _swings;
        private int _carriedSeen;
        private int _mined;

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

        /// <summary>
        /// How much this shift has actually dug out of the ground.
        ///
        /// Not the same question as "how much is it carrying", and the difference is the whole
        /// reason this field exists. EquipmentTable spawns every Miner with 3-15 IronOre as a
        /// working stash from its last shift, so a bot that has mined NOTHING still walks into
        /// town with a pack full of ore, hands it over, and looks from the outside exactly like
        /// one that worked. That is precisely how the first work probe passed against sites that
        /// had no rock on them.
        /// </summary>
        public int Mined
        {
            get { return _mined; }
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
            _carriedSeen = Carried(bot);

            // NO UNPAINTED-SITE FALLBACK. Upstream's gatherer worked wherever it landed when no
            // polygon was painted, and that is how a Miner ended up clocked in on the west bridge
            // at 1400,1748 reporting "0 swings, carrying 9" - the 9 being its spawn kit. A bot
            // with no zone has nowhere to work, and standing in a field pretending is worse than
            // walking away.
            if (_site == null)
            {
                Log.Warn(
                    "{0} has no work zone at or around '{1}'; it cannot clock in and is leaving.",
                    bot.Name,
                    DestinationId ?? "(nowhere)");

                bot.SetBehavior(BotBehaviors.Create("Traveler"), "no work zone");
                return;
            }

            if (CanClockIn(bot))
            {
                ClockIn(bot);
            }
            else
            {
                _walkInDeadline = Core.TickCount + (long)WalkInTimeout.TotalMilliseconds;
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
                BotLog.Note(bot, BotLogKind.Work, "downed tools: {0} is attacking", threat.Name);

                Release(bot);
                bot.HaulPending = Carried(bot) > 0;
                bot.SetBehavior(BotBehaviors.Create("Traveler"), "combat");
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

            if (!CanClockIn(bot))
            {
                if (_clockedIn)
                {
                    // Shoved out, or worked the last tile within reach out. Walk back in; the
                    // shift clock keeps running.
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

            NoticeYield(bot);

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

        /// <summary>
        /// Two conditions, and both are load-bearing: INSIDE the site's zone, and something within
        /// harvest range to actually swing at.
        ///
        /// The zone alone is not enough - a face has thin edges, and a bot standing on one clocks
        /// in and then swings at nothing for the length of its shift. The reach test is the same
        /// sweep BotHarvest.FindTarget performs, so passing it means the very next swing finds a
        /// target.
        /// </summary>
        private bool CanClockIn(PlayerBot bot)
        {
            if (_site == null || !Contains(_site, bot.Location))
            {
                return false;
            }

            HarvestDefinition definition = BotHarvest.DefinitionFor(bot.Class);

            return definition != null
                && BotWorkSites.ReachFrom(bot.Map, bot.Location, definition) > 0;
        }

        /// <summary>
        /// Count what the harvest timer actually delivered since the last tick.
        ///
        /// HarvestSystem.Give drops the ore into the pack asynchronously, a second or so after the
        /// swing, so the only honest way to know a swing produced anything is to watch the pack -
        /// the same way CrafterBehavior watches for finished goods.
        /// </summary>
        private void NoticeYield(PlayerBot bot)
        {
            int carried = Carried(bot);

            if (carried > _carriedSeen)
            {
                int gained = carried - _carriedSeen;

                _mined += gained;
                BotWorkSites.NoteMined(gained);

                // On the YIELD, not on the swing. A swing every four seconds would fill a
                // fifty-deep ring in three minutes and bury everything else in it; what somebody
                // reading this log wants to know is whether the ground gave anything up.
                BotLog.Note(bot, BotLogKind.Work, "+{0} at {1},{2} - {3} mined this shift, carrying {4}/{5}",
                    gained, bot.X, bot.Y, _mined, carried, Capacity(bot));
            }

            _carriedSeen = carried;
        }

        private void ClockIn(PlayerBot bot)
        {
            Release(bot);

            HarvestDefinition definition = BotHarvest.DefinitionFor(bot.Class);
            int reach = definition == null ? 0 : BotWorkSites.ReachFrom(bot.Map, bot.Location, definition);

            Log.Debug(
                "{0} clocked in at {1} - inside zone '{2}', {3} harvestable tile(s) in reach.",
                bot.Name,
                bot.Location,
                _site.Id,
                reach);

            BotLog.Note(bot, BotLogKind.Clock, "clocked in at {0},{1} in '{2}', reach {3}",
                bot.X, bot.Y, _site.Id, reach);

            _clockedIn = true;
            _seeking = false;
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
            // THE DEADLINE DOES NOT RUN WHILE THE WALKER IS WORKING.
            //
            // NavWalker aims the last step of a route at an EXACT tile - ArrivalRangeFor returns 0
            // for a NavStepKind.Arrival - and its recovery ladder is five rungs at HopTimeout
            // apiece, so a contended approach strip can legitimately take 80 to 100 seconds to
            // land. Against a flat 75-second budget the give-up fired first, and Release() then
            // called _walker.Stop(), which drops the route with no Arrived callback: the recovery
            // that was about to succeed was thrown away and the bot walked off.
            //
            // Pushing the deadline forward while the walker is active is not the same as removing
            // it. The walker cannot spin for ever - it has its own abandonment path, watched by
            // the branch below - so this bounds the time spent NOT walking, which is the thing
            // this timeout was actually meant to bound.
            if (_walkingIn && _walker != null && _walker.Active)
            {
                _walkInDeadline = Core.TickCount + (long)WalkInTimeout.TotalMilliseconds;
            }

            if (Core.TickCount - _walkInDeadline >= 0)
            {
                // Info, not Debug. This is a real failure - the bot was sent to work and could
                // not get to the face - and as a Debug line it was invisible on a shard not
                // running with -debug, which is how it went undiagnosed for a whole session.
                Log.Info(
                    "{0} could not get inside '{1}'; leaving rather than working outside it.",
                    bot.Name,
                    DestinationId);

                BotLog.Note(bot, BotLogKind.Clock, "gave up getting inside '{0}'", DestinationId);
                BotTickManager.NoteGaveUp();

                Release(bot);
                bot.SetBehavior(BotBehaviors.Create("Traveler"), "walk-in gave up");
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
                    BotLog.Note(bot, BotLogKind.Arrive, "walk-in abandoned; the walker stopped without arriving");
                    BotTickManager.NoteAbandoned();
                    Release(bot);
                }

                return;
            }

            // ALREADY INSIDE, JUST NOT ON ANYTHING. Routing again would be a no-op: the route ends
            // at an arrival point of a destination this bot is standing in, so the walker finishes
            // without taking a step and the bot asks again next tick until the deadline. What is
            // needed is not a route across town but a few steps sideways, and until now nothing
            // could take them - StepAlongTheFace is reachable only from Swing, i.e. only AFTER
            // clocking in, which is the thing that cannot happen.
            if (Contains(_site, bot.Location) && SeekReach(bot))
            {
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
                BotLog.Note(bot, BotLogKind.Route, "no route in to '{0}': {1}", DestinationId, error);
                BotTickManager.NoteNoRoute();
                return;
            }

            if (_walker == null)
            {
                _walker = new NavWalker(bot);
                _walker.Arrived = OnWalkedIn;
                LogWalker(bot, _walker);
            }

            bot.Commuting = true;
            _walkingIn = true;

            BotLog.Note(bot, BotLogKind.Route, "walking in to '{0}', {1} hop(s) from {2},{3}",
                DestinationId, route.Count, bot.X, bot.Y);

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

            // THE SAME QUESTION Tick ASKS, and it has to be, because Tick is what happens next.
            //
            // This used to clock in on containment alone while CanClockIn demands containment AND
            // something in reach. So a bot that landed on a thin tile clocked in - spawning its
            // pack beast, which is what made the bug look like a success - and then un-clocked on
            // the very next tick, walked in again to the same tile, and repeated that until the
            // deadline. Asking the weaker question here bought nothing and cost a llama.
            if (CanClockIn(bot))
            {
                ClockIn(bot);
                return;
            }

            BotLog.Note(bot, BotLogKind.Clock,
                "arrived at {0},{1} but nothing is in reach; looking for rock", bot.X, bot.Y);
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

            BotLog.Note(bot, BotLogKind.Clock, "shift over - {0} swing(s), {1} mined, carrying {2}",
                _swings, _mined, carried);

            bot.SetBehavior(BotBehaviors.Create("Traveler"), "shift over");
        }

        /// <summary>
        /// Standing inside the site with nothing in reach: step toward the nearest tile that has
        /// something, and report whether a step was taken.
        ///
        /// This is the piece the walk-in never had. A bot lands where NavWalker put it - an
        /// arrival point scattered by Custom.NavArrivalScatter, or wherever the recovery ladder
        /// left it - and a face has thin edges, so landing on a tile with zero reach is ordinary
        /// rather than exceptional. The old code answered that by asking for the route again,
        /// which cannot help: the destination is where it already is.
        ///
        /// Bounded by the site rectangle and by SeekRadius, and every candidate must pass the same
        /// three tests a shuffle does - inside the zone, standable, and still able to route home -
        /// so this cannot walk a bot somewhere StepAlongTheFace would refuse to.
        /// </summary>
        private bool SeekReach(PlayerBot bot)
        {
            HarvestDefinition definition = BotHarvest.DefinitionFor(bot.Class);

            if (definition == null || _site == null)
            {
                return false;
            }

            Point3D from = bot.Location;
            Point3D target = Point3D.Zero;
            int best = 0;
            bool found = false;

            for (int dx = -SeekRadius; dx <= SeekRadius; dx++)
            {
                for (int dy = -SeekRadius; dy <= SeekRadius; dy++)
                {
                    int x = from.X + dx;
                    int y = from.Y + dy;

                    if (!_site.Contains(x, y))
                    {
                        continue;
                    }

                    int z = bot.Map.GetAverageZ(x, y);

                    if (!bot.Map.CanSpawnMobile(x, y, z))
                    {
                        continue;
                    }

                    var candidate = new Point3D(x, y, z);

                    if (BotWorkSites.ReachFrom(bot.Map, candidate, definition) <= 0)
                    {
                        continue;
                    }

                    if (!CanGetHomeFrom(bot, candidate))
                    {
                        continue;
                    }

                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                    if (!found || distance < best)
                    {
                        target = candidate;
                        best = distance;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            // One tile per tick, through Move, so it walks rather than teleports and the AI's own
            // move gate still applies. The next tick re-runs the sweep from where it ended up,
            // which is also how it copes with something standing in the way.
            Direction toward = bot.GetDirectionTo(target);

            bot.Direction = toward;

            if (!bot.Move(toward))
            {
                return false;
            }

            if (!_seeking)
            {
                _seeking = true;
                BotLog.Note(bot, BotLogKind.Clock, "seeking rock {0} tile(s) away at {1},{2}",
                    best, target.X, target.Y);
            }

            return true;
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
        /// in?
        ///
        /// Returning null now MEANS SOMETHING: there is no zone, so there is no work, and
        /// OnAttached walks the bot away rather than letting it mime a shift in a field.
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
                _walker.RungFired = null;
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
