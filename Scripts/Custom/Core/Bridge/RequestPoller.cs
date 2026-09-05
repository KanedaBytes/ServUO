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
                    ok = Dispatch(name, body, out message);
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

            WriteAck(name, body, ok, message);

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
        private static bool Dispatch(string name, string body, out string message)
        {
            string error;

            switch (name.ToLowerInvariant())
            {
                case "nav-reload":
                    if (!NavigationSystem.TryReload(out error))
                    {
                        message = error;
                        return false;
                    }

                    message = String.Format("{0} waypoint(s), {1} destination(s), {2} warning(s)",
                        NavigationSystem.Graph.NodeCount,
                        NavigationSystem.Destinations.Count,
                        NavigationSystem.DataWarnings.Count);
                    return true;

                case "dailylife-reload":
                    if (!DailyLifeCommands.TryReload(out error))
                    {
                        message = error;
                        return false;
                    }

                    message = String.Format("{0} config warning(s)", DailyLifeSystem.ConfigWarnings.Count);
                    return true;

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

        private static void WriteAck(string name, string token, bool ok, string message)
        {
            var builder = new StringBuilder(256);

            builder.Append("{\n");
            builder.Append("  \"request\": ").Append(Json.Quote(name)).Append(",\n");
            builder.Append("  \"token\": ").Append(Json.Quote(token)).Append(",\n");
            builder.Append("  \"ok\": ").Append(ok ? "true" : "false").Append(",\n");
            builder.Append("  \"message\": ").Append(Json.Quote(message)).Append(",\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append("\n");
            builder.Append("}\n");

            string error;

            if (!AtomicFile.Write(RequestDirectory + "/" + name + ".ack.json", builder.ToString(), out error))
            {
                Log.Error("Could not write the ack for '{0}': {1}", name, error);
            }
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
