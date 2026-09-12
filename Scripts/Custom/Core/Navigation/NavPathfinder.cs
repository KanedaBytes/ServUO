// NavPathfinder.cs - the instrument for evaluating the tile pathfinder, and nothing more.
//
// WHAT THIS IS FOR
// ----------------
// The fleet walks on ServUO's FastAStarAlgorithm (Scripts/Services/Pathing/FastAStarAlgorithm.cs).
// Three of its properties are not written down anywhere else in this tree, and one of them looks
// like a defect:
//
//   MaxDepth = 300 expansions (:26), AreaSize = 38 (:27), and a Heuristic (:33-43) returning the
//   SQUARED distance with x and y scaled by 11 - 121(dx^2+dy^2) + dz^2 - against a cost that
//   accumulates at 1 per step (:129). At twelve tiles h is about 34,800 against g of 12, so g is
//   noise and the search is heuristic-dominated. REVIEW.md:85 is right that "greedy" is loose
//   shorthand; the accurate statement is that its heuristic is inflated by a factor that GROWS
//   with distance, so it carries no optimality guarantee at all.
//
//   A SINGLE DEAD END ABORTS THE WHOLE SEARCH. :118 is `if (count == 0) break;`, which leaves the
//   `while (m_OpenList != -1)` loop the first time ANY expanded cell has no legal successor -
//   discarding an open list that may hold hundreds of live frontier nodes. ModernUO's otherwise
//   line-for-line descendant of this same RunUO file `continue`s instead
//   (BitmapAStarAlgorithm.cs:244-247). That is the most likely mechanism behind the directional
//   behaviour the Navigation README documents: a frontier walks into a concavity, the first cell
//   with no exit kills the search, and the same pair of tiles paths one way and not the other.
//
//   FindBest (:298-320) is a linear scan of the whole open list per expansion, so cost grows
//   quadratically with the budget. Raising the budget alone is the wrong lever.
//
// WHY A COPY OF THE ALGORITHM RATHER THAN INSTRUMENTING THE REAL ONE
// -------------------------------------------------------------------
// Every scratch array on FastAStarAlgorithm is `private static` (:19-31) and MaxDepth is a
// `private const` (:26). A subclass inherits nothing usable and there is no seam to count
// expansions through. MODIFICATIONS entry 6 already records this. So the expansion counter has to
// be a copy - and a copy is only worth anything if it is PROVED to be the same algorithm, which is
// what Mirror mode and the copy-validation in NavHopProbe exist for.
//
// THE FOUR MODES, AND WHY THERE ARE FOUR
// --------------------------------------
//   Off         nothing installed. MovementPath.OverrideAlgorithm stays null. The shipping state.
//   Meter       a pass-through that forwards to FastAStarAlgorithm.Instance verbatim and counts.
//               GLOBAL, because a route-cost figure that ignores the daily-life walkers is not a
//               route-cost figure. Behaviour-identical by construction: it calls the same instance
//               with the same arguments and returns what it returns.
//   Mirror      the copy, dead-end `break` intact. THE CONTROL. Bot subjects only.
//   DeadEndFix  the same copy with `break` changed to `continue`, and NOTHING else. THE
//               MEASUREMENT. Bot subjects only.
//
// Mirror is not redundant and dropping it would make the whole exercise worthless. Three bot walk
// audits are run - Off, Mirror, DeadEndFix - and Mirror must come back row-identical to Off on the
// deterministic columns. Only against a green control is DeadEndFix's delta attributable to one
// token rather than to some accident of the copy.
//
// BOT SUBJECTS ONLY, WHICH IS SEAN'S CALL ON BLAST RADIUS
// -------------------------------------------------------
// MovementPath.OverrideAlgorithm (MovementPath.cs:58-68) is global: assigning it swaps the
// pathfinder for every mobile on the shard, which REVIEW.md:89 names as the risk. So Mirror and
// DeadEndFix run the copy only when Server.Custom.BotPathPolicy.IgnoreDoors(p) returns a value,
// and delegate verbatim to FastAStarAlgorithm.Instance for everything else. The creature walk
// audit is then PROVABLY unchanged rather than argued to be, and any delta in the bot audit is
// attributable to the fleet's own pathfinder.
//
// That predicate is also the door policy - the same one MODIFICATIONS entry 6 put in the engine -
// so the copy asks Custom/ exactly what the engine asks it, and IgnoreMovableImpassables stays
// false for bots. A bot may do what a player may do and no more.
//
// THE [Path HAZARD IS HANDLED, NOT DOCUMENTED AWAY
// ------------------------------------------------
// MovementPath.Path_OnTarget sets the override to null at MovementPath.cs:126 when a GM runs
// [Path. Nav.Pathfinder therefore does not merely ASSERT the override - when the mode is not Off
// and it finds the override null or foreign, it REINSTALLS and counts it, reporting Warn with the
// count. Exposure is bounded at one HealthCheck.RunAll interval (60 s) and it is visible in the
// health line rather than silent. [Path's own Fast-vs-Slow comparison is untouched: both its
// Path() calls complete inside one command, long before the next check.
//
// THIS FILE INSTALLS NOTHING BY DEFAULT AND EDITS NO UPSTREAM FILE.

using System;
using System.Collections;
using System.Diagnostics;

using Server.Mobiles;
using Server.PathAlgorithms;
using Server.PathAlgorithms.FastAStar;

using CalcMoves = Server.Movement.Movement;
using MoveImpl = Server.Movement.MovementImpl;

namespace Server.Custom
{
    /// <summary>What NavPathfinder installs behind MovementPath.OverrideAlgorithm, if anything.</summary>
    public enum NavPathfinderMode
    {
        /// <summary>Nothing installed. The shipping state.</summary>
        Off,

        /// <summary>Pass-through counter over stock, for every caller on the shard.</summary>
        Meter,

        /// <summary>The copy of stock with its dead-end break intact, bot subjects only. The control.</summary>
        Mirror,

        /// <summary>The copy with break changed to continue, bot subjects only. The measurement.</summary>
        DeadEndFix
    }

    /// <summary>
    /// How a search ended, so a caller can tell "no route exists" from "the budget ran out" from
    /// "a dead end killed it" - three answers stock collapses into one null.
    /// </summary>
    public enum NavSearchOutcome
    {
        Found,
        BudgetExhausted,
        DeadEnd,
        OpenListDrained,
        OutOfBox
    }

    /// <summary>One search, told honestly. The reason the copy exists.</summary>
    public struct NavSearchResult
    {
        public Direction[] Directions;
        public NavSearchOutcome Outcome;

        /// <summary>Nodes taken off the open list, i.e. what stock counts against MaxDepth.</summary>
        public int Expansions;

        /// <summary>Calls into Movement.CheckMovement. Eight per expansion, and the real cost.</summary>
        public int SuccessorProbes;

        public double Milliseconds;

        public bool Success
        {
            get { return Directions != null && Directions.Length > 0; }
        }

        public int Length
        {
            get { return Directions == null ? 0 : Directions.Length; }
        }
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A copy of Server.PathAlgorithms.FastAStar.FastAStarAlgorithm, faithful in every respect
    /// that can change an answer, with two things added: an expansion counter, and one switch.
    ///
    /// WHAT IS DELIBERATELY IDENTICAL, because any of these would change a route:
    ///   - the 38x38x13 node index, PlaneOffset 128, PlaneHeight 20, and GetIndex's arithmetic
    ///   - the inflated heuristic, exactly as written, including the *11 on x and y
    ///   - MaxDepth 300 and where the counter is incremented (after FindBest, before successors)
    ///   - the doubly-linked open chain, AddToChain's LIFO insertion and FindBest's linear scan,
    ///     which together decide the tie-break between equal-total nodes
    ///   - `if (!wasTouched)` guarding the whole improvement branch, which makes the inner
    ///     `total > newTotal` test dead code. It is dead upstream too and is copied dead.
    ///   - unit step cost, so g is 1 per step whether the step is diagonal or not. ModernUO charges
    ///     10 and 14 here; a UO diagonal costs the same step TIME as a cardinal, so uniform is the
    ///     honest metric and porting 10/14 would make the copy answer a different question.
    ///   - the MoveImpl flag dance per expansion, including MODIFICATIONS entry 6's bot branch,
    ///     asked through the same BotPathPolicy predicate the engine asks.
    ///
    /// WHAT IS NOT IDENTICAL, and it is one token:
    ///   DeadEndContinue. False reproduces `if (count == 0) break;`. True makes it `continue`.
    ///
    /// Instance fields rather than statics. Upstream's are `private static` (:19-31) and ModernUO
    /// de-statically'd its own for the same reason (BitmapAStarAlgorithm.cs:55-58): two
    /// differently-configured instances must not share scratch space, and this file holds two.
    /// Reuse within an instance is safe because the game loop is single-threaded and Find never
    /// re-enters.
    /// </summary>
    public sealed class NavStockCopy
    {
        private const int MaxDepth = 300;
        private const int AreaSize = 38;
        private const int PlaneOffset = 128;
        private const int PlaneCount = 13;
        private const int PlaneHeight = 20;
        private const int NodeCount = AreaSize * AreaSize * PlaneCount;

        private struct PathNode
        {
            public int cost, total;
            public int parent, next, prev;
            public int z;
        }

        private readonly Direction[] _path = new Direction[AreaSize * AreaSize];
        private readonly PathNode[] _nodes = new PathNode[NodeCount];
        private readonly BitArray _touched = new BitArray(NodeCount);
        private readonly BitArray _onOpen = new BitArray(NodeCount);
        private readonly int[] _successors = new int[8];

        private int _xOffset, _yOffset;
        private int _openList;
        private Point3D _goal;
        private int _probes;

        /// <summary>
        /// False is stock: a cell with no successors ends the search. True is ModernUO's
        /// BitmapAStarAlgorithm.cs:244-247. This is the entire difference between Mirror and
        /// DeadEndFix, and the only field on this class that changes an answer.
        /// </summary>
        public bool DeadEndContinue { get; set; }

        public bool CheckCondition(Point3D start, Point3D goal)
        {
            return Utility.InRange(start, goal, AreaSize);
        }

        public NavSearchResult Find(IPoint3D p, Map map, Point3D start, Point3D goal)
        {
            var result = new NavSearchResult();
            var watch = Stopwatch.StartNew();

            if (!Utility.InRange(start, goal, AreaSize))
            {
                result.Outcome = NavSearchOutcome.OutOfBox;
                result.Milliseconds = watch.Elapsed.TotalMilliseconds;
                return result;
            }

            _touched.SetAll(false);
            _probes = 0;

            _goal = goal;

            _xOffset = (start.X + goal.X - AreaSize) / 2;
            _yOffset = (start.Y + goal.Y - AreaSize) / 2;

            int fromNode = GetIndex(start.X, start.Y, start.Z);
            int destNode = GetIndex(goal.X, goal.Y, goal.Z);

            if (fromNode < 0 || fromNode >= NodeCount)
            {
                // Upstream indexes without this guard and relies on GetSuccessors' bounds test to
                // keep it out of trouble; a start node outside the array would throw there before
                // it ever reached a comparison. Returning OutOfBox rather than throwing is the one
                // divergence that cannot change a route, because upstream cannot return here at all.
                result.Outcome = NavSearchOutcome.OutOfBox;
                result.Milliseconds = watch.Elapsed.TotalMilliseconds;
                return result;
            }

            _openList = fromNode;

            _nodes[_openList].cost = 0;
            _nodes[_openList].total = Heuristic(start.X - _xOffset, start.Y - _yOffset, start.Z);
            _nodes[_openList].parent = -1;
            _nodes[_openList].next = -1;
            _nodes[_openList].prev = -1;
            _nodes[_openList].z = start.Z;

            _onOpen[_openList] = true;
            _touched[_openList] = true;

            BaseCreature bc = p as BaseCreature;

            // MODIFICATIONS entry 6's policy, asked of Custom/ exactly as the engine asks it.
            // IgnoreMovableImpassables is deliberately not granted; see BotPathPolicy.
            bool? botDoors = BotPathPolicy.IgnoreDoors(p);

            int pathCount, parent;
            int backtrack = 0, depth = 0;

            Direction[] path = _path;

            // Set up front rather than inferred after the loop: Found is the enum's zero value, so
            // a loop that simply runs out of open nodes would otherwise report success.
            result.Outcome = NavSearchOutcome.OpenListDrained;

            while (_openList != -1)
            {
                int bestNode = FindBest(_openList);

                if (++depth > MaxDepth)
                {
                    result.Outcome = NavSearchOutcome.BudgetExhausted;
                    break;
                }

                if (bc != null)
                {
                    MoveImpl.AlwaysIgnoreDoors = bc.CanOpenDoors;
                    MoveImpl.IgnoreMovableImpassables = bc.CanMoveOverObstacles;
                }
                else if (botDoors.HasValue)
                {
                    MoveImpl.AlwaysIgnoreDoors = botDoors.Value;
                }

                MoveImpl.Goal = goal;

                int[] vals = _successors;
                int count = GetSuccessors(bestNode, p, map);

                MoveImpl.AlwaysIgnoreDoors = false;
                MoveImpl.IgnoreMovableImpassables = false;
                MoveImpl.Goal = Point3D.Zero;

                if (count == 0)
                {
                    // THE ONE TOKEN. Stock breaks here (FastAStarAlgorithm.cs:118-119).
                    if (!DeadEndContinue)
                    {
                        result.Outcome = NavSearchOutcome.DeadEnd;
                        break;
                    }

                    continue;
                }

                for (int i = 0; i < count; ++i)
                {
                    int newNode = vals[i];

                    bool wasTouched = _touched[newNode];

                    if (!wasTouched)
                    {
                        int newCost = _nodes[bestNode].cost + 1;
                        int newTotal = newCost + Heuristic(
                            newNode % AreaSize, (newNode / AreaSize) % AreaSize, _nodes[newNode].z);

                        // Dead upstream and dead here. Copied rather than tidied: the copy's whole
                        // value is that it is the same algorithm, and "I removed a branch that
                        // could never be taken" is exactly the claim a validation run exists to
                        // stop anybody having to take on trust.
                        if (!wasTouched || _nodes[newNode].total > newTotal)
                        {
                            _nodes[newNode].parent = bestNode;
                            _nodes[newNode].cost = newCost;
                            _nodes[newNode].total = newTotal;

                            if (!wasTouched || !_onOpen[newNode])
                            {
                                AddToChain(newNode);

                                if (newNode == destNode)
                                {
                                    pathCount = 0;
                                    parent = _nodes[newNode].parent;

                                    while (parent != -1)
                                    {
                                        path[pathCount++] = Direct(
                                            parent % AreaSize, (parent / AreaSize) % AreaSize,
                                            newNode % AreaSize, (newNode / AreaSize) % AreaSize);

                                        newNode = parent;
                                        parent = _nodes[newNode].parent;

                                        if (newNode == fromNode)
                                        {
                                            break;
                                        }
                                    }

                                    var dirs = new Direction[pathCount];

                                    while (pathCount > 0)
                                    {
                                        dirs[backtrack++] = path[--pathCount];
                                    }

                                    // dirs may be empty when start and goal share a tile; stock
                                    // returns that empty array too and MovementPath reads it as a
                                    // failure. Copied rather than corrected.
                                    result.Directions = dirs;
                                    result.Outcome = NavSearchOutcome.Found;
                                    result.Expansions = depth;
                                    result.SuccessorProbes = _probes;
                                    result.Milliseconds = watch.Elapsed.TotalMilliseconds;
                                    return result;
                                }
                            }
                        }
                    }
                }
            }

            result.Expansions = depth;
            result.SuccessorProbes = _probes;
            result.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        private int Heuristic(int x, int y, int z)
        {
            x -= _goal.X - _xOffset;
            y -= _goal.Y - _yOffset;
            z -= _goal.Z;

            x *= 11;
            y *= 11;

            return (x * x) + (y * y) + (z * z);
        }

        private int GetSuccessors(int p, IPoint3D pnt, Map map)
        {
            int px = p % AreaSize;
            int py = (p / AreaSize) % AreaSize;
            int pz = _nodes[p].z;
            int x, y, z;

            var p3D = new Point3D(px + _xOffset, py + _yOffset, pz);

            int[] vals = _successors;
            int count = 0;

            for (int i = 0; i < 8; ++i)
            {
                switch (i)
                {
                    default:
                    case 0: x = 0; y = -1; break;
                    case 1: x = 1; y = -1; break;
                    case 2: x = 1; y = 0; break;
                    case 3: x = 1; y = 1; break;
                    case 4: x = 0; y = 1; break;
                    case 5: x = -1; y = 1; break;
                    case 6: x = -1; y = 0; break;
                    case 7: x = -1; y = -1; break;
                }

                x += px;
                y += py;

                if (x < 0 || x >= AreaSize || y < 0 || y >= AreaSize)
                {
                    continue;
                }

                _probes++;

                if (CalcMoves.CheckMovement(pnt, map, p3D, (Direction)i, out z))
                {
                    int idx = GetIndex(x + _xOffset, y + _yOffset, z);

                    if (idx >= 0 && idx < NodeCount)
                    {
                        _nodes[idx].z = z;
                        vals[count++] = idx;
                    }
                }
            }

            return count;
        }

        private void RemoveFromChain(int node)
        {
            if (node < 0 || node >= NodeCount)
            {
                return;
            }

            if (!_touched[node] || !_onOpen[node])
            {
                return;
            }

            int prev = _nodes[node].prev;
            int next = _nodes[node].next;

            if (_openList == node)
            {
                _openList = next;
            }

            if (prev != -1)
            {
                _nodes[prev].next = next;
            }

            if (next != -1)
            {
                _nodes[next].prev = prev;
            }

            _nodes[node].prev = -1;
            _nodes[node].next = -1;
        }

        private void AddToChain(int node)
        {
            if (node < 0 || node >= NodeCount)
            {
                return;
            }

            RemoveFromChain(node);

            if (_openList != -1)
            {
                _nodes[_openList].prev = node;
            }

            _nodes[node].next = _openList;
            _nodes[node].prev = -1;

            _openList = node;

            _touched[node] = true;
            _onOpen[node] = true;
        }

        private int GetIndex(int x, int y, int z)
        {
            x -= _xOffset;
            y -= _yOffset;
            z += PlaneOffset;
            z /= PlaneHeight;

            return x + (y * AreaSize) + (z * AreaSize * AreaSize);
        }

        private int FindBest(int node)
        {
            int least = _nodes[node].total;
            int leastNode = node;

            while (node != -1)
            {
                if (_nodes[node].total < least)
                {
                    least = _nodes[node].total;
                    leastNode = node;
                }

                node = _nodes[node].next;
            }

            RemoveFromChain(leastNode);

            _touched[leastNode] = true;
            _onOpen[leastNode] = false;

            return leastNode;
        }

        /// <summary>
        /// PathAlgorithm.GetDirection's table, which is an instance method on an abstract class
        /// this type deliberately does not derive from - NavStockCopy is a search, not a
        /// PathAlgorithm, so that NavHopProbe can call it without installing anything.
        /// </summary>
        private static readonly Direction[] _calcDirections =
        {
            Direction.Up, Direction.North, Direction.Right,
            Direction.West, Direction.North, Direction.East,
            Direction.Left, Direction.South, Direction.Down
        };

        private static Direction Direct(int xSource, int ySource, int xDest, int yDest)
        {
            int x = xDest + 1 - xSource;
            int y = yDest + 1 - ySource;
            int v = (y * 3) + x;

            if (v < 0 || v >= 9)
            {
                return Direction.North;
            }

            return _calcDirections[v];
        }
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The PathAlgorithm actually installed behind MovementPath.OverrideAlgorithm.
    ///
    /// It is one class for all three installed modes because MovementPath consults exactly one
    /// object (MovementPath.cs:39) and swapping the object under a running server is the thing
    /// Nav.Pathfinder exists to police. One object, one identity to assert.
    /// </summary>
    public sealed class NavPathfinderAlgorithm : PathAlgorithm
    {
        private readonly NavStockCopy _copy = new NavStockCopy();

        public NavPathfinderMode Mode { get; private set; }

        // Counters. Read by Nav.Pathfinder and by the step-2 report; reset by NavPathfinder.Reset.
        public long Calls;
        public long Delegated;
        public long Refused;
        public long Succeeded;
        public long Failed;
        public long TotalExpansions;
        public long TotalProbes;
        public double TotalMilliseconds;
        public double WorstMilliseconds;

        public NavPathfinderAlgorithm(NavPathfinderMode mode)
        {
            Mode = mode;
            _copy.DeadEndContinue = mode == NavPathfinderMode.DeadEndFix;
        }

        /// <summary>
        /// Whether this subject gets the copy. Meter never does - it is a counter over stock for
        /// every caller. Mirror and DeadEndFix do only for a bot, which is Sean's call on blast
        /// radius: everything else delegates verbatim, so the creature audit is provably unchanged.
        /// </summary>
        private bool UsesCopy(IPoint3D p)
        {
            return Mode != NavPathfinderMode.Meter && BotPathPolicy.IgnoreDoors(p).HasValue;
        }

        public override bool CheckCondition(IPoint3D p, Map map, Point3D start, Point3D goal)
        {
            // Identical test either way - the copy's CheckCondition IS FastAStarAlgorithm's - but
            // routed through the same object so a future divergence cannot hide here.
            return UsesCopy(p)
                ? _copy.CheckCondition(start, goal)
                : FastAStarAlgorithm.Instance.CheckCondition(p, map, start, goal);
        }

        public override Direction[] Find(IPoint3D p, Map map, Point3D start, Point3D goal)
        {
            Calls++;

            if (!UsesCopy(p))
            {
                Delegated++;

                var watch = Stopwatch.StartNew();
                Direction[] stock = FastAStarAlgorithm.Instance.Find(p, map, start, goal);
                double ms = watch.Elapsed.TotalMilliseconds;

                Record(ms, 0, 0, stock != null && stock.Length > 0);
                return stock;
            }

            NavSearchResult result = _copy.Find(p, map, start, goal);

            if (result.Outcome == NavSearchOutcome.OutOfBox)
            {
                Refused++;
            }

            Record(result.Milliseconds, result.Expansions, result.SuccessorProbes, result.Success);
            return result.Directions;
        }

        private void Record(double ms, int expansions, int probes, bool success)
        {
            TotalMilliseconds += ms;
            TotalExpansions += expansions;
            TotalProbes += probes;

            if (ms > WorstMilliseconds)
            {
                WorstMilliseconds = ms;
            }

            if (success)
            {
                Succeeded++;
            }
            else
            {
                Failed++;
            }
        }
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Installs, polices and reports the pathfinder instrument. Nothing here runs unless
    /// Custom.NavPathfinder names a mode other than Off.
    /// </summary>
    public static class NavPathfinder
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        private static NavPathfinderAlgorithm _installed;

        /// <summary>Times the override was found missing or foreign and put back. See the header.</summary>
        public static int Reinstalls { get; private set; }

        /// <summary>
        /// The mode named by Config/Custom.cfg. Read per call rather than cached, so [CoreSmoke
        /// after a config edit reports what the file says and not what boot happened to see.
        /// </summary>
        public static NavPathfinderMode Mode
        {
            get { return Config.GetEnum("Custom.NavPathfinder", NavPathfinderMode.Off); }
        }

        public static NavPathfinderAlgorithm Installed
        {
            get { return _installed; }
        }

        /// <summary>
        /// A standalone copy for a caller that wants to run the search WITHOUT installing anything
        /// - which is how NavHopProbe validates the copy against stock and counts expansions while
        /// the shard is at its shipping default. One per caller, because the scratch arrays are
        /// instance state.
        /// </summary>
        public static NavStockCopy NewCopy(bool deadEndContinue)
        {
            return new NavStockCopy { DeadEndContinue = deadEndContinue };
        }

        public static void Initialize()
        {
            HealthCheck.Register("Nav.Pathfinder", BuildHealthResult);

            Apply();
        }

        /// <summary>
        /// Bring the installed override into line with the configured mode. Idempotent, and safe
        /// to call from the health check - which is exactly what closes the [Path hole.
        /// </summary>
        public static bool Apply()
        {
            NavPathfinderMode mode = Mode;

            if (mode == NavPathfinderMode.Off)
            {
                if (_installed != null)
                {
                    if (ReferenceEquals(MovementPath.OverrideAlgorithm, _installed))
                    {
                        MovementPath.OverrideAlgorithm = null;
                    }

                    _installed = null;
                    Log.Info("Pathfinder instrument removed; the shard is back on stock.");
                }

                return false;
            }

            if (_installed == null || _installed.Mode != mode)
            {
                _installed = new NavPathfinderAlgorithm(mode);
                MovementPath.OverrideAlgorithm = _installed;

                Log.Info(
                    "Pathfinder instrument installed in {0} mode ({1}).",
                    mode,
                    mode == NavPathfinderMode.Meter
                        ? "pass-through over stock, every caller"
                        : "the stock copy, bot subjects only; everything else delegates");

                return true;
            }

            if (!ReferenceEquals(MovementPath.OverrideAlgorithm, _installed))
            {
                // [Path nulls the override at MovementPath.cs:126, and any other assignment would
                // land here too. Put it back and say so; a silent revert to stock mid-measurement
                // is how a session ends up quoting the wrong numbers.
                MovementPath.OverrideAlgorithm = _installed;
                Reinstalls++;

                Log.Warn(
                    "MovementPath.OverrideAlgorithm had been cleared or replaced - reinstalled "
                    + "({0} time(s) this boot). [Path does this at MovementPath.cs:126.",
                    Reinstalls);

                return true;
            }

            return false;
        }

        /// <summary>Zero the counters, so a measurement window starts clean without a restart.</summary>
        public static void Reset()
        {
            Reinstalls = 0;

            if (_installed == null)
            {
                return;
            }

            _installed.Calls = 0;
            _installed.Delegated = 0;
            _installed.Refused = 0;
            _installed.Succeeded = 0;
            _installed.Failed = 0;
            _installed.TotalExpansions = 0;
            _installed.TotalProbes = 0;
            _installed.TotalMilliseconds = 0.0;
            _installed.WorstMilliseconds = 0.0;
        }

        /// <summary>One line, in the form every other health check writes.</summary>
        public static string Describe()
        {
            NavPathfinderMode mode = Mode;

            if (mode == NavPathfinderMode.Off)
            {
                return "off; MovementPath.OverrideAlgorithm "
                    + (MovementPath.OverrideAlgorithm == null ? "null" : "NOT NULL");
            }

            NavPathfinderAlgorithm alg = _installed;

            if (alg == null)
            {
                return String.Format("{0}; nothing installed", mode);
            }

            double mean = alg.Calls > 0 ? alg.TotalMilliseconds / alg.Calls : 0.0;

            return String.Format(
                "{0}; {1} call(s), {2} delegated, {3} ok, {4} no route, {5} out of box; "
                + "{6:F3} ms mean, {7:F3} ms worst; {8} expansion(s), {9} reinstall(s)",
                mode, alg.Calls, alg.Delegated, alg.Succeeded, alg.Failed, alg.Refused,
                mean, alg.WorstMilliseconds, alg.TotalExpansions, Reinstalls);
        }

        /// <summary>
        /// The assertion the brief asked for, both ways round.
        ///
        /// Off  - the override MUST be null. A non-null override with the flag off means somebody
        ///        installed a pathfinder and did not say so, and it is a Fail rather than a Warn
        ///        because every number this shard has ever recorded assumed stock.
        /// On   - the override MUST be ours. Apply() has already put it back by the time this
        ///        reads, so the check reports the reinstall count rather than the breach; a
        ///        non-zero count is a Warn naming [Path.
        /// </summary>
        public static HealthResult BuildHealthResult()
        {
            NavPathfinderMode mode = Mode;

            if (mode == NavPathfinderMode.Off)
            {
                if (_installed != null || MovementPath.OverrideAlgorithm != null)
                {
                    // Try to put it right before reporting, so the shard is not left on a
                    // pathfinder nobody asked for while somebody reads the line.
                    Apply();

                    if (MovementPath.OverrideAlgorithm != null)
                    {
                        return HealthResult.Fail(
                            "Custom.NavPathfinder is Off but MovementPath.OverrideAlgorithm is "
                            + MovementPath.OverrideAlgorithm.GetType().Name
                            + " - the shard is not on stock.");
                    }
                }

                return HealthResult.Ok(Describe());
            }

            bool reinstalled = Apply();

            if (_installed == null)
            {
                return HealthResult.Fail(
                    "Custom.NavPathfinder is " + mode + " but nothing is installed.");
            }

            if (Reinstalls > 0)
            {
                return HealthResult.Warn(
                    Describe()
                    + (reinstalled ? " - put back just now" : "")
                    + ". [Path clears the override (MovementPath.cs:126); it is restored within "
                    + "one health interval.");
            }

            return HealthResult.Ok(Describe());
        }
    }
}
