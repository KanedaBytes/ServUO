// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// NamedBots.cs - the named cast: an Account each, a character the world save keeps, and a logout
// that is a logout.
//
// Sean's decisions (CLASS-DECISION.md, "Named bots", 22 September 2026, on top of the OFFLINE answer
// of 21 September): the fixed-role cast is NAMED; each named bot has one real ServUO Account, locked
// so no human can log in and with the name reserved so a player cannot take it; a named bot goes
// OFFLINE at session end - the character and everything it owns persist like a player logging out -
// and is never destroyed. Throwaway bots are unchanged and still own nothing durable.
//
// WHAT UPSTREAM DOES, and we do the same wherever no named seam says otherwise (uo-offline @ 7f38c7c,
// playerbots/source/CustomBots/):
//
//   One saved flag decides permanence - PlayerBot.GuildBound (:166, v7 at :1068), IsPermanent (:168)
//   - and every destructive path asks it: the boot purge (BotStartupManager.cs:106-110), the session
//   logout (BotSessionManager.cs:228) and the regen (GenerateBotsCommand.cs:174,
//   ClearSpawnersCommand.cs:47). Becoming permanent detaches the bot from its spawner (PlayerBot.cs
//   :185-189). Nothing custom persists its belongings: the engine's world save carries the mobile,
//   its pack and its bank. Ours: PlayerBot.RosterName; BotStartupPurge.IsStale; a named bot is never
//   on a spawner at all; the world save carries the rest.
//
// THE NAMED SEAMS, where we differ:
//
//   1. AN ACCOUNT PER BOT. Upstream has none - the only "Account" in its bot source is a Banker
//      helper. Sean's decision; REVIEW.md section 3's default ("a dedicated, non-login account per
//      persistent economic bot").
//   2. THE TRIGGER IS A ROSTER, not a guild recruit (BotRoster). Recruitment is deferred to 7g.
//   3. LOGOUT IS Internalize, NOT Delete. Upstream's permanent bot never logs out at all. Ours does
//      what the engine does for a player whose client goes: LogoutLocation, LogoutMap, Map.Internal
//      (Mobile.cs:1990-2000). A named bot saved while online loads the same way, because
//      Mobile.Deserialize parks every Player-flagged mobile on Map.Internal (:6182-6188) - so a
//      restart IS a logout, and "brought back in" is LogIn putting it at its post.
//   4. DEATH IS A CORPSE RUN - INTERIM (Sean, 22 September 2026). Resurrect after a second, take
//      everything back off the corpse, return to the post. 7h Death replaces this with a player-like
//      ghost and resurrection. Upstream's permanent bot dies into its death manager's corpse run.
//   5. NO MOUNT until 7f. BotMovement.SweepStrayMounts deletes at boot any animal whose master is a
//      PlayerBot, and ownership is transient, so a named bot is built without one.
//
// GAME THREAD ONLY. Everything here runs from Initialize, ServerStarted, a command, a token, a death
// timer or an EventSink handler.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Accounting;
using Server.Commands;
using Server.Items;
using Server.Network;

namespace Server.Custom
{
    public static class NamedBots
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>The account tag that says "this is a bot's account". Its value is the identity.</summary>
        public const string AccountTag = "GG.PlayerBot";

        /// <summary>A fixture's own named bot is deleted with this reason and is not a fault.</summary>
        public const string FixtureReason = "fixture";

        public const string SnapshotPath = "Data/Live/bot-named.json";

        /// <summary>What stock CharacterCreation falls back to for an illegal name (:363).</summary>
        public const string FallbackName = "Generic Player";

        /// <summary>How long after the logins before "is everybody at their post" is asserted.</summary>
        private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(150.0);

        // identity -> the character, for every named bot this boot knows about
        private static readonly Dictionary<string, PlayerBot> _bots =
            new Dictionary<string, PlayerBot>(StringComparer.OrdinalIgnoreCase);

        // identity -> why this entry cannot be put in the world
        private static readonly Dictionary<string, string> _refused =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // identities a staff command logged out; nothing logs them back in but a command
        private static readonly HashSet<string> _heldOffline =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<PlayerBot> _retired = new List<PlayerBot>();
        private static readonly List<string> _deleted = new List<string>();

        private static bool _hooked;
        private static long _loggedInAt;
        private static bool _loggedIn;

        // Internal setters so NamedBotFixtures can put these back after proving the paths that move
        // them - a [CoreSmoke run must not read on Bots.Named as a human refused at the login screen.
        public static int CharactersCreated { get; internal set; }
        public static int AccountsCreated { get; internal set; }
        public static int Logins { get; internal set; }
        public static int Logouts { get; internal set; }
        public static int Deaths { get; internal set; }
        public static int CorpseRuns { get; internal set; }
        public static int RefusedLogins { get; internal set; }
        public static int RenamedHumans { get; internal set; }

        // -------------------------------------------------------------------
        // Boot
        // -------------------------------------------------------------------

        /// <summary>
        /// 905: after BotStartupPurge (-1000) has kept the named cast; after the stock account
        /// handler and character creation (both 0) have subscribed, so the guards below run after
        /// theirs; after the stock AccountPrompt (0), so a fresh shard still asks for its owner
        /// account; and at 900 or above so the boot line reaches Data/Live/console.json. Accounts are
        /// loaded by now - they load on WorldLoad, after every mobile (Accounts.cs:14), and the load
        /// REPLACES the table (:56), so nothing may be created before Initialize.
        /// </summary>
        [CallPriority(905)]
        public static void Initialize()
        {
            if (!_hooked)
            {
                _hooked = true;

                EventSink.AccountLogin += GuardAccountLogin;
                EventSink.GameLogin += GuardGameLogin;
                EventSink.CharacterCreated += GuardCharacterCreated;
                EventSink.Login += GuardLogin;

                HealthCheck.Register("Bots.Named", BuildHealthResult);

                CommandSystem.Register("BotNamed", AccessLevel.Administrator, BotNamed_OnCommand);

                // ServerStarted, beside the population fill and for its reason: putting mobiles in
                // the world during Initialize is before the map and the regions have settled.
                EventSink.ServerStarted += () => LogInAll("boot");
            }

            Ensure();

            Log.Info(
                "Named bots: {0} in the roster, {1} character(s) ready ({2} created), {3} account(s) created, "
                + "{4} refused, {5} retired; the boot purge kept {6}.",
                BotRoster.Store.Bots.Count,
                _bots.Count,
                CharactersCreated,
                AccountsCreated,
                _refused.Count,
                _retired.Count,
                BotStartupPurge.LastKept);

            foreach (var entry in _refused)
            {
                Log.Warn("Named bot {0} refused: {1}", entry.Key, entry.Value);
            }
        }

        /// <summary>
        /// Make the roster true of the world: every valid entry has its account and its character.
        /// Nothing here puts a bot in the world - that is LogIn - and nothing here deletes one.
        /// Run at boot and after a roster reload.
        /// </summary>
        public static void Ensure()
        {
            Dictionary<string, PlayerBot> inWorld = FindInWorld();
            Map facet = BotPopulation.Facet;
            Dictionary<string, string> postErrors = BotRoster.PostErrors(BotRoster.Store, facet);

            _refused.Clear();

            foreach (BotRosterEntry entry in BotRoster.Store.Bots)
            {
                string identity = entry.Identity;
                PlayerBot existing;

                inWorld.TryGetValue(identity, out existing);

                string error;

                if (postErrors.TryGetValue(identity, out error))
                {
                    _refused[identity] = error;

                    // Refused is not deleted: a bot that already exists stays, offline.
                    if (existing != null)
                    {
                        _bots[identity] = existing;
                    }

                    continue;
                }

                Account account = EnsureAccount(identity, out error);

                if (account == null)
                {
                    _refused[identity] = error;

                    if (existing != null)
                    {
                        _bots[identity] = existing;
                    }

                    continue;
                }

                PlayerBot bot = EnsureCharacter(entry, account, existing, out error);

                if (bot == null)
                {
                    _refused[identity] = error;
                    continue;
                }

                _bots[identity] = bot;
            }

            // Retired: named, in the save, and no longer in the roster. Offline for ever.
            _retired.Clear();

            foreach (var pair in inWorld)
            {
                if (BotRoster.FindByIdentity(pair.Key) == null)
                {
                    _retired.Add(pair.Value);
                }
            }
        }

        /// <summary>
        /// Every named bot in the world, by identity.
        ///
        /// A World.Mobiles walk, which custom code otherwise avoids (CLAUDE.md section 15). It runs
        /// once at boot and once per roster reload or staff command - never on a timer - and nothing
        /// else can answer it: an offline named bot is in no registry, by design.
        /// </summary>
        private static Dictionary<string, PlayerBot> FindInWorld()
        {
            var found = new Dictionary<string, PlayerBot>(StringComparer.OrdinalIgnoreCase);

            foreach (Mobile mobile in World.Mobiles.Values)
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted || !bot.IsNamed)
                {
                    continue;
                }

                PlayerBot first;

                if (found.TryGetValue(bot.RosterName, out first))
                {
                    Log.Error(
                        "Two named bots carry the identity '{0}' (0x{1:X} and 0x{2:X}); keeping the first. "
                        + "Neither is deleted.",
                        bot.RosterName,
                        first.Serial.Value,
                        bot.Serial.Value);

                    continue;
                }

                found[bot.RosterName] = bot;
            }

            return found;
        }

        // -------------------------------------------------------------------
        // Accounts
        // -------------------------------------------------------------------

        /// <summary>
        /// This identity's account: found, or created, and locked either way. Idempotent.
        ///
        /// The username IS the identity, so the name is reserved at the account layer as well: a
        /// human who types it at the login screen finds an existing, banned account rather than
        /// auto-creating one (AccountHandler.cs:277-283 creates only on a null lookup, and the lookup
        /// is case-insensitive). An account of that name that is NOT ours belongs to a human, and is
        /// refused and never touched.
        ///
        /// Locked three ways: Banned with no ban tags, which is permanent (Account.cs:454-457, the
        /// stock pattern at AdminGump.cs:1555); a random password nobody is told; and GuardAccountLogin
        /// below. LastLogin is refreshed every boot so the account never reads Inactive
        /// (Account.cs:508-519), which would condemn a house it owns in 7f (BaseHouse.cs:102).
        /// </summary>
        internal static Account EnsureAccount(string identity, out string error)
        {
            error = null;

            if (String.IsNullOrEmpty(identity))
            {
                error = "no identity";
                return null;
            }

            IAccount found = Accounts.GetAccount(identity);
            Account account;

            if (found != null)
            {
                account = found as Account;

                if (account == null || !IsBotAccount(account))
                {
                    error = String.Format(
                        "an account named '{0}' already exists and is not a bot account - it is a person's, and is left alone",
                        identity);
                    return null;
                }
            }
            else
            {
                // Account's constructor registers itself (Account.cs:186), and Accounts.Add
                // overwrites without asking (Accounts.cs:46) - which is why the lookup comes first.
                account = new Account(identity, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
                account.SetTag(AccountTag, identity);
                AccountsCreated++;

                Log.Info("Created the account for named bot {0}.", identity);
            }

            Lock(account);

            return account;
        }

        private static void Lock(Account account)
        {
            account.SetUnspecifiedBan(null);
            account.Banned = true;
            account.Young = false;
            account.AccessLevel = AccessLevel.Player;
            account.LastLogin = DateTime.UtcNow;
        }

        public static bool IsBotAccount(IAccount account)
        {
            var real = account as Account;

            return real != null && real.GetTag(AccountTag) != null;
        }

        // -------------------------------------------------------------------
        // Characters
        // -------------------------------------------------------------------

        /// <summary>
        /// This entry's character, bound to its account: the one already in slot, else the one the
        /// world save brought back, else a new one - built offline, at its post's logout location.
        /// A roster rename lands here: the display name follows the roster and the identity stays.
        /// </summary>
        internal static PlayerBot EnsureCharacter(
            BotRosterEntry entry, Account account, PlayerBot existing, out string error)
        {
            error = null;

            PlayerBot bot = null;

            for (int i = 0; i < account.Length; i++)
            {
                var candidate = account[i] as PlayerBot;

                if (candidate != null && !candidate.Deleted
                    && Insensitive.Equals(candidate.RosterName, entry.Identity))
                {
                    bot = candidate;
                    break;
                }
            }

            if (bot == null)
            {
                bot = existing;
            }

            if (bot == null)
            {
                bot = PlayerBot.CreateNamed(entry);

                // Offline from birth, exactly as a loaded one is: Map.Internal, with a logout location
                // that is its post, so the engine's own login path would put it in the right place.
                NavDestination post = Nav.Destination(entry.Post);

                if (post != null && post.Map != null)
                {
                    bot.LogoutLocation = StandFor(entry, post);
                    bot.LogoutMap = post.Map;
                }

                bot.Internalize();

                // Its spawn kit is on the books from the first moment, in the offline bucket - and
                // its purse, which the constructor gave it, in the gold ledger's offline set.
                BotGoodsLedger.NoteOffline(bot);
                BotGoldLedger.NoteOffline(bot);

                CharactersCreated++;

                Log.Info("Created named bot {0} ({1}, {2}), offline until it logs in at {3}.",
                    bot.Name, BotClassHelper.DisplayName(bot.Class), entry.Home, entry.Post);
            }

            if (!Bind(account, bot))
            {
                error = String.Format("account '{0}' has no free character slot", account.Username);
                return null;
            }

            // THE PURSE, LATE (Sean, 23 September 2026). The cast built on 22 September came into
            // the world before every bot was born with economy.startingGold. Each is given it once,
            // on top of whatever it already holds - that gold is its own and counted as opening -
            // and the saved flag is what stops a second boot giving it again.
            if (BotGoldLedger.GrantLate(bot))
            {
                Log.Info("Named bot {0} was given its starting purse of {1} gold (built before the rule).",
                    bot.Name, BotSystem.Store.Economy.StartingGold);
            }

            if (!String.Equals(bot.Name, entry.Name, StringComparison.Ordinal))
            {
                Log.Info("Named bot {0} is renamed {1} by the roster.", bot.Name, entry.Name);
                bot.Name = entry.Name;
            }

            return bot;
        }

        /// <summary>Put the character on its account, the way CharacterCreation does (a[i] = mobile, :160).</summary>
        private static bool Bind(Account account, PlayerBot bot)
        {
            for (int i = 0; i < account.Length; i++)
            {
                if (account[i] == bot)
                {
                    bot.Account = account;
                    return true;
                }
            }

            for (int i = 0; i < account.Length; i++)
            {
                if (account[i] == null)
                {
                    account[i] = bot;
                    return true;
                }
            }

            return false;
        }

        // -------------------------------------------------------------------
        // Online and offline
        // -------------------------------------------------------------------

        public static bool IsOnline(PlayerBot bot)
        {
            return bot != null && !bot.Deleted && bot.Map != null && bot.Map != Map.Internal;
        }

        /// <summary>Log in every valid named bot that is offline, bar the ones a command is holding out.</summary>
        public static int LogInAll(string reason)
        {
            int done = 0;

            foreach (BotRosterEntry entry in BotRoster.Store.Bots)
            {
                PlayerBot bot;

                if (_refused.ContainsKey(entry.Identity) || _heldOffline.Contains(entry.Identity)
                    || !_bots.TryGetValue(entry.Identity, out bot) || IsOnline(bot))
                {
                    continue;
                }

                if (LogIn(bot, entry, reason))
                {
                    done++;
                }
            }

            _loggedInAt = Core.TickCount;
            _loggedIn = true;

            if (done > 0)
            {
                Log.Info("Logged in {0} named bot(s) at their posts ({1}).", done, reason);
            }

            return done;
        }

        /// <summary>
        /// Put a named bot at its post and wake it up: on the live map, on the books as a live bot,
        /// and seeded exactly as a spawner seeds a fixture - the same PlayerBot.ApplySeed, so a named
        /// smith clocks in at its forge by the same path the generated one did.
        /// </summary>
        public static bool LogIn(PlayerBot bot, BotRosterEntry entry, string reason)
        {
            if (bot == null || bot.Deleted || entry == null)
            {
                return false;
            }

            NavDestination post = Nav.Destination(entry.Post);

            if (post == null || post.Map == null || post.Map == Map.Internal)
            {
                Log.Warn("Named bot {0} cannot log in: post '{1}' is not on the graph.", bot.Name, entry.Post);
                return false;
            }

            // Saved as a ghost (died, and the shard stopped inside the second before its corpse run):
            // it comes back alive, as the interim death rule would have left it.
            if (!bot.Alive)
            {
                bot.Resurrect();
            }

            BotMovement.ReleaseMount(bot);

            bot.Role = BotRole.Fixed;
            bot.MoveToWorld(StandFor(entry, post), post.Map);

            LiveRegistry.Register(bot);
            BotGoodsLedger.NoteOnline(bot);
            BotGoldLedger.NoteOnline(bot);

            Seed(bot, entry);

            Logins++;

            BotLog.Note(bot, BotLogKind.Session, "logged in at {0} ({1})", entry.Post, reason);

            return true;
        }

        /// <summary>
        /// Take a named bot offline, as a player's client going would: its brain stands down, its
        /// holdings go to the ledger's offline bucket, and the engine's own logout state is set -
        /// LogoutLocation, LogoutMap, Map.Internal (Mobile.cs:1990-2000). Never a delete.
        /// </summary>
        public static bool LogOut(PlayerBot bot, string reason)
        {
            if (!IsOnline(bot))
            {
                return false;
            }

            bot.SetBehavior(new IdleBehavior(), "logout: " + reason);

            bot.Home = Point3D.Zero;
            bot.IdleTolerance = 0;
            bot.LoggingOut = false;

            BotMovement.ReleaseMount(bot);
            BotGoodsLedger.NoteOffline(bot);
            BotGoldLedger.NoteOffline(bot);

            BotLog.Note(bot, BotLogKind.Session, "logged out ({0})", reason);

            bot.LogoutLocation = bot.Location;
            bot.LogoutMap = bot.Map;
            bot.Internalize();

            LiveRegistry.Unregister(bot);

            Logouts++;

            return true;
        }

        /// <summary>The tile a named bot logs in on: its post's arrival, a bank's crowd spread across them.</summary>
        internal static Point3D StandFor(BotRosterEntry entry, NavDestination post)
        {
            if (post.ArrivalList == null || post.ArrivalList.Count == 0)
            {
                return post.Location;
            }

            int index = 0;

            foreach (BotRosterEntry other in BotRoster.Store.Bots)
            {
                if (other == entry)
                {
                    break;
                }

                if (Insensitive.Equals(other.Post, entry.Post))
                {
                    index++;
                }
            }

            return post.ArrivalList[index % post.ArrivalList.Count].Location;
        }

        /// <summary>The fixed-role seed, as BotPopulation writes it for a spawner: home, post, class and brain.</summary>
        private static void Seed(PlayerBot bot, BotRosterEntry entry)
        {
            NavDestination post = Nav.Destination(entry.Post);
            BotPopulationConfig config = BotSystem.Store.Population;

            bool bank = post != null && Insensitive.Equals(post.Type, "bank");

            bot.SeedHome = entry.Home;
            bot.SeedStation = entry.Post;

            // Only a class the roster changed re-derives the bot (ApplySeed compares first), and that
            // re-derive is a staff edit's consequence: new skills, new outfit, the pack's goods written
            // down as `reclass`. The bank box is never touched by it.
            bot.SeedClass = entry.ParsedClass;
            bot.Seed = config.RoleFor(bank ? BotPopulationConfig.RoleBank : BotPopulationConfig.RoleStation);
        }

        // -------------------------------------------------------------------
        // Death - INTERIM
        // -------------------------------------------------------------------

        /// <summary>
        /// A named bot died. INTERIM RULE (Sean, 22 September 2026): a second later it is resurrected,
        /// takes everything back off its corpse and returns to its post. 7h Death replaces this with a
        /// player-like ghost and resurrection.
        /// </summary>
        public static void OnDied(PlayerBot bot, Container corpse)
        {
            if (bot == null)
            {
                return;
            }

            Deaths++;
            bot.CorpseRunPending = true;

            Timer.DelayCall(TimeSpan.FromSeconds(1.0), () => CorpseRun(bot));
        }

        /// <summary>
        /// Resurrect, then self-loot through the engine's own path (Corpse.Open with checkSelfLoot,
        /// Corpse.cs:1229) - and then take what that path deliberately leaves, because it skips any
        /// item that is not Movable (:1266) and most of a bot's outfit is not. Anything tracked that
        /// still cannot come back is lost to the corpse, like a player's, and written down as `death`.
        /// </summary>
        internal static void CorpseRun(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            try
            {
                if (!bot.Alive)
                {
                    bot.Resurrect();
                }

                if (!bot.Alive)
                {
                    // A region that refuses resurrection. Try again shortly rather than leave it a ghost.
                    Log.Warn("Named bot {0} could not be resurrected at {1}; retrying.", bot.Name, bot.Location);
                    Timer.DelayCall(TimeSpan.FromSeconds(5.0), () => CorpseRun(bot));
                    return;
                }

                var corpse = bot.Corpse as Corpse;

                if (corpse != null && !corpse.Deleted)
                {
                    if (bot.InRange(corpse.GetWorldLocation(), 2))
                    {
                        corpse.Open(bot, true);
                    }

                    var robe = bot.FindItemOnLayer(Layer.OuterTorso) as DeathRobe;

                    if (robe != null)
                    {
                        robe.Delete();
                    }

                    foreach (Item item in new List<Item>(corpse.Items))
                    {
                        if (item.Layer == Layer.Hair || item.Layer == Layer.FacialHair)
                        {
                            continue;
                        }

                        if (corpse.EquipItems != null && corpse.EquipItems.Contains(item) && bot.EquipItem(item))
                        {
                            continue;
                        }

                        if (bot.Backpack != null)
                        {
                            bot.Backpack.DropItem(item);
                        }
                    }

                    BotGoodsLedger.NoteContainerLoss(bot, corpse, BotGoodsLedger.ReasonDeath);

                    // Gold is Movable, so Corpse.Open brings the purse back; anything that still
                    // did not come is lost to the corpse like a player's, and written down.
                    BotGoldLedger.NoteContainerLoss(bot, corpse, BotGoodsLedger.ReasonDeath);
                }

                bot.Hits = bot.HitsMax;
                bot.Stam = bot.StamMax;
                bot.Mana = bot.ManaMax;

                CorpseRuns++;

                BotLog.Note(bot, BotLogKind.Session, "corpse run done (interim death rule)");
            }
            finally
            {
                if (bot.Alive)
                {
                    bot.CorpseRunPending = false;
                }
            }

            BotRosterEntry entry = BotRoster.FindByIdentity(bot.RosterName);

            if (entry != null && IsOnline(bot))
            {
                Seed(bot, entry);
            }
        }

        // -------------------------------------------------------------------
        // Guards
        // -------------------------------------------------------------------

        /// <summary>A named bot is being deleted. Only a fixture's own bot may be; anything else is a fault.</summary>
        public static void NoteDeleted(PlayerBot bot, string reason)
        {
            if (bot == null || String.Equals(reason, FixtureReason, StringComparison.Ordinal))
            {
                return;
            }

            string line = String.Format(
                "{0} (identity {1}, 0x{2:X}) by '{3}' at {4:HH:mm:ss}Z",
                bot.Name,
                bot.RosterName,
                bot.Serial.Value,
                reason ?? "unspecified",
                DateTime.UtcNow);

            _deleted.Add(line);

            Log.Error("A NAMED BOT WAS DELETED: {0}. Named bots are never destroyed; Bots.Named has failed.", line);
        }

        /// <summary>
        /// A human at the login screen with a bot account's name. Banned already refuses it
        /// (AccountHandler.cs:311); this is the second lock, run after stock, and it is the one
        /// that does not depend on a flag somebody might clear from the admin gump.
        /// </summary>
        internal static void GuardAccountLogin(AccountLoginEventArgs e)
        {
            if (e == null || !IsBotAccount(Accounts.GetAccount(e.Username)))
            {
                return;
            }

            if (e.Accepted)
            {
                Log.Warn("A login on bot account '{0}' was accepted by the stock handler and refused here.", e.Username);
            }

            e.Accepted = false;
            e.RejectReason = ALRReason.Blocked;
            RefusedLogins++;
        }

        internal static void GuardGameLogin(GameLoginEventArgs e)
        {
            if (e == null || !IsBotAccount(Accounts.GetAccount(e.Username)))
            {
                return;
            }

            e.Accepted = false;
            RefusedLogins++;
        }

        /// <summary>
        /// A new human character named after a named bot is renamed to stock's own fallback. The
        /// event cannot be cancelled (CharacterCreatedEventArgs.Name is read-only, EventSink.cs:905),
        /// so this runs after stock has built the mobile and corrects it.
        /// </summary>
        internal static void GuardCharacterCreated(CharacterCreatedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            RenameIfReserved(e.Mobile, "creation");
        }

        /// <summary>
        /// The same check at every login, for the rename paths that have no hook (NameChangeDeed and
        /// NameChangeToken set the name directly and raise nothing). A known gap until then: a human
        /// may wear the name for the rest of the session in which they bought it.
        /// </summary>
        private static void GuardLogin(LoginEventArgs e)
        {
            if (e != null && RenameIfReserved(e.Mobile, "login"))
            {
                e.Mobile.SendMessage(0x22, "That name belongs to one of the shard's residents; you have been renamed. Use a name change to pick another.");
            }
        }

        internal static bool RenameIfReserved(Mobile mobile, string when)
        {
            if (mobile == null || mobile is PlayerBot || !BotRoster.IsReserved(mobile.RawName))
            {
                return false;
            }

            string was = mobile.RawName;

            mobile.RawName = FallbackName;
            RenamedHumans++;

            Log.Warn("A human character named '{0}' was renamed '{1}' at {2}: the name belongs to a named bot.",
                was, FallbackName, when);

            return true;
        }

        // -------------------------------------------------------------------
        // Reporting
        // -------------------------------------------------------------------

        public static PlayerBot Get(BotRosterEntry entry)
        {
            PlayerBot bot;

            return entry != null && _bots.TryGetValue(entry.Identity, out bot) && !bot.Deleted ? bot : null;
        }

        /// <summary>Where this named bot is against its post: "at post", or where it is instead.</summary>
        public static bool AtPost(PlayerBot bot, BotRosterEntry entry, out string where)
        {
            where = "offline";

            if (!IsOnline(bot))
            {
                return false;
            }

            NavDestination post = Nav.Destination(entry.Post);

            if (post == null)
            {
                where = "post not on the graph";
                return false;
            }

            var crafter = bot.Behavior as CrafterBehavior;

            if (crafter != null)
            {
                if (crafter.IsAtStation && Insensitive.Equals(crafter.DestinationId, entry.Post))
                {
                    where = "at post";
                    return true;
                }

                where = crafter.IsAtStation
                    ? "clocked in at " + crafter.DestinationId + " instead"
                    : "not clocked in (" + bot.X + "," + bot.Y + ")";

                return false;
            }

            if (bot.Behavior is BankSitterBehavior && bot.Map == post.Map && bot.InRange(post.Location, 16))
            {
                where = "at post";
                return true;
            }

            where = String.Format("{0} at {1},{2}", bot.Behavior == null ? "no brain" : bot.Behavior.SerializableName, bot.X, bot.Y);
            return false;
        }

        public static HealthResult BuildHealthResult()
        {
            if (BotRoster.LastError != null)
            {
                return HealthResult.Fail(String.Format(
                    "{0} did not load: {1}. The previous roster is in force ({2} named bot(s)).",
                    BotRoster.ConfigPath,
                    BotRoster.LastError,
                    BotRoster.Store.Bots.Count));
            }

            int roster = BotRoster.Store.Bots.Count;
            int online = 0;
            int atPost = 0;
            var offline = new List<string>();
            var away = new List<string>();
            var unlocked = new List<string>();

            foreach (BotRosterEntry entry in BotRoster.Store.Bots)
            {
                if (_refused.ContainsKey(entry.Identity))
                {
                    continue;
                }

                PlayerBot bot = Get(entry);

                if (bot == null)
                {
                    offline.Add(entry.Name + " (no character)");
                    continue;
                }

                var account = bot.Account as Account;

                if (account == null || !account.Banned || !IsBotAccount(account))
                {
                    unlocked.Add(entry.Name);
                }

                if (!IsOnline(bot))
                {
                    offline.Add(entry.Name + (_heldOffline.Contains(entry.Identity) ? " (held by command)" : ""));
                    continue;
                }

                online++;

                string where;

                if (AtPost(bot, entry, out where))
                {
                    atPost++;
                }
                else
                {
                    away.Add(entry.Name + " " + where);
                }
            }

            var text = new StringBuilder(480);

            text.AppendFormat(
                "{0} named in the roster: {1} online ({2} at their post), {3} offline, {4} refused, {5} retired. "
                + "Boot purge kept {6} named and deleted {7} throwaway(s). Since boot: {8} character(s) and {9} account(s) "
                + "created, {10} login(s), {11} logout(s), {12} death(s) and {13} corpse run(s) (interim rule), "
                + "{14} human login(s) refused on a bot account, {15} human character(s) renamed off a reserved name.",
                roster,
                online,
                atPost,
                offline.Count,
                _refused.Count,
                _retired.Count,
                BotStartupPurge.LastKept,
                BotStartupPurge.LastPurged,
                CharactersCreated,
                AccountsCreated,
                Logins,
                Logouts,
                Deaths,
                CorpseRuns,
                RefusedLogins,
                RenamedHumans);

            if (_deleted.Count > 0)
            {
                text.AppendFormat(" DELETED: {0}.", String.Join("; ", _deleted.ToArray()));

                return HealthResult.Fail(text + " A named bot is never destroyed.");
            }

            if (unlocked.Count > 0)
            {
                text.AppendFormat(" Account not locked: {0}.", String.Join(", ", unlocked.ToArray()));

                return HealthResult.Fail(text.ToString());
            }

            if (_refused.Count > 0)
            {
                var parts = new List<string>();

                foreach (var entry in _refused)
                {
                    parts.Add(entry.Key + ": " + entry.Value);
                }

                text.AppendFormat(" Refused: {0}.", String.Join("; ", parts.ToArray()));
            }

            if (_retired.Count > 0)
            {
                var names = new List<string>();

                foreach (PlayerBot bot in _retired)
                {
                    names.Add(bot.Name);
                }

                text.AppendFormat(" Retired (offline for ever): {0}.", String.Join(", ", names.ToArray()));
            }

            if (offline.Count > 0)
            {
                text.AppendFormat(" Offline: {0}.", String.Join(", ", offline.ToArray()));
            }

            bool settled = _loggedIn && Core.TickCount - (_loggedInAt + (long)SettlingTime.TotalMilliseconds) >= 0;

            if (away.Count > 0)
            {
                text.AppendFormat(" Away from post: {0}.", String.Join("; ", away.ToArray()));
            }

            if (_refused.Count > 0 || (settled && away.Count > 0))
            {
                return HealthResult.Warn(text.ToString());
            }

            int unheld = 0;

            foreach (string name in offline)
            {
                if (!name.EndsWith("(held by command)", StringComparison.Ordinal))
                {
                    unheld++;
                }
            }

            if (_loggedIn && unheld > 0)
            {
                return HealthResult.Warn(text.ToString());
            }

            if (!settled)
            {
                text.Append(" Settling: posts are asserted 150s after the logins.");
            }

            return HealthResult.Ok(text.ToString());
        }

        /// <summary>
        /// Data/Live/bot-named.json: every roster entry, its account, its character and where it
        /// stands - and, for the bots named in <paramref name="bankOf"/>, the bank box item by item.
        /// FindBankNoCreate throughout: inspecting a bot must never mint a box.
        /// </summary>
        public static string BuildJson(ICollection<string> bankOf)
        {
            var json = new StringBuilder(8192);

            json.Append("{\"utc\":").Append(Json.Quote(DateTime.UtcNow.ToString("o")));
            json.Append(",\"roster\":").Append(BotRoster.Store.Bots.Count);
            json.Append(",\"bots\":[");

            bool first = true;

            foreach (BotRosterEntry entry in BotRoster.Store.Bots)
            {
                PlayerBot bot = Get(entry);
                var account = bot == null ? Accounts.GetAccount(entry.Identity) as Account : bot.Account as Account;
                string refused;
                _refused.TryGetValue(entry.Identity, out refused);

                string where;
                bool atPost = AtPost(bot, entry, out where);

                json.Append(first ? "" : ",");
                first = false;

                json.Append("{\"name\":").Append(Json.Quote(entry.Name));
                json.Append(",\"identity\":").Append(Json.Quote(entry.Identity));
                json.Append(",\"class\":").Append(Json.Quote(entry.ParsedClass.ToString()));
                json.Append(",\"home\":").Append(Json.Quote(entry.Home));
                json.Append(",\"post\":").Append(Json.Quote(entry.Post));
                json.Append(",\"state\":").Append(Json.Quote(
                    refused != null ? "refused" : bot == null ? "missing" : IsOnline(bot) ? "online" : "offline"));

                if (refused != null)
                {
                    json.Append(",\"refused\":").Append(Json.Quote(refused));
                }

                json.Append(",\"account\":");

                if (account == null)
                {
                    json.Append("null");
                }
                else
                {
                    json.Append("{\"username\":").Append(Json.Quote(account.Username));
                    json.Append(",\"banned\":").Append(account.Banned ? "true" : "false");
                    json.Append(",\"tagged\":").Append(IsBotAccount(account) ? "true" : "false");
                    json.Append('}');
                }

                if (bot != null)
                {
                    json.Append(",\"serial\":").Append(Json.Quote(String.Format("0x{0:X}", bot.Serial.Value)));
                    json.Append(",\"map\":").Append(Json.Quote(bot.Map == null ? "none" : bot.Map.Name));
                    json.Append(",\"x\":").Append(bot.X).Append(",\"y\":").Append(bot.Y).Append(",\"z\":").Append(bot.Z);
                    json.Append(",\"behaviour\":").Append(Json.Quote(bot.Behavior == null ? "none" : bot.Behavior.SerializableName));
                    json.Append(",\"atPost\":").Append(atPost ? "true" : "false");
                    json.Append(",\"where\":").Append(Json.Quote(where));

                    BankBox box = bot.FindBankNoCreate();

                    json.Append(",\"bankItems\":").Append(box == null ? 0 : box.Items.Count);

                    // Its gold, where it is (7f-1). Pack gold is what the trade path spends;
                    // bank and account are counted and untouched (ECONOMY.md section 2).
                    long pack = BotGoldLedger.PackGold(bot);
                    long bank = BotGoldLedger.BankGold(bot);
                    long inAccount = BotGoldLedger.AccountGold(bot);

                    json.Append(",\"gold\":{\"pack\":").Append(pack);
                    json.Append(",\"bank\":").Append(bank);
                    json.Append(",\"account\":").Append(inAccount);
                    json.Append(",\"total\":").Append(pack + bank + inAccount);
                    json.Append(",\"startingGranted\":").Append(bot.StartingGoldGranted ? "true" : "false");
                    json.Append(",\"startingRemaining\":").Append(bot.StartingGoldRemaining);
                    json.Append('}');

                    if (bankOf != null && box != null && Contains(bankOf, entry))
                    {
                        json.Append(",\"bank\":[");

                        for (int i = 0; i < box.Items.Count; i++)
                        {
                            Item item = box.Items[i];

                            json.Append(i == 0 ? "" : ",");
                            json.Append("{\"serial\":").Append(Json.Quote(String.Format("0x{0:X}", item.Serial.Value)));
                            json.Append(",\"type\":").Append(Json.Quote(item.GetType().Name));
                            json.Append(",\"name\":").Append(Json.Quote(item.Name ?? ""));
                            json.Append(",\"amount\":").Append(item.Amount);
                            json.Append('}');
                        }

                        json.Append(']');
                    }
                }

                json.Append('}');
            }

            json.Append("],\"retired\":[");

            for (int i = 0; i < _retired.Count; i++)
            {
                json.Append(i == 0 ? "" : ",").Append(Json.Quote(_retired[i].Name));
            }

            json.Append("]}");

            return json.ToString();
        }

        private static bool Contains(ICollection<string> names, BotRosterEntry entry)
        {
            foreach (string name in names)
            {
                if (Insensitive.Equals(name, entry.Name) || Insensitive.Equals(name, entry.Identity))
                {
                    return true;
                }
            }

            return false;
        }

        // -------------------------------------------------------------------
        // [BotNamed and the bot-named token
        // -------------------------------------------------------------------

        [Usage("BotNamed [list | mark <name> [text] | bank <name> | logout <name> | login <name>]")]
        [Description(
            "The named cast: list every roster entry with its account and where it stands; put a marked "
            + "scroll in a named bot's bank box; list a bank box; log a named bot out or back in. "
            + "Every form also writes Data/Live/bot-named.json.")]
        private static void BotNamed_OnCommand(CommandEventArgs e)
        {
            string message;
            List<string> lines;

            Run(e.ArgString, out message, out lines);

            foreach (string line in lines)
            {
                e.Mobile.SendMessage(line);
            }

            e.Mobile.SendMessage(message);
        }

        /// <summary>The command and the token share this. False only for a malformed request.</summary>
        public static bool Run(string body, out string message, out List<string> lines)
        {
            lines = new List<string>();

            var words = new List<string>();

            foreach (string word in (body ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // A '#word' is not an argument (the bridge's old nonce convention; see botinfo).
                if (!word.StartsWith("#"))
                {
                    words.Add(word);
                }
            }

            string verb = words.Count == 0 ? "list" : words[0].ToLowerInvariant();
            BotRosterEntry entry = null;
            PlayerBot bot = null;
            var bankOf = new List<string>();

            if (verb != "list")
            {
                if (words.Count < 2)
                {
                    message = "BotNamed " + verb + " needs a name";
                    return false;
                }

                entry = BotRoster.Find(words[1]);
                bot = Get(entry);

                if (entry == null || bot == null)
                {
                    message = String.Format("no named bot called '{0}'", words[1]);
                    return false;
                }
            }

            switch (verb)
            {
                case "list":
                    message = String.Format("{0} named bot(s); wrote {1}", BotRoster.Store.Bots.Count, SnapshotPath);
                    break;

                case "mark":
                {
                    string text = words.Count > 2 ? String.Join(" ", words.GetRange(2, words.Count - 2).ToArray()) : "";

                    // NOT gold: with an Account behind the bot, gold dropped in its bank box is turned
                    // into account currency and the item deleted (Gold.cs:91-121). A scroll is an item
                    // that stays an item, which is what a marker has to be.
                    var marker = new BlankScroll();
                    marker.Name = ("GG marker " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + " " + text).Trim();

                    // The creating getter, on purpose: a named bot may own a bank box, and this is it.
                    bot.BankBox.DropItem(marker);

                    bankOf.Add(entry.Name);
                    message = String.Format("put '{0}' (0x{1:X}) in {2}'s bank box", marker.Name, marker.Serial.Value, bot.Name);
                    break;
                }

                case "bank":
                    bankOf.Add(entry.Name);
                    message = String.Format("{0}'s bank box: see {1}", bot.Name, SnapshotPath);
                    break;

                case "logout":
                    _heldOffline.Add(entry.Identity);
                    message = LogOut(bot, "command")
                        ? String.Format("{0} logged out; held offline until [BotNamed login", bot.Name)
                        : String.Format("{0} was already offline; now held offline", bot.Name);
                    break;

                case "login":
                    _heldOffline.Remove(entry.Identity);

                    if (_refused.ContainsKey(entry.Identity))
                    {
                        message = String.Format("{0} is refused: {1}", entry.Name, _refused[entry.Identity]);
                        return false;
                    }

                    message = IsOnline(bot)
                        ? String.Format("{0} is already online", bot.Name)
                        : LogIn(bot, entry, "command")
                            ? String.Format("{0} logged in at {1}", bot.Name, entry.Post)
                            : String.Format("{0} could not log in", bot.Name);
                    break;

                default:
                    message = "BotNamed takes list, mark, bank, logout or login";
                    return false;
            }

            foreach (BotRosterEntry each in BotRoster.Store.Bots)
            {
                PlayerBot named = Get(each);
                string where;

                AtPost(named, each, out where);

                string refused;
                _refused.TryGetValue(each.Identity, out refused);

                lines.Add(String.Format(
                    "{0} - {1} {2} of {3}: {4}{5}",
                    each.Name,
                    BotClassHelper.DisplayName(each.ParsedClass),
                    each.Post,
                    each.Home,
                    refused != null ? "REFUSED " + refused : named == null ? "no character" : where,
                    named == null ? "" : String.Format(" (0x{0:X}, bank {1}, gold {2:N0})",
                        named.Serial.Value,
                        named.FindBankNoCreate() == null ? 0 : named.FindBankNoCreate().Items.Count,
                        BotGoldLedger.GoldOf(named))));
            }

            string error;

            if (!AtomicFile.Write(SnapshotPath, BuildJson(bankOf), out error))
            {
                message += "; the snapshot was NOT written: " + error;
            }

            return true;
        }

        /// <summary>After a roster reload: make the world match it and log in whoever can be.</summary>
        public static void Apply()
        {
            Ensure();

            if (_loggedIn)
            {
                LogInAll("roster reload");
            }
        }
    }
}
