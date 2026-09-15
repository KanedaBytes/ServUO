// NavGate.cs - a gate hop, taken the way a player takes a public moongate.
//
// THE RULE: if a player could not do it, a bot does not either. A player at a moongate walks onto
// the gate tile, PublicMoongate.OnMoveOver opens the MoongateGump, the player picks a destination,
// and MoongateGump.OnResponse re-checks and moves them onto that destination's PMEntry tile. A bot
// has no NetState, so SendGump returns false (Mobile.cs:7548-7563) and OnResponse can never run -
// stepping onto a real gate plays 0x20E and nothing else. So the gump's part is done here, and
// NOTHING ELSE IS INVENTED: the same preconditions OnResponse applies, the same tail it runs, the
// same landing tile.
//
// What that rules out, deliberately:
//   - a gate hop from anywhere but the gate. The walker must be standing ON the gate tile
//     (OnResponse itself allows one tile off; walking onto it is what a player does).
//   - a gate that is not in the world. The route is authored data; the PublicMoongate item is
//     what a player would actually use, so its absence fails the hop.
//   - a spread landing. uo-offline lands its bots up to two tiles off the exit gate
//     (MoongateTravel.cs:34, :301-314); a player cannot choose that, and lands on the entry tile.
//   - a creature. OnMoveOver serves m.Player only (PublicMoongate.cs:164), and Nav.TryRoute never
//     plans a gate for a non-player in the first place.
//
// What IS taken from uo-offline: the step-through beat. Their StepThroughDelay is 2 s
// (MoongateTravel.cs:36-37); a player spends about that reading the gump and clicking.
//
// GAME THREAD ONLY, like the walker that calls it.

using System;

using Server.Engines.CityLoyalty;
using Server.Factions;
using Server.Items;
using Server.Mobiles;
using Server.Spells;

namespace Server.Custom
{
    public static class NavGate
    {
        /// <summary>The gump-reading beat between standing on the gate and going through it.</summary>
        public static readonly TimeSpan StepThrough = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How close an authored gate waypoint must be to a PMList entry, and a mobile to the gate
        /// item, to count. One tile is OnResponse's own tolerance (PublicMoongate.cs:668-676).
        /// </summary>
        public const int Tolerance = 1;

        /// <summary>The live PublicMoongate within a tile of <paramref name="at"/>, or null.</summary>
        public static PublicMoongate MoongateAt(Map map, Point3D at)
        {
            if (map == null || PublicMoongate.Moongates == null)
            {
                return null;
            }

            foreach (PublicMoongate gate in PublicMoongate.Moongates)
            {
                if (gate == null || gate.Deleted || gate.Map != map)
                {
                    continue;
                }

                if (Utility.InRange(gate.GetWorldLocation(), at, Tolerance))
                {
                    return gate;
                }
            }

            return null;
        }

        /// <summary>
        /// The gump destination a gate waypoint names: the PMList entry on <paramref name="map"/>
        /// within a tile of <paramref name="at"/>. Null when the tile is not a moongate
        /// destination at all, which is an authoring fault.
        /// </summary>
        public static PMEntry EntryAt(Map map, Point3D at, out PMList list)
        {
            list = null;

            if (map == null)
            {
                return null;
            }

            foreach (PMList candidate in PMList.AllLists)
            {
                if (candidate == null || candidate.Map != map || candidate.Entries == null)
                {
                    continue;
                }

                foreach (PMEntry entry in candidate.Entries)
                {
                    if (entry != null && Utility.InRange(entry.Location, at, Tolerance))
                    {
                        list = candidate;
                        return entry;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Every refusal MoongateGump.OnResponse applies between a player choosing a destination
        /// and moving (PublicMoongate.cs:664-698), in its order, with its reasons. Staff pass, as
        /// they do there.
        /// </summary>
        public static bool CanUse(Mobile m, PublicMoongate gate, PMList list, PMEntry entry, out string reason)
        {
            reason = null;

            if (m == null || m.Deleted || !m.Alive || m.Map == null)
            {
                reason = "not alive in the world";
                return false;
            }

            if (!m.Player)
            {
                reason = "a public moongate serves players only";
                return false;
            }

            if (gate == null || gate.Deleted)
            {
                reason = "there is no public moongate here";
                return false;
            }

            if (m.Map == list.Map && m.InRange(entry.Location, 1))
            {
                reason = "already there";
                return false;
            }

            if (m.IsStaff())
            {
                return true;
            }

            if (!m.InRange(gate.GetWorldLocation(), Tolerance) || m.Map != gate.Map)
            {
                reason = "too far away to use the gate";
                return false;
            }

            if (SpellHelper.RestrictRedTravel && m.Murderer && list.Map != Map.Felucca && !Siege.SiegeShard)
            {
                reason = "a murderer may not travel there";
                return false;
            }

            if (Sigil.ExistsOn(m) && list.Map != Faction.Facet)
            {
                reason = "carrying a sigil";
                return false;
            }

            if (m.Criminal)
            {
                reason = "criminal";
                return false;
            }

            if (SpellHelper.CheckCombat(m))
            {
                reason = "in combat";
                return false;
            }

            if (m.Spell != null)
            {
                reason = "casting";
                return false;
            }

            if (m.Holding != null)
            {
                // CanUseGate's refusal rather than OnResponse's: the gump never opens for a player
                // dragging an item, so they never reach the choice.
                reason = "dragging an item";
                return false;
            }

            return true;
        }

        /// <summary>
        /// OnResponse's tail up to the move, verbatim (PublicMoongate.cs:702-706). The move itself
        /// is the caller's, because the walker's actor has a cached path to drop with it.
        /// </summary>
        public static void BeforeMove(Mobile m, PMList list, PMEntry entry)
        {
            BaseCreature.TeleportPets(m, entry.Location, list.Map);

            m.Combatant = null;
            m.Warmode = false;
            m.Hidden = true;
        }

        /// <summary>OnResponse's tail after the move (PublicMoongate.cs:710-712).</summary>
        public static void AfterMove(Mobile m, PMList list, PMEntry entry)
        {
            Effects.PlaySound(entry.Location, list.Map, 0x1FE);

            CityTradeSystem.OnPublicMoongateUsed(m);
        }
    }
}
