using System;

using Server.Items;

namespace Server.Custom
{
    /// <summary>
    /// Tracks the town's day phase and raises an event when it changes.
    ///
    /// In-game time is a pure function of DateTime.UtcNow and a fixed 1997 epoch: 5 real seconds
    /// per UO minute, so a UO hour is 5 real minutes and a UO day is 2 real hours. Nothing here
    /// is persisted - the phase is always recomputed from the clock.
    /// </summary>
    public static class DayCycleSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        /// <summary>
        /// Mirrors LightCycle's own 5-second poll, which is exactly one UO minute. The dawn and
        /// dusk ramps are only 10 real minutes each, so this is fine-grained enough to land
        /// inside them.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5.0);

        private static DayPhase _current = DayPhase.Day;
        private static bool _started;
        private static bool _hasOverride;
        private static DayPhase _override;

        /// <summary>
        /// Raised when the phase changes, including when a staff override forces one.
        ///
        /// ServUO has no [GeneratedEvent]/[OnEvent]; this plain static event is the equivalent.
        /// </summary>
        public static event Action<DayPhase, DayPhase> PhaseChanged;

        public static DayPhase Current
        {
            get { return _current; }
        }

        public static bool HasOverride
        {
            get { return _hasOverride; }
        }

        /// <summary>
        /// CallPriority -10 so the phase is settled before the tavern, watch, townsfolk and shop
        /// systems read Current in their own untagged (priority 0) Initialize.
        ///
        /// NOTE the number: the ModernUO original used [CallPriority(10)] against a default of
        /// 50. ServUO's default is 0, so copying that number across would run this AFTER its
        /// consumers - the exact trap CLAUDE.md section 3 warns about.
        /// </summary>
        [CallPriority(-10)]
        public static void Initialize()
        {
            // A forced phase must never outlive the session that asked for it. LightCycle's
            // override is a static field so it could not survive a restart anyway; clearing it
            // here is the difference between "cannot happen" and "cannot happen, and this is the
            // line that guarantees it".
            ClearOverride();

            _current = ComputePhase();
            _started = true;

            Log.Info("Day cycle started in {0} phase.", _current.ToFriendlyString());

            Timer.DelayCall(PollInterval, PollInterval, Poll);
        }

        /// <summary>
        /// The in-game hour at the town anchor.
        ///
        /// Clock.GetTime adds map.MapIndex * 320 minutes per facet and x / 16 minutes for
        /// longitude (Scripts/Items/Tools/Clocks.cs:93-109), so "the hour" differs by over seven
        /// hours across a single map. Sampling at the town's own anchor is what makes the
        /// schedule agree with the light level players standing there actually see.
        /// </summary>
        public static void GetAnchorTime(out int hours, out int minutes)
        {
            Point3D anchor;
            Map map;

            if (DailyLifeSystem.TryGetAnchor(out anchor, out map))
            {
                Clock.GetTime(map, anchor.X, anchor.Y, out hours, out minutes);
                return;
            }

            Clock.GetTime(null, 0, 0, out hours, out minutes);
        }

        private static DayPhase ComputePhase()
        {
            if (_hasOverride)
            {
                return _override;
            }

            int hours, minutes;
            GetAnchorTime(out hours, out minutes);

            return DayPhaseExtensions.FromHour(hours);
        }

        private static void Poll()
        {
            if (!_started)
            {
                return;
            }

            // Always recompute and compare. Never advance by increment: in-game time is derived
            // from DateTime.UtcNow, so an NTP step moves it by 12 minutes per real minute and can
            // run backwards, and downtime is not paused - a three-hour outage advances the world
            // a day and a half.
            DayPhase phase = ComputePhase();

            if (phase == _current)
            {
                return;
            }

            DayPhase old = _current;
            _current = phase;

            Log.Info("Day phase {0} -> {1}.", old.ToFriendlyString(), phase.ToFriendlyString());

            Raise(old, phase);
        }

        private static void Raise(DayPhase oldPhase, DayPhase newPhase)
        {
            Action<DayPhase, DayPhase> handler = PhaseChanged;

            if (handler == null)
            {
                return;
            }

            try
            {
                handler(oldPhase, newPhase);
            }
            catch (Exception ex)
            {
                // A faulting consumer must not stop the clock, or every later phase change is
                // lost too.
                Log.Error(ex, "A day-phase consumer faulted.");
            }
        }

        /// <summary>
        /// Forces a phase for testing. The game clock cannot be set - it is derived from UTC -
        /// so a forced phase is the only way to watch a full cycle without waiting two real
        /// hours. Also pins the global light level so the sky matches what the NPCs are doing.
        /// </summary>
        public static void SetOverride(DayPhase phase)
        {
            _hasOverride = true;
            _override = phase;

            LightCycle.LevelOverride = GetLightLevelFor(phase);

            Poll();
        }

        public static void ClearOverride()
        {
            _hasOverride = false;

            // Int32.MinValue is LightCycle's own sentinel for "no override"
            // (Scripts/Misc/LightCycle.cs:18, :50).
            LightCycle.LevelOverride = Int32.MinValue;

            Poll();
        }

        /// <summary>
        /// The steady-state light level for a phase. The ramps take a mid-way value, since an
        /// override freezes time rather than animating through the ramp.
        /// </summary>
        private static int GetLightLevelFor(DayPhase phase)
        {
            switch (phase)
            {
                case DayPhase.Night:
                    return LightCycle.NightLevel;
                case DayPhase.Dawn:
                    return LightCycle.NightLevel / 2;
                case DayPhase.Dusk:
                    return LightCycle.NightLevel / 2;
                default:
                    return LightCycle.DayLevel;
            }
        }
    }
}
