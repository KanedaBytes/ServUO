using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Watches Data/Live/requests/ for token files and runs the matching command path.
    ///
    /// This is the whole editor-to-shard channel. The bridge drops a file, the shard acts and
    /// writes an ack beside it. No sockets, no RPC, no protocol version, and it survives either
    /// side restarting - a token written while the shard is down is picked up when it comes back.
    /// It is also debuggable with `type` and `del`, which an HTTP API is not.
    ///
    /// The poller is a plain repeating Timer, not a LoopQueue post: a Timer callback already runs
    /// on the game thread, so LoopQueue would only add a hop. LoopQueue stays the seam if the
    /// directory scan ever moves off-thread.
    /// </summary>
    public static class RequestPoller
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        public const string RequestDirectory = "Data/Live/requests";

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0);

        /// <summary>
        /// A token bigger than this is not a token. The bridge writes a few dozen bytes; anything
        /// larger is a mistake or a mischief, and reading it into memory is the only cost we are
        /// not prepared to pay.
        /// </summary>
        private const int MaxTokenBytes = 4096;

        // The same cap LogWarnings uses for the console, so the ack and the log agree.
        private const int MaxDetails = 40;

        /// <summary>
        /// Tiles between the waypoints nav-route proposes along a road.
        ///
        /// Ten, comfortably inside Custom.NavHopMaxTiles (12) so a proposed hop is never born
        /// over the cap, and far enough apart that a corridor is a handful of records rather than
        /// a waypoint every step.
        /// </summary>
        private const int HopSpacing = 10;

        private static readonly IList<string> NoDetails = new string[0];

        private static long _handled;
        private static long _failed;
        private static string _lastRequest;
        private static string _lastResult;
        private static DateTime? _lastUtc;

        public static void Initialize()
        {
            HealthCheck.Register("Bridge.Poller", BuildHealthResult);

            Timer.DelayCall(Interval, Interval, Poll);
        }

        private static void Poll()
        {
            if (World.Loading || World.Saving)
            {
                // A reload mid-save would be a mutation the save may or may not include. The
                // token stays on disk and is picked up on the next tick.
                return;
            }

            string directory = Path.Combine(Core.BaseDirectory, RequestDirectory);

            string[] files;

            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                files = Directory.GetFiles(directory, "*.token");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not scan {0}.", RequestDirectory);
                return;
            }

            for (int i = 0; i < files.Length; i++)
            {
                Handle(files[i]);
            }
        }

        private static void Handle(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string body = null;
            bool ok;
            string message;
            IList<string> errors = NoDetails;
            IList<string> warnings = NoDetails;

            try
            {
                var info = new FileInfo(path);

                if (info.Length > MaxTokenBytes)
                {
                    ok = false;
                    message = String.Format("token is {0} bytes; the limit is {1}", info.Length, MaxTokenBytes);
                }
                else
                {
                    body = File.ReadAllText(path).Trim();
                    ok = Dispatch(name, body, out message, out errors, out warnings);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                message = ex.Message;
            }

            // Delete BEFORE THE ACKNOWLEDGEMENT, and always - which is AFTER the dispatch above,
            // and the difference is not pedantry. The token sits on disk, read and not yet
            // deleted, for as long as the request takes to run; a second request published into
            // that window is deleted here, unread, by this one's cleanup. That is REVIEW.md's F3,
            // it is a property of this ordering rather than a bug in it, and the fix is a request
            // identity rather than a different order - the bridge's interim guard is to refuse a
            // publish while a token for that operation is still on disk. The comment used to read
            // "delete FIRST", and the fake shard in the editor tests read it as "before dispatch"
            // and deleted up front, which left the suite unable to reproduce the window at all.
            //
            // Unconditional, whatever happened: a token that survives its own failure is retried
            // on every tick for ever, and a token left on disk after a malformed request looks
            // exactly like one the shard never noticed, which is the worst of both.
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not delete the request token {0}.", name);
            }

            WriteAck(name, body, ok, message, errors, warnings);

            _handled++;
            _lastRequest = name;
            _lastResult = (ok ? "ok: " : "failed: ") + message;
            _lastUtc = DateTime.UtcNow;

            if (ok)
            {
                Log.Info("Request '{0}': {1}", name, message);
            }
            else
            {
                _failed++;
                Log.Warn("Request '{0}' failed: {1}", name, message);
            }
        }

        /// <summary>
        /// Runs the existing command path for a request name.
        ///
        /// An unknown name is a failure with an ack, never a silent skip: a request that vanishes
        /// without trace is indistinguishable from a bridge that never wrote one, and that is
        /// exactly the bug that costs an afternoon.
        /// </summary>
        private static bool Dispatch(
            string name, string body, out string message,
            out IList<string> errors, out IList<string> warnings)
        {
            string error;

            errors = NoDetails;
            warnings = NoDetails;

            switch (name.ToLowerInvariant())
            {
                case "nav-reload":
                    if (!NavigationSystem.TryReload(out error, out errors))
                    {
                        message = error;
                        return false;
                    }

                    warnings = NavigationSystem.DataWarnings;

                    message = String.Format("{0} waypoint(s), {1} destination(s), {2} warning(s)",
                        NavigationSystem.Graph.NodeCount,
                        NavigationSystem.Destinations.Count,
                        NavigationSystem.DataWarnings.Count);
                    return true;

                case "dailylife-reload":
                    if (!DailyLifeCommands.TryReload(out error, out errors))
                    {
                        message = error;
                        return false;
                    }

                    warnings = DailyLifeSystem.ConfigWarnings;

                    message = String.Format("{0} config warning(s)", DailyLifeSystem.ConfigWarnings.Count);
                    return true;

                // Restricted zones have no warning tier at all: they load, or they do not.
                case "zones-reload":
                    if (!RestrictedZoneSystem.TryReload(out error, out errors))
                    {
                        message = error;
                        return false;
                    }

                    message = String.Format("{0} restricted zone(s)", RestrictedZoneSystem.Zones.Count);
                    return true;

                // The editor's per-save reload. The body is a file NAME relative to Spawns/Custom -
                // never a path - and everything after the first space is the bridge's nonce.
                //
                // Resolved and range-checked on this side as well as in the bridge's whitelist. A
                // sandbox enforced only by the caller is a sandbox enforced by whoever calls next.
                case "spawn-reload":
                {
                    string relative = (body ?? "").Trim();
                    int space = relative.IndexOf(' ');

                    if (space >= 0)
                    {
                        relative = relative.Substring(0, space);
                    }

                    string spawnSummary;

                    if (!GGSpawnCommands.TryReloadFile(relative, out spawnSummary, out error))
                    {
                        message = error;
                        return false;
                    }

                    message = spawnSummary;
                    return true;
                }

                case "nav-audit":
                {
                    string auditSummary;
                    IList<string> auditReport;
                    IList<NavAuditProblem> problems;

                    // The body was ignored. `full` adds the approach-tile cliff scan, and the
                    // editor deliberately never sends it: runAudit fires quietly after EVERY nav
                    // save, so the default has to stay a second rather than a minute.
                    bool full = (body ?? "").IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0;

                    NavAudit.TryRun(full, out auditSummary, out auditReport, out problems);
                    NavAudit.WriteSnapshot(problems);

                    // Never a failure: an audit that finds blocked edges has done its job. The
                    // findings go in warnings, and the full structured set is in nav-audit.json
                    // because the ack caps its arrays at forty.
                    warnings = auditReport;
                    message = auditSummary;
                    return true;
                }

                // The whole graph, walked by real probe walkers rather than pathed with a Point3D.
                //
                // STARTED RATHER THAN RUN, which is what the ack says. A sweep is fourteen hundred
                // walks and takes minutes; Poll is a Timer callback on the game thread, so
                // dispatching it inline would freeze the world for the length of the audit and the
                // ack would arrive long after any caller had given up. The editor drops this, gets
                // "started", and reads `status` out of walk-audit.json - which is written once at
                // the start with "running" for exactly that.
                //
                // Body: any of a probe count, "selftest" for the one hop the audit must fail,
                // and a probe class key ("creature" or "bot"), in any order. No key means every
                // registered class, which is what an acceptance run wants.
                //
                // THE SAME PARSE AS THE COMMAND, and it has to be: a headless run reaches the
                // audit only through this token, so a class argument the command understood and
                // the token did not would make one of the two classes unreachable without a
                // client.
                case "walk-audit":
                {
                    bool selfTest = false;
                    int probes = 0;
                    string probeKey = null;

                    foreach (string word in (body ?? "").Split(
                        new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int parsed;

                        if (Insensitive.Equals(word, "selftest"))
                        {
                            selfTest = true;
                        }
                        else if (Int32.TryParse(word, out parsed))
                        {
                            probes = parsed;
                        }
                        else
                        {
                            probeKey = word;
                        }
                    }

                    string auditError;

                    if (!NavWalkAudit.TryStart(null, probes, selfTest, probeKey, out auditError))
                    {
                        message = "walk audit not started: " + auditError;
                        return false;
                    }

                    message = String.Format(
                        "walk audit started{0}{1}; watch {2} for status",
                        selfTest ? " (self-test)" : "",
                        probeKey == null ? "" : " (" + probeKey + " only)",
                        NavWalkAudit.SnapshotPath);
                    return true;
                }

                // Walks that climbed the whole ladder and were teleported, with the goal tile and
                // who was standing near it. Body "clear" starts the ledger again, which is how a
                // measurement run gets a clean window without a restart.
                // How much bots move while idle, and what moved them. `clear` opens a window.
                //
                // Its own token rather than a field on `health`, for walk-failures' reason: the
                // value is in the LINES, and a rate reported as a count is no answer.
                case "bot-steps":
                {
                    if (Insensitive.Equals(FirstWord(body), "clear"))
                    {
                        BotStepCensus.Clear();
                        message = "step census cleared";
                        return true;
                    }

                    if (!BotStepCensus.TryWrite(out message))
                    {
                        return false;
                    }

                    warnings = BotStepCensus.Describe();

                    return true;
                }

                case "walk-failures":
                {
                    if (Insensitive.Equals(FirstWord(body), "clear"))
                    {
                        NavWalkFailures.Clear();
                        message = "walk-failure ledger cleared";
                        return true;
                    }

                    if (!NavWalkFailures.TryWrite(out message))
                    {
                        return false;
                    }

                    return true;
                }

                // What the engine sees at one tile, and whether it will let a step onto it.
                // Body "x,y" or "x,y,z" - several tiles may be given, separated by whitespace.
                //
                // Answers in `warnings` as well as to Data/Live/tile-probe.json, because this is
                // the one request whose whole value is the LINES: a headless run reads them off
                // the ack, and a passability question answered by a count is no answer at all.
                case "tile-probe":
                {
                    var probed = new List<string>();
                    int tiles = 0;

                    // "sweep <lo> <hi>" - every world item in an ItemID range, reporting the ones
                    // the engine will let a mobile step onto. The sampling form below cannot
                    // answer a question about 519 tiles, and a token is capped at 4 KB.
                    if (Insensitive.Equals(FirstWord(body), "sweep"))
                    {
                        string[] range = (body ?? "").Split(
                            new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                        int lo, hi;

                        if (range.Length < 3
                            || !TryParseId(range[1], out lo)
                            || !TryParseId(range[2], out hi))
                        {
                            message = "sweep takes two ItemIDs: \"sweep 0x0B20 0x0B25\"";
                            return false;
                        }

                        warnings = TileProbe.Sweep(Map.Trammel, lo, hi);

                        string sweepError;

                        if (!AtomicFile.Write(
                            TileProbe.SnapshotPath, LinesJson(warnings), out sweepError))
                        {
                            message = sweepError;
                            return false;
                        }

                        message = warnings.Count > 1 ? warnings[1] : "swept";
                        return true;
                    }

                    foreach (string word in (body ?? "").Split(
                        new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] numbers = word.Split(',');
                        int px, py;

                        if (numbers.Length < 2
                            || !Int32.TryParse(numbers[0], out px)
                            || !Int32.TryParse(numbers[1], out py))
                        {
                            continue;
                        }

                        int pz;
                        int? hint = numbers.Length > 2 && Int32.TryParse(numbers[2], out pz)
                            ? (int?)pz
                            : null;

                        if (tiles > 0)
                        {
                            probed.Add("");
                        }

                        probed.Add(String.Format(
                            "== {0},{1}{2} ==", px, py, hint == null ? "" : "," + hint.Value));
                        probed.AddRange(TileProbe.Describe(Map.Trammel, px, py, hint));
                        tiles++;
                    }

                    if (tiles == 0)
                    {
                        message = "no tile given; the body is \"x,y\" or \"x,y,z\"";
                        return false;
                    }

                    string probeError;

                    if (!AtomicFile.Write(TileProbe.SnapshotPath, LinesJson(probed), out probeError))
                    {
                        message = probeError;
                        return false;
                    }

                    warnings = probed;
                    message = String.Format("{0} tile(s) probed", tiles);
                    return true;
                }

                // The Z resample. Body "apply" writes; anything else is a dry run.
                //
                // Never a failure for finding something, as nav-audit is not: a record the walker
                // cannot place is exactly what this exists to surface, and it comes back in
                // warnings alongside the corrections.
                case "nav-resample-z":
                {
                    bool applyZ = Insensitive.Equals(FirstWord(body), "apply");
                    NavResampleZ.Result resample;

                    if (applyZ)
                    {
                        if (!NavResampleZ.Apply(out resample, out error))
                        {
                            message = "rolled back, nothing written: " + error;
                            return false;
                        }
                    }
                    else
                    {
                        resample = NavResampleZ.Scan();
                    }

                    warnings = NavResampleZ.Describe(resample, applyZ);
                    message = String.Format(
                        "{0} {1} of {2} record(s); {3} could not be placed",
                        applyZ ? "corrected" : "would correct",
                        resample.Changes.Count,
                        resample.Scanned,
                        resample.Cannot.Count);
                    return true;
                }

                case "gg-reimport":
                    string summary;

                    if (!GGSpawnCommands.TryReimport(null, out summary, out error))
                    {
                        message = error;
                        return false;
                    }

                    message = summary;
                    return true;

                // The population recipe, read-only. Answers what each town would get and whether
                // GG_BotPop.xml still matches - a recipe is derived from the graph and the file is
                // not, so authoring a destination changes the recipe's mind while the file goes on
                // saying the old thing.
                case "botpop-audit":
                {
                    Map facet = BotPopulation.Facet;
                    BotRecipe recipe = BotPopulation.Build(facet);
                    var report = new List<string>(BotPopulation.Describe(facet, recipe));
                    List<string> differences = BotPopulation.DiffAgainstFile(facet, recipe);

                    foreach (string line in differences)
                    {
                        report.Add("regen: " + line);
                    }

                    // Never a failure, for the same reason nav-audit is not: an audit that finds
                    // something has done its job.
                    warnings = report;
                    message = String.Format(
                        "{0} spawner(s) for {1} bot(s); {2}",
                        recipe.Slots.Count,
                        recipe.TotalBots,
                        differences.Count == 0 ? "the file matches" : differences.Count + " difference(s)");
                    return true;
                }

                // Write the recipe out. Deliberately does NOT reimport: gg-reimport deletes every
                // GG_ spawner in the world along with its spawned mobiles, and the editor already
                // puts that behind a confirmation. Two requests, two decisions.
                case "botpop-gen":
                {
                    Map facet = BotPopulation.Facet;
                    BotRecipe recipe = BotPopulation.Build(facet);

                    if (!BotPopulation.Write(facet, recipe, out error))
                    {
                        message = error;
                        return false;
                    }

                    warnings = recipe.Notes;
                    message = String.Format(
                        "wrote {0}: {1} spawner(s) for {2} bot(s), {3} fixed. Now gg-reimport.",
                        BotPopulation.GeneratedPath(facet),
                        recipe.Slots.Count,
                        recipe.TotalBots,
                        recipe.FixedBots);
                    return true;
                }

                case "livemap-on":
                    return StartLiveMap(body, out message);

                case "livemap-off":
                    LiveMapSnapshot.Stop();
                    message = "live map stopped";
                    return true;

                case "world-items":
                    // The art view's furniture. The body is an optional facet name; empty means
                    // the shard's primary facet. It walks World.Items, which is why it is a
                    // request rather than a timer - see the note at the top of WorldItemSnapshot.
                    if (!WorldItemSnapshot.TryWrite(body.Length == 0 ? null : FirstWord(body), out message))
                    {
                        return false;
                    }

                    return true;

                case "health":
                    HealthSnapshot.Write();
                    message = "health snapshot written";
                    return true;

                // What the shard actually loaded, so the editor's create forms can be dropdowns
                // rather than free text typed from memory. No body: it reports everything, and
                // the answer only changes when Scripts.dll does.
                case "vocabulary":
                    return VocabularySnapshot.TryWrite(out message);

                // Per-arrival harvest reach, for the editor's work-site layer and its Site tool.
                //
                // The body is optional and is "<mine|lumber> x,y x,y ..." - points that are NOT in
                // navigation.json yet, so a tile can be judged while it is being placed rather
                // than after a save and a reload. With no body it just refreshes the authored set.
                // Walk a road between two points the way a bot would, doors and gates included,
                // and write the tiles to Data/Live/nav-route.json for the editor to lay waypoints
                // along. Body is "x1,y1 x2,y2 [facet]".
                case "nav-route":
                {
                    string[] fields = (body ?? "").Split(
                        new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);

                    var ends = new List<Point3D>();
                    Map facet = Map.Trammel;

                    for (int i = 0; i < fields.Length; i++)
                    {
                        string field = fields[i];

                        if (field.StartsWith("#"))
                        {
                            continue;
                        }

                        int comma = field.IndexOf(',');

                        if (comma < 0)
                        {
                            Map named;

                            if (JsonConfig.TryParseMap(field, out named))
                            {
                                facet = named;
                            }

                            continue;
                        }

                        int x, y;

                        if (Int32.TryParse(field.Substring(0, comma), out x)
                            && Int32.TryParse(field.Substring(comma + 1), out y))
                        {
                            ends.Add(new Point3D(x, y, 0));
                        }
                    }

                    if (ends.Count != 2)
                    {
                        message = "nav-route needs exactly two points: '1420,1640 1200,1750'";
                        return false;
                    }

                    List<Point3D> route;
                    string routeError;

                    // Flood, verify every hop with a real BaseCreature, and re-flood around any
                    // step the engine refuses. The flood answers "a road exists"; only the
                    // creature answers "the pathfinder a bot uses can walk this leg", and that
                    // second question is the one [NavAudit asks once the waypoints are saved.
                    List<Point3D> hops;

                    bool walked = NavCorridor.TryRoad(
                        facet, ends[0], ends[1], HopSpacing, out route, out hops, out routeError);

                    NavCorridor.WriteSnapshot(
                        facet, ends[0], ends[1], route, hops, walked, routeError, walked, routeError);

                    // A road that cannot be completed is still an ANSWER, and the partial path is
                    // the useful half of it: "it reaches the gate and stops" is a different problem
                    // from "it never leaves town", and the editor draws both.
                    message = walked
                        ? String.Format(
                            "walked {0} tile(s), {1}% on roads; {2} hop(s), every one verified",
                            route.Count, NavCorridor.RoadFraction(facet, route), hops.Count)
                        : String.Format("{0} ({1} tile(s) walked)", routeError, route.Count);

                    return true;
                }

                // The two questions a hand-edited proposal asks, in one request: "is this hop
                // walkable" and "where is the nearest road". Body is
                //   "verify x1,y1 x2,y2 [x3,y3 x4,y4 ...]"  - pairs, each a hop
                //   "snap x,y"                              - one point to pull onto a road
                // Both may appear. Answers go to Data/Live/nav-hop.json.
                case "nav-hop":
                {
                    string[] words = (body ?? "").Split(
                        new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);

                    var pairs = new List<Point3D>();
                    Point3D? snap = null;
                    bool snapping = false;

                    foreach (string word in words)
                    {
                        if (word.StartsWith("#"))
                        {
                            continue;
                        }

                        if (word.IndexOf(',') < 0)
                        {
                            snapping = word.Equals("snap", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }

                        // "x,y" or "x,y,z". Z MATTERS AND USED TO BE DROPPED: every point was
                        // built at Z 0, so probing a bridge deck at Z 6 over water asked the
                        // pathfinder about the river bed and answered "not walkable" about a
                        // bridge a player rides across. A probe that silently substitutes its own
                        // Z cannot be used to decide whether a Z is right.
                        string[] numbers = word.Split(',');
                        int x, y;

                        if (numbers.Length < 2
                            || !Int32.TryParse(numbers[0], out x)
                            || !Int32.TryParse(numbers[1], out y))
                        {
                            continue;
                        }

                        int z = 0;

                        if (numbers.Length > 2 && !Int32.TryParse(numbers[2], out z))
                        {
                            z = 0;
                        }

                        if (snapping)
                        {
                            snap = new Point3D(x, y, z);
                            snapping = false;
                        }
                        else
                        {
                            pairs.Add(new Point3D(x, y, z));
                        }
                    }

                    string json = NavCorridor.ProbeHops(Map.Trammel, pairs, snap);
                    string hopWriteError;

                    if (!AtomicFile.Write("Data/Live/nav-hop.json", json, out hopWriteError))
                    {
                        message = hopWriteError;
                        return false;
                    }

                    message = String.Format(
                        "{0} hop(s) probed{1}", pairs.Count / 2, snap == null ? "" : ", snap answered");
                    return true;
                }

                // "<x>,<y>,<w>,<h>" - a region of the uo-offline reference to propose.
                //
                // Answers as soon as the work is SET UP, not when it is finished: an adopt is a
                // flood-fill per edge and a large region is minutes of them, so it runs in
                // LoopQueue passes and reports progress through Data/Live/nav-adopt.json. The ack
                // saying "started" is the honest answer to a request that cannot complete inside
                // a poll.
                // Re-pick every join in a region by WALKED road length instead of straight line.
                // Body is a region, "x,y,width,height", like nav-adopt's. It proposes edges only -
                // no waypoint is created, moved, renamed or deleted - and writes to the SAME
                // proposal file, so accept-adopt.js accepts it with no change at all.
                case "nav-rejoin":
                {
                    Rectangle2D rejoinRegion;

                    if (!TryParseRegion(body, out rejoinRegion))
                    {
                        message = "nav-rejoin needs a region: 'x,y,width,height'";
                        return false;
                    }

                    NavRejoinResult rejoin;

                    if (!NavRejoin.TryRun(Map.Trammel, rejoinRegion, out rejoin, out error))
                    {
                        message = error;
                        return false;
                    }

                    warnings = NavRejoin.Describe(rejoin);

                    if (!NavRejoin.TryWrite(Map.Trammel, rejoin, out error))
                    {
                        message = error;
                        return false;
                    }

                    message = rejoin.Summary + " Proposal at " + NavAdopt.ProposalPath
                        + "; accept it with tools/editor/accept-adopt.js.";

                    return true;
                }

                case "nav-adopt":
                {
                    string[] parts = (body ?? "").Split(
                        new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);

                    Rectangle2D region = default(Rectangle2D);
                    bool haveRegion = false;
                    bool rebase = false;

                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].StartsWith("#"))
                        {
                            continue;
                        }

                        // The word rather than a flag character, because this one is worth having
                        // to type: rebase is the mode that proposes road over ground we authored.
                        if (Insensitive.Equals(parts[i], "rebase"))
                        {
                            rebase = true;
                            continue;
                        }

                        string[] numbers = parts[i].Split(',');
                        int x, y, width, height;

                        if (numbers.Length == 4
                            && Int32.TryParse(numbers[0], out x)
                            && Int32.TryParse(numbers[1], out y)
                            && Int32.TryParse(numbers[2], out width)
                            && Int32.TryParse(numbers[3], out height))
                        {
                            region = new Rectangle2D(x, y, width, height);
                            haveRegion = true;
                        }
                    }

                    if (!haveRegion)
                    {
                        message = "nav-adopt needs a region: 'x,y,width,height'";
                        return false;
                    }

                    string adoptError;

                    if (!NavAdopt.TryStart(Map.Trammel, region, rebase, out adoptError))
                    {
                        message = String.Format("adopt not started: {0}", adoptError);
                        return false;
                    }

                    message = String.Format(
                        "adopt{0} started over {1}x{2} at {3},{4}; watch Data/Live/nav-adopt.json",
                        rebase ? " (rebase)" : "", region.Width, region.Height, region.X, region.Y);
                    return true;
                }

                // One bot's [BotInfo report, so the editor's bot card can show the same forty
                // lines the command shows in game.
                //
                // A FILE RATHER THAN AN ACK, for the reason nav-audit writes one: the ack's arrays
                // are capped and carry prose, and this is forty lines of aligned skill columns.
                // A file also means the card can re-read it without another round trip.
                //
                // Body is the bot's serial. Nothing else: the editor already has the serial - it is
                // what it draws the dot with - and a name would have to be resolved against a
                // population where names repeat across towns.
                case "botinfo":
                {
                    string[] words = (body ?? "").Split(
                        new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    int serial = 0;

                    for (int i = 0; i < words.Length; i++)
                    {
                        // The bridge appends its nonce as '#abc'; it is not an argument.
                        if (words[i].StartsWith("#"))
                        {
                            continue;
                        }

                        string word = words[i];

                        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            Int32.TryParse(
                                word.Substring(2),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out serial);
                        }
                        else
                        {
                            Int32.TryParse(word, out serial);
                        }
                    }

                    if (serial == 0)
                    {
                        message = "botinfo needs a bot serial: '1074321' or '0x1064B1'";
                        return false;
                    }

                    var bot = World.FindMobile((Serial)serial) as PlayerBot;

                    // A bot that has gone is a normal answer, not a failure: they are ephemeral by
                    // design and the card is looking at a snapshot that may be a poll old. Writing
                    // the empty answer is what stops the card showing the PREVIOUS bot's report
                    // under this one's name.
                    string report = BotInfoSnapshot.BuildJson(serial, bot);
                    string reportError;

                    if (!AtomicFile.Write(BotInfoSnapshot.Path, report, out reportError))
                    {
                        message = reportError;
                        return false;
                    }

                    message = bot == null
                        ? String.Format("no bot with serial 0x{0:X}; wrote an empty report", serial)
                        : String.Format("{0}'s report written", bot.Name);

                    return true;
                }

                case "site-reach":
                {
                    string[] parts = (body ?? "").Split(
                        new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    string probeType = null;
                    var probe = new List<Point3D>();
                    var zones = new List<Rectangle2D>();

                    for (int i = 0; i < parts.Length; i++)
                    {
                        string part = parts[i];

                        // The bridge appends its nonce as '#abc'; it is not an argument.
                        if (part.StartsWith("#"))
                        {
                            continue;
                        }

                        string[] numbers = part.Split(',');

                        // A part is the type, a point, or a zone rect, told apart by how many
                        // numbers it carries. The zone form exists because the editor cannot ask
                        // about a whole zone as points: MaxTokenBytes is 4096 and a point costs
                        // about ten bytes, so a 25x20 lumber zone would not fit in the token at
                        // all. One rect asks the same question in nineteen.
                        if (numbers.Length == 1)
                        {
                            probeType = part;
                            continue;
                        }

                        int x, y;

                        if (numbers.Length == 2)
                        {
                            if (Int32.TryParse(numbers[0], out x) && Int32.TryParse(numbers[1], out y))
                            {
                                probe.Add(new Point3D(x, y, 0));
                            }

                            continue;
                        }

                        int width, height;

                        if (numbers.Length == 4
                            && Int32.TryParse(numbers[0], out x)
                            && Int32.TryParse(numbers[1], out y)
                            && Int32.TryParse(numbers[2], out width)
                            && Int32.TryParse(numbers[3], out height))
                        {
                            zones.Add(new Rectangle2D(x, y, width, height));
                        }
                    }

                    if ((probe.Count > 0 || zones.Count > 0) && probeType == null)
                    {
                        message = "site-reach needs a type before its points: 'mine 1451,1517'";
                        return false;
                    }

                    var swept = 0;
                    var sweepWarnings = new List<string>();

                    for (int i = 0; i < zones.Count; i++)
                    {
                        string problem;
                        List<Point3D> candidates = BotWorkSites.SweepZone(
                            Map.Trammel, zones[i], probeType, out problem);

                        probe.AddRange(candidates);
                        swept += candidates.Count;

                        if (problem != null)
                        {
                            sweepWarnings.Add(problem);
                        }
                    }

                    if (sweepWarnings.Count > 0)
                    {
                        warnings = sweepWarnings;
                    }

                    BotWorkSites.WriteReachSnapshot(Map.Trammel, probe, probeType);

                    message = zones.Count > 0
                        ? String.Format(
                            "site reach written, {0} candidate(s) from {1} zone(s)", swept, zones.Count)
                        : String.Format("site reach written, {0} probe point(s)", probe.Count);
                    return true;
                }

                // Exactly what [Save does (Handlers.cs:583), for the same reason it exists: this
                // is the ONLY way to save from outside the game. ServUO's console takes no staff
                // commands and HandleClosed does not save on exit, so a headless run - a probe
                // over SSH, a verification pass before a rebuild - could otherwise only save by
                // waiting out Config/AutoSave.cfg's fifteen-minute timer.
                //
                // AutoSave.Save rather than World.Save so the backup rotation happens too: a
                // forced save that skipped it would be the one save with no way back.
                //
                // Safe to run inline here. Poll is a Timer callback, so this IS the game thread,
                // which is where World.Save must run; and Poll has already refused to dispatch
                // anything while World.Saving, so this cannot re-enter a save in progress.
                case "save":
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    Server.Misc.AutoSave.Save();

                    watch.Stop();

                    message = String.Format(
                        "world saved in {0:F2}s", watch.Elapsed.TotalSeconds);
                    return true;
                }

                case "nav-export-golden":
                    if (!NavExportGolden.Export(out error))
                    {
                        message = error;
                        return false;
                    }

                    message = "golden fixtures written to " + NavExportGolden.GoldenDirectory;
                    return true;

                // ---- the Admin panel -----------------------------------------------------------
                //
                // STARTED, NOT RUN, for the two smokes. Poll is a Timer callback on the game
                // thread, so dispatching a six-minute probe chain inline would freeze the world for
                // its whole length and the ack would arrive long after any caller had given up -
                // which is exactly why `walk-audit` answers "started" and publishes its own status.
                // Both of these already report through HealthCheck, so the panel reads the result
                // out of health.json rather than out of an ack that cannot wait for it.
                case "core-smoke":
                {
                    // Run(null) is the path Custom.CoreSmokeOnStart already takes at
                    // EventSink.ServerStarted, so the null-Mobile branch is the tested one rather
                    // than a new one this case invented.
                    CoreSmoke.Run(null);

                    message = "core smoke started; its report is on the console and in health.json";
                    return true;
                }

                case "bot-smoke":
                {
                    // BotSmoke.Run handles from == null by falling back to Trammel and the
                    // Britain spawn point (BotSmoke.cs:270), for the same reason.
                    BotSmoke.Run(null);

                    message = "bot smoke started; watch health.json for Bots.* and the console";
                    return true;
                }

                // BOTH HALVES OF THE COMMAND, because the second one is the half that is easy to
                // lose. BotsReload_OnCommand re-validates the work sites after reloading, and it
                // does that in the command body rather than inside TryReload - so a dispatch that
                // called only TryReload would silently do less than [BotsReload and leave a site
                // fixed by a nav edit excluded until a restart.
                case "bots-reload":
                {
                    IList<string> botErrors;

                    if (!BotSystem.TryReload(out error, out botErrors))
                    {
                        message = "bot config NOT reloaded: " + error;
                        errors = botErrors;
                        return false;
                    }

                    BotWorkSites.Validate(Map.Trammel);

                    var excluded = new List<string>();

                    foreach (var entry in BotWorkSites.Excluded)
                    {
                        excluded.Add(String.Format(
                            "work site '{0}' excluded: {1}", entry.Key, entry.Value));
                    }

                    warnings = excluded;
                    message = "bot config reloaded. " + BotSystem.Caps.Describe();
                    return true;
                }

                // The body IS the message, in full - no FirstWord here, and no nonce to strip
                // either, which is why `broadcast` is not in the bridge's NONCED set: appending
                // "#a1b2c3d4" to what every player is about to read is not a thing to do.
                case "broadcast":
                {
                    string text = (body ?? "").Trim();

                    if (text.Length == 0)
                    {
                        message = "nothing to broadcast";
                        return false;
                    }

                    Server.Commands.CommandHandlers.BroadcastMessage(AccessLevel.Player, 0x482, text);

                    message = String.Format(
                        "broadcast to {0} client(s)", Server.Network.NetState.Instances.Count);
                    return true;
                }

                // THE ACK IS WRITTEN BEFORE THE PROCESS DIES, which is the whole trick. Core.Kill
                // calls HandleClosed and then Process.Kill, so a caller polling for an ack after
                // dispatch would poll a dead shard for its timeout and read the shutdown as a
                // failure. Handle() writes the ack when Dispatch returns, so the kill is deferred
                // by one poll interval instead of running inline.
                //
                // The SAVE is the caller's job, not this one's: the bridge drops `save`, waits for
                // its ack, and only then drops this. Doing both here would make a shutdown that
                // failed to save indistinguishable from one that did.
                case "shutdown":
                {
                    Timer.DelayCall(TimeSpan.FromSeconds(2.0), () => Core.Kill(false));

                    message = "shutting down in 2s";
                    return true;
                }

                // How fast is a bot actually stepping, without a client to target one with.
                //
                // STARTED, NOT RUN: the sample is a window of N seconds and the ack cannot wait for
                // it, so the answer lands in Data/Live/bot-pace.json. The body is the window length;
                // "last" reads back the sample already taken rather than starting another, which is
                // what a caller wants after it has waited.
                case "bot-pace":
                {
                    string first = FirstWord(body);

                    if (Insensitive.Equals(first, "last"))
                    {
                        message = BotPaceSnapshot.Describe();
                        return true;
                    }

                    int paceSeconds;

                    if (!Int32.TryParse(first, out paceSeconds))
                    {
                        paceSeconds = 30;
                    }

                    string paceError;

                    if (!BotCommands.TryStartPace(null, paceSeconds, null, out paceError))
                    {
                        message = paceError;
                        return false;
                    }

                    message = String.Format(
                        "sampling a walking bot for {0}s; the answer lands in {1}",
                        paceSeconds, BotPaceSnapshot.Path);
                    return true;
                }

                default:
                    message = "unknown request";
                    return false;
            }
        }

        /// <summary>
        /// livemap-on carries its arguments in the token body: "&lt;seconds&gt; [custom|all] [zoneId]".
        ///
        /// The same refusal as the command: 'all' without a zone is rejected rather than obeyed,
        /// because the bridge is if anything MORE likely than a person to ask for it by accident.
        /// </summary>
        private static bool StartLiveMap(string body, out string message)
        {
            double seconds = 2.0;
            bool all = false;
            string zoneId = null;

            foreach (string part in (body ?? "").Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double parsed;

                if (Double.TryParse(part, out parsed))
                {
                    seconds = parsed;
                }
                else if (Insensitive.Equals(part, "all"))
                {
                    all = true;
                }
                else if (Insensitive.Equals(part, "custom"))
                {
                    all = false;
                }
                else
                {
                    zoneId = part;
                }
            }

            if (seconds < 0.5 || seconds > 60.0)
            {
                message = "interval must be between 0.5 and 60 seconds";
                return false;
            }

            if (all && zoneId == null)
            {
                message = "'all' needs a nav zone to bound it";
                return false;
            }

            if (zoneId != null && Nav.Zone(zoneId) == null)
            {
                message = "no nav zone '" + zoneId + "'";
                return false;
            }

            LiveMapSnapshot.Start(seconds, all, zoneId);

            message = String.Format("live map started, {0}, every {1}s", all ? "all in " + zoneId : "custom", seconds);
            return true;
        }

        /// <summary>
        /// Writes the ack the bridge reads back.
        ///
        /// `errors` and `warnings` carry the shard's own validator strings UNEDITED, so the banner
        /// in the editor and the line in the console say the same thing. Before this, a reload that
        /// succeeded with problems acked "3 warning(s)" and there was no way to find out which
        /// three without reading a console the person driving the editor is not looking at.
        ///
        /// Both arrays are always written, empty when there is nothing to say: an absent key and an
        /// empty list only read alike in JavaScript if every reader remembers to guard, and one of
        /// them will not.
        /// </summary>
        /// <summary>
        /// The first space-delimited word of a body, skipping the bridge's `#nonce`. Bodies that
        /// take one optional argument all want this and nothing more.
        /// </summary>
        /// <summary>An ItemID written either way round - "0x0B20" or "2848".</summary>
        private static bool TryParseId(string word, out int id)
        {
            if (word != null && word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return Int32.TryParse(
                    word.Substring(2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out id);
            }

            return Int32.TryParse(word, out id);
        }

        /// <summary>
        /// A list of report lines as JSON, for the requests whose whole answer IS the lines.
        ///
        /// The ack caps its details at MaxDetails so the console stays readable; a file has no
        /// reason to, and a probe truncated at forty lines is a probe you have to run again.
        /// </summary>
        private static string LinesJson(IList<string> lines)
        {
            var builder = new StringBuilder(1024);

            builder.Append("{\n  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o")));
            builder.Append(",\n  \"lines\": [\n");

            for (int i = 0; i < lines.Count; i++)
            {
                builder.Append("    ").Append(Json.Quote(lines[i]));
                builder.Append(i < lines.Count - 1 ? ",\n" : "\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }

        private static string FirstWord(string body)
        {
            string[] parts = body.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                if (!parts[i].StartsWith("#", StringComparison.Ordinal))
                {
                    return parts[i];
                }
            }

            return null;
        }

        /// <summary>
        /// "x,y,width,height" out of a token body, ignoring the bridge's '#nonce' and any words.
        ///
        /// Shared by nav-rejoin and nav-adopt so the two cannot drift on what a region looks like;
        /// nav-adopt keeps its own loop because it also reads the `rebase` word out of the same
        /// pass and a second walk of the same words to find one flag would be the worse trade.
        /// </summary>
        private static bool TryParseRegion(string body, out Rectangle2D region)
        {
            region = default(Rectangle2D);

            string[] parts = (body ?? "").Split(
                new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("#"))
                {
                    continue;
                }

                string[] numbers = parts[i].Split(',');
                int x, y, width, height;

                if (numbers.Length == 4
                    && Int32.TryParse(numbers[0], out x)
                    && Int32.TryParse(numbers[1], out y)
                    && Int32.TryParse(numbers[2], out width)
                    && Int32.TryParse(numbers[3], out height))
                {
                    region = new Rectangle2D(x, y, width, height);
                    return true;
                }
            }

            return false;
        }

        private static void WriteAck(
            string name, string token, bool ok, string message,
            IList<string> errors, IList<string> warnings)
        {
            var builder = new StringBuilder(256);

            builder.Append("{\n");
            builder.Append("  \"request\": ").Append(Json.Quote(name)).Append(",\n");
            builder.Append("  \"token\": ").Append(Json.Quote(token)).Append(",\n");
            builder.Append("  \"ok\": ").Append(ok ? "true" : "false").Append(",\n");
            builder.Append("  \"message\": ").Append(Json.Quote(message)).Append(",\n");
            AppendDetails(builder, "errors", errors);
            AppendDetails(builder, "warnings", warnings);
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append("\n");
            builder.Append("}\n");

            string error;

            if (!AtomicFile.Write(RequestDirectory + "/" + name + ".ack.json", builder.ToString(), out error))
            {
                Log.Error("Could not write the ack for '{0}': {1}", name, error);
            }
        }

        /// <summary>
        /// Writes one detail array, capped the same way LogWarnings caps the console.
        ///
        /// The same cap in both places on purpose: an ack that listed forty and a log that listed
        /// twenty would send someone hunting for a difference that is not there.
        /// </summary>
        private static void AppendDetails(StringBuilder builder, string key, IList<string> items)
        {
            builder.Append("  \"").Append(key).Append("\": [");

            if (items == null || items.Count == 0)
            {
                builder.Append("],\n");
                return;
            }

            int shown = Math.Min(items.Count, MaxDetails);

            builder.Append('\n');

            for (int i = 0; i < shown; i++)
            {
                builder.Append("    ").Append(Json.Quote(items[i]));

                if (i < shown - 1 || items.Count > shown)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            if (items.Count > shown)
            {
                builder
                    .Append("    ")
                    .Append(Json.Quote(String.Format("... and {0} more.", items.Count - shown)))
                    .Append('\n');
            }

            builder.Append("  ],\n");
        }

        private static HealthResult BuildHealthResult()
        {
            string when = _lastUtc.HasValue ? _lastUtc.Value.ToString("HH:mm:ss") + "Z" : "never";

            if (_handled == 0)
            {
                return HealthResult.Ok("watching " + RequestDirectory + ", no requests yet");
            }

            string detail = String.Format(
                "{0} request(s), {1} failed, last '{2}' at {3} - {4}",
                _handled, _failed, _lastRequest, when, _lastResult);

            return _failed > 0 ? HealthResult.Warn(detail) : HealthResult.Ok(detail);
        }
    }
}
