// -----------------------------------------------------------------------------
// A NAMED PORT of the PlayerBots system in Klein187/uo-offline (GPL-3.0): the file below says
// which file and which lines. It is therefore licensed under the GNU General Public License,
// version 3. See LICENSE-BOTS at the repository root - a directory-level notice is an
// incomplete inventory when a translation crosses a directory boundary, so this one says so
// where it lives rather than where its siblings live.
// -----------------------------------------------------------------------------
//
// NavEdgeHealth.cs — decaying per-edge failure memory, so the fleet routes around a bad edge.
//
// A NAMED PORT of uo-offline's NavEdgeHealth (BotStuckTelemetry.cs:320-459), and its own summary
// of itself is the contract this keeps:
//
//     // NavEdgeHealth — decaying per-edge failure memory consulted by
//     // WaypointGraph.FindPath. Strikes come from Traveler leg give-ups
//     // (the bot ground through its whole nudge/repath ladder and still
//     // couldn't cross the edge). Penalties are cost MULTIPLIERS, never
//     // removals — connectivity is untouched, only path shape changes.
//
// WHY IT IS WORTH PORTING HERE. A thirty-minute measurement with sixty bots produced 36 walks that
// climbed the whole recovery ladder and were teleported, and five of them were the SAME hop. There
// is nothing in this shard that notices an edge failing repeatedly: every bot that rolls a route
// through it walks into the same wall, discovers the same thing over eighty seconds, and teaches
// nobody. Upstream's answer is the right shape - the fleet learns, the data does not change, and
// the memory fades so a transient jam does not permanently deform the graph.
//
// THREE DELIBERATE DIFFERENCES FROM UPSTREAM.
//
// 1. Their strikes come from a Traveler LEG GIVE-UP, a concept this shard does not have: their
//    behaviour owns the walking and can abandon a leg. Ours are raised by NavWalker at the top of
//    its ladder - the same moment, one layer down, and the layer that actually knows which edge it
//    was. It also means every walker benefits, not only bots.
//
// 2. Their penalty is consulted inside FindPath through a WaypointGraph.EdgePenalty hook; ours is
//    the same hook on NavGraph, because our edge costs are computed once at build time and a
//    runtime penalty has to be applied during the search or not at all.
//
// 3. Upstream's numbers are MaxStrikes 6, StrikeTtl 45 min, penalty 1 + 2*strikes. Ours are
//    smaller and faster, because our hops are smaller and faster: a 12-tile hop cap against their
//    38 means a detour costs proportionally less, and a shard with two towns has fewer alternative
//    roads, so a penalty that is too heavy or too long-lived strands a destination instead of
//    routing around it.
//
// WHAT IT IS NOT. It never removes an edge and never marks one unwalkable. A penalised edge still
// connects, so no destination can become unreachable because of it - which matters more here than
// upstream, because 8 of our 53 destinations hang off a single approach waypoint.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class NavEdgeHealth
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// The most strikes one edge may carry. Upstream's is 6.
        ///
        /// Four, because the penalty is what matters and it saturates: at four strikes an edge
        /// already costs three times its length, which on a 12-tile hop cap is enough to prefer
        /// almost any real alternative. More strikes past that would only lengthen the memory.
        /// </summary>
        public const int MaxStrikes = 4;

        /// <summary>
        /// How long a single strike is remembered. Upstream's is 45 minutes.
        ///
        /// Fifteen. Their walk is a 38-tile leg through open country and a bad one stays bad;
        /// ours are 12-tile town hops where the usual cause is a crowd that will have moved on.
        /// A memory that outlives the jam turns a busy afternoon into a permanently ugly road.
        /// </summary>
        public static readonly TimeSpan StrikeTtl = TimeSpan.FromMinutes(15.0);

        private sealed class Record
        {
            public int Strikes;
            public DateTime Expires;
        }

        private static readonly Dictionary<string, Record> _records =
            new Dictionary<string, Record>(StringComparer.Ordinal);

        /// <summary>
        /// Bumped whenever the penalty a search would read has changed - a strike, an expiry that
        /// actually dropped a record, a clear.
        ///
        /// THE ROUTE CACHE IS WHY THIS EXISTS. Penalties are read inside NavGraph.Search, and a
        /// cache hit returns before the search runs (NavGraph.cs:556), so a warm route went on
        /// using an edge after that edge was struck and went on avoiding one after the strike
        /// expired. The learning was real and the fleet never saw it. NavGraph stamps each cached
        /// route with this number and refuses a hit that does not match, which is the whole of the
        /// invalidation: one int compared per lookup, no subscription, no per-edge bookkeeping.
        ///
        /// An int and not a hash of the table: it only ever has to answer "has anything moved
        /// since", and a counter cannot collide with itself the way a truncated hash can.
        /// </summary>
        public static int Version
        {
            get { return _version; }
        }

        private static int _version;

        /// <summary>
        /// The cost multiplier for an edge, 1.0 when it has no history.
        ///
        /// Linear in strikes, as upstream's is, but gentler: 1 + 0.75 * strikes, so one strike
        /// makes an edge 1.75x its length and a saturated one 4x. The shape matters more than the
        /// slope - a penalty that grows means a road that keeps failing keeps losing, and a road
        /// that stops failing recovers on its own as the strikes expire.
        /// </summary>
        public static double Penalty(string from, string to)
        {
            int strikes = StrikesOn(from, to);

            return strikes <= 0 ? 1.0 : 1.0 + (0.75 * strikes);
        }

        public static int StrikesOn(string from, string to)
        {
            Record record;

            if (!_records.TryGetValue(Key(from, to), out record))
            {
                return 0;
            }

            // Expiry is checked on read rather than swept on a timer. There is no tick to hang a
            // sweep on in Core, the table is tens of entries, and a stale record that is never
            // read again costs nothing but a dictionary slot until the next report clears it.
            if (DateTime.UtcNow >= record.Expires)
            {
                _records.Remove(Key(from, to));
                _version++;
                return 0;
            }

            return record.Strikes;
        }

        /// <summary>
        /// This edge just cost somebody the whole recovery ladder.
        ///
        /// Raised by NavWalker at the top of its ladder, which is the only place that knows both
        /// that the hop failed completely AND which two waypoints it was between. A rung firing is
        /// not enough: rungs 2 to 4 are the ladder working, and striking on those would penalise
        /// every busy road in Britain within an hour.
        /// </summary>
        public static void Strike(string from, string to)
        {
            if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to))
            {
                // An arrival step has no waypoint id, and an arrival is not an edge - the tile is
                // the problem there, and NavWalkFailures is what records it.
                return;
            }

            string key = Key(from, to);
            Record record;

            if (!_records.TryGetValue(key, out record) || DateTime.UtcNow >= record.Expires)
            {
                record = new Record();
                _records[key] = record;
            }

            if (record.Strikes < MaxStrikes)
            {
                record.Strikes++;
            }

            // The clock restarts on every strike rather than running from the first: an edge that
            // is still failing is still bad, and an expiry measured from the first failure would
            // forgive it in the middle of the run that proves it.
            record.Expires = DateTime.UtcNow + StrikeTtl;

            // After the record is settled, not before: this is what tells the route cache that
            // every warm route through this edge is now answering with the old cost.
            _version++;

            Log.Debug(
                "edge '{0}' <-> '{1}' takes a strike ({2}) - cost x{3:0.00} for {4:0} minute(s).",
                from,
                to,
                record.Strikes,
                Penalty(from, to),
                StrikeTtl.TotalMinutes);
        }

        /// <summary>Every edge currently carrying strikes, worst first. The data-fix backlog.</summary>
        public static List<string> Describe()
        {
            var lines = new List<string>();
            var expired = new List<string>();
            DateTime now = DateTime.UtcNow;

            foreach (var pair in _records)
            {
                if (now >= pair.Value.Expires)
                {
                    expired.Add(pair.Key);
                    continue;
                }

                lines.Add(String.Format(
                    "{0} x{1} strike(s), cost x{2:0.00}, {3:0} min left",
                    pair.Key,
                    pair.Value.Strikes,
                    1.0 + (0.75 * pair.Value.Strikes),
                    (pair.Value.Expires - now).TotalMinutes));
            }

            foreach (string key in expired)
            {
                _records.Remove(key);
            }

            if (expired.Count > 0)
            {
                _version++;
            }

            lines.Sort(StringComparer.Ordinal);

            return lines;
        }

        /// <summary>
        /// Drop every expired record, and say whether anything went.
        ///
        /// EXPIRY USED TO BE INVISIBLE TO EVERYTHING BUT A READ. Records are aged lazily inside
        /// StrikesOn, which is only reached from a search - and a cached route does not search.
        /// So a strike could expire with nothing ever noticing: the version would not move, the
        /// cached detour would stay, and the edge would be avoided long after it had recovered.
        ///
        /// NavGraph.TryFindPath calls this before every cache lookup. The table is tens of
        /// entries and the sweep is a walk of it, which is cheaper than the dictionary lookup it
        /// sits in front of; making expiry deterministic at the one place that cares is worth far
        /// more than saving it.
        /// </summary>
        public static bool Sweep()
        {
            if (_records.Count == 0)
            {
                return false;
            }

            List<string> expired = null;
            DateTime now = DateTime.UtcNow;

            foreach (var pair in _records)
            {
                if (now < pair.Value.Expires)
                {
                    continue;
                }

                if (expired == null)
                {
                    expired = new List<string>();
                }

                expired.Add(pair.Key);
            }

            if (expired == null)
            {
                return false;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                _records.Remove(expired[i]);
            }

            _version++;

            return true;
        }

        public static void Clear()
        {
            if (_records.Count == 0)
            {
                return;
            }

            _records.Clear();
            _version++;
        }

        /// <summary>
        /// Age a record out now, for the contract test in [CoreSmoke.
        ///
        /// The test has to show a cached detour going away when its strike expires, and the real
        /// TTL is fifteen minutes. It sets the expiry into the past rather than deleting the
        /// record, so what runs afterwards is the ORDINARY expiry path - Sweep, the version bump,
        /// the cache miss - and not a second, test-only route through the code.
        /// </summary>
        public static void ExpireNow(string from, string to)
        {
            Record record;

            if (_records.TryGetValue(Key(from, to), out record))
            {
                record.Expires = DateTime.UtcNow.AddSeconds(-1.0);
            }
        }

        /// <summary>
        /// Undirected: a road that cannot be walked one way is the same road the other way.
        ///
        /// Upstream strikes the directed pair it was walking; ours folds them, because our edges
        /// are authored as undirected walk links and the graph builds both directions from one
        /// record. Striking only the direction that failed would leave half the fleet still
        /// walking into it.
        /// </summary>
        private static string Key(string from, string to)
        {
            return String.CompareOrdinal(from, to) <= 0
                ? from + "<->" + to
                : to + "<->" + from;
        }
    }
}
