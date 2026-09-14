// NavHopClassCheck.cs - the probe class the verify token walks has to be the fleet's, and the two
// classes have to be telling different stories.
//
// WHAT THIS GUARDS, AND WHY A GUARD IS NEEDED AT ALL
//
// `nav-hop` used to walk a CorridorProbe, which is a BaseCreature, and a BaseCreature is not what
// walks an arrival. FastAStarAlgorithm grants the creature branch IgnoreMovableImpassables from
// bc.CanMoveOverObstacles (:97-101); the IBotActor branch beside it withholds that on purpose
// (:102-107, and BotPathPolicy's header). So the creature walks through the crates, barrels and
// furniture a bot has to go round - and it said OK about hops the fleet cannot walk, systematically
// INSIDE BUILDINGS, which is exactly where arrival points live.
//
// The token now defaults to the bot class. The failure mode that would put it back is quiet: if
// somebody re-wires ProbeHops to a creature, or the two branches stop differing, nothing breaks and
// nothing logs - the answers just get more permissive again. So this asserts the difference itself.
//
// THE FIXTURE IS RAW GEOMETRY, NOT A RECORD. `uo-brit-west-rd-1-s2 -> brit-shop-provisioner-south`
// is where the gap was measured - 297 expansions and success for a creature, 301 and failure for a
// bot, same tiles and the same stock budget. That arrival has since been relocated, so the check
// pins the TILES rather than the records: 1424,1739,20 to 1414,1748,10 is still the same shop, the
// same door and the same crates, and still parts the two classes. A check that named the record
// would have disarmed itself the moment the record moved.
//
// It asks FastAStarAlgorithm directly rather than through MovementPath, for the reason NavHopProbe
// gives: MovementPath fails any goal within one tile and honours whatever OverrideAlgorithm is
// installed, and neither belongs in an assertion about branch behaviour.

using System;
using System.Collections.Generic;

using Server.PathAlgorithms.FastAStar;

namespace Server.Custom
{
    public static class NavHopClassCheck
    {
        /// <summary>The hop the two classes disagree about. Tiles, not records - see the header.</summary>
        private static readonly Point3D Start = new Point3D(1424, 1739, 20);

        private static readonly Point3D Goal = new Point3D(1414, 1748, 10);

        private static HealthResult _last;

        public static void Initialize()
        {
            HealthCheck.Register("Nav.HopClass", BuildHealthResult);

            // Once per boot, after the world has settled, on Nav.Doors' pattern. The probes are
            // real mobiles and the map has to be there to plan across.
            EventSink.ServerStarted += () => Timer.DelayCall(TimeSpan.FromSeconds(22.0), () => Run());
        }

        public static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                return HealthResult.Ok("not run this boot (runs at startup)");
            }

            return _last;
        }

        /// <summary>Can this class plan the fixture hop, under stock rules at the stock budget?</summary>
        private static bool Plans(NavWalkAudit.ProbeClass cls)
        {
            NavWalkAudit.IWalkAuditProbe probe = null;

            try
            {
                probe = cls.Create();

                Mobile mobile = probe.Mobile;

                // The subject has to STAND at the start tile: it is the IPoint3D the algorithm
                // casts, so where it is decides both the BaseCreature branch and BotPathPolicy's
                // answer. This is NavHopProbe's rule and the reason it is written down there.
                mobile.MoveToWorld(Start, Map.Trammel);

                if (!FastAStarAlgorithm.Instance.CheckCondition(mobile, Map.Trammel, Start, Goal))
                {
                    return false;
                }

                Direction[] path = FastAStarAlgorithm.Instance.Find(mobile, Map.Trammel, Start, Goal);

                return path != null && path.Length > 0;
            }
            finally
            {
                if (probe != null && probe.Mobile != null && !probe.Mobile.Deleted)
                {
                    probe.Mobile.Delete();
                }
            }
        }

        public static HealthResult Run()
        {
            try
            {
                var seen = new Dictionary<string, bool>();

                foreach (NavWalkAudit.ProbeClass cls in NavWalkAudit.ProbeClasses)
                {
                    seen[cls.Key] = Plans(cls);
                }

                if (!seen.ContainsKey("bot") || !seen.ContainsKey("creature"))
                {
                    _last = HealthResult.Warn(
                        "both classes are needed to compare and the registry has "
                        + NavWalkAudit.ProbeKeyList());

                    return _last;
                }

                string where = String.Format(
                    "{0},{1},{2} -> {3},{4},{5}",
                    Start.X, Start.Y, Start.Z, Goal.X, Goal.Y, Goal.Z);

                if (seen["creature"] && !seen["bot"])
                {
                    _last = HealthResult.Ok(String.Format(
                        "the classes are distinguished: on {0} a creature plans a route and a bot "
                        + "does not, which is the gap that made nav-hop answer about the wrong "
                        + "walker. nav-hop now defaults to bot.", where));

                    return _last;
                }

                // Either the branches stopped differing, or the geometry did. Both are worth a
                // person's attention: the first would silently restore the permissive answers,
                // and the second means this fixture has stopped testing anything.
                _last = HealthResult.Fail(String.Format(
                    "the two probe classes no longer part on {0} - creature {1}, bot {2}. Either "
                    + "FastAStarAlgorithm's IBotActor branch stopped differing from the "
                    + "BaseCreature one, or the tiles changed and this fixture now proves nothing.",
                    where,
                    seen["creature"] ? "routes" : "does not route",
                    seen["bot"] ? "routes" : "does not route"));

                return _last;
            }
            catch (Exception e)
            {
                _last = HealthResult.Fail("the hop-class check threw " + e.GetType().Name + ": " + e.Message);

                return _last;
            }
        }
    }
}
