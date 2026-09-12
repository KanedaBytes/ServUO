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
        /// Four assertions. The first three run once per adapter; the fourth compares them with
        /// each other and lives in StepsRecoverAlike below.
        ///
        ///   1. A step onto the known wall is REFUSED through the adapter.
        ///   2. That verdict EQUALS what Movement.CheckMovement says about the identical step.
        ///   3. After a step the engine does allow, the mobile the adapter hands back is the one
        ///      that actually moved in the world.
        ///   4. On that same blocked tile, every adapter reaches the same verdict and leaves its
        ///      probe turned by the same amount - the step-level recovery BaseAI.DoMoveImpl has
        ///      always had and NavPlayerActor gained on 12 September 2026.
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

            string recovery;

            if (!StepsRecoverAlike(out recovery))
            {
                detail = "step recovery: " + recovery;
                return false;
            }

            details.Add("step recovery: " + recovery);

            detail = String.Join(" | ", details.ToArray());
            return true;
        }

        /// <summary>
        /// THE FOURTH ASSERTION, and the only cross-class one: on the same blocked tile, every
        /// adapter reaches the same verdict and leaves its probe turned by the same amount.
        ///
        /// WHAT IT IS FOR. BaseAI.DoMoveImpl recovers a refused step by turning up to twice and
        /// retrying in the turned direction (BaseAI.cs:2483-2499). NavPlayerActor did not, which is
        /// Finding 2 of the 12 September rebaseline - two of the three bot-class walk failures were
        /// `arrived-no-stand-tile` with nobody standing near. The port is only worth anything if
        /// something fails when it is reverted, and the three assertions above cannot see it: they
        /// exercise Step, which is Mobile.Move for both adapters and has no auto-turn on either.
        /// This one goes through MoveTowards, which is where DoMoveImpl lives.
        ///
        /// WHY IT CANNOT ASSERT AN IDENTICAL FACING, which is the shape it was first asked for.
        /// BaseAI.cs:2485 rolls the turn direction per call - `Utility.RandomDouble() >= 0.6 ? 1 :
        /// -1` - so two probes on one tile legitimately end up facing into+1 and into-1. Magnitude
        /// is the half that is not rolled, and it discriminates just as sharply: before the port a
        /// PlayerMobile's refused step built a PathFollower to an unstandable tile, whose no-path
        /// branch sets the facing straight back to `into` (PathFollower.cs:137-146) - magnitude 0
        /// against the creature's 1 or 2.
        ///
        /// AND THE EXPECTED MAGNITUDE IS DERIVED FROM THE ENGINE, not named here, for the same
        /// reason TryStepSomewhere derives its direction: a hard-coded "it ends up facing north"
        /// would be a second claim about Trinsic's geometry that nothing checks. The two shapes
        /// that survive the random sign are the only ones asserted, and a tile that is in neither
        /// is reported rather than failed - it is a fact about the map, and the verdict half of the
        /// assertion still runs.
        /// </summary>
        private static bool StepsRecoverAlike(out string detail)
        {
            Map map = Map.Trammel;

            Point3D from = NavMovement.SolidFrom;
            Point3D wall = NavMovement.SolidTile;

            Direction into = Utility.GetDirection(from, wall);

            // The four tiles an auto-turn can reach, asked of the engine rather than assumed. Note
            // what the wall itself does to two of them: a diagonal step needs both of its
            // orthogonal neighbours, one of which is the wall, so into+/-1 are refused BY the
            // obstruction being stepped into. That is the geometry, not a coincidence, and it is
            // why the forced-magnitude-2 branch is the one this seed tile is expected to take.
            bool plus1, minus1, plus2, minus2;

            if (!EngineAllowsTurns(map, from, into, out plus1, out minus1, out plus2, out minus2))
            {
                detail = "no probe classes are registered, so the engine could not be asked";
                return false;
            }

            int expectedTurn;
            string shape;

            if (plus1 && minus1)
            {
                expectedTurn = 1;
                shape = "the first turn lands whichever way it is rolled";
            }
            else if (!plus1 && !minus1 && plus2 && minus2)
            {
                expectedTurn = 2;
                shape = "the first turn is refused both ways and the second lands both ways";
            }
            else
            {
                // Sign-dependent: one roll recovers in one turn and the other in two, or one
                // recovers and the other does not. Nothing deterministic can be said about the
                // magnitude, so it is not said. The verdicts are still compared.
                expectedTurn = -1;
                shape = String.Format(
                    "SIGN-DEPENDENT at {0},{1} - the engine allows +1 {2}, -1 {3}, +2 {4}, -2 {5}, "
                    + "so BaseAI.cs:2485's roll decides the magnitude and only the verdicts are compared",
                    from.X, from.Y, plus1, minus1, plus2, minus2);
            }

            bool haveFirst = false;
            bool firstVerdict = false;
            int firstTurn = 0;
            string firstLabel = null;
            int classes = 0;

            foreach (NavWalkAudit.ProbeClass cls in NavWalkAudit.ProbeClasses)
            {
                classes++;

                bool verdict;
                int turned;

                // One probe at a time, created and deleted inside the loop, so no probe is ever
                // standing on the tile another one is being measured from and each adapter starts
                // with its step clock due (NavActor.cs's ctor comment).
                NavWalkAudit.IWalkAuditProbe probe = cls.Create();

                try
                {
                    probe.Mobile.MoveToWorld(from, map);
                    probe.Mobile.Direction = into;

                    INavActor actor = NavActor.For(probe.Mobile);

                    if (actor == null)
                    {
                        detail = "NavActor.For refused " + cls.Label;
                        return false;
                    }

                    // The wall tile itself, so the call cannot be satisfied by pathing around it:
                    // whatever happens here is the step-level recovery and nothing else.
                    verdict = actor.MoveTowards(new Point3D(wall), false, 0);
                    turned = TurnSteps(into, probe.Mobile.Direction);
                }
                catch (Exception ex)
                {
                    detail = cls.Label + "'s probe threw: " + ex.Message;
                    return false;
                }
                finally
                {
                    probe.Mobile.Delete();
                }

                if (expectedTurn >= 0 && turned != expectedTurn)
                {
                    detail = String.Format(
                        "{0} was refused a step {1} from {2},{3} and ended up turned {4} step(s) "
                        + "where the engine's own recovery turns {5} - {6}",
                        cls.Label, into, from.X, from.Y, turned, expectedTurn, shape);
                    return false;
                }

                if (!haveFirst)
                {
                    haveFirst = true;
                    firstVerdict = verdict;
                    firstTurn = turned;
                    firstLabel = cls.Label;
                    continue;
                }

                // The magnitude is only comparable when it is forced. Under a sign-dependent
                // neighbourhood two adapters can differ by BaseAI.cs:2485's roll alone, and an
                // assertion that failed on a coin toss would be worse than no assertion.
                if (verdict != firstVerdict || (expectedTurn >= 0 && turned != firstTurn))
                {
                    detail = String.Format(
                        "{0} and {1} DISAGREE about a refused step {2} from {3},{4}: {0} {5} and "
                        + "turned {6} step(s), {1} {7} and turned {8}",
                        firstLabel, cls.Label, into, from.X, from.Y,
                        firstVerdict ? "recovered" : "did not recover", firstTurn,
                        verdict ? "recovered" : "did not recover", turned);
                    return false;
                }
            }

            if (classes < 2)
            {
                detail = String.Format(
                    "only {0} probe class is registered, so the adapters were not compared with "
                    + "each other; the one that ran turned {1} step(s)",
                    classes, firstTurn);
                return true;
            }

            detail = String.Format(
                "every adapter met the refused step {0} from {1},{2} the same way - {3} and turned "
                + "{4} step(s) ({5})",
                into, from.X, from.Y,
                firstVerdict ? "recovered" : "did not recover", firstTurn, shape);

            return true;
        }

        /// <summary>
        /// What the engine says about the four tiles an auto-turn can reach, asked once and
        /// without moving anything.
        ///
        /// One probe for all four questions, and the FIRST registered class's, because
        /// CheckMovement reads its mobile for passability rules only and both probe classes are
        /// ordinary human-bodied mobiles standing on the same tile - the wall answers the same for
        /// either. A stand-in declared here would be the third instrument this file already
        /// refuses to grow.
        /// </summary>
        private static bool EngineAllowsTurns(
            Map map, Point3D from, Direction into,
            out bool plus1, out bool minus1, out bool plus2, out bool minus2)
        {
            plus1 = minus1 = plus2 = minus2 = false;

            foreach (NavWalkAudit.ProbeClass cls in NavWalkAudit.ProbeClasses)
            {
                NavWalkAudit.IWalkAuditProbe probe = cls.Create();

                try
                {
                    probe.Mobile.MoveToWorld(from, map);

                    int newZ;

                    plus1 = Movement.Movement.CheckMovement(probe.Mobile, map, from, Turned(into, 1), out newZ);
                    minus1 = Movement.Movement.CheckMovement(probe.Mobile, map, from, Turned(into, -1), out newZ);
                    plus2 = Movement.Movement.CheckMovement(probe.Mobile, map, from, Turned(into, 2), out newZ);
                    minus2 = Movement.Movement.CheckMovement(probe.Mobile, map, from, Turned(into, -2), out newZ);
                }
                finally
                {
                    probe.Mobile.Delete();
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// BaseCreature.TurnInternal's arithmetic (BaseCreature.cs:4098), without the mobile: the
        /// low three bits are the facing and the turn wraps inside them.
        /// </summary>
        private static Direction Turned(Direction d, int steps)
        {
            int v = (int)d;

            return (Direction)((((v & 0x7) + steps) & 0x7) | (v & 0x80));
        }

        /// <summary>
        /// How many 45 degree steps apart two facings are, 0 to 4. The short way round, because a
        /// turn of +1 and a turn of -1 are the same size and BaseAI.cs:2485 chooses between them
        /// at random.
        /// </summary>
        private static int TurnSteps(Direction a, Direction b)
        {
            int diff = Math.Abs(((int)a & 0x7) - ((int)b & 0x7));

            return Math.Min(diff, 8 - diff);
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
