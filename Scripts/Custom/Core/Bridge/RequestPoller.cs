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

            // Delete FIRST, and always. A token that survives its own failure is retried on every
            // tick for ever; and a token left on disk after a malformed request looks exactly
            // like one the shard never noticed, which is the worst of both.
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

                    NavAudit.TryRun(out auditSummary, out auditReport, out problems);
                    NavAudit.WriteSnapshot(problems);

                    // Never a failure: an audit that finds blocked edges has done its job. The
                    // findings go in warnings, and the full structured set is in nav-audit.json
                    // because the ack caps its arrays at forty.
                    warnings = auditReport;
                    message = auditSummary;
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

                case "livemap-on":
                    return StartLiveMap(body, out message);

                case "livemap-off":
                    LiveMapSnapshot.Stop();
                    message = "live map stopped";
                    return true;

                case "health":
                    HealthSnapshot.Write();
                    message = "health snapshot written";
                    return true;

                case "nav-export-golden":
                    if (!NavExportGolden.Export(out error))
                    {
                        message = error;
                        return false;
                    }

                    message = "golden fixtures written to " + NavExportGolden.GoldenDirectory;
                    return true;

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
