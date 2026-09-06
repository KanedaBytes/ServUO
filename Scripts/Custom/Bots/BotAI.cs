using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// True while something is steering this bot along a route, so the stock wander tick knows to
    /// stand aside. The same contract IDailyLifeActor carries, kept separate because a bot is not
    /// a daily-life actor and the two will diverge.
    /// </summary>
    public interface IBotActor
    {
        bool Commuting { get; set; }
    }

    /// <summary>
    /// The AI a PlayerBot runs.
    ///
    /// SEAM 5 of 5 (see README.md): the BASE CLASS OF THIS TYPE IS AN IMPLEMENTATION DETAIL, and
    /// this file is the only one in the tree permitted to name it. Nothing outside BotAI.cs may
    /// depend on a bot's AI being a VendorAI - not a cast, not a type test, not a call to a
    /// VendorAI member. PlayerBot reaches its AI only through ForcedAI and the Commuting flag.
    ///
    /// The reason is that this base is temporary. VendorAI is the right stand-aside for a bot that
    /// only stands still: it does not acquire targets, does not wander far, and its
    /// DoActionWander is easy to suppress. The moment bots fight, this becomes MeleeAI, MageAI or
    /// a purpose-built BaseAI subclass, and that swap must be a change to this one file.
    ///
    /// Keep this class thin. Anything that is really bot logic belongs on PlayerBot or in a
    /// behaviour, where it survives the swap.
    /// </summary>
    public class BotAI : VendorAI
    {
        /// <summary>Seconds per step while being steered. A brisk walk, not a run.</summary>
        public const double CommuteMoveDelay = 0.5;

        public BotAI(BaseCreature m)
            : base(m)
        {
        }

        private bool IsCommuting
        {
            get
            {
                var actor = m_Mobile as IBotActor;

                return actor != null && actor.Commuting;
            }
        }

        public override bool DoActionWander()
        {
            if (IsCommuting)
            {
                // Handled elsewhere: something is steering. Returning true keeps the action as
                // Wander without issuing a step, so the two do not fight over the move gate.
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
