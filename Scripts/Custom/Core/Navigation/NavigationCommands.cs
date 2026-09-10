using System;
using System.Collections.Generic;

using Server.Commands;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// Staff commands for reading and authoring navigation data.
    ///
    /// The authoring commands exist because uo-offline-server's 4000-node graph only exists at
    /// all because they could capture waypoints in-game. Seeding a file by hand gets you a
    /// starting point; walking the roads gets you data that is right.
    ///
    /// EDGES ARE NEVER DERIVED FROM PROXIMITY. [NavMark links only to the previous waypoint
    /// marked in the same session, so a chain follows the path you actually walked. Linking by
    /// nearness would silently connect two waypoints through a building.
    ///
    /// Every mutator applies to the live data, keeps a .bak, writes, and ROLLS BACK if the
    /// write fails - so [NavDebug reflects a change immediately and memory can never disagree
    /// with the file. See NavigationSystem.ApplyAndSave.
    /// </summary>
    public static class NavigationCommands
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>Markers placed by [NavDebug beyond this are not worth the client's time.</summary>
        private const int MaxMarkers = 300;

        private static readonly Dictionary<Mobile, string> _chain = new Dictionary<Mobile, string>();
        private static readonly Dictionary<Mobile, List<Item>> _markers = new Dictionary<Mobile, List<Item>>();
        private static readonly Dictionary<Mobile, Recorder> _recorders = new Dictionary<Mobile, Recorder>();

        public static void Initialize()
        {
            CommandSystem.Register("NavReload", AccessLevel.GameMaster, NavReload_OnCommand);
            CommandSystem.Register("ReloadNav", AccessLevel.GameMaster, NavReload_OnCommand);
            CommandSystem.Register("NavDebug", AccessLevel.GameMaster, NavDebug_OnCommand);
            CommandSystem.Register("NavMark", AccessLevel.GameMaster, NavMark_OnCommand);
            CommandSystem.Register("NavRecord", AccessLevel.GameMaster, NavRecord_OnCommand);
            CommandSystem.Register("NavLink", AccessLevel.GameMaster, NavLink_OnCommand);
            CommandSystem.Register("NavUnlink", AccessLevel.GameMaster, NavUnlink_OnCommand);
            CommandSystem.Register("NavDelete", AccessLevel.GameMaster, NavDelete_OnCommand);
            CommandSystem.Register("NavArrival", AccessLevel.GameMaster, NavArrival_OnCommand);
            CommandSystem.Register("NavRoute", AccessLevel.GameMaster, NavRoute_OnCommand);
            CommandSystem.Register("NavAudit", AccessLevel.Administrator, NavAudit_OnCommand);
            CommandSystem.Register("WalkAudit", AccessLevel.Administrator, WalkAudit_OnCommand);

            EventSink.Disconnected += OnDisconnected;
        }

        private static void OnDisconnected(DisconnectedEventArgs e)
        {
            StopRecording(e.Mobile);
            ClearMarkers(e.Mobile);
            _chain.Remove(e.Mobile);
        }

        // ---- [NavReload ----

        [Usage("NavReload")]
        [Aliases("ReloadNav")]
        [Description("Re-reads navigation.json and rebuilds the travel graph.")]
        private static void NavReload_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            string error;

            if (!NavigationSystem.TryReload(out error))
            {
                from.SendMessage(0x35, String.Format("Navigation NOT reloaded: {0}", error));
                from.SendMessage(0x35, "The shard is still running on the previously loaded data.");
                return;
            }

            from.SendMessage(String.Format(
                "Navigation reloaded: {0} waypoint(s), {1} destination(s).",
                NavigationSystem.Graph.NodeCount,
                NavigationSystem.Destinations.Count));

            ReportWarnings(from);

            CommandLogging.WriteLine(
                from,
                String.Format("{0} {1} reloading the navigation file", from.AccessLevel, CommandLogging.Format(from)));
        }

        private static void ReportWarnings(Mobile from)
        {
            IList<string> warnings = NavigationSystem.DataWarnings;

            if (warnings.Count == 0)
            {
                return;
            }

            from.SendMessage(0x35, String.Format("{0} data warning(s):", warnings.Count));

            for (int i = 0; i < warnings.Count && i < 20; i++)
            {
                from.SendMessage(0x35, "  " + warnings[i]);
            }

            if (warnings.Count > 20)
            {
                from.SendMessage(0x35, "  ... see the console for the rest.");
            }
        }

        // ---- [NavDebug ----

        [Usage("NavDebug [radius]")]
        [Description("Toggles temporary markers on every waypoint, arrival point, destination and zone corner nearby.")]
        private static void NavDebug_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (_markers.ContainsKey(from))
            {
                ClearMarkers(from);
                from.SendMessage("Navigation markers cleared.");
                return;
            }

            if (from.Map == null || from.Map == Map.Internal)
            {
                from.SendMessage(0x35, "You are not on a facet.");
                return;
            }

            int radius = NavigationSystem.DebugRadius;

            if (e.Length >= 1)
            {
                radius = e.GetInt32(0);
            }

            if (radius <= 0)
            {
                from.SendMessage(0x35, "The radius must be positive.");
                return;
            }

            var placed = new List<Item>();

            PlaceMarkers(from, radius, placed);

            if (placed.Count == 0)
            {
                from.SendMessage(0x35, String.Format("No navigation data within {0} tiles.", radius));
                return;
            }

            _markers[from] = placed;

            from.SendMessage(String.Format(
                "{0} marker(s) placed within {1} tiles. [NavDebug again to clear; they expire in {2} minute(s).",
                placed.Count,
                radius,
                NavigationSystem.DebugMarkerMinutes));

            Timer.DelayCall(
                TimeSpan.FromMinutes(NavigationSystem.DebugMarkerMinutes),
                () => ClearMarkers(from));
        }

        /// <summary>A polygon zone's vertices as points, for [NavDebug to mark.</summary>
        private static Point3D[] VerticesOf(NavZone zone)
        {
            int[] v = zone.Vertices;
            var points = new Point3D[v.Length / 2];

            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new Point3D(v[i * 2], v[(i * 2) + 1], 0);
            }

            return points;
        }

        private static void PlaceMarkers(Mobile from, int radius, List<Item> placed)
        {
            Map map = from.Map;
            Point3D centre = from.Location;

            foreach (NavWaypoint waypoint in NavigationSystem.Graph.NodesOn(map))
            {
                if (placed.Count >= MaxMarkers)
                {
                    return;
                }

                if (NavGraph.Chebyshev(centre, waypoint.Location) <= radius)
                {
                    placed.Add(Place(
                        map,
                        waypoint.Location,
                        NavDebugMarker.WaypointHue,
                        String.Format("wp {0}", waypoint.Id)));
                }
            }

            foreach (NavDestination destination in NavigationSystem.DestinationsOn(map))
            {
                if (placed.Count >= MaxMarkers)
                {
                    return;
                }

                if (NavGraph.Chebyshev(centre, destination.Location) > radius)
                {
                    continue;
                }

                placed.Add(Place(
                    map,
                    destination.Location,
                    NavDebugMarker.DestinationHue,
                    String.Format("dest {0} [{1}]", destination.Id, destination.Type)));

                foreach (NavArrival arrival in destination.ArrivalList)
                {
                    if (placed.Count >= MaxMarkers)
                    {
                        return;
                    }

                    placed.Add(Place(
                        map,
                        arrival.Location,
                        arrival.Exclusive ? NavDebugMarker.ExclusiveHue : NavDebugMarker.ArrivalHue,
                        String.Format("arr {0}{1}", destination.Id, arrival.Exclusive ? " (exclusive)" : "")));
                }
            }

            foreach (NavZone zone in NavigationSystem.ZonesOn(map))
            {
                if (placed.Count >= MaxMarkers)
                {
                    return;
                }

                // A polygon is marked at its VERTICES; only a rectangle is described by its
                // corners, and marking a poly's bounding box would draw a shape it is not.
                Point3D[] corners = zone.IsPoly && zone.Vertices.Length >= 6
                    ? VerticesOf(zone)
                    : new[]
                {
                    new Point3D(zone.X, zone.Y, 0),
                    new Point3D(zone.X + zone.Width - 1, zone.Y, 0),
                    new Point3D(zone.X, zone.Y + zone.Height - 1, 0),
                    new Point3D(zone.X + zone.Width - 1, zone.Y + zone.Height - 1, 0)
                };

                for (int i = 0; i < corners.Length; i++)
                {
                    if (placed.Count >= MaxMarkers)
                    {
                        return;
                    }

                    if (NavGraph.Chebyshev(centre, corners[i]) > radius)
                    {
                        continue;
                    }

                    placed.Add(Place(
                        map,
                        new Point3D(corners[i].X, corners[i].Y, map.GetAverageZ(corners[i].X, corners[i].Y)),
                        NavDebugMarker.ZoneHue,
                        String.Format("zone {0}", zone.Id)));
                }
            }
        }

        private static Item Place(Map map, Point3D point, int hue, string label)
        {
            var marker = new NavDebugMarker(hue, label);
            marker.MoveToWorld(point, map);
            return marker;
        }

        private static void ClearMarkers(Mobile from)
        {
            List<Item> placed;

            if (!_markers.TryGetValue(from, out placed))
            {
                return;
            }

            _markers.Remove(from);

            foreach (Item marker in placed)
            {
                if (marker != null && !marker.Deleted)
                {
                    marker.Delete();
                }
            }
        }

        // ---- [NavMark ----

        [Usage("NavMark [id] [nolink]")]
        [Description("Adds a waypoint where you stand, linked to the previous one you marked this session.")]
        private static void NavMark_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            string id = null;
            bool link = true;

            for (int i = 0; i < e.Length; i++)
            {
                string argument = e.GetString(i);

                if (Insensitive.Equals(argument, "nolink"))
                {
                    link = false;
                }
                else if (id == null)
                {
                    id = argument;
                }
                else
                {
                    from.SendMessage(0x35, "Usage: [NavMark [id] [nolink]");
                    return;
                }
            }

            string error;
            string created = AddWaypoint(from, id, link, out error);

            if (created == null)
            {
                from.SendMessage(0x35, error);
                return;
            }

            string previous;
            bool linked = link && _chain.TryGetValue(from, out previous) && previous != null;

            _chain[from] = created;

            from.SendMessage(String.Format(
                "Waypoint '{0}' added{1}.", created, linked ? " and linked to the previous mark" : ""));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} marking navigation waypoint {2}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    created));
        }

        /// <summary>
        /// Shared by [NavMark and [NavRecord. Returns the new id, or null with a reason.
        /// The chain is read here but only written by the caller, so a failed add does not
        /// break the chain.
        /// </summary>
        private static string AddWaypoint(Mobile from, string id, bool link, out string error)
        {
            error = null;

            Map map = from.Map;

            if (map == null || map == Map.Internal)
            {
                error = "You are not on a facet.";
                return null;
            }

            if (id == null)
            {
                id = NextId(from.Location, map);
            }

            if (!NavIds.IsValid(id))
            {
                error = String.Format(
                    "'{0}' is not a valid id (letters, digits, '-', '_' and '.' only, no spaces).", id);
                return null;
            }

            if (NavigationSystem.Graph.Contains(id))
            {
                error = String.Format("A waypoint called '{0}' already exists.", id);
                return null;
            }

            string previous = null;

            if (link && _chain.TryGetValue(from, out previous))
            {
                // The previous mark may have been deleted, or the file reloaded under us.
                if (previous != null && !NavigationSystem.Graph.Contains(previous))
                {
                    previous = null;
                }
            }

            var waypoint = new NavWaypoint(id, map, from.Location);
            NavEdge edge = previous != null ? new NavEdge(previous, id) : null;

            NavigationStore store = NavigationSystem.Store;

            bool saved = NavigationSystem.ApplyAndSave(
                delegate
                {
                    store.Waypoints.Add(waypoint);

                    if (edge != null)
                    {
                        store.Edges.Add(edge);
                    }
                },
                delegate
                {
                    store.Waypoints.Remove(waypoint);

                    if (edge != null)
                    {
                        store.Edges.Remove(edge);
                    }
                },
                out error);

            return saved ? id : null;
        }

        /// <summary>
        /// Auto-generated ids are "&lt;zoneTag&gt;-&lt;n&gt;", taken from the smallest nav zone
        /// containing the point - which is the one whose tags are most specific. Outside every
        /// zone the prefix is "wp".
        /// </summary>
        private static string NextId(Point3D point, Map map)
        {
            NavZone zone = Nav.SmallestZoneAt(point, map);

            string prefix = "wp";

            if (zone != null)
            {
                prefix = zone.PrimaryTag ?? zone.Id;
            }

            for (int n = 1; ; n++)
            {
                string candidate = prefix + "-" + n;

                if (!NavigationSystem.Graph.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // ---- [NavRecord ----

        [Usage("NavRecord on|off [tiles]")]
        [Description("While on, drops a chained waypoint every few tiles you walk.")]
        private static void NavRecord_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage(0x35, "Usage: [NavRecord on|off [tiles]");
                return;
            }

            string mode = e.GetString(0);

            if (Insensitive.Equals(mode, "off"))
            {
                if (StopRecording(from))
                {
                    from.SendMessage("Recording stopped.");
                }
                else
                {
                    from.SendMessage(0x35, "You were not recording.");
                }

                return;
            }

            if (!Insensitive.Equals(mode, "on"))
            {
                from.SendMessage(0x35, "Usage: [NavRecord on|off [tiles]");
                return;
            }

            if (from.Map == null || from.Map == Map.Internal)
            {
                from.SendMessage(0x35, "You are not on a facet.");
                return;
            }

            int interval = NavigationSystem.RecordIntervalTiles;

            if (e.Length >= 2)
            {
                interval = e.GetInt32(1);
            }

            if (interval <= 0 || interval > NavigationSystem.HopMaxTiles)
            {
                from.SendMessage(0x35, String.Format(
                    "The interval must be between 1 and the hop cap ({0}).", NavigationSystem.HopMaxTiles));
                return;
            }

            StopRecording(from);

            var recorder = new Recorder(from, interval);
            _recorders[from] = recorder;
            recorder.Start();

            from.SendMessage(String.Format(
                "Recording waypoints every {0} tile(s). Walk, do not run - a run samples too coarsely. [NavRecord off to stop.",
                interval));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} starting navigation recording",
                    from.AccessLevel,
                    CommandLogging.Format(from)));
        }

        private static bool StopRecording(Mobile from)
        {
            Recorder recorder;

            if (from == null || !_recorders.TryGetValue(from, out recorder))
            {
                return false;
            }

            _recorders.Remove(from);
            recorder.Stop();

            return true;
        }

        /// <summary>
        /// Samples a staff member's position and drops a chained waypoint each time they have
        /// moved far enough. This is how a road becomes data.
        /// </summary>
        private sealed class Recorder
        {
            private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1.0);

            private readonly Mobile _from;
            private readonly int _interval;

            private Timer _timer;
            private Point3D _last;
            private bool _hasLast;

            public Recorder(Mobile from, int interval)
            {
                _from = from;
                _interval = interval;
            }

            public void Start()
            {
                _timer = Timer.DelayCall(SampleInterval, SampleInterval, Sample);
            }

            public void Stop()
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer = null;
                }
            }

            private void Sample()
            {
                if (_from == null || _from.Deleted || _from.NetState == null
                    || _from.Map == null || _from.Map == Map.Internal)
                {
                    StopRecording(_from);
                    return;
                }

                if (_hasLast && NavGraph.Chebyshev(_last, _from.Location) < _interval)
                {
                    return;
                }

                string error;
                string created = AddWaypoint(_from, null, true, out error);

                if (created == null)
                {
                    _from.SendMessage(0x35, "Recording stopped: " + error);
                    StopRecording(_from);
                    return;
                }

                _chain[_from] = created;
                _last = _from.Location;
                _hasLast = true;

                _from.SendMessage(0x40, String.Format("Recorded '{0}'.", created));
            }
        }

        // ---- [NavLink / [NavUnlink ----

        [Usage("NavLink <a> <b> [gate]")]
        [Description("Adds one explicit edge between two waypoints.")]
        private static void NavLink_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 2)
            {
                from.SendMessage(0x35, "Usage: [NavLink <a> <b> [gate]");
                return;
            }

            string a = e.GetString(0);
            string b = e.GetString(1);
            bool gate = e.Length >= 3 && Insensitive.Equals(e.GetString(2), "gate");

            if (!NavigationSystem.Graph.Contains(a) || !NavigationSystem.Graph.Contains(b))
            {
                from.SendMessage(0x35, "Both ends must be existing waypoints.");
                return;
            }

            if (Insensitive.Equals(a, b))
            {
                from.SendMessage(0x35, "A waypoint cannot link to itself.");
                return;
            }

            if (FindEdges(a, b).Count > 0)
            {
                from.SendMessage(0x35, "Those waypoints are already linked.");
                return;
            }

            var edge = new NavEdge(a, b);

            if (gate)
            {
                edge.KindName = "gate";
            }

            NavigationStore store = NavigationSystem.Store;
            string error;

            if (!NavigationSystem.ApplyAndSave(
                () => store.Edges.Add(edge),
                () => store.Edges.Remove(edge),
                out error))
            {
                from.SendMessage(0x35, "Not saved: " + error);
                return;
            }

            from.SendMessage(String.Format("Linked '{0}' to '{1}'{2}.", a, b, gate ? " as a gate" : ""));
            ReportWarnings(from);

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} linking navigation waypoints {2} and {3}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    a,
                    b));
        }

        [Usage("NavUnlink <a> <b>")]
        [Description("Removes every edge between two waypoints.")]
        private static void NavUnlink_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 2)
            {
                from.SendMessage(0x35, "Usage: [NavUnlink <a> <b>");
                return;
            }

            string a = e.GetString(0);
            string b = e.GetString(1);

            List<NavEdge> doomed = FindEdges(a, b);

            if (doomed.Count == 0)
            {
                from.SendMessage(0x35, "Those waypoints are not linked.");
                return;
            }

            NavigationStore store = NavigationSystem.Store;
            string error;

            if (!NavigationSystem.ApplyAndSave(
                () => RemoveAll(store.Edges, doomed),
                () => store.Edges.AddRange(doomed),
                out error))
            {
                from.SendMessage(0x35, "Not saved: " + error);
                return;
            }

            from.SendMessage(String.Format("Removed {0} edge(s) between '{1}' and '{2}'.", doomed.Count, a, b));
            ReportWarnings(from);

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} unlinking navigation waypoints {2} and {3}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    a,
                    b));
        }

        private static List<NavEdge> FindEdges(string a, string b)
        {
            var result = new List<NavEdge>();

            foreach (NavEdge edge in NavigationSystem.Store.Edges)
            {
                if ((Insensitive.Equals(edge.From, a) && Insensitive.Equals(edge.To, b))
                    || (Insensitive.Equals(edge.From, b) && Insensitive.Equals(edge.To, a)))
                {
                    result.Add(edge);
                }
            }

            return result;
        }

        // ---- [NavDelete ----

        [Usage("NavDelete <id>")]
        [Description("Removes a waypoint and every edge touching it.")]
        private static void NavDelete_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage(0x35, "Usage: [NavDelete <id>");
                return;
            }

            string id = e.GetString(0);

            NavWaypoint waypoint = Nav.Waypoint(id);

            if (waypoint == null)
            {
                from.SendMessage(0x35, String.Format("There is no waypoint '{0}'.", id));
                return;
            }

            var doomedEdges = new List<NavEdge>();

            foreach (NavEdge edge in NavigationSystem.Store.Edges)
            {
                if (Insensitive.Equals(edge.From, id) || Insensitive.Equals(edge.To, id))
                {
                    doomedEdges.Add(edge);
                }
            }

            NavigationStore store = NavigationSystem.Store;
            string error;

            if (!NavigationSystem.ApplyAndSave(
                delegate
                {
                    store.Waypoints.Remove(waypoint);
                    RemoveAll(store.Edges, doomedEdges);
                },
                delegate
                {
                    store.Waypoints.Add(waypoint);
                    store.Edges.AddRange(doomedEdges);
                },
                out error))
            {
                from.SendMessage(0x35, "Not saved: " + error);
                return;
            }

            // A chain pointing at a waypoint that no longer exists would link the next mark to
            // nothing; AddWaypoint tolerates that, but clearing it is clearer.
            var stale = new List<Mobile>();

            foreach (KeyValuePair<Mobile, string> pair in _chain)
            {
                if (Insensitive.Equals(pair.Value, id))
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (Mobile mobile in stale)
            {
                _chain.Remove(mobile);
            }

            from.SendMessage(String.Format(
                "Waypoint '{0}' and {1} edge(s) removed.", id, doomedEdges.Count));

            ReportWarnings(from);

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} deleting navigation waypoint {2}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    id));
        }

        // ---- [NavArrival ----

        [Usage("NavArrival <destinationId> [exclusive]")]
        [Description("Adds an arrival point for a destination where you stand.")]
        private static void NavArrival_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage(0x35, "Usage: [NavArrival <destinationId> [exclusive]");
                return;
            }

            string id = e.GetString(0);
            bool exclusive = e.Length >= 2 && Insensitive.Equals(e.GetString(1), "exclusive");

            NavDestination destination = Nav.Destination(id);

            if (destination == null)
            {
                from.SendMessage(0x35, String.Format("There is no destination '{0}'.", id));
                return;
            }

            if (from.Map != destination.Map)
            {
                from.SendMessage(0x35, String.Format(
                    "'{0}' is on {1}; you are on {2}.", id, destination.MapName, from.Map));
                return;
            }

            var arrival = new NavArrival(destination.Id, from.Location, exclusive);

            NavWaypoint nearest = NavigationSystem.Graph.Nearest(
                from.Location, from.Map, NavigationSystem.HopMaxTiles);

            if (nearest != null)
            {
                arrival.WaypointIds = nearest.Id;
            }

            NavigationStore store = NavigationSystem.Store;
            string error;

            if (!NavigationSystem.ApplyAndSave(
                () => store.Arrivals.Add(arrival),
                () => store.Arrivals.Remove(arrival),
                out error))
            {
                from.SendMessage(0x35, "Not saved: " + error);
                return;
            }

            from.SendMessage(String.Format(
                "Arrival point added for '{0}'{1}{2}.",
                destination.Id,
                exclusive ? " (exclusive)" : "",
                nearest != null ? " via '" + nearest.Id + "'" : " with no waypoint in range"));

            ReportWarnings(from);

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} adding a navigation arrival point for {2}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    destination.Id));
        }

        // ---- [NavRoute ----

        [Usage("NavRoute <from> <to>")]
        [Description("Prints the computed route between two waypoints or destinations.")]
        private static void NavRoute_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 2)
            {
                from.SendMessage(0x35, "Usage: [NavRoute <from> <to>");
                return;
            }

            NavRoute route;
            string error;

            if (!Nav.TryRoute(e.GetString(0), e.GetString(1), out route, out error))
            {
                from.SendMessage(0x35, "No route: " + error);
                return;
            }

            from.SendMessage(String.Format(
                "{0} step(s), cost {1:F1}:", route.Count, route.Cost));

            for (int i = 0; i < route.Count; i++)
            {
                NavStep step = route.Steps[i];

                int hop = i > 0 ? NavGraph.Chebyshev(route.Steps[i - 1].Point, step.Point) : 0;

                from.SendMessage(0x40, String.Format(
                    "  {0,2}. {1,-10} {2,-24} {3}",
                    i + 1,
                    step.Kind,
                    step.WaypointId ?? "(arrival)",
                    i > 0 ? String.Format("{0} {1} tile(s)", step.Point, hop) : step.Point.ToString()));
            }
        }

        // ---- [NavAudit ----

        [Usage("NavAudit")]
        [Description("Pathfinds every walk edge against the real map and reports the ones that cannot be walked.")]
        private static void NavAudit_OnCommand(CommandEventArgs e)
        {
            NavAudit.Run(e.Mobile);

            CommandLogging.WriteLine(
                e.Mobile,
                String.Format(
                    "{0} {1} auditing navigation edges",
                    e.Mobile.AccessLevel,
                    CommandLogging.Format(e.Mobile)));
        }

        // ---- [WalkAudit ----

        [Usage("WalkAudit [probes | selftest]")]
        [Description(
            "Walks every edge in both directions and every arrival from each of its approach "
            + "waypoints with real probe walkers, and reports what actually happened. "
            + "'selftest' walks one hop the audit is required to fail.")]
        private static void WalkAudit_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            string argument = e.ArgString == null ? "" : e.ArgString.Trim();
            bool selfTest = Insensitive.Equals(argument, "selftest");

            int probes = 0;

            if (!selfTest && argument.Length > 0 && !Int32.TryParse(argument, out probes))
            {
                from.SendMessage(0x35, "Usage: [WalkAudit [probes | selftest]");
                return;
            }

            string error;

            if (!NavWalkAudit.TryStart(from, probes, selfTest, out error))
            {
                from.SendMessage(0x35, "Walk audit not started: " + error);
                return;
            }

            from.SendMessage(
                selfTest
                    ? "Walk audit self-test running; it must report one FAILED row."
                    : "Walk audit running. The result goes to the console and to "
                        + NavWalkAudit.SnapshotPath + ".");

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} running a walk audit{2}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    selfTest ? " (self-test)" : ""));
        }

        // ---- helpers ----

        private static void RemoveAll<T>(List<T> list, List<T> doomed)
        {
            foreach (T item in doomed)
            {
                list.Remove(item);
            }
        }
    }
}
