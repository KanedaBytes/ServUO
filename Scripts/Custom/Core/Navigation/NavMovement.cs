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

        public static HealthResult BuildHealthResult()
        {
            string name = ImplName;
            bool? blocks = BlocksOnMobiles;

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
                + "tile is {2}",
                name,
                blocks.Value ? "BLOCKS" : "does not block",
                blocks.Value
                    ? "refused before OnMoveOver and BotShove are ever consulted"
                    : "refused only by the occupant's own OnMoveOver, which is where BotShove sits"));
        }
    }
}
