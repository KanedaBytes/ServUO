using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Adopting a region of uo-offline's data into ours — as a PROPOSAL, never as a write.
    ///
    /// Nothing in here touches `Data/Custom/navigation.json`. It reads the reference file, works
    /// out what could be adopted, walks every edge, and writes the answer to
    /// `Data/Live/nav-adopt.json`. The editor draws that as unsaved records and the author accepts
    /// it through the ordinary save path, which is the same review a hand-drawn corridor gets.
    /// That separation is the whole safety property: this file cannot damage the nav data because
    /// it cannot write the nav data.
    ///
    /// THREE THINGS IT REFUSES TO DO.
    ///
    /// 1. **It never proposes over ground we have authored.** The authored region is the union of
    ///    our nav-zone rects and a radius around every existing waypoint — derived, not a Britain
    ///    rectangle somebody typed, so it holds for the next town authored by hand. 158 of their
    ///    waypoints sit inside Britain where we have 121 of our own, and merging the two would be
    ///    a second road network laid over the first.
    /// 2. **A failed edge is not proposed.** Its waypoints still are; the edge itself is reported
    ///    with the walker's reason and excluded from what can be accepted. An edge nothing could
    ///    walk is exactly the record that strands a bot, and writing it "for the author to fix
    ///    later" is how it gets forgotten.
    /// 3. **It never re-derives an id.** Reference ids are namespaced `uo-` and five of them carry
    ///    a collision suffix that does not match the name beside it; copying the `id` field is the
    ///    only thing that keeps an adopted edge pointing at the record it named.
    ///
    /// COST. Their edges were authored against a 38-tile leg cap and 56% of them are longer than
    /// `Custom.NavHopMaxTiles`, so most of an adopted region has to be re-walked with
    /// `NavCorridor.TryPath` and subdivided — a real flood-fill each. That is minutes of game
    /// thread for a large region, which is why the work is chunked through `LoopQueue` and reports
    /// progress rather than running to completion inside one tick.
    /// </summary>
    public static class NavAdopt
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>The converted reference. Authoring input; the shard never loads it as data.</summary>
        public const string ReferencePath = "Data/Custom/reference/uo-offline-nav.trammel.json";

        /// <summary>Where the proposal is written for the editor to draw.</summary>
        public const string ProposalPath = "Data/Live/nav-adopt.json";

        /// <summary>Stamped on every adopted record, so it stays tellable from a hand-authored one.</summary>
        public const string SourceTag = "uo-offline";

        /// <summary>
        /// How close to an existing waypoint counts as ground we have already authored.
        ///
        /// The hop cap rather than a number of its own: inside it, an adopted waypoint is close
        /// enough to one of ours that the two are alternative names for the same piece of road,
        /// and the graph wants a link rather than a twin. It is also the radius within which our
        /// own corridor tool reuses a waypoint instead of minting one.
        /// </summary>
        public static int AuthoredRadius
        {
            get { return NavigationSystem.HopMaxTiles; }
        }

        /// <summary>
        /// How far a join may reach to find one of our waypoints to land on.
        ///
        /// The hop cap: a join is an ordinary edge once it is written, so it has to be one the
        /// walker can plan. Beyond this there is nothing to join to and the edge is not proposed.
        /// </summary>
        public static int JoinReach
        {
            get { return NavigationSystem.HopMaxTiles; }
        }

        /// <summary>Edges walked per LoopQueue pass. Each is a flood-fill; this is the budget.</summary>
        private const int EdgesPerPass = 4;

        private static bool _running;

        public static bool Running
        {
            get { return _running; }
        }

        /// <summary>
        /// Start an adopt over a region. Returns false when one is already running.
        ///
        /// The work happens across later ticks; this call only sets it up and writes the first
        /// progress record, so the request poller answers immediately rather than holding the game
        /// thread for the length of a flood-fill per edge.
        /// </summary>
        public static bool TryStart(Map map, Rectangle2D region, out string error)
        {
            error = null;

            if (_running)
            {
                error = "an adopt is already running";
                return false;
            }

            if (map == null || map == Map.Internal)
            {
                error = "no facet";
                return false;
            }

            NavigationStore reference;
            IList<string> errors;

            if (!JsonConfig.TryLoad(ReferencePath, out reference, out errors) || reference == null)
            {
                error = errors != null && errors.Count > 0
                    ? errors[0]
                    : "the reference file could not be read";
                return false;
            }

            var job = new Job(map, region, reference);

            if (job.Total == 0 && job.Waypoints.Count == 0)
            {
                error = "nothing in that region to adopt";
                Write(job);
                return false;
            }

            _running = true;

            Write(job);
            LoopQueue.Post(() => Step(job));

            Log.Info(
                "Adopt started: {0} waypoint(s), {1} edge(s) to walk, {2} skipped as already authored.",
                job.Waypoints.Count, job.Total, job.SkippedAuthored);

            return true;
        }

        /// <summary>One pass: walk a few edges, write progress, and queue the next.</summary>
        private static void Step(Job job)
        {
            try
            {
                for (int i = 0; i < EdgesPerPass && job.Next < job.Pending.Count; i++)
                {
                    job.Walk(job.Pending[job.Next++]);
                }

                Write(job);

                if (job.Next < job.Pending.Count)
                {
                    LoopQueue.Post(() => Step(job));
                    return;
                }

                job.FindIslands();

                _running = false;

                Write(job);

                Log.Info(
                    "Adopt finished: {0} waypoint(s), {1} edge(s) proposed, {2} failed, {3} island(s).",
                    job.Waypoints.Count, job.Edges.Count, job.Failures.Count, job.Islands.Count);
            }
            catch (Exception ex)
            {
                _running = false;
                Log.Error(ex, "Adopt threw and was stopped.");
            }
        }

        /// <summary>
        /// The whole run: what was selected, what has been walked, and what came of it.
        ///
        /// A class rather than a pile of statics because the work spans ticks and an abandoned run
        /// has to be collectable. Nothing here is touched off the game thread.
        /// </summary>
        private sealed class Job
        {
            public readonly Map Map;
            public readonly Rectangle2D Region;

            /// <summary>Reference waypoints being adopted, by id.</summary>
            public readonly Dictionary<string, NavWaypoint> Waypoints =
                new Dictionary<string, NavWaypoint>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Extra waypoints minted to subdivide an over-cap hop.</summary>
            public readonly List<NavWaypoint> Subdivisions = new List<NavWaypoint>();

            public readonly List<NavEdge> Edges = new List<NavEdge>();
            public readonly List<NavDestination> Destinations = new List<NavDestination>();
            public readonly List<NavArrival> Arrivals = new List<NavArrival>();

            public readonly List<PendingEdge> Pending = new List<PendingEdge>();
            public readonly List<FailedEdge> Failures = new List<FailedEdge>();

            /// <summary>Proposed edges that land on an existing waypoint, as "from&gt;to".</summary>
            public readonly HashSet<string> Joins =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Proposed waypoints no surviving edge reaches. Accept drops these.</summary>
            public readonly List<string> Stranded = new List<string>();
            public readonly List<string> Islands = new List<string>();

            public int Next;
            public int SkippedAuthored;
            public int SkippedRegion;
            public int SkippedNoArrival;
            public int Links;

            public int Total
            {
                get { return Pending.Count; }
            }

            /// <summary>Every id already spoken for: the reference's, and our own graph's.</summary>
            private readonly HashSet<string> _taken =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Job(Map map, Rectangle2D region, NavigationStore reference)
            {
                Map = map;
                Region = region;

                foreach (NavWaypoint waypoint in reference.Waypoints ?? new List<NavWaypoint>())
                {
                    _taken.Add(waypoint.Id);
                }

                foreach (NavWaypoint waypoint in NavigationSystem.Graph.Nodes)
                {
                    _taken.Add(waypoint.Id);
                }

                Select(reference);
            }

            /// <summary>
            /// What is in the box, minus what we have already authored and what Adopt refuses.
            /// </summary>
            private void Select(NavigationStore reference)
            {
                var authored = new AuthoredRegion(Map);

                foreach (NavWaypoint waypoint in reference.Waypoints ?? new List<NavWaypoint>())
                {
                    if (!Insensitive.Equals(waypoint.MapName, Map.Name)
                        || !Region.Contains(new Point2D(waypoint.X, waypoint.Y)))
                    {
                        continue;
                    }

                    // Dungeon and Lost Lands wait for their own steps: nothing on this shard can
                    // reach them, and adopting a road to nowhere is a graph that warns for ever.
                    if (Refused(waypoint.Tags))
                    {
                        SkippedRegion++;
                        continue;
                    }

                    if (authored.Contains(waypoint.X, waypoint.Y))
                    {
                        SkippedAuthored++;
                        continue;
                    }

                    Waypoints[waypoint.Id] = waypoint;
                }

                var byId = new Dictionary<string, NavWaypoint>(StringComparer.OrdinalIgnoreCase);

                foreach (NavWaypoint waypoint in reference.Waypoints ?? new List<NavWaypoint>())
                {
                    byId[waypoint.Id] = waypoint;
                }

                foreach (NavEdge edge in reference.Edges ?? new List<NavEdge>())
                {
                    bool from = Waypoints.ContainsKey(edge.From);
                    bool to = Waypoints.ContainsKey(edge.To);

                    if (!from && !to)
                    {
                        continue;
                    }

                    var pending = new PendingEdge { Edge = edge, FromId = edge.From, ToId = edge.To };

                    // AN EDGE WITH ONE END IN GROUND WE HAVE AUTHORED BECOMES A JOIN.
                    //
                    // Skipping authored ground keeps our records safe, but on its own it also
                    // guarantees the adopted region is an island - the road to Trinsic is exactly
                    // the edge whose Britain end we refused. Adding an edge ONTO an existing
                    // waypoint is additive: Britain's own record is not touched, it simply gains
                    // a neighbour. So the endpoint is mapped to our nearest waypoint and the edge
                    // is walked like any other.
                    if (from != to)
                    {
                        string outsideId = from ? edge.To : edge.From;
                        NavWaypoint outside = byId.ContainsKey(outsideId) ? byId[outsideId] : null;

                        if (outside == null || !authored.Contains(outside.X, outside.Y))
                        {
                            continue;
                        }

                        NavWaypoint ours = NavigationSystem.Graph.Nearest(
                            new Point3D(outside.X, outside.Y, outside.Z), Map, JoinReach);

                        if (ours == null)
                        {
                            // Inside a zone we authored but with no waypoint near enough to hang
                            // an edge on. Nothing to join to, so there is nothing to propose.
                            SkippedAuthored++;
                            continue;
                        }

                        if (from)
                        {
                            pending.ToId = ours.Id;
                        }
                        else
                        {
                            pending.FromId = ours.Id;
                        }

                        pending.IsJoin = true;
                        pending.OurId = ours.Id;
                        Links++;
                    }

                    Pending.Add(pending);
                }

                foreach (NavDestination destination in reference.Destinations ?? new List<NavDestination>())
                {
                    if (!Insensitive.Equals(destination.MapName, Map.Name)
                        || !Region.Contains(new Point2D(destination.X, destination.Y))
                        || Refused(destination.Tags))
                    {
                        continue;
                    }

                    if (authored.Contains(destination.X, destination.Y))
                    {
                        SkippedAuthored++;
                        continue;
                    }

                    // A destination with no arrival is one a bot can be sent to and cannot stand
                    // at, and Nav.Data warns about exactly that. Four of theirs are like this on
                    // the overworld and all four duplicate a better-covered record nearby.
                    List<NavArrival> mine = ArrivalsFor(reference, destination.Id);

                    if (mine.Count == 0)
                    {
                        SkippedNoArrival++;
                        continue;
                    }

                    Destinations.Add(destination);
                    Arrivals.AddRange(mine);
                }
            }

            private static List<NavArrival> ArrivalsFor(NavigationStore reference, string destinationId)
            {
                var found = new List<NavArrival>();

                foreach (NavArrival arrival in reference.Arrivals ?? new List<NavArrival>())
                {
                    if (Insensitive.Equals(arrival.DestinationId, destinationId))
                    {
                        found.Add(arrival);
                    }
                }

                return found;
            }

            private static bool Refused(string tags)
            {
                if (String.IsNullOrEmpty(tags))
                {
                    return false;
                }

                string[] tokens = tags.Split(' ');

                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i] == "dungeon" || tokens[i] == "lostlands")
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Walk one reference edge and turn it into hops our walker can plan.
            ///
            /// The whole point of Adopt. Their edge is a straight assertion that two points are
            /// connected; ours has to be a hop the engine's own pathfinder can follow, under the
            /// cap. So the edge is re-walked with the same flood the corridor tool uses, and the
            /// walked path is subdivided at the cap rather than trusted at its authored length.
            /// </summary>
            public void Walk(PendingEdge pending)
            {
                NavWaypoint from = Resolve(pending.FromId);
                NavWaypoint to = Resolve(pending.ToId);

                if (from == null || to == null)
                {
                    Failures.Add(new FailedEdge
                    {
                        From = pending.FromId,
                        To = pending.ToId,
                        Reason = "one end is not in the proposal",
                        FromX = from == null ? 0 : from.X,
                        FromY = from == null ? 0 : from.Y,
                        ToX = to == null ? 0 : to.X,
                        ToY = to == null ? 0 : to.Y
                    });
                    return;
                }

                var start = new Point3D(from.X, from.Y, from.Z);
                var goal = new Point3D(to.X, to.Y, to.Z);

                List<Point3D> path;
                string error;

                if (!NavCorridor.TryPath(Map, start, goal, out path, out error) || path.Count < 2)
                {
                    Failures.Add(new FailedEdge
                    {
                        From = pending.FromId,
                        To = pending.ToId,
                        Reason = error ?? "no route",
                        FromX = from.X,
                        FromY = from.Y,
                        ToX = to.X,
                        ToY = to.Y
                    });
                    return;
                }

                Subdivide(pending, from, to, path);
            }

            /// <summary>Cut a walked path into hops no longer than the cap, minting waypoints.</summary>
            private void Subdivide(PendingEdge pending, NavWaypoint from, NavWaypoint to, List<Point3D> path)
            {
                int cap = NavigationSystem.HopMaxTiles;

                string previous = from.Id;
                Point3D anchor = new Point3D(from.X, from.Y, from.Z);
                int minted = 0;

                for (int i = 1; i < path.Count; i++)
                {
                    Point3D at = path[i];
                    bool last = i == path.Count - 1;

                    int span = Math.Max(Math.Abs(at.X - anchor.X), Math.Abs(at.Y - anchor.Y));

                    if (!last && span < cap)
                    {
                        continue;
                    }

                    string next;

                    if (last)
                    {
                        next = to.Id;
                    }
                    else
                    {
                        minted++;
                        next = MintId(from.Id);

                        Subdivisions.Add(new NavWaypoint
                        {
                            Id = next,
                            MapName = Map.Name,
                            X = at.X,
                            Y = at.Y,
                            Z = at.Z,
                            ArrivalRange = 0,
                            Tags = "road",
                            Source = SourceTag
                        });
                    }

                    Edges.Add(new NavEdge
                    {
                        From = previous,
                        To = next,
                        KindName = "walk",
                        Tags = pending.Edge.Tags ?? "",
                        Source = SourceTag
                    });

                    // The join is the hop that touches OUR waypoint - which may be either end,
                    // depending on which way the reference authored the edge. Marking the far end
                    // instead would point the author at the one hop that changes nothing of ours.
                    if (pending.IsJoin
                        && (Insensitive.Equals(previous, pending.OurId)
                            || Insensitive.Equals(next, pending.OurId)))
                    {
                        Joins.Add(String.Format("{0}>{1}", previous, next));
                    }

                    previous = next;
                    anchor = at;
                }
            }

            /**
             * A free id for a waypoint minted to split an over-cap hop.
             *
             * Counted, not hashed. The first version derived it from `edge.To.GetHashCode()`,
             * which is not guaranteed stable between processes - so re-running the same adopt
             * could produce a proposal with different ids for the same road, and a proposal whose
             * ids move is one nobody can review by diffing it.
             *
             * Seeded with the reference and with our own graph, so a minted id cannot land on
             * either. The `uo-` prefix it inherits from its parent already keeps it clear of
             * hand-authored records.
             */
            private string MintId(string parent)
            {
                // Namespaced even when the parent is one of OURS. A subdivision minted while
                // walking a join hangs off `brit-gate-w`, and `brit-gate-w-s2` reads as a record
                // somebody authored here - which is the one thing the uo- prefix exists to stop.
                string stem = parent.StartsWith("uo-", StringComparison.OrdinalIgnoreCase)
                    ? parent
                    : "uo-" + parent;

                for (int n = 1; ; n++)
                {
                    string candidate = String.Format("{0}-s{1}", stem, n);

                    if (!_taken.Contains(candidate))
                    {
                        _taken.Add(candidate);
                        return candidate;
                    }
                }
            }

            /// <summary>
            /// Which proposed waypoints cannot reach the graph we already have.
            ///
            /// Run on the PROPOSAL, before it is offered, because the alternative is finding out
            /// at the next boot: that is exactly how the west road shipped as a 43-waypoint island
            /// with a mine on it, pathing perfectly and reachable by nothing. A failed edge that
            /// was excluded above is precisely what disconnects a road, so this is the check that
            /// tells the author what excluding it cost.
            ///
            /// An adopt that touches nothing of ours is legitimately an island for now - a far
            /// town adopted before the road to it - so this reports rather than refuses.
            /// </summary>
            public void FindIslands()
            {
                var links = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Edges.Count; i++)
                {
                    Add(links, Edges[i].From, Edges[i].To);
                    Add(links, Edges[i].To, Edges[i].From);
                }

                // Anything already on our graph is the mainland by definition.
                var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();

                foreach (KeyValuePair<string, List<string>> pair in links)
                {
                    if (NavigationSystem.Graph.Node(pair.Key) != null)
                    {
                        reached.Add(pair.Key);
                        queue.Enqueue(pair.Key);
                    }
                }

                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    List<string> neighbours;

                    if (!links.TryGetValue(current, out neighbours))
                    {
                        continue;
                    }

                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        if (reached.Add(neighbours[i]))
                        {
                            queue.Enqueue(neighbours[i]);
                        }
                    }
                }

                // A waypoint no surviving edge even mentions is worse than merely cut off: every
                // edge it had failed to walk, so it is a point in space with no road at all.
                // Accept drops these rather than writing a record nothing can reach - which is the
                // shape of the fault the west road shipped with.
                foreach (string id in Waypoints.Keys)
                {
                    if (!links.ContainsKey(id))
                    {
                        Stranded.Add(id);
                    }
                }

                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    if (!links.ContainsKey(Subdivisions[i].Id))
                    {
                        Stranded.Add(Subdivisions[i].Id);
                    }
                }

                if (Stranded.Count > 0)
                {
                    Islands.Add(String.Format(
                        "{0} proposed waypoint(s) have no surviving edge at all - every edge they "
                        + "had failed to walk. Accept drops them: {1}",
                        Stranded.Count, Name(Stranded)));
                }

                // And the softer case: connected to each other, but not to anything we already
                // have. Legitimate for a far town adopted before the road to it, so it is
                // reported rather than refused.
                var cutOff = new List<string>();

                foreach (string id in Waypoints.Keys)
                {
                    if (!reached.Contains(id) && !Stranded.Contains(id))
                    {
                        cutOff.Add(id);
                    }
                }

                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    string id = Subdivisions[i].Id;

                    if (!reached.Contains(id) && !Stranded.Contains(id))
                    {
                        cutOff.Add(id);
                    }
                }

                if (cutOff.Count == 0)
                {
                    return;
                }

                Islands.Add(String.Format(
                    "{0} of {1} proposed waypoint(s) cannot reach the existing graph{2}: {3}",
                    cutOff.Count,
                    Waypoints.Count + Subdivisions.Count,
                    Links == 0 ? " (this region touches nothing already authored)" : "",
                    Name(cutOff)));
            }

            private static void Add(Dictionary<string, List<string>> links, string from, string to)
            {
                List<string> list;

                if (!links.TryGetValue(from, out list))
                {
                    list = new List<string>();
                    links[from] = list;
                }

                list.Add(to);
            }

            private static string Name(List<string> ids)
            {
                int shown = Math.Min(ids.Count, 6);
                var names = new string[shown];

                for (int i = 0; i < shown; i++)
                {
                    names[i] = String.Format("'{0}'", ids[i]);
                }

                string joined = String.Join(", ", names);

                return ids.Count > shown
                    ? String.Format("{0} and {1} more", joined, ids.Count - shown)
                    : joined;
            }

            private NavWaypoint Resolve(string id)
            {
                NavWaypoint found;

                if (Waypoints.TryGetValue(id, out found))
                {
                    return found;
                }

                // The far end of a link: one of ours, already on the graph.
                NavWaypoint existing = NavigationSystem.Graph.Node(id);

                return existing;
            }
        }

        /// <summary>
        /// One reference edge, with the ids it will actually be walked between.
        ///
        /// The two are not always the reference's own. Where an edge reaches into ground we have
        /// authored, the endpoint inside it is replaced by OUR nearest waypoint - that substitution
        /// is what makes a join possible, and keeping it here rather than re-deriving it at walk
        /// time means the decision is made once, while the authored region is still in hand.
        /// </summary>
        /// <summary>
        /// An edge that could not be walked, with the ends needed to draw it.
        ///
        /// A sentence is enough for a log and not enough for a map: the editor draws these in red
        /// so the author can see WHERE the road broke, and a string cannot be given a position.
        /// </summary>
        private sealed class FailedEdge
        {
            public string From;
            public string To;
            public string Reason;
            public int FromX;
            public int FromY;
            public int ToX;
            public int ToY;
        }

        private sealed class PendingEdge
        {
            public NavEdge Edge;

            /// <summary>Resolved ids: a reference waypoint, or one of ours at a join.</summary>
            public string FromId;
            public string ToId;

            /// <summary>True when one end is an existing authored waypoint.</summary>
            public bool IsJoin;

            /// <summary>That waypoint's id, when there is one. The join hop is the one touching it.</summary>
            public string OurId;
        }

        /// <summary>
        /// Ground this shard has already authored: our zones, and a radius around our waypoints.
        ///
        /// Derived rather than a hardcoded Britain rect, so it keeps holding for the next town
        /// somebody authors by hand without an edit here.
        /// </summary>
        private sealed class AuthoredRegion
        {
            private readonly List<Rectangle2D> _zones = new List<Rectangle2D>();
            private readonly List<Point2D> _waypoints = new List<Point2D>();

            public AuthoredRegion(Map map)
            {
                foreach (NavZone zone in NavigationSystem.Zones)
                {
                    _zones.Add(new Rectangle2D(zone.X, zone.Y, zone.Width, zone.Height));
                }

                foreach (NavWaypoint waypoint in NavigationSystem.Graph.Nodes)
                {
                    if (waypoint.Map == map)
                    {
                        _waypoints.Add(new Point2D(waypoint.X, waypoint.Y));
                    }
                }
            }

            public bool Contains(int x, int y)
            {
                var at = new Point2D(x, y);

                for (int i = 0; i < _zones.Count; i++)
                {
                    if (_zones[i].Contains(at))
                    {
                        return true;
                    }
                }

                int radius = AuthoredRadius;

                for (int i = 0; i < _waypoints.Count; i++)
                {
                    if (Math.Max(Math.Abs(_waypoints[i].X - x), Math.Abs(_waypoints[i].Y - y)) <= radius)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// The proposal, as the editor reads it.
        ///
        /// Rewritten on every pass so the dialog can show progress: a large region is minutes of
        /// walking and an author watching a frozen window has no way to tell it from a hang.
        /// </summary>
        private static void Write(Job job)
        {
            var builder = new StringBuilder(4096);

            bool done = job.Next >= job.Pending.Count;

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"status\": ").Append(Json.Quote(done ? "done" : "working")).Append(",\n");
            builder.Append("  \"done\": ").Append(job.Next).Append(",\n");
            builder.Append("  \"total\": ").Append(job.Pending.Count).Append(",\n");
            builder.Append("  \"map\": ").Append(Json.Quote(job.Map.Name)).Append(",\n");
            builder.Append("  \"region\": {\"x\":").Append(job.Region.X)
                .Append(",\"y\":").Append(job.Region.Y)
                .Append(",\"width\":").Append(job.Region.Width)
                .Append(",\"height\":").Append(job.Region.Height).Append("},\n");
            builder.Append("  \"skipped\": {\"authored\":").Append(job.SkippedAuthored)
                .Append(",\"region\":").Append(job.SkippedRegion)
                .Append(",\"noArrival\":").Append(job.SkippedNoArrival).Append("},\n");
            builder.Append("  \"links\": ").Append(job.Links).Append(",\n");

            WriteWaypoints(builder, job);
            WriteEdges(builder, job);
            WriteDestinations(builder, job);
            WriteArrivals(builder, job);
            WriteFailures(builder, job);
            WriteStrings(builder, "stranded", job.Stranded, true);
            WriteStrings(builder, "islands", job.Islands, false);

            builder.Append("}\n");

            string error;

            if (!AtomicFile.Write(ProposalPath, builder.ToString(), out error))
            {
                Log.Error("Adopt proposal not written: {0}", error);
            }
        }

        private static void WriteWaypoints(StringBuilder builder, Job job)
        {
            builder.Append("  \"waypoints\": [\n");

            bool first = true;

            foreach (NavWaypoint waypoint in job.Waypoints.Values)
            {
                AppendWaypoint(builder, waypoint, ref first);
            }

            for (int i = 0; i < job.Subdivisions.Count; i++)
            {
                AppendWaypoint(builder, job.Subdivisions[i], ref first);
            }

            builder.Append(first ? "" : "\n").Append("  ],\n");
        }

        private static void AppendWaypoint(StringBuilder builder, NavWaypoint waypoint, ref bool first)
        {
            if (!first)
            {
                builder.Append(",\n");
            }

            first = false;

            builder.Append("    {\"id\":").Append(Json.Quote(waypoint.Id));

            if (!String.IsNullOrEmpty(waypoint.Name))
            {
                builder.Append(",\"name\":").Append(Json.Quote(waypoint.Name));
            }

            builder.Append(",\"map\":").Append(Json.Quote(waypoint.MapName))
                .Append(",\"x\":").Append(waypoint.X)
                .Append(",\"y\":").Append(waypoint.Y)
                .Append(",\"z\":").Append(waypoint.Z)
                .Append(",\"arrivalRange\":").Append(waypoint.ArrivalRange)
                .Append(",\"tags\":").Append(Json.Quote(waypoint.Tags ?? ""))
                .Append(",\"source\":").Append(Json.Quote(SourceTag))
                .Append("}");
        }

        private static void WriteEdges(StringBuilder builder, Job job)
        {
            builder.Append("  \"edges\": [\n");

            for (int i = 0; i < job.Edges.Count; i++)
            {
                NavEdge edge = job.Edges[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"from\":").Append(Json.Quote(edge.From))
                    .Append(",\"to\":").Append(Json.Quote(edge.To))
                    .Append(",\"kind\":\"walk\",\"tags\":").Append(Json.Quote(edge.Tags ?? ""))
                    .Append(",\"source\":").Append(Json.Quote(SourceTag));

                // Marked, because a join is the one proposed edge that touches a record we did
                // not write - the author should be able to see which those are before accepting.
                if (job.Joins.Contains(String.Format("{0}>{1}", edge.From, edge.To)))
                {
                    builder.Append(",\"join\":true");
                }

                builder.Append("}");
            }

            builder.Append(job.Edges.Count > 0 ? "\n" : "").Append("  ],\n");
        }

        private static void WriteDestinations(StringBuilder builder, Job job)
        {
            builder.Append("  \"destinations\": [\n");

            for (int i = 0; i < job.Destinations.Count; i++)
            {
                NavDestination destination = job.Destinations[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"id\":").Append(Json.Quote(destination.Id))
                    .Append(",\"name\":").Append(Json.Quote(destination.Name ?? destination.Id))
                    .Append(",\"type\":").Append(Json.Quote(destination.Type))
                    .Append(",\"map\":").Append(Json.Quote(destination.MapName))
                    .Append(",\"x\":").Append(destination.X)
                    .Append(",\"y\":").Append(destination.Y)
                    .Append(",\"z\":").Append(destination.Z)
                    .Append(",\"tags\":").Append(Json.Quote(destination.Tags ?? ""))
                    .Append(",\"waypoints\":").Append(Json.Quote(destination.WaypointIds ?? ""))
                    .Append(",\"source\":").Append(Json.Quote(SourceTag))
                    .Append("}");
            }

            builder.Append(job.Destinations.Count > 0 ? "\n" : "").Append("  ],\n");
        }

        private static void WriteArrivals(StringBuilder builder, Job job)
        {
            builder.Append("  \"arrivals\": [\n");

            for (int i = 0; i < job.Arrivals.Count; i++)
            {
                NavArrival arrival = job.Arrivals[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"destination\":").Append(Json.Quote(arrival.DestinationId))
                    .Append(",\"x\":").Append(arrival.X)
                    .Append(",\"y\":").Append(arrival.Y)
                    .Append(",\"z\":").Append(arrival.Z)
                    .Append(",\"exclusive\":false,\"waypoints\":")
                    .Append(Json.Quote(arrival.WaypointIds ?? ""))
                    .Append(",\"source\":").Append(Json.Quote(SourceTag))
                    .Append("}");
            }

            builder.Append(job.Arrivals.Count > 0 ? "\n" : "").Append("  ],\n");
        }

        private static void WriteFailures(StringBuilder builder, Job job)
        {
            builder.Append("  \"failures\": [\n");

            for (int i = 0; i < job.Failures.Count; i++)
            {
                FailedEdge failure = job.Failures[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"from\":").Append(Json.Quote(failure.From))
                    .Append(",\"to\":").Append(Json.Quote(failure.To))
                    .Append(",\"reason\":").Append(Json.Quote(failure.Reason))
                    .Append(",\"fromX\":").Append(failure.FromX)
                    .Append(",\"fromY\":").Append(failure.FromY)
                    .Append(",\"toX\":").Append(failure.ToX)
                    .Append(",\"toY\":").Append(failure.ToY)
                    .Append("}");
            }

            builder.Append(job.Failures.Count > 0 ? "\n" : "").Append("  ],\n");
        }

        private static void WriteStrings(StringBuilder builder, string key, List<string> lines, bool comma)
        {
            builder.Append("  \"").Append(key).Append("\": [\n");

            for (int i = 0; i < lines.Count; i++)
            {
                builder.Append(i > 0 ? ",\n" : "").Append("    ").Append(Json.Quote(lines[i]));
            }

            builder.Append(lines.Count > 0 ? "\n" : "").Append("  ]").Append(comma ? ",\n" : "\n");
        }
    }
}
