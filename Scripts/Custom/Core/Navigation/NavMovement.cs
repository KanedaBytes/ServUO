// NavMovement.cs — which movement implementation is actually installed, and what follows from it.
//
// WHY THIS EXISTS
// ---------------
// Two classes in this tree answer Mobile.Move's "may I step there", and they disagree about the
// one question the whole bot-crowding design rests on: does a mobile standing on a tile block a
// step onto it?
//
//   Scripts/Services/Pathing/Movement.cs      MovementImpl      - YES. checkMobs is true for an
//                                                                 uncontrolled BaseCreature on
//                                                                 every tile but MoveImpl.Goal
//                                                                 (:409), and CanMoveOver (:360)
//                                                                 lets past only the dead and
//                                                                 hidden staff.
//   Scripts/Services/Pathing/FastMovement.cs  FastMovementImpl  - NO. Its CheckMovement never
//                                                                 builds a mobile list at all.
//
// Both install themselves, and the LAST one wins: MovementImpl.Configure() (:65) runs in the
// Configure pass, FastMovementImpl.Initialize() (:23) runs in the Initialize pass afterwards and
// replaces it, keeping the first as a _Successor it only delegates to while Enabled is false.
// Nothing in this tree sets Enabled = false.
//
// That ordering is easy to read the wrong way round, and it HAS been read the wrong way round
// here: NavWalker.TryShiftWithinArrival and NavArrivals.Choose both argued from Movement.cs:345-411
// that "an occupied goal is an impossible goal ... BotShove is never consulted on an occupied tile
// at all". If FastMovementImpl is installed that reasoning describes code that is not running, and
// the real gate is Mobile.Move's OnMoveOver loop (Mobile.cs:3216) - which is where BotShove lives,
// and which is why BotShove fixed anything.
//
// So the answer is reported rather than assumed, once, in [CoreSmoke. It is one line, it costs
// nothing, and it is the line to read first when a walking bug does not match the comments.
//
// WHAT IT DOES NOT DO
// -------------------
// It does not spawn two mobiles on adjacent tiles and try to walk one through the other, which
// would be a direct measurement. NavAudit's occupancy pass already refuses to put a mobile in the
// world for the sake of a report, and this is a smaller question than that one. The type name is
// a fact; the consequence is derived from the two implementations above, and an implementation
// this file does not recognise is a Warn rather than a silent Ok.

using System;

using Server.Movement;

namespace Server.Custom
{
    public static class NavMovement
    {
        /// <summary>The installed IMovementImpl's type name, or "(none)".</summary>
        public static string ImplName
        {
            get
            {
                IMovementImpl impl = Movement.Movement.Impl;

                return impl == null ? "(none)" : impl.GetType().Name;
            }
        }

        /// <summary>
        /// Whether the installed implementation refuses a step onto a tile a live mobile is on.
        ///
        /// Null when the implementation is not one of the two this tree ships, which is the case
        /// worth saying out loud rather than guessing at.
        /// </summary>
        public static bool? BlocksOnMobiles
        {
            get
            {
                switch (ImplName)
                {
                    case "FastMovementImpl": return false;
                    case "MovementImpl": return true;
                    default: return null;
                }
            }
        }

        public static void Initialize()
        {
            HealthCheck.Register("Nav.Movement", BuildHealthResult);
        }

        /// <summary>
        /// A tile the engine must refuse, and the tile a mobile stands on to be refused from it.
        ///
        /// Trinsic's alchemist, the sandstone wall at 1843,2711 z 10 - Impassable, 20 tall, and a
        /// MAP STATIC rather than a world item, deliberately: a decoration can be moved, deleted or
        /// never placed, and a check that fails because somebody re-decorated is a check people
        /// learn to ignore. This one is in statics0.mul and can only change if the client data
        /// does. Its neighbour at 1842,2711 is the shop's wooden floor at the same Z.
        /// </summary>
        /// <remarks>
        /// PUBLIC because Nav.Actor asks the same tile the same question through the locomotion
        /// adapter, and a second "known wall" declared next to it would be a second definition of
        /// the one fact both checks rest on - which is exactly how the two would drift apart the
        /// day somebody re-decorated Trinsic.
        /// </remarks>
        public static readonly Point3D SolidTile = new Point3D(1843, 2711, 10);

        /// <remarks>See <see cref="SolidTile"/>.</remarks>
        public static readonly Point3D SolidFrom = new Point3D(1842, 2711, 10);

        /// <summary>
        /// Whether the engine still refuses a step onto a known-impassable static.
        ///
        /// WHY A HEALTH CHECK AND NOT A COMMENT. "A player can walk onto lamp posts" was reported
        /// against this shard, and answering it took a diff of the whole movement path against
        /// upstream, a read of tiledata.mul and a sweep of all 519 lamp posts in the world. The
        /// answer was no - but nothing in the tree could have said so, and every walkability number
        /// this shard publishes (`[NavAudit`'s 655 edges, the unstandable arrivals, the failure
        /// ledger) is worth what the engine's honesty is worth. So the engine is asked, on every
        /// `[CoreSmoke`, in one line.
        ///
        /// The two globals go with it. `MovementImpl.IgnoreMovableImpassables` and
        /// `AlwaysIgnoreDoors` are plain statics set without `try`/`finally` (BaseAI.cs:2358,
        /// FastAStarAlgorithm.cs:93-94, SlowAStarAlgorithm.cs:156-157, PlayerMobile.cs:2081) and
        /// cleared only on explicit return paths, so an exception thrown anywhere inside `Move`
        /// leaves one of them set for every mobile for the rest of the process - and this tree put
        /// its own code (BotShove, through BaseCreature.OnMoveOver) inside that window. A leak is
        /// invisible until something walks through a fence; here it is a red line on boot.
        /// </summary>
        public static bool RefusesSolidTile(out string detail)
        {
            Map map = Map.Trammel;

            var probe = new Server.Mobiles.Rat { Blessed = true, Frozen = true, Controlled = true };

            try
            {
                probe.MoveToWorld(SolidFrom, map);

                int newZ;

                bool allowed = Movement.Movement.CheckMovement(
                    probe,
                    map,
                    SolidFrom,
                    Utility.GetDirection(SolidFrom, SolidTile),
                    out newZ);

                detail = String.Format(
                    "a step from {0},{1} onto the sandstone wall at {2},{3} is {4}",
                    SolidFrom.X,
                    SolidFrom.Y,
                    SolidTile.X,
                    SolidTile.Y,
                    allowed ? "ALLOWED" : "refused");

                return !allowed;
            }
            catch (Exception ex)
            {
                detail = "the solid-tile probe threw: " + ex.Message;
                return false;
            }
            finally
            {
                probe.Delete();
            }
        }

        public static HealthResult BuildHealthResult()
        {
            string name = ImplName;
            bool? blocks = BlocksOnMobiles;

            string solid;
            bool refuses = RefusesSolidTile(out solid);

            bool leaked = MovementImpl.IgnoreMovableImpassables || MovementImpl.AlwaysIgnoreDoors;

            if (!refuses)
            {
                return HealthResult.Fail(String.Format(
                    "Movement.Impl is '{0}' and {1} - an impassable static is not blocking, so every "
                    + "walkability number this shard publishes is measured against a permissive "
                    + "engine{2}",
                    name,
                    solid,
                    leaked ? ". " + LeakDetail() : ""));
            }

            if (leaked)
            {
                return HealthResult.Warn(String.Format(
                    "Movement.Impl is '{0}' and {1}, but {2}",
                    name,
                    solid,
                    LeakDetail()));
            }

            if (blocks == null)
            {
                return HealthResult.Warn(String.Format(
                    "Movement.Impl is '{0}', which is neither MovementImpl nor FastMovementImpl - "
                    + "whether a standing mobile blocks a step is unknown, and NavWalker's arrival "
                    + "and shove reasoning both depend on the answer",
                    name));
            }

            return HealthResult.Ok(String.Format(
                "Movement.Impl is '{0}': a standing mobile {1} a step onto its tile, so an occupied "
                + "tile is {2}; {3}",
                name,
                blocks.Value ? "BLOCKS" : "does not block",
                blocks.Value
                    ? "refused before OnMoveOver and BotShove are ever consulted"
                    : "refused only by the occupant's own OnMoveOver, which is where BotShove sits",
                solid));
        }

        private static string LeakDetail()
        {
            return String.Format(
                "MovementImpl.IgnoreMovableImpassables={0} and AlwaysIgnoreDoors={1} on an idle "
                + "shard - both are set without try/finally and cleared only on explicit return "
                + "paths, so a set flag here is a leak out of a throwing Move, not a setting",
                MovementImpl.IgnoreMovableImpassables,
                MovementImpl.AlwaysIgnoreDoors);
        }
    }
}
