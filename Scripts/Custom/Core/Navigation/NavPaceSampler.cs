using System;
using System.Collections.Generic;

using Server;

namespace Server.Custom
{
    /// <summary>
    /// Thirty seconds of one walker's stepping, measured rather than inferred.
    ///
    /// Attached to a NavWalker by [BotPace and fed from its Tick: every tick the timer delivered,
    /// every tick the NextMove gate turned away, and every MoveTo - whether the mobile actually
    /// moved, whether its Direction carried the running bit afterwards, and the two numbers that
    /// have to agree for a bot to move at the pace it was given: CurrentSpeed, which is what the
    /// pace system writes and the editor's panel prints, and TransformMoveDelay(CurrentSpeed),
    /// which is what BaseAI.DoMoveImpl advances NextMove by and derives the run flag from.
    ///
    /// It exists because the panel said "running, 200ms/step" of a bot that was visibly walking
    /// and pausing, and nothing in the tree could say which of the two was lying. The answer was
    /// neither: they were different numbers, and only one of them moves the bot.
    /// </summary>
    public sealed class NavPaceSampler
    {
        private readonly List<long> _ticks = new List<long>();
        private readonly List<long> _moves = new List<long>();

        private readonly long _startedAt = Core.TickCount;

        private int _gated;
        private int _attempts;
        private int _running;
        private int _delayMs = -1;
        private int _speedMs = -1;
        private long _nextMoveDelta;

        // Per moved step: the delay the engine used and whether the bit was set. The bit is
        // decided from the delay (BaseAI.DoMoveImpl), so "0 of 8 running" is only a finding when
        // the 8 were at run pace; at walk pace it is correct. The first version of this sampler
        // kept only the last delay it saw, and could not tell those apart afterwards.
        private int _runPaceSteps;
        private int _runPaceRunning;
        private int _walkPaceSteps;
        private int _walkPaceRunning;

        // Recovery steps: the ladder's Sidestep moves with Mobile.Move directly, outside
        // DoMoveImpl, so they carry no bit and no pace. Counted apart so a wedged bot's handful
        // of moves are labelled for what they were.
        private int _sidesteps;

        /// <summary>The walker's timer delivered a tick to this walker.</summary>
        public void TickSeen()
        {
            _ticks.Add(Core.TickCount);
        }

        /// <summary>The tick was turned away at the NextMove gate without asking for a step.</summary>
        public void Gated()
        {
            _gated++;
        }

        /// <summary>
        /// MoveTo was called. `moved` is whether the mobile's location changed, `running` whether
        /// its Direction carries Direction.Running afterwards, `nextMoveDelta` how far ahead of now
        /// NextMove was left, `delayMs` the delay the engine derived from the pace, `speedMs` the
        /// pace itself.
        /// </summary>
        public void Attempted(bool moved, bool running, long nextMoveDelta, int delayMs, int speedMs)
        {
            _attempts++;
            _delayMs = delayMs;
            _speedMs = speedMs;
            _nextMoveDelta = nextMoveDelta;

            if (!moved)
            {
                return;
            }

            _moves.Add(Core.TickCount);

            if (running)
            {
                _running++;
            }

            // Run pace is a delay under the engine's walk delay for the body in question -
            // DoMoveImpl's own test - and the mobile's mount state is not known here, so a delay
            // under WalkFoot counts as run pace for either: 100 and 200 are runs, 400 is a walk.
            bool runPace = delayMs >= 0 && delayMs < Mobile.WalkFoot;

            if (runPace)
            {
                _runPaceSteps++;

                if (running)
                {
                    _runPaceRunning++;
                }
            }
            else
            {
                _walkPaceSteps++;

                if (running)
                {
                    _walkPaceRunning++;
                }
            }
        }

        /// <summary>The ladder's Sidestep moved the mobile one tile, outside the pace clock.</summary>
        public void Sidestepped()
        {
            _sidesteps++;
        }

        /// <summary>The report, one fact per line, for the caller, the console and the bot's log.</summary>
        public List<string> Report(TimeSpan tickInterval)
        {
            var lines = new List<string>();
            long elapsed = Core.TickCount - _startedAt;

            lines.Add(String.Format(
                "sampled {0:0.0}s: {1} walker tick(s), mean {2}ms apart (timer set to {3}ms)",
                elapsed / 1000.0,
                _ticks.Count,
                MeanGap(_ticks),
                (int)tickInterval.TotalMilliseconds));

            lines.Add(String.Format(
                "pace given (CurrentSpeed) {0}ms/step; delay the engine used (TransformMoveDelay) {1}ms; NextMove last left {2}ms ahead",
                _speedMs,
                _delayMs,
                _nextMoveDelta));

            lines.Add(String.Format(
                "{0} MoveTo call(s): {1} moved, {2} did not; {3} tick(s) turned away at the NextMove gate",
                _attempts,
                _moves.Count,
                _attempts - _moves.Count,
                _gated));

            if (_moves.Count >= 2)
            {
                var gaps = new List<long>(_moves.Count - 1);

                for (int i = 1; i < _moves.Count; i++)
                {
                    gaps.Add(_moves[i] - _moves[i - 1]);
                }

                gaps.Sort();

                lines.Add(String.Format(
                    "interval between steps: min {0}ms, median {1}ms, mean {2}ms, max {3}ms",
                    gaps[0],
                    gaps[gaps.Count / 2],
                    MeanGap(_moves),
                    gaps[gaps.Count - 1]));

                lines.Add(String.Format(
                    "tiles per second: {0:0.0}",
                    _moves.Count * 1000.0 / Math.Max(1, _moves[_moves.Count - 1] - _moves[0])));
            }
            else
            {
                lines.Add("fewer than two steps: nothing to measure an interval from");
            }

            lines.Add(String.Format(
                "running bit set on {0} of {1} step(s) ({2}%)",
                _running,
                _moves.Count,
                _moves.Count == 0 ? 0 : _running * 100 / _moves.Count));

            lines.Add(String.Format(
                "of those, {0} at run pace ({1} with the bit) and {2} at walk pace ({3} with the bit); "
                + "{4} sidestep(s) by the recovery ladder, outside the pace clock",
                _runPaceSteps,
                _runPaceRunning,
                _walkPaceSteps,
                _walkPaceRunning,
                _sidesteps));

            if (_runPaceSteps > 0 && _runPaceRunning < _runPaceSteps)
            {
                lines.Add(String.Format(
                    "FINDING: {0} run-pace step(s) moved without the running bit",
                    _runPaceSteps - _runPaceRunning));
            }

            if (_delayMs >= 0 && _speedMs >= 0)
            {
                lines.Add(_delayMs == _speedMs
                    ? String.Format("engine delay {0}ms matches the pace: the bot steps at the pace it was given", _delayMs)
                    : String.Format(
                        "MISMATCH: engine delay {0}ms against a pace of {1}ms - the bot steps at the engine's number, and the run flag is decided from it too",
                        _delayMs,
                        _speedMs));
            }

            return lines;
        }

        private static long MeanGap(List<long> stamps)
        {
            if (stamps.Count < 2)
            {
                return 0;
            }

            return (stamps[stamps.Count - 1] - stamps[0]) / (stamps.Count - 1);
        }
    }
}
