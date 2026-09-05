using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Levelled console logging for custom shard code. ServUO has no logging abstraction at all
    /// (see CLAUDE.md section 14) - the stock idiom is a per-file "ToConsole" helper wrapping
    /// Utility.PushColor / Console.WriteLine / Utility.PopColor. This generalises that.
    ///
    /// Nothing here ever throws. Logging is not allowed to be the thing that breaks a system.
    /// </summary>
    public sealed class CustomLogger
    {
        private static readonly object _sync = new object();

        private static readonly Dictionary<string, CustomLogger> _loggers =
            new Dictionary<string, CustomLogger>(StringComparer.OrdinalIgnoreCase);

        public string Source { get; private set; }

        private CustomLogger(string source)
        {
            Source = source;
        }

        /// <summary>Returns the cached logger for a source tag, creating it on first use.</summary>
        public static CustomLogger For(string source)
        {
            if (String.IsNullOrWhiteSpace(source))
            {
                source = "Custom";
            }

            lock (_sync)
            {
                CustomLogger logger;

                if (!_loggers.TryGetValue(source, out logger))
                {
                    logger = new CustomLogger(source);
                    _loggers[source] = logger;
                }

                return logger;
            }
        }

        public void Debug(string message)
        {
            Write(LogLevel.Debug, message);
        }

        public void Debug(string format, params object[] args)
        {
            Write(LogLevel.Debug, Safe(format, args));
        }

        public void Info(string message)
        {
            Write(LogLevel.Info, message);
        }

        public void Info(string format, params object[] args)
        {
            Write(LogLevel.Info, Safe(format, args));
        }

        public void Warn(string message)
        {
            Write(LogLevel.Warn, message);
        }

        public void Warn(string format, params object[] args)
        {
            Write(LogLevel.Warn, Safe(format, args));
        }

        public void Error(string message)
        {
            Write(LogLevel.Error, message);
        }

        public void Error(string format, params object[] args)
        {
            Write(LogLevel.Error, Safe(format, args));
        }

        public void Error(Exception ex, string message)
        {
            Write(LogLevel.Error, message);

            if (ex != null)
            {
                Write(LogLevel.Error, "  " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void Error(Exception ex, string format, params object[] args)
        {
            Error(ex, Safe(format, args));
        }

        /// <summary>
        /// Formats defensively. A bad template must not take down the caller, so a
        /// FormatException degrades to the raw template rather than propagating.
        /// </summary>
        private static string Safe(string format, object[] args)
        {
            if (format == null)
            {
                return String.Empty;
            }

            if (args == null || args.Length == 0)
            {
                return format;
            }

            try
            {
                return String.Format(format, args);
            }
            catch
            {
                return format;
            }
        }

        private void Write(LogLevel level, string message)
        {
            // Debug is noise on a live shard; it costs nothing when the level is off.
            if (level == LogLevel.Debug && !Core.Debug)
            {
                return;
            }

            try
            {
                string line = String.Concat(
                    "[", DateTime.UtcNow.ToString("HH:mm:ss"), "] ",
                    "[", Label(level), "] ",
                    "[", Source, "] ",
                    message ?? String.Empty);

                // Deliberately the single-argument overload. Utility.WriteConsoleColor also has a
                // (ConsoleColor, string, params object[]) form, and routing an already-formatted
                // string through that runs it back through String.Format - any stray brace in a
                // message (a serial, a JSON fragment) would then throw FormatException.
                Utility.WriteConsoleColor(ColorFor(level), line);
            }
            catch
            {
                // Console may be redirected or closed. Never propagate.
            }
        }

        private static string Label(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Info: return "INFO ";
                case LogLevel.Warn: return "WARN ";
                case LogLevel.Error: return "ERROR";
            }

            return "?????";
        }

        private static ConsoleColor ColorFor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return ConsoleColor.DarkGray;
                case LogLevel.Info: return ConsoleColor.Green;
                case LogLevel.Warn: return ConsoleColor.Yellow;
                case LogLevel.Error: return ConsoleColor.Red;
            }

            return ConsoleColor.Gray;
        }
    }
}
