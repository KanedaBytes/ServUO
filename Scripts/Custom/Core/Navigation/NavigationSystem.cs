using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Custom
{
    /// <summary>
    /// Loads Data/Custom/navigation.json and owns the live navigation data.
    ///
    /// GAME THREAD ONLY - see NavGraph. Background callers marshal through LoopQueue.
    ///
    /// Failure contract, matching RestrictedZoneSystem: on ANY load failure the live data is
    /// kept, because a bad edit must never leave the shard with no navigation. Structural
    /// problems fail the load; referential ones (an edge naming a waypoint that does not exist)
    /// drop the offending record and are reported by the Nav.Data health check instead, so one
    /// typo cannot take navigation offline.
    /// </summary>
    public static class NavigationSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        public const string ConfigPath = "Data/Custom/navigation.json";

        /// <summary>Bumped when the JSON shape changes incompatibly.</summary>
        public const int SchemaVersion = 1;

        private static NavigationStore _store = new NavigationStore();
        private static NavGraph _graph = new NavGraph();

        private static readonly Dictionary<string, NavDestination> _destinations =
            new Dictionary<string, NavDestination>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, NavZone> _zones =
            new Dictionary<string, NavZone>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, NavRouteDef> _routes =
            new Dictionary<string, NavRouteDef>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<Map, List<NavDestination>> _destinationsByMap =
            new Dictionary<Map, List<NavDestination>>();

        private static readonly Dictionary<Map, List<NavZone>> _zonesByMap =
            new Dictionary<Map, List<NavZone>>();

        private static readonly Dictionary<string, double> _costTags =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<string> _dataWarnings = new List<string>();

        private static string _lastError;
        private static DateTime? _lastLoadUtc;
        private static bool _navLive;

        // ---- config ----

        /// <summary>
        /// Maximum authored edge length, in tiles (Chebyshev).
        ///
        /// ServUO's FastAStarAlgorithm searches a 38x38 box CENTRED ON THE MIDPOINT of start
        /// and goal (Scripts/Services/Pathing/FastAStarAlgorithm.cs:27), so a hop whose real
        /// detour leaves that box is unfindable at any budget - the mobile then walks into
        /// scenery rather than failing loudly. The ModernUO shard independently settled on 15
        /// for route legs for exactly this reason; 12 keeps margin for a detour.
        /// </summary>
        public static int HopMaxTiles
        {
            get { return Config.Get("Custom.NavHopMaxTiles", 12); }
        }

        /// <summary>
        /// The waypoint that decides which side of a split graph is "the main graph".
        ///
        /// Britain's bank plaza, because it is the one place this shard is certainly built around.
        /// The island check used to call the LARGEST component the mainland, which held only while
        /// we were the biggest thing in the file - a single adopt inverted it.
        /// </summary>
        public static string HomeWaypoint
        {
            get { return Config.Get("Custom.NavHomeWaypoint", "brit-bank-2"); }
        }

        public static int RecordIntervalTiles
        {
            get { return Config.Get("Custom.NavRecordIntervalTiles", 8); }
        }

        public static int DebugRadius
        {
            get { return Config.Get("Custom.NavDebugRadius", 32); }
        }

        public static int DebugMarkerMinutes
        {
            get { return Config.Get("Custom.NavDebugMarkerMinutes", 5); }
        }

        public static int ArrivalScatter
        {
            get { return Config.Get("Custom.NavArrivalScatter", 2); }
        }

        public static int RouteCacheMax
        {
            get { return Config.Get("Custom.NavRouteCacheMax", 512); }
        }

        // ---- state ----

        public static NavGraph Graph
        {
            get { return _graph; }
        }

        public static NavigationStore Store
        {
            get { return _store; }
        }

        public static string LastError
        {
            get { return _lastError; }
        }

        public static DateTime? LastLoadUtc
        {
            get { return _lastLoadUtc; }
        }

        public static IList<NavDestination> Destinations
        {
            get { return _store.Destinations.AsReadOnly(); }
        }

        public static IList<NavZone> Zones
        {
            get { return _store.Zones.AsReadOnly(); }
        }

        public static IList<NavRouteDef> Routes
        {
            get { return _store.Routes.AsReadOnly(); }
        }

        /// <summary>
        /// Everything wrong with the data that did not stop it loading: dropped references,
        /// over-cap edges, destinations with no arrival points, disconnected components.
        /// </summary>
        public static IList<string> DataWarnings
        {
            get { return _dataWarnings.AsReadOnly(); }
        }

        // ---- boot ----

        /// <summary>
        /// CallPriority 100 puts this above MapDefinitions.Configure() (untagged, so 0),
        /// because validating a record resolves its facet name and the facets must exist.
        /// No world state is touched here - only the JSON and the graph.
        /// </summary>
        [CallPriority(100)]
        public static void Configure()
        {
            string error;

            if (!TryLoad(out error))
            {
                Log.Error("Navigation is EMPTY - {0} was not loaded: {1}", ConfigPath, error);
                return;
            }

            LogWarnings();
        }

        /// <summary>
        /// Writes every data warning to the console.
        ///
        /// The health check can only carry the first one, and ServUO's console cannot invoke
        /// [NavReload to see the rest - so without this, verifying nav data over SSH would mean
        /// fixing one warning per restart.
        /// </summary>
        private static void LogWarnings()
        {
            if (_dataWarnings.Count == 0)
            {
                return;
            }

            Log.Warn("{0} navigation data warning(s):", _dataWarnings.Count);

            for (int i = 0; i < _dataWarnings.Count && i < 40; i++)
            {
                Log.Warn("  " + _dataWarnings[i]);
            }

            if (_dataWarnings.Count > 40)
            {
                Log.Warn("  ... and {0} more.", _dataWarnings.Count - 40);
            }
        }

        public static void Initialize()
        {
            _navLive = true;

            HealthCheck.Register("Nav.Data", BuildHealthResult);

            if (Config.Get("Custom.NavAuditOnStart", false))
            {
                // Deferred to ServerStarted because the audit paths against real map data.
                EventSink.ServerStarted += () => NavAudit.Run(null);
            }
        }

        // ---- loading ----

        /// <summary>
        /// Reads and validates the file, then rebuilds every index and the graph.
        ///
        /// On ANY failure the live data is kept: every error path returns before the existing
        /// store is replaced. Silent by design so Configure() and TryReload() can each phrase
        /// their own message.
        /// </summary>
        public static bool TryLoad(out string error)
        {
            IList<string> ignored;
            return TryLoad(out error, out ignored);
        }

        /// <summary>
        /// As TryLoad, but also hands back the validator's errors ONE PER ITEM.
        ///
        /// JsonConfig collects them as a list and every caller used to flatten it immediately with
        /// "; ". That is fine for a console line and wrong for anything that wants to show them:
        /// re-splitting on "; " in the reader is unsafe, because a Newtonsoft parse message can
        /// contain anything. The bridge shows these in a banner, so the list survives the trip.
        /// </summary>
        public static bool TryLoad(out string error, out IList<string> errors)
        {
            NavigationStore store;

            if (!JsonConfig.TryLoad(ConfigPath, out store, out errors))
            {
                error = String.Join("; ", ToArray(errors));
                _lastError = error;
                return false;
            }

            // Only now is it safe to swap.
            _store = store;

            Rebuild();

            error = null;
            _lastError = null;
            _lastLoadUtc = DateTime.UtcNow;

            return true;
        }

        /// <summary>Single entry point for the staff command and, later, the admin API.</summary>
        public static bool TryReload(out string error)
        {
            IList<string> ignored;
            return TryReload(out error, out ignored);
        }

        /// <summary>As TryReload, keeping the validator's errors as a list. See TryLoad.</summary>
        public static bool TryReload(out string error, out IList<string> errors)
        {
            if (!TryLoad(out error, out errors))
            {
                NotifyStaff(String.Format("Navigation NOT reloaded: {0}", error));
                Log.Error("Navigation NOT reloaded: {0}", error);
                return false;
            }

            // The recovery counts describe a graph. After an edit they would describe two, and
            // the whole point of the health line is to say whether the CURRENT graph is walkable.
            NavWalker.ResetRungTotals();

            Log.Info(
                "Reloaded navigation: {0} waypoint(s), {1} destination(s), {2} warning(s).",
                _graph.NodeCount,
                _destinations.Count,
                _dataWarnings.Count);

            LogWarnings();

            return true;
        }

        /// <summary>
        /// Rebinds every record, rebuilds the indexes and the graph, and recomputes the data
        /// warnings. Called after a load and after every mutation.
        /// </summary>
        private static void Rebuild()
        {
            _destinations.Clear();
            _zones.Clear();
            _routes.Clear();
            _destinationsByMap.Clear();
            _zonesByMap.Clear();
            _costTags.Clear();
            _dataWarnings.Clear();

            foreach (NavCostTag tag in _store.CostTags)
            {
                _costTags[tag.Tag] = tag.Multiplier;
            }

            foreach (NavWaypoint waypoint in _store.Waypoints)
            {
                waypoint.Bind();
            }

            foreach (NavEdge edge in _store.Edges)
            {
                edge.Bind();
            }

            foreach (NavDestination destination in _store.Destinations)
            {
                destination.Bind();
                _destinations[destination.Id] = destination;

                List<NavDestination> perMap;

                if (!_destinationsByMap.TryGetValue(destination.Map, out perMap))
                {
                    perMap = new List<NavDestination>();
                    _destinationsByMap[destination.Map] = perMap;
                }

                perMap.Add(destination);
            }

            foreach (NavZone zone in _store.Zones)
            {
                zone.Bind();
                _zones[zone.Id] = zone;

                List<NavZone> perMap;

                if (!_zonesByMap.TryGetValue(zone.Map, out perMap))
                {
                    perMap = new List<NavZone>();
                    _zonesByMap[zone.Map] = perMap;
                }

                perMap.Add(zone);
            }

            foreach (NavArrival arrival in _store.Arrivals)
            {
                arrival.Bind();
            }

            foreach (NavRouteDef route in _store.Routes)
            {
                route.Bind();
            }

            _graph.Build(_store, _costTags, HopMaxTiles);

            foreach (string warning in _graph.Warnings)
            {
                _dataWarnings.Add(warning);
            }

            AttachArrivals();
            CheckReferences();
            CheckQuality();
            RunSelfTests();
        }

        /// <summary>Hangs each arrival off its destination, dropping orphans with a warning.</summary>
        private static void AttachArrivals()
        {
            foreach (NavArrival arrival in _store.Arrivals)
            {
                NavDestination destination;

                if (!_destinations.TryGetValue(arrival.DestinationId, out destination))
                {
                    _dataWarnings.Add(String.Format(
                        "arrival at {0},{1} dropped: no destination '{2}'",
                        arrival.X,
                        arrival.Y,
                        arrival.DestinationId));
                    continue;
                }

                destination.ArrivalList.Add(arrival);
            }
        }

        private static void CheckReferences()
        {
            foreach (NavDestination destination in _store.Destinations)
            {
                WarnUnknownWaypoints(
                    destination.WaypointList,
                    String.Format("destination '{0}'", destination.Id));
            }

            foreach (NavArrival arrival in _store.Arrivals)
            {
                WarnUnknownWaypoints(
                    arrival.WaypointList,
                    String.Format("arrival for '{0}'", arrival.DestinationId));
            }

            foreach (NavRouteDef route in _store.Routes)
            {
                bool usable = true;

                for (int i = 0; i < route.WaypointList.Length; i++)
                {
                    if (!_graph.Contains(route.WaypointList[i]))
                    {
                        _dataWarnings.Add(String.Format(
                            "route '{0}' is unusable: no waypoint '{1}'",
                            route.Id,
                            route.WaypointList[i]));
                        usable = false;
                    }
                }

                // A route with a dangling id is withheld from the index rather than handed to
                // a consumer that would walk into the gap.
                if (usable)
                {
                    _routes[route.Id] = route;
                }
            }
        }

        private static void WarnUnknownWaypoints(string[] ids, string owner)
        {
            if (ids == null)
            {
                return;
            }

            for (int i = 0; i < ids.Length; i++)
            {
                if (!_graph.Contains(ids[i]))
                {
                    _dataWarnings.Add(String.Format("{0} names unknown waypoint '{1}'", owner, ids[i]));
                }
            }
        }

        private static void CheckQuality()
        {
            int cap = HopMaxTiles;

            foreach (NavDestination destination in _store.Destinations)
            {
                if (destination.ArrivalList.Count == 0)
                {
                    _dataWarnings.Add(String.Format(
                        "destination '{0}' has no arrival points", destination.Id));
                    continue;
                }

                int exclusive = 0;
                int shared = 0;
                bool reachable = false;
                var stranding = new List<string>();

                foreach (NavArrival arrival in destination.ArrivalList)
                {
                    if (arrival.Exclusive)
                    {
                        exclusive++;
                    }
                    else
                    {
                        shared++;
                    }

                    if (_graph.Nearest(arrival.Location, destination.Map, cap) != null)
                    {
                        reachable = true;
                        continue;
                    }

                    // EVERY arrival has to be routable, not just one of them.
                    //
                    // Nav.TryRouteFrom refuses to route from anywhere with no waypoint inside the
                    // hop cap, so an arrival beyond it is a ONE-WAY TRIP: the picker sends a
                    // mobile there, it does whatever it came to do, and then asks for a way home
                    // every tick and is told there is none, for ever. The failure is silent,
                    // permanent, and from outside it looks exactly like a broken walker.
                    //
                    // This used to be asked as "is ANY arrival reachable", which a destination
                    // with one good point and four stranding ones passes quietly - and did. The
                    // mine at brit-mine-north shipped with an arrival seventeen tiles from its
                    // only approach waypoint against a cap of twelve, so roughly one miner in five
                    // was stranded on arrival, and the only symptom was an intermittent work-probe
                    // failure a long way downstream.
                    // maxTiles of 0 means "no limit" here, which is how the real distance is got
                    // for the message: the capped call above has already said only that there is
                    // nothing inside the cap, not how far outside it the nearest one is.
                    NavWaypoint nearest = _graph.Nearest(arrival.Location, destination.Map, 0);

                    stranding.Add(nearest == null
                        ? String.Format("({0},{1}) has no waypoint at all on this facet",
                            arrival.X, arrival.Y)
                        : String.Format("({0},{1}) is {2} tiles from '{3}'",
                            arrival.X,
                            arrival.Y,
                            NavGraph.Chebyshev(arrival.Location, nearest.Location),
                            nearest.Id));
                }

                if (!reachable)
                {
                    _dataWarnings.Add(String.Format(
                        "destination '{0}' has no arrival point within {1} tiles of a waypoint",
                        destination.Id,
                        cap));
                }
                else if (stranding.Count > 0)
                {
                    _dataWarnings.Add(String.Format(
                        "destination '{0}' has {1} arrival point(s) further than the {2}-tile hop cap "
                        + "from any waypoint, so a mobile sent to one cannot route away again: {3}",
                        destination.Id,
                        stranding.Count,
                        cap,
                        String.Join(", ", stranding.ToArray())));
                }

                // A guard post with nowhere for a second guard to stand is a data bug, not a
                // runtime one: the picker would fall back to scattering round the centre.
                //
                // `shared` counts every non-exclusive arrival, `exact` ones included: an exact
                // arrival is not reserved, so a second mobile can still be sent to it. Counting
                // exactness as exclusivity is what made a station of four shareable benches report
                // itself as having nowhere for a second worker to stand.
                if (exclusive > 0 && shared < 2)
                {
                    _dataWarnings.Add(String.Format(
                        "destination '{0}' has {1} exclusive arrival point(s) but only {2} shared one(s)",
                        destination.Id,
                        exclusive,
                        shared));
                }
            }

            foreach (NavWaypoint waypoint in _store.Waypoints)
            {
                if (_graph.EdgeCountOf(waypoint.Id) == 0)
                {
                    _dataWarnings.Add(String.Format("waypoint '{0}' has no edges", waypoint.Id));
                }
            }

            CheckRoutes(cap);
            CheckComponents();
        }

        /// <summary>
        /// An authored route is walked hop by hop, so a leg longer than the cap is as broken
        /// here as it is on an edge - and less obvious, because the two ends may each be
        /// perfectly good waypoints.
        /// </summary>
        private static void CheckRoutes(int cap)
        {
            foreach (NavRouteDef route in _routes.Values)
            {
                string[] ids = route.WaypointList;

                // Only a cycle has a closing leg from the last waypoint back to the first. A
                // ping-pong retraces the legs it already walked, so its legs are the forward
                // ones and nothing more.
                int legs = route.Mode == NavRouteMode.Cycle ? ids.Length : ids.Length - 1;

                for (int i = 0; i < legs; i++)
                {
                    NavWaypoint a = _graph.Node(ids[i]);
                    NavWaypoint b = _graph.Node(ids[(i + 1) % ids.Length]);

                    if (a == null || b == null)
                    {
                        continue;
                    }

                    int distance = NavGraph.Chebyshev(a.Location, b.Location);

                    if (distance > cap)
                    {
                        _dataWarnings.Add(String.Format(
                            "route '{0}' leg '{1}' -> '{2}' is {3} tiles, over the {4}-tile hop cap",
                            route.Id,
                            a.Id,
                            b.Id,
                            distance,
                            cap));
                    }
                }
            }
        }

        /// <summary>
        /// Warns when a walk-connected component OTHER than the largest holds a destination or an
        /// arrival point - an island, which is the single most common authoring mistake.
        ///
        /// This used to count components per facet and say only how many there were, which is
        /// true and almost useless: it did not say which waypoints were cut off, did not look at
        /// arrivals at all, and left the reader to find the seam by hand. A road authored out to
        /// the west cliff was joined to nothing, because the corridor tool had put a DUPLICATE
        /// waypoint on top of 'brit-gate-w' rather than linking to it - 43 waypoints and a mine
        /// hanging off the graph, and the first symptom anybody saw was a Miner choosing the
        /// wrong site. An island has to be a warning at load, not a bot that cannot route.
        ///
        /// The mainland is the component holding `Custom.NavHomeWaypoint`, not the largest one.
        ///
        /// Largest was wrong, and adopting proved it: one adopt brought in 481 waypoints against
        /// Britain's 231, so the adopted region became "the mainland" and the warning pointed at
        /// BRITAIN as the island. The warning has to name the side cut off from the shard's own
        /// home, and that home does not move because somebody imported a continent. Size is kept
        /// only as a fallback, for a graph that has not named an anchor.
        ///
        /// A component holding nothing but waypoints is still left alone - scaffolding, not a
        /// fault.
        /// </summary>
        private static void CheckComponents()
        {
            if (_graph == null || _graph.ComponentCount <= 1)
            {
                return;
            }

            // Every waypoint, grouped by the component it walks within.
            var members = new Dictionary<int, List<NavWaypoint>>();

            foreach (NavWaypoint waypoint in _graph.Nodes)
            {
                int component = _graph.ComponentOf(waypoint.Id);

                if (component < 0)
                {
                    continue;
                }

                List<NavWaypoint> list;

                if (!members.TryGetValue(component, out list))
                {
                    list = new List<NavWaypoint>();
                    members[component] = list;
                }

                list.Add(waypoint);
            }

            int mainland = -1;
            int biggest = -1;

            foreach (KeyValuePair<int, List<NavWaypoint>> pair in members)
            {
                if (pair.Value.Count > biggest)
                {
                    biggest = pair.Value.Count;
                    mainland = pair.Key;
                }
            }

            // The anchor wins over size wherever it exists.
            NavWaypoint home = _graph.Node(HomeWaypoint);

            if (home != null)
            {
                int anchored = _graph.ComponentOf(home.Id);

                if (anchored >= 0)
                {
                    mainland = anchored;
                }
            }

            // What each island holds. Both questions matter and the old check asked neither:
            // a destination on an island cannot be walked to, and an arrival on one cannot be
            // walked away from.
            var strandedDestinations = new Dictionary<int, List<string>>();
            var strandedArrivals = new Dictionary<int, int>();

            foreach (NavDestination destination in _store.Destinations)
            {
                int component = ComponentNear(destination.Location, destination.Map);

                if (component < 0 || component == mainland)
                {
                    continue;
                }

                List<string> ids;

                if (!strandedDestinations.TryGetValue(component, out ids))
                {
                    ids = new List<string>();
                    strandedDestinations[component] = ids;
                }

                ids.Add(destination.Id);
            }

            foreach (NavArrival arrival in _store.Arrivals)
            {
                // An arrival has no map of its own; it lives on the one its destination is on.
                NavDestination owner;

                if (!_destinations.TryGetValue(arrival.DestinationId, out owner))
                {
                    continue;
                }

                int component = ComponentNear(arrival.Location, owner.Map);

                if (component < 0 || component == mainland)
                {
                    continue;
                }

                int count;
                strandedArrivals.TryGetValue(component, out count);
                strandedArrivals[component] = count + 1;
            }

            foreach (KeyValuePair<int, List<NavWaypoint>> pair in members)
            {
                if (pair.Key == mainland)
                {
                    continue;
                }

                List<string> destinations;
                int arrivals;

                strandedDestinations.TryGetValue(pair.Key, out destinations);
                strandedArrivals.TryGetValue(pair.Key, out arrivals);

                if ((destinations == null || destinations.Count == 0) && arrivals == 0)
                {
                    continue;
                }

                _dataWarnings.Add(String.Format(
                    "{0} waypoint(s) form an island nothing can walk to or from, holding {1}{2}: {3}. "
                    + "Link one of them to the main graph.",
                    pair.Value.Count,
                    destinations == null || destinations.Count == 0
                        ? "no destination"
                        : String.Format("destination(s) {0}", String.Join(", ", destinations.ToArray())),
                    arrivals == 0 ? "" : String.Format(" and {0} arrival point(s)", arrivals),
                    NameWaypoints(pair.Value)));
            }
        }

        /// <summary>The component of the waypoint nearest a point, or -1 when the graph is empty.</summary>
        private static int ComponentNear(Point3D at, Map map)
        {
            NavWaypoint nearest = _graph.Nearest(at, map, 0);

            return nearest == null ? -1 : _graph.ComponentOf(nearest.Id);
        }

        /// <summary>
        /// The island's waypoints, named. Capped, because an island of forty-three is as findable
        /// from the first eight ids as from all of them, and a warning nobody can read is one
        /// nobody acts on.
        /// </summary>
        private static string NameWaypoints(List<NavWaypoint> waypoints)
        {
            int shown = Math.Min(waypoints.Count, 8);
            var names = new string[shown];

            for (int i = 0; i < shown; i++)
            {
                names[i] = String.Format("'{0}'", waypoints[i].Id);
            }

            string joined = String.Join(", ", names);

            return waypoints.Count > shown
                ? String.Format("{0} and {1} more", joined, waypoints.Count - shown)
                : joined;
        }

        private static void RunSelfTests()
        {
            foreach (NavSelfTest test in _store.SelfTests)
            {
                NavRoute route;
                string error;

                if (!Nav.TryRoute(test.From, test.To, out route, out error))
                {
                    _dataWarnings.Add(String.Format("selfTest {0}: {1}", test, error));
                }
            }
        }

        // ---- lookup, used by Nav ----

        public static NavDestination Destination(string id)
        {
            NavDestination result;
            return id != null && _destinations.TryGetValue(id, out result) ? result : null;
        }

        public static NavZone Zone(string id)
        {
            NavZone result;
            return id != null && _zones.TryGetValue(id, out result) ? result : null;
        }

        public static NavRouteDef Route(string id)
        {
            NavRouteDef result;
            return id != null && _routes.TryGetValue(id, out result) ? result : null;
        }

        public static IList<NavDestination> DestinationsOn(Map map)
        {
            List<NavDestination> result;

            if (map != null && _destinationsByMap.TryGetValue(map, out result))
            {
                return result;
            }

            return new List<NavDestination>();
        }

        public static IList<NavZone> ZonesOn(Map map)
        {
            List<NavZone> result;

            if (map != null && _zonesByMap.TryGetValue(map, out result))
            {
                return result;
            }

            return new List<NavZone>();
        }

        // ---- saving and mutation ----

        /// <summary>
        /// Writes the live store back, keeping a .bak of what was there.
        ///
        /// AutoSave rotation does not cover Data/, so the .bak is the only copy of a file a
        /// mistyped command has just rewritten.
        /// </summary>
        public static bool Save(out string error)
        {
            error = null;

            string fullPath = JsonConfig.Resolve(ConfigPath);

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Copy(fullPath, fullPath + ".bak", true);
                }
            }
            catch (Exception ex)
            {
                error = "Could not write the .bak: " + ex.Message;
                return false;
            }

            return JsonConfig.TrySave(ConfigPath, _store, out error);
        }

        /// <summary>
        /// Applies a mutation to the live data, rebuilds, and saves - ROLLING THE MUTATION BACK
        /// if the save fails, so what is in memory and what is on disk can never diverge.
        ///
        /// This is why the mutating commands do not write-then-reload: a reload would rebuild
        /// the whole graph on every one of the many writes [NavRecord produces, and a failed
        /// write would leave the shard running data that is not in the file.
        /// </summary>
        public static bool ApplyAndSave(Action apply, Action undo, out string error)
        {
            apply();
            Rebuild();

            if (Save(out error))
            {
                return true;
            }

            undo();
            Rebuild();

            Log.Error("Navigation change rolled back - {0} could not be written: {1}", ConfigPath, error);
            return false;
        }

        // ---- health ----

        /// <summary>
        /// Extra data checks contributed by layers above this one.
        ///
        /// The navigation layer is Core and knows nothing about bots, but "is there actually any
        /// ore under this mine?" is a fact about navigation.json and belongs in the health check
        /// that reads navigation.json. So consumers register an auditor instead of Core reaching
        /// upward: BotWorkSites registers the work-site reach check, and anything later can add
        /// its own without this file learning what it is.
        /// </summary>
        private static readonly List<Func<IList<string>>> _auditors = new List<Func<IList<string>>>();

        public static void RegisterAuditor(Func<IList<string>> auditor)
        {
            if (auditor != null && !_auditors.Contains(auditor))
            {
                _auditors.Add(auditor);
            }
        }

        private static List<string> RunAuditors()
        {
            var found = new List<string>();

            foreach (var auditor in _auditors)
            {
                IList<string> lines;

                try
                {
                    lines = auditor();
                }
                catch (Exception ex)
                {
                    // An auditor that throws must not take the health check down with it.
                    found.Add("an auditor threw: " + ex.Message);
                    continue;
                }

                if (lines == null)
                {
                    continue;
                }

                foreach (string line in lines)
                {
                    found.Add(line);
                }
            }

            return found;
        }

        public static HealthResult BuildHealthResult()
        {
            string loaded = _lastLoadUtc.HasValue
                ? _lastLoadUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            if (_lastError != null)
            {
                return HealthResult.Fail(String.Format(
                    "{0} did not load: {1}. Running on {2} previously loaded waypoint(s), last good load {3}.",
                    ConfigPath,
                    _lastError,
                    _graph.NodeCount,
                    loaded));
            }

            if (_graph.NodeCount == 0)
            {
                return HealthResult.Warn("No waypoints configured. Last load " + loaded + ".");
            }

            string counts = String.Format(
                "{0} waypoint(s), {1} walk + {2} gate edge(s), {3} destination(s), {4} arrival(s), {5} zone(s), {6} route(s), last load {7}",
                _graph.NodeCount,
                _graph.WalkEdgeCount,
                _graph.GateEdgeCount,
                _destinations.Count,
                _store.Arrivals.Count,
                _zones.Count,
                _routes.Count,
                loaded);

            List<string> extra = RunAuditors();

            if (_dataWarnings.Count > 0 || extra.Count > 0)
            {
                var all = new List<string>(_dataWarnings);

                all.AddRange(extra);

                return HealthResult.Warn(String.Format(
                    "{0} data warning(s) - first: {1}. {2}",
                    all.Count,
                    all[0],
                    counts));
            }

            return HealthResult.Ok(counts);
        }

        // ---- helpers ----

        public static void NotifyStaff(string message)
        {
            foreach (Server.Network.NetState ns in Server.Network.NetState.Instances)
            {
                Mobile staff = ns.Mobile;

                if (staff != null && staff.AccessLevel >= AccessLevel.Counselor)
                {
                    staff.SendMessage(0x35, message);
                }
            }
        }

        private static string[] ToArray(IList<string> source)
        {
            if (source == null)
            {
                return new string[0];
            }

            var result = new string[source.Count];

            for (int i = 0; i < source.Count; i++)
            {
                result[i] = source[i];
            }

            return result;
        }

        public static bool IsLive
        {
            get { return _navLive; }
        }
    }
}
