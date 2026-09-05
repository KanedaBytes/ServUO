using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public enum HealthStatus
    {
        Ok,
        Warn,
        Fail
    }

    public sealed class HealthResult
    {
        public string Name { get; set; }
        public HealthStatus Status { get; private set; }
        public string Detail { get; private set; }

        private HealthResult(HealthStatus status, string detail)
        {
            Status = status;
            Detail = detail ?? String.Empty;
        }

        public static HealthResult Ok(string detail)
        {
            return new HealthResult(HealthStatus.Ok, detail);
        }

        public static HealthResult Warn(string detail)
        {
            return new HealthResult(HealthStatus.Warn, detail);
        }

        public static HealthResult Fail(string detail)
        {
            return new HealthResult(HealthStatus.Fail, detail);
        }

        public override string ToString()
        {
            return String.Concat("[", Status.ToString().ToUpperInvariant(), "] ", Name, " - ", Detail);
        }
    }

    /// <summary>
    /// Registry of shard health checks. Custom systems register here rather than growing the
    /// [CoreSmoke command itself, so [CoreSmoke stays a one-command health check for the whole
    /// shard as more systems are ported. The admin API's /api/status will call RunAll() too.
    ///
    /// Register from Initialize(), or from a constructor for per-instance checks.
    /// </summary>
    public static class HealthCheck
    {
        private static readonly object _sync = new object();

        private static readonly List<string> _order = new List<string>();
        private static readonly Dictionary<string, Func<HealthResult>> _checks =
            new Dictionary<string, Func<HealthResult>>(StringComparer.OrdinalIgnoreCase);

        public static int Count
        {
            get
            {
                lock (_sync)
                {
                    return _checks.Count;
                }
            }
        }

        /// <summary>
        /// Registers a check under a unique name. Re-registering a name replaces the previous
        /// check but keeps its position, so a reload does not shuffle the report.
        /// </summary>
        public static void Register(string name, Func<HealthResult> check)
        {
            if (String.IsNullOrWhiteSpace(name) || check == null)
            {
                return;
            }

            lock (_sync)
            {
                if (!_checks.ContainsKey(name))
                {
                    _order.Add(name);
                }

                _checks[name] = check;
            }
        }

        public static void Unregister(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return;
            }

            lock (_sync)
            {
                if (_checks.Remove(name))
                {
                    _order.Remove(name);
                }
            }
        }

        /// <summary>
        /// Runs every registered check in registration order. Each is individually guarded, so
        /// one badly-behaved check reports as a failure instead of breaking the whole report.
        /// </summary>
        public static List<HealthResult> RunAll()
        {
            List<string> names;
            Dictionary<string, Func<HealthResult>> checks;

            lock (_sync)
            {
                names = new List<string>(_order);
                checks = new Dictionary<string, Func<HealthResult>>(_checks, StringComparer.OrdinalIgnoreCase);
            }

            var results = new List<HealthResult>(names.Count);

            foreach (string name in names)
            {
                Func<HealthResult> check;

                if (!checks.TryGetValue(name, out check))
                {
                    continue;
                }

                HealthResult result;

                try
                {
                    result = check() ?? HealthResult.Warn("Check returned null.");
                }
                catch (Exception ex)
                {
                    result = HealthResult.Fail("Check threw " + ex.GetType().Name + ": " + ex.Message);
                }

                result.Name = name;
                results.Add(result);
            }

            return results;
        }

        /// <summary>The worst status across all checks - the shard's overall health.</summary>
        public static HealthStatus Worst(IEnumerable<HealthResult> results)
        {
            var worst = HealthStatus.Ok;

            if (results == null)
            {
                return worst;
            }

            foreach (HealthResult result in results)
            {
                if (result != null && result.Status > worst)
                {
                    worst = result.Status;
                }
            }

            return worst;
        }
    }
}
