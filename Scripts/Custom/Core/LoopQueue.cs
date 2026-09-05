using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Custom
{
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
    /// </summary>
    public static class LoopQueue
    {
        private static readonly CustomLogger Log = CustomLogger.For("LoopQueue");

        // Static field: usable from the moment the type loads, which is well before
        // Initialize() starts the drain timer. Anything posted early simply waits.
        private static readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();

        private static long _totalProcessed;
        private static long _totalFaulted;
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
        /// world has loaded. Returns immediately - use TryPostAndWait if you need the result.
        /// </summary>
        public static void Post(Action work)
        {
            if (work == null)
            {
                return;
            }

            _pending.Enqueue(work);

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
        }

        /// <summary>
        /// Runs work on the game thread and waits for its result.
        ///
        /// Intended for a background thread servicing a request (the admin API). Returns false
        /// on timeout or if the work threw, with the reason in <paramref name="error"/>.
        /// </summary>
        public static bool TryPostAndWait<T>(Func<T> work, TimeSpan timeout, out T result, out string error)
        {
            result = default(T);
            error = null;

            if (work == null)
            {
                error = "No work was supplied.";
                return false;
            }

            // Calling this from the game thread would deadlock: the drain that would complete
            // the task cannot run until we return. Execute inline instead.
            if (IsOnGameThread)
            {
                try
                {
                    result = work();
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            // RunContinuationsAsynchronously keeps the waiter's continuation off the game
            // thread. TaskCompletionSource rather than a ManualResetEventSlim on purpose: if we
            // time out and the loop runs the work anyway, setting a result nobody awaits is
            // harmless, whereas signalling a *disposed* reset event would throw on the game
            // thread from a request that had already given up.
            var completion = new TaskCompletionSource<Outcome<T>>(TaskCreationOptions.RunContinuationsAsynchronously);

            Post(
                () =>
                {
                    try
                    {
                        completion.TrySetResult(new Outcome<T>(work(), null));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetResult(new Outcome<T>(default(T), ex.Message));
                    }
                });

            if (!completion.Task.Wait(timeout))
            {
                error = String.Format("The game loop did not respond within {0}.", timeout);
                return false;
            }

            Outcome<T> outcome = completion.Task.Result;

            if (outcome.Error != null)
            {
                error = outcome.Error;
                return false;
            }

            result = outcome.Value;
            return true;
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
            Action work;

            while (count < budget && _pending.TryDequeue(out work))
            {
                ++count;

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _totalFaulted);
                    Volatile.Write(ref _lastFault, ex.GetType().Name + ": " + ex.Message);
                    _lastFaultUtc = DateTime.UtcNow;

                    Log.Error(ex, "Queued callback faulted.");
                }

                Interlocked.Increment(ref _totalProcessed);
            }
        }

        private static HealthResult BuildHealthResult()
        {
            int queued = QueuedCount;
            long processed = TotalProcessed;
            long faulted = UnacknowledgedFaults;

            string detail = String.Format(
                "queued {0}, processed {1}, faulted {2} new / {3} total, peak depth {4}, running {5}",
                queued,
                processed,
                faulted,
                TotalFaulted,
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

        private sealed class Outcome<T>
        {
            public readonly T Value;
            public readonly string Error;

            public Outcome(T value, string error)
            {
                Value = value;
                Error = error;
            }
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
