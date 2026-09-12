// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// IBotActor.cs - what a bot is, to code that is not allowed to know what a bot is.
//
// THIS USED TO LIVE IN BotAI.cs, which the PlayerMobile class swap deleted along with the AI it
// was named after. The interface long outlived its neighbour: nothing here is about an AI, and
// three of its consumers are in Core, which may not name a bot class at all.
//
// It is the one handle Core/Navigation has on a bot:
//
//   NavWalkFailures.Describe        labels a mobile in a failure record
//   NavWalkFailures.MayBotPass      answers "may this mover walk through this occupant?" - and
//                                   asks IBotActor of BOTH. Of the occupant first, because since
//                                   the swap a bot also satisfies the PlayerMobile test that
//                                   method used to call THE ONE REFUSAL; and of the mover, because
//                                   from 12 September 2026 a bot passes a live real player and the
//                                   walk audit's BaseCreature probe still does not
//   NavWalker.DescribeMobiles       labels the occupants in a wedge log, same ordering, same reason
//   NavActor.NavPlayerActor         reads the step pace off it
//
// Keep it to what Core genuinely needs. Everything else about a bot belongs on PlayerBot or in a
// behaviour, where Core cannot see it and does not want to.

namespace Server.Custom
{
    /// <summary>
    /// A bot, as the navigation layer sees one.
    /// </summary>
    public interface IBotActor
    {
        /// <summary>
        /// True while something is steering this actor along a route.
        ///
        /// The same contract IDailyLifeActor carries, kept separate because a bot is not a
        /// daily-life actor and the two will diverge.
        ///
        /// It used to have a second job: BotAI.DoActionWander read it to stand aside while the
        /// walker was driving, because a BaseCreature had a stock wander tick underneath it that
        /// would otherwise fight the walker for the move gate. A PlayerMobile has no such tick, so
        /// that reader is gone and this is now read by the step census (to tell a commuting step
        /// from a drifting one) and set by the four travelling behaviours.
        /// </summary>
        bool Commuting { get; set; }

        /// <summary>
        /// Seconds per step - the pace this actor was given, not a pace it negotiated.
        ///
        /// On a BaseCreature this job was done by BaseCreature.CurrentSpeed, which BaseAI read on
        /// every step and which restarted the AI timer when it changed. A PlayerMobile has neither
        /// the property nor the timer, so the pace became a plain field on the bot and this is how
        /// NavPlayerActor reads it without naming the class that holds it.
        /// </summary>
        double StepDelaySeconds { get; }
    }
}
