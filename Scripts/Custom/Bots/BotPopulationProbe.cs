// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPopulationProbe.cs — Bots.Recipe: does the world match the recipe?
//
// Every other bot probe SPAWNS what it measures: the walk probe makes five
// travellers, the work probe makes a smith and two miners, and each deletes its
// own afterwards. This one measures nothing of its own, because the thing under
// test is the population itself - it is already standing there, and a probe that
// spawned its own copy of it would be testing a copy.
//
// So it is a plain health check with no cleanup and no window, which also means
// it runs on every [CoreSmoke rather than only inside the six-minute chain.
//
// It answers four questions, in the order they are worth asking:
//
//   1. Does GG_BotPop.xml still say what the recipe says?  A recipe is DERIVED
//      and a file is not, so authoring a destination in the editor changes the
//      recipe's mind while the file - and the world - goes on saying the old
//      thing. Nothing else would ever notice.
//   2. Is every staffed bench actually staffed?  This is 7f's premise, and the
//      predicate is BotDestinations.IsStaffed's own so the probe cannot pass
//      while the economy's haul roll disagrees.
//   3. Does the NAMED cast cover every fixed post, and is the world rid of the
//      fixed spawners that used to staff them?  Since 22 September 2026 a bank
//      crowd or a staffed bench is a roster entry, not a spawner (BotRoster,
//      NamedBots), so a post the roster does not cover stays empty and nothing
//      else would say so; and a fixed spawner still in the world from before
//      would put a throwaway on a named bot's post.
//   4. Are the towns within tolerance of their share, and what does a tick cost
//      at this population?
//
// Upstream has no equivalent to any of it.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Mobiles;

namespace Server.Custom
{
    public static class BotPopulationProbe
    {
        /// <summary>
        /// How far a town's live count may sit from its share before it is worth saying.
        ///
        /// Wide, and deliberately: the number is the product of a curve that moves hourly, a
        /// session length rolled between one and four hours, and spawners that refill on a five to
        /// fifteen minute timer. A tight tolerance here would fail for a living population doing
        /// exactly what it should, which is the fastest way to teach somebody to ignore a check.
        /// The DETERMINISTIC half - the spawner census - is asserted exactly, just below.
        /// </summary>
        private const double Tolerance = 0.25;

        /// <summary>
        /// A population needs a moment after a boot: the spawners fill at ServerStarted, and a
        /// crafter then has to walk to its bench and clock in before IsAtStation is true.
        /// Asserting during that is asserting that walking is instant.
        /// </summary>
        private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(150.0);

        private static long _filledAt;
        private static bool _filled;

        /// <summary>Called by the startup fill, so the probe knows how long the world has had.</summary>
        public static void NoteFilled()
        {
            _filledAt = Core.TickCount;
            _filled = true;
        }

        public static HealthResult BuildHealthResult()
        {
            Map map = BotPopulation.Facet;
            BotRecipe recipe = BotPopulation.Build(map);

            if (recipe.Slots.Count == 0)
            {
                var reason = new StringBuilder("the recipe produces nothing");

                foreach (string note in recipe.Notes)
                {
                    reason.Append(". ").Append(note);
                }

                return HealthResult.Warn(reason.ToString());
            }

            var detail = new StringBuilder(320);

            detail.AppendFormat(
                "recipe: {0} spawner(s) for {1} throwaway bot(s), {2} named post bot(s) from the roster",
                recipe.Spawners.Count,
                recipe.ThrowawayBots,
                recipe.FixedBots);

            detail.Append(". ").Append(BotPopulation.DescribeNamed());

            // Which stop each town's roamers resolved at. Reported unconditionally, because the
            // point of the anchor being a rule over tags rather than a table of town names is that
            // authoring a plaza changes the answer - and a change nobody can see is a change
            // nobody will trust.
            var anchors = new List<string>();

            foreach (var entry in recipe.RoamAnchors)
            {
                anchors.Add(entry.Key + " -> " + entry.Value);
            }

            anchors.Sort(StringComparer.Ordinal);

            if (anchors.Count > 0)
            {
                detail.Append(". anchors: ").Append(String.Join(", ", anchors.ToArray()));
            }

            // The tick cost, always. This number is the whole reason the target is 60 rather than
            // a guess, and it is only useful if it is in front of somebody.
            detail.Append(". ").Append(BotTickManager.DescribeCost());

            // ---- 1. the file ----

            List<string> differences = BotPopulation.DiffAgainstFile(map, recipe);

            if (differences.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "REGEN NEEDED: {0} difference(s) between the recipe and {1} ({2}{3}). "
                    + "Run [BotPopulationGen then [GG_Reimport. {4}",
                    differences.Count,
                    BotPopulation.GeneratedPath(map),
                    String.Join("; ", differences.GetRange(0, Math.Min(3, differences.Count)).ToArray()),
                    differences.Count > 3 ? "; ..." : "",
                    detail));
            }

            // ---- the roster covers every fixed post ----
            //
            // Before the live census, because it is a data fault like the file above and needs no
            // settling: a post nobody on the roster holds is empty at every hour, and adding a roster
            // entry is the fix.

            List<string> gaps = BotPopulation.RosterGaps(recipe);

            if (gaps.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "ROSTER GAP: {0} post(s) and {1} disagree ({2}{3}). Add or move a roster entry. {4}",
                    gaps.Count,
                    BotRoster.ConfigPath,
                    String.Join("; ", gaps.GetRange(0, Math.Min(3, gaps.Count)).ToArray()),
                    gaps.Count > 3 ? "; ..." : "",
                    detail));
            }

            // ---- the live census, gathered once ----

            var liveByTown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int fixtures = 0;
            int lifecycle = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }

                if (bot.LifecycleExempt)
                {
                    fixtures++;
                }
                else
                {
                    lifecycle++;
                }

                // By HOME, not by where it is standing. Where a bot stands is the walker's
                // business and changes minute to minute; where it is FROM is what the share
                // decided, and it is the thing the weights are a claim about.
                if (!String.IsNullOrEmpty(bot.HomeTown))
                {
                    int count;
                    liveByTown.TryGetValue(bot.HomeTown, out count);
                    liveByTown[bot.HomeTown] = count + 1;
                }
            }

            detail.AppendFormat(". live: {0} lifecycle, {1} fixed", lifecycle, fixtures);

            List<XmlSpawner> spawners = BotPopulation.LiveSpawners();

            detail.AppendFormat(", {0} spawner(s) in world", spawners.Count);

            // Nothing has been imported yet, or the world has not been filled. Not a fault: a
            // freshly generated file is a file nobody has run [GG_Reimport on.
            if (spawners.Count == 0)
            {
                return HealthResult.Warn(String.Format(
                    "no bot spawners in the world - run [GG_Reimport to load {0}. {1}",
                    BotPopulation.GeneratedPath(map),
                    detail));
            }

            if (!_filled || Core.TickCount - (_filledAt + (long)SettlingTime.TotalMilliseconds) < 0)
            {
                return HealthResult.Ok(String.Format(
                    "settling ({0:0}s after the startup fill before the recipe is asserted). {1}",
                    SettlingTime.TotalSeconds,
                    detail));
            }

            // ---- 2. every staffed bench is staffed ----

            var unstaffed = new List<string>();

            foreach (BotSlot slot in recipe.Slots)
            {
                if (!Insensitive.Equals(slot.Kind, BotPopulationConfig.RoleStation))
                {
                    continue;
                }

                if (!Staffed(slot.Station))
                {
                    unstaffed.Add(slot.Station + WhereItWent(slot));
                }
            }

            if (unstaffed.Count > 0)
            {
                unstaffed.Sort(StringComparer.Ordinal);

                return HealthResult.Warn(String.Format(
                    "{0} station(s) the recipe staffs have nobody clocked in: {1}. {2}",
                    unstaffed.Count,
                    String.Join(", ", unstaffed.ToArray()),
                    detail));
            }

            // ---- 3. no fixed spawner is left over from before the named cast ----
            //
            // This check used to ask whether a fixture spawner had quietly failed to place its
            // bots. There are no fixture spawners now - the posts are the roster's - so the one
            // spawner that can still be wrong is one the world save kept from before: it would put
            // a throwaway on a named bot's post. [GG_Reimport deletes every GG_ spawner and loads
            // the file, which no longer has any.

            var leftover = new List<string>();

            foreach (XmlSpawner spawner in spawners)
            {
                if (IsFixture(spawner))
                {
                    leftover.Add(spawner.Name);
                }
            }

            if (leftover.Count > 0)
            {
                leftover.Sort(StringComparer.Ordinal);

                return HealthResult.Warn(String.Format(
                    "{0} fixed spawner(s) from before the named cast are still in the world: {1}. "
                    + "Run [GG_Reimport to retire them. {2}",
                    leftover.Count,
                    String.Join(", ", leftover.ToArray()),
                    detail));
            }

            // ---- 4. the towns are within tolerance of their share ----

            var outside = new List<string>();
            int totalLive = lifecycle + fixtures;

            foreach (var entry in recipe.TownTargets)
            {
                int want = entry.Value;
                int have;
                liveByTown.TryGetValue(entry.Key, out have);

                detail.AppendFormat(". {0}: {1} of {2}", entry.Key, have, want);

                if (want <= 0)
                {
                    continue;
                }

                // Against the CURVE's target rather than the peak: at 05:00 the lifecycle half is
                // meant to be a fifth of what it is at 19:00, and measuring against the peak would
                // report every night as a fault.
                double expected = (want - FixedIn(recipe, entry.Key)) * BotSession.CurveNow
                    + FixedIn(recipe, entry.Key);

                if (expected < 1.0)
                {
                    continue;
                }

                double drift = Math.Abs(have - expected) / expected;

                if (drift > Tolerance)
                {
                    outside.Add(String.Format(
                        "{0} has {1} where the share and the curve want about {2:0}",
                        entry.Key,
                        have,
                        expected));
                }
            }

            if (outside.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} town(s) outside {1:P0} of their share: {2}. {3}",
                    outside.Count,
                    Tolerance,
                    String.Join("; ", outside.ToArray()),
                    detail));
            }

            return HealthResult.Ok(detail.ToString());
        }

        /// <summary>
        /// Where the crafter that should be here went instead, when it is findable.
        ///
        /// "Nobody clocked in" on its own is a puzzle; nine times in ten the answer is that the
        /// bot IS working, at the other bench of its trade in the same town, because
        /// CrafterBehavior.TakeUpStation moves to one when reach at the authored station holds no
        /// standable tile at all. That is a documented rule and not a fault - but it means the
        /// FAULT is in the arrival point, not in the bot, and the message should say so rather
        /// than leaving somebody to work it out from the live map.
        ///
        /// Found on this check's first real run: trinsic-shop-tailor-2's tailor was working at
        /// trinsic-shop-tailor instead, because its arrival was authored inside a display case.
        ///
        /// The diagnosis written here originally - "the shop is at Z 35 and the arrival is twenty
        /// below the floor" - was backwards, and is corrected in the README. The floor is Z 15, the
        /// arrival's Z was right and its X,Y was wrong, and the destination's Z 35 pointed at the
        /// upper of two standable storeys on its tile. Worth keeping as a caution about this
        /// message: it can say WHERE the crafter went, which is a fact, and it cannot say why,
        /// which is a guess somebody will believe.
        /// </summary>
        private static string WhereItWent(BotSlot slot)
        {
            if (slot.Class == null)
            {
                return String.Empty;
            }

            var elsewhere = new List<string>();

            foreach (CrafterBehavior crafter in CrafterBehavior.Live())
            {
                if (!crafter.IsAtStation || String.IsNullOrEmpty(crafter.DestinationId)
                    || Insensitive.Equals(crafter.DestinationId, slot.Station))
                {
                    continue;
                }

                NavDestination where = Nav.Destination(crafter.DestinationId);

                // Same trade, same town: that is the relocation rule's own scope, so anything
                // outside it is a different bot doing a different job and says nothing here.
                if (where != null && where.HasTag(slot.Town)
                    && Insensitive.Equals(where.Type, Nav.Destination(slot.Station) == null
                        ? where.Type
                        : Nav.Destination(slot.Station).Type)
                    && !elsewhere.Contains(crafter.DestinationId))
                {
                    elsewhere.Add(crafter.DestinationId);
                }
            }

            return elsewhere.Count == 0
                ? " (nobody of that trade is working in the town at all)"
                : " (its crafter is at " + String.Join(" / ", elsewhere.ToArray())
                    + " instead - check the arrival points, a station with no standable tile in "
                    + "reach hands its crafter to the next one of its kind)";
        }

        /// <summary>
        /// Somebody is clocked in at this station.
        ///
        /// BotDestinations.IsStaffed's own predicate, deliberately: the economy's haul roll asks
        /// exactly this question of exactly this data, so a probe that asked a different one could
        /// pass while a laden miner walked past the forge it says is staffed.
        /// </summary>
        private static bool Staffed(string destinationId)
        {
            if (String.IsNullOrEmpty(destinationId))
            {
                return true;
            }

            foreach (CrafterBehavior crafter in CrafterBehavior.Live())
            {
                if (crafter.IsAtStation && Insensitive.Equals(crafter.DestinationId, destinationId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A fixed-role spawner, read off the spawn string rather than off a second record of it.
        /// Only a leftover can match now: the generator writes none (BotPopulation.Write).
        ///
        /// The Objects2 entry is what the shard actually spawns from, so asking it is asking the
        /// thing that decides. A parallel list of which names are fixtures would be one more thing
        /// to keep in step with the generator.
        /// </summary>
        private static bool IsFixture(XmlSpawner spawner)
        {
            XmlSpawner.SpawnObject[] entries = spawner.SpawnObjects;

            if (entries == null)
            {
                return false;
            }

            foreach (XmlSpawner.SpawnObject entry in entries)
            {
                if (entry != null && entry.TypeName != null
                    && entry.TypeName.IndexOf("/Role/Fixed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static int FixedIn(BotRecipe recipe, string town)
        {
            int total = 0;

            foreach (BotSlot slot in recipe.Slots)
            {
                if (slot.Role == BotRole.Fixed && Insensitive.Equals(slot.Town, town))
                {
                    total += slot.Count;
                }
            }

            return total;
        }
    }
}
