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

                // A shape this build does not understand is FATAL rather than a warning. An
                // unknown discriminator silently treated as a rectangle would make a zone contain
                // its whole bounding box - quietly wrong over a much larger area than intended,
                // which for a restricted zone or a guard region is the worst possible failure.
                if (record.Shape != null
                    && !Insensitive.Equals(record.Shape, "rect")
                    && !Insensitive.Equals(record.Shape, "poly"))
                {
                    errors.Add("{0} has an unknown shape '{1}'; expected 'rect' or 'poly'",
                        where, record.Shape);
                }

                if (record.IsPoly)
                {
                    if (record.Vertices.Length < 6)
                    {
                        errors.Add(
                            "{0} is a poly with {1} vertex/vertices; a polygon needs at least three",
                            where,
                            record.Vertices.Length / 2);
                    }

                    // The box is the poly's index, so a vertex outside it would be unreachable:
                    // Contains rejects on the box before it ever casts a ray.
                    int[] v = record.Vertices;

                    for (int p = 0; p + 1 < v.Length; p += 2)
                    {
                        if (v[p] < record.X || v[p] >= record.X + record.Width
                            || v[p + 1] < record.Y || v[p + 1] >= record.Y + record.Height)
                        {
                            errors.Add(
                                "{0} has a vertex at {1},{2} outside its own bounds",
                                where, v[p], v[p + 1]);
                            break;
                        }
                    }
                }
                else if (!String.IsNullOrEmpty(record.Points))
                {
                    errors.Add("{0} has points but is not shape 'poly'", where);
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

        /// <summary>
        /// An optional human name for the editor to draw instead of the id. Empty means show the id.
        ///
        /// OPTIONAL, and NullValueHandling.Ignore is load-bearing rather than tidiness: JsonConfig
        /// serializes nulls by default, so without it the first save would stamp `"name": ""` onto
        /// all seventy-odd existing waypoints and turn a one-line edit into a whole-file diff.
        ///
        /// A waypoint is not a destination. Most of them are road nodes whose id ('brit-plaza-4')
        /// is the most useful thing to show, so this stays absent for the great majority and is
        /// worth setting only where a human name genuinely reads better than the id - the corridor
        /// out to a work site, say, where 'West gate road 3' says something the id does not.
        /// </summary>
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("z")]
        public int Z { get; set; }

        /// <summary>
        /// The Z the reference stored, when Adopt wrote a different one. Absent means Z is as
        /// authored or as the reference had it.
        ///
        /// uo-offline stores the water's Z for every generated dock waypoint - `uo-wp-990` at -15
        /// under a deck at -2 - and a record like that is one every audit line annotates for ever.
        /// Adopt writes the Z the walker actually stood on and keeps theirs here, so a diff against
        /// the reference file explains the change rather than looking like a typo.
        /// </summary>
        [JsonProperty("refZ", NullValueHandling = NullValueHandling.Ignore)]
        public int? RefZ { get; set; }

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

        /// <summary>
        /// Where this record came from, when it was not authored here. Absent means hand-authored.
        ///
        /// Set to `uo-offline` by the Adopt step. It exists so that a record somebody accepted
        /// from somebody else's data stays TELLABLE from one measured against this shard's map -
        /// the two have different warranties. Ours were flood-filled and audited here; an adopted
        /// one had its edges re-walked at adopt time and nothing else.
        ///
        /// Optional, and NullValueHandling.Ignore for the reason `name` has it: JsonConfig
        /// serializes nulls, so without it the first save would stamp `"source": ""` onto every
        /// existing record and turn a one-line edit into a whole-file diff.
        /// </summary>
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }

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

        /// <summary>
        /// Where this record came from, when it was not authored here. See NavWaypoint.Source.
        /// </summary>
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }

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

        /// <summary>
        /// Where this record came from, when it was not authored here. See NavWaypoint.Source.
        /// </summary>
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }

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
        /// The Z the reference stored, when Adopt wrote a different one. See NavWaypoint.RefZ.
        /// Declared right after Z because the golden fixture is the serializer's order.
        /// </summary>
        [JsonProperty("refZ", NullValueHandling = NullValueHandling.Ignore)]
        public int? RefZ { get; set; }

        /// <summary>
        /// An exclusive spot is an exact tile - no scatter - and is skipped by the picker if
        /// any mobile is already standing within a tile of it. For a guard post, where the
        /// point is that the guard stands precisely there.
        /// </summary>
        [JsonProperty("exclusive")]
        public bool Exclusive { get; set; }

        /// <summary>
        /// Take this tile EXACTLY, but do not reserve it. Optional; default false.
        ///
        /// Exactness and exclusivity are different properties, and until now the only way to buy
        /// the first was to pay for the second. That cost real capacity: brit-forge's four
        /// arrival tiles are the ones within two of both the anvil and the forge, and
        /// DefBlacksmithy.CanCraft refuses anything further, so a scattered arrival put a smith
        /// somewhere it could not work. Making all four exclusive fixed that and immediately drew
        /// a Nav.Data warning for having no shared spots left - a station that four bots could
        /// share had become one that four bots each had to themselves.
        ///
        /// So: `exact` suppresses the scatter, `exclusive` reserves the tile, and a station wants
        /// the first without the second. The occupancy rules are untouched - a non-exclusive
        /// arrival is still handed out to as many mobiles as ask for it, they simply all get the
        /// authored tile and shove for it the way they would for any crowded spot.
        /// </summary>
        /// <remarks>
        /// DefaultValueHandling, not NullValueHandling: a bool is never null, so Ignore-on-null
        /// would do nothing and every one of the sixty-odd existing arrival lines would gain
        /// `"exact":false` on the next save. Ignore-on-default omits it unless it is true, which
        /// is what keeps this a one-line addition to two records rather than a whole-file diff.
        /// </remarks>
        [JsonProperty("exact", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Exact { get; set; }

        [JsonProperty("waypoints")]
        public string WaypointIds { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        /// <summary>
        /// Where this record came from, when it was not authored here. See NavWaypoint.Source.
        /// </summary>
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }

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

        /// <summary>
        /// "rect" (the default) or "poly". The discriminator this schema was designed around.
        ///
        /// Every zone written before this existed stays valid: absent means "rect", and x/y/width
        /// /height keep their meaning. A poly reads its shape from Points and uses x/y/width
        /// /height as the BOUNDING BOX, which Contains checks first because rejecting a point
        /// outside the box costs four comparisons and rejects almost everything.
        /// </summary>
        [JsonProperty("shape", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string Shape { get; set; }

        /// <summary>
        /// A polygon's vertices as "x,y x,y ..." - a space-token string, not a JSON array.
        ///
        /// The house rule from the schema's first day: JsonConfig.SerializeCompact only collapses
        /// a container whose children are all scalars, so a nested array here would expand every
        /// zone over eight lines and break the one-record-per-line layout the whole file depends
        /// on. Same reason tags and waypoint lists are space-separated.
        /// </summary>
        [JsonProperty("points", NullValueHandling = NullValueHandling.Ignore)]
        public string Points { get; set; }

        [JsonProperty("tags")]
        public string Tags { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        [JsonConstructor]
        public NavZone()
        {
            MapName = "Trammel";
        }

        /// <summary>True when this zone is a polygon rather than a rectangle.</summary>
        [JsonIgnore]
        public bool IsPoly
        {
            get { return Shape != null && Insensitive.Equals(Shape, "poly"); }
        }

        private int[] _vertices;

        /// <summary>
        /// The polygon's vertices, flattened to x,y pairs, or null.
        ///
        /// Parsed once and cached: Contains is called from the bot tick for every zone at a bot's
        /// feet, and re-splitting a string there would be the kind of allocation that only shows
        /// up under a hundred bots.
        /// </summary>
        [JsonIgnore]
        public int[] Vertices
        {
            get
            {
                if (_vertices != null)
                {
                    return _vertices;
                }

                if (String.IsNullOrEmpty(Points))
                {
                    return _vertices = new int[0];
                }

                string[] tokens = Points.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
                var parsed = new List<int>(tokens.Length * 2);

                foreach (string token in tokens)
                {
                    int comma = token.IndexOf(',');

                    if (comma <= 0)
                    {
                        continue;
                    }

                    int x, y;

                    if (Int32.TryParse(token.Substring(0, comma), out x)
                        && Int32.TryParse(token.Substring(comma + 1), out y))
                    {
                        parsed.Add(x);
                        parsed.Add(y);
                    }
                }

                return _vertices = parsed.ToArray();
            }
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
            // The bounding box first, for a rect because it IS the answer and for a poly because
            // it rejects almost every point for four comparisons instead of a whole ray cast.
            if (x < X || x >= X + Width || y < Y || y >= Y + Height)
            {
                return false;
            }

            if (!IsPoly)
            {
                return true;
            }

            int[] v = Vertices;

            if (v.Length < 6)
            {
                // Fewer than three vertices is not a polygon. Falling back to the bounding box
                // rather than to "contains nothing", because a zone that silently stopped
                // containing anything would look exactly like a bot ignoring its work area.
                return true;
            }

            // Ray casting, counting crossings of a ray going east from the point. The tile's
            // CENTRE is tested rather than its corner, so a vertex landing exactly on a tile
            // boundary cannot make containment depend on which way a floating-point comparison
            // happens to fall.
            double px = x + 0.5;
            double py = y + 0.5;
            bool inside = false;

            for (int i = 0, j = v.Length - 2; i < v.Length; j = i, i += 2)
            {
                double ix = v[i], iy = v[i + 1];
                double jx = v[j], jy = v[j + 1];

                if ((iy > py) != (jy > py)
                    && px < (jx - ix) * (py - iy) / (jy - iy) + ix)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// The zone's area in tiles, for "which zone is smallest at this point".
        ///
        /// A polygon's bounding box is not its area, and using the box would let a large diagonal
        /// zone beat a small rectangle it overlaps. The shoelace formula, halved and absolute.
        /// </summary>
        [JsonIgnore]
        public int Area
        {
            get
            {
                if (!IsPoly)
                {
                    return Width * Height;
                }

                int[] v = Vertices;

                if (v.Length < 6)
                {
                    return Width * Height;
                }

                long sum = 0;

                for (int i = 0, j = v.Length - 2; i < v.Length; j = i, i += 2)
                {
                    sum += (long)v[j] * v[i + 1] - (long)v[i] * v[j + 1];
                }

                return (int)(Math.Abs(sum) / 2);
            }
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
