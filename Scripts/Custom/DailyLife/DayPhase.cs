using System;

namespace Server.Custom
{
    /// <summary>
    /// The four phases of the town's day.
    ///
    /// The boundaries deliberately match LightCycle.ComputeLevelFor exactly - night below 4, a
    /// dawn ramp to 6, day to 22, then a dusk ramp (Scripts/Misc/LightCycle.cs:70-81) - so NPC
    /// behaviour and the light level players actually see always agree. Change one and you must
    /// change the other.
    /// </summary>
    public enum DayPhase
    {
        Night,
        Dawn,
        Day,
        Dusk
    }

    public static class DayPhaseExtensions
    {
        /// <summary>
        /// Maps an in-game hour to its phase.
        ///
        /// Clock hours can go negative if the host clock is set before the 1997 epoch, and C#
        /// '%' keeps the sign, so normalise rather than trusting the input.
        /// </summary>
        public static DayPhase FromHour(int hour)
        {
            hour = ((hour % 24) + 24) % 24;

            if (hour < 4)
            {
                return DayPhase.Night;
            }

            if (hour < 6)
            {
                return DayPhase.Dawn;
            }

            if (hour < 22)
            {
                return DayPhase.Day;
            }

            return DayPhase.Dusk;
        }

        /// <summary>
        /// The single predicate every consumer keys off: the tavern fills, the watch deploys and
        /// the shops close together.
        ///
        /// Note that DAWN IS NOT AFTER DARK. The tavern empties and the watch stands down at
        /// hour 4, when the dawn ramp starts - not at 6 when it finishes.
        /// </summary>
        public static bool IsAfterDark(this DayPhase phase)
        {
            return phase == DayPhase.Dusk || phase == DayPhase.Night;
        }

        public static string ToFriendlyString(this DayPhase phase)
        {
            switch (phase)
            {
                case DayPhase.Night:
                    return "night";
                case DayPhase.Dawn:
                    return "dawn";
                case DayPhase.Day:
                    return "day";
                case DayPhase.Dusk:
                    return "dusk";
                default:
                    return "unknown";
            }
        }

        public static bool TryParse(string value, out DayPhase phase)
        {
            phase = DayPhase.Day;

            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (Insensitive.Equals(value, "night"))
            {
                phase = DayPhase.Night;
                return true;
            }

            if (Insensitive.Equals(value, "dawn"))
            {
                phase = DayPhase.Dawn;
                return true;
            }

            if (Insensitive.Equals(value, "day"))
            {
                phase = DayPhase.Day;
                return true;
            }

            if (Insensitive.Equals(value, "dusk"))
            {
                phase = DayPhase.Dusk;
                return true;
            }

            return false;
        }
    }
}
