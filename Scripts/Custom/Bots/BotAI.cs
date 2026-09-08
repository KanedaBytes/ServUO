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

        /// <summary>
        /// While being steered, the pace the bot was given IS the delay - untouched.
        ///
        /// This used to return a constant 0.5, copied from DailyLifeAI along with its comment ("a
        /// brisk walk, not a run"). There it is deliberate: townsfolk are never given a pace and
        /// the constant exists to escape VendorAI's 30-120 SECOND delay. Here it silently overrode
        /// the pace system built after it. BotMovement.SetPace wrote 200ms on foot or 100ms mounted
        /// into CurrentSpeed, the editor's panel printed that and said "running", and DoMoveImpl -
        /// which steps on TransformMoveDelay(CurrentSpeed), not on CurrentSpeed - advanced NextMove
        /// by 500ms and derived the run flag from 500 too. Measured by [BotPace before this change:
        /// a mounted bot given 100ms stepped every 543ms with the running bit set on none of them,
        /// which is exactly the step-pause-step-pause Sean saw on the road to Trinsic.
        ///
        /// Not base.TransformMoveDelay either: SpeedInfo's version clamps toward MaxDelayWild (0.8)
        /// for any uncontrolled creature whose Stam is below StamMax, which would re-inflate a run
        /// the moment a bot took damage. The four values SetPace writes are the engine's own
        /// constants, and the engine's own DoMoveImpl decides the run flag from them.
        /// </summary>
        public override double TransformMoveDelay(double delay)
        {
            if (IsCommuting)
            {
                return delay;
            }

            return base.TransformMoveDelay(delay);
        }
    }
}
