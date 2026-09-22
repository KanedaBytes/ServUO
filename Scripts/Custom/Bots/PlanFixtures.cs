// -----------------------------------------------------------------------------
// PlanFixtures.cs - the plan budget is served round-robin, proved from [CoreSmoke.
//
// Run from CoreSmoke.Finish beside WorkFixtures, in the same shape: one Expect per claim and a catch
// at the driver. BotPlanRota is pure, so the fixtures drive it with strings standing for bots, in
// the order LiveRegistry would hold them - creation order, newest last - and ask every bot to plan
// on every pass it is ticked, which is the worst case the live shard reaches at 95% refused.
//
// The claim is Sean's acceptance line for the change: with a budget smaller than the bot count,
// every bot receives a grant within N passes, and creation order does not decide who starves. The
// last fixture runs the OLD order - every pass from the head - through the same harness and expects
// it to starve the tail, so a harness that could not tell the two apart would fail here.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class PlanFixtures
    {
        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- plan budget fixtures --");

            bool passed = true;

            try
            {
                passed &= FixtureEveryBotWithinPasses(report);
                passed &= FixtureSparseAskers(report);
                passed &= FixtureChurn(report);
                passed &= FixtureOldOrderStarvesTheTail(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }

            return passed;
        }

        /// <summary>
        /// Forty bots, eight plans a pass, every bot asking whenever it is ticked and not yet
        /// served: all forty are granted within ceil(40 / 8) = 5 passes, the eight newest among
        /// them, no wait is longer than that, and no pass grants more or less than its budget.
        /// </summary>
        private static bool FixtureEveryBotWithinPasses(List<string> report)
        {
            List<string> bots = Bots(40, "bot");
            var rota = new BotPlanRota<string>();

            Dictionary<string, int> servedAt;
            bool exact;
            Run(rota, bots, 8, 5, false, null, out servedAt, out exact);

            int newest = 0;

            for (int i = 32; i < 40; i++)
            {
                if (servedAt.ContainsKey(bots[i]))
                {
                    newest++;
                }
            }

            return Expect(
                report,
                servedAt.Count == 40 && newest == 8 && rota.LongestWait <= 5 && exact,
                "40 bots at 8 plans a pass: every bot granted within 5 passes, the 8 newest included, longest wait "
                    + rota.LongestWait + " pass(es)",
                String.Format(
                    "{0} of 40 served in 5 passes, {1} of the 8 newest, longest wait {2}, every pass granted its budget {3}",
                    servedAt.Count,
                    newest,
                    rota.LongestWait,
                    exact));
        }

        /// <summary>
        /// Only every third bot wants a plan (the rest are lingering, working, walking), and there
        /// are still more askers than plans: 60 bots, 20 askers, 4 plans a pass - every asker is
        /// served within ceil(20 / 4) = 5 passes, and nobody who did not ask is charged a plan.
        /// </summary>
        private static bool FixtureSparseAskers(List<string> report)
        {
            List<string> bots = Bots(60, "bot");
            var askers = new HashSet<string>();

            for (int i = 0; i < bots.Count; i += 3)
            {
                askers.Add(bots[i]);
            }

            var rota = new BotPlanRota<string>();

            Dictionary<string, int> servedAt;
            bool exact;
            Run(rota, bots, 4, 5, false, askers, out servedAt, out exact);

            int askersServed = 0;

            foreach (string asker in askers)
            {
                if (servedAt.ContainsKey(asker))
                {
                    askersServed++;
                }
            }

            return Expect(
                report,
                askersServed == askers.Count && servedAt.Count == askers.Count && exact,
                "20 askers among 60 bots at 4 plans a pass: every asker granted within 5 passes, no plan to a bot that did not ask",
                String.Format(
                    "{0} of {1} askers served, {2} grants in all, every pass granted its budget {3}",
                    askersServed,
                    askers.Count,
                    servedAt.Count,
                    exact));
        }

        /// <summary>
        /// The live registry is not still: bots log out and new ones are appended. Thirty bots at
        /// six a pass; after the second pass the bot the next pass was to start at logs out, along
        /// with two others, and six new bots are appended. Every bot present at the end, old or new,
        /// is still served within ceil(33 / 6) + 1 = 7 passes of the change.
        /// </summary>
        private static bool FixtureChurn(List<string> report)
        {
            List<string> bots = Bots(30, "bot");
            var rota = new BotPlanRota<string>();

            Dictionary<string, int> servedAt;
            bool exact;
            Run(rota, bots, 6, 2, false, null, out servedAt, out exact);

            // Passes 1 and 2 granted bot00-bot11; pass 3 would start at bot12.
            bots.Remove("bot12");
            bots.Remove("bot03");
            bots.Remove("bot20");
            bots.AddRange(Bots(6, "new"));

            Dictionary<string, int> after;
            bool exactAfter;
            Run(rota, bots, 6, 7, false, null, out after, out exactAfter);

            int missing = 0;
            string first = null;

            for (int i = 0; i < bots.Count; i++)
            {
                if (!after.ContainsKey(bots[i]))
                {
                    missing++;
                    first = first ?? bots[i];
                }
            }

            return Expect(
                report,
                missing == 0 && exact && exactAfter,
                "churn (the next bot in line and two others log out, six are appended): all 33 served within 7 passes",
                String.Format(
                    "{0} of {1} never served after the churn (first {2}), budgets exact {3}/{4}",
                    missing,
                    bots.Count,
                    first ?? "-",
                    exact,
                    exactAfter));
        }

        /// <summary>
        /// The teeth: the order this replaced. Every pass from the head of the list, the same forty
        /// bots and eight plans, twenty passes - four times what the rota needs - and the served
        /// bots re-asking as soon as they are ticked again, which is what a Traveler does after a
        /// short visit. The head is served over and over and the tail never is.
        /// </summary>
        private static bool FixtureOldOrderStarvesTheTail(List<string> report)
        {
            List<string> bots = Bots(40, "bot");
            var rota = new BotPlanRota<string>();

            Dictionary<string, int> servedAt;
            bool exact;
            Run(rota, bots, 8, 20, true, null, out servedAt, out exact);

            int tail = 0;

            for (int i = 32; i < 40; i++)
            {
                if (servedAt.ContainsKey(bots[i]))
                {
                    tail++;
                }
            }

            return Expect(
                report,
                tail == 0 && servedAt.Count == 8,
                "control: first come, first served from the head (the old order) never serves the 8 newest in 20 passes",
                String.Format(
                    "the old order served {0} of the 8 newest and {1} bots in all - the harness cannot tell a starving order from a fair one",
                    tail,
                    servedAt.Count));
        }

        /// <summary>
        /// Drive <paramref name="passes"/> passes the way BotTickManager.OnTick does: open the pass,
        /// tick from the start index round the list, and let each bot that asks take or be refused
        /// a plan. A bot asks while it has not been served (or, with <paramref name="headEveryPass"/>,
        /// every pass - the old order, where a served bot came back asking), and only if it is in
        /// <paramref name="askers"/> when that is given. <paramref name="headEveryPass"/> ignores the
        /// rota's start and begins at 0, which is exactly first come, first served.
        /// </summary>
        private static void Run(
            BotPlanRota<string> rota, List<string> bots, int budget, int passes, bool headEveryPass,
            HashSet<string> askers, out Dictionary<string, int> servedAt, out bool exact)
        {
            servedAt = new Dictionary<string, int>();
            exact = true;

            for (int pass = 1; pass <= passes; pass++)
            {
                int start = rota.BeginPass(bots, budget);

                if (headEveryPass)
                {
                    start = 0;
                }

                int wanting = 0;
                int granted = 0;

                for (int step = 0; step < bots.Count; step++)
                {
                    int i = (start + step) % bots.Count;
                    string bot = bots[i];

                    if (askers != null && !askers.Contains(bot))
                    {
                        continue;
                    }

                    if (!headEveryPass && servedAt.ContainsKey(bot))
                    {
                        continue;
                    }

                    wanting++;

                    if (rota.TryTake(bot, i))
                    {
                        granted++;

                        if (!servedAt.ContainsKey(bot))
                        {
                            servedAt[bot] = pass;
                        }
                    }
                }

                rota.EndPass();

                if (granted != Math.Min(budget, wanting))
                {
                    exact = false;
                }
            }
        }

        private static List<string> Bots(int count, string prefix)
        {
            var bots = new List<string>(count);

            for (int i = 0; i < count; i++)
            {
                bots.Add(prefix + i.ToString("00"));
            }

            return bots;
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
