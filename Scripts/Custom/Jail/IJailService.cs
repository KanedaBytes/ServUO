using System;

namespace Server.Custom
{
    /// <summary>
    /// The seam between anything that wants to jail a player and the jail implementation.
    ///
    /// Callers only ever go through <see cref="JailService"/>.
    /// </summary>
    public interface IJailService
    {
        /// <summary>True while the player is serving a sentence.</summary>
        bool IsPlayerJailed(Mobile player);

        /// <summary>
        /// When the player's current sentence ends, or <see cref="DateTime.MinValue"/> if they
        /// have no record. Pair with <see cref="IsPlayerJailed"/> rather than comparing this to
        /// the clock yourself, so there is only one definition of "jailed".
        /// </summary>
        DateTime GetJailEndTime(Mobile player);

        /// <summary>
        /// Jails a player. <paramref name="from"/> is the staff member responsible, or null for
        /// an automatic jail (a restricted zone, for instance) - implementations must tolerate
        /// a null <paramref name="from"/> rather than throwing from their own audit logging.
        /// </summary>
        void JailPlayer(Mobile from, Mobile player, string reason);
    }

    /// <summary>
    /// Static facade over the current <see cref="IJailService"/>.
    ///
    /// Callers must still guard with <see cref="IJailService.IsPlayerJailed"/> before jailing and
    /// assert it afterwards. That contract is carried over from the ModernUO shard, where
    /// JailPlayer returned void and could silently no-op; keeping it means an implementation is
    /// held to it rather than trusted.
    /// </summary>
    public static class JailService
    {
        private static IJailService _provider;

        /// <summary>
        /// Never null. Falls back to a no-op implementation that logs loudly, so a missing
        /// registration is a visible error rather than a NullReferenceException in a caller.
        /// </summary>
        public static IJailService Provider
        {
            get
            {
                if (_provider == null)
                {
                    _provider = new NullJailService();
                }

                return _provider;
            }
            set { _provider = value; }
        }

        /// <summary>True while no real implementation is registered.</summary>
        public static bool IsUnavailable { get { return Provider is NullJailService; } }

        /// <summary>
        /// Runs in the Initialize phase, after every Configure(), so a real implementation that
        /// assigned itself in Configure() wins. Only complains if nothing did.
        /// </summary>
        public static void Initialize()
        {
            if (IsUnavailable)
            {
                CustomLogger.For("Jail").Error(
                    "No jail implementation is registered. Anything that tries to jail a player " +
                    "will fail loudly and the player will go unpunished.");
            }
        }

        /// <summary>
        /// Fallback used only when no implementation registered itself. Does nothing except
        /// complain, which keeps the guard-then-assert contract in callers meaningful: they see
        /// IsPlayerJailed stay false after the call and report the failure to staff.
        /// </summary>
        private sealed class NullJailService : IJailService
        {
            private static readonly CustomLogger Log = CustomLogger.For("Jail");

            public bool IsPlayerJailed(Mobile player)
            {
                return false;
            }

            public DateTime GetJailEndTime(Mobile player)
            {
                return DateTime.MinValue;
            }

            public void JailPlayer(Mobile from, Mobile player, string reason)
            {
                Log.Error(
                    "Cannot jail {0} ('{1}') - no jail implementation is registered.",
                    player != null ? player.Name : "null",
                    reason);
            }
        }
    }
}
