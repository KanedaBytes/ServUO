using System;
using System.Collections.Generic;
using System.Text;

using Server.Commands;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// Writes Data/Live/entities.json on a timer so the editor can draw what is actually moving.
    ///
    /// Off by default: there is no reason to write a file every two seconds when nobody is
    /// watching, and the editor turns it on by dropping a request token.
    /// </summary>
    public static class LiveMapSnapshot
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        public const string OutputPath = "Data/Live/entities.json";

        /// <summary>
        /// Route points sent per entity. The editor draws where an NPC is heading; it does not
        /// need the whole lap, and a patrol lap can be dozens of points on every snapshot.
        /// </summary>
        private const int MaxRoutePoints = 24;

        private static Timer _timer;
        private static bool _all;
        private static string _zoneId;
        private static long _sequence;
        private static DateTime? _lastWriteUtc;
        private static string _lastError;

        public static bool Running
        {
            get { return _timer != null; }
        }

        public static void Initialize()
        {
            CommandSystem.Register("LiveMap", AccessLevel.Administrator, LiveMap_OnCommand);

            HealthCheck.Register("Bridge.LiveMap", BuildHealthResult);
        }

        [Usage("LiveMap on [seconds] [custom|all] [zoneId] | LiveMap off")]
        [Description("Writes a live entity snapshot for the shard editor.")]
        private static void LiveMap_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length == 0)
            {
                from.SendMessage(0x35, "Usage: [LiveMap on [seconds] [custom|all] [zoneId] | [LiveMap off");
                return;
            }

            if (Insensitive.Equals(e.GetString(0), "off"))
            {
                Stop();
                from.SendMessage("Live map stopped.");
                return;
            }

            if (!Insensitive.Equals(e.GetString(0), "on"))
            {
                from.SendMessage(0x35, "Usage: [LiveMap on [seconds] [custom|all] [zoneId] | [LiveMap off");
                return;
            }

            double seconds = 2.0;
            bool all = false;
            string zoneId = null;

            for (int i = 1; i < e.Length; i++)
            {
                string argument = e.GetString(i);

                double parsed;

                if (Double.TryParse(argument, out parsed))
                {
                    seconds = parsed;
                }
                else if (Insensitive.Equals(argument, "all"))
                {
                    all = true;
                }
                else if (Insensitive.Equals(argument, "custom"))
                {
                    all = false;
                }
                else
                {
                    zoneId = argument;
                }
            }

            if (seconds < 0.5 || seconds > 60.0)
            {
                from.SendMessage(0x35, "The interval must be between 0.5 and 60 seconds.");
                return;
            }

            // Twenty thousand mobiles every two seconds is not something to do by accident, so
            // the unbounded form has to be asked for explicitly and bounded to somewhere.
            if (all && zoneId == null)
            {
                from.SendMessage(0x35,
                    "'all' needs a nav zone to bound it - there are too many mobiles to write them all.");
                from.SendMessage(0x35, "Try: [LiveMap on 2 all brit-town");
                return;
            }

            if (zoneId != null && Nav.Zone(zoneId) == null)
            {
                from.SendMessage(0x35, String.Format("There is no nav zone '{0}'.", zoneId));
                return;
            }

            Start(seconds, all, zoneId);

            from.SendMessage(String.Format(
                "Live map writing {0} every {1}s{2}.",
                all ? "everything in '" + zoneId + "'" : "custom actors and players",
                seconds,
                zoneId != null && !all ? " within '" + zoneId + "'" : ""));

            CommandLogging.WriteLine(
                from,
                String.Format("{0} {1} starting the live map", from.AccessLevel, CommandLogging.Format(from)));
        }

        public static void Start(double seconds, bool all, string zoneId)
        {
            Stop();

            _all = all;
            _zoneId = zoneId;

            var interval = TimeSpan.FromSeconds(seconds);

            _timer = Timer.DelayCall(TimeSpan.Zero, interval, Write);

            Log.Info("Live map started: {0}, every {1}s.", all ? "all in " + zoneId : "custom", seconds);
        }

        public static void Stop()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;

                Log.Info("Live map stopped.");
            }
        }

        private static void Write()
        {
            try
            {
                _sequence++;

                string json = Build();

                string error;

                if (!AtomicFile.Write(OutputPath, json, out error))
                {
                    _lastError = error;
                    Log.Error("Live map snapshot not written: {0}", error);
                    return;
                }

                _lastError = null;
                _lastWriteUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "Live map snapshot failed.");
            }
        }

        private static string Build()
        {
            var builder = new StringBuilder(4096);

            builder.Append("{\n");

            // A sequence number and a timestamp so the editor can tell a stale file from a
            // stopped shard. Without them a frozen map looks exactly like a quiet one.
            builder.Append("  \"sequence\": ").Append(_sequence).Append(",\n");
            builder.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            builder.Append("  \"scope\": \"").Append(_all ? "all" : "custom").Append("\",\n");
            builder.Append("  \"zone\": ").Append(_zoneId == null ? "null" : Json.Quote(_zoneId)).Append(",\n");
            builder.Append("  \"entities\": [\n");

            List<Mobile> mobiles = Collect();

            for (int i = 0; i < mobiles.Count; i++)
            {
                AppendEntity(builder, mobiles[i]);

                builder.Append(i < mobiles.Count - 1 ? ",\n" : "\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }

        private static List<Mobile> Collect()
        {
            var result = new List<Mobile>();

            NavZone zone = _zoneId != null ? Nav.Zone(_zoneId) : null;

            // Players always come from NetState.Instances - the online client list, not a world
            // scan - which is both the house rule and the only list that is actually correct.
            foreach (NetState ns in NetState.Instances)
            {
                Mobile player = ns.Mobile;

                if (player != null && !player.Deleted && InScope(player, zone))
                {
                    result.Add(player);
                }
            }

            if (!_all)
            {
                foreach (Mobile mobile in LiveRegistry.Snapshot())
                {
                    if (InScope(mobile, zone))
                    {
                        result.Add(mobile);
                    }
                }

                return result;
            }

            // 'all' is bounded by a zone, so this is a sector query over that rectangle rather
            // than a walk of World.Mobiles.
            if (zone == null || zone.Map == null)
            {
                return result;
            }

            IPooledEnumerable<Mobile> eable = zone.Map.GetMobilesInBounds(zone.Bounds);

            try
            {
                foreach (Mobile mobile in eable)
                {
                    if (mobile != null && !mobile.Deleted && !mobile.Player)
                    {
                        result.Add(mobile);
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            return result;
        }

        private static bool InScope(Mobile mobile, NavZone zone)
        {
            if (mobile.Map == null || mobile.Map == Map.Internal)
            {
                return false;
            }

            if (zone == null)
            {
                return true;
            }

            return mobile.Map == zone.Map && zone.Contains(mobile.X, mobile.Y);
        }

        private static void AppendEntity(StringBuilder builder, Mobile mobile)
        {
            builder.Append("    {");
            builder.Append("\"serial\":").Append(mobile.Serial.Value);
            builder.Append(",\"type\":").Append(Json.Quote(mobile.GetType().Name));
            builder.Append(",\"kind\":").Append(Json.Quote(KindOf(mobile)));
            builder.Append(",\"name\":").Append(Json.Quote(mobile.Name ?? ""));
            builder.Append(",\"map\":").Append(Json.Quote(mobile.Map.Name));
            builder.Append(",\"x\":").Append(mobile.X);
            builder.Append(",\"y\":").Append(mobile.Y);
            builder.Append(",\"z\":").Append(mobile.Z);

            AppendRoute(builder, mobile);

            builder.Append("}");
        }

        /// <summary>
        /// Where this mobile is heading, when a NavWalker is steering it.
        ///
        /// This is the thing the editor cannot get any other way: a stationary NPC and one
        /// halfway through a walk home look identical from position alone.
        /// </summary>
        private static void AppendRoute(StringBuilder builder, Mobile mobile)
        {
            NavWalker walker = NavWalker.For(mobile);

            if (walker == null || walker.Route == null)
            {
                return;
            }

            NavRoute route = walker.Route;
            int index = walker.StepIndex;

            if (index >= route.Count)
            {
                return;
            }

            builder.Append(",\"edge\":").Append(Json.Quote(route.Steps[index].WaypointId ?? ""));
            builder.Append(",\"route\":[");

            int emitted = 0;

            for (int i = index; i < route.Count && emitted < MaxRoutePoints; i++, emitted++)
            {
                if (emitted > 0)
                {
                    builder.Append(",");
                }

                Point3D point = route.Steps[i].Point;

                builder.Append(point.X).Append(",").Append(point.Y);
            }

            builder.Append("]");
        }

        private static string KindOf(Mobile mobile)
        {
            // Before the Player check, deliberately. A PlayerBot sets Mobile.Player so that party
            // invites work (AddPartyTarget.cs:30 refuses a human-bodied non-player), which would
            // otherwise make every bot draw on the live map as a real logged-in account - the one
            // distinction the map most needs to keep.
            if (mobile is PlayerBot)
            {
                return "bot";
            }

            if (mobile.Player)
            {
                return mobile.AccessLevel > AccessLevel.Player ? "staff" : "player";
            }

            if (mobile is IDailyLifeActor)
            {
                return mobile is BaseVendor ? "vendor" : "actor";
            }

            if (mobile is BaseVendor)
            {
                return "vendor";
            }

            return "creature";
        }

        private static HealthResult BuildHealthResult()
        {
            if (_lastError != null)
            {
                return HealthResult.Fail("last snapshot failed: " + _lastError);
            }

            if (!Running)
            {
                return HealthResult.Ok("stopped - [LiveMap on to start");
            }

            string when = _lastWriteUtc.HasValue
                ? _lastWriteUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            return HealthResult.Ok(String.Format(
                "running ({0}), {1} tracked actor(s), sequence {2}, last write {3}",
                _all ? "all in " + _zoneId : "custom",
                LiveRegistry.Count,
                _sequence,
                when));
        }
    }
}
