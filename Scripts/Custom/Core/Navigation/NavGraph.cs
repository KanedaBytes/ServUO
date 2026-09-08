using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>What a consumer has to do to take one step of a route.</summary>
    public enum NavStepKind
    {
        /// <summary>Walk to this point. Always within the hop cap of the previous step.</summary>
        Walk,

        /// <summary>
        /// A gate hop: the previous step and this one are not connected on foot. The consumer
        /// handles it - stepping onto a teleporter, taking a moongate, or for a staff tool,
        /// simply moving there.
        /// </summary>
        Transition,

        /// <summary>The final standing spot, produced by the arrival picker.</summary>
        Arrival
    }

    public sealed class NavStep
    {
        public NavStep(Point3D point, Map map, string waypointId, NavStepKind kind)
            : this(point, map, waypointId, kind, 0)
        {
        }

        public NavStep(Point3D point, Map map, string waypointId, NavStepKind kind, int range)
        {
            Point = point;
            Map = map;
            WaypointId = waypointId;
            Kind = kind;
            Range = range;
        }

        public Point3D Point { get; private set; }

        public Map Map { get; private set; }

        /// <summary>Null for an arrival step, which is not a graph node.</summary>
        public string WaypointId { get; private set; }

        public NavStepKind Kind { get; private set; }

        /// <summary>
        /// For an Arrival step, how close counts as arrived - the picked arrival's own range
        /// (NavArrival.Range). Zero is the tile itself. Ignored for a walk hop, whose tolerance is
        /// the waypoint's (NavWalker.ArrivalRangeFor).
        /// </summary>
        public int Range { get; private set; }

        public override string ToString()
        {
            return String.Format("{0} {1} {2}", Kind, WaypointId ?? "-", Point);
        }
    }

    public sealed class NavRoute
    {
        public NavRoute(IList<NavStep> steps, double cost)
        {
            Steps = steps;
            Cost = cost;
        }

        public IList<NavStep> Steps { get; private set; }

        public double Cost { get; private set; }

        public int Count
        {
            get { return Steps == null ? 0 : Steps.Count; }
        }

        public override string ToString()
        {
            return String.Format("{0} step(s), cost {1:F1}", Count, Cost);
        }
    }

    /// <summary>
    /// The travel graph: waypoints joined by explicit edges, searched with A*.
    ///
    /// GAME THREAD ONLY. Nothing here is locked, and a route request may be followed by a
    /// MovementPath call, whose FastAStarAlgorithm working set is shared static state. A
    /// background thread must marshal through LoopQueue.
    ///
    /// Walk edges never cross facets, so the structure is one graph per map in practice even
    /// though it is stored as one array. Gate edges are the only inter-facet links and are the
    /// only ones a consumer has to handle specially.
    /// </summary>
    public sealed class NavGraph
    {
        /// <summary>
        /// Flat cost charged for a gate hop before tag multipliers. Distance is meaningless
        /// across a teleport, so this is what decides whether the router prefers walking round
        /// or gating: roughly "a gate is worth thirty tiles of walking".
        /// </summary>
        public const double GateBaseCost = 30.0;

        private struct Link
        {
            public int To;
            public double Cost;
            public NavEdgeKind Kind;
        }

        private readonly Dictionary<string, int> _index =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly List<NavWaypoint> _nodes = new List<NavWaypoint>();
        private readonly List<List<Link>> _links = new List<List<Link>>();
        private readonly Dictionary<Map, List<int>> _byMap = new Dictionary<Map, List<int>>();

        private readonly List<string> _warnings = new List<string>();

        private readonly Dictionary<string, NavRoute> _cache =
            new Dictionary<string, NavRoute>(StringComparer.OrdinalIgnoreCase);

        private int[] _components;
        private int _componentCount;

        private double _minMultiplier = 1.0;
        private int _walkEdges;
        private int _gateEdges;

        public int NodeCount
        {
            get { return _nodes.Count; }
        }

        public int WalkEdgeCount
        {
            get { return _walkEdges; }
        }

        public int GateEdgeCount
        {
            get { return _gateEdges; }
        }

        public int ComponentCount
        {
            get { return _componentCount; }
        }

        public IList<NavWaypoint> Nodes
        {
            get { return _nodes.AsReadOnly(); }
        }

        /// <summary>Problems found while building: dropped edges, over-cap hops, and so on.</summary>
        public IList<string> Warnings
        {
            get { return _warnings.AsReadOnly(); }
        }

        public int CachedRouteCount
        {
            get { return _cache.Count; }
        }

        // ---- construction ----

        /// <summary>
        /// Builds the adjacency from validated records. Referential problems are collected as
        /// warnings and the offending edge is dropped, never fatal - one typo must not take
        /// navigation offline.
        /// </summary>
        public void Build(NavigationStore store, IDictionary<string, double> costTags, int hopCap)
        {
            _index.Clear();
            _nodes.Clear();
            _links.Clear();
            _byMap.Clear();
            _warnings.Clear();
            _cache.Clear();
            _components = null;
            _componentCount = 0;
            _walkEdges = 0;
            _gateEdges = 0;

            _minMultiplier = 1.0;

            foreach (double multiplier in costTags.Values)
            {
                if (multiplier < _minMultiplier)
                {
                    _minMultiplier = multiplier;
                }
            }

            foreach (NavWaypoint waypoint in store.Waypoints)
            {
                _index[waypoint.Id] = _nodes.Count;
                _nodes.Add(waypoint);
                _links.Add(new List<Link>());

                List<int> perMap;

                if (!_byMap.TryGetValue(waypoint.Map, out perMap))
                {
                    perMap = new List<int>();
                    _byMap[waypoint.Map] = perMap;
                }

                perMap.Add(_nodes.Count - 1);
            }

            foreach (NavEdge edge in store.Edges)
            {
                AddEdge(edge, costTags, hopCap);
            }

            LabelComponents();
        }

        private void AddEdge(NavEdge edge, IDictionary<string, double> costTags, int hopCap)
        {
            int from, to;

            if (!_index.TryGetValue(edge.From, out from))
            {
                _warnings.Add(String.Format(
                    "edge '{0}' -> '{1}' dropped: no waypoint '{0}'", edge.From, edge.To));
                return;
            }

            if (!_index.TryGetValue(edge.To, out to))
            {
                _warnings.Add(String.Format(
                    "edge '{0}' -> '{1}' dropped: no waypoint '{1}'", edge.From, edge.To));
                return;
            }

            NavWaypoint a = _nodes[from];
            NavWaypoint b = _nodes[to];

            double multiplier = 1.0;

            if (edge.TagList != null)
            {
                for (int i = 0; i < edge.TagList.Length; i++)
                {
                    double tagMultiplier;

                    if (costTags.TryGetValue(edge.TagList[i], out tagMultiplier))
                    {
                        multiplier *= tagMultiplier;
                    }
                }
            }

            double cost;

            if (edge.Kind == NavEdgeKind.Gate)
            {
                cost = GateBaseCost * multiplier;
                _gateEdges++;
            }
            else
            {
                if (a.Map != b.Map)
                {
                    // A walk edge across facets is not walkable by any means. Dropping it keeps
                    // the per-map graphs honest; the author wanted kind "gate".
                    _warnings.Add(String.Format(
                        "walk edge '{0}' -> '{1}' dropped: it crosses facets ({2} to {3}). Use kind \"gate\".",
                        edge.From,
                        edge.To,
                        a.MapName,
                        b.MapName));
                    return;
                }

                int distance = Chebyshev(a.Location, b.Location);

                if (distance > hopCap)
                {
                    // Warn, do not reject: uo-offline-server's policy, and the right one. The
                    // edge may still be walkable; it is the author's call whether to split it.
                    _warnings.Add(String.Format(
                        "edge '{0}' -> '{1}' is {2} tiles, over the {3}-tile hop cap - the engine may not be able to path it",
                        edge.From,
                        edge.To,
                        distance,
                        hopCap));
                }

                cost = distance * multiplier;

                if (cost <= 0.0)
                {
                    cost = 0.01;
                }

                _walkEdges++;
            }

            AddLink(from, to, cost, edge.Kind);
            AddLink(to, from, cost, edge.Kind);
        }

        private void AddLink(int from, int to, double cost, NavEdgeKind kind)
        {
            List<Link> list = _links[from];

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].To == to)
                {
                    // Duplicate declaration, or the reverse of one already added. Keep the
                    // cheaper of the two rather than counting the edge twice.
                    if (cost < list[i].Cost)
                    {
                        Link existing = list[i];
                        existing.Cost = cost;
                        existing.Kind = kind;
                        list[i] = existing;
                    }

                    return;
                }
            }

            list.Add(new Link { To = to, Cost = cost, Kind = kind });
        }

        /// <summary>
        /// Labels connected components over WALK edges only. A gate makes two islands mutually
        /// reachable but not mutually walkable, and the warning this feeds is about walkability.
        /// </summary>
        private void LabelComponents()
        {
            _components = new int[_nodes.Count];

            for (int i = 0; i < _components.Length; i++)
            {
                _components[i] = -1;
            }

            _componentCount = 0;

            var queue = new Queue<int>();

            for (int i = 0; i < _components.Length; i++)
            {
                if (_components[i] != -1)
                {
                    continue;
                }

                int component = _componentCount++;

                _components[i] = component;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    List<Link> links = _links[current];

                    for (int j = 0; j < links.Count; j++)
                    {
                        if (links[j].Kind != NavEdgeKind.Walk)
                        {
                            continue;
                        }

                        int next = links[j].To;

                        if (_components[next] == -1)
                        {
                            _components[next] = component;
                            queue.Enqueue(next);
                        }
                    }
                }
            }
        }

        // ---- lookup ----

        public NavWaypoint Node(string id)
        {
            int index;
            return id != null && _index.TryGetValue(id, out index) ? _nodes[index] : null;
        }

        public bool Contains(string id)
        {
            return id != null && _index.ContainsKey(id);
        }

        public int ComponentOf(string id)
        {
            int index;

            if (id == null || _components == null || !_index.TryGetValue(id, out index))
            {
                return -1;
            }

            return _components[index];
        }

        public bool SameComponent(string a, string b)
        {
            int ca = ComponentOf(a);
            return ca >= 0 && ca == ComponentOf(b);
        }

        public IList<NavWaypoint> Neighbours(string id)
        {
            var result = new List<NavWaypoint>();

            int index;

            if (id == null || !_index.TryGetValue(id, out index))
            {
                return result;
            }

            List<Link> links = _links[index];

            for (int i = 0; i < links.Count; i++)
            {
                result.Add(_nodes[links[i].To]);
            }

            return result;
        }

        public int EdgeCountOf(string id)
        {
            int index;
            return id != null && _index.TryGetValue(id, out index) ? _links[index].Count : 0;
        }

        /// <summary>
        /// Nearest waypoint on a facet by Chebyshev distance. maxTiles of 0 or less means no
        /// limit. Scans that facet's node list only.
        /// </summary>
        public NavWaypoint Nearest(Point3D from, Map map, int maxTiles)
        {
            List<int> candidates;

            if (map == null || !_byMap.TryGetValue(map, out candidates))
            {
                return null;
            }

            NavWaypoint best = null;
            int bestDistance = Int32.MaxValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                NavWaypoint node = _nodes[candidates[i]];
                int distance = Chebyshev(from, node.Location);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = node;
                }
            }

            if (best != null && maxTiles > 0 && bestDistance > maxTiles)
            {
                return null;
            }

            return best;
        }

        public IList<NavWaypoint> NodesOn(Map map)
        {
            var result = new List<NavWaypoint>();

            List<int> candidates;

            if (map == null || !_byMap.TryGetValue(map, out candidates))
            {
                return result;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                result.Add(_nodes[candidates[i]]);
            }

            return result;
        }

        public IList<Map> Maps
        {
            get { return new List<Map>(_byMap.Keys); }
        }

        // ---- search ----

        public void ClearCache()
        {
            _cache.Clear();
        }

        /// <summary>
        /// A* from one waypoint to another, returning the waypoint sequence inclusive of both
        /// ends. Cached by (from, to); the cache is cleared on reload and whenever it overflows.
        ///
        /// The heuristic is Chebyshev distance scaled by the cheapest cost multiplier in the
        /// data, which keeps it admissible when a tag makes an edge cheaper than its length
        /// (a "road" at 0.9, say). Nodes on a different facet from the goal get a heuristic of
        /// zero, so a cross-facet search degrades to Dijkstra rather than going wrong.
        /// </summary>
        public bool TryFindPath(string fromId, string toId, int cacheLimit, out NavRoute route, out string error)
        {
            route = null;
            error = null;

            int from, to;

            if (fromId == null || !_index.TryGetValue(fromId, out from))
            {
                error = String.Format("no waypoint '{0}'", fromId);
                return false;
            }

            if (toId == null || !_index.TryGetValue(toId, out to))
            {
                error = String.Format("no waypoint '{0}'", toId);
                return false;
            }

            string key = fromId + ">" + toId;

            if (_cache.TryGetValue(key, out route))
            {
                return true;
            }

            route = Search(from, to);

            if (route == null)
            {
                error = String.Format("no route from '{0}' to '{1}'", fromId, toId);
                return false;
            }

            // No LRU on purpose. A wholesale clear is one line and cannot leak; the cost of a
            // cold cache is one A* over a few hundred nodes.
            if (cacheLimit > 0 && _cache.Count >= cacheLimit)
            {
                _cache.Clear();
            }

            _cache[key] = route;
            return true;
        }

        private NavRoute Search(int from, int to)
        {
            if (from == to)
            {
                NavWaypoint only = _nodes[from];

                return new NavRoute(
                    new List<NavStep> { new NavStep(only.Location, only.Map, only.Id, NavStepKind.Walk) },
                    0.0);
            }

            int count = _nodes.Count;

            var cost = new double[count];
            var previous = new int[count];
            var previousKind = new NavEdgeKind[count];
            var closed = new bool[count];

            for (int i = 0; i < count; i++)
            {
                cost[i] = Double.PositiveInfinity;
                previous[i] = -1;
            }

            Point3D goal = _nodes[to].Location;
            Map goalMap = _nodes[to].Map;

            var open = new NavHeap(count);

            cost[from] = 0.0;
            open.Push(from, Heuristic(from, goal, goalMap));

            while (open.Count > 0)
            {
                int current = open.Pop();

                if (current == to)
                {
                    return Reconstruct(previous, previousKind, from, to, cost[to]);
                }

                if (closed[current])
                {
                    continue;
                }

                closed[current] = true;

                List<Link> links = _links[current];

                for (int i = 0; i < links.Count; i++)
                {
                    Link link = links[i];

                    if (closed[link.To])
                    {
                        continue;
                    }

                    double candidate = cost[current] + link.Cost;

                    if (candidate >= cost[link.To])
                    {
                        continue;
                    }

                    cost[link.To] = candidate;
                    previous[link.To] = current;
                    previousKind[link.To] = link.Kind;

                    open.Push(link.To, candidate + Heuristic(link.To, goal, goalMap));
                }
            }

            return null;
        }

        private double Heuristic(int node, Point3D goal, Map goalMap)
        {
            NavWaypoint waypoint = _nodes[node];

            if (waypoint.Map != goalMap)
            {
                return 0.0;
            }

            return Chebyshev(waypoint.Location, goal) * _minMultiplier;
        }

        private NavRoute Reconstruct(int[] previous, NavEdgeKind[] previousKind, int from, int to, double cost)
        {
            var reversed = new List<int>();

            int current = to;

            while (current != -1)
            {
                reversed.Add(current);

                if (current == from)
                {
                    break;
                }

                current = previous[current];
            }

            reversed.Reverse();

            var steps = new List<NavStep>(reversed.Count);

            for (int i = 0; i < reversed.Count; i++)
            {
                int node = reversed[i];
                NavWaypoint waypoint = _nodes[node];

                // The first step is where you already are, so it is never a transition. Every
                // other step's kind is the kind of the edge that reached it.
                NavStepKind kind = i > 0 && previousKind[node] == NavEdgeKind.Gate
                    ? NavStepKind.Transition
                    : NavStepKind.Walk;

                steps.Add(new NavStep(waypoint.Location, waypoint.Map, waypoint.Id, kind));
            }

            return new NavRoute(steps, cost);
        }

        public static int Chebyshev(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);

            return dx > dy ? dx : dy;
        }

        /// <summary>Binary min-heap keyed on the A* priority. Allows duplicate entries.</summary>
        private sealed class NavHeap
        {
            private int[] _items;
            private double[] _keys;
            private int _count;

            public NavHeap(int capacity)
            {
                if (capacity < 8)
                {
                    capacity = 8;
                }

                _items = new int[capacity];
                _keys = new double[capacity];
            }

            public int Count
            {
                get { return _count; }
            }

            public void Push(int item, double key)
            {
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _count * 2);
                    Array.Resize(ref _keys, _count * 2);
                }

                int i = _count++;
                _items[i] = item;
                _keys[i] = key;

                while (i > 0)
                {
                    int parent = (i - 1) / 2;

                    if (_keys[parent] <= _keys[i])
                    {
                        break;
                    }

                    Swap(parent, i);
                    i = parent;
                }
            }

            public int Pop()
            {
                int result = _items[0];

                _count--;
                _items[0] = _items[_count];
                _keys[0] = _keys[_count];

                int i = 0;

                while (true)
                {
                    int left = (i * 2) + 1;
                    int right = left + 1;
                    int smallest = i;

                    if (left < _count && _keys[left] < _keys[smallest])
                    {
                        smallest = left;
                    }

                    if (right < _count && _keys[right] < _keys[smallest])
                    {
                        smallest = right;
                    }

                    if (smallest == i)
                    {
                        break;
                    }

                    Swap(smallest, i);
                    i = smallest;
                }

                return result;
            }

            private void Swap(int a, int b)
            {
                int item = _items[a];
                _items[a] = _items[b];
                _items[b] = item;

                double key = _keys[a];
                _keys[a] = _keys[b];
                _keys[b] = key;
            }
        }
    }
}
