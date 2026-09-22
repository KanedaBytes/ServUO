using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Custom
{
    /// <summary>What a caller may conclude about a job it handed to the game thread.</summary>
    public enum LoopOutcome
    {
        /// <summary>The work ran to the end and returned.</summary>
        Completed,

        /// <summary>The work ran and threw. The error text says what.</summary>
        Faulted,

        /// <summary>
        /// The work NEVER STARTED: its waiter timed out and withdrew it while it was still queued,
        /// or the shard shut down before the drain reached it. Nothing happened, so it is safe to
        /// retry.
        /// </summary>
        NotRun,

        /// <summary>
        /// The waiter gave up while the work was RUNNING, or nothing can say whether it ran. It may
        /// have completed after the wait expired. Retrying as though nothing happened is unsafe;
        /// re-query the state it would have changed.
        /// </summary>
        Unknown
    }

    /// <summary>
    /// One queued callback and what became of it.
    ///
    /// The state is one int moved by compare-and-swap, so a waiter withdrawing a job and the drain
    /// starting it can race and exactly one of them wins: Queued goes to Running (the drain won,
    /// the work runs), to Abandoned (a timed-out waiter won, the work never runs), or to Orphaned
    /// (the shard is shutting down, the work never runs). Running goes to Completed or Faulted.
    /// </summary>
    public sealed class LoopJob
    {
        internal const int QueuedState = 0;
        internal const int RunningState = 1;
        internal const int CompletedState = 2;
        internal const int FaultedState = 3;
        internal const int AbandonedState = 4;
        internal const int OrphanedState = 5;

        private int _state;

        internal readonly Action Work;

        /// <summary>Completed by the drain (or the orphaner) so a waiter wakes. Null for fire-and-forget.</summary>
        internal readonly TaskCompletionSource<LoopOutcome> Completion;

        internal string Error;

        internal LoopJob(Action work, TaskCompletionSource<LoopOutcome> completion)
        {
            Work = work;
            Completion = completion;
        }

        internal int State { get { return Volatile.Read(ref _state); } }

        public bool IsQueued { get { return State == QueuedState; } }

        public bool IsRunning { get { return State == RunningState; } }

        /// <summary>
        /// The outcome as this handle can vouch for it. Queued and Running read as Unknown: from
        /// the outside, a job that has not finished has no outcome yet, and a caller that stopped
        /// waiting is in exactly that position.
        /// </summary>
        public LoopOutcome Outcome
        {
            get
            {
                switch (State)
                {
                    case CompletedState:
                        return LoopOutcome.Completed;
                    case FaultedState:
                        return LoopOutcome.Faulted;
                    case AbandonedState:
                    case OrphanedState:
                        return LoopOutcome.NotRun;
                    default:
                        return LoopOutcome.Unknown;
                }
            }
        }

        /// <summary>The exception text when Faulted; the reason when NotRun; null otherwise.</summary>
        public string Detail { get { return Volatile.Read(ref Error); } }

        /// <summary>
        /// Withdraws the job if it has not started. True means it never ran and never will; false
        /// means the drain got there first and the job is running or already finished.
        /// </summary>
        public bool TryAbandon()
        {
            return Transition(QueuedState, AbandonedState);
        }

        internal bool TryBeginRun()
        {
            return Transition(QueuedState, RunningState);
        }

        internal bool TryOrphan()
        {
            return Transition(QueuedState, OrphanedState);
        }

        internal void Finish(bool faulted)
        {
            Volatile.Write(ref _state, faulted ? FaultedState : CompletedState);
        }

        private bool Transition(int from, int to)
        {
            return Interlocked.CompareExchange(ref _state, to, from) == from;
        }
    }

    /// <summary>
    /// The ServUO equivalent of ModernUO's Core.LoopContext.Post.
    ///
    /// ServUO has no general-purpose way to run work on the game thread (CLAUDE.md section 4).
    /// Timer.DelayCall is only nominally thread-safe: under -profile, TimerProfile.Acquire
    /// touches an unsynchronized Dictionary, and Timer.Slice() holds lock(m_Queue) across the
    /// entire callback batch, so an off-thread DelayCall can block behind tick execution.
    ///
    /// The house rule is therefore: a background thread enqueues onto this ConcurrentQueue, and
    /// one repeating Timer on the game thread drains it. Background threads never call
    /// Timer.DelayCall and never touch world state directly.
    ///
    /// Post() is safe before the world has loaded - work is queued, never dropped.
    ///
    /// THE OUTCOME CONTRACT (21 September 2026, REVIEW.md section 2)
    /// ---------------------------------------------------------------
    /// A caller that handed work to this queue - or dropped a request token that ends up here -
    /// may conclude one of four things, and nothing else:
    ///
    ///   Completed  the work ran and returned. An ack with ok:true means this and only this.
    ///   Faulted    the work ran and threw. An ack with ok:false means it failed, and where.
    ///   NotRun     the work never started: the caller withdrew it after a timeout while it was
    ///              still queued, or the shard shut down first. Nothing happened. Safe to retry.
    ///   Unknown    the caller stopped waiting while the work was running, or the process ended
    ///              with it running, or no ack ever came. It may have completed after the caller
    ///              gave up. NEVER report this as done, NEVER as failed, and never retry it as
    ///              though nothing happened - re-query the state it would have changed instead.
    ///
    /// The cases that used to be misreported, and what they are now:
    ///   - A TryPostAndWait timeout used to return "did not respond" while the work still ran when
    ///     the loop next drained. Now the waiter withdraws the job by compare-and-swap: if it was
    ///     still queued, NotRun; if the drain had already started it, Unknown.
    ///   - A save freezes the loop (Drain returns while World.Saving). Queued work waits and runs
    ///     afterwards; a waiter whose timeout expires inside the freeze withdraws its job, NotRun.
    ///     The pre-save flush runs whatever is queued at BeforeWorldSave - a withdrawn job is skipped.
    ///   - A shutdown orphans every job still queued (EventSink.Shutdown), wakes their waiters
    ///     with NotRun, and says on the console how many there were. Drain runs nothing after
    ///     Core.Closing. A crash raises no Shutdown event: waiters time out and withdraw, which
    ///     is NotRun too, because the job is still Queued when they look.
    ///   - No ack from the bridge is Unknown. The bridge and the editor say so in those words.
    ///
    /// Owners of long-running work that reports through a Data/Live file - nav-adopt, walk-audit -
    /// apply the same vocabulary to their status field: "done", "failed" with the error, and
    /// "unknown" when the shard stopped while they ran (LiveStatus.cs marks a stale file at boot).
    /// </summary>
    public static class LoopQueue
    {
        private static readonly CustomLogger Log = CustomLogger.For("LoopQueue");

        // Static field: usable from the moment the type loads, which is well before
        // Initialize() starts the drain timer. Anything posted early simply waits.
        private static readonly ConcurrentQueue<LoopJob> _pending = new ConcurrentQueue<LoopJob>();

        private static long _totalProcessed;
        private static long _totalFaulted;
        private static long _totalWithdrawn;
        private static long _totalOrphaned;
        private static long _acknowledgedFaults;
        private static int _maxObservedDepth;
        private static string _lastFault;
        private static DateTime _lastFaultUtc;

        private static DrainTimer _timer;
        private static int _coreThreadId = -1;

        /// <summary>Maximum callbacks drained per tick. Config: Custom.LoopQueueBudget</summary>
        public static int Budget { get; private set; }

        /// <summary>Interval between drain passes. Config: Custom.LoopQueueIntervalMs</summary>
        public static TimeSpan Interval { get; private set; }

        public static int QueuedCount { get { return _pending.Count; } }

        public static long TotalProcessed { get { return Interlocked.Read(ref _totalProcessed); } }

        public static long TotalFaulted { get { return Interlocked.Read(ref _totalFaulted); } }

        /// <summary>Jobs a timed-out waiter withdrew before they ran (NotRun).</summary>
        public static long TotalWithdrawn { get { return Interlocked.Read(ref _totalWithdrawn); } }

        /// <summary>Jobs still queued when the shard shut down (NotRun).</summary>
        public static long TotalOrphaned { get { return Interlocked.Read(ref _totalOrphaned); } }

        /// <summary>Faults since the last AcknowledgeFaults() - what health status is based on.</summary>
        public static long UnacknowledgedFaults
        {
            get { return Interlocked.Read(ref _totalFaulted) - Interlocked.Read(ref _acknowledgedFaults); }
        }

        public static int MaxObservedDepth { get { return Volatile.Read(ref _maxObservedDepth); } }

        public static string LastFault { get { return Volatile.Read(ref _lastFault); } }

        public static DateTime LastFaultUtc { get { return _lastFaultUtc; } }

        public static bool Running { get { return _timer != null && _timer.Running; } }

        /// <summary>
        /// True when the caller is already on the game thread. Recorded during Initialize(),
        /// which ScriptCompiler.Invoke runs on the core thread.
        /// </summary>
        public static bool IsOnGameThread
        {
            get
            {
                int id = Volatile.Read(ref _coreThreadId);
                return id != -1 && Thread.CurrentThread.ManagedThreadId == id;
            }
        }

        public static void Configure()
        {
            Budget = Math.Max(1, Config.Get("Custom.LoopQueueBudget", 256));

            int intervalMs = Math.Max(1, Config.Get("Custom.LoopQueueIntervalMs", 50));
            Interval = TimeSpan.FromMilliseconds(intervalMs);

            // Flush pending work into the world before it is snapshotted, so posted mutations
            // land in the save instead of sitting in memory until the next tick. At this point
            // the world is quiescent and NetState is paused, and the save strategy has not run.
            EventSink.BeforeWorldSave += OnBeforeWorldSave;

            // Whatever is still queued when the shard stops never runs. Say so to the waiters and
            // to the console, rather than letting each waiter find out by timing out.
            EventSink.Shutdown += OnShutdown;
        }

        public static void Initialize()
        {
            // ScriptCompiler.Invoke("Initialize") runs on the core thread, so this is the
            // game thread's id.
            Volatile.Write(ref _coreThreadId, Thread.CurrentThread.ManagedThreadId);

            _timer = new DrainTimer(Interval);
            _timer.Start();

            HealthCheck.Register("LoopQueue", BuildHealthResult);

            Log.Info(
                "Started. Budget {0}/tick, interval {1}ms, {2} item(s) queued during boot.",
                Budget,
                (int)Interval.TotalMilliseconds,
                _pending.Count);
        }

        /// <summary>
        /// Queues work to run on the game thread. Safe from any thread, and safe before the
        /// world has loaded. Returns immediately - use TryPostAndWait if you need the result, or
        /// Submit if you need to know afterwards whether it ran.
        /// </summary>
        public static void Post(Action work)
        {
            Submit(work);
        }

        /// <summary>
        /// Queues work and returns its handle, whose Outcome says afterwards whether it ran. Null
        /// when there was nothing to queue.
        /// </summary>
        public static LoopJob Submit(Action work)
        {
            return Submit(work, null);
        }

        private static LoopJob Submit(Action work, TaskCompletionSource<LoopOutcome> completion)
        {
            if (work == null)
            {
                return null;
            }

            var job = new LoopJob(work, completion);

            _pending.Enqueue(job);

            int depth = _pending.Count;

            // Diagnostic only; a lost race here costs nothing.
            if (depth > Volatile.Read(ref _maxObservedDepth))
            {
                Volatile.Write(ref _maxObservedDepth, depth);
            }

            try
            {
                // The main loop blocks on an AutoResetEvent when idle. Core.Set() is a plain
                // AutoResetEvent.Set() and is safe from any thread - it is exactly what
                // Timer.cs does after queuing due timers.
                Core.Set();
            }
            catch
            {
                // The drain timer will pick the work up regardless.
            }

            return job;
        }

        /// <summary>
        /// Runs work on the game thread and waits for its result.
        ///
        /// Intended for a background thread servicing a request (the admin API). Returns false
        /// on timeout or if the work threw, with the reason in <paramref name="error"/>. Callers
        /// that must act on a timeout use the overload that also reports the LoopOutcome.
        /// </summary>
        public static bool TryPostAndWait<T>(Func<T> work, TimeSpan timeout, out T result, out string error)
        {
            LoopOutcome outcome;
            return TryPostAndWait(work, timeout, out result, out error, out outcome);
        }

        /// <summary>
        /// As above, and says which of the four outcomes the wait ended in. True only for Completed.
        /// On a timeout the job is withdrawn if it has not started (NotRun, safe to retry); if the
        /// drain had already begun it, the outcome is Unknown and the result must not be assumed
        /// either way.
        /// </summary>
        public static bool TryPostAndWait<T>(
            Func<T> work, TimeSpan timeout, out T result, out string error, out LoopOutcome outcome)
        {
            result = default(T);
            error = null;
            outcome = LoopOutcome.Unknown;

            if (work == null)
            {
                error = "No work was supplied.";
                outcome = LoopOutcome.NotRun;
                return false;
            }

            // Calling this from the game thread would deadlock: the drain that would complete
            // the task cannot run until we return. Execute inline instead.
            if (IsOnGameThread)
            {
                try
                {
                    result = work();
                    outcome = LoopOutcome.Completed;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    outcome = LoopOutcome.Faulted;
                    return false;
                }
            }

            // RunContinuationsAsynchronously keeps the waiter's continuation off the game
            // thread. TaskCompletionSource rather than a ManualResetEventSlim on purpose: if we
            // time out and the loop runs the work anyway, setting a result nobody awaits is
            // harmless, whereas signalling a *disposed* reset event would throw on the game
            // thread from a request that had already given up.
            var completion = new TaskCompletionSource<LoopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var box = new ResultBox<T>();

            LoopJob job = Submit(
                () =>
                {
                    box.Value = work();
                },
                completion);

            if (!completion.Task.Wait(timeout))
            {
                outcome = JudgeTimeout(job, completion.Task);

                switch (outcome)
                {
                    case LoopOutcome.NotRun:
                        error = String.Format(
                            "The game loop did not pick the job up within {0}; it was withdrawn and did not run.",
                            timeout);
                        return false;

                    case LoopOutcome.Unknown:
                        error = String.Format(
                            "The job was still running when the wait of {0} expired; its outcome is unknown.",
                            timeout);
                        return false;
                }
            }

            outcome = completion.Task.Result;

            if (outcome != LoopOutcome.Completed)
            {
                error = job.Detail ?? outcome.ToString();
                return false;
            }

            result = box.Value;
            return true;
        }

        /// <summary>
        /// What a waiter that has just timed out may conclude. Withdraws the job if it can; if the
        /// drain beat it, checks whether the job has in fact finished in the meantime (the drain
        /// completes the task BEFORE it marks the state, so a finished state means the task is
        /// readable) and otherwise reports Unknown. Internal so SaveIntegrity's fixture can drive
        /// it with a hand-built job in each state without threads.
        /// </summary>
        internal static LoopOutcome JudgeTimeout(LoopJob job, Task<LoopOutcome> task)
        {
            if (job.TryAbandon())
            {
                job.Error = "withdrawn by its waiter after a timeout, before it ran";
                Interlocked.Increment(ref _totalWithdrawn);
                return LoopOutcome.NotRun;
            }

            if (task != null && task.IsCompleted)
            {
                return task.Result;
            }

            return LoopOutcome.Unknown;
        }

        /// <summary>For the fixture: a job that is never queued, to be walked through its states.</summary>
        internal static LoopJob CreateDetached(Action work, TaskCompletionSource<LoopOutcome> completion)
        {
            return new LoopJob(work, completion);
        }

        /// <summary>
        /// Marks all faults recorded so far as seen, so health reports only NEW faults.
        ///
        /// This is an operator action ("I have read those errors"), and it is what stops
        /// [CoreSmoke's own deliberate fault probe from pinning the shard at Warn forever.
        /// TotalFaulted still counts everything, for the record.
        /// </summary>
        public static void AcknowledgeFaults()
        {
            Interlocked.Exchange(ref _acknowledgedFaults, Interlocked.Read(ref _totalFaulted));
        }

        private static void OnBeforeWorldSave(BeforeWorldSaveEventArgs e)
        {
            try
            {
                Drain(true);
            }
            catch (Exception ex)
            {
                // An exception escaping here is rethrown by World.Save as
                // "FATAL: Exception in EventSink.BeforeWorldSave" and kills the shard.
                Log.Error(ex, "Pre-save drain faulted.");
            }
        }

        private static void OnShutdown(ShutdownEventArgs e)
        {
            try
            {
                int orphaned = 0;

                // A ConcurrentQueue enumeration is a snapshot and thread-safe. Nothing is dequeued:
                // Drain returns as soon as Core.Closing is true, so an orphaned job stays where it
                // is and is never started.
                foreach (LoopJob job in _pending)
                {
                    if (!job.TryOrphan())
                    {
                        continue;
                    }

                    orphaned++;
                    job.Error = "the shard shut down before it ran";

                    if (job.Completion != null)
                    {
                        job.Completion.TrySetResult(LoopOutcome.NotRun);
                    }
                }

                Interlocked.Add(ref _totalOrphaned, orphaned);

                if (orphaned > 0)
                {
                    Log.Warn(
                        "{0} job(s) were still queued at shutdown and did not run. Their callers were " +
                        "told NotRun; anything that dropped a token and got no ack has an unknown outcome.",
                        orphaned);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not orphan the queue at shutdown.");
            }
        }

        /// <summary>Drains queued work on the game thread.</summary>
        /// <param name="force">
        /// Bypass the World.Saving guard. Only the pre-save flush passes true, where the world
        /// is quiescent by design.
        /// </param>
        private static void Drain(bool force)
        {
            if (Core.Crashed || Core.Closing)
            {
                return;
            }

            if (!force && (World.Loading || World.Saving))
            {
                // Leave everything queued. Dropping work here would silently lose admin API
                // mutations that arrived during a save.
                return;
            }

            int budget = force ? Int32.MaxValue : Budget;
            int count = 0;
            LoopJob job;

            while (count < budget && _pending.TryDequeue(out job))
            {
                if (!job.TryBeginRun())
                {
                    // Withdrawn by its waiter, or orphaned. It never runs, and it does not count
                    // against this tick's budget either - nothing was done.
                    continue;
                }

                ++count;

                bool faulted = false;

                try
                {
                    job.Work();
                }
                catch (Exception ex)
                {
                    faulted = true;
                    job.Error = ex.GetType().Name + ": " + ex.Message;

                    Interlocked.Increment(ref _totalFaulted);
                    Volatile.Write(ref _lastFault, job.Error);
                    _lastFaultUtc = DateTime.UtcNow;

                    Log.Error(ex, "Queued callback faulted.");
                }

                // The task first, the state second: a waiter that sees a finished state may then
                // read the task's result without waiting again (see JudgeTimeout).
                if (job.Completion != null)
                {
                    job.Completion.TrySetResult(faulted ? LoopOutcome.Faulted : LoopOutcome.Completed);
                }

                job.Finish(faulted);

                Interlocked.Increment(ref _totalProcessed);
            }
        }

        private static HealthResult BuildHealthResult()
        {
            int queued = QueuedCount;
            long processed = TotalProcessed;
            long faulted = UnacknowledgedFaults;

            string detail = String.Format(
                "queued {0}, processed {1}, faulted {2} new / {3} total, withdrawn {4}, orphaned {5}, peak depth {6}, running {7}",
                queued,
                processed,
                faulted,
                TotalFaulted,
                TotalWithdrawn,
                TotalOrphaned,
                MaxObservedDepth,
                Running);

            if (!Running)
            {
                return HealthResult.Fail("Drain timer is not running. " + detail);
            }

            if (faulted > 0)
            {
                return HealthResult.Warn(
                    String.Format("{0} unacknowledged fault(s); last was {1}. {2}", faulted, LastFault, detail));
            }

            if (queued > Budget)
            {
                return HealthResult.Warn("Backlog exceeds one tick's budget. " + detail);
            }

            return HealthResult.Ok(detail);
        }

        private sealed class ResultBox<T>
        {
            public T Value;
        }

        private sealed class DrainTimer : Timer
        {
            public DrainTimer(TimeSpan interval)
                : base(TimeSpan.Zero, interval)
            {
                // Timer priority - not Interval - decides how often the timer thread even
                // examines this timer (Server/Timer.cs bucket delays). Without this an
                // interval shorter than the derived bucket's poll delay is silently coarsened.
                Priority = TimerPriority.EveryTick;
            }

            protected override void OnTick()
            {
                Drain(false);
            }
        }
    }
}
