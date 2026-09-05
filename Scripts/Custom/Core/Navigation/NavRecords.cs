using System;
using System.Collections.Generic;
using System.Text;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// Helpers for the id and tag tokens used throughout navigation.json.
    ///
    /// List-valued fields are SPACE-SEPARATED STRINGS rather than JSON arrays, which is a
    /// consequence of the house one-record-per-line layout: JsonConfig.SerializeCompact only
    /// collapses a container whose children are all scalars, so a nested "tags": [...] would
    /// expand its whole record over eight lines. Tokens therefore may not contain whitespace,
    /// which IsValid enforces.
    /// </summary>
    public static class NavIds
    {
        private static readonly char[] Separator = new[] { ' ' };

        private static readonly string[] Empty = new string[0];

        /// <summary>Splits a space-separated token list. Never returns null.</summary>
        public static string[] Split(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return Empty;
            }

            return value.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        }

        public static string Join(IEnumerable<string> tokens)
        {
            if (tokens == null)
            {
                return null;
            }

            var builder = new StringBuilder();

            foreach (string token in tokens)
            {
                if (String.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(token);
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        /// <summary>
        /// A token is letters, digits, '-', '_' or '.'. The prohibition that matters is
        /// whitespace: a space inside an id would silently split it into two ids.
        /// </summary>
        public static bool IsValid(string token)
        {
            if (String.IsNullOrEmpty(token))
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];

                if (!Char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Validates every token in a space-separated list, naming the bad one.</summary>
        public static bool AllValid(string value, out string offender)
        {
            offender = null;

            foreach (string token in Split(value))
            {
                if (!IsValid(token))
                {
                    offender = token;
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>How a mobile crosses an edge.</summary>
    public enum NavEdgeKind
    {
        /// <summary>Walked. Both ends must be on the same facet.</summary>
        Walk,

        /// <summary>
        /// A moongate, teleporter or other instantaneous hop. The only edge allowed to cross
        /// facets, and the only one the route consumer has to handle specially.
        /// </summary>
        Gate
    }

    /// <summary>How an authored route is traversed.</summary>
    public enum NavRouteMode
    {
        /// <summary>Wraps from the last waypoint back to the first. Patrol loops.</summary>
        Cycle,

        /// <summary>Stops at the last waypoint. A shopkeeper's walk home.</summary>
        OneWay,

        /// <summary>Runs to the end, then back to the start, forever.</summary>
        PingPong
    }

    /// <summary>
    /// Root of Data/Custom/navigation.json.
    ///
    /// A wrapper object rather than a bare array on purpose: if a root key is ever renamed,
    /// MissingMemberHandling.Error fails the load loudly instead of silently producing an
    /// empty section.
    ///
    /// Validate() reports STRUCTURAL problems only - a blank or duplicate id, an unknown facet,
    /// a negative size - and those fail the load, keeping the live data. Problems of REFERENCE
    /// (an edge naming a waypoint that does not exist) are deliberately not fatal: they are
    /// dropped when the indexes are built and reported by the Nav.Data health check, so one
    /// typo cannot take navigation offline.
    /// </summary>
    public class NavigationStore : IValidatableConfig
    {
        /// <summary>
        /// Bumped when the shape changes incompatibly. uo-offline-server has no equivalent and
        /// migrates ad hoc; a version int costs one line and makes a migration possible.
        /// </summary>
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        [JsonProperty("costTags")]
        public List<NavCostTag> CostTags { get; set; }

        [JsonProperty("waypoints")]
        public List<NavWaypoint> Waypoints { get; set; }

        [JsonProperty("edges")]
        public List<NavEdge> Edges { get; set; }

        [JsonProperty("destinations")]
        public List<NavDestination> Destinations { get; set; }

        [JsonProperty("arrivals")]
        public List<NavArrival> Arrivals { get; set; }

        [JsonProperty("zones")]
        public List<NavZone> Zones { get; set; }

        [JsonProperty("routes")]
        public List<NavRouteDef> Routes { get; set; }

        [JsonProperty("selfTests")]
        public List<NavSelfTest> SelfTests { get; set; }

        [JsonConstructor]
        public NavigationStore()
        {
            // Applied before binding, so an absent section is an empty list rather than null.
            // A RENAMED section is still caught - MissingMemberHandling.Error rejects the
            // unknown key - so the wrapper object keeps the loud-failure property either way.
            SchemaVersion = NavigationSystem.SchemaVersion;
            CostTags = new List<NavCostTag>();
            Waypoints = new List<NavWaypoint>();
            Edges = new List<NavEdge>();
            Destinations = new List<NavDestination>();
            Arrivals = new List<NavArrival>();
            Zones = new List<NavZone>();
            Routes = new List<NavRouteDef>();
            SelfTests = new List<NavSelfTest>();
        }

        public void Validate(ConfigErrors errors)
        {
            if (SchemaVersion > NavigationSystem.SchemaVersion)
            {
                errors.Add(
                    "schemaVersion {0} is newer than this build understands ({1}).",
                    SchemaVersion,
                    NavigationSystem.SchemaVersion);
            }

            ValidateCostTags(errors);
            ValidateWaypoints(errors);
            ValidateEdges(errors);
            ValidateDestinations(errors);
            ValidateArrivals(errors);
            ValidateZones(errors);
            ValidateRoutes(errors);
            ValidateSelfTests(errors);
        }

        private void ValidateCostTags(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < CostTags.Count; i++)
            {
                NavCostTag record = CostTags[i];
                string where = String.Format("costTags[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                if (!NavIds.IsValid(record.Tag))
                {
                    errors.Add("{0} has an invalid tag '{1}'", where, record.Tag);
                }
                else if (!seen.Add(record.Tag))
                {
                    errors.Add("{0} duplicates the tag '{1}'", where, record.Tag);
                }

                if (record.Multiplier <= 0.0)
                {
                    errors.Add(
                        "{0} multiplier must be greater than zero (found {1})",
                        where,
                        record.Multiplier);
                }
            }
        }

        private void ValidateWaypoints(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Waypoints.Count; i++)
            {
                NavWaypoint record = Waypoints[i];
                string where = String.Format("waypoints[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, record.Id, seen);
                ValidateMap(errors, where, record.MapName, record.Map);
                ValidateTags(errors, where, record.Tags);

                if (record.ArrivalRange < 0)
                {
                    errors.Add(
                        "{0} arrivalRange must not be negative (found {1})",
                        where,
                        record.ArrivalRange);
                }
            }
        }

        private void ValidateEdges(ConfigErrors errors)
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                NavEdge record = Edges[i];
                string where = String.Format("edges[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                if (!NavIds.IsValid(record.From))
                {
                    errors.Add("{0} has an invalid 'from' id '{1}'", where, record.From);
                }

                if (!NavIds.IsValid(record.To))
                {
                    errors.Add("{0} has an invalid 'to' id '{1}'", where, record.To);
                }

                if (record.From != null && Insensitive.Equals(record.From, record.To))
                {
                    errors.Add("{0} links '{1}' to itself", where, record.From);
                }

                if (!record.KindIsKnown)
                {
                    errors.Add("{0} kind '{1}' is not 'walk' or 'gate'", where, record.KindName);
                }

                ValidateTags(errors, where, record.Tags);
            }
        }

        private void ValidateDestinations(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Destinations.Count; i++)
            {
                NavDestination record = Destinations[i];
                string where = String.Format("destinations[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, record.Id, seen);
                ValidateMap(errors, where, record.MapName, record.Map);
                ValidateTags(errors, where, record.Tags);
                ValidateTags(errors, where, record.WaypointIds);

                // A destination with no type cannot be filtered for, which is most of what
                // callers ask of it. uo-offline-server's flat enum conflated category and
                // mechanism; ours is a free token deliberately, but it must be present.
                if (!NavIds.IsValid(record.Type))
                {
                    errors.Add("{0} has an invalid or missing type '{1}'", where, record.Type);
                }

                if (String.IsNullOrWhiteSpace(record.Name))
                {
                    errors.Add("{0} has no display name", where);
                }
            }
        }

        private void ValidateArrivals(ConfigErrors errors)
        {
            for (int i = 0; i < Arrivals.Count; i++)
            {
                NavArrival record = Arrivals[i];
                string where = String.Format("arrivals[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                if (!NavIds.IsValid(record.DestinationId))
                {
                    errors.Add("{0} has an invalid destination id '{1}'", where, record.DestinationId);
                }

                ValidateTags(errors, where, record.WaypointIds);
            }
        }

        private void ValidateZones(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Zones.Count; i++)
            {
                NavZone record = Zones[i];
                string where = String.Format("zones[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, record.Id, seen);
                ValidateMap(errors, where, record.MapName, record.Map);
                ValidateTags(errors, where, record.Tags);

                if (record.Width <= 0 || record.Height <= 0)
                {
                    errors.Add(
                        "{0} bounds must have a positive width and height (found {1}x{2})",
                        where,
                        record.Width,
                        record.Height);
                }
            }
        }

        private void ValidateRoutes(ConfigErrors errors)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Routes.Count; i++)
            {
                NavRouteDef record = Routes[i];
                string where = String.Format("routes[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                ValidateId(errors, where, record.Id, seen);
                ValidateMap(errors, where, record.MapName, record.Map);
                ValidateTags(errors, where, record.WaypointIds);

                if (!record.ModeIsKnown)
                {
                    errors.Add(
                        "{0} mode '{1}' is not 'cycle', 'oneway' or 'pingpong'",
                        where,
                        record.ModeName);
                }

                if (NavIds.Split(record.WaypointIds).Length < 2)
                {
                    errors.Add("{0} needs at least two waypoints", where);
                }
            }
        }

        private void ValidateSelfTests(ConfigErrors errors)
        {
            for (int i = 0; i < SelfTests.Count; i++)
            {
                NavSelfTest record = SelfTests[i];
                string where = String.Format("selfTests[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                if (!NavIds.IsValid(record.From))
                {
                    errors.Add("{0} has an invalid 'from' id '{1}'", where, record.From);
                }

                if (!NavIds.IsValid(record.To))
                {
                    errors.Add("{0} has an invalid 'to' id '{1}'", where, record.To);
                }
            }
        }

        private static void ValidateId(ConfigErrors errors, string where, string id, HashSet<string> seen)
        {
            if (!NavIds.IsValid(id))
            {
                errors.Add(
                    "{0} has an invalid or missing id '{1}' (letters, digits, '-', '_' and '.' only)",
                    where,
                    id);
                return;
            }

            if (!seen.Add(id))
            {
                errors.Add("{0} duplicates the id '{1}'", where, id);
            }
        }

        private static void ValidateMap(ConfigErrors errors, string where, string mapName, Map map)
        {
            if (map == null)
            {
                errors.Add(
                    "{0} map '{1}' is not a valid facet. Valid: {2}",
                    where,
                    mapName,
                    JsonConfig.ValidMapNames());
            }
        }

        private static void ValidateTags(ConfigErrors errors, string where, string tags)
        {
            string offender;

            if (!NavIds.AllValid(tags, out offender))
            {
                errors.Add("{0} contains the invalid token '{1}'", where, offender);
            }
        }
    }

    /// <summary>A cost multiplier applied to every edge carrying this tag.</summary>
    public class NavCostTag
    {
        [JsonProperty("tag")]
        public string Tag { get; set; }

        [JsonProperty("multiplier")]
        public double Multiplier { get; set; }

        [JsonConstructor]
        public NavCostTag()
        {
            Multiplier = 1.0;
        }

        public override string ToString()
        {
            return String.Format("{0} x{1}", Tag, Multiplier);
        }
    }

    /// <summary>
    /// One node of the travel graph.
    ///
    /// Z IS ADVISORY. The pathing goal always uses map.GetAverageZ(x, y), because a route node's
    /// authored Z that is a little wrong makes the hop time out and be skipped rather than
    /// positioning the mobile wrongly. Z is used for debug markers and for disambiguating
    /// levels, nothing else.
    /// </summary>
    public class NavWaypoint
    {
        private Map _map;
        private bool _mapResolved;

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("z")]
        public int Z { get; set; }

        /// <summary>
        /// Per-waypoint arrival tolerance in tiles; 0 means "use the walker's default".
        ///
        /// Adopted from uo-offline-server, name and semantics unchanged, because their reason
        /// is specific and hard-won: a doorway or entrance waypoint at an unusual Z needs the
        /// mobile to step onto the exact tile, so its tolerance must be 1 rather than 3.
        /// </summary>
        [JsonProperty("arrivalRange")]
        public int ArrivalRange { get; set; }

        [JsonProperty("tags")]
        public string Tags { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavWaypoint()
        {
            MapName = "Trammel";
        }

        public NavWaypoint(string id, Map map, Point3D location)
        {
            Id = id;
            MapName = map != null ? map.Name : null;
            X = location.X;
            Y = location.Y;
            Z = location.Z;
        }

        /// <summary>Null when the facet name is unrecognised. A getter must never throw.</summary>
        [JsonIgnore]
        public Map Map
        {
            get
            {
                if (!_mapResolved)
                {
                    Map map;
                    _map = JsonConfig.TryParseMap(MapName, out map) ? map : null;
                    _mapResolved = true;
                }

                return _map;
            }
        }

        [JsonIgnore]
        public Point3D Location
        {
            get { return new Point3D(X, Y, Z); }
        }

        /// <summary>Parsed once by NavigationSystem after a successful load.</summary>
        [JsonIgnore]
        public string[] TagList { get; private set; }

        public void Bind()
        {
            TagList = NavIds.Split(Tags);
        }

        public override string ToString()
        {
            return String.Format("{0} ({1} {2},{3},{4})", Id, MapName, X, Y, Z);
        }
    }

    /// <summary>
    /// One explicit, one-way-declared link between two waypoints. The graph symmetrises walk
    /// edges when it is built.
    ///
    /// Edges are their OWN records rather than a "connects" list on the waypoint, for two
    /// reasons: they must never be derived from proximity (which would silently link two
    /// waypoints through a building), and an edge needs to carry its own kind and tags.
    /// </summary>
    public class NavEdge
    {
        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("to")]
        public string To { get; set; }

        [JsonProperty("kind")]
        public string KindName { get; set; }

        [JsonProperty("tags")]
        public string Tags { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavEdge()
        {
            KindName = "walk";
        }

        public NavEdge(string from, string to)
        {
            From = from;
            To = to;
            KindName = "walk";
        }

        [JsonIgnore]
        public bool KindIsKnown
        {
            get
            {
                return Insensitive.Equals(KindName, "walk") || Insensitive.Equals(KindName, "gate");
            }
        }

        [JsonIgnore]
        public NavEdgeKind Kind
        {
            get { return Insensitive.Equals(KindName, "gate") ? NavEdgeKind.Gate : NavEdgeKind.Walk; }
        }

        [JsonIgnore]
        public string[] TagList { get; private set; }

        public void Bind()
        {
            TagList = NavIds.Split(Tags);
        }

        public override string ToString()
        {
            return String.Format("{0} -> {1} ({2})", From, To, KindName);
        }
    }

    /// <summary>
    /// A place worth going to. Its own x/y is the centre of the thing; where a mobile actually
    /// STANDS is an arrival record, which is a separate concept on purpose.
    /// </summary>
    public class NavDestination
    {
        private Map _map;
        private bool _mapResolved;

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// A free token - "bank", "tavern", "shop", "home", "gate", "work", "wander", "guard".
        /// Deliberately not an enum: uo-offline-server's flat 30-member DestinationType ended
        /// up conflating category, mechanism and resource, so every consumer needed
        /// "if (type is A or B or C)" guards. Category here, everything else in tags.
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("z")]
        public int Z { get; set; }

        [JsonProperty("tags")]
        public string Tags { get; set; }

        /// <summary>
        /// Approach waypoints, space separated. Plural on purpose: approaching from the north
        /// and from the south are genuinely different routes. uo-offline-server persisted one
        /// denormalised "NearestWaypoint" and had to build an audit command to catch it going
        /// stale.
        /// </summary>
        [JsonProperty("waypoints")]
        public string WaypointIds { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavDestination()
        {
            MapName = "Trammel";
        }

        [JsonIgnore]
        public Map Map
        {
            get
            {
                if (!_mapResolved)
                {
                    Map map;
                    _map = JsonConfig.TryParseMap(MapName, out map) ? map : null;
                    _mapResolved = true;
                }

                return _map;
            }
        }

        [JsonIgnore]
        public Point3D Location
        {
            get { return new Point3D(X, Y, Z); }
        }

        [JsonIgnore]
        public string[] TagList { get; private set; }

        [JsonIgnore]
        public string[] WaypointList { get; private set; }

        /// <summary>Populated when the indexes are built; never null after a successful load.</summary>
        [JsonIgnore]
        public List<NavArrival> ArrivalList { get; private set; }

        public void Bind()
        {
            TagList = NavIds.Split(Tags);
            WaypointList = NavIds.Split(WaypointIds);
            ArrivalList = new List<NavArrival>();
        }

        public bool HasTag(string tag)
        {
            if (TagList == null)
            {
                return false;
            }

            for (int i = 0; i < TagList.Length; i++)
            {
                if (Insensitive.Equals(TagList[i], tag))
                {
                    return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            return String.Format("{0} '{1}' [{2}] ({3} {4},{5},{6})", Id, Name, Type, MapName, X, Y, Z);
        }
    }

    /// <summary>
    /// A specific standable tile at a destination.
    ///
    /// Adopted wholesale from uo-offline-server, which is the strongest idea in their tree: a
    /// destination is a THING, an arrival is a TILE YOU CAN ACTUALLY ROUTE TO, and several of
    /// them is most of the anti-crowding answer for free.
    ///
    /// Flattened into its own top-level array rather than nested inside the destination purely
    /// so each arrival lands on one line under SerializeCompact.
    /// </summary>
    public class NavArrival
    {
        [JsonProperty("destination")]
        public string DestinationId { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("z")]
        public int Z { get; set; }

        /// <summary>
        /// An exclusive spot is an exact tile - no scatter - and is skipped by the picker if
        /// any mobile is already standing within a tile of it. For a guard post, where the
        /// point is that the guard stands precisely there.
        /// </summary>
        [JsonProperty("exclusive")]
        public bool Exclusive { get; set; }

        [JsonProperty("waypoints")]
        public string WaypointIds { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavArrival()
        {
        }

        public NavArrival(string destinationId, Point3D location, bool exclusive)
        {
            DestinationId = destinationId;
            X = location.X;
            Y = location.Y;
            Z = location.Z;
            Exclusive = exclusive;
        }

        [JsonIgnore]
        public Point3D Location
        {
            get { return new Point3D(X, Y, Z); }
        }

        [JsonIgnore]
        public string[] WaypointList { get; private set; }

        public void Bind()
        {
            WaypointList = NavIds.Split(WaypointIds);
        }

        public override string ToString()
        {
            return String.Format("{0} @ {1},{2},{3}{4}", DestinationId, X, Y, Z, Exclusive ? " (exclusive)" : "");
        }
    }

    /// <summary>
    /// A tagged area. Rectangles only for now.
    ///
    /// Polygons later without a migration: add "shape":"poly" plus a "points" token list
    /// alongside x/y/width/height, default shape to "rect", and make Contains a switch. Every
    /// record written today stays valid.
    /// </summary>
    public class NavZone
    {
        private Map _map;
        private bool _mapResolved;

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("tags")]
        public string Tags { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavZone()
        {
            MapName = "Trammel";
        }

        [JsonIgnore]
        public Map Map
        {
            get
            {
                if (!_mapResolved)
                {
                    Map map;
                    _map = JsonConfig.TryParseMap(MapName, out map) ? map : null;
                    _mapResolved = true;
                }

                return _map;
            }
        }

        [JsonIgnore]
        public Rectangle2D Bounds
        {
            get { return new Rectangle2D(X, Y, Width, Height); }
        }

        [JsonIgnore]
        public string[] TagList { get; private set; }

        /// <summary>The first tag, used to name auto-generated waypoint ids. May be null.</summary>
        [JsonIgnore]
        public string PrimaryTag
        {
            get { return TagList != null && TagList.Length > 0 ? TagList[0] : null; }
        }

        public void Bind()
        {
            TagList = NavIds.Split(Tags);
        }

        public bool Contains(int x, int y)
        {
            return x >= X && x < X + Width && y >= Y && y < Y + Height;
        }

        public bool HasTag(string tag)
        {
            if (TagList == null)
            {
                return false;
            }

            for (int i = 0; i < TagList.Length; i++)
            {
                if (Insensitive.Equals(TagList[i], tag))
                {
                    return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            return String.Format("{0}: {1}, {2}x{3} at {4},{5}", Id, MapName, Width, Height, X, Y);
        }
    }

    /// <summary>
    /// An AUTHORED ordered waypoint sequence - a patrol loop, a shopkeeper's walk home.
    ///
    /// This is the thing a shortest-path search cannot invent, and it is why routes survive as
    /// data even though Nav.TryRoute computes A -> B. uo-offline-server has no equivalent: they
    /// only ever search.
    /// </summary>
    public class NavRouteDef
    {
        private Map _map;
        private bool _mapResolved;

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("mode")]
        public string ModeName { get; set; }

        [JsonProperty("waypoints")]
        public string WaypointIds { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavRouteDef()
        {
            MapName = "Trammel";
            ModeName = "cycle";
        }

        [JsonIgnore]
        public Map Map
        {
            get
            {
                if (!_mapResolved)
                {
                    Map map;
                    _map = JsonConfig.TryParseMap(MapName, out map) ? map : null;
                    _mapResolved = true;
                }

                return _map;
            }
        }

        [JsonIgnore]
        public bool ModeIsKnown
        {
            get
            {
                return Insensitive.Equals(ModeName, "cycle")
                    || Insensitive.Equals(ModeName, "oneway")
                    || Insensitive.Equals(ModeName, "pingpong");
            }
        }

        [JsonIgnore]
        public NavRouteMode Mode
        {
            get
            {
                if (Insensitive.Equals(ModeName, "oneway"))
                {
                    return NavRouteMode.OneWay;
                }

                if (Insensitive.Equals(ModeName, "pingpong"))
                {
                    return NavRouteMode.PingPong;
                }

                return NavRouteMode.Cycle;
            }
        }

        [JsonIgnore]
        public string[] WaypointList { get; private set; }

        public void Bind()
        {
            WaypointList = NavIds.Split(WaypointIds);
        }

        public override string ToString()
        {
            return String.Format("{0} ({1}, {2}, {3} points)", Id, MapName, ModeName, NavIds.Split(WaypointIds).Length);
        }
    }

    /// <summary>
    /// A canary route pair. Nav.Data warns if it cannot be routed.
    ///
    /// This is what makes routing verifiable headlessly: ServUO's console cannot invoke staff
    /// commands, so a boot-time health check is the only way to prove the graph still connects
    /// two known places without a client.
    /// </summary>
    public class NavSelfTest
    {
        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("to")]
        public string To { get; set; }

        public override string ToString()
        {
            return String.Format("{0} -> {1}", From, To);
        }
    }
}
