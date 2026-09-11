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
        /// A HUNDRED TILES, not the hop cap. The first version used the cap, reasoning that a join
        /// is an ordinary edge once written - but a join is not written as one edge. It is WALKED
        /// and subdivided like any other, so the only thing the reach has to satisfy is that a
        /// road exists, not that it fits in one hop.
        ///
        /// Using the cap meant a join needed one of our waypoints within twelve tiles of the
        /// crossing edge's inside end, and our Britain graph does not reach the south gate. One
        /// adopt of 481 waypoints made exactly ONE join and left 371 of them unreachable.
        /// </summary>
        public static int JoinReach
        {
            get { return Config.Get("Custom.NavAdoptJoinReach", 100); }
        }

        /// <summary>
        /// In REBASE mode, how close one of our waypoints has to be to a road we are proposing
        /// before it counts as the same piece of road and is merged away.
        ///
        /// SIX, half the hop cap, so a merged route end never moves by more than half a leg - and
        /// measured to the nearest point on a proposed EDGE, not to the nearest proposed waypoint.
        /// That distinction is the whole calibration. uo-offline authored against a 38-tile leg,
        /// so their nodes sit about sixteen tiles apart, and ours sit BETWEEN them on the same
        /// street: measured node-to-node, only 44 of Britain's 93 are within six tiles of theirs,
        /// while measured to the segment 73 are. Merging on the node distance would have left
        /// thirty of our waypoints standing on top of their roads, which is the second road
        /// network this mode exists to avoid.
        /// </summary>
        public static int MergeRadius
        {
            get { return Config.Get("Custom.NavAdoptMergeRadius", 6); }
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
            return TryStart(map, region, false, out error);
        }

        /// <summary>
        /// Start an adopt, optionally in REBASE mode.
        ///
        /// WHAT REBASE CHANGES, and it is one thing stated two ways: the authored-region skip stops
        /// applying to waypoints and edges, and starts being enforced by a merge instead. Ordinary
        /// adopt refuses to propose a road over ground we have authored, which is right while their
        /// roads and ours are in different places. Britain is the case where they are in the SAME
        /// place and ours is the worse of the two - hand-authored, partial, and carrying every
        /// high-detour edge the walk audit finds - so refusing to propose there means the town can
        /// never be improved by their data at all.
        ///
        /// WHAT IT DOES NOT CHANGE. Destinations, arrivals, sites, zones and routes keep the skip
        /// in full. Those are the records that carry authored work - names, tags, exclusive and
        /// exact flags, positions somebody placed by eye - and nothing here may propose over one.
        /// Only the ROAD is rebased.
        ///
        /// The rule this serves, whole-facet rather than a Britain special case: wherever
        /// uo-offline has roads, theirs replace ours; wherever they have none, ours stay authored.
        /// A waypoint of ours further than <see cref="MergeRadius"/> from every proposed edge is
        /// one their graph does not cover, and it is kept and joined exactly as before.
        /// </summary>
        public static bool TryStart(Map map, Rectangle2D region, bool rebase, out string error)
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

            var job = new Job(map, region, reference, rebase);

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
                "Adopt{0} started: {1} waypoint(s), {2} edge(s) to walk, {3} skipped as already authored.",
                rebase ? " (REBASE)" : "", job.Waypoints.Count, job.Total, job.SkippedAuthored);

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

                // Try to connect what is left before calling it an island. A region only
                // joins where one of THEIR edges happened to cross into ground we
                // authored, and that is not where a road meets a town - one adopt made a
                // single join and left 372 waypoints floating. Each cut-off piece gets a
                // corridor of its own.
                if (job.PlanComponentJoins())
                {
                    LoopQueue.Post(() => Step(job));
                    return;
                }

                // Then the destinations nothing can leave: a corridor from the nearest reachable
                // waypoint to an arrival that sits beyond the hop cap of every waypoint. Their
                // data, our cap - all eight in the Britain-Trinsic box were inside uo-offline's
                // 38-tile leg and outside our 12.
                if (job.PlanArrivalCorridors())
                {
                    LoopQueue.Post(() => Step(job));
                    return;
                }

                // AFTER the corridors, because a merge is measured against the road as it will be
                // written and an arrival corridor is part of that road - and because it queues
                // WORK of its own: the relinks that keep a removed waypoint's neighbours attached
                // have to walk like any other edge, so this sits in the same return-and-come-back
                // chain the joins and the corridors use.
                if (job.PlanMerges())
                {
                    LoopQueue.Post(() => Step(job));
                    return;
                }

                job.SettleCorridors();
                job.SettleMerges();

                job.FindIslands();
                job.PruneUnreachable();

                _running = false;
                job.Finished = true;

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

            /// <summary>True when this run may propose road over ground we have authored.</summary>
            public readonly bool Rebase;

            /// <summary>
            /// Waypoints of OURS this proposal asks to remove, because the road it proposes runs
            /// where they stand. Written to the proposal; applied only by the editor's save path.
            ///
            /// THIS IS THE ONE PLACE ADOPT ASKS FOR A DELETION, and the safety property is
            /// unchanged in kind: it is still a proposal in `Data/Live/nav-adopt.json`, and there
            /// is still no code path from this file to `navigation.json`.
            /// </summary>
            public readonly List<Removal> Removals = new List<Removal>();

            /// <summary>
            /// Every reference that has to move because a removal took its waypoint away - a
            /// destination's or arrival's approach list, a route's step list, an edge of ours.
            ///
            /// CARRIED EXPLICITLY RATHER THAN LEFT TO WARN. The shard drops a dangling edge and
            /// warns about a destination naming an unknown waypoint, so a removal with no rewrite
            /// would reload cleanly and silently point somebody's shop at nothing. The proposal
            /// says what has to change, and the reference audit at the end is what checks it did.
            /// </summary>
            public readonly List<Rewrite> Rewrites = new List<Rewrite>();

            /// <summary>Edges queued to keep a removed waypoint's neighbours on the road, as "a&gt;b".</summary>
            public readonly List<string> Relinks = new List<string>();

            /// <summary>Our edges naming a removed waypoint, as editor shape ids. See PlanEdgeRemovals.</summary>
            public readonly List<string> RemovedEdges = new List<string>();

            /// <summary>Removals taken back because their relink would not walk. See SettleMerges.</summary>
            public readonly List<string> Withdrawn = new List<string>();

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

            /// <summary>Destinations skipped for having no arrival a bot could route away from.</summary>
            public readonly List<string> SkippedNoReach = new List<string>();
            public readonly List<string> Islands = new List<string>();

            /// <summary>Reference waypoints folded into another on the same tile, as "folded&gt;kept".</summary>
            public readonly List<string> Folded = new List<string>();

            /// <summary>Records whose Z was moved to where the walker stood, as "id x,y: ref -&gt; z".</summary>
            public readonly List<string> Corrected = new List<string>();

            /// <summary>
            /// Proposed waypoints connected to each other but not to the graph we have. Accept
            /// drops these and writes the rest; they stay in the reference layer for a later box.
            /// </summary>
            public readonly List<string> Unreachable = new List<string>();

            /// <summary>How many proposed waypoints reach the graph we have.</summary>
            public int ReachedCount;

            /// <summary>
            /// True when NOTHING proposed reaches the graph we have. Decided from reachability,
            /// not from joins queued: a join whose walk failed still counted as a link, which let
            /// a proposal that reached nothing pass as joined.
            /// </summary>
            public bool Blocked;

            /// <summary>
            /// True once joins, corridors, islands and pruning have all run. The proposal says
            /// "done" only then: it used to say it as soon as the last edge was walked, and the
            /// editor latched that snapshot - no joins, no islands, arrivals unpruned.
            /// </summary>
            public bool Finished;

            /// <summary>The kept id for every folded one; anything naming the folded id is re-pointed.</summary>
            private readonly Dictionary<string, string> _alias =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// REBASE: the proposed waypoint each removed one of ours re-points to. Separate from
            /// `_alias`, which is a fold between two REFERENCE records on one tile; this is a merge
            /// between one of OURS and the road being proposed over it, and the two must not share
            /// a map because a fold is applied to the reference before the walk while a merge is
            /// decided after it.
            /// </summary>
            private readonly Dictionary<string, string> _merged =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Corridors walked from a reachable waypoint to a destination none of whose arrivals
            /// was inside the hop cap. See PlanArrivalCorridors.
            /// </summary>
            public readonly List<Corridor> Corridors = new List<Corridor>();

            public int Next;
            private bool _joinsPlanned;
            private bool _corridorsPlanned;
            private bool _mergesPlanned;
            public int SkippedAuthored;

            /// <summary>
            /// Reference records already in our graph from an earlier adopt. Rebase only: ordinary
            /// adopt never meets one, because an adopted waypoint is one of ours and the authored
            /// skip catches it first.
            /// </summary>
            public int SkippedAdopted;
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

            public Job(Map map, Rectangle2D region, NavigationStore reference, bool rebase)
            {
                Map = map;
                Region = region;
                Rebase = rebase;

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

                    // A REFERENCE RECORD WE HAVE ALREADY ADOPTED IS NOT PROPOSED AGAIN - and in
                    // rebase mode this is load-bearing rather than an optimisation. Ordinary adopt
                    // never meets the case: an adopted waypoint is one of ours, so it sits in the
                    // authored region and the skip below catches it. Rebase turns that skip off,
                    // and 216 of the reference's ids are already in navigation.json from the
                    // Britain-Trinsic box - so a rebase box overlapping saved ground would propose
                    // a CREATE for an id that already exists, and a duplicate id is one of the few
                    // things that refuses the reload outright. It is already adopted, at the
                    // position it was walked to; there is nothing to do but let edges join onto it.
                    if (Rebase && NavigationSystem.Graph.Node(waypoint.Id) != null)
                    {
                        SkippedAdopted++;
                        continue;
                    }

                    // REBASE PROPOSES HERE ANYWAY. Ordinary adopt refuses ground we have authored,
                    // which is right while their roads and ours are in different places; Britain is
                    // where they are in the same place and ours is the worse of the two. What keeps
                    // the two graphs from becoming one road laid over another is not this skip in
                    // rebase mode - it is PlanMerges, which removes ours where theirs now runs.
                    if (!Rebase && authored.Contains(waypoint.X, waypoint.Y))
                    {
                        SkippedAuthored++;
                        continue;
                    }

                    Waypoints[waypoint.Id] = waypoint;
                }

                // TWO RECORDS ON ONE TILE FOLD INTO THE FIRST. uo-offline's WP 140 and Honor Trail
                // 1 both sit at 1824,2843 with an edge between them - a road of no length, which
                // `MovementPath` cannot path (it returns no path for a goal within one tile,
                // MovementPath.cs:34) and NavAudit would have to special-case for ever. Their own
                // dungeon cleanup "merged 519 co-located/tight nodes" (waypoints.json:2), so this
                // is their rule applied to the overworld. The first in reference order is kept;
                // every edge, destination and arrival that names the other is re-pointed to it,
                // and the pair is listed so the proposal explains the missing id.
                var byTile = new Dictionary<long, string>();

                foreach (NavWaypoint waypoint in reference.Waypoints ?? new List<NavWaypoint>())
                {
                    if (!Waypoints.ContainsKey(waypoint.Id))
                    {
                        continue;
                    }

                    long tile = ((long)waypoint.X << 32) | (uint)waypoint.Y;
                    string kept;

                    if (!byTile.TryGetValue(tile, out kept))
                    {
                        byTile[tile] = waypoint.Id;
                        continue;
                    }

                    _alias[waypoint.Id] = kept;
                    Folded.Add(String.Format("{0}>{1}", waypoint.Id, kept));
                    Waypoints.Remove(waypoint.Id);
                }

                var byId = new Dictionary<string, NavWaypoint>(StringComparer.OrdinalIgnoreCase);

                foreach (NavWaypoint waypoint in reference.Waypoints ?? new List<NavWaypoint>())
                {
                    byId[waypoint.Id] = waypoint;
                }

                foreach (NavEdge edge in reference.Edges ?? new List<NavEdge>())
                {
                    string fromId = Alias(edge.From);
                    string toId = Alias(edge.To);

                    // The edge between the two halves of a folded pair: nothing to walk.
                    if (Insensitive.Equals(fromId, toId))
                    {
                        continue;
                    }

                    bool from = Waypoints.ContainsKey(fromId);
                    bool to = Waypoints.ContainsKey(toId);

                    if (!from && !to)
                    {
                        continue;
                    }

                    var pending = new PendingEdge { Edge = edge, FromId = fromId, ToId = toId };

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
                        string outsideId = from ? toId : fromId;
                        NavWaypoint outside = byId.ContainsKey(outsideId) ? byId[outsideId] : null;

                        if (outside == null || !authored.Contains(outside.X, outside.Y))
                        {
                            continue;
                        }

                        // BY WALKED ROAD, NOT BY STRAIGHT LINE, which is the mistake the merge rule
                        // at PlanMerges already learned and this one kept making. Nearest() is
                        // pure Chebyshev with Z and walls ignored, and it is what put a
                        // thirty-three tile road on brit-tan-1's four-tile join - eighteen of the
                        // twenty highest-detour edges on the graph were joins. NearestByRoad
                        // shortlists by line and then floods, and falls back to exactly this
                        // waypoint when nothing floods, so it can only improve on the old answer.
                        NavWaypoint ours = NavRejoin.NearestByRoad(
                            Map, new Point3D(outside.X, outside.Y, outside.Z), JoinReach);

                        if (ours == null)
                        {
                            // Nothing of ours within reach at all. Rare now the reach is a
                            // hundred tiles, and genuinely nothing to join to when it happens.
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

                    // The reference is loaded fresh for every run, so re-pointing its records
                    // through the fold aliases touches nothing that outlives this job.
                    destination.WaypointIds = Alias(destination.WaypointIds);

                    foreach (NavArrival arrival in mine)
                    {
                        arrival.WaypointIds = Alias(arrival.WaypointIds);
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

            /// <summary>
            /// A waypoint id, or a space-separated list of them, with every folded id replaced
            /// by the one it was folded into. Anything else passes through untouched.
            /// </summary>
            private string Alias(string ids)
            {
                if (String.IsNullOrEmpty(ids) || _alias.Count == 0)
                {
                    return ids;
                }

                string[] parts = ids.Split(' ');
                bool changed = false;

                for (int i = 0; i < parts.Length; i++)
                {
                    string kept;

                    if (_alias.TryGetValue(parts[i], out kept))
                    {
                        parts[i] = kept;
                        changed = true;
                    }
                }

                return changed ? String.Join(" ", parts) : ids;
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

                // A one-point path is two ends that snapped onto one tile, which Subdivide writes
                // as a single hop with nothing between; it is not a failure.
                if (!NavCorridor.TryPath(Map, start, goal, out path, out error) || path.Count < 1)
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

                // THE Z A MOBILE STANDS AT ON THE RECORD'S TILE, NOT THE ONE THE REFERENCE STORED.
                // Asked of the tile itself with NavWalker.TryResolveZ - the same question the
                // walker asks when it aims a hop there - rather than read off the snapped path,
                // because the snap may have started from a neighbouring tile while the record's
                // own tile stands at a Z the reference simply had wrong: uo-wp-990 at -15, the
                // water under a deck at -2. Written as stood, with theirs kept as refZ so the diff
                // explains itself; uo-offline's own [fixdest repairs their data the same way. Only
                // OUR proposal's records: the far end of a join is one of ours and is never touched.
                CorrectZ(from);
                CorrectZ(to);

                Subdivide(pending, from, to, path);
            }

            private void CorrectZ(NavWaypoint waypoint)
            {
                if (!Waypoints.ContainsKey(waypoint.Id) || waypoint.RefZ.HasValue)
                {
                    return;
                }

                int stood;

                if (!NavWalker.TryResolveZ(Map, new Point3D(waypoint.X, waypoint.Y, waypoint.Z), out stood)
                    || stood == waypoint.Z)
                {
                    return;
                }

                Corrected.Add(String.Format(
                    "{0} {1},{2}: {3} -> {4}", waypoint.Id, waypoint.X, waypoint.Y, waypoint.Z, stood));

                waypoint.RefZ = waypoint.Z;
                waypoint.Z = stood;
            }

            /// <summary>
            /// Cut a walked path into hops no longer than the cap, minting waypoints.
            ///
            /// THE ENDS ARE THE AUTHORED POSITIONS, NOT THE PATH'S. `NavCorridor.TryPath` SNAPS
            /// both ends to the nearest standable tile - up to `SnapRadius`, 8 - because a
            /// waypoint's own tile is often not one a mobile can occupy. So `path[0]` and the last
            /// point are not where the waypoints are, and measuring the cut against the path while
            /// WRITING an edge between the waypoints let a hop come out at 12 + 8. That is exactly
            /// the `uo-wp-204-s1 -> uo-wp-205` at 15 tiles in the log: cap held against the path
            /// and the edge was written against the records.
            ///
            /// So the walk is re-expressed between the real endpoints first, and the cut is made
            /// where the NEXT point would break the cap rather than where the current one already
            /// has - which is what makes every emitted hop measure under it.
            /// </summary>
            private void Subdivide(PendingEdge pending, NavWaypoint from, NavWaypoint to, List<Point3D> path)
            {
                // THE BUDGET IS THE CAP MINUS WHERE A WALKER IS ALLOWED TO STAND, NOT THE CAP.
                //
                // This cut at the cap for as long as it existed, which is why 566 of the graph's
                // 1153 walk edges are authored at exactly 12 - and why a whole class of cliff
                // exists. NavWalker.ArrivalRangeFor lets a walker stop two tiles short of the
                // waypoint it was heading for, so a 12-tile edge is a FOURTEEN tile hop for the
                // bot that actually has to plan it, past what the greedy 38x38-box pathfinder is
                // calibrated for. uo-wp-194-s1 -> uo-wp-194 was exactly that (commit 239ff6e2):
                // clean on every instrument that starts ON the waypoint, and two of its twenty-four
                // approach tiles could not leave.
                //
                // Expressed as cap minus ArrivalRangeFor rather than as a literal, so a later
                // pathfinder evaluation can move Custom.NavHopMaxTiles without touching this line.
                // The far end of every hop this test governs is a waypoint this method is about to
                // mint, and a minted waypoint takes DefaultArrivalRange - so the displacement can
                // be at either end and the budget has to allow for the larger. The last hop is
                // emitted unconditionally and is not governed by this at all.
                //
                // Existing edges are NOT rewritten by this. It changes what a future adopt mints.
                int cap = NavigationSystem.HopMaxTiles;
                int anchorRange = NavWalker.ArrivalRangeFor(from);

                // At least one tile of budget whatever the cap says: a configuration that leaves
                // none would cut at every point on the path and mint a waypoint per tile.
                int budget = Math.Max(
                    1, cap - Math.Max(anchorRange, NavWalker.DefaultArrivalRange));

                // The path with its snapped ends replaced by the records the edge will name.
                var points = new List<Point3D>();

                points.Add(new Point3D(from.X, from.Y, from.Z));

                for (int i = 1; i < path.Count - 1; i++)
                {
                    points.Add(path[i]);
                }

                points.Add(new Point3D(to.X, to.Y, to.Z));

                string previous = from.Id;
                Point3D anchor = points[0];
                int minted = 0;

                // What this call adds, so a hop the engine refuses can take the whole edge back
                // out again: the edge is proposed whole or not at all.
                int edgesBefore = Edges.Count;
                int subdivisionsBefore = Subdivisions.Count;
                var joinsAdded = new List<string>();
                var hops = new List<Point3D>();

                hops.Add(points[0]);

                for (int i = 1; i < points.Count; i++)
                {
                    bool last = i == points.Count - 1;

                    // Cut at the point BEFORE the budget breaks, not after. Looking ahead is what
                    // keeps the emitted hop under it; looking behind emits the one that broke it.
                    if (!last)
                    {
                        int ahead = Math.Max(
                            Math.Abs(points[i + 1].X - anchor.X), Math.Abs(points[i + 1].Y - anchor.Y));

                        if (ahead <= budget)
                        {
                            continue;
                        }
                    }

                    Point3D at = points[i];
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
                        string join = String.Format("{0}>{1}", previous, next);

                        if (Joins.Add(join))
                        {
                            joinsAdded.Add(join);
                        }
                    }

                    hops.Add(at);
                    previous = next;
                    anchor = at;
                }

                // THE ENGINE HAS THE LAST WORD. The flood said a road exists; this asks whether
                // the pathfinder a bot will actually use walks each hop of it, both ways, which is
                // the question [NavAudit asks after the save. A refused hop fails the edge with its
                // own reason, drawn red like any other failure, rather than being saved for the
                // audit to find - and the reason says the two disagreed, which is the thing worth
                // knowing when the climb window is one Z looser than uo-offline's.
                string refused;

                if (NavCorridor.TryVerifyHopsBothWays(Map, hops, out refused))
                {
                    return;
                }

                Edges.RemoveRange(edgesBefore, Edges.Count - edgesBefore);
                Subdivisions.RemoveRange(subdivisionsBefore, Subdivisions.Count - subdivisionsBefore);

                foreach (string join in joinsAdded)
                {
                    Joins.Remove(join);
                }

                Failures.Add(new FailedEdge
                {
                    From = pending.FromId,
                    To = pending.ToId,
                    Reason = refused ?? "flood ok, engine refused",
                    FromX = from.X,
                    FromY = from.Y,
                    ToX = to.X,
                    ToY = to.Y
                });
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
            /// <summary>
            /// REBASE ONLY: work out which of our waypoints the proposed road now runs through,
            /// and ask for them to be removed - with every reference that named one re-pointed.
            ///
            /// MEASURED TO A SEGMENT, NOT TO A NODE, and that is the whole calibration rather than
            /// a detail. Their graph was authored against a 38-tile leg, so their nodes stand about
            /// sixteen tiles apart and ours stand BETWEEN them on the same street. Britain measured
            /// both ways: 44 of our 93 are within six tiles of one of their NODES, and 73 are
            /// within six tiles of one of their ROADS. Merging on node distance would have kept
            /// thirty waypoints of ours sitting on a road this proposal is also laying - which is
            /// the second road network the authored-region skip existed to prevent, arrived at by
            /// the other door.
            ///
            /// The segments are the WALKED hops, not the reference's edges: by the time this runs
            /// every over-cap edge has been subdivided under the cap and every hop has been pathed
            /// both ways, so the road being measured against is the road that will be written.
            ///
            /// WHAT IS NEVER MERGED. A waypoint further than the radius from every proposed hop is
            /// one their graph does not cover, and it is kept and joined - the shop doors and
            /// approaches their roads do not front, and the work-site corridors they never reach.
            /// That asymmetry IS the rule: wherever uo-offline has roads, theirs replace ours;
            /// wherever they have none, ours stay authored.
            /// </summary>
            public bool PlanMerges()
            {
                if (!Rebase || _mergesPlanned)
                {
                    return false;
                }

                _mergesPlanned = true;

                int radius = MergeRadius;

                // The proposed road as segments, each with the two waypoints that bound it, so a
                // merge can name the nearer end as the record everything re-points to.
                var hops = new List<Hop>();

                for (int i = 0; i < Edges.Count; i++)
                {
                    NavWaypoint a = Resolve(Edges[i].From);
                    NavWaypoint b = Resolve(Edges[i].To);

                    if (a != null && b != null)
                    {
                        hops.Add(new Hop { A = a, B = b });
                    }
                }

                if (hops.Count == 0)
                {
                    return false;
                }

                // A WAYPOINT THE PROPOSAL JOINS ONTO IS NEVER MERGED, and the reason is circular
                // rather than cautious. A join maps their edge's outside end onto our nearest
                // waypoint and then subdivides the walk, so the subdivisions on that edge exist
                // BECAUSE our waypoint does. Merge our waypoint into one of them and the edge
                // becomes a self-edge, the self-edge is dropped, and the subdivision is left with
                // no road at all - so the record every rewrite now points at is one the save never
                // writes. Britain produced it exactly: town-7 merged into uo-town-7-s1, one hop
                // along town-7's own join, and brit-shop-tinker came back naming a waypoint that
                // did not exist. The proposal is deliberately attaching to these; they stay.
                var joinTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Pending.Count; i++)
                {
                    if (Pending[i].IsJoin && Pending[i].OurId != null)
                    {
                        joinTargets.Add(Pending[i].OurId);
                    }
                }

                // A WAYPOINT AN ARRIVAL OR DESTINATION NAMES IS NOT ROAD - IT IS A DOOR, and the
                // difference only shows up in walked tiles, which is why the first Britain rebase
                // shipped without it. A door waypoint stands ON the shop's threshold; the street
                // waypoint that replaces it is four tiles away in a straight line and thirty-two
                // by road, because the road goes round the building. The merge measured the
                // straight line, took the door, and moved the detour off the EDGE that used to
                // reach it and onto the ARRIVAL that now starts outside: `brit-shop-tanner`'s
                // cheapest approach went from 1 walked tile to 20, `brit-home-carpenter`'s from 2
                // to 28. Nothing failed and no ratio looked alarming, because the ratio's
                // denominator moved with it.
                //
                // The rule is the join rule applied to the other kind of attachment: the proposal
                // may replace the roads between our places, never the record standing at one.
                foreach (NavDestination destination in NavigationSystem.Destinations)
                {
                    foreach (string id in Split(destination.WaypointIds))
                    {
                        joinTargets.Add(id);
                    }
                }

                foreach (NavArrival arrival in NavigationSystem.Store.Arrivals)
                {
                    foreach (string id in Split(arrival.WaypointIds))
                    {
                        joinTargets.Add(id);
                    }
                }

                foreach (NavWaypoint ours in NavigationSystem.Graph.NodesOn(Map))
                {
                    if (!Region.Contains(new Point2D(ours.X, ours.Y)))
                    {
                        continue;
                    }

                    // An id this proposal itself minted is not one of ours to remove.
                    if (Waypoints.ContainsKey(ours.Id) || joinTargets.Contains(ours.Id))
                    {
                        continue;
                    }

                    double best = Double.MaxValue;
                    NavWaypoint onto = null;

                    for (int i = 0; i < hops.Count; i++)
                    {
                        double distance = hops[i].DistanceTo(ours.X, ours.Y);

                        if (distance >= best)
                        {
                            continue;
                        }

                        // THE TARGET HAS TO BE A PROPOSED WAYPOINT, and this is not a formality.
                        // A join edge's far end is one of OURS, so the nearer end of a hop can be
                        // a waypoint of ours - and the first run of the Britain box merged
                        // brit-tink-1 into town-7 on exactly that path. Collapsing two of ours into
                        // one is a different decision from rebasing ours onto their road: it is
                        // legal, it preserves connectivity, and nobody asked for it. The rule is
                        // ours-into-theirs, so a hop whose nearer end is ours offers its other end,
                        // and a hop with no proposed end at all offers nothing.
                        NavWaypoint target = ProposedEnd(hops[i], ours.X, ours.Y);

                        if (target == null)
                        {
                            continue;
                        }

                        best = distance;
                        onto = target;
                    }

                    if (onto == null || best > radius || Insensitive.Equals(onto.Id, ours.Id))
                    {
                        continue;
                    }

                    _merged[ours.Id] = onto.Id;

                    Removals.Add(new Removal
                    {
                        Id = ours.Id,
                        Into = onto.Id,
                        X = ours.X,
                        Y = ours.Y,
                        Tiles = (int)Math.Round(best)
                    });
                }

                return PlanRelinks();
            }

            /// <summary>
            /// A REMOVAL HAS TO RELINK, or it takes a neighbour's only road away with it.
            ///
            /// This is the fault a dry run of the first version found, and it is worth stating
            /// plainly because nothing refuses it: our edges naming a removed waypoint come back
            /// from the validator as WARNINGS - "edge dropped: no waypoint 'brit-carp-3'" - the
            /// shard drops them and reloads clean. In the trial box that silently cut brit-carp-4
            /// loose, and brit-carp-4 is the only approach waypoint brit-shop-carpenter has. A
            /// stranded shop that reloads without an error is precisely the shape of fault the
            /// island check exists for.
            ///
            /// The editor's own hand-delete already knew: `deleteAndRelink` removes the edges
            /// touching a waypoint and joins its two neighbours back up. This is that rule for a
            /// merge - every surviving neighbour of a removed waypoint gets an edge to the proposed
            /// waypoint the removal folded into, walked and subdivided like any other. A neighbour
            /// that is itself being removed needs nothing: the road between the two of them is the
            /// road being proposed.
            /// </summary>
            private bool PlanRelinks()
            {
                if (Removals.Count == 0)
                {
                    return false;
                }

                int before = Pending.Count;
                var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Removals.Count; i++)
                {
                    Removal removal = Removals[i];

                    foreach (NavWaypoint neighbour in NavigationSystem.Graph.Neighbours(removal.Id))
                    {
                        if (_merged.ContainsKey(neighbour.Id))
                        {
                            continue;
                        }

                        string key = neighbour.Id + ">" + removal.Into;

                        if (!queued.Add(key))
                        {
                            continue;
                        }

                        // Already a road between these two: the neighbour was reached by one of
                        // their edges as well, and one hop is enough.
                        if (HasEdge(neighbour.Id, removal.Into))
                        {
                            continue;
                        }

                        Relinks.Add(key);

                        Pending.Add(new PendingEdge
                        {
                            Edge = new NavEdge
                            {
                                From = neighbour.Id,
                                To = removal.Into,
                                KindName = "walk",
                                Tags = "road"
                            },
                            FromId = neighbour.Id,
                            ToId = removal.Into,
                            IsJoin = true,
                            OurId = neighbour.Id
                        });

                        Links++;
                    }
                }

                return Pending.Count > before;
            }

            private bool HasEdge(string a, string b)
            {
                for (int i = 0; i < Edges.Count; i++)
                {
                    if ((Insensitive.Equals(Edges[i].From, a) && Insensitive.Equals(Edges[i].To, b))
                        || (Insensitive.Equals(Edges[i].From, b) && Insensitive.Equals(Edges[i].To, a)))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// After the relinks have walked: withdraw any removal whose relink failed, then write
            /// the rewrites for the removals that stand.
            ///
            /// WITHDRAWN RATHER THAN FORCED. A relink that will not walk means the neighbour cannot
            /// reach the new road, and removing the waypoint anyway would strand it. Keeping our
            /// waypoint leaves two roads over one piece of ground, which is untidy and visible;
            /// stranding a shop is neither. The author is told which, and can move the record and
            /// re-run rather than discover it at the next audit.
            /// </summary>
            public void SettleMerges()
            {
                if (!Rebase || Removals.Count == 0)
                {
                    return;
                }

                var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Failures.Count; i++)
                {
                    failed.Add(Failures[i].From + ">" + Failures[i].To);
                    failed.Add(Failures[i].To + ">" + Failures[i].From);
                }

                for (int i = Removals.Count - 1; i >= 0; i--)
                {
                    Removal removal = Removals[i];
                    bool broken = false;

                    foreach (NavWaypoint neighbour in NavigationSystem.Graph.Neighbours(removal.Id))
                    {
                        if (_merged.ContainsKey(neighbour.Id))
                        {
                            continue;
                        }

                        if (failed.Contains(neighbour.Id + ">" + removal.Into))
                        {
                            broken = true;
                            break;
                        }
                    }

                    if (!broken)
                    {
                        continue;
                    }

                    Withdrawn.Add(String.Format(
                        "{0} is KEPT: the relink from one of its neighbours to {1} would not walk,"
                        + " and removing it would strand them",
                        removal.Id, removal.Into));

                    _merged.Remove(removal.Id);
                    Removals.RemoveAt(i);
                }

                RepointProposedEdges();
                PlanRewrites();
                PlanEdgeRemovals();
            }

            /// <summary>
            /// A PROPOSED edge may name a waypoint of ours that a merge has just removed, and it
            /// has to be re-pointed or dropped before the proposal is offered.
            ///
            /// This is the join, biting back. An edge crossing into authored ground is mapped onto
            /// our nearest waypoint and walked - so the proposal contains edges whose far end is
            /// one of OURS - and a merge can then remove exactly that waypoint. The Britain box
            /// produced the case in its purest form: `town-7 -> uo-town-7-s1`, a join from town-7
            /// subdivided one hop out, and town-7 then merged into that very subdivision at zero
            /// tiles. Saved as it stood, the edge named a record the same save was deleting, and
            /// the reload dropped it with a warning.
            ///
            /// So every proposed edge is re-pointed through the merge map, an edge that becomes a
            /// self-edge is dropped - a self-edge REFUSES the reload, it is not a warning - and a
            /// pair that ends up duplicated is folded to one.
            /// </summary>
            private void RepointProposedEdges()
            {
                if (_merged.Count == 0)
                {
                    return;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = Edges.Count - 1; i >= 0; i--)
                {
                    NavEdge edge = Edges[i];
                    string from = MergedId(edge.From);
                    string to = MergedId(edge.To);

                    if (Insensitive.Equals(from, to))
                    {
                        Edges.RemoveAt(i);
                        continue;
                    }

                    edge.From = from;
                    edge.To = to;
                }

                // Duplicates are folded from the front, so the first-written edge is the one kept
                // and the proposal's order is otherwise untouched.
                for (int i = 0; i < Edges.Count; i++)
                {
                    string a = Edges[i].From, b = Edges[i].To;
                    string key = String.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
                        ? a + ">" + b
                        : b + ">" + a;

                    if (seen.Add(key))
                    {
                        continue;
                    }

                    Edges.RemoveAt(i--);
                }
            }

            private string MergedId(string id)
            {
                string onto;

                return _merged.TryGetValue(id ?? "", out onto) ? onto : id;
            }

            /// <summary>
            /// Our own edges naming a removed waypoint, as editor shape ids, so Save deletes the
            /// records rather than leaving them to be dropped at load.
            ///
            /// THE SHARD WOULD COPE, AND THAT IS THE PROBLEM. A dangling edge id is a warning here:
            /// the edge is dropped and the reload succeeds. So a rebase that removed a waypoint and
            /// left its edges behind would reload perfectly, every walk would work, and
            /// `navigation.json` would carry a handful of edges naming records that no longer
            /// exist - invisible until somebody greps the file, and re-introduced verbatim by the
            /// next golden export. The editor's own hand-delete has always cascaded these
            /// (`deleteAndRelink` removes the edges touching a waypoint before removing it); this
            /// is the same cascade for a proposal, listed by the side that knows which edges they
            /// are rather than reconstructed by the side that does not.
            ///
            /// In their STORED order, because that is the order the editor mints a shape id from -
            /// `edge:from>to` - and `byFromTo` resolves it back the same way.
            /// </summary>
            private void PlanEdgeRemovals()
            {
                foreach (NavEdge edge in NavigationSystem.Store.Edges)
                {
                    if (_merged.ContainsKey(edge.From ?? "") || _merged.ContainsKey(edge.To ?? ""))
                    {
                        RemovedEdges.Add(String.Format("edge:{0}>{1}", edge.From, edge.To));
                    }
                }
            }

            /// <summary>
            /// Everything that named a removed waypoint, and what it has to name instead.
            ///
            /// WRITTEN OUT RATHER THAN LEFT TO THE RELOAD. On this shard a dangling edge id is a
            /// warning and the edge is simply dropped, and a destination naming an unknown waypoint
            /// is a warning too - so a removal with no rewrite reloads clean and quietly points a
            /// shop's approach at nothing. The proposal therefore says what must change, and the
            /// reference audit run after the save is what proves it did.
            ///
            /// A REWRITE CAN COLLAPSE A LIST. Two of our waypoints merging onto the same proposed
            /// one leaves a route or an approach list naming it twice, and a route naming the same
            /// step twice is legal but silly while a route left with fewer than two steps is FATAL
            /// at load. So a rewritten list drops consecutive repeats, and a route that would fall
            /// under two steps is reported instead of rewritten - there is a person to ask.
            /// </summary>
            private void PlanRewrites()
            {
                if (_merged.Count == 0)
                {
                    return;
                }

                foreach (NavDestination destination in NavigationSystem.Destinations)
                {
                    string next;

                    if (TryRewriteList(destination.WaypointIds, out next))
                    {
                        Rewrites.Add(new Rewrite
                        {
                            Kind = "destination",
                            ShapeId = "dest:" + destination.Id,
                            Owner = destination.Id,
                            From = destination.WaypointIds,
                            To = next
                        });
                    }
                }

                var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (NavArrival arrival in NavigationSystem.Store.Arrivals)
                {
                    int index;
                    seen.TryGetValue(arrival.DestinationId ?? "", out index);
                    seen[arrival.DestinationId ?? ""] = index + 1;

                    string next;

                    if (TryRewriteList(arrival.WaypointIds, out next))
                    {
                        Rewrites.Add(new Rewrite
                        {
                            Kind = "arrival",
                            ShapeId = String.Format("arr:{0}#{1}", arrival.DestinationId, index),
                            Owner = arrival.DestinationId,
                            From = arrival.WaypointIds,
                            To = next
                        });
                    }
                }

                foreach (NavRouteDef route in NavigationSystem.Routes)
                {
                    string next;

                    if (!TryRewriteList(route.WaypointIds, out next))
                    {
                        continue;
                    }

                    // A ROUTE'S STEPS ARE A PATH, NOT A SET, so re-pointing each one on its own is
                    // not enough. Two adjacent steps that merged onto records further apart than
                    // the hop cap leave a leg the walker cannot plan - brit-courier-loop came back
                    // with legs of 13, 14, 16 and 14 tiles the first time this ran, on a route
                    // whose every leg had been under twelve. So a leg over the cap is re-filled
                    // from the graph this proposal is about to make, and the route keeps its shape
                    // by following the new road between the two points rather than by luck.
                    next = Refill(next);

                    if (Split(next).Count < 2)
                    {
                        // A route with fewer than two steps refuses the reload outright, so this
                        // is not something to write and hope. brit-farmer-loop is the live case:
                        // its last step is brit-arm-1, a leaf.
                        SkippedNoReach.Add(String.Format(
                            "route '{0}' would be left with fewer than two steps by these removals",
                            route.Id));
                        continue;
                    }

                    Rewrites.Add(new Rewrite
                    {
                        Kind = "route",
                        ShapeId = "route:" + route.Id,
                        Owner = route.Id,
                        From = route.WaypointIds,
                        To = next
                    });
                }
            }

            /// <summary>
            /// Re-point a space-separated approach or step list through the merge map. False when
            /// nothing in it moved, so an unchanged record is never written back.
            /// </summary>
            private bool TryRewriteList(string ids, out string next)
            {
                next = null;

                List<string> parts = Split(ids);

                if (parts.Count == 0)
                {
                    return false;
                }

                var rebuilt = new List<string>(parts.Count);
                bool moved = false;

                for (int i = 0; i < parts.Count; i++)
                {
                    string id = parts[i];
                    string onto;

                    if (_merged.TryGetValue(id, out onto))
                    {
                        id = onto;
                        moved = true;
                    }

                    // A list that named two of ours which merged onto the same record would
                    // otherwise repeat it.
                    if (rebuilt.Count > 0 && Insensitive.Equals(rebuilt[rebuilt.Count - 1], id))
                    {
                        continue;
                    }

                    rebuilt.Add(id);
                }

                if (!moved)
                {
                    return false;
                }

                next = String.Join(" ", rebuilt.ToArray());
                return true;
            }

            /// <summary>
            /// Put the intermediate steps back into any leg the merge stretched past the hop cap,
            /// by walking the graph this proposal is about to make.
            ///
            /// THE GRAPH IS THE POST-SAVE ONE, assembled here rather than read: our own edges minus
            /// the ones a removal takes away, plus the proposal's, with every end re-pointed
            /// through the merge map. That is the only graph in which the answer is true - ours no
            /// longer has the road, and the proposal's alone does not have the parts of the town we
            /// kept.
            ///
            /// A breadth-first search rather than a cost search on purpose: this is repairing an
            /// authored patrol route, and the shortest chain of hops between two points a person
            /// chose is the honest reconstruction of what they drew. A leg that cannot be filled at
            /// all is left as it is - over-cap and reported by the reload - because inventing a
            /// detour for a patrol nobody can walk would hide it.
            /// </summary>
            private string Refill(string ids)
            {
                List<string> steps = Split(ids);

                if (steps.Count < 2)
                {
                    return ids;
                }

                Dictionary<string, List<string>> links = MergedAdjacency();
                int cap = NavigationSystem.HopMaxTiles;
                var built = new List<string> { steps[0] };

                for (int i = 1; i < steps.Count; i++)
                {
                    string a = steps[i - 1], b = steps[i];

                    if (!Insensitive.Equals(a, b) && Tiles(a, b) > cap)
                    {
                        List<string> between = ShortestPath(links, a, b);

                        // The interior only: the ends are already in the list, or about to be.
                        for (int j = 1; between != null && j < between.Count - 1; j++)
                        {
                            built.Add(between[j]);
                        }
                    }

                    if (!Insensitive.Equals(built[built.Count - 1], b))
                    {
                        built.Add(b);
                    }
                }

                return String.Join(" ", built.ToArray());
            }

            /// <summary>The graph as it will be after this proposal is saved. See Refill.</summary>
            private Dictionary<string, List<string>> MergedAdjacency()
            {
                var links = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (NavEdge edge in NavigationSystem.Store.Edges)
                {
                    if (_merged.ContainsKey(edge.From ?? "") || _merged.ContainsKey(edge.To ?? ""))
                    {
                        continue;
                    }

                    Link(links, edge.From, edge.To);
                }

                for (int i = 0; i < Edges.Count; i++)
                {
                    Link(links, Edges[i].From, Edges[i].To);
                }

                return links;
            }

            private static void Link(Dictionary<string, List<string>> links, string a, string b)
            {
                if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b))
                {
                    return;
                }

                List<string> to;

                if (!links.TryGetValue(a, out to))
                {
                    links[a] = to = new List<string>();
                }

                to.Add(b);

                if (!links.TryGetValue(b, out to))
                {
                    links[b] = to = new List<string>();
                }

                to.Add(a);
            }

            private static List<string> ShortestPath(
                Dictionary<string, List<string>> links, string from, string to)
            {
                if (!links.ContainsKey(from) || !links.ContainsKey(to))
                {
                    return null;
                }

                var back = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();

                back[from] = null;
                queue.Enqueue(from);

                while (queue.Count > 0)
                {
                    string at = queue.Dequeue();

                    if (Insensitive.Equals(at, to))
                    {
                        var path = new List<string>();

                        for (string step = to; step != null; step = back[step])
                        {
                            path.Insert(0, step);
                        }

                        return path;
                    }

                    foreach (string next in links[at])
                    {
                        if (!back.ContainsKey(next))
                        {
                            back[next] = at;
                            queue.Enqueue(next);
                        }
                    }
                }

                return null;
            }

            /// <summary>Tile distance between two records, whichever graph each of them lives in.</summary>
            private int Tiles(string a, string b)
            {
                NavWaypoint one = Resolve(a), two = Resolve(b);

                if (one == null || two == null)
                {
                    return Int32.MaxValue;
                }

                return Math.Max(Math.Abs(one.X - two.X), Math.Abs(one.Y - two.Y));
            }

            private static List<string> Split(string ids)
            {
                var found = new List<string>();

                if (String.IsNullOrEmpty(ids))
                {
                    return found;
                }

                string[] parts = ids.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < parts.Length; i++)
                {
                    found.Add(parts[i]);
                }

                return found;
            }

            /// <summary>
            /// The end of a hop a merge may point at: the nearer one when this proposal is minting
            /// it, the further one when the nearer is a waypoint of ours, and nothing when neither
            /// is proposed. See the comment at the call site for why ours is never a target.
            /// </summary>
            private NavWaypoint ProposedEnd(Hop hop, int x, int y)
            {
                NavWaypoint nearer, further;
                hop.Ends(x, y, out nearer, out further);

                if (IsProposed(nearer))
                {
                    return nearer;
                }

                return IsProposed(further) ? further : null;
            }

            private bool IsProposed(NavWaypoint waypoint)
            {
                if (waypoint == null)
                {
                    return false;
                }

                if (Waypoints.ContainsKey(waypoint.Id))
                {
                    return true;
                }

                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    if (Insensitive.Equals(Subdivisions[i].Id, waypoint.Id))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>One proposed hop, and the geometry a merge is measured against.</summary>
            private struct Hop
            {
                public NavWaypoint A;
                public NavWaypoint B;

                /// <summary>Distance from a tile to the nearest point on this hop.</summary>
                public double DistanceTo(int x, int y)
                {
                    double ax = A.X, ay = A.Y, bx = B.X, by = B.Y;
                    double dx = bx - ax, dy = by - ay;
                    double length = dx * dx + dy * dy;

                    // A hop of no length is a point; both ends are the same answer.
                    double t = length <= 0.0
                        ? 0.0
                        : ((x - ax) * dx + (y - ay) * dy) / length;

                    if (t < 0.0)
                    {
                        t = 0.0;
                    }
                    else if (t > 1.0)
                    {
                        t = 1.0;
                    }

                    double px = ax + t * dx - x;
                    double py = ay + t * dy - y;

                    return Math.Sqrt(px * px + py * py);
                }

                /// <summary>
                /// The two ends, nearer first - the nearer being the one a route through this hop
                /// reaches first from where our waypoint stood.
                /// </summary>
                public void Ends(int x, int y, out NavWaypoint nearer, out NavWaypoint further)
                {
                    double da = (A.X - x) * (double)(A.X - x) + (A.Y - y) * (double)(A.Y - y);
                    double db = (B.X - x) * (double)(B.X - x) + (B.Y - y) * (double)(B.Y - y);

                    bool first = da <= db;

                    nearer = first ? A : B;
                    further = first ? B : A;
                }
            }

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

                // Sort every proposed record into reached, stranded or cut off. Reachable and
                // Unreachable are what the editor writes and drops respectively; Blocked is the
                // one refusal left, and it is decided from what the flood reached rather than
                // from Links (see the field).
                var stranded = new HashSet<string>(Stranded, StringComparer.OrdinalIgnoreCase);

                foreach (string id in Waypoints.Keys)
                {
                    Sort(id, reached, stranded);
                }

                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    Sort(Subdivisions[i].Id, reached, stranded);
                }

                Blocked = ReachedCount == 0;

                // The one line that matters most when it applies, so it goes first: a region that
                // reaches nothing can only ever be an island, and the author should be told that
                // before reading anything else about it.
                if (Blocked)
                {
                    Islands.Insert(0, String.Format(
                        "NO JOIN WAS MADE. Nothing in this region reaches a waypoint we already "
                        + "have within {0} tiles, so everything proposed here would be cut off "
                        + "from the rest of the graph. Adopt a region that overlaps ground you "
                        + "have already saved first.",
                        JoinReach));
                }

                if (Stranded.Count > 0)
                {
                    Islands.Add(String.Format(
                        "{0} proposed waypoint(s) have no surviving edge at all - every edge they "
                        + "had failed to walk. Accept drops them: {1}",
                        Stranded.Count, Name(Stranded)));
                }

                // And the softer case: connected to each other, but not to anything we already
                // have. Save writes the part that reaches us and drops this part; the dropped
                // records are still in the reference layer, so the next box that overlaps what
                // was saved joins onto it and picks them up.
                if (Unreachable.Count == 0)
                {
                    return;
                }

                Islands.Add(String.Format(
                    "{0} of {1} proposed waypoint(s) cannot reach the existing graph and will "
                    + "not be written{2}. They stay in the uo-offline reference layer, dashed, "
                    + "to adopt from a box that overlaps this one once it is saved: {3}",
                    Unreachable.Count,
                    Waypoints.Count + Subdivisions.Count,
                    Blocked ? " (this region touches nothing already authored)" : "",
                    Name(Unreachable)));
            }

            private void Sort(string id, HashSet<string> reached, HashSet<string> stranded)
            {
                if (stranded.Contains(id))
                {
                    return;
                }

                if (reached.Contains(id))
                {
                    ReachedCount++;
                }
                else
                {
                    Unreachable.Add(id);
                }
            }

            /// <summary>
            /// Drop arrivals nothing can stand at, and destinations left with none.
            ///
            /// Two faults, one rule. Dropping a stranded waypoint orphans every arrival that named
            /// it - one adopt dropped `uo-wp-990` and left four arrivals for a dock still pointing
            /// at it. And a destination whose arrivals are all further than the hop cap from any
            /// waypoint is one a bot can be sent to and cannot leave, which is exactly what
            /// `Nav.Data` warns about at load.
            ///
            /// So the test is the one Nav.Data applies: is there a waypoint within the cap of this
            /// arrival, counting the ones we are proposing and the ones we already have. An
            /// arrival that fails it is not written, and a destination with none left is skipped
            /// and listed rather than written to warn on the next boot.
            ///
            /// Runs AFTER the walk, because subdivision mints waypoints - a tile out of reach of
            /// the reference's own waypoints is often within reach of one cut into the road.
            /// </summary>
            public void PruneUnreachable()
            {
                int cap = NavigationSystem.HopMaxTiles;

                // Surviving means WRITTEN: not stranded, and not cut off from the graph either.
                // An arrival measured against a waypoint Save is about to drop would be written
                // pointing at nothing, which is the fault this method exists to stop.
                var dropped = new HashSet<string>(Stranded, StringComparer.OrdinalIgnoreCase);
                dropped.UnionWith(Unreachable);

                var surviving = new List<Point2D>();

                foreach (KeyValuePair<string, NavWaypoint> pair in Waypoints)
                {
                    if (!dropped.Contains(pair.Key))
                    {
                        surviving.Add(new Point2D(pair.Value.X, pair.Value.Y));
                    }
                }

                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    if (!dropped.Contains(Subdivisions[i].Id))
                    {
                        surviving.Add(new Point2D(Subdivisions[i].X, Subdivisions[i].Y));
                    }
                }

                var kept = new List<NavArrival>();
                var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (NavArrival arrival in Arrivals)
                {
                    if (!InReach(arrival.X, arrival.Y, surviving, cap))
                    {
                        continue;
                    }

                    // An arrival is where a bot stands, so its Z is corrected like a waypoint's -
                    // but only when something IS standable there. A tile with nothing to stand on
                    // keeps its authored Z rather than gaining the land's, which would be a second
                    // wrong answer dressed as a correction.
                    int stood;

                    if (NavWalker.TryResolveZ(Map, new Point3D(arrival.X, arrival.Y, arrival.Z), out stood)
                        && stood != arrival.Z)
                    {
                        Corrected.Add(String.Format(
                            "{0} arrival {1},{2}: {3} -> {4}",
                            arrival.DestinationId, arrival.X, arrival.Y, arrival.Z, stood));

                        arrival.RefZ = arrival.Z;
                        arrival.Z = stood;
                    }

                    kept.Add(arrival);
                    reachable.Add(arrival.DestinationId);
                }

                var keptDestinations = new List<NavDestination>();

                foreach (NavDestination destination in Destinations)
                {
                    if (reachable.Contains(destination.Id))
                    {
                        keptDestinations.Add(destination);
                        continue;
                    }

                    SkippedNoReach.Add(destination.Id);
                }

                Arrivals.Clear();
                Arrivals.AddRange(kept);

                Destinations.Clear();
                Destinations.AddRange(keptDestinations);

                if (SkippedNoReach.Count > 0)
                {
                    Islands.Add(String.Format(
                        "{0} destination(s) skipped: no arrival within the {1}-tile hop cap of any "
                        + "waypoint, so a bot sent there could not route away again. {2}",
                        SkippedNoReach.Count, cap, Name(SkippedNoReach)));
                }
            }

            /// <summary>
            /// Walk a corridor from each cut-off piece of the proposal to the graph we have.
            ///
            /// The joins made during selection are opportunistic: they exist only where one of
            /// THEIR edges happens to cross into ground we authored. That is not where a road
            /// meets a town. Britain's own graph does not reach the south gate, so the road south
            /// was cut at the edge of our zones and everything beyond it floated free - one join
            /// out of 482 waypoints.
            ///
            /// This asks the other question: for each piece that cannot reach us, which of its
            /// waypoints is nearest to one of ours, and is there a road between them? The corridor
            /// is walked and subdivided like any other edge, so a join is never longer than a hop
            /// and is verified rather than asserted.
            ///
            /// Returns true when it queued work, so the caller keeps stepping - each corridor is a
            /// flood-fill and belongs in the same budget as the rest.
            /// </summary>
            public bool PlanComponentJoins()
            {
                if (_joinsPlanned)
                {
                    return false;
                }

                _joinsPlanned = true;

                HashSet<string> reached = Reachable();
                Dictionary<string, List<string>> links = BuildLinks();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int queued = 0;

                foreach (KeyValuePair<string, NavWaypoint> pair in Waypoints)
                {
                    if (reached.Contains(pair.Key) || seen.Contains(pair.Key))
                    {
                        continue;
                    }

                    // The whole cut-off piece this waypoint belongs to, so one corridor is walked
                    // for it rather than one per waypoint.
                    List<NavWaypoint> piece = Piece(pair.Key, links, reached, seen);

                    NavWaypoint mine = null;
                    NavWaypoint ours = null;
                    int best = Int32.MaxValue;

                    for (int i = 0; i < piece.Count; i++)
                    {
                        NavWaypoint near = NavigationSystem.Graph.Nearest(
                            new Point3D(piece[i].X, piece[i].Y, piece[i].Z), Map, JoinReach);

                        if (near == null)
                        {
                            continue;
                        }

                        int span = Math.Max(
                            Math.Abs(near.X - piece[i].X), Math.Abs(near.Y - piece[i].Y));

                        if (span < best)
                        {
                            best = span;
                            mine = piece[i];
                            ours = near;
                        }
                    }

                    if (mine == null || ours == null)
                    {
                        continue;
                    }

                    // THE ROAD RULE, APPLIED ONCE RATHER THAN PER CANDIDATE. The scan above is
                    // choosing two things at once - which waypoint of the stranded piece to join
                    // FROM, and which of ours to join TO - over a piece that can hold hundreds, so
                    // flooding inside it would cost a flood per piece-waypoint per candidate. The
                    // cheap Chebyshev pass settles the first question and this re-asks only the
                    // second, for the winner: one extra shortlist, six floods.
                    ours = NavRejoin.NearestByRoad(
                        Map, new Point3D(mine.X, mine.Y, mine.Z), JoinReach) ?? ours;

                    Pending.Add(new PendingEdge
                    {
                        Edge = new NavEdge
                        {
                            From = ours.Id, To = mine.Id, KindName = "walk", Tags = "road"
                        },
                        FromId = ours.Id,
                        ToId = mine.Id,
                        IsJoin = true,
                        OurId = ours.Id
                    });

                    queued++;
                }

                Links += queued;

                return queued > 0;
            }

            /// <summary>
            /// Walk a corridor to every destination none of whose arrivals a bot could leave.
            ///
            /// Nav.Data's rule, applied before the write rather than warned about at the next
            /// boot: an arrival further than the hop cap from every waypoint is one a bot can be
            /// sent to and cannot route away from. uo-offline authored against a 38-tile leg, so
            /// a forge 20 tiles from its road is fine in their data and skipped in ours - eight
            /// destinations in the Britain-Trinsic box, all for our cap and none for theirs.
            ///
            /// So, for each: the arrival nearest to any REACHABLE waypoint (the proposal's, once
            /// the joins have walked, or ours), a waypoint minted on that arrival's tile, and a
            /// corridor queued from the one to the other - walked, engine-pathed and subdivided
            /// like any edge, so it is never longer than a hop and never asserted. Listed in the
            /// proposal with its length and the waypoint it starts from, and flagged for review
            /// past two hops, so a far one is visible before Save rather than refused.
            ///
            /// Runs once, after the component joins, so the reachable set it measures against is
            /// the final one. Returns true when it queued work, like PlanComponentJoins.
            /// </summary>
            public bool PlanArrivalCorridors()
            {
                if (_corridorsPlanned)
                {
                    return false;
                }

                _corridorsPlanned = true;

                int cap = NavigationSystem.HopMaxTiles;
                HashSet<string> reached = Reachable();
                var reachable = new List<NavWaypoint>();

                foreach (KeyValuePair<string, NavWaypoint> pair in Waypoints)
                {
                    if (reached.Contains(pair.Key))
                    {
                        reachable.Add(pair.Value);
                    }
                }

                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    if (reached.Contains(Subdivisions[i].Id))
                    {
                        reachable.Add(Subdivisions[i]);
                    }
                }

                var points = new List<Point2D>(reachable.Count);

                for (int i = 0; i < reachable.Count; i++)
                {
                    points.Add(new Point2D(reachable[i].X, reachable[i].Y));
                }

                int queued = 0;

                foreach (NavDestination destination in Destinations)
                {
                    NavArrival chosen = null;
                    NavWaypoint from = null;
                    int best = Int32.MaxValue;
                    bool covered = false;

                    foreach (NavArrival arrival in Arrivals)
                    {
                        if (!Insensitive.Equals(arrival.DestinationId, destination.Id))
                        {
                            continue;
                        }

                        if (InReach(arrival.X, arrival.Y, points, cap))
                        {
                            covered = true;
                            break;
                        }

                        // The nearest reachable waypoint to this arrival: the proposal's, then ours.
                        for (int i = 0; i < reachable.Count; i++)
                        {
                            int span = Math.Max(
                                Math.Abs(reachable[i].X - arrival.X), Math.Abs(reachable[i].Y - arrival.Y));

                            if (span < best)
                            {
                                best = span;
                                from = reachable[i];
                                chosen = arrival;
                            }
                        }

                        NavWaypoint ours = NavigationSystem.Graph.Nearest(
                            new Point3D(arrival.X, arrival.Y, arrival.Z), Map, JoinReach);

                        if (ours != null)
                        {
                            int span = Math.Max(Math.Abs(ours.X - arrival.X), Math.Abs(ours.Y - arrival.Y));

                            if (span < best)
                            {
                                best = span;
                                from = ours;
                                chosen = arrival;
                            }
                        }
                    }

                    if (covered || chosen == null || from == null || best > JoinReach)
                    {
                        continue;
                    }

                    // A waypoint on the arrival's own tile, at the Z a mobile stands at there, so
                    // the arrival is inside the cap of it by zero tiles.
                    int z;

                    if (!NavWalker.TryResolveZ(Map, new Point3D(chosen.X, chosen.Y, chosen.Z), out z))
                    {
                        z = chosen.Z;
                    }

                    var minted = new NavWaypoint
                    {
                        Id = MintId(destination.Id),
                        MapName = Map.Name,
                        X = chosen.X,
                        Y = chosen.Y,
                        Z = z,
                        ArrivalRange = 0,
                        Tags = "road",
                        Source = SourceTag
                    };

                    Subdivisions.Add(minted);

                    bool fromOurs = NavigationSystem.Graph.Node(from.Id) != null && !Waypoints.ContainsKey(from.Id);

                    Pending.Add(new PendingEdge
                    {
                        Edge = new NavEdge { From = from.Id, To = minted.Id, KindName = "walk", Tags = "road" },
                        FromId = from.Id,
                        ToId = minted.Id,
                        IsJoin = fromOurs,
                        OurId = fromOurs ? from.Id : null
                    });

                    if (fromOurs)
                    {
                        Links++;
                    }

                    Corridors.Add(new Corridor
                    {
                        DestinationId = destination.Id,
                        FromId = from.Id,
                        ToId = minted.Id,
                        X = chosen.X,
                        Y = chosen.Y,
                        Tiles = best,
                        Review = best > cap * 2,
                        Arrival = chosen
                    });

                    queued++;
                }

                return queued > 0;
            }

            /// <summary>
            /// After the corridors have walked: point the destination and its arrival at the
            /// minted waypoint where the corridor arrived, and take the waypoint back out where it
            /// did not - a corridor that failed is already in Failures with the walker's reason,
            /// and its waypoint must not surface as a stranded record the author never asked for.
            /// </summary>
            public void SettleCorridors()
            {
                for (int i = 0; i < Corridors.Count; i++)
                {
                    Corridor corridor = Corridors[i];
                    bool walked = false;

                    for (int j = 0; j < Edges.Count && !walked; j++)
                    {
                        walked = Insensitive.Equals(Edges[j].From, corridor.ToId)
                            || Insensitive.Equals(Edges[j].To, corridor.ToId);
                    }

                    corridor.Walked = walked;

                    if (!walked)
                    {
                        for (int j = Subdivisions.Count - 1; j >= 0; j--)
                        {
                            if (Insensitive.Equals(Subdivisions[j].Id, corridor.ToId))
                            {
                                Subdivisions.RemoveAt(j);
                            }
                        }

                        continue;
                    }

                    // A route to the destination ends at the first of its declared waypoints that
                    // exists (Nav.cs, TryResolveWaypoint), and the arrival is appended after it -
                    // so the corridor's end goes FIRST, or the route would still end at the
                    // reference's waypoint twenty tiles away and the final leg would be the very
                    // hop this corridor exists to remove.
                    foreach (NavDestination destination in Destinations)
                    {
                        if (Insensitive.Equals(destination.Id, corridor.DestinationId))
                        {
                            destination.WaypointIds = String.IsNullOrEmpty(destination.WaypointIds)
                                ? corridor.ToId
                                : corridor.ToId + " " + destination.WaypointIds;
                        }
                    }

                    corridor.Arrival.WaypointIds = corridor.ToId;
                }
            }

            /// <summary>Every waypoint in one cut-off piece, marking them all seen.</summary>
            private List<NavWaypoint> Piece(
                string start, Dictionary<string, List<string>> links,
                HashSet<string> reached, HashSet<string> seen)
            {
                var members = new List<NavWaypoint>();
                var queue = new Queue<string>();

                seen.Add(start);
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    NavWaypoint waypoint;

                    if (Waypoints.TryGetValue(current, out waypoint))
                    {
                        members.Add(waypoint);
                    }

                    List<string> neighbours;

                    if (!links.TryGetValue(current, out neighbours))
                    {
                        continue;
                    }

                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        if (!reached.Contains(neighbours[i]) && seen.Add(neighbours[i]))
                        {
                            queue.Enqueue(neighbours[i]);
                        }
                    }
                }

                return members;
            }

            /// <summary>Which proposed waypoints can already reach the graph we have.</summary>
            private HashSet<string> Reachable()
            {
                Dictionary<string, List<string>> links = BuildLinks();
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

                return reached;
            }

            private Dictionary<string, List<string>> BuildLinks()
            {
                var links = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Edges.Count; i++)
                {
                    Add(links, Edges[i].From, Edges[i].To);
                    Add(links, Edges[i].To, Edges[i].From);
                }

                return links;
            }

            /// <summary>Whether any surviving waypoint is within the cap of this tile.</summary>
            private bool InReach(int x, int y, List<Point2D> waypoints, int cap)
            {
                for (int i = 0; i < waypoints.Count; i++)
                {
                    if (Math.Max(Math.Abs(waypoints[i].X - x), Math.Abs(waypoints[i].Y - y)) <= cap)
                    {
                        return true;
                    }
                }

                // And ours, which an adopted arrival at the edge of a join legitimately uses. With
                // the job's Map: NavGraph.Nearest answers null for a null map, which made this
                // line dead for as long as it passed one.
                return NavigationSystem.Graph.Nearest(new Point3D(x, y, 0), Map, cap) != null;
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

                // A waypoint this job minted: the end of an arrival corridor stands on the
                // arrival's tile and exists nowhere else.
                for (int i = 0; i < Subdivisions.Count; i++)
                {
                    if (Insensitive.Equals(Subdivisions[i].Id, id))
                    {
                        return Subdivisions[i];
                    }
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

        /// <summary>
        /// One of our waypoints the rebase asks to remove, and the proposed one it folds into.
        /// `Tiles` is how far it stood from the proposed road, which is what the author is really
        /// judging: a merge at one tile is the same piece of road under two names, a merge at six
        /// has moved something.
        /// </summary>
        private sealed class Removal
        {
            public string Id;
            public string Into;
            public int X;
            public int Y;
            public int Tiles;
        }

        /// <summary>
        /// A record that named a removed waypoint, and the list it has to carry instead. `ShapeId`
        /// is the editor's own identifier for the record, so accepting a proposal is a matter of
        /// posting these as `updates` rather than of matching records up again on the way in.
        /// </summary>
        private sealed class Rewrite
        {
            public string Kind;
            public string ShapeId;
            public string Owner;
            public string From;
            public string To;
        }

        /// <summary>
        /// A corridor walked to a destination whose arrivals were all beyond the hop cap. Carries
        /// what the proposal lists: where it starts, how far it reaches, and whether that is far
        /// enough to want a look before Save.
        /// </summary>
        private sealed class Corridor
        {
            public string DestinationId;
            public string FromId;
            public string ToId;
            public int X;
            public int Y;
            public int Tiles;
            public bool Review;
            public bool Walked;
            public NavArrival Arrival;
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

            // Done when the JOB is done, not when the last edge has been walked: joins, islands
            // and pruning run after that, and the editor accepts the first "done" it polls.
            bool done = job.Finished;

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
                .Append(",\"noArrival\":").Append(job.SkippedNoArrival)
                .Append(",\"adopted\":").Append(job.SkippedAdopted).Append("},\n");
            builder.Append("  \"rebase\": ").Append(job.Rebase ? "true" : "false").Append(",\n");
            builder.Append("  \"mergeRadius\": ").Append(job.Rebase ? MergeRadius : 0).Append(",\n");
            builder.Append("  \"links\": ").Append(job.Links).Append(",\n");
            builder.Append("  \"blocked\": ").Append(job.Blocked ? "true" : "false").Append(",\n");
            builder.Append("  \"reachable\": ").Append(job.ReachedCount).Append(",\n");

            WriteWaypoints(builder, job);
            WriteEdges(builder, job);
            WriteDestinations(builder, job);
            WriteArrivals(builder, job);
            WriteFailures(builder, job);
            WriteStrings(builder, "stranded", job.Stranded, true);
            WriteStrings(builder, "unreachable", job.Unreachable, true);
            WriteStrings(builder, "folded", job.Folded, true);
            WriteStrings(builder, "corrected", job.Corrected, true);
            WriteCorridors(builder, job);
            WriteRemovals(builder, job);
            WriteRewrites(builder, job);
            WriteStrings(builder, "relinks", job.Relinks, true);
            WriteStrings(builder, "removedEdges", job.RemovedEdges, true);
            WriteStrings(builder, "withdrawn", job.Withdrawn, true);
            WriteStrings(builder, "skippedNoReach", job.SkippedNoReach, true);
            WriteStrings(builder, "islands", job.Islands, false);

            builder.Append("}\n");

            string error;

            if (!AtomicFile.Write(ProposalPath, builder.ToString(), out error))
            {
                Log.Error("Adopt proposal not written: {0}", error);
            }
        }

        /// <summary>
        /// Every arrival corridor, walked or not: the destination, the waypoint it starts from,
        /// the minted waypoint it ends on, its straight-line length, and whether that length wants
        /// a look (over two hops). A corridor that did not walk is in `failures` as well.
        /// </summary>
        /// <summary>
        /// The waypoints of ours a rebase asks to remove. Empty on an ordinary adopt, and the
        /// editor draws nothing for an empty list, so an ordinary proposal is unchanged in shape.
        /// </summary>
        private static void WriteRemovals(StringBuilder builder, Job job)
        {
            builder.Append("  \"removals\": [\n");

            for (int i = 0; i < job.Removals.Count; i++)
            {
                Removal removal = job.Removals[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"id\":").Append(Json.Quote(removal.Id))
                    .Append(",\"into\":").Append(Json.Quote(removal.Into))
                    .Append(",\"x\":").Append(removal.X)
                    .Append(",\"y\":").Append(removal.Y)
                    .Append(",\"tiles\":").Append(removal.Tiles)
                    .Append("}");
            }

            builder.Append(job.Removals.Count > 0 ? "\n" : "").Append("  ],\n");
        }

        /// <summary>
        /// Every approach or step list that has to move because a removal took its waypoint away,
        /// keyed by the editor's own shape id so accepting is a matter of posting them as updates.
        /// </summary>
        private static void WriteRewrites(StringBuilder builder, Job job)
        {
            builder.Append("  \"rewrites\": [\n");

            for (int i = 0; i < job.Rewrites.Count; i++)
            {
                Rewrite rewrite = job.Rewrites[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"kind\":").Append(Json.Quote(rewrite.Kind))
                    .Append(",\"shape\":").Append(Json.Quote(rewrite.ShapeId))
                    .Append(",\"owner\":").Append(Json.Quote(rewrite.Owner))
                    .Append(",\"from\":").Append(Json.Quote(rewrite.From))
                    .Append(",\"to\":").Append(Json.Quote(rewrite.To))
                    .Append("}");
            }

            builder.Append(job.Rewrites.Count > 0 ? "\n" : "").Append("  ],\n");
        }

        private static void WriteCorridors(StringBuilder builder, Job job)
        {
            builder.Append("  \"corridors\": [\n");

            for (int i = 0; i < job.Corridors.Count; i++)
            {
                Corridor corridor = job.Corridors[i];

                builder.Append(i > 0 ? ",\n" : "");
                builder.Append("    {\"destination\":").Append(Json.Quote(corridor.DestinationId))
                    .Append(",\"from\":").Append(Json.Quote(corridor.FromId))
                    .Append(",\"to\":").Append(Json.Quote(corridor.ToId))
                    .Append(",\"x\":").Append(corridor.X)
                    .Append(",\"y\":").Append(corridor.Y)
                    .Append(",\"tiles\":").Append(corridor.Tiles)
                    .Append(",\"review\":").Append(corridor.Review ? "true" : "false")
                    .Append(",\"walked\":").Append(corridor.Walked ? "true" : "false")
                    .Append("}");
            }

            builder.Append(job.Corridors.Count > 0 ? "\n" : "").Append("  ],\n");
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
                .Append(",\"z\":").Append(waypoint.Z);

            if (waypoint.RefZ.HasValue)
            {
                builder.Append(",\"refZ\":").Append(waypoint.RefZ.Value);
            }

            builder.Append(",\"arrivalRange\":").Append(waypoint.ArrivalRange)
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
                    .Append(",\"z\":").Append(arrival.Z);

                if (arrival.RefZ.HasValue)
                {
                    builder.Append(",\"refZ\":").Append(arrival.RefZ.Value);
                }

                builder.Append(",\"exclusive\":false,\"waypoints\":")
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
