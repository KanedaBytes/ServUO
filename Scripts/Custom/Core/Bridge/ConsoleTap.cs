// ConsoleTap.cs - the shard's console, readable from outside the window it is printed in.
//
// WHY THIS EXISTS
// ---------------
// The shard's log IS its console, and nothing outside that window can read it. `tools/dev.ps1`
// starts the shard with `Start-Process powershell -NoExit` so a crash leaves its evidence on
// screen, which is the right call and also means the editor - running in a browser, talking to a
// bridge, with the shard's window behind it - has no way to see a single line of it.
//
// ServUO writes `Logs/Console.log` only under `-service` (`Server/Main.cs:392-399`), and
// `CustomLogger` goes straight to `Utility.WriteConsoleColor`. So there was no file to tail.
//
// THE SEAM THAT LOOKS RIGHT AND IS NOT
// ------------------------------------
// `Core.MultiConsoleOut` is a `MultiTextWriter` and `Scripts/Services/RemoteAdmin/Network.cs:23`
// adds a listener to it, which reads exactly like the intended extension point. It is bypassed in
// a RELEASE build, which is what `build.ps1` produces: `ConsoleHook.Initialize()`
// (`Scripts/Misc/Timestamp.cs:31-38`) calls `Console.SetOut` with a writer that holds
// `Console.OpenStandardOutput()` and writes to it directly, so from that moment on nothing reaches
// `MultiConsoleOut` at all. (Which also means RemoteAdmin's console relay has been dead in Release
// for as long as both have existed. Noted, not fixed - it is upstream and nothing here uses it.)
//
// So this wraps whatever `Console.Out` IS, from an Initialize at [CallPriority(900)].
// `ConsoleHook.Initialize` is untagged and therefore priority 0 (CLAUDE.md section 3), so it has
// already run; `ScriptCompiler.Invoke` sorts by that comparer before invoking
// (`ScriptCompiler.cs:106`). One path works in Debug, where ConsoleHook is compiled out and
// `Console.Out` is the MultiTextWriter, and in Release, where it is the hook. No upstream edit.
//
// WHAT IT WRITES
// --------------
// A ring buffer of the last N lines to `Data/Live/console.json`, on a timer, and only when the
// buffer has changed. The buffer is the point: a file that grew without bound would be a second
// logging system, and this is a tail, not an archive - the console window is still where the whole
// log lives.
//
// THREADING. `Console.SetOut` wraps what it is given in `TextWriter.Synchronized`, so the Write
// overloads below are already serialised against each other - but the flush timer reads the buffer
// from the game thread while a socket thread may be writing to it, so the buffer has its own lock.
// Nothing here ever touches world state, so the LoopQueue rule does not apply: it is a list of
// strings and a file.
//
// NOTHING HERE MAY THROW. A logger that can break the thing it is logging is worse than no logger,
// which is the rule CustomLogger already states in its own header.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Server.Accounting;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// Tees `Console.Out` into a ring buffer, and publishes that buffer and a login feed as
    /// `Data/Live/console.json` and `Data/Live/logins.json`.
    /// </summary>
    public static class ConsoleTap
    {
        public const string ConsolePath = "Data/Live/console.json";

        public const string LoginPath = "Data/Live/logins.json";

        /// <summary>
        /// Lines kept. Two thousand is a few minutes of a busy boot and about 200 KB, which is a
        /// tail somebody scrolls rather than a log somebody greps.
        /// </summary>
        private static int Capacity
        {
            get { return Math.Max(50, Config.Get("Custom.ConsoleTapLines", 2000)); }
        }

        /// <summary>Sign-ins and sign-outs kept. Far fewer, and each one is worth more.</summary>
        private static int LoginCapacity
        {
            get { return Math.Max(20, Config.Get("Custom.ConsoleTapLogins", 200)); }
        }

        private static TimeSpan Interval
        {
            get { return TimeSpan.FromSeconds(Config.Get("Custom.ConsoleTapSeconds", 1.0)); }
        }

        private static readonly object _sync = new object();

        private static readonly Queue<Line> _lines = new Queue<Line>();

        private static readonly Queue<Line> _logins = new Queue<Line>();

        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        private static bool _enabled;

        private static bool _installed;

        /// <summary>Bumped on every appended line, so a reader can tell a quiet shard from a dead one.</summary>
        private static long _sequence;

        private static long _consoleWritten = -1;

        private static long _loginSequence;

        private static long _loginWritten = -1;

        private sealed class Line
        {
            public DateTime Utc;

            public string Text;
        }

        public static void Configure()
        {
            _enabled = Config.Get("Custom.ConsoleTap", true);

            if (!_enabled)
            {
                return;
            }

            // The login feed answers a different question from the console on a different cadence,
            // so it is its own file: folding it in would make the console's sequence number lie
            // about which of the two last changed.
            EventSink.Login += OnLogin;
            EventSink.Logout += OnLogout;
            EventSink.Disconnected += OnDisconnected;

            HealthCheck.Register("Bridge.ConsoleTap", BuildHealthResult);

            // INSTALLED TWICE, ON PURPOSE, AND THE FIRST ONE IS FOR THE BOOT.
            //
            // Configure runs before World.Load, so this catches the part of a boot that is worth
            // reading when a boot goes wrong: the region parse, the world load, the serialization
            // warnings. It is then thrown away rather than chained, because ConsoleHook.Initialize
            // calls Console.SetOut with a writer over the RAW stdout stream a moment later - so
            // nothing survives it, and the Initialize install below is what covers the running
            // shard. The gap between the two is one Invoke pass.
            Install("Configure");
        }

        /// <summary>
        /// 900, so this runs AFTER ConsoleHook.Initialize, which is untagged and therefore 0.
        ///
        /// Installing first would tee the MultiTextWriter and then be replaced by the hook a moment
        /// later, and the file would hold the boot banner and nothing else - which is the worst
        /// kind of broken, because it looks like it is working.
        /// </summary>
        [CallPriority(900)]
        public static void Initialize()
        {
            if (!_enabled)
            {
                return;
            }

            Install("Initialize");

            if (_installed)
            {
                Timer.DelayCall(Interval, Interval, Flush);
            }
        }

        private static void Install(string stage)
        {
            try
            {
                TextWriter previous = Console.Out;

                // Our own writer already in place means ConsoleHook did NOT replace it, which is
                // the Debug build. Wrapping it again would count every line twice.
                if (previous is Tee)
                {
                    return;
                }

                Console.SetOut(new Tee(previous));
                _installed = true;

                Log.Info(
                    "Console tap installed at {0} over {1}; last {2} line(s) to {3}.",
                    stage, previous.GetType().Name, Capacity, ConsolePath);
            }
            catch (Exception ex)
            {
                // Never fatal. A shard that will not boot because its log tail would not install
                // is a worse trade than a panel with an empty feed.
                Log.Error("Console tap not installed at " + stage + ": " + ex.Message);
            }
        }

        /// <summary>The tail, newest last, for the bridge. Cheap enough to call on a poll.</summary>
        public static IList<string> Tail(int count)
        {
            var take = new List<string>();

            lock (_sync)
            {
                int skip = Math.Max(0, _lines.Count - Math.Max(1, count));
                int at = 0;

                foreach (Line line in _lines)
                {
                    if (at++ >= skip)
                    {
                        take.Add(line.Text);
                    }
                }
            }

            return take;
        }

        // ---- internals ---------------------------------------------------------------------------

        private static void Append(Queue<Line> into, int capacity, string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_sync)
            {
                into.Enqueue(new Line { Utc = DateTime.UtcNow, Text = text });

                while (into.Count > capacity)
                {
                    into.Dequeue();
                }
            }
        }

        private static void OnLogin(LoginEventArgs e)
        {
            Note("login", e.Mobile);
        }

        private static void OnLogout(LogoutEventArgs e)
        {
            Note("logout", e.Mobile);
        }

        /// <summary>
        /// Disconnected fires for a dropped link where Logout does not, and the two together are
        /// what makes "who is on" answerable. A bot is never in here: it has no NetState.
        /// </summary>
        private static void OnDisconnected(DisconnectedEventArgs e)
        {
            Note("disconnect", e.Mobile);
        }

        private static void Note(string kind, Mobile mobile)
        {
            try
            {
                if (mobile == null)
                {
                    return;
                }

                NetState state = mobile.NetState;
                Account account = mobile.Account as Account;

                Append(_logins, LoginCapacity, String.Format(
                    "{0} {1}{2}{3} at {4},{5}",
                    kind,
                    mobile.Name,
                    account == null ? "" : " [" + account.Username + "]",
                    state == null || state.Address == null ? "" : " from " + state.Address,
                    mobile.X,
                    mobile.Y));

                _loginSequence++;
            }
            catch
            {
                // See the header: logging never breaks the thing it logs.
            }
        }

        private static void Flush()
        {
            try
            {
                if (_sequence != _consoleWritten)
                {
                    string error;

                    if (AtomicFile.Write(ConsolePath, Render(_lines, _sequence), out error))
                    {
                        _consoleWritten = _sequence;
                    }
                }

                if (_loginSequence != _loginWritten)
                {
                    string error;

                    if (AtomicFile.Write(LoginPath, Render(_logins, _loginSequence), out error))
                    {
                        _loginWritten = _loginSequence;
                    }
                }
            }
            catch
            {
            }
        }

        private static string Render(Queue<Line> from, long sequence)
        {
            var builder = new StringBuilder(16384);

            builder.Append("{\n  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            builder.Append("  \"sequence\": ").Append(sequence).Append(",\n");
            builder.Append("  \"lines\": [");

            lock (_sync)
            {
                bool first = true;

                foreach (Line line in from)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;

                    builder.Append("\n    {\"utc\":").Append(Json.Quote(line.Utc.ToString("o")));
                    builder.Append(",\"text\":").Append(Json.Quote(line.Text)).Append('}');
                }
            }

            builder.Append("\n  ]\n}\n");

            return builder.ToString();
        }

        private static HealthResult BuildHealthResult()
        {
            if (!_installed)
            {
                return HealthResult.Warn("not installed - the editor's console feed will be empty");
            }

            lock (_sync)
            {
                return HealthResult.Ok(String.Format(
                    "{0} of {1} line(s) buffered at sequence {2}; {3} login event(s)",
                    _lines.Count, Capacity, _sequence, _logins.Count));
            }
        }

        /// <summary>
        /// Forwards everything to the writer it replaced and keeps a copy of each completed line.
        ///
        /// Only the string overloads are intercepted. The char overloads still forward, but a
        /// partial line assembled character by character is not worth reassembling here: everything
        /// in this tree that logs does so with WriteLine(string), and a tap that tried to buffer
        /// stray chars would hold a half-line for ever whenever one was never finished.
        /// </summary>
        private sealed class Tee : TextWriter
        {
            private readonly TextWriter _inner;

            public Tee(TextWriter inner)
            {
                _inner = inner;
            }

            public override Encoding Encoding
            {
                get { return _inner == null ? Encoding.UTF8 : _inner.Encoding; }
            }

            public override void WriteLine(string value)
            {
                Capture(value);

                if (_inner != null)
                {
                    _inner.WriteLine(value);
                }
            }

            public override void Write(string value)
            {
                if (_inner != null)
                {
                    _inner.Write(value);
                }
            }

            public override void Write(char value)
            {
                if (_inner != null)
                {
                    _inner.Write(value);
                }
            }

            public override void Write(char[] buffer, int index, int count)
            {
                if (_inner != null)
                {
                    _inner.Write(buffer, index, count);
                }
            }

            public override void Flush()
            {
                if (_inner != null)
                {
                    _inner.Flush();
                }
            }

            private static void Capture(string value)
            {
                try
                {
                    Append(_lines, Capacity, value);
                    _sequence++;
                }
                catch
                {
                }
            }
        }
    }
}
