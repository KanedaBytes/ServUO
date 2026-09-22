// -----------------------------------------------------------------------------
// WorkFixtures.cs - what Bots.Work and the destination weights must say, proved from [CoreSmoke.
//
// Run from CoreSmoke.Finish, after the haul fixtures, in the same shape: one Expect per claim and
// a catch at the driver. Nothing here needs a mobile or the live graph: the verdict is a pure
// function of what the census found (BotSystem.WorkVerdict), so a fixture can hand it a shard that
// does not exist - three excluded forges AND a class with nowhere to work, which is the shard this
// one actually was for a week while health reported only the forges.
//
// The weight fixtures read the LOADED bots.json and hand WeightFor destinations made up on the
// spot, so they prove the single-minded gatherer weights are the ones upstream ships (uo-offline
// DestinationType.cs:338-352) and that no other class moved. A deliberate re-tune of those numbers
// is a reason to change this file too, which is the point of it.
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
                passed &= FixtureSingleMindedMiner(report);
                passed &= FixtureSingleMindedLumberjack(report);
                passed &= FixtureOthersUnchanged(report);
                passed &= FixtureCrowdFloorSkipsSingleMinded(report);
                passed &= FixtureStayBoostSkipsSingleMinded(report);
                passed &= FixtureOpenDoorwayIsNotFail(report);
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

        private static NavDestination Fake(string type, string tags)
        {
            return new NavDestination { Id = "fixture-" + type, Type = type, Tags = tags, MapName = "Trammel" };
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) < 1e-9;
        }

        /// <summary>
        /// Upstream's numbers, and the two things ours adds: tags are not consulted (a craft shop
        /// in the Miner's home town weighs `otherwise`, not otherwise times the craft tag), and a
        /// type excluded by default stays excluded (a forge is 0, not 0.02).
        /// </summary>
        private static bool FixtureSingleMindedMiner(List<string> report)
        {
            BotDestinationConfig config = BotSystem.Store.Destinations;

            double mine = config.WeightFor(Fake("mine", "wilderness britain"), BotClass.Miner);
            double bank = config.WeightFor(Fake("bank", "service britain"), BotClass.Miner);
            double tavern = config.WeightFor(Fake("tavern", "social food"), BotClass.Miner);
            double shop = config.WeightFor(Fake("shop", "craft blacksmith britain"), BotClass.Miner);
            double gate = config.WeightFor(Fake("gate", "town"), BotClass.Miner);
            double forge = config.WeightFor(Fake("forge", "craft town"), BotClass.Miner);
            double lumber = config.WeightFor(Fake("lumber", "wilderness"), BotClass.Miner);
            double guard = config.WeightFor(Fake("guard", "guarded"), BotClass.Miner);

            bool ok = Near(mine, 10.0) && Near(bank, 0.3) && Near(tavern, 0.2) && Near(shop, 0.02)
                && Near(gate, 0.02) && forge == 0.0 && lumber == 0.0 && guard == 0.0;

            return Expect(
                report,
                ok,
                "a Miner is single-minded: mine 10, bank 0.3, tavern 0.2, a craft shop 0.02, forge and lumber 0",
                String.Format(
                    "Miner weighs mine {0}, bank {1}, tavern {2}, craft shop {3}, gate {4}, forge {5}, lumber {6}, guard {7}",
                    mine, bank, tavern, shop, gate, forge, lumber, guard));
        }

        private static bool FixtureSingleMindedLumberjack(List<string> report)
        {
            BotDestinationConfig config = BotSystem.Store.Destinations;

            double lumber = config.WeightFor(Fake("lumber", "wilderness yew"), BotClass.Lumberjack);
            double inn = config.WeightFor(Fake("inn", "social"), BotClass.Lumberjack);
            double shop = config.WeightFor(Fake("shop", "craft carpenter"), BotClass.Lumberjack);
            double mine = config.WeightFor(Fake("mine", "wilderness"), BotClass.Lumberjack);

            return Expect(
                report,
                Near(lumber, 10.0) && Near(inn, 0.2) && Near(shop, 0.02) && mine == 0.0,
                "a Lumberjack is single-minded: lumber 10, inn 0.2, a craft shop 0.02, mine 0",
                String.Format(
                    "Lumberjack weighs lumber {0}, inn {1}, craft shop {2}, mine {3}",
                    lumber, inn, shop, mine));
        }

        /// <summary>
        /// No other class moved: every non-single-minded class weighs a spread of destinations
        /// exactly as the same byType and byTag tables do with no singleMinded block at all. The
        /// Fisherman is in that set on purpose - it waits for the dock session.
        /// </summary>
        private static bool FixtureOthersUnchanged(List<string> report)
        {
            BotDestinationConfig live = BotSystem.Store.Destinations;

            var plain = new BotDestinationConfig { ByType = live.ByType, ByTag = live.ByTag };

            NavDestination[] probes =
            {
                Fake("shop", "craft blacksmith britain"),
                Fake("bank", "service"),
                Fake("dock", "craft water"),
                Fake("forge", "craft town"),
                Fake("tavern", "social food"),
                Fake("gate", "town")
            };

            var moved = new List<string>();

            foreach (BotClass cls in BotClassHelper.Rollable())
            {
                if (live.SingleMinded.ContainsKey(cls.ToString()))
                {
                    continue;
                }

                foreach (NavDestination probe in probes)
                {
                    double a = live.WeightFor(probe, cls);
                    double b = plain.WeightFor(probe, cls);

                    if (!Near(a, b))
                    {
                        moved.Add(String.Format("{0} at {1}: {2} vs {3}", cls, probe.Type, a, b));
                    }
                }
            }

            bool fisherman = !live.SingleMinded.ContainsKey(BotClass.Fisherman.ToString());

            return Expect(
                report,
                moved.Count == 0 && fisherman,
                "no class outside singleMinded weighs anything differently, and the Fisherman is not in it",
                String.Format(
                    "Fisherman single-minded {0}; moved: {1}",
                    !fisherman,
                    String.Join("; ", moved.ToArray())));
        }

        /// <summary>
        /// The crowd floor passes a single-minded class by: with the floor active (three short,
        /// the cap), a Miner's empty bank stays upstream's 0.3 rather than 1.2, while a class
        /// outside singleMinded still gets its fourfold pull.
        /// </summary>
        private static bool FixtureCrowdFloorSkipsSingleMinded(List<string> report)
        {
            BotDestinationConfig config = BotSystem.Store.Destinations;

            NavDestination bank = Fake("bank", "service");

            double minerBase = config.WeightFor(bank, BotClass.Miner);
            double miner = BotDestinations.CrowdFloor(minerBase, 3, false, config.IsSingleMinded(BotClass.Miner));

            double warriorBase = config.WeightFor(bank, BotClass.Warrior);
            double warrior = BotDestinations.CrowdFloor(warriorBase, 3, false, config.IsSingleMinded(BotClass.Warrior));

            bool classes = config.IsSingleMinded(BotClass.Miner)
                && config.IsSingleMinded(BotClass.Lumberjack)
                && !config.IsSingleMinded(BotClass.Warrior);

            return Expect(
                report,
                classes && Near(miner, 0.3) && warriorBase > 0.0 && Near(warrior, warriorBase * 4.0),
                "the crowd floor skips a single-minded class: a Miner's empty bank stays 0.3, a Warrior's is fourfold",
                String.Format(
                    "single-minded Miner {0}, Lumberjack {1}, Warrior {2}; empty bank Miner {3} -> {4}, Warrior {5} -> {6}",
                    config.IsSingleMinded(BotClass.Miner),
                    config.IsSingleMinded(BotClass.Lumberjack),
                    config.IsSingleMinded(BotClass.Warrior),
                    minerBase,
                    miner,
                    warriorBase,
                    warrior));
        }

        /// <summary>
        /// The stay boost passes a single-minded class by, as the crowd floor does: at a bank three
        /// short of its crowd a Miner keeps the bank's plain hand-over chance, a Warrior's rises
        /// halfway to certain, and at a bank with its crowd neither moves - so the fixture cannot
        /// pass by the boost having gone for everybody.
        /// </summary>
        private static bool FixtureStayBoostSkipsSingleMinded(List<string> report)
        {
            BotDestinationConfig config = BotSystem.Store.Destinations;

            double bank = BotLifecycle.Config_.HandoffChance("bank");

            double miner = BotDestinations.StayBoost(bank, 3, config.IsSingleMinded(BotClass.Miner));
            double warrior = BotDestinations.StayBoost(bank, 3, config.IsSingleMinded(BotClass.Warrior));
            double warriorFull = BotDestinations.StayBoost(bank, 0, config.IsSingleMinded(BotClass.Warrior));
            double minerFull = BotDestinations.StayBoost(bank, 0, config.IsSingleMinded(BotClass.Miner));

            return Expect(
                report,
                bank > 0.0 && bank < 1.0
                    && Near(miner, bank)
                    && Near(warrior, bank + (1.0 - bank) * 0.5)
                    && Near(warriorFull, bank)
                    && Near(minerFull, bank),
                String.Format(
                    "the stay boost skips a single-minded class: at an under-crowded bank a Miner stays {0:0.###}, a Warrior {1:0.###}",
                    miner,
                    warrior),
                String.Format(
                    "bank hand-over {0}; three short: Miner {1}, Warrior {2}; crowd met: Miner {3}, Warrior {4}",
                    bank,
                    miner,
                    warrior,
                    minerFull,
                    warriorFull));
        }

        /// <summary>
        /// Nav.Doors (BotDoorCheck.ClassifyBotRoute): a door standing open beside the route makes a
        /// route around the closed one unverifiable, never a Fail - the false alarm after every
        /// [BotSmoke once the inn corridor got busy. With every door shut, around and no route are
        /// still Fail, so the fixture cannot pass by never failing. Here beside the work fixtures
        /// because the busy corridor was the gatherers' doing, and because this file is already
        /// on [CoreSmoke.
        /// </summary>
        private static bool FixtureOpenDoorwayIsNotFail(List<string> report)
        {
            BotDoorCheck.DoorPlan openAround = BotDoorCheck.ClassifyBotRoute(true, false, true);
            BotDoorCheck.DoorPlan openNoRoute = BotDoorCheck.ClassifyBotRoute(false, false, true);
            BotDoorCheck.DoorPlan shutAround = BotDoorCheck.ClassifyBotRoute(true, false, false);
            BotDoorCheck.DoorPlan shutNoRoute = BotDoorCheck.ClassifyBotRoute(false, false, false);
            BotDoorCheck.DoorPlan through = BotDoorCheck.ClassifyBotRoute(true, true, false);
            BotDoorCheck.DoorPlan openThrough = BotDoorCheck.ClassifyBotRoute(true, true, true);

            bool ok = BotDoorCheck.StatusFor(openAround) != HealthStatus.Fail
                && BotDoorCheck.StatusFor(openNoRoute) != HealthStatus.Fail
                && BotDoorCheck.StatusFor(shutAround) == HealthStatus.Fail
                && BotDoorCheck.StatusFor(shutNoRoute) == HealthStatus.Fail
                && through == BotDoorCheck.DoorPlan.Through
                && openThrough == BotDoorCheck.DoorPlan.Through;

            return Expect(
                report,
                ok,
                "Nav.Doors: an open doorway door is not a Fail; every door shut and routed around still is",
                String.Format(
                    "open+around {0} ({1}), open+no route {2} ({3}), shut+around {4} ({5}), shut+no route {6} ({7}), through {8}, open+through {9}",
                    openAround, BotDoorCheck.StatusFor(openAround),
                    openNoRoute, BotDoorCheck.StatusFor(openNoRoute),
                    shutAround, BotDoorCheck.StatusFor(shutAround),
                    shutNoRoute, BotDoorCheck.StatusFor(shutNoRoute),
                    through,
                    openThrough));
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
