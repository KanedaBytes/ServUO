using System;
using System.Collections.Generic;

using Server.Items;

namespace Server.Custom
{
    /// <summary>
    /// Picks where a mobile actually stands when it gets somewhere.
    ///
    /// STOCHASTIC, NOT ALLOCATED. There is no claim/release registry: a random arrival point
    /// plus a small scatter is stateless, survives a restart, cannot leak a claim, and is what
    /// both reference shards converged on. ServUO's CanSpawnMobile already refuses an occupied
    /// tile, so the failure mode of two mobiles choosing the same spot is that the second one
    /// stands beside it.
    ///
    /// The exception is an EXCLUSIVE arrival point - a guard post, where the point is that the
    /// guard stands precisely there. Those are taken exactly, with no scatter, and are skipped
    /// while anyone is within a tile of them. That check is a live world query, not a registry,
    /// so nothing has to be released.
    /// </summary>
    public static class NavArrivals
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>Attempts before a scattered spot falls back to the exact tile.</summary>
        private const int ScatterAttempts = 6;

        public static bool TryPick(NavDestination destination, Mobile forMobile, out Point3D spot)
        {
            int range;

            return TryPick(destination, forMobile, out spot, out range);
        }

        /// <summary>
        /// As above, and also how close to the spot counts as arrived - the chosen arrival's
        /// Range, or 0 when the spot is the destination's centre because nothing was eligible.
        /// </summary>
        public static bool TryPick(NavDestination destination, Mobile forMobile, out Point3D spot, out int range)
        {
            spot = Point3D.Zero;
            range = 0;

            if (destination == null)
            {
                return false;
            }

            Map map = forMobile != null && forMobile.Map != null && forMobile.Map != Map.Internal
                ? forMobile.Map
                : destination.Map;

            if (map == null || map == Map.Internal)
            {
                return false;
            }

            List<NavArrival> candidates = Eligible(destination, map, forMobile);

            if (candidates.Count > 0)
            {
                NavArrival chosen = Choose(candidates, map, forMobile);

                range = chosen.Range;

                // Two different reasons to take the tile exactly, and they are independent.
                // `exclusive` reserves it (a guard post: one guard, precisely there). `exact` only
                // suppresses the scatter (a forge bench: any number of smiths may queue for it,
                // but two tiles off is out of range of the anvil and useless).
                if (chosen.Exclusive || chosen.Exact)
                {
                    spot = chosen.Location;
                    return true;
                }

                spot = Scatter(chosen.Location, map);
                return true;
            }

            // Everything eligible is taken, or the destination has no arrival points at all.
            // Standing still would be worse than standing roughly right, so scatter around the
            // centre - and say so, because it means the data wants another arrival point.
            Log.Debug(
                "No free arrival point at '{0}'; scattering around its centre.",
                destination.Id);

            spot = Scatter(destination.Location, map);
            return true;
        }

        /// <summary>
        /// A free arrival by preference, at random among the free ones, and at random among all of
        /// them only when every one is taken.
        ///
        /// IT USED TO BE A FLAT RANDOM PICK, and occupancy was consulted only for `exclusive`
        /// arrivals - so a bank with four arrival points routinely sent a bot to the one another
        /// bot was already standing on, and the recovery ladder climbed for eighty seconds before
        /// the bot was teleported onto the tile it could not walk to.
        ///
        /// THE REASON GIVEN HERE WAS WRONG, and is corrected rather than deleted because the
        /// conclusion survives it. It said Movement.CheckMovement refuses an uncontrolled
        /// BaseCreature any tile holding a live mobile (Movement.cs:345-356, exemption at :411
        /// reading MoveImpl.Goal, set only inside the A* search). All true of MovementImpl, which
        /// is not what is installed: FastMovementImpl replaces it in the Initialize pass and never
        /// checks mobiles at all (FastMovement.cs:23-26, :361-484). See NavMovement.cs. The step is
        /// refused by the OCCUPANT's OnMoveOver instead (Mobile.cs:3216) - so a bot-occupied
        /// arrival is merely crowded, and a vendor-occupied one is the impossible walk.
        ///
        /// Preference rather than refusal: with every arrival taken, aiming at a taken one and
        /// letting the stand-tile sweep find a neighbour is still better than standing still.
        /// </summary>
        private static NavArrival Choose(List<NavArrival> candidates, Map map, Mobile forMobile)
        {
            var free = new List<NavArrival>();

            foreach (NavArrival arrival in candidates)
            {
                if (!IsOccupied(arrival.Location, map, forMobile))
                {
                    free.Add(arrival);
                }
            }

            List<NavArrival> pool = free.Count > 0 ? free : candidates;

            return pool[Utility.Random(pool.Count)];
        }

        private static List<NavArrival> Eligible(NavDestination destination, Map map, Mobile forMobile)
        {
            var result = new List<NavArrival>();

            if (destination.ArrivalList == null)
            {
                return result;
            }

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                if (arrival.Exclusive && IsOccupied(arrival.Location, map, forMobile))
                {
                    continue;
                }

                result.Add(arrival);
            }

            return result;
        }

        /// <summary>True if anyone other than the asker is standing within a tile.</summary>
        private static bool IsOccupied(Point3D point, Map map, Mobile forMobile)
        {
            IPooledEnumerable<Mobile> eable = map.GetMobilesInRange(point, 1);

            try
            {
                foreach (Mobile mobile in eable)
                {
                    if (mobile != forMobile && !mobile.Deleted)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            return false;
        }

        /// <summary>
        /// A random offset within the configured radius, validated with CanSpawnMobile so the
        /// mobile is never sent into a wall. Falls back to the exact tile, which is what the
        /// author asked for anyway.
        ///
        /// THE Z IS RESOLVED THE WALKER'S WAY, not with GetAverageZ, and that is the whole of a
        /// measured fault. GetAverageZ only sees land, so scattering around an arrival on a raised
        /// shop floor asked about the street underneath it: `brit-shop-tinker` authors 1421,1651 at
        /// z 11 and every scattered spot came back at z 10, on the ground outside. CanSpawnMobile
        /// then passed - a street tile IS standable - and the bot was sent to a tile three away
        /// with a wall between. Four of one 30-minute window's seventeen terminal failures were
        /// that one arrival, and three more were the same shape at the carpenter and the inn.
        ///
        /// NavWalker.TryResolveZ searches a window around the HINT first and the land second, which
        /// is what keeps a scatter on the floor its anchor is on. The hint is the anchor's own Z
        /// rather than the candidate's, because the candidate has none - that is the question being
        /// asked.
        ///
        /// It still validates standing only, never reachability: a scattered spot can be standable
        /// and unreachable, and answering that costs a MovementPath per candidate. NavWalker's
        /// arrival retarget is where reachability is settled, once, from close enough for the
        /// pathfinder's answer to be worth having.
        ///
        /// AND IT REFUSES A DOORWAY, because CanSpawnMobile's answer about one is only true until
        /// the door swings. Measured: a bot was sent to 1845,2710 at the Trinsic alchemist and
        /// arrived to find "nothing can stand on it". Probing that tile six times over a minute
        /// showed it flipping between a wooden door with CanFit false and an empty tile with
        /// CanFit true - because BaseDoor.Open MOVES the item by its Offset (BaseDoor.cs:88-94),
        /// so a door occupies one tile shut and its neighbour open. The scatter had validated the
        /// half-second the doorway was clear. Nothing is wrong with the validation; the tile is
        /// simply not a place to stand, in either state.
        /// </summary>
        private static Point3D Scatter(Point3D around, Map map)
        {
            int radius = NavigationSystem.ArrivalScatter;

            if (radius > 0)
            {
                HashSet<int> doorTiles = DoorTiles(map, around, radius);

                for (int i = 0; i < ScatterAttempts; i++)
                {
                    int x = around.X + Utility.RandomMinMax(-radius, radius);
                    int y = around.Y + Utility.RandomMinMax(-radius, radius);

                    if (doorTiles.Contains(Key(x, y)))
                    {
                        continue;
                    }

                    int z;

                    if (!NavWalker.TryResolveZ(map, new Point3D(x, y, around.Z), out z))
                    {
                        continue;
                    }

                    if (map.CanSpawnMobile(x, y, z))
                    {
                        return new Point3D(x, y, z);
                    }
                }
            }

            return around;
        }

        /// <summary>
        /// Every tile a door near this anchor can stand on - BOTH its states.
        ///
        /// Both, because either one is a tile whose standability depends on the door rather than on
        /// the map: shut, the door is impassable where it is; open, it is impassable one tile over.
        /// A scatter that lands on either gets an answer with a lifetime measured in seconds.
        ///
        /// One range query per Scatter call, not per candidate. The radius is the scatter's plus
        /// one, because an open door reaches a tile beyond the closed one it belongs to.
        /// </summary>
        private static HashSet<int> DoorTiles(Map map, Point3D around, int radius)
        {
            var tiles = new HashSet<int>();

            IPooledEnumerable eable = map.GetItemsInRange(around, radius + 1);

            try
            {
                foreach (Item item in eable)
                {
                    var door = item as BaseDoor;

                    if (door == null || door.Deleted)
                    {
                        continue;
                    }

                    // Where it is now, and where the other state would put it. Offset is signed
                    // from the CLOSED position, so the arithmetic runs one way or the other
                    // depending on which state we are looking at.
                    Point3D here = door.Location;
                    Point3D other = door.Open
                        ? new Point3D(here.X - door.Offset.X, here.Y - door.Offset.Y, here.Z)
                        : new Point3D(here.X + door.Offset.X, here.Y + door.Offset.Y, here.Z);

                    tiles.Add(Key(here.X, here.Y));
                    tiles.Add(Key(other.X, other.Y));
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                eable.Free();
            }

            return tiles;
        }

        /// <summary>A tile as one int, so the door set is a HashSet rather than a string join.</summary>
        private static int Key(int x, int y)
        {
            return (x << 16) ^ (y & 0xFFFF);
        }
    }
}
