using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Mirrors the health registry to Data/Live/health.json for the editor's side panel.
    ///
    /// Written on a slow timer and after every [CoreSmoke, so the panel shows the same thing the
    /// console does rather than a second, subtly different opinion.
    /// </summary>
    public static class HealthSnapshot
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        public const string OutputPath = "Data/Live/health.json";

        /// <summary>
        /// Slow on purpose. Health checks are cheap but not free - one of them counts vendors in
        /// the world - and nothing here changes second to second.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60.0);

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromSeconds(5.0), Interval, Write);
        }

        public static void Write()
        {
            try
            {
                List<HealthResult> results = HealthCheck.RunAll();

                var builder = new StringBuilder(1024);

                builder.Append("{\n");
                builder.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
                builder.Append("  \"worst\": \"").Append(HealthCheck.Worst(results)).Append("\",\n");
                builder.Append("  \"checks\": [\n");

                for (int i = 0; i < results.Count; i++)
                {
                    builder.Append("    {\"name\":").Append(Json.Quote(results[i].Name));
                    builder.Append(",\"status\":").Append(Json.Quote(results[i].Status.ToString()));
                    builder.Append(",\"detail\":").Append(Json.Quote(results[i].Detail));
                    builder.Append(i < results.Count - 1 ? "},\n" : "}\n");
                }

                builder.Append("  ]\n}\n");

                string error;

                if (!AtomicFile.Write(OutputPath, builder.ToString(), out error))
                {
                    Log.Error("Health snapshot not written: {0}", error);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Health snapshot failed.");
            }
        }
    }
}
