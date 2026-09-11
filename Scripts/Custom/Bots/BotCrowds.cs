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
    /// extra sitters to a uniformly random bank with no occupancy check at all. Ours is a PULL on
    /// top of that: an under-floor destination is simply somewhere bots want to go, and nobody is
    /// placed, teleported or commandeered to satisfy it.
    ///
    /// ON TOP OF THAT, AND THAT IS THE WHOLE POINT - this header used to say "this shard walks, so
    /// the floor is expressed as a pull", which read as though we had no garrison at all. We do.
    /// BotPopulation pins `FloorFor("bank")` BotRole.Fixed sitters at every bank, so the same number
    /// sizes the garrison AND sets the threshold the garrison is meant to satisfy.
    ///
    /// WHICH MEANS EVERY FIXTURE MUST BE COUNTED HERE, and for a long time none of them was. This
    /// method matches on DestinationId, and a seeded fixture had none - ApplySeed forwarded
    /// _seedStation to a Crafter and a Gatherer and to nothing else, and the bank slot passed null
    /// for it anyway - so twelve permanent sitters counted toward no destination's floor, every bank
    /// read under 3 for ever, and the 4x pull ran permanently on top of a garrison that already met
    /// the floor. Measured: exactly 3.00 standing at all four banks in 449 of 449 samples, total
    /// crowd 3.43 to 4.94, and this count seeing 1.05 to 2.88 of it.
    ///
    /// So: a new behaviour that can be a FIXTURE needs a branch here, and it needs its DestinationId
    /// set from the seed. Getting only one of the two is silent - the count simply reads low.
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
