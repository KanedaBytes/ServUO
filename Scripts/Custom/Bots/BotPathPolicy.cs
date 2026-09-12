// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPathPolicy.cs - what the PATHFINDER is allowed to assume about a bot.
//
// THE OTHER HALF OF THE DOOR PROBLEM, and the reason there is an upstream edit behind it.
// PlayerBot.Move opens the closed door a step was just refused onto (PlayerBot.cs Move, through
// Core/DoorHelper.cs). That recovers a STEP. It cannot make the pathfinder willing to PLAN a
// route through a door, because FastAStarAlgorithm.cs:77 casts the pathing subject to
// BaseCreature and :93 sets MoveImpl.AlwaysIgnoreDoors only then - and :102 resets the flag
// after every GetSuccessors call, so nothing set from this side survives one loop iteration.
// A bot became a PlayerMobile, missed that cast, and terminal walk failures went from about 0.5
// to 5.23 per 100 walks, concentrated on interior shop arrivals.
//
// So FastAStarAlgorithm calls this, and this answers. See Scripts/Custom/MODIFICATIONS.md
// entry 6 for the edit, why no Custom/-side approach works, and how to reapply it.
//
// WHY THE ENGINE CALLS A CUSTOM STATIC RATHER THAN NAMING PlayerBot ITSELF. Upstream took the
// other road: their patch writes `m is Server.CustomBots.PlayerBot` into the engine file, at two
// sites (BitmapAStarAlgorithm.cs:161,169 in an installed tree). They can afford it; a tree with a
// MODIFICATIONS log cannot, because then every change to WHICH mobiles route through doors is
// another upstream edit. This is MODIFICATIONS entry 5's shape - BaseCreature.OnMoveOver calls
// BotShove.OnMoveOver and gets bool?, where null means "not mine, carry on" - and it keeps the
// vocabulary on this side of the seam where the collision session can move it.
//
// IBotActor RATHER THAN PlayerBot, for the same reason NavWalkFailures.MayBotPass asks that
// question: the policy is about the role, not the class. It also means this file states no
// opinion about what a bot IS, which is the one thing the class swap proved is worth avoiding.
//
// WHAT IT DELIBERATELY DOES NOT GRANT: IgnoreMovableImpassables. The BaseCreature branch sets it
// from bc.CanMoveOverObstacles and upstream does not grant it to bots either. It is a separate
// policy with its own failure mode and it is not what the doors regression was about.
//
// THE KNOWN LIMIT, inherited rather than invented. ServUO's AlwaysIgnoreDoors has no "unlocked
// only" notion, so a bot can plan through a LOCKED non-house door and then fail to open it at
// step time. That is exactly upstream's own documented limit on their slow path
// (INTEGRATION-NOTES.txt:245-250); their cache path closes it with a second flag and a per-cell
// guard, which here would mean a second upstream edit, in FastMovement.cs:39-52. House doors are
// already safe without it: FastMovementImpl.IsOk (FastMovement.cs:50-52) ends its door branch
// with a BaseHouseDoor.CheckAccess, so a bot never plans through a house door it may not open.
// The locked non-house case is COUNTED rather than guessed at - NavWalkFailures reports it as
// the `locked-door` cause - and the second edit is taken only if that number justifies it.

namespace Server.Custom
{
    /// <summary>
    /// Pathfinder policy for bots, asked by the engine's A* and by nothing else.
    /// </summary>
    public static class BotPathPolicy
    {
        /// <summary>
        /// May a route be planned through a closed door for this pathing subject?
        ///
        /// Returns null for anything that is not a bot, which is the caller's signal to fall
        /// through to its own rule - so a real player's routes are unchanged, and this is NOT a
        /// PlayerMobile-wide grant.
        ///
        /// True for a bot, unconditionally, because the step-time half is unconditional:
        /// PlayerBot.Move opens whatever closed door the next step is refused onto. The two
        /// halves have to agree or a route aims at a door the step will not open, which is the
        /// shape of failure this whole pair exists to remove.
        /// </summary>
        public static bool? IgnoreDoors(IPoint3D p)
        {
            return p is IBotActor ? (bool?)true : null;
        }
    }
}
