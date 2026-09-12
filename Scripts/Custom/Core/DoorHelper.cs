// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// DoorHelper.cs - open the closed door in the way, the way a player's client does.
//
// ONE SCAN, TWO CALLERS, and that is the whole reason this file exists rather than the method
// staying where it was. NavWalker's Door rung has asked this question since the walker was
// written; PlayerBot.Move now asks it too, because upstream opens doors inside Move itself
// (PlayerBot.cs:611-620) and a PlayerMobile has no other place to do it. Two copies of a scan
// with this many rules in it would drift, and the half that drifted would be the one nobody was
// looking at.
//
// It lives in Core rather than in Core/Navigation because both callers need it and Navigation is
// not allowed to know that bots exist - and it lives in Core rather than in Bots because
// NavWalker cannot depend on Bots. It takes a Mobile and knows nothing about either.
//
// Upstream has exactly this file, in the same shape, called from both places for the same reason:
// PlayerBot.Move (PlayerBot.cs:619) and the Traveler's step loop (TravelerBehavior.cs:3352).
//
// THE TWO RULES ITS COMMENTS WERE WRITTEN TO RECORD, carried over verbatim from the walker:
//
//   ONLY CLOSED DOORS ARE TOUCHED, because Use() toggles and calling it on an open door slams it
//   shut on whoever is walking through - and a step can fail for reasons that have nothing to do
//   with the door, another mobile in the doorway being the common one.
//
//   IT GOES THROUGH Use() RATHER THAN SETTING Open, because Use() carries the rules with it: a
//   locked door stays shut, and a house door runs its own access check, so this cannot walk an
//   NPC into someone's locked home.
//
// And the reason both methods report door.Open rather than that they tried: Use() is a no-op on a
// door this mobile may not open, so "I called Use" and "the door is open" are different facts and
// the caller wants the second one.

using System;

using Server.Items;

using CalcMoves = Server.Movement.Movement;

namespace Server.Custom
{
    public static class DoorHelper
    {
        /// <summary>
        /// The same vertical window the client's open-door macro uses, so a door on the floor
        /// above is not reachable from down here.
        /// </summary>
        private static bool WithinReach(Mobile from, BaseDoor door)
        {
            return door.Z + door.ItemData.Height > from.Z && from.Z + 16 > door.Z;
        }

        private static bool TryUse(Mobile from, BaseDoor door)
        {
            if (door == null || door.Open || !WithinReach(from, door))
            {
                return false;
            }

            if (!from.CanSee(door) || !from.InLOS(door))
            {
                return false;
            }

            door.Use(from);

            return door.Open;
        }

        /// <summary>
        /// Any closed door within a tile - the walker's Door rung.
        ///
        /// Deliberately wider than <see cref="TryOpenAhead"/>: by the time the ladder reaches this
        /// rung the walker has already lost track of which direction it was refused in, and a door
        /// diagonally adjacent is as likely to be the obstruction as the one straight ahead.
        /// </summary>
        public static bool TryOpenAdjacent(Mobile from)
        {
            if (from == null || from.Deleted)
            {
                return false;
            }

            Map map = from.Map;

            if (map == null || map == Map.Internal || !from.CheckAlive())
            {
                return false;
            }

            // Doors within a tile of the mobile, which includes the tile it is trying to reach.
            // Anything further away is not what is blocking this step.
            IPooledEnumerable<Item> items = map.GetItemsInRange(from.Location, 1);

            try
            {
                foreach (Item item in items)
                {
                    if (TryUse(from, item as BaseDoor))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                items.Free();
            }

            return false;
        }

        /// <summary>
        /// The closed door on the tile a step was just refused onto - PlayerBot.Move.
        ///
        /// Narrower than <see cref="TryOpenAdjacent"/> on purpose, and the narrowness is the
        /// point: Move knows exactly which tile it was refused onto, so opening a door on any
        /// OTHER tile would be a door opened for no reason - visible to players, and on a house
        /// door an access check run on somebody's porch every time a bot brushed past it.
        ///
        /// Upstream's note on the ordering is worth keeping: Mobile.Move only returns false for a
        /// genuinely blocked STEP - a turn always succeeds - so by the time this is called the
        /// mobile is already facing d, and d is the tile to look at.
        /// </summary>
        public static bool TryOpenAhead(Mobile from, Direction d)
        {
            if (from == null || from.Deleted)
            {
                return false;
            }

            Map map = from.Map;

            if (map == null || map == Map.Internal || !from.CheckAlive())
            {
                return false;
            }

            int x = from.X;
            int y = from.Y;

            CalcMoves.Offset(d, ref x, ref y);

            // Range 0 is the single tile, which is what GetItemsInRange(loc, 0) means - the same
            // call the walker makes at range 1, asked of one tile instead of nine.
            IPooledEnumerable<Item> items = map.GetItemsInRange(new Point3D(x, y, from.Z), 0);

            try
            {
                foreach (Item item in items)
                {
                    if (TryUse(from, item as BaseDoor))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                items.Free();
            }

            return false;
        }
    }
}
