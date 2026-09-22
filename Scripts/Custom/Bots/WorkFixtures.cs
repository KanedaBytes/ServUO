// -----------------------------------------------------------------------------
// WorkFixtures.cs - what Bots.Work and the destination weights must say, proved from [CoreSmoke.
//
// Run from CoreSmoke.Finish, after the haul fixtures, in the same shape: one Expect per claim and
// a catch at the driver. Nothing here needs a mobile or the live graph: the verdict is a pure
// function of what the census found (BotSystem.WorkVerdict), so a fixture can hand it a shard that
// does not exist - three excluded forges AND a class with nowhere to work, which is the shard this
// one actually was for a week while health reported only the forges.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class WorkFixtures
    {
        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- work reporting fixtures --");

            bool passed = true;

            try
            {
                passed &= FixtureStationlessBesideExcluded(report);
                passed &= FixtureStationlessAlone(report);
                passed &= FixtureNothingWrong(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }

            return passed;
        }

        private static readonly IList<string> None = new string[0];

        /// <summary>
        /// The regression itself. An excluded site used to return before the stationless check was
        /// reached, so a class that could never work was reported only when no site was excluded -
        /// and from the Trammel adopt on, three forges always were.
        /// </summary>
        private static bool FixtureStationlessBesideExcluded(List<string> report)
        {
            HealthResult result = BotSystem.WorkVerdict(
                new[] { "fixture-forge (no arrival point stands within two tiles of both a forge and an anvil)" },
                new[] { "Lumberjack works 'lumber' and the graph has none" },
                None,
                None,
                "census text");

            bool warn = result.Status == HealthStatus.Warn;
            bool site = result.Detail.Contains("fixture-forge");
            bool cls = result.Detail.Contains("Lumberjack works 'lumber'");
            bool census = result.Detail.EndsWith("census text", StringComparison.Ordinal);

            return Expect(
                report,
                warn && site && cls && census,
                "a class with no station is named beside an excluded site, in one Warn line",
                String.Format(
                    "status {0}, names the site {1}, names the class {2}, keeps the census {3}: {4}",
                    result.Status,
                    site,
                    cls,
                    census,
                    result.Detail));
        }

        private static bool FixtureStationlessAlone(List<string> report)
        {
            HealthResult result = BotSystem.WorkVerdict(
                None,
                new[] { "Lumberjack works 'lumber' and the graph has none" },
                None,
                None,
                "census text");

            return Expect(
                report,
                result.Status == HealthStatus.Warn && result.Detail.Contains("1 class(es) have nowhere to work"),
                "a class with no station is a Warn on its own",
                String.Format("status {0}: {1}", result.Status, result.Detail));
        }

        private static bool FixtureNothingWrong(List<string> report)
        {
            HealthResult result = BotSystem.WorkVerdict(None, None, None, None, "census text");

            return Expect(
                report,
                result.Status == HealthStatus.Ok && result.Detail == "census text",
                "nothing to report is Ok, and the census text is all it says",
                String.Format("status {0}: {1}", result.Status, result.Detail));
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
