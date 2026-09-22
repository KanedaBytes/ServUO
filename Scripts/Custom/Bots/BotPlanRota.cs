// -----------------------------------------------------------------------------
// BotPlanRota.cs - the route-plan budget, served round-robin.
//
// Ours; uo-offline has no admission gate at all (SCALE.md, Ramp 2). Pure - no mobiles, no timers -
// so PlanFixtures can drive it with strings from [CoreSmoke and prove nobody starves.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Who gets a plan this pass, and where the next pass starts.
    ///
    /// THE ORDER USED TO BE THE LIVEREGISTRY'S, AND THAT STARVED THE NEWEST BOTS. The budget was
    /// granted first come, first served down a list that new bots are appended to, and it is
    /// refused most of the time by design (SCALE.md). So the bots near the head re-asked and were
    /// served every pass, and the ones at the tail - which is always where a probe's bots are -
    /// never got a plan once refusals climbed with uptime: 79%, 86%, then 95% on boot 894f75ab,
    /// and Bots.Shift and Bots.Life failed for it (WORK-INVESTIGATION.md, status block).
    ///
    /// Now each pass STARTS WHERE THE LAST ONE STOPPED: at the first bot refused last pass, found by
    /// identity so a bot logging out ahead of it does not shift the start. The rate is untouched -
    /// PlansFor still decides how many - only who is first in line changes. Every bot that asks is
    /// served within ceil(askers / budget) passes, whatever order it was created in.
    /// </summary>
    public sealed class BotPlanRota<T> where T : class
    {
        // How often the wait ledger forgets bots that have left the order or stopped asking. Cheap
        // either way; the point is only that a refused bot is not held for the life of the boot.
        private const int PruneEveryPasses = 30;

        private int _start;
        private T _resume;

        private int _left;
        private long _pass;

        private T _firstRefused;
        private int _firstRefusedIndex = -1;

        // Each waiting bot's first refusal of its current run of refusals, and its latest one. A
        // wait is a run of CONSECUTIVE refused passes: a bot refused as a Traveler and then made
        // Idle by the lifecycle has stopped asking, and the gap before it next asks is not time
        // spent in the queue. Counting it read 95 passes on a live boot where a round of the rota
        // at that budget was under 50.
        private readonly Dictionary<T, Waiting> _waiting = new Dictionary<T, Waiting>();

        private struct Waiting
        {
            public long Since;
            public long Last;
        }

        public long Granted { get; private set; }

        public long Refused { get; private set; }

        /// <summary>
        /// The longest a bot waited, in passes, from its first refusal to its grant, asking every pass
        /// in between, since the last Reset. The live evidence that the rota is fair: first come,
        /// first served had no bound.
        /// </summary>
        public long LongestWait { get; private set; }

        /// <summary>Plans left in the pass in progress.</summary>
        public int Left
        {
            get { return _left; }
        }

        /// <summary>
        /// Open a pass over <paramref name="order"/> with <paramref name="budget"/> plans, and say
        /// which index to tick first. The caller walks (start + step) % count.
        /// </summary>
        public int BeginPass(IList<T> order, int budget)
        {
            _pass++;
            _left = budget;
            _firstRefused = null;
            _firstRefusedIndex = -1;

            int count = order == null ? 0 : order.Count;

            if (count <= 0)
            {
                _start = 0;
                return 0;
            }

            int found = _resume == null ? -1 : order.IndexOf(_resume);

            // The bot we meant to start at has gone (logged out, deleted): start at the index it
            // held. If bots ahead of it went too, that skips a few waiting bots past the start -
            // they are reached at the end of this round instead, and are still queued.
            _start = found >= 0 ? found : _start % count;

            if (found < 0)
            {
                _resume = null;
            }

            if (_pass % PruneEveryPasses == 0 && _waiting.Count > 0)
            {
                Prune(order);
            }

            return _start;
        }

        /// <summary>
        /// Grant or refuse one plan to <paramref name="item"/>, ticked at <paramref name="index"/>.
        /// A null item (a caller outside the pass) is counted but has no place in the queue.
        /// </summary>
        public bool TryTake(T item, int index)
        {
            if (_left <= 0)
            {
                Refused++;

                if (item != null)
                {
                    if (_firstRefused == null)
                    {
                        _firstRefused = item;
                        _firstRefusedIndex = index;
                    }

                    Waiting waiting;

                    // A bot that skipped a pass without asking starts a new run.
                    if (!_waiting.TryGetValue(item, out waiting) || _pass - waiting.Last > 1)
                    {
                        waiting.Since = _pass;
                    }

                    waiting.Last = _pass;
                    _waiting[item] = waiting;
                }

                return false;
            }

            _left--;
            Granted++;

            Waiting was;

            if (item != null && _waiting.TryGetValue(item, out was))
            {
                _waiting.Remove(item);

                // Only a run that reached the pass before this one ends in this grant.
                long wait = _pass - was.Last <= 1 ? _pass - was.Since : 0;

                if (wait > LongestWait)
                {
                    LongestWait = wait;
                }
            }

            return true;
        }

        /// <summary>
        /// Close the pass. If anybody was refused, the next pass starts with the first of them;
        /// if nobody was, every asker was served and the start does not matter, so it stays.
        /// </summary>
        public void EndPass()
        {
            if (_firstRefused != null)
            {
                _resume = _firstRefused;
                _start = _firstRefusedIndex >= 0 ? _firstRefusedIndex : _start;
            }
        }

        /// <summary>Open a fresh ledger window. The queue itself - the start and the waits - is kept.</summary>
        public void Reset()
        {
            Granted = 0;
            Refused = 0;
            LongestWait = 0;
        }

        private void Prune(IList<T> order)
        {
            var present = new HashSet<T>(order);
            var gone = new List<T>();

            // Gone from the order, or stopped asking: either way the run is over.
            foreach (KeyValuePair<T, Waiting> entry in _waiting)
            {
                if (!present.Contains(entry.Key) || _pass - entry.Value.Last > 1)
                {
                    gone.Add(entry.Key);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                _waiting.Remove(gone[i]);
            }
        }
    }
}
