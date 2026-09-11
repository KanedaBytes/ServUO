// NavResampleZ.cs — put every nav record back on the ground it names.
//
// WHY THIS EXISTS
// ---------------
// Half the Z values in navigation.json are stale. 388 of 772 z-bearing records sit at z 0, and
// the cause is documented on the editor side rather than guessed at: js/build.js:22-28 records
// that "every record the editor has ever created carries z: 0", because the create path wrote a
// flat zero before it learned to read the pick map. Britain's upper town stands at Z 20-30, so
// those records draw about 2.7 tiles off along the isometric diagonal - which is how somebody
// comes to drag a marker "back" onto what looks right and move a record that was never wrong.
//
// THE Z THIS WRITES, AND THE ONE IT DOES NOT
// ------------------------------------------
// It writes NavWalker.TryResolveZ's answer - where a mobile carrying the stored Z as a hint would
// actually stand - and NOT the pick map's StandingZ.
//
// StandingZ (tools/MapExport/IsoTileRenderer.cs:256-264) is one line: map.GetAverageZ(x, y). Land
// only, no hint, no statics, no clearance test. It is exactly right for deciding what height to
// DRAW a tile at, and exactly wrong as a repair value: resampling to it would drag every record
// that legitimately sits on an upper floor, a bridge deck or a second storey down to the ground.
// The two functions answer different questions and this file needs the walker's one, which is
// also the one NavAudit uses so that the audit and the walker cannot disagree (NavAudit.cs:472).
//
// A RECORD NOTHING CAN PLACE IS REPORTED, NEVER MOVED
// ---------------------------------------------------
// TryResolveZ's return value already draws the distinction this needs:
//
//   true  - SOMETHING standable was found  -> correct the record to it
//   false - nothing was; the out value fell through to the land Z (or to the hint, with no facet,
//           which is not a resolution either)  -> LEAVE IT
//
// Read that true case for what it is, because two earlier versions of this comment read it too
// narrowly and REVIEW.md section 11 called both. It is NOT "within the window of the stored Z":
// the windows are only the first of four branches (NavWalker.cs:2311-2375). A true answer may also
// be a DOORWAY - where nothing stands in either window, because a closed door is an Impassable
// item - or a SEED-SCAN hit expanding outward from the land surface as far as SeedScanRange, which
// is what reaches a pier deck from a hint left under the water.
//
// Nor is it true that this "never writes a land Z". The doorway branch's second arm returns
// map.GetAverageZ(x, y) exactly, and is RIGHT to: NavCorridor.CanStand counts a door tile as
// standable at the Z beside it, and so does the walker that has to open it.
//
// The accurate promise is the one that matters to this file: A FAILED RESOLUTION IS NEVER
// COMMITTED AS A CORRECTION. A record nothing can place is not a stale Z at all - it is a record
// in the wrong PLACE, like an arrival authored inside a shop's display case, and quietly dropping
// it to the ground would bury the one fault worth finding. Those come out as their own section of
// the report for a human to place by hand.
//
// That splits two failure classes that have been indistinguishable until now: "your Z is out of
// date", which this fixes, and "your X,Y is wrong", which it can only point at.
//
// NO refZ. NavAdopt.CorrectZ (NavAdopt.cs:620-640) is the same operation and keeps the old value
// in a refZ field. This does not, deliberately: refZ is written by that one method and read by
// nothing - not the walker, not the audit, not the editor, which round-trips it as an unmodelled
// extra prop - so adding a key to half the file to annotate a value git already holds buys
// nothing. The corrections worth a second look go in the report and the commit message instead.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Commands;

namespace Server.Custom
{
    public static class NavResampleZ
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// A correction large enough to be worth naming individually rather than counting.
        /// One storey, the same threshold the editor's stale-Z flag uses.
        /// </summary>
        public const int LargeCorrection = 20;

        public sealed class Change
        {
            public string Kind;
            public string Id;
            public int X;
            public int Y;
            public int OldZ;
            public int NewZ;

            public int Delta
            {
                get { return Math.Abs(NewZ - OldZ); }
            }

            public override string ToString()
            {
                return String.Format(
                    "{0} '{1}' {2},{3}: z {4} -> {5}", Kind, Id, X, Y, OldZ, NewZ);
            }
        }

        public sealed class Stranded
        {
            public string Kind;
            public string Id;
            public int X;
            public int Y;
            public int Z;

            /// <summary>What the land says, for comparison with the stored Z.</summary>
            public int LandZ;

            /// <summary>
            /// Whether a mobile fits at the land Z, ignoring mobiles.
            ///
            /// This is the whole diagnosis. TryResolveZ searches a window around the stored Z and
            /// then around the land Z, so a record it cannot place has failed BOTH - and knowing
            /// which is the difference between "the Z is wrong" and "the tile is solid". A tile
            /// that cannot fit a mobile at its own land Z is furniture, a wall or a counter, and
            /// the record needs moving rather than resampling.
            /// </summary>
            public bool FitsAtLand;

            /// <summary>
            /// What is actually in the way: the id and name of the tallest static or item whose
            /// span overlaps the mobile's box, and how many others do.
            ///
            /// "UNSTANDABLE arrival for 'brit-bank' at 1432,1692" told a reader that a tile was
            /// bad and nothing else. Whether to move the record one tile or to move it across the
            /// room depends entirely on whether the answer is a bank counter, a wall or a barrel,
            /// and that is a lookup the reader was being asked to do by hand with no tools.
            /// </summary>
            public int BlockerId;

            public string BlockerName;

            /// <summary>How many things overlap, so "and 3 more" is honest about the mess.</summary>
            public int BlockerCount;

            /// <summary>
            /// The nearest tile within two that WOULD take the record, or Distance 0 for none.
            ///
            /// Two, because that is the arrival range a place authors and the radius the walker's
            /// own shift searches: a fix inside it needs no other change, and a fix outside it is a
            /// different decision about where the destination is.
            /// </summary>
            public int NearestX;

            public int NearestY;

            public int NearestZ;

            public int NearestDistance;

            /// <summary>
            /// Whether the SAME x,y is standable at some other Z within twenty of the stored one,
            /// and which.
            ///
            /// This separates two faults that the badge merges. A record whose tile is solid needs
            /// moving; a record sitting at the wrong height on a tile that is perfectly standable
            /// ten Z up needs resampling, and the reason TryResolveZ did not resample it is its own
            /// window rather than the world. Counting this class across the whole graph is the
            /// evidence for whether that window should change - which is a separate decision, and
            /// deliberately not made here.
            /// </summary>
            public bool HasStandableZ;

            public int StandableZ;

            public override string ToString()
            {
                var text = new System.Text.StringBuilder(160);

                text.AppendFormat(
                    "{0} '{1}' {2},{3} z {4} (land {5}{6})",
                    Kind, Id, X, Y, Z, LandZ,
                    FitsAtLand ? ", fits at land" : ", solid - nothing fits here at all");

                if (BlockerName != null)
                {
                    text.AppendFormat(
                        " - blocked by 0x{0:X4} '{1}'", BlockerId, BlockerName);

                    if (BlockerCount > 1)
                    {
                        text.AppendFormat(" and {0} more", BlockerCount - 1);
                    }
                }

                if (HasStandableZ)
                {
                    text.AppendFormat("; standable at z {0}", StandableZ);
                }

                text.Append(NearestDistance > 0
                    ? String.Format(
                        "; nearest standable {0},{1},{2} ({3} tile{4} away)",
                        NearestX, NearestY, NearestZ, NearestDistance, NearestDistance == 1 ? "" : "s")
                    : "; nothing standable within 2");

                return text.ToString();
            }
        }

        public sealed class Result
        {
            public readonly List<Change> Changes = new List<Change>();
            public readonly List<Stranded> Cannot = new List<Stranded>();
            public int Scanned;
            public int Skipped;

            public Change Largest
            {
                get
                {
                    Change best = null;

                    foreach (Change change in Changes)
                    {
                        if (best == null || change.Delta > best.Delta)
                        {
                            best = change;
                        }
                    }

                    return best;
                }
            }

            public List<Change> Large
            {
                get
                {
                    var large = new List<Change>();

                    foreach (Change change in Changes)
                    {
                        if (change.Delta > LargeCorrection)
                        {
                            large.Add(change);
                        }
                    }

                    large.Sort((a, b) => b.Delta.CompareTo(a.Delta));

                    return large;
                }
            }
        }

        // -------------------------------------------------------------------
        // The scan
        // -------------------------------------------------------------------

        /// <summary>
        /// Work out what would change. Reads only; the command and the request token both come
        /// through here so a dry run and an apply cannot disagree about what they are doing.
        /// </summary>
        public static Result Scan()
        {
            var result = new Result();
            NavigationStore store = NavigationSystem.Store;

            if (store == null)
            {
                return result;
            }

            foreach (NavWaypoint waypoint in store.Waypoints)
            {
                Examine(result, "waypoint", waypoint.Id, waypoint.Map, waypoint.X, waypoint.Y, waypoint.Z);
            }

            foreach (NavDestination destination in store.Destinations)
            {
                Examine(result, "destination", destination.Id, destination.Map,
                    destination.X, destination.Y, destination.Z);
            }

            // An arrival carries no map of its own - it belongs to its destination and inherits
            // it, which is why the record has no "map" key. Resolve through the parent rather
            // than assuming the primary facet, so a second facet would still be handled.
            foreach (NavArrival arrival in store.Arrivals)
            {
                NavDestination parent = Nav.Destination(arrival.DestinationId);

                Examine(result, "arrival", arrival.DestinationId,
                    parent == null ? null : parent.Map, arrival.X, arrival.Y, arrival.Z);
            }

            return result;
        }

        private static void Examine(Result result, string kind, string id, Map map, int x, int y, int z)
        {
            if (map == null || map == Map.Internal)
            {
                // A record whose map will not resolve is already a load-time error with its own
                // message; counting it here would report the same fault twice in different words.
                result.Skipped++;
                return;
            }

            result.Scanned++;

            int stood;

            if (!NavWalker.TryResolveZ(map, new Point3D(x, y, z), out stood))
            {
                int landZ = map.GetAverageZ(x, y);

                var stranded = new Stranded
                {
                    Kind = kind,
                    Id = id,
                    X = x,
                    Y = y,
                    Z = z,
                    LandZ = landZ,
                    // The walker's own standability test, mobiles deliberately not counted: who is
                    // standing there today says nothing about whether the tile is authorable.
                    FitsAtLand = map.CanFit(x, y, landZ, 16, false, false, true)
                };

                Diagnose(map, stranded);

                result.Cannot.Add(stranded);
                return;
            }

            if (stood == z)
            {
                return;
            }

            result.Changes.Add(new Change
            {
                Kind = kind,
                Id = id,
                X = x,
                Y = y,
                OldZ = z,
                NewZ = stood
            });
        }

        /// <summary>
        /// How far above or below the stored Z a standable surface still counts as "the same tile,
        /// wrong height".
        ///
        /// Twenty is one storey in this world - Britain's upper town stands 20 above its lower -
        /// so it is the distance at which "the floor above" is still the same place and not a
        /// different building.
        /// </summary>
        private const int SameTileZWindow = 20;

        /// <summary>How far to look for somewhere the record could go instead. See Stranded.</summary>
        private const int NearestSearch = 2;

        /// <summary>
        /// Fill in WHY a record is stranded and what could be done about it.
        ///
        /// Read-only and per-record, run only on the records that already failed - twenty of them
        /// on this graph - so it can afford to be thorough where the scan itself cannot.
        /// </summary>
        private static void Diagnose(Map map, Stranded stranded)
        {
            NameBlocker(map, stranded);

            // The same tile at another height. Nearest to the STORED Z rather than to the land,
            // because the question is how wrong the record is, and a record is what would be
            // edited.
            for (int d = 1; d <= SameTileZWindow; d++)
            {
                if (map.CanFit(stranded.X, stranded.Y, stranded.Z + d, 16, false, false, true))
                {
                    stranded.HasStandableZ = true;
                    stranded.StandableZ = stranded.Z + d;
                    break;
                }

                if (map.CanFit(stranded.X, stranded.Y, stranded.Z - d, 16, false, false, true))
                {
                    stranded.HasStandableZ = true;
                    stranded.StandableZ = stranded.Z - d;
                    break;
                }
            }

            // Somewhere else within two. Rings outward so the first hit is the nearest, and the
            // walker's own resolver decides the Z so the answer is one the walker would accept.
            for (int ring = 1; ring <= NearestSearch; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        if (Math.Abs(dx) != ring && Math.Abs(dy) != ring)
                        {
                            continue;
                        }

                        int x = stranded.X + dx;
                        int y = stranded.Y + dy;

                        int found;

                        if (!NavWalker.TryResolveZ(map, new Point3D(x, y, stranded.Z), out found))
                        {
                            continue;
                        }

                        if (!map.CanFit(x, y, found, 16, false, false, true))
                        {
                            continue;
                        }

                        stranded.NearestX = x;
                        stranded.NearestY = y;
                        stranded.NearestZ = found;
                        stranded.NearestDistance = ring;

                        return;
                    }
                }
            }
        }

        /// <summary>
        /// The tallest thing whose span overlaps a mobile standing at the land Z, and how many
        /// others do.
        ///
        /// BOTH statics and items, because CanFit counts both and naming only the map would be
        /// silent about a decoration barrel - which is the one class of blocker somebody can
        /// actually move. Measured against the LAND Z rather than the stored one: the stored Z is
        /// what is under suspicion, and a record floating twenty above its tile would otherwise be
        /// reported as blocked by nothing at all.
        /// </summary>
        private static void NameBlocker(Map map, Stranded stranded)
        {
            int floor = stranded.LandZ;
            int ceiling = floor + 16;

            int bestTop = Int32.MinValue;
            int count = 0;

            StaticTile[] statics = map.Tiles.GetStaticTiles(stranded.X, stranded.Y, true);

            for (int i = 0; i < statics.Length; i++)
            {
                StaticTile tile = statics[i];
                ItemData data = TileData.ItemTable[tile.ID & TileData.MaxItemValue];

                if ((data.Flags & (TileFlag.Impassable | TileFlag.Surface)) == 0)
                {
                    continue;
                }

                int top = tile.Z + data.CalcHeight;

                if (top <= floor || tile.Z >= ceiling)
                {
                    continue;
                }

                count++;

                if (top > bestTop)
                {
                    bestTop = top;
                    stranded.BlockerId = tile.ID;
                    stranded.BlockerName = String.IsNullOrEmpty(data.Name) ? "(unnamed static)" : data.Name;
                }
            }

            IPooledEnumerable items = map.GetItemsInRange(new Point3D(stranded.X, stranded.Y, floor), 0);

            try
            {
                foreach (Item item in items)
                {
                    if (item == null || item.Deleted || item.X != stranded.X || item.Y != stranded.Y)
                    {
                        continue;
                    }

                    ItemData data = item.ItemData;

                    if ((data.Flags & (TileFlag.Impassable | TileFlag.Surface)) == 0)
                    {
                        continue;
                    }

                    int top = item.Z + data.CalcHeight;

                    if (top <= floor || item.Z >= ceiling)
                    {
                        continue;
                    }

                    count++;

                    if (top > bestTop)
                    {
                        bestTop = top;
                        stranded.BlockerId = item.ItemID;
                        stranded.BlockerName = String.IsNullOrEmpty(item.Name)
                            ? (String.IsNullOrEmpty(data.Name) ? "(unnamed item)" : data.Name)
                            : item.Name;
                    }
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                items.Free();
            }

            stranded.BlockerCount = count;
        }

        // -------------------------------------------------------------------
        // The apply
        // -------------------------------------------------------------------

        /// <summary>
        /// Write the corrections through NavigationSystem.ApplyAndSave, which rebuilds the graph
        /// and rolls the whole change back if the file cannot be written - so memory and disk
        /// cannot end up disagreeing about where anything is.
        ///
        /// The store is re-scanned rather than trusting a Result from an earlier call: a dry run
        /// somebody read ten minutes ago is not a promise about the file now.
        /// </summary>
        public static bool Apply(out Result applied, out string error)
        {
            applied = Scan();
            error = null;

            if (applied.Changes.Count == 0)
            {
                return true;
            }

            NavigationStore store = NavigationSystem.Store;

            // Captured for the undo, by the same index the apply walks, so a rollback puts every
            // record back exactly as it was rather than re-deriving anything.
            var waypoints = new Dictionary<NavWaypoint, int>();
            var destinations = new Dictionary<NavDestination, int>();
            var arrivals = new Dictionary<NavArrival, int>();

            var byKey = new Dictionary<string, Change>(StringComparer.Ordinal);

            foreach (Change change in applied.Changes)
            {
                byKey[Key(change.Kind, change.Id, change.X, change.Y)] = change;
            }

            foreach (NavWaypoint waypoint in store.Waypoints)
            {
                Change change;

                if (byKey.TryGetValue(Key("waypoint", waypoint.Id, waypoint.X, waypoint.Y), out change))
                {
                    waypoints[waypoint] = waypoint.Z;
                }
            }

            foreach (NavDestination destination in store.Destinations)
            {
                Change change;

                if (byKey.TryGetValue(Key("destination", destination.Id, destination.X, destination.Y), out change))
                {
                    destinations[destination] = destination.Z;
                }
            }

            foreach (NavArrival arrival in store.Arrivals)
            {
                Change change;

                if (byKey.TryGetValue(Key("arrival", arrival.DestinationId, arrival.X, arrival.Y), out change))
                {
                    arrivals[arrival] = arrival.Z;
                }
            }

            Result captured = applied;

            bool ok = NavigationSystem.ApplyAndSave(
                () =>
                {
                    foreach (var pair in waypoints)
                    {
                        pair.Key.Z = Find(captured, "waypoint", pair.Key.Id, pair.Key.X, pair.Key.Y);
                    }

                    foreach (var pair in destinations)
                    {
                        pair.Key.Z = Find(captured, "destination", pair.Key.Id, pair.Key.X, pair.Key.Y);
                    }

                    foreach (var pair in arrivals)
                    {
                        pair.Key.Z = Find(captured, "arrival", pair.Key.DestinationId, pair.Key.X, pair.Key.Y);
                    }
                },
                () =>
                {
                    foreach (var pair in waypoints)
                    {
                        pair.Key.Z = pair.Value;
                    }

                    foreach (var pair in destinations)
                    {
                        pair.Key.Z = pair.Value;
                    }

                    foreach (var pair in arrivals)
                    {
                        pair.Key.Z = pair.Value;
                    }
                },
                out error);

            if (ok)
            {
                Log.Info("Resampled Z on {0} navigation record(s).", applied.Changes.Count);
            }

            return ok;
        }

        private static string Key(string kind, string id, int x, int y)
        {
            return String.Format("{0}|{1}|{2}|{3}", kind, id, x, y);
        }

        private static int Find(Result result, string kind, string id, int x, int y)
        {
            foreach (Change change in result.Changes)
            {
                if (change.X == x && change.Y == y
                    && String.Equals(change.Kind, kind, StringComparison.Ordinal)
                    && String.Equals(change.Id, id, StringComparison.Ordinal))
                {
                    return change.NewZ;
                }
            }

            return 0;
        }

        // -------------------------------------------------------------------
        // Reporting
        // -------------------------------------------------------------------

        /// <summary>The lines a dry run prints, and the ones the request token puts in warnings.</summary>
        public static List<string> Describe(Result result, bool applied)
        {
            var lines = new List<string>();

            Change largest = result.Largest;

            lines.Add(String.Format(
                "{0} {1} record(s) of {2} scanned; largest correction {3}.",
                applied ? "Corrected" : "Would correct",
                result.Changes.Count,
                result.Scanned,
                largest == null ? "none" : String.Format("{0} ({1})", largest.Delta, largest)));

            // Every correction when there are few enough to read, otherwise only the ones worth a
            // second look. A list somebody scrolls past is the same as no list.
            List<Change> named = result.Changes.Count <= 40 ? result.Changes : result.Large;

            if (named.Count > 0)
            {
                lines.Add(result.Changes.Count <= 40
                    ? "Corrections:"
                    : String.Format("Corrections over {0}:", LargeCorrection));

                foreach (Change change in named)
                {
                    lines.Add("  " + change);
                }
            }

            // The important half, and it splits in two.
            //
            // AN ARRIVAL THAT IS SOLID IS A FAULT. Arrivals are the authored standable tiles - the
            // whole reason they exist - and NavArrivals.TryPick chooses among them at RANDOM with
            // no standability test, so an unstandable one is handed out like any other and the bot
            // sent to it can never arrive.
            //
            // A DESTINATION THAT IS SOLID IS NORMAL. A destination's coordinate is the LANDMARK -
            // the bank counter, the forge tile - not a place to stand, which is exactly why it has
            // arrivals. Reporting those as misplaced would send somebody to fix a forge for being
            // solid.
            var badArrivals = new List<Stranded>();
            var landmarks = new List<Stranded>();

            foreach (Stranded stranded in result.Cannot)
            {
                if (String.Equals(stranded.Kind, "destination", StringComparison.Ordinal))
                {
                    landmarks.Add(stranded);
                }
                else
                {
                    badArrivals.Add(stranded);
                }
            }

            if (badArrivals.Count > 0)
            {
                lines.Add(String.Format(
                    "{0} ARRIVAL(S) ARE UNSTANDABLE and were not moved. An arrival is the authored "
                    + "tile a bot stands on, and the picker hands these out at random with no "
                    + "standability test - so every bot sent to one is a walk that cannot finish. "
                    + "Place them by hand:",
                    badArrivals.Count));

                foreach (Stranded stranded in badArrivals)
                {
                    lines.Add("  " + stranded);
                }
            }

            if (landmarks.Count > 0)
            {
                lines.Add(String.Format(
                    "{0} destination landmark(s) sit on solid ground, which is expected - a "
                    + "destination names the forge or the counter, not a tile to stand on. Listed "
                    + "only so they are not mistaken for the arrivals above:",
                    landmarks.Count));

                foreach (Stranded stranded in landmarks)
                {
                    lines.Add("  " + stranded);
                }
            }

            if (result.Skipped > 0)
            {
                lines.Add(String.Format("{0} record(s) skipped for an unresolved map.", result.Skipped));
            }

            return lines;
        }

        // -------------------------------------------------------------------
        // The command
        // -------------------------------------------------------------------

        public static void Initialize()
        {
            CommandSystem.Register("NavResampleZ", AccessLevel.Administrator, OnCommand);
        }

        [Usage("NavResampleZ [apply]")]
        [Description(
            "Reports every navigation record whose stored Z is not where a mobile would stand, and "
            + "with 'apply' corrects them. Records the walker cannot place at all are reported and "
            + "never moved - those are misplaced, not stale.")]
        private static void OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            bool apply = e.Arguments.Length > 0
                && Insensitive.Equals(e.Arguments[0], "apply");

            Result result;
            string error = null;
            bool ok = true;

            if (apply)
            {
                ok = Apply(out result, out error);
            }
            else
            {
                result = Scan();
            }

            foreach (string line in Describe(result, apply && ok))
            {
                from.SendMessage(line);
            }

            if (!ok)
            {
                from.SendMessage(0x35, "Nothing was written and the change was rolled back: " + error);
                return;
            }

            if (!apply)
            {
                from.SendMessage("Run [NavResampleZ apply to write them.");
                return;
            }

            if (result.Changes.Count > 0)
            {
                from.SendMessage(0x40, "Written. Run [NavExportGolden and commit both files.");

                CommandLogging.WriteLine(
                    from,
                    String.Format(
                        "{0} {1} resampling Z on {2} navigation record(s)",
                        from.AccessLevel,
                        CommandLogging.Format(from),
                        result.Changes.Count));
            }
        }
    }
}
