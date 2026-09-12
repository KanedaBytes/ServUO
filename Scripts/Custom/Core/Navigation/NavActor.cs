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
            var creature = mobile as BaseCreature;

            if (creature != null)
            {
                return new NavCreatureActor(creature);
            }

            return null;
        }
    }
}
