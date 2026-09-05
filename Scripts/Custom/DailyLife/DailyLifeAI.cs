using System;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Implemented by every daily-life actor so its AI knows when a NavWalker owns it.
    /// </summary>
    public interface IDailyLifeActor
    {
        /// <summary>True while a NavWalker is steering this mobile along a route.</summary>
        bool Commuting { get; set; }
    }

    /// <summary>
    /// The AI every daily-life actor uses, injected through BaseCreature.ForcedAI.
    ///
    /// It exists to get out of NavWalker's way, and it solves two problems that would otherwise
    /// make a walking shopkeeper impossible:
    ///
    /// 1. BaseAI.DoActionWander eventually calls WalkRandomInHome (BaseAI.cs:2509), which has no
    ///    spawner gate at all and keeps stepping the mobile back toward Home. Two things steering
    ///    one mobile means it lurches; whichever passes the NextMove gate first wins that step.
    ///
    /// 2. VendorAI.TransformMoveDelay (VendorAI.cs:148) returns BaseVendor.GetMoveDelay verbatim,
    ///    ignoring CurrentSpeed - and that is Utility.RandomMinMax(30, 120) SECONDS per step
    ///    (BaseVendor.cs:74). Fine for a vendor idling behind a counter; a forty-tile walk home
    ///    at that rate takes twenty to eighty real minutes, and dusk to dawn is thirty.
    ///
    /// Both effects are scoped exactly to the journey by Commuting, so an idle shopkeeper still
    /// paces its counter at the stock pace rather than scurrying.
    /// </summary>
    public class DailyLifeAI : VendorAI
    {
        /// <summary>Seconds per step while commuting. A brisk walk, not a run.</summary>
        public const double CommuteMoveDelay = 0.5;

        public DailyLifeAI(BaseCreature m)
            : base(m)
        {
        }

        private bool IsCommuting
        {
            get
            {
                var actor = m_Mobile as IDailyLifeActor;
                return actor != null && actor.Commuting;
            }
        }

        public override bool DoActionWander()
        {
            if (IsCommuting)
            {
                // Handled: NavWalker is steering. Returning true keeps the action as Wander
                // without issuing a step.
                return true;
            }

            return base.DoActionWander();
        }

        public override double TransformMoveDelay(double delay)
        {
            if (IsCommuting)
            {
                return CommuteMoveDelay;
            }

            return base.TransformMoveDelay(delay);
        }
    }
}
