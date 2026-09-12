// NavActorCheck.cs — does the locomotion adapter agree with the engine, and is it still the only
// way into the mobile?
//
// Two questions, one health check, reported as Nav.Actor on [CoreSmoke and in health.json.
//
// THE CONTRACT. NavWalker used to drive BaseAI directly; it now drives an INavActor. An adapter
// that quietly disagreed with the engine about whether a step is possible would not fail a walk
// audit - the audit would simply report a road as blocked, or not, and the number would look like
// a finding about the map. So the adapter is asked about a tile whose answer is not a matter of
// opinion: the sandstone wall Nav.Movement already uses, which is a map static rather than a
// decoration and can only change if the client data does. The adapter's verdict is COMPARED with
// the engine's rather than each being asserted on its own, because the thing worth knowing is that
// the two cannot come apart.
//
// THE COUPLING GUARD. The extraction found exactly eleven places where NavWalker needed to know
// what class it was walking. Nothing stops a twelfth being added later by somebody reaching for
// AIObject again, and it would compile, pass every audit, and silently re-couple the walker to
// BaseCreature - undoing the session. So the guard reads the sources under Navigation/ and pins an
// inventory. It is an unusual thing for a health check to do; it is here because there is no C#
// test harness in this tree, and a rule nothing enforces is a comment.
//
// WHAT IT DOES NOT DO. It does not walk a route, and it does not assert anything about pace. The
// walk audit does the first across 2,510 walks and [BotPace the second; this is the one-line
// question underneath both of them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Server.Mobiles;

namespace Server.Custom
{
    public static class NavActorCheck
    {
        public static void Initialize()
        {
            HealthCheck.Register("Nav.Actor", BuildHealthResult);
        }

        // ---- the contract ----

        /// <summary>
        /// Does a step through the adapter match what the engine says about the same step?
        ///
        /// Three assertions, in order:
        ///
        ///   1. A step onto the known wall is REFUSED through the adapter.
        ///   2. That verdict EQUALS what Movement.CheckMovement says about the identical step.
        ///   3. After a step the engine does allow, the mobile the adapter hands back is the one
        ///      that actually moved in the world.
        ///
        /// The third needs its shape explained, because the obvious form of it is vacuous. Under
        /// this interface the adapter's position and map ARE the mobile's - INavActor.Mobile hands
        /// back the same object, which is what keeps the interface at eleven members instead of
        /// thirty - so `actor.Mobile.Location == probe.Location` compares a field with itself and
        /// would pass against any implementation at all, including a broken one. So the mobile is
        /// re-resolved out of World by serial, independently of the adapter, and the adapter's
        /// answer is checked against THAT. What that catches is an adapter bound to a mobile other
        /// than the one it moves - which is the failure the PlayerMobile implementation can
        /// actually have, and the reason this assertion is worth writing down at all.
        ///
        /// The probes are NavWalkAudit's own, not look-alikes, for the reason that file records:
        /// a stand-in declared here would be a second instrument with its own collision rules.
        /// They are blessed, hidden, never wander, and must NOT be frozen - unlike Nav.Movement's
        /// rat, which only ever asks CheckMovement, these really step, and Mobile.Move refuses a
        /// frozen mobile (Mobile.cs:3130).
        ///
        /// RUN ONCE PER PROBE CLASS, from 12 September 2026, and that is the whole reason this
        /// check was worth revisiting. There are two adapters - NavCreatureActor over BaseAI and
        /// NavPlayerActor over a PathFollower - and until the audit grew a second probe this
        /// check only ever spawned a BaseCreature, so NavPlayerActor, which is what the entire
        /// bot fleet walks on, had no contract test at all. It went in on the class swap and
        /// passed this check unedited, because the check could not see it.
        ///
        /// The classes come from NavWalkAudit's registry rather than from a list here, so Core
        /// still names no bot class - the rule NavActorCheck's own type-test ledger exists to
        /// enforce.
        /// </summary>
        public static bool StepsAgreeWithEngine(out string detail)
        {
            var details = new List<string>();

            foreach (NavWalkAudit.ProbeClass cls in NavWalkAudit.ProbeClasses)
            {
                string one;

                if (!StepsAgreeForClass(cls, out one))
                {
                    detail = cls.Label + ": " + one;
                    return false;
                }

                details.Add(cls.Label + ": " + one);
            }

            if (details.Count == 0)
            {
                detail = "no probe classes are registered, so no adapter was exercised";
                return false;
            }

            detail = String.Join(" | ", details.ToArray());
            return true;
        }

        /// <summary>
        /// The three assertions above, for one probe class and therefore for one adapter.
        /// </summary>
        private static bool StepsAgreeForClass(NavWalkAudit.ProbeClass cls, out string detail)
        {
            Map map = Map.Trammel;

            Point3D from = NavMovement.SolidFrom;
            Point3D wall = NavMovement.SolidTile;

            NavWalkAudit.IWalkAuditProbe probe = cls.Create();

            try
            {
                probe.Mobile.MoveToWorld(from, map);

                INavActor actor = NavActor.For(probe.Mobile);

                if (actor == null)
                {
                    detail = "NavActor.For refused the walk probe, so nothing on this shard can be walked";
                    return false;
                }

                Direction into = Utility.GetDirection(from, wall);

                // Mobile.Move steps only when the mobile ALREADY faces d, and returns true for the
                // turn otherwise (Mobile.cs:3120). Facing it first is what makes the next call a
                // step rather than a turn, and it is the same thing the Sidestep rung relies on.
                probe.Mobile.Direction = into;

                int newZ;
                bool engineAllows =
                    Movement.Movement.CheckMovement(probe.Mobile, map, from, into, out newZ);

                Point3D before = probe.Mobile.Location;
                bool adapterStepped = actor.Step(into) && probe.Mobile.Location != before;

                if (adapterStepped != engineAllows)
                {
                    detail = String.Format(
                        "the adapter and the engine DISAGREE about a step from {0},{1} onto the "
                        + "sandstone wall at {2},{3}: the adapter {4}, the engine {5}",
                        from.X, from.Y, wall.X, wall.Y,
                        adapterStepped ? "took it" : "was refused",
                        engineAllows ? "allows it" : "refuses it");
                    return false;
                }

                if (adapterStepped)
                {
                    detail = String.Format(
                        "a step from {0},{1} onto the sandstone wall at {2},{3} was ALLOWED - the "
                        + "engine agrees, which means the wall is gone, not that the adapter is wrong",
                        from.X, from.Y, wall.X, wall.Y);
                    return false;
                }

                // Now a step the engine does allow, so the agreement is proved in both directions
                // rather than only on a refusal - a Step that always returned false would pass
                // everything above.
                string moved;

                if (!TryStepSomewhere(actor, probe.Mobile, map, out moved))
                {
                    detail = String.Format(
                        "a step onto the sandstone wall at {0},{1} is refused by both the adapter "
                        + "and the engine, but {2}",
                        wall.X, wall.Y, moved);
                    return false;
                }

                detail = String.Format(
                    "a step from {0},{1} onto the sandstone wall at {2},{3} is refused by the "
                    + "adapter and by the engine alike, and {4}",
                    from.X, from.Y, wall.X, wall.Y, moved);

                return true;
            }
            catch (Exception ex)
            {
                detail = "the adapter probe threw: " + ex.Message;
                return false;
            }
            finally
            {
                probe.Mobile.Delete();
            }
        }

        /// <summary>
        /// Step in the first direction the engine will take, then check that the adapter is still
        /// describing the mobile that moved.
        ///
        /// The direction is DERIVED rather than named: a hard-coded "west is the shop floor" would
        /// be a second claim about Trinsic's geometry that nothing had checked, and the one claim
        /// this file is entitled to make is the wall. If the engine will take no direction at all
        /// the probe is walled in, which is a finding about the map rather than about the adapter -
        /// so it returns false with a reason and the caller warns rather than fails.
        /// </summary>
        private static bool TryStepSomewhere(INavActor actor, Mobile probe, Map map, out string detail)
        {
            Direction[] all =
            {
                Direction.North, Direction.East, Direction.South, Direction.West,
                Direction.Up, Direction.Down, Direction.Left, Direction.Right,
            };

            for (int i = 0; i < all.Length; i++)
            {
                Point3D from = probe.Location;

                int newZ;

                if (!Movement.Movement.CheckMovement(probe, map, from, all[i], out newZ))
                {
                    continue;
                }

                probe.Direction = all[i];

                if (!actor.Step(all[i]) || probe.Location == from)
                {
                    detail = String.Format(
                        "the engine allows a step {0} from {1},{2} and the adapter would not take it",
                        all[i], from.X, from.Y);
                    return false;
                }

                // Independently of the adapter. See StepsAgreeWithEngine for why the obvious
                // comparison would be a field against itself.
                Mobile world = World.FindMobile(probe.Serial);

                if (world == null)
                {
                    detail = "the probe is not in World after a successful step";
                    return false;
                }

                if (actor.Mobile.Location != world.Location || actor.Mobile.Map != world.Map)
                {
                    detail = String.Format(
                        "after a step {0} the adapter reports {1},{2},{3} on {4} while the world "
                        + "has the same mobile at {5},{6},{7} on {8}",
                        all[i],
                        actor.Mobile.Location.X, actor.Mobile.Location.Y, actor.Mobile.Location.Z,
                        actor.Mobile.Map,
                        world.Location.X, world.Location.Y, world.Location.Z, world.Map);
                    return false;
                }

                detail = String.Format(
                    "a step {0} onto {1},{2},{3} leaves the adapter and the world describing the "
                    + "same mobile in the same place",
                    all[i], world.Location.X, world.Location.Y, world.Location.Z);

                return true;
            }

            detail = String.Format(
                "the engine will not take a step in ANY direction from {0},{1} - the probe is "
                + "walled in, so the successful-step half could not be measured",
                probe.X, probe.Y);

            return false;
        }

        // ---- the coupling guard ----

        private const string SourceDirectory = "Scripts/Custom/Core/Navigation";

        /// <summary>
        /// How many diagnostic BaseCreature tests are allowed to remain, and where.
        ///
        /// NOT ZERO, and pretending otherwise would mean fudging the rule around four legitimate
        /// sites. Two of them are class declarations - NavCorridor's CorridorProbe and
        /// NavWalkAudit's WalkAuditProbe - which are instruments that have to BE creatures; those
        /// are excluded by shape rather than counted. The other two are type tests in the
        /// diagnostic vocabulary: NavWalker.DescribeMobiles says "player" for anything that is not
        /// a BaseCreature, and NavWalkFailures.Describe picks its animal/monster branch the same
        /// way. Both are wrong the day bots are PlayerMobiles - they would label every bot a
        /// player - and CLASS-DECISION.md schedules the inversion for the class swap, where every
        /// test must ask `is PlayerBot` first.
        ///
        /// So this is a LEDGER, not a ban. It is what stops the two becoming three between now and
        /// then, which a blanket rule nobody could satisfy would not.
        ///
        /// AND IT WENT TO THREE ON PURPOSE, 12 September 2026, with the collision vocabulary.
        /// NavWalkFailures.cs holds two now rather than one. The second is IsUncontrolledCreature,
        /// and the reason is that MayBotPass had to stop paraphrasing the engine and start
        /// mirroring it: the condition both PlayerMobile.OnMoveOver (:3488) and
        /// BaseCreature.OnMoveOver (:4564) actually test is whether the mover is an uncontrolled
        /// BaseCreature, and the previous version expressed it as "the occupant is a PlayerMobile
        /// and the mover is not a bot" - which is a different claim, and was wrong for a stock
        /// creature mover onto a bot. A mirror of a type test is a type test.
        ///
        /// AND NavWalker.cs LEFT THE LEDGER ENTIRELY, the same day and for the opposite reason.
        /// Its one test was DescribeMobiles' `other.Player && !(other is BaseCreature)` - a second,
        /// older copy of Describe's own rule, correct only because an IBotActor branch above it got
        /// there first. DescribeMobiles now calls Describe, so there is one vocabulary instead of
        /// two and nothing in NavWalker.cs names BaseCreature at all. The check refuses a ledger
        /// entry with no matching test, by design, so the entry had to go with the test.
        ///
        /// This is the ledger doing the job it describes, in both directions: a number moves only
        /// with a reason attached, and an entry whose test is gone is deleted rather than left to
        /// sit there claiming a coupling that no longer exists.
        /// </summary>
        private static readonly Dictionary<string, int> AllowedTypeTests =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                // The factory's `mobile as BaseCreature`. Choosing the implementation is the whole
                // job; this is the seam, not a leak through it.
                { "NavActor.cs", 1 },
                // NavWalker.cs is deliberately ABSENT: DescribeMobiles used to carry its own copy
                // of Describe's rule and now calls Describe instead, so the file names BaseCreature
                // nowhere. See the note above.

                // TWO: Describe's animal-and-monster branch, and IsUncontrolledCreature - the
                // engine's own uncontrolled-mover condition, which MayBotPass mirrors rather than
                // paraphrases. See the note above for why the number moved.
                { "NavWalkFailures.cs", 2 },
            };

        /// <summary>
        /// The one file where knowing the class is the point.
        ///
        /// The rule this guard enforces is NOT "nothing under Navigation/ touches BaseAI" - the
        /// BaseCreature implementation has to, or it could not drive a creature. It is the
        /// stronger and more useful claim that the coupling lives in EXACTLY ONE FILE: AIObject
        /// appears in the adapter and nowhere else, so a twelfth touchpoint cannot be added
        /// anywhere that matters without this going red.
        ///
        /// Which is a rule this check learned the hard way - its first run failed on the adapter
        /// itself, which is the file it exists to protect.
        /// </summary>
        private const string AdapterFile = "NavActor.cs";

        /// <summary>
        /// Drives the mobile. Allowed in the adapter, which is what an adapter is for; anywhere
        /// else under Navigation/ it means the walker has been re-coupled to BaseCreature.
        /// </summary>
        private static readonly Regex AiObject = new Regex(@"\bAIObject\b", RegexOptions.Compiled);

        /// <summary>
        /// A BaseCreature cast or type test. `class X : BaseCreature` is deliberately not matched -
        /// see AllowedTypeTests.
        /// </summary>
        private static readonly Regex CreatureTest = new Regex(
            @"(\bas\s+BaseCreature\b|\bis\s+BaseCreature\b|\(\s*BaseCreature\s*\)|\bBaseCreature\s+\w+\s*=)",
            RegexOptions.Compiled);

        /// <summary>
        /// Is the walker still the only thing under Navigation/ that knows what it is walking?
        ///
        /// Reads source rather than reflecting, because the thing being guarded is a cast inside a
        /// method body and reflection cannot see one. Comments and string literals are stripped
        /// first, crudely but sufficiently: this file's own prose says "BaseCreature" a dozen
        /// times and so does NavAudit's, and a guard that counted those would be noise from the
        /// day it shipped.
        /// </summary>
        public static bool SeamIsIntact(out string detail, out bool sourcesFound)
        {
            sourcesFound = false;

            string directory = Path.Combine(Core.BaseDirectory, SourceDirectory);

            string[] files;

            try
            {
                if (!Directory.Exists(directory))
                {
                    detail = SourceDirectory + " is not on disk, so the seam could not be checked";
                    return true;
                }

                files = Directory.GetFiles(directory, "*.cs");
            }
            catch (Exception ex)
            {
                detail = "could not read " + SourceDirectory + ": " + ex.Message;
                return true;
            }

            if (files.Length == 0)
            {
                detail = "no sources under " + SourceDirectory + ", so the seam could not be checked";
                return true;
            }

            sourcesFound = true;

            var faults = new List<string>();
            var tests = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int adapterDrives = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(files[i]);
                }
                catch (Exception ex)
                {
                    faults.Add(name + " could not be read: " + ex.Message);
                    continue;
                }

                for (int n = 0; n < lines.Length; n++)
                {
                    string code = StripCommentsAndStrings(lines[n]);

                    if (code.Length == 0)
                    {
                        continue;
                    }

                    if (AiObject.IsMatch(code))
                    {
                        if (String.Equals(name, AdapterFile, StringComparison.OrdinalIgnoreCase))
                        {
                            adapterDrives++;
                        }
                        else
                        {
                            faults.Add(String.Format(
                                "{0}:{1} reaches for AIObject - only {2} may, and everything else "
                                + "goes through INavActor",
                                name, n + 1, AdapterFile));
                        }
                    }

                    // A class declaration is a probe that has to be a creature, not a coupling.
                    if (code.IndexOf(": BaseCreature", StringComparison.Ordinal) >= 0
                        || code.IndexOf(":BaseCreature", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }

                    if (CreatureTest.IsMatch(code))
                    {
                        int seen;
                        tests[name] = tests.TryGetValue(name, out seen) ? seen + 1 : 1;
                    }
                }
            }

            foreach (KeyValuePair<string, int> found in tests)
            {
                int allowed;

                if (!AllowedTypeTests.TryGetValue(found.Key, out allowed))
                {
                    faults.Add(String.Format(
                        "{0} has {1} BaseCreature type test(s) and is allowed none", found.Key, found.Value));
                }
                else if (found.Value != allowed)
                {
                    faults.Add(String.Format(
                        "{0} has {1} BaseCreature type test(s); the ledger says {2}",
                        found.Key, found.Value, allowed));
                }
            }

            foreach (KeyValuePair<string, int> expected in AllowedTypeTests)
            {
                if (!tests.ContainsKey(expected.Key))
                {
                    faults.Add(String.Format(
                        "{0} has no BaseCreature type test but the ledger expects {1} - if it was "
                        + "removed, remove it from the ledger too",
                        expected.Key, expected.Value));
                }
            }

            // At least one, not a pinned count: the adapter IS the BaseAI implementation, so zero
            // here would mean it had stopped driving one - which is a change worth hearing about
            // even though it is the opposite of the leak this guard was built for.
            if (adapterDrives == 0)
            {
                faults.Add(String.Format(
                    "{0} no longer touches AIObject at all - the BaseCreature actor has stopped "
                    + "driving BaseAI", AdapterFile));
            }

            if (faults.Count > 0)
            {
                detail = String.Join("; ", faults.ToArray());
                return false;
            }

            // The SUM, not the entry count. It read AllowedTypeTests.Count, which is how many
            // FILES are listed rather than how many tests they hold, and the two stopped being
            // equal on 12 September 2026 when NavWalkFailures.cs went to two.
            int allowedTests = 0;

            foreach (int count in AllowedTypeTests.Values)
            {
                allowedTests += count;
            }

            detail = String.Format(
                "{0} source(s) under {1}: AIObject appears in {2} and nowhere else ({3} site(s)), "
                + "and the BaseCreature type tests are the {4} the ledger allows across {5} file(s)"
                + " - the adapter factory's, plus NavWalkFailures' Describe and "
                + "IsUncontrolledCreature, which are the diagnostic vocabulary mirroring the "
                + "engine's own condition",
                files.Length,
                SourceDirectory,
                AdapterFile,
                adapterDrives,
                allowedTests,
                AllowedTypeTests.Count);

            return true;
        }

        /// <summary>
        /// The code on a line, with `//` comments, `/* */` fragments and string literals removed.
        ///
        /// Line-at-a-time and therefore imperfect: it cannot see that a line sits inside a block
        /// comment opened three lines above. That is deliberate rather than overlooked - the
        /// alternative is a C# lexer, and the failure mode of this version is a false POSITIVE on
        /// a commented-out cast, which is loud and fixable. A false negative would be silent, and
        /// that is the one this must not have. The `///` doc comments that carry most of the prose
        /// in this folder are caught by the `//` rule.
        /// </summary>
        private static string StripCommentsAndStrings(string line)
        {
            var builder = new System.Text.StringBuilder(line.Length);

            bool inString = false;
            bool inChar = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (inChar)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '\'')
                    {
                        inChar = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && (line[i + 1] == '/' || line[i + 1] == '*'))
                {
                    break;
                }

                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    // The tail of a block comment. Everything before it on this line was prose.
                    builder.Length = 0;
                    i++;
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString().Trim();
        }

        // ---- the report ----

        public static HealthResult BuildHealthResult()
        {
            string contract;
            bool agrees = StepsAgreeWithEngine(out contract);

            string seam;
            bool sourcesFound;
            bool intact = SeamIsIntact(out seam, out sourcesFound);

            if (!agrees)
            {
                return HealthResult.Fail(String.Format(
                    "the locomotion adapter does not match the engine: {0}{1}",
                    contract,
                    intact ? "" : ". And the seam has moved: " + seam));
            }

            if (!intact)
            {
                return HealthResult.Fail(String.Format(
                    "{0}, but the BaseCreature seam has moved: {1}", contract, seam));
            }

            if (!sourcesFound)
            {
                return HealthResult.Warn(String.Format(
                    "{0}; but {1}", contract, seam));
            }

            return HealthResult.Ok(String.Format("{0}; {1}", contract, seam));
        }
    }
}
