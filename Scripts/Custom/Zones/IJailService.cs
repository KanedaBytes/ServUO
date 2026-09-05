using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// The seam between anything that wants to jail a player and the jail implementation.
    ///
    /// Port step 2 replaces the stub with the real jail administration layer built on ServUO's
    /// existing Jail region. Callers only ever go through <see cref="JailService"/>.
    /// </summary>
    public interface IJailService
    {
        /// <summary>True while the player is serving a sentence.</summary>
        bool IsPlayerJailed(Mobile player);

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
    /// assert it afterwards. That is the behaviour contract carried over from the ModernUO shard,
    /// where JailPlayer returned void and could silently no-op; keeping the contract here means
    /// step 2's implementation is held to it rather than inheriting the same trap.
    /// </summary>
    public static class JailService
    {
        private static IJailService _provider;

        public static IJailService Provider
        {
            get
            {
                if (_provider == null)
                {
                    _provider = new StubJailService();
                }

                return _provider;
            }
            set { _provider = value; }
        }

        /// <summary>True while a stub, rather than a real jail, is answering.</summary>
        public static bool IsStub { get { return Provider is StubJailService; } }

        /// <summary>
        /// Forces a provider to exist so its health check is registered before anyone is jailed.
        ///
        /// Runs in the Initialize phase, after every Configure(), so a real implementation that
        /// assigned itself in Configure() wins and no stub is ever created.
        /// </summary>
        public static void Initialize()
        {
            IJailService provider = Provider;

            if (provider is StubJailService)
            {
                CustomLogger.For("Jail").Warn(
                    "No jail implementation is registered - using the TEMPORARY stub. " +
                    "Players sent to jail will be teleported but will serve no sentence.");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // TEMPORARY - replaced in port step 2 (jail administration).
    //
    // This exists only so restricted zones have somewhere to hand a player. It is NOT a jail:
    // there is no sentence, no release timer, no persistence and no escalation. It teleports the
    // player into ServUO's existing Jail region and remembers, in memory only, that it did so.
    //
    // Everything below should be deleted wholesale when the real JailSystem lands - do not build
    // on it.
    // ---------------------------------------------------------------------------------------
    public sealed class StubJailService : IJailService
    {
        private static readonly CustomLogger Log = CustomLogger.For("Jail(stub)");

        /// <summary>Fallback when the player is on a facet with no Jail region.</summary>
        public static readonly Point3D FallbackLocation = new Point3D(5275, 1163, 0);

        // In memory only. A restart forgets everyone, which is exactly why this is a stub.
        private readonly HashSet<Mobile> _jailed = new HashSet<Mobile>();

        public StubJailService()
        {
            HealthCheck.Register(
                "Jail",
                () => HealthResult.Warn(
                    String.Format(
                        "STUB - no sentences, no release, no persistence. {0} player(s) jailed this session. " +
                        "Replaced in port step 2.",
                        _jailed.Count)));
        }

        public bool IsPlayerJailed(Mobile player)
        {
            return player != null && !player.Deleted && _jailed.Contains(player);
        }

        public void JailPlayer(Mobile from, Mobile player, string reason)
        {
            if (player == null || player.Deleted)
            {
                return;
            }

            Map map = ResolveJailMap(player);
            Point3D destination = ResolveJailLocation(map);

            _jailed.Add(player);

            player.SendMessage(0x35, "You are being sent to jail!");
            player.PlaySound(0x204);
            player.MoveToWorld(destination, map);

            Log.Error(
                "STUB JAIL: {0} sent to {1} on {2}. Reason: {3}. By: {4}. " +
                "No sentence is being served - release them manually with [go.",
                player.Name,
                destination,
                map,
                reason,
                from != null ? from.Name : "the server");
        }

        /// <summary>
        /// The player's own facet when it has a jail, otherwise Trammel.
        ///
        /// Matches the release-to-origin-facet contract step 2 will implement, so swapping the
        /// stub out does not change where players end up.
        /// </summary>
        private static Map ResolveJailMap(Mobile player)
        {
            Map map = player.Map;

            if (map == Map.Felucca || map == Map.Trammel)
            {
                return map;
            }

            return Map.Trammel;
        }

        /// <summary>
        /// Reads the go point straight off the Jail region rather than hardcoding it, so moving
        /// the jail in Data/Regions.xml moves the stub too. Regions are name-keyed per facet and
        /// the dictionary is case-insensitive.
        /// </summary>
        private static Point3D ResolveJailLocation(Map map)
        {
            if (map != null)
            {
                Region region;

                if (map.Regions.TryGetValue("Jail", out region) && region.GoLocation != Point3D.Zero)
                {
                    return region.GoLocation;
                }
            }

            return FallbackLocation;
        }
    }
}
