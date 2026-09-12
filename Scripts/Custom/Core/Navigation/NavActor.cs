// NavActor.cs — the locomotion seam. What NavWalker needs from the thing it is walking.
//
// WHY THIS EXISTS
// ---------------
// NavWalker took a BaseCreature and drove movement through BaseAI. That is reason 1 in
// Bots/README.md's "The constraint everything else follows from" for why a PlayerBot is a
// BaseCreature and not a PlayerMobile, and CLASS-DECISION.md is the session where Sean decided
// to spend it: bots become PlayerMobiles, and the walker has to stop caring which.
//
// It cannot become a rewrite, because three of the walker's eight users are daily-life
// BaseCreatures - DailyLifeTownsfolk, the shop-schedule vendors, and [WalkAudit's own probe - and
// they must keep BaseAI's clock exactly as they have it. So: one interface, two implementations,
// and the BaseCreature one proved green BEFORE anything changes class.
//
// WHAT IS AND IS NOT IN HERE
// --------------------------
// NavWalker touches its mobile in about seventy places. Sixty of them are plain Mobile members -
// Location, Map, Deleted, InRange, CanSee, MoveToWorld - that are identical whichever class is
// underneath, and three more hand the mobile to something that already takes a Mobile
// (MovementPath, BaseDoor.Use, NavWalkFailures.Record). Wrapping those would be a thirty-member
// interface that bought nothing and rewrote sixty lines inside the file whose behaviour has to be
// proved unchanged.
//
// So the interface is the Mobile handle plus ONLY what actually differs, which the enumeration
// puts at eleven members. Everything the walker keeps to itself - the route cache, NavEdgeHealth,
// the recovery ladder, rescue-tile validation, ResolveZ - stays where it is and is not the
// actor's business.
//
// Two members are on here despite being Mobile-level, and each earns it by what the PlayerMobile
// implementation will have to do at that exact moment:
//
//   Step      - BaseAI.DoMoveImpl advances NextMove INSIDE the step (BaseAI.cs:2349-2353). A
//               PlayerMobile sidestep has to move its own step clock; a bare Mobile.Move would
//               not, and the walker has no way to know it should.
//   PlaceAt   - BaseAI.OnTeleported force-repaths the cached path (BaseAI.cs:2615-2618). A
//               PlayerMobile adapter holding a PathFollower must do the same or the rescued
//               mobile walks the path it had before it was moved.
//
// And one member is here because of a null check that looks like tidy-up and is not. See
// HasMover.

using System;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// The thing a NavWalker walks.
    ///
    /// Implemented once per mobile class the walker has to drive. NavCreatureActor below is the
    /// BaseCreature one and is a pure extraction of what NavWalker did inline; the PlayerMobile
    /// one arrives with the class swap and mirrors BaseAI.MoveTo's own two-step structure over a
    /// plain PathFollower, which takes a Mobile and needs nothing from BaseAI
    /// (PathFollower.cs:18). That shape is not invented - it is what uo-offline's Traveler does
    /// (playerbots/source/CustomBots/Behaviors/TravelerBehavior.cs:1963, :3323).
    /// </summary>
    public interface INavActor
    {
        /// <summary>
        /// The mobile itself: identity, and the handle for everything that already takes a Mobile.
        ///
        /// NOT an escape hatch that defeats the interface - it is what keeps the interface small.
        /// The walker reads position, map, name and deleted-ness straight off this, and hands it
        /// to MovementPath, BaseDoor.Use and NavWalkFailures unchanged, because none of those
        /// questions have a different answer under a different base class.
        /// </summary>
        Mobile Mobile { get; }

        /// <summary>
        /// Is there anything underneath that can be asked for a step at all?
        ///
        /// A BaseCreature can have a null AIObject, and NavWalker returned early on it
        /// (NavWalker.cs, before the extraction) from BELOW the recovery ladder and ABOVE the
        /// NextMove gate. Both halves of that position matter and neither is obvious:
        ///
        ///   Below the ladder, so a null-AI creature still recovers. It never steps, so it never
        ///   gets closer, so the hop deadline fires, HandleStuck runs, and the ladder climbs -
        ///   Sidestep is Mobile.Move and needs no AI, and the top rung rescues it. That is what
        ///   happens today and it must keep happening.
        ///
        ///   Above the gate, so the two lines below it are never reached with a null AI:
        ///   NextStepTick and TransformedStepDelaySeconds would both throw. Today they are
        ///   unreachable. If "MoveTowards returned false" were the only signal, the walker would
        ///   fall through into the pace sampler and throw on the game thread the first time
        ///   [BotPace attached a sampler to a walker whose creature had no AI - a fault no audit
        ///   or smoke run would ever see, because none of them attach a sampler.
        ///
        /// So it is a member, not a cleanup. Always true for a PlayerMobile: there is no AI to be
        /// null and the adapter is itself the mover.
        ///
        /// NOT called CanMove, which is taken: BaseCreature.CanMove (BaseCreature.cs:232) is a
        /// settable gate BaseAI.DoMoveImpl reads (BaseAI.cs:2325) and this asks a different
        /// question. A member that shadowed it would read as the engine's and answer its own.
        /// </summary>
        bool HasMover { get; }

        /// <summary>
        /// Will this actor refuse goals away from its home, whatever it is asked?
        ///
        /// BaseCreature.ForceStayHome (BaseCreature.cs:338), which BaseAI.MoveTo honours by
        /// refusing to path outside Home +/- min(10, RangeHome) 97.5% of the time
        /// (BaseAI.cs:2628-2636) - silently pinning a walker to its spawn point. The walker warns
        /// about it once, at Follow. False for a PlayerMobile: the property is not on Mobile.
        /// </summary>
        bool RefusesDistantGoals { get; }

        /// <summary>
        /// When the next step is due, as a Core.TickCount.
        ///
        /// A TICK RATHER THAN A BOOL, for two reasons. The walker compares it by subtraction,
        /// which is the wraparound-safe idiom this tree requires; and the pace sampler reports
        /// how far away it is, which a bool cannot answer.
        ///
        /// For a BaseCreature this is BaseAI.NextMove (BaseAI.cs:2301), the clock DoMoveImpl
        /// would check anyway. For a PlayerMobile it becomes the adapter's own field, set from
        /// the pace after each successful step the way DoMoveImpl sets it (BaseAI.cs:2349-2353) -
        /// because upstream's answer, a per-behaviour Timer at a mount-and-run-derived interval
        /// (TravelerBehavior.cs:3249-3272), is not available to a shard that drives its whole
        /// fleet from one shared 50 ms timer on purpose.
        ///
        /// Only read when HasMover is true.
        /// </summary>
        long NextStepTick { get; }

        /// <summary>
        /// One step toward <paramref name="goal"/>, pathing if the direct step is refused.
        ///
        /// BaseAI.MoveTo (BaseAI.cs:2621) for a creature. Note what it does and what the walker
        /// therefore does not have to: it tries DoMove(GetDirectionTo(p)) first and only builds a
        /// PathFollower when that is refused, and a call whose goal matches the cached one
        /// re-follows the existing path without searching. THE GOAL IS COMPARED BY REFERENCE
        /// (BaseAI.cs:2644), which is why the walker holds a NavGoal - a class - for the length
        /// of a hop and replaces the instance to force a repath.
        ///
        /// Only called when HasMover is true.
        /// </summary>
        bool MoveTowards(IPoint3D goal, bool run, int range);

        /// <summary>
        /// One step in a named direction. Mobile.Move, and the recovery ladder's Sidestep rung.
        ///
        /// Returns what Move returns, which is true for a TURN as well as for a step
        /// (Mobile.cs:3120 - it only moves when the mobile already faces d). Callers that care
        /// about the difference compare Location, exactly as the sidestep does.
        /// </summary>
        bool Step(Direction d);

        /// <summary>
        /// Put the mobile at <paramref name="location"/> on <paramref name="map"/>: the rescue
        /// at the top of the ladder, and a gate hop.
        ///
        /// MoveToWorld, plus whatever the implementation has to do about a path it was following
        /// before it was moved.
        /// </summary>
        void PlaceAt(Point3D location, Map map);

        /// <summary>
        /// Point the actor's idle wander at where it is going, under NavWalker.KeepHomeAligned.
        ///
        /// BaseCreature.Home (BaseCreature.cs:3513). For an actor whose AI we do not own,
        /// BaseAI.WalkRandomInHome keeps stepping it back toward Home with no spawner gate,
        /// dragging against the route; moving Home to each hop makes the wander pull the same
        /// way. A no-op for a PlayerMobile, which has no wander to aim and no Home to aim it
        /// with - XmlSpawner2.cs:9318 only sets Home `if (m is BaseCreature)`.
        /// </summary>
        void AlignHome(Point3D location);

        /// <summary>
        /// The pace the actor was GIVEN, in seconds per step. BaseCreature.CurrentSpeed.
        /// Read only by the pace sampler, and only when HasMover is true.
        /// </summary>
        double StepDelaySeconds { get; }

        /// <summary>
        /// The pace the engine actually STEPS ON, in seconds - what the step clock advances by.
        ///
        /// BaseAI.TransformMoveDelay(CurrentSpeed) (BaseAI.cs:2279), which runs the creature
        /// through SpeedInfo and can differ from what it was given. The sampler reports both so a
        /// pace that was set and a pace that was honoured can be told apart. Equal to
        /// StepDelaySeconds for a PlayerMobile, where the adapter owns both.
        ///
        /// Only read when HasMover is true.
        /// </summary>
        double TransformedStepDelaySeconds { get; }

        /// <summary>
        /// Forget any cached path. Called when the walker stops.
        ///
        /// A no-op for a BaseCreature: BaseAI's PathFollower is private with no public reset, and
        /// the walker forces a repath by replacing the NavGoal instance instead (see
        /// MoveTowards). It is here for the PlayerMobile implementation, which owns its follower
        /// and has to drop it - upstream's equivalent is `_follower?.ForceRepath()`
        /// (TravelerBehavior.cs:1161).
        /// </summary>
        void DropCachedPath();
    }

    /// <summary>
    /// The BaseCreature implementation: BaseAI, exactly as NavWalker drove it inline.
    ///
    /// PURE EXTRACTION. Every member below is the line that used to be in NavWalker.Tick,
    /// Sidestep, Teleport, OnTransition or EnsureGoal, moved and not rewritten, because the whole
    /// point of doing the adapter before the class swap is that this half can be proved identical
    /// against a captured baseline. Anything that would be nicer done differently belongs in a
    /// different commit in a different session.
    /// </summary>
    public sealed class NavCreatureActor : INavActor
    {
        private readonly BaseCreature _creature;

        public NavCreatureActor(BaseCreature creature)
        {
            if (creature == null)
            {
                throw new ArgumentNullException("creature");
            }

            _creature = creature;
        }

        public Mobile Mobile
        {
            get { return _creature; }
        }

        public bool HasMover
        {
            get { return _creature.AIObject != null; }
        }

        public bool RefusesDistantGoals
        {
            get { return _creature.ForceStayHome; }
        }

        public long NextStepTick
        {
            get { return _creature.AIObject.NextMove; }
        }

        public bool MoveTowards(IPoint3D goal, bool run, int range)
        {
            return _creature.AIObject.MoveTo(goal, run, range);
        }

        public bool Step(Direction d)
        {
            return _creature.Move(d);
        }

        public void PlaceAt(Point3D location, Map map)
        {
            _creature.MoveToWorld(location, map);
        }

        public void AlignHome(Point3D location)
        {
            _creature.Home = location;
        }

        public double StepDelaySeconds
        {
            get { return _creature.CurrentSpeed; }
        }

        public double TransformedStepDelaySeconds
        {
            get { return _creature.AIObject.TransformMoveDelay(_creature.CurrentSpeed); }
        }

        public void DropCachedPath()
        {
        }
    }

    /// <summary>
    /// The PlayerMobile implementation: BaseAI.MoveTo's structure over a plain PathFollower.
    ///
    /// NOT AN INVENTION - this is what BaseAI.MoveTo does (BaseAI.cs:2621-2669) with the two
    /// BaseCreature-only clauses removed, and it is the shape upstream already runs in production
    /// for exactly this class (Behaviors/TravelerBehavior.cs:1963, :3323). A PathFollower takes a
    /// plain Mobile (PathFollower.cs:18) and needs nothing from BaseAI.
    ///
    /// TWO THINGS BaseAI DID FOR FREE AND THIS MUST DO ITSELF, and they are the whole reason Step
    /// and PlaceAt are on the interface at all.
    ///
    ///   THE STEP CLOCK. BaseAI.DoMoveImpl advances NextMove INSIDE the step (BaseAI.cs:2349-2353)
    ///   and clamps a stale one forward to now. Here that is AdvanceClock, which every step goes
    ///   through, so a step taken by the recovery ladder moves the clock exactly as a step taken by
    ///   the follower does. Additive then clamped, and compared by subtraction, because
    ///   Core.TickCount wraps (CLAUDE.md section 15).
    ///
    ///   THE CACHED PATH. BaseAI.OnTeleported force-repaths its follower (BaseAI.cs:2612-2619).
    ///   A rescue that moved the mobile and left a follower still walking to the old goal from the
    ///   old tile would spend the next hop walking back. PlaceAt drops it, which is the same
    ///   guarantee one step stronger than a repath.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO: SpeedInfo. BaseAI.TransformMoveDelay runs the creature
    /// through SpeedInfo, which clamps toward MaxDelayWild for an uncontrolled creature below full
    /// Stam - re-inflating a run the moment a bot takes damage. BotAI overrode that away for
    /// exactly that reason while it existed. So here the two delay members are EQUAL, and the pace
    /// the bot was given is the pace it steps at. One consequence worth knowing: [BotPace exists to
    /// tell those two apart, and for a PlayerMobile they can no longer disagree.
    /// </summary>
    public sealed class NavPlayerActor : INavActor
    {
        private readonly PlayerMobile _player;

        private PathFollower _path;
        private long _nextStepTick;

        public NavPlayerActor(PlayerMobile player)
        {
            if (player == null)
            {
                throw new ArgumentNullException("player");
            }

            _player = player;

            // Due immediately. A fresh actor that made the walker wait would lose the first step
            // of every leg, and a leg gets a fresh walker in four of the five behaviours.
            _nextStepTick = Core.TickCount;
        }

        public Mobile Mobile
        {
            get { return _player; }
        }

        /// <summary>
        /// Always. There is no AI to be missing - which is the point of the class swap, and the
        /// reason the null-AI early return in NavWalker never fires for this actor.
        /// </summary>
        public bool HasMover
        {
            get { return true; }
        }

        /// <summary>ForceStayHome is a BaseCreature property; a PlayerMobile has no tether.</summary>
        public bool RefusesDistantGoals
        {
            get { return false; }
        }

        public long NextStepTick
        {
            get { return _nextStepTick; }
        }

        /// <summary>
        /// The pace the bot was given, in seconds per step.
        ///
        /// Read off IBotActor rather than off a concrete class, because this is Core and Core does
        /// not name bot classes. The fallback is the engine's own walk-on-foot constant, for a
        /// PlayerMobile that is not a bot: nothing constructs one today, but an actor whose step
        /// delay was zero would ask the walker for a step on every single tick of the shared timer.
        /// </summary>
        private double PaceSeconds
        {
            get
            {
                var actor = _player as IBotActor;

                if (actor == null)
                {
                    return Mobile.WalkFoot / 1000.0;
                }

                double pace = actor.StepDelaySeconds;

                return pace > 0.0 ? pace : Mobile.WalkFoot / 1000.0;
            }
        }

        public double StepDelaySeconds
        {
            get { return PaceSeconds; }
        }

        public double TransformedStepDelaySeconds
        {
            get { return PaceSeconds; }
        }

        /// <summary>
        /// The run flag, derived from the delay rather than passed in, which is what the engine
        /// does (BaseAI.cs:2338-2344): a mobile is running when its step delay is shorter than the
        /// walk constant for whatever it is on. Deriving it here rather than trusting the caller is
        /// what keeps a sidestep and a follower step look the same to a watching client.
        ///
        /// BaseAI ANDs in `CanRun` (BaseAI.cs:2339), a BaseAI property with no Mobile equivalent.
        /// It is true for every walker this shard drives - it is only ever set false by fleeing and
        /// hiding AI that does not exist here - so it is recorded rather than substituted.
        /// </summary>
        private Direction WithRunningFlag(Direction d, int delay)
        {
            bool mounted = _player.Mounted || _player.Flying;

            if (mounted ? delay < Mobile.WalkMount : delay < Mobile.WalkFoot)
            {
                d |= Direction.Running;
            }

            return d;
        }

        /// <summary>
        /// BaseAI.cs:2349-2354, verbatim over our own field.
        ///
        /// ADVANCED WHETHER OR NOT THE STEP LANDED, which is what DoMoveImpl does: it advances
        /// NextMove before Move is attempted, so a mobile grinding against a wall is rate-limited
        /// exactly as a walking one is. Without that, a blocked walker would ask for a step on
        /// every 50ms tick of the shared timer.
        /// </summary>
        private void AdvanceClock(int delay)
        {
            _nextStepTick += delay;

            if (Core.TickCount - _nextStepTick > 0)
            {
                _nextStepTick = Core.TickCount;
            }
        }

        /// <summary>
        /// BaseCreature.TurnInternal (BaseCreature.cs:4098) - the one member of DoMoveImpl that is
        /// not reachable from a PlayerMobile, and the only substitute in this port.
        ///
        /// Its whole body is the line below, and every part of that line is Mobile-level:
        /// Mobile.SetDirection is public (Mobile.cs:8740), the mask keeps the turn inside the eight
        /// facings, and `v & 0x80` preserves the running bit. So this is the same arithmetic on the
        /// same setter rather than a reimplementation - including the choice of SetDirection over
        /// the Direction property, which is not cosmetic: the property sends a Direction delta
        /// (Mobile.cs:8755) and SetDirection is silent, so a turn that is about to be followed by a
        /// successful Move sends one packet rather than two, and a turn that fails sends none.
        /// </summary>
        private void TurnInternal(int turnSteps)
        {
            int v = (int)_player.Direction;

            _player.SetDirection((Direction)((((v & 0x7) + turnSteps) & 0x7) | (v & 0x80)));
        }

        /// <summary>
        /// BaseAI.DoMoveImpl (BaseAI.cs:2323-2507) for a PlayerMobile - one step, with the engine's
        /// own step-level recovery.
        ///
        /// WHY IT IS A FULL PORT. This was `StepAndAdvance`, which was DoMoveImpl's *tail* only:
        /// derive the run flag, Move, advance the clock. The 12 September rebaseline measured what
        /// the rest of it was worth. A BaseCreature whose step is refused turns up to twice and
        /// retries in the turned direction (:2483-2499); a bot did not, and its coarsest substitute
        /// was NavWalker's Sidestep rung twenty seconds later. Two of the three bot-class walk
        /// failures were `arrived-no-stand-tile` with NOBODY STANDING ANYWHERE NEAR, which is the
        /// shape a missing step-level recovery makes and not a shape contention makes.
        ///
        /// WHAT EACH OMITTED CLAUSE IS, because "ported exactly" has to be checkable:
        ///
        ///   :2325-2329, the bad-state guard. Only `Deleted` survives the class change. `CanMove`
        ///     (BaseCreature.cs:232), `DisallowAllMoves` (:338) and `FreezeOnCast` (:1062) are
        ///     BaseCreature members; Frozen, Paralyzed and the casting test are enforced one level
        ///     down by Mobile.Move itself (Mobile.cs:3123-3132), for both implementations.
        ///
        ///   :2358, MoveImpl.IgnoreMovableImpassables. DELIBERATELY NOT SET, which is Sean's rule
        ///     for this layer: if a player cannot do it, a bot cannot either. Bots path and step
        ///     around movable impassables, MODIFICATIONS entry 6 withholds the same flag from the
        ///     route, and the two halves now agree. See Bots/README.md's Deviations.
        ///
        ///   :2360-2366, the direction-mismatch branch. DEAD, here and upstream: :2347 has just
        ///     assigned Direction = d, so the masks cannot differ. Recorded rather than carried.
        ///
        ///   :2374-2479, obstacle clearing. Its two gates are `CanOpenDoors` and
        ///     `CanDestroyObstacles`, both BaseCreature. The door half already runs for a bot ONE
        ///     LEVEL LOWER - PlayerBot.Move tries DoorHelper.TryOpenAhead and re-Moves
        ///     (PlayerBot.cs:759-767), upstream's own answer - so by the time Move returns false
        ///     here the door has already been tried, which is exactly the state DoMoveImpl reaches
        ///     with `blocked` still true. The destroy half is the furniture rule again. Note the
        ///     turned retries below go through PlayerBot.Move too, so each of them gets the door
        ///     attempt as well.
        /// </summary>
        private MoveResult DoStep(Direction d)
        {
            // BaseAI.cs:2325-2329, less the five BaseCreature clauses. See the summary.
            if (_player.Deleted)
            {
                return MoveResult.BadState;
            }

            // BaseAI.cs:2331-2334, over CheckMove (:2303-2306). NEW: StepAndAdvance advanced this
            // clock and never read it, so PathFollower.Follow's blocked-retry (PathFollower.cs:172-186)
            // could take a SECOND physical step inside one Follow, where a creature's second call
            // is refused here as BadState. Wraparound-safe by subtraction, as everything else is.
            if (Core.TickCount - _nextStepTick < 0)
            {
                return MoveResult.BadState;
            }

            int delay = (int)(PaceSeconds * 1000.0);

            d = WithRunningFlag(d, delay);

            // BaseAI.cs:2346-2347, comment and all: "This makes them always move one step, never
            // any direction changes."
            //
            // NEW, AND IT COSTS A STEP EVERY TIME IT IS MISSING. Mobile.Move only moves when the
            // mobile ALREADY faces d (Mobile.cs:3119); otherwise it turns and returns true. So
            // without this line a heading change on the direct-step branch burned the step clock,
            // moved nothing, and reported success to the walker. The follower branch never had the
            // fault - PathFollower.Follow calls SetDirection itself (PathFollower.cs:141,148).
            _player.Direction = d;

            AdvanceClock(delay);

            // BaseAI.cs:2356. Mobile-level (Mobile.cs:3061), and only ever set by the shove branch
            // at Mobile.cs:3531 - which is inside the body FreeMovement skips, so on Trammel this
            // is always false and the clause below is Felucca's. Ported because the flag is real
            // there, not because it fires here.
            _player.Pushing = false;

            if (_player.Move(d))
            {
                return MoveResult.Success; // BaseAI.cs:2502, :2506
            }

            bool wasPushing = _player.Pushing; // BaseAI.cs:2369

            // BaseAI.cs:2483-2496 - turn twice, retry in the turned direction. The 0.6 split is
            // upstream's: it picks a side, it does not pick a side fairly.
            int offset = Utility.RandomDouble() >= 0.6 ? 1 : -1;

            for (int i = 0; i < 2; i++)
            {
                TurnInternal(offset);

                if (_player.Move(_player.Direction))
                {
                    return MoveResult.SuccessAutoTurn;
                }
            }

            return wasPushing ? MoveResult.BadState : MoveResult.Blocked; // BaseAI.cs:2499
        }

        /// <summary>
        /// One step with no auto-turn: Mobile.Move plus the step clock.
        ///
        /// THE SIDESTEP RUNG'S STEP, and it is deliberately NOT DoStep. NavCreatureActor.Step is a
        /// bare `_creature.Move(d)` - the rung never went through BaseAI at all - so giving this
        /// one an auto-turn would make the two adapters disagree on the member that Nav.Actor's
        /// wall assertion measures, which asks whether a step in ONE NAMED DIRECTION is refused.
        /// An adapter that answered by stepping somewhere else would be reporting a different
        /// question's answer.
        ///
        /// It keeps the clock advance, which the creature's Step does not have. That is the
        /// existing, deliberate difference (see the class summary): the rung's step is a step, and
        /// a bot that sidestepped for free would step faster than its pace while recovering.
        /// </summary>
        private bool StepAndAdvance(Direction d)
        {
            int delay = (int)(PaceSeconds * 1000.0);

            d = WithRunningFlag(d, delay);

            bool moved = _player.Move(d);

            AdvanceClock(delay);

            return moved;
        }

        /// <summary>
        /// The Mover the PathFollower calls, so a path-driven step moves the clock the same way a
        /// direct one does - the counterpart of BaseAI setting m_Path.Mover = DoMoveImpl
        /// (BaseAI.cs:2655). Leaving Mover null would let PathFollower call Mobile.Move behind the
        /// clock (PathFollower.cs:44) and step at the shared timer rate instead of at the pace.
        ///
        /// It hands back the MoveResult RAW, which is the half that used to be lost: Follow
        /// branches on Blocked alone (PathFollower.cs:153), so a SuccessAutoTurn correctly skips
        /// the repath-and-retry and a BadState correctly does nothing, where the old
        /// `moved ? Success : Blocked` collapsed all three into two.
        /// </summary>
        private MoveResult FollowerMove(Direction d)
        {
            return DoStep(d);
        }

        public bool MoveTowards(IPoint3D goal, bool run, int range)
        {
            // BaseAI.MoveTo also tests DisallowAllMoves here; that is a BaseCreature property and
            // there is no PlayerMobile equivalent to test. Nothing is lost: Mobile.Move refuses a
            // deleted, frozen, paralyzed or mid-cast mobile on its own (Mobile.cs:3105, :3130), so
            // the guards that matter are enforced one level down for both implementations.
            if (_player.Deleted || goal == null)
            {
                return false;
            }

            var damageable = goal as IDamageable;

            if (damageable != null && damageable.Deleted)
            {
                return false;
            }

            if (_player.InRange(goal, range))
            {
                _path = null;
                return true;
            }

            // Goal compared BY REFERENCE, as BaseAI.cs:2644 does - which is why NavWalker holds a
            // NavGoal instance for the length of a hop and replaces it to force a repath.
            if (_path != null && _path.Goal == goal)
            {
                if (_path.Follow(run, 1))
                {
                    _path = null;
                    return true;
                }

                return false;
            }

            // Direct step first, path only on refusal. This is the ordering that keeps a walker
            // off the pathfinder for the ordinary case of an unobstructed tile.
            //
            // BaseAI.MoveTo:2651 asks DoMove(dir, badStateOk: true), and DoMove (:2313-2321) reads
            // badStateOk as "a step we were not due is not a reason to go and search". The three
            // accepted results below are that test, spelled out.
            MoveResult direct = DoStep(_player.GetDirectionTo(goal));

            if (direct == MoveResult.Success || direct == MoveResult.SuccessAutoTurn ||
                direct == MoveResult.BadState)
            {
                _path = null;
                return true;
            }

            _path = new PathFollower(_player, goal);
            _path.Mover = FollowerMove;

            if (_path.Follow(run, 1))
            {
                _path = null;
                return true;
            }

            return false;
        }

        public bool Step(Direction d)
        {
            return StepAndAdvance(d);
        }

        public void PlaceAt(Point3D location, Map map)
        {
            _player.MoveToWorld(location, map);

            // BaseAI.OnTeleported, one step stronger: the follower is dropped rather than
            // repathed, so the next MoveTowards rebuilds it from where the mobile now stands.
            _path = null;
        }

        /// <summary>
        /// Nothing to align. A PlayerMobile has no idle wander to point at a destination, and
        /// XmlSpawner2.cs:9318 never gave it a Home to point with.
        /// </summary>
        public void AlignHome(Point3D location)
        {
        }

        public void DropCachedPath()
        {
            _path = null;
        }
    }

    /// <summary>
    /// Which implementation a mobile gets.
    ///
    /// ONE PLACE, and that is the point of it existing at all rather than each of the eight
    /// construction sites naming a class. The class swap adds a branch here for PlayerBot and
    /// nothing else in the tree moves.
    /// </summary>
    public static class NavActor
    {
        /// <summary>
        /// The actor for this mobile, or null when nothing here can drive it.
        ///
        /// Null rather than a throw: NavWalker already refuses a route for a null or deleted
        /// mobile at Follow, and a caller that hands over something unwalkable should get the
        /// same silence it gets today rather than an exception on the game thread.
        /// </summary>
        public static INavActor For(Mobile mobile)
        {
            // PlayerMobile first, and the two are disjoint - nothing is both - so the order
            // is for the reader rather than for correctness. This is the ONE branch the
            // class swap adds anywhere in the tree: the eight construction sites all call
            // For, and not one of them changed.
            var player = mobile as PlayerMobile;

            if (player != null)
            {
                return new NavPlayerActor(player);
            }

            var creature = mobile as BaseCreature;

            if (creature != null)
            {
                return new NavCreatureActor(creature);
            }

            return null;
        }
    }
}
