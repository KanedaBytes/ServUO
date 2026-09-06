using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Who is standing where, and where is short.
    ///
    /// Two numbers, read in opposite directions, off ONE census:
    ///
    ///   FLOOR     (crowds)   fewer than this and the place pulls harder - a bank looks busy.
    ///   CAPACITY  (capacity) more than this and it stops pulling at all - a work site fills up.
    ///
    /// Session 7e added the second, and deliberately on the same code path rather than beside it:
    /// they are the same question about the same count, and two counts would eventually disagree.
    ///
    /// The standing-crowd floor is a design ADDITION, not a translation. Upstream kept its bank
    /// crowds with permanent spawner-held, lifecycle-exempt bots, and its lifecycle teleported
    /// extra sitters to a uniformly random bank with no occupancy check at all. This shard walks,
    /// so the floor is expressed as a PULL - an under-floor destination is simply somewhere bots
    /// want to go - and nobody is placed, teleported or commandeered.
    ///
    /// GAME THREAD ONLY.
    /// </summary>
    public static class BotCrowds
    {
        /// <summary>
        /// How many bots count toward a destination's floor: those standing there, plus those on
        /// their way.
        ///
        /// Counting travellers-in-transit is what stops a stampede. With only the standing count,
        /// one empty slot at a bank pulls every Traveller in town toward it and eleven of them
        /// arrive to find it filled - each having abandoned somewhere it would rather have been.
        /// </summary>
        public static int CountFor(string destinationId)
        {
            if (String.IsNullOrEmpty(destinationId))
            {
                return 0;
            }

            int count = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                var sitter = bot.Behavior as BankSitterBehavior;

                if (sitter != null && Insensitive.Equals(sitter.DestinationId, destinationId))
                {
                    count++;
                    continue;
                }

                var crafter = bot.Behavior as CrafterBehavior;

                if (crafter != null && Insensitive.Equals(crafter.DestinationId, destinationId))
                {
                    count++;
                    continue;
                }

                var gatherer = bot.Behavior as GathererBehavior;

                if (gatherer != null && Insensitive.Equals(gatherer.DestinationId, destinationId))
                {
                    count++;
                    continue;
                }

                var traveler = bot.Behavior as TravelerBehavior;

                if (traveler != null
                    && traveler.IsTravelling
                    && Insensitive.Equals(traveler.DestinationId, destinationId))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// How far below its floor a destination is, or zero when it is met.
        /// </summary>
        public static int Shortfall(NavDestination destination)
        {
            if (destination == null)
            {
                return 0;
            }

            int floor = BotLifecycle.Config_.FloorFor(destination.Type);

            if (floor <= 0)
            {
                return 0;
            }

            int shortfall = floor - CountFor(destination.Id);

            return shortfall > 0 ? shortfall : 0;
        }

        /// <summary>Every destination currently short of its floor, for the health line.</summary>
        public static List<string> BelowFloor(Map map)
        {
            var below = new List<string>();

            if (map == null || map == Map.Internal)
            {
                return below;
            }

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                int shortfall = Shortfall(destination);

                if (shortfall > 0)
                {
                    below.Add(String.Format("{0} -{1}", destination.Id, shortfall));
                }
            }

            below.Sort(StringComparer.Ordinal);

            return below;
        }
    }
}
