using System;
using System.Collections.Generic;

using Server.Mobiles;
using Server.Network;
using Server.Regions;

namespace Server.Custom
{
    /// <summary>
    /// The jail administration layer, built on top of ServUO's existing Jail region.
    ///
    /// ServUO already enforces what a prisoner cannot DO - no casting, no skills (or skill gain),
    /// no harmful/beneficial acts, no recall or gate out, no escape items, no [help stuck, no
    /// housing, and stabled pets are withheld (Jail.AllowAutoClaim returns false). See CLAUDE.md
    /// section 13. This class supplies only what is missing: sentences, escalation, persistence,
    /// release, and the commands.
    ///
    /// One object is both the persistence store and the IJailService implementation.
    /// </summary>
    public sealed class JailSystem : CustomPersistence, IJailService
    {
        private static readonly CustomLogger Log = CustomLogger.For("Jail");

        public static JailSystem Instance { get; private set; }

        /// <summary>Sentence for a first offence.</summary>
        public static readonly TimeSpan MinJailTime = TimeSpan.FromMinutes(5.0);

        /// <summary>Sentence at the tenth offence and beyond.</summary>
        public static readonly TimeSpan MaxJailTime = TimeSpan.FromHours(12.0);

        /// <summary>Britain Bank - valid on both Felucca and Trammel, which share terrain.</summary>
        public static readonly Point3D ReleaseLocation = new Point3D(1444, 1697, 10);

        /// <summary>Used when the origin facet has no jail (or no Britain).</summary>
        public static readonly Map FallbackMap = Map.Trammel;

        /// <summary>Used only if the Jail region is missing from Data/Regions.xml.</summary>
        public static readonly Point3D FallbackJailLocation = new Point3D(5275, 1163, 0);

        private readonly Dictionary<Mobile, JailRecord> _records = new Dictionary<Mobile, JailRecord>();

        private readonly Dictionary<Mobile, Timer> _releaseTimers = new Dictionary<Mobile, Timer>();

        /// <summary>
        /// Players mid-way through a jail or release sequence.
        ///
        /// Transient and NEVER populated on load. The ModernUO original persisted the equivalent
        /// latch across restarts and never cleared it on release, which left a restored prisoner
        /// permanently unjailable.
        /// </summary>
        private readonly HashSet<Mobile> _inTransit = new HashSet<Mobile>();

        public JailSystem()
            : base("Jail", 0)
        {
        }

        internal static void Create()
        {
            if (Instance == null)
            {
                Instance = new JailSystem();
            }
        }

        public int PrisonerCount
        {
            get
            {
                int count = 0;

                foreach (JailRecord record in _records.Values)
                {
                    if (record.IsCurrentlyJailed)
                    {
                        ++count;
                    }
                }

                return count;
            }
        }

        // -----------------------------------------------------------------------------------
        // IJailService
        // -----------------------------------------------------------------------------------

        public bool IsPlayerJailed(Mobile player)
        {
            JailRecord record = GetRecord(player);
            return record != null && record.IsCurrentlyJailed;
        }

        public DateTime GetJailEndTime(Mobile player)
        {
            JailRecord record = GetRecord(player);
            return record != null ? record.JailEndTimeUtc : DateTime.MinValue;
        }

        /// <summary>The player's record, or null if they have never been jailed.</summary>
        public JailRecord GetRecord(Mobile player)
        {
            if (player == null || player.Deleted)
            {
                return null;
            }

            JailRecord record;
            return _records.TryGetValue(player, out record) ? record : null;
        }

        public bool IsInTransit(Mobile player)
        {
            return player != null && _inTransit.Contains(player);
        }

        /// <summary>
        /// Why a jail attempt would be refused, or null if it would proceed. Exposed so [Jail can
        /// report the reason rather than silently no-opping.
        /// </summary>
        public string GetRefusalReason(Mobile player)
        {
            if (player == null || player.Deleted)
            {
                return "That player no longer exists.";
            }

            if (!player.Player)
            {
                return "That is not a player.";
            }

            // A BOT PASSES THE FLAG TEST ABOVE, and has done since the class swap of 12 September
            // 2026: a PlayerBot is an accountless PlayerMobile with Player = true. Refused
            // separately rather than by tightening that test, because the two refusals mean
            // different things and the message should say which.
            //
            // Why refuse rather than allow: a jail record is durable and keyed to the character,
            // and a bot is not - BotStartupPurge deletes every one of them at boot. A sentence
            // served by a mobile that will not exist after the next restart is a record that
            // outlives its subject, and the escalation count it carries would be inherited by
            // whoever next rolls that name out of the pool.
            if (player is IBotActor)
            {
                return "That is a bot, not a player.";
            }

            if (player.AccessLevel > AccessLevel.Player)
            {
                return "You cannot jail staff members.";
            }

            if (IsPlayerJailed(player))
            {
                return String.Format("{0} is already jailed.", player.Name);
            }

            if (IsInTransit(player))
            {
                return String.Format("{0} is already being jailed.", player.Name);
            }

            return null;
        }

        public void JailPlayer(Mobile from, Mobile player, string reason)
        {
            if (GetRefusalReason(player) != null)
            {
                return;
            }

            if (String.IsNullOrWhiteSpace(reason))
            {
                reason = "No reason given.";
            }

            JailRecord record = GetRecord(player);

            if (record == null)
            {
                record = new JailRecord();
                _records[player] = record;
            }

            record.JailCount++;
            record.LastJailedUtc = DateTime.UtcNow;
            record.LastReason = reason;
            record.JailedBy = from;

            // Captured before the teleport, so release returns them to the facet they were taken
            // from rather than always dumping them on one.
            record.OriginMap = player.Map;

            TimeSpan sentence = CalculateJailTime(record.JailCount);
            record.JailEndTimeUtc = DateTime.UtcNow + sentence;

            _inTransit.Add(player);

            player.Frozen = true;
            player.Combatant = null;
            player.Warmode = false;

            player.SendMessage(0x35, "You are being sent to jail!");
            player.PlaySound(0x204);

            LogAction(
                from,
                String.Format(
                    "Player {0} jailed for: {1} (Offense #{2}, {3})",
                    player.Name,
                    reason,
                    record.JailCount,
                    FormatDuration(sentence)));

            NotifyStaff(
                String.Format(
                    "{0} has been jailed for {1}. Reason: {2} (Offense #{3})",
                    player.Name,
                    FormatDuration(sentence),
                    reason,
                    record.JailCount));

            Timer.DelayCall(TimeSpan.FromSeconds(2.0), () => JailStepSecureFollowers(from, player));
        }

        // -----------------------------------------------------------------------------------
        // Jail sequence
        // -----------------------------------------------------------------------------------

        private void JailStepSecureFollowers(Mobile from, Mobile player)
        {
            if (player == null || player.Deleted)
            {
                _inTransit.Remove(player);
                return;
            }

            SecureFollowers(player);

            LogAction(from, String.Format("Player {0} dismounted and pets secured before jail", player.Name));

            Timer.DelayCall(TimeSpan.FromSeconds(3.0), () => JailStepTeleport(from, player));
        }

        private void JailStepTeleport(Mobile from, Mobile player)
        {
            if (player == null || player.Deleted)
            {
                _inTransit.Remove(player);
                return;
            }

            Map map = ResolveJailMap(player.Map);
            Point3D destination = ResolveJailLocation(map);

            player.MoveToWorld(destination, map);
            player.SendMessage(0x35, "Use [JailRecord to view your record.");
            player.SendMessage(0x35, "Please contact staff if you believe this was a mistake.");

            LogAction(from, String.Format("Player {0} teleported to jail at {1} on {2}", player.Name, destination, map));

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), () => JailStepFinish(from, player));
        }

        private void JailStepFinish(Mobile from, Mobile player)
        {
            _inTransit.Remove(player);

            if (player == null || player.Deleted)
            {
                return;
            }

            player.Frozen = false;

            JailRecord record = GetRecord(player);

            if (record != null && record.IsCurrentlyJailed)
            {
                ArmReleaseTimer(player, record);
            }

            LogAction(from, String.Format("Player {0} unfrozen in jail", player.Name));

            JailStatusSystem.ShowNow(player);
        }

        /// <summary>
        /// Dismount, dismiss summons, then force-stable the rest.
        ///
        /// Order matters: BaseCreature.CanAutoStable returns false both for a mount that still
        /// has a rider and for anything Summoned, so dismounting and dismissing first is what
        /// stops the mount and every summon being silently left standing in the world.
        ///
        /// AutoStablePets is ServUO's own logout path - it bypasses the stable fee and the slot
        /// limit, and Jail.AllowAutoClaim being false means the pets stay stabled for the whole
        /// sentence and come back on the first login after release.
        /// </summary>
        private static void SecureFollowers(Mobile player)
        {
            BaseMount.Dismount(player);

            var pm = player as PlayerMobile;

            if (pm == null)
            {
                return;
            }

            var followers = new List<Mobile>(pm.AllFollowers);

            foreach (Mobile follower in followers)
            {
                var creature = follower as BaseCreature;

                if (creature != null && !creature.Deleted && creature.Summoned)
                {
                    Timer.DelayCall(creature.Delete);
                }
            }

            pm.AutoStablePets();
        }

        // -----------------------------------------------------------------------------------
        // Release
        // -----------------------------------------------------------------------------------

        /// <summary>Ends a sentence early. Used by [Unjail.</summary>
        public bool Unjail(Mobile from, Mobile player)
        {
            JailRecord record = GetRecord(player);

            if (record == null)
            {
                return false;
            }

            // Deliberately does NOT require IsCurrentlyJailed: a sentence that expired while the
            // server was down leaves a player sitting in the cell with no timer, and the
            // ModernUO original refused to free exactly those people.
            if (!record.IsCurrentlyJailed && !IsInJailRegion(player))
            {
                return false;
            }

            ReleasePlayer(from, player);
            return true;
        }

        private void ReleasePlayer(Mobile from, Mobile player)
        {
            if (player == null || player.Deleted)
            {
                return;
            }

            CancelReleaseTimer(player);

            JailRecord record = GetRecord(player);

            if (record != null)
            {
                // Makes IsCurrentlyJailed false immediately, which is what closes the status gump.
                record.JailEndTimeUtc = DateTime.UtcNow;
            }

            _inTransit.Add(player);

            player.Frozen = true;
            player.SendMessage(0x35, "You have been released from jail!");
            player.PlaySound(0x1FF);

            LogAction(from, String.Format("Player {0} released from jail", player.Name));
            NotifyStaff(String.Format("{0} has been released from jail.", player.Name));

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), () => ReleaseStepTeleport(from, player));
        }

        private void ReleaseStepTeleport(Mobile from, Mobile player)
        {
            if (player == null || player.Deleted)
            {
                _inTransit.Remove(player);
                return;
            }

            JailRecord record = GetRecord(player);
            Map map = ResolveReleaseMap(record);

            player.MoveToWorld(ReleaseLocation, map);

            LogAction(from, String.Format("Player {0} teleported from jail to {1} on {2}", player.Name, ReleaseLocation, map));

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), () => ReleaseStepFinish(from, player));
        }

        private void ReleaseStepFinish(Mobile from, Mobile player)
        {
            _inTransit.Remove(player);

            if (player == null || player.Deleted)
            {
                return;
            }

            player.Frozen = false;

            player.SendMessage(0x35, "Welcome back!");
            player.SendMessage(0x35, "Please follow the shard rules.");

            var pm = player as PlayerMobile;

            if (pm != null && pm.AutoStabled.Count > 0)
            {
                player.SendMessage(
                    0x35,
                    "Your pets were stabled while you were jailed. They will be returned the next time you log in.");
            }

            player.CloseGump(typeof(JailStatusGump));

            LogAction(from, String.Format("Player {0} unfrozen after release", player.Name));
        }

        // -----------------------------------------------------------------------------------
        // Timers
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Arms the release timer from the ABSOLUTE end time, not the sentence length.
        ///
        /// The ModernUO original stamped the end time at the start of the sequence but armed the
        /// timer ten seconds later for the full duration, so the release ran ten seconds late
        /// while IsPlayerJailed went false ten seconds early. Deriving from JailEndTimeUtc keeps
        /// them in agreement.
        /// </summary>
        private void ArmReleaseTimer(Mobile player, JailRecord record)
        {
            // Always stop the previous timer before replacing it. The ModernUO original
            // overwrote the dictionary entry and left the old timer live, which released the
            // player early and then fired a second time.
            CancelReleaseTimer(player);

            TimeSpan remaining = record.Remaining;

            Mobile from = record.JailedBy;
            _releaseTimers[player] = Timer.DelayCall(remaining, () => ReleasePlayer(from, player));
        }

        private void CancelReleaseTimer(Mobile player)
        {
            Timer timer;

            // net48 has no Dictionary.Remove(key, out value).
            if (!_releaseTimers.TryGetValue(player, out timer))
            {
                return;
            }

            _releaseTimers.Remove(player);

            if (timer != null)
            {
                timer.Stop();
            }
        }

        // -----------------------------------------------------------------------------------
        // Sentence maths and location resolution
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Linear escalation: 5 minutes at the first offence, rising to 12 hours by the tenth,
        /// clamped after that.
        /// </summary>
        public static TimeSpan CalculateJailTime(int jailCount)
        {
            double ticks = MinJailTime.Ticks
                + (jailCount - 1) * (double)(MaxJailTime.Ticks - MinJailTime.Ticks) / 9.0;

            if (ticks < MinJailTime.Ticks)
            {
                return MinJailTime;
            }

            if (ticks > MaxJailTime.Ticks)
            {
                return MaxJailTime;
            }

            return TimeSpan.FromTicks((long)Math.Round(ticks));
        }

        /// <summary>Whole units. The original interpolated raw TotalMinutes, printing "84.444444445 minutes".</summary>
        public static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            int hours = (int)span.TotalHours;
            int minutes = span.Minutes;

            if (hours > 0)
            {
                if (minutes > 0)
                {
                    return String.Format(
                        "{0} hour{1} {2} minute{3}",
                        hours, hours == 1 ? "" : "s",
                        minutes, minutes == 1 ? "" : "s");
                }

                return String.Format("{0} hour{1}", hours, hours == 1 ? "" : "s");
            }

            if (minutes > 0)
            {
                return String.Format("{0} minute{1}", minutes, minutes == 1 ? "" : "s");
            }

            return "less than a minute";
        }

        /// <summary>Only Felucca and Trammel have a Jail region; anything else falls back.</summary>
        public static Map ResolveJailMap(Map origin)
        {
            if (origin == Map.Felucca || origin == Map.Trammel)
            {
                return origin;
            }

            return FallbackMap;
        }

        /// <summary>
        /// Reads the go point off the Jail region rather than hardcoding it, so moving the jail
        /// in Data/Regions.xml moves the destination too.
        /// </summary>
        public static Point3D ResolveJailLocation(Map map)
        {
            if (map != null)
            {
                Region region;

                if (map.Regions.TryGetValue("Jail", out region) && region.GoLocation != Point3D.Zero)
                {
                    return region.GoLocation;
                }
            }

            return FallbackJailLocation;
        }

        private static Map ResolveReleaseMap(JailRecord record)
        {
            Map origin = record != null ? record.OriginMap : null;

            if (origin == Map.Felucca || origin == Map.Trammel)
            {
                return origin;
            }

            return FallbackMap;
        }

        private static bool IsInJailRegion(Mobile player)
        {
            if (player == null || player.Deleted || player.Map == null || player.Map == Map.Internal)
            {
                return false;
            }

            return player.Region != null && player.Region.IsPartOf<Jail>();
        }

        // -----------------------------------------------------------------------------------
        // Login safety net
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Runs at EventSink.Login, where Map, Location, Region and NetState are all restored and
        /// final (the restore happens in the Mobile.NetState setter, before the event fires).
        /// </summary>
        internal void OnLogin(Mobile player)
        {
            if (player == null || player.Deleted)
            {
                return;
            }

            // Frozen means a jail or release sequence is already in flight for this session.
            if (player.Frozen || IsInTransit(player))
            {
                return;
            }

            JailRecord record = GetRecord(player);

            if (record == null)
            {
                return;
            }

            if (record.IsCurrentlyJailed)
            {
                if (!_releaseTimers.ContainsKey(player))
                {
                    ArmReleaseTimer(player, record);
                }

                JailStatusSystem.ShowNow(player);
                return;
            }

            // The sentence is over. If they are still in the cell, the release never happened -
            // the sentence expired while the server was down.
            if (IsInJailRegion(player))
            {
                Log.Info("{0}'s sentence expired while offline; releasing on login.", player.Name);
                ReleasePlayer(record.JailedBy, player);
            }
        }

        /// <summary>Re-arms timers for everyone still serving. Called after the world has loaded.</summary>
        internal void RearmTimers()
        {
            int armed = 0;

            foreach (KeyValuePair<Mobile, JailRecord> pair in _records)
            {
                if (pair.Value.IsCurrentlyJailed)
                {
                    ArmReleaseTimer(pair.Key, pair.Value);
                    ++armed;
                }
            }

            Log.Info("Loaded {0} jail record(s); {1} sentence(s) still running.", _records.Count, armed);
        }

        // -----------------------------------------------------------------------------------
        // Persistence
        // -----------------------------------------------------------------------------------

        protected override void Reset()
        {
            foreach (Mobile player in new List<Mobile>(_releaseTimers.Keys))
            {
                CancelReleaseTimer(player);
            }

            _records.Clear();
            _inTransit.Clear();
        }

        protected override void SerializeCore(GenericWriter writer)
        {
            writer.Write(_records.Count);

            foreach (KeyValuePair<Mobile, JailRecord> pair in _records)
            {
                writer.Write(pair.Key);
                pair.Value.Serialize(writer);
            }
        }

        protected override void DeserializeCore(GenericReader reader, int version)
        {
            switch (version)
            {
                case 0:
                    {
                        int count = reader.ReadInt();

                        for (int i = 0; i < count; i++)
                        {
                            Mobile player = reader.ReadMobile();

                            var record = new JailRecord();
                            record.Deserialize(reader);

                            // Consume the stream unconditionally, then decide whether to keep it:
                            // a deleted character resolves to null and its record is dropped.
                            if (player != null && !player.Deleted)
                            {
                                _records[player] = record;
                            }
                        }
                    }
                    break;
            }

            // Timers are armed in RearmTimers() after the world has finished loading, not here.
        }

        // -----------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// CommandLogging.WriteLine dereferences from.NetState/Account/AccessLevel and throws on
        /// a null actor, but IJailService explicitly allows a null from for automatic jails.
        /// </summary>
        private static void LogAction(Mobile from, string text)
        {
            if (from != null && !from.Deleted)
            {
                Server.Commands.CommandLogging.WriteLine(from, text);
            }

            Log.Info(text);
        }

        private static void NotifyStaff(string message)
        {
            foreach (NetState ns in NetState.Instances)
            {
                Mobile staff = ns.Mobile;

                if (staff != null && staff.AccessLevel >= AccessLevel.Counselor)
                {
                    staff.SendMessage(0x35, message);
                }
            }
        }

        internal HealthResult BuildHealthResult()
        {
            if (IsDegraded)
            {
                return HealthResult.Fail(
                    String.Format(
                        "SAVING DISABLED - {0}. No sentences are being tracked; every prisoner is free.",
                        DegradedReason));
            }

            return HealthResult.Ok(
                String.Format(
                    "{0} prisoner(s), {1} record(s), {2} in transit",
                    PrisonerCount,
                    _records.Count,
                    _inTransit.Count));
        }
    }

    /// <summary>
    /// ScriptCompiler entry points for the jail.
    ///
    /// Separate from JailSystem because a static Configure() there would hide
    /// CustomPersistence.Configure() (which hooks the world save/load events) and give one type
    /// two unrelated meanings for the same name.
    /// </summary>
    public static class JailBootstrap
    {
        public static void Configure()
        {
            JailSystem.Create();

            // Assigned during Configure so it beats JailService.Initialize(), which only
            // complains when nothing has registered by then.
            JailService.Provider = JailSystem.Instance;

            EventSink.Login += OnLogin;
        }

        public static void Initialize()
        {
            JailSystem.Instance.RearmTimers();

            HealthCheck.Register("Jail", JailSystem.Instance.BuildHealthResult);
        }

        private static void OnLogin(LoginEventArgs e)
        {
            JailSystem.Instance.OnLogin(e.Mobile);
        }
    }
}
