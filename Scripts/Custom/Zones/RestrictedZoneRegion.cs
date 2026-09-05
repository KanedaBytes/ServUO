using System;

using Server.Mobiles;
using Server.Regions;

namespace Server.Custom
{
    /// <summary>
    /// A restricted zone, layered on top of whatever region already covers the ground.
    ///
    /// The zone is purely additive: it does not block movement, casting, or anything else. It
    /// warns, counts down, and hands the player to the jail service.
    /// </summary>
    public class RestrictedZoneRegion : BaseRegion
    {
        public RestrictedZoneRecord Record { get; private set; }

        public RestrictedZoneRegion(RestrictedZoneRecord record)
            : base(null, record.Map, FindParent(record), record.Bounds)
        {
            Record = record;
        }

        /// <summary>
        /// Resolves the parent at the centre of the zone.
        ///
        /// Nearly every Region hook delegates to Parent by default, so adopting the region that
        /// already covers this ground makes the zone layer on top of the town/dungeon rules
        /// rather than replacing them.
        /// </summary>
        private static Region FindParent(RestrictedZoneRecord record)
        {
            Rectangle2D bounds = record.Bounds;
            Map map = record.Map;

            int x = bounds.X + bounds.Width / 2;
            int y = bounds.Y + bounds.Height / 2;

            return Find(new Point3D(x, y, map.GetAverageZ(x, y)), map);
        }

        /// <summary>
        /// Suppresses BaseRegion.OnEnter's YoungDungeonWarning gump.
        ///
        /// ServUO's BaseRegion.OnEnter is NOT empty (unlike ModernUO's base): it pops a dungeon
        /// warning at Young players. A restricted zone is not a dungeon, and that gump would
        /// fight the countdown gump for the same screen.
        /// </summary>
        public override bool YoungProtected { get { return true; } }

        public override void OnEnter(Mobile m)
        {
            base.OnEnter(m);

            var pm = m as PlayerMobile;

            if (pm != null)
            {
                RestrictedZoneSystem.OnEnterZone(pm, this);
            }
        }

        public override void OnExit(Mobile m)
        {
            base.OnExit(m);

            // Also reached while a mobile is being deleted, with items mid-strip, so this must
            // not assume the mobile is in a usable state.
            var pm = m as PlayerMobile;

            if (pm == null)
            {
                return;
            }

            // Capture before cancelling: only someone who was actually being counted down got
            // out in time. The message lives here rather than in CancelCountdown because that
            // also runs on disconnect, on re-entry and after a jail - none of which "left".
            bool escaped = !pm.Deleted && RestrictedZoneSystem.HasCountdown(pm);

            RestrictedZoneSystem.CancelCountdown(pm);

            if (escaped)
            {
                pm.SendMessage(0x40, String.Format("You have left {0}.", Record.Name));
            }
        }

        /// <summary>
        /// Death fires no region change at all, so ghosts keep whatever countdown state they had
        /// (and OnEnter ignores them, since ShouldWarn requires Alive). Resurrecting inside the
        /// zone therefore has to count as a fresh entry - otherwise dying at 29 seconds and
        /// resurrecting at a shrine inside the zone is a way to loiter indefinitely.
        ///
        /// Unlike OnEnter/OnExit, ServUO's Region.OnResurrect chains to the parent, so the base
        /// result must be respected or the parent region's resurrect rules are silently lost.
        /// </summary>
        public override bool OnResurrect(Mobile m)
        {
            bool allowed = base.OnResurrect(m);

            if (allowed)
            {
                var pm = m as PlayerMobile;

                if (pm != null)
                {
                    RestrictedZoneSystem.OnEnterZone(pm, this);
                }
            }

            return allowed;
        }
    }
}
