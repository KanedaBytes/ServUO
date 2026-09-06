// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BankSitterBehavior.cs — the standing crowd.
//
// A bank with people at it is the single clearest sign a shard is inhabited,
// and it costs almost nothing: the bots are already walking there.
//
// HOW IT STANDS STILL, which is the whole trick. It does not steer itself. It
// sets Home to a spot near where it arrived and RangeHome to a couple of tiles,
// and the stock wander does the rest - BaseAI.WalkRandomInHome drifts inside two
// thirds of the range and steps back beyond it (BaseAI.cs:2548-2570). That is
// "standing about, shuffling occasionally" for no movement code at all.
// DailyLifePatron does the same thing in the tavern (TavernSystem.cs:102-103).
//
// Upstream had to steer by hand - a WalkBackIfShoved stepping one tile per tick -
// because their bot was a PlayerMobile with no BaseAI to delegate to. Ours is a
// BaseCreature, so the engine does it. Their PickScatteredHome we DO keep, and
// the reason is at OnAttached: it is load-bearing here in a way it was not there.
//
// NOT PORTED: upstream's six roles (Regular, Hawker, Afk, and three macroers).
// Hawker is pure speech and belongs with the chat corpus; the macro roles need
// real spellcasting, Hidden toggling and reagent bookkeeping, which is a lot of
// machinery for posture. What is left is the non-speech, non-magic crowd.

using System;

namespace Server.Custom
{
    public class BankSitterBehavior : PlayerBotBehavior
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How far a sitter may drift from its spot.
        ///
        /// Small but NOT zero. Zero pins it to one tile and the crowd reads as statues that slide
        /// back into place when nudged; two lets them shuffle like people waiting.
        /// </summary>
        public const int SitterRange = 2;

        /// <summary>How far a sitter looks for somebody to face.</summary>
        private const int FaceRange = 6;

        /// <summary>How far off the arrival point a sitter settles. Bank floors are large.</summary>
        private const int ScatterRadius = 4;

        private const int ScatterAttempts = 12;

        /// <summary>Chance per tick of doing something idle - a turn, or a look in the bank box.</summary>
        private const double IdleChance = 0.05;

        private string _destinationId;
        private bool _pinned;

        public override string SerializableName
        {
            get { return "BankSitter"; }
        }

        /// <summary>Which bank this sitter is standing at. Read by the crowd census.</summary>
        public string DestinationId
        {
            get { return _destinationId; }
            set { _destinationId = value; }
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            return _destinationId == null ? "at the bank" : "at " + _destinationId;
        }

        public override void OnAttached(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            // Stand NEAR where it arrived, not exactly on it.
            //
            // This is upstream's PickScatteredHome and the reason for it is worth keeping with it:
            // "without this every bot homes on the exact tile it arrived at and the crowd stacks."
            // On this shard it is worse than cosmetic. An uncontrolled BaseCreature cannot walk
            // through another mobile (Movement.cs:411), so sitters parked on the arrival points
            // make those tiles unreachable - and the arrival points are exactly where the next
            // bot is aiming. Dropping this turned a bank with a crowd floor into a traffic jam
            // that showed up as a flood of Sidestep and Door recoveries.
            bot.Home = PickScatteredHome(bot);
            bot.RangeHome = SitterRange;
            _pinned = true;
        }

        public override void OnDetached(PlayerBot bot)
        {
            Release(bot);
        }

        /// <summary>
        /// A standable tile a short way off the arrival point, or the arrival point itself when
        /// nothing better is free.
        ///
        /// checkMobiles is TRUE, unlike upstream's: picking a tile somebody is already standing on
        /// is how a crowd stacks in the first place, and the cost of asking is one sector query
        /// per bot per bank visit.
        /// </summary>
        private static Point3D PickScatteredHome(PlayerBot bot)
        {
            Point3D arrival = bot.Location;
            Map map = bot.Map;

            if (map == null || map == Map.Internal)
            {
                return arrival;
            }

            for (int attempt = 0; attempt < ScatterAttempts; attempt++)
            {
                int dx = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);
                int dy = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);

                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int x = arrival.X + dx;
                int y = arrival.Y + dy;
                int z = NavWalker.ResolveZ(map, new Point3D(x, y, arrival.Z));

                if (map.CanFit(x, y, z, 16, false, false, true) && !Occupied(map, x, y, z))
                {
                    return new Point3D(x, y, z);
                }
            }

            // Nothing free nearby. Standing on the arrival point is worse than standing beside it,
            // but it is better than not standing anywhere.
            return arrival;
        }

        private static bool Occupied(Map map, int x, int y, int z)
        {
            IPooledEnumerable<Mobile> mobiles = map.GetMobilesInRange(new Point3D(x, y, z), 0);

            try
            {
                foreach (Mobile mobile in mobiles)
                {
                    if (mobile != null && !mobile.Deleted && mobile.X == x && mobile.Y == y
                        && mobile.Alive && Math.Abs(mobile.Z - z) < 16)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                mobiles.Free();
            }

            return false;
        }

        /// <summary>
        /// Give the leash back.
        ///
        /// A behaviour that sets Home MUST clear it. PlayerBot's constructor sets Home to zero and
        /// everything downstream assumes that: WalkRandomInHome special-cases a zero Home into a
        /// free wander (BaseAI.cs:2516), so a leftover Home would quietly leash a later Traveler
        /// back to this bank every time it stopped commuting.
        /// </summary>
        private void Release(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !_pinned)
            {
                return;
            }

            bot.Home = Point3D.Zero;
            bot.RangeHome = 0;
            _pinned = false;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // The visit ends on its own clock. This has already swapped the brain, so nothing
            // below may touch the bot.
            if (CheckVisitExpired(bot))
            {
                return;
            }

            // SPEECH GOES HERE, in session 7d with the chat corpus. Upstream's bank crowd is
            // mostly talk - WTS spam, small talk, the "afk" line - and the posture below was
            // written to punctuate it. Deliberately a no-op rather than a stub method, so there
            // is nothing to leave accidentally wired up.

            if (Utility.RandomDouble() >= IdleChance)
            {
                return;
            }

            if (Utility.RandomDouble() < 0.35)
            {
                // The bend over the bank box. Every bank crowd did this all day.
                bot.Animate(32, 5, 1, true, false, 0);
            }
            else
            {
                FaceNearestPerson(bot);
            }
        }

        /// <summary>
        /// Turn toward the nearest visible person.
        ///
        /// The smallest thing in the whole file and the one that matters most: a crowd that all
        /// faces the same way reads as scenery, and a crowd that looks at each other reads as
        /// people.
        /// </summary>
        private static void FaceNearestPerson(PlayerBot bot)
        {
            Mobile nearest = null;
            int bestDistance = Int32.MaxValue;

            IPooledEnumerable<Mobile> mobiles = bot.Map.GetMobilesInRange(bot.Location, FaceRange);

            try
            {
                foreach (Mobile mobile in mobiles)
                {
                    if (mobile == bot || mobile.Deleted || !mobile.Alive || mobile.Hidden)
                    {
                        continue;
                    }

                    // Players and bots both - a sitter should look at a real person too. Bots
                    // qualify because PlayerBot sets Mobile.Player for the party gate.
                    if (!mobile.Player)
                    {
                        continue;
                    }

                    int dx = Math.Abs(mobile.X - bot.X);
                    int dy = Math.Abs(mobile.Y - bot.Y);
                    int distance = dx > dy ? dx : dy;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = mobile;
                    }
                }
            }
            finally
            {
                // ServUO's pooled enumerable does not free itself, unlike ModernUO's.
                mobiles.Free();
            }

            if (nearest == null)
            {
                return;
            }

            Direction direction = bot.GetDirectionTo(nearest);

            if (bot.Direction != direction)
            {
                bot.Direction = direction;
            }
        }
    }
}
