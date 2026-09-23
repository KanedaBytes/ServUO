// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// NamedBotFixtures.cs - the named cast, proved from [CoreSmoke.
//
// MountFixtures' shape: real mobiles and real accounts, one Expect per claim, everything the fixture
// made taken away again in a finally. Fixture accounts are named "GGFixture..." and are removed
// before the call returns, so none can reach accounts.xml unless a save lands inside a synchronous
// smoke run, which it cannot.
//
// WHAT IS PROVED HERE, AND WHAT IS NOT. Every path of OURS a named bot takes across a session
// boundary is exercised on a real bot: the logout, the purge's decision, the login, the ledger's
// bucket, the bank box and the marker in it. The ENGINE half - that the world save writes the mobile
// and its bank box, and World.Load brings them back - cannot be run inside a smoke that must not
// restart the shard. That half is proved live, by marking a named bot's bank with [BotNamed mark,
// saving, stopping, booting and reading it back; the fixture says so in its own report line.
//
// The counters these paths move (a refused login, a renamed human, an account created) are put back
// afterwards: a [CoreSmoke run must not read on Bots.Named as something that happened to the shard.

using System;
using System.Collections.Generic;

using Server.Accounting;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
    public static class NamedBotFixtures
    {
        private const string FixtureAccount = "GGFixtureNamedBot";
        private const string FixtureHuman = "GGFixtureHumanAcct";

        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- named bot fixtures --");

            bool passed = true;

            int created = NamedBots.CharactersCreated;
            int accounts = NamedBots.AccountsCreated;
            int logins = NamedBots.Logins;
            int logouts = NamedBots.Logouts;
            int refused = NamedBots.RefusedLogins;
            int renamed = NamedBots.RenamedHumans;
            int deaths = NamedBots.Deaths;
            int runs = NamedBots.CorpseRuns;

            try
            {
                passed &= FixtureRosterValidation(report);
                passed &= FixtureAccountIsIdempotent(report);
                passed &= FixtureHumanLoginRefused(report);
                passed &= FixtureBootPurgeKeepsNamed(report);
                passed &= FixtureBankSurvivesTheSessionBoundary(report);
                passed &= FixtureRegenLeavesNamed(report);
                passed &= FixtureReservedName(report);
                passed &= FixtureInterimDeath(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }
            finally
            {
                NamedBots.CharactersCreated = created;
                NamedBots.AccountsCreated = accounts;
                NamedBots.Logins = logins;
                NamedBots.Logouts = logouts;
                NamedBots.RefusedLogins = refused;
                NamedBots.RenamedHumans = renamed;
                NamedBots.Deaths = deaths;
                NamedBots.CorpseRuns = runs;

                RemoveAccount(FixtureAccount);
                RemoveAccount(FixtureHuman);
            }

            return passed;
        }

        // ---- 1. the roster refuses what it should ----

        private static bool FixtureRosterValidation(List<string> report)
        {
            bool ok = Expect(report,
                Errors(Store(Entry("Drizzt", "Ranger"), Entry("Gimli", "Smith", "brit-forge"))) == "",
                "a valid roster loads with no errors",
                "a valid roster was refused: " + Errors(Store(Entry("Drizzt", "Ranger"), Entry("Gimli", "Smith", "brit-forge"))));

            string duplicate = Errors(Store(Entry("Drizzt", "Ranger"), Entry("drizzt", "Mage")));

            ok &= Expect(report,
                duplicate.Contains("already"),
                "a duplicate name is refused, whatever its case",
                "a duplicate name loaded: '" + duplicate + "'");

            string unknownClass = Errors(Store(Entry("Drizzt", "Wizard")));

            ok &= Expect(report,
                unknownClass.Contains("not a bot class"),
                "an unknown class is refused",
                "class Wizard loaded: '" + unknownClass + "'");

            string legacy = Errors(Store(Entry("Drizzt", "Crafter")));

            ok &= Expect(report,
                legacy.Contains("not a bot class"),
                "the legacy Crafter class is refused",
                "class Crafter loaded: '" + legacy + "'");

            string outside = Errors(Store(Entry("Elrond", "Mage")));

            ok &= Expect(report,
                outside.Contains("not in the pool"),
                "a name outside the pool is refused",
                "a name outside the pool loaded: '" + outside + "'");

            BotRosterEntry renamed = Entry("Thorin", "Smith", "brit-forge");
            renamed.RenamedFrom = "Gimli";

            ok &= Expect(report,
                Errors(Store(renamed)) == "" && renamed.Identity == "Gimli",
                "a rename keeps its pool name as its identity and loads",
                "a valid rename was refused: " + Errors(Store(renamed)));

            BotRosterEntry stolen = Entry("Drizzt", "Mage");
            stolen.RenamedFrom = "Gimli";

            ok &= Expect(report,
                Errors(Store(stolen, Entry("Gimli", "Smith", "brit-forge"))).Contains("already"),
                "a rename cannot take another bot's identity",
                "two bots loaded with one identity");

            // The posts, against the real graph.
            Map map = BotPopulation.Facet;

            BotRosterEntry nowhere = Valid(Entry("Drizzt", "Ranger", "no-such-post"));
            string nowhereError = BotRoster.PostError(nowhere, map);

            ok &= Expect(report,
                nowhereError != null && nowhereError.Contains("not a destination"),
                "an unknown post is refused",
                "post no-such-post was accepted: " + (nowhereError ?? "no error"));

            BotRosterEntry wrongTrade = Valid(Entry("Regis", "Tailor", "brit-forge"));
            string tradeError = BotRoster.PostError(wrongTrade, map);

            ok &= Expect(report,
                tradeError != null,
                "a tailor posted to a forge is refused",
                "a tailor was accepted at brit-forge");

            ok &= Expect(report,
                BotRoster.PostError(Valid(Entry("Gimli", "Smith", "brit-forge")), map) == null
                    && BotRoster.PostError(Valid(Entry("Drizzt", "Ranger", "brit-bank")), map) == null,
                "a smith at a forge and a ranger at a bank are accepted",
                "a valid post was refused");

            var twoSmiths = Store(Entry("Gimli", "Smith", "brit-forge"), Entry("Flint", "Smith", "brit-forge"));
            Errors(twoSmiths);

            ok &= Expect(report,
                BotRoster.PostErrors(twoSmiths, map).ContainsKey("Flint"),
                "a second bot on a one-bot station is refused",
                "two smiths were posted to one forge");

            return ok;
        }

        // ---- 2. an account is created once ----

        private static bool FixtureAccountIsIdempotent(List<string> report)
        {
            RemoveAccount(FixtureAccount);
            RemoveAccount(FixtureHuman);

            int before = Accounts.Count;
            string error;

            Account first = NamedBots.EnsureAccount(FixtureAccount, out error);
            Account second = NamedBots.EnsureAccount(FixtureAccount, out error);

            bool ok = Expect(report,
                first != null && ReferenceEquals(first, second) && Accounts.Count == before + 1,
                "ensuring an account twice makes one account",
                String.Format("first {0}, second {1}, count {2} -> {3}", first, second, before, Accounts.Count));

            ok &= Expect(report,
                first != null && first.Banned && NamedBots.IsBotAccount(first) && !first.Young
                    && first.AccessLevel == AccessLevel.Player,
                "and it is banned, tagged as a bot's, not Young, and Player-level",
                "the account is not locked and tagged");

            // A person's account of the same name is refused and not modified.
            var human = new Account(FixtureHuman, "fixture-password");

            Account taken = NamedBots.EnsureAccount(FixtureHuman, out error);

            ok &= Expect(report,
                taken == null && !human.Banned && !NamedBots.IsBotAccount(human)
                    && human.CheckPassword("fixture-password"),
                "a person's account with the name is refused and left exactly as it was",
                "a person's account was taken over or changed");

            return ok;
        }

        // ---- 3. no human logs in on a bot account ----

        private static bool FixtureHumanLoginRefused(List<string> report)
        {
            string error;
            Account account = NamedBots.EnsureAccount(FixtureAccount, out error);

            bool ok = Expect(report,
                account != null && account.Banned && !account.CheckPassword("password")
                    && !account.CheckPassword(FixtureAccount),
                "a bot account is banned and no guessed password opens it",
                "a bot account can be opened");

            var login = new AccountLoginEventArgs(null, FixtureAccount, "password");
            NamedBots.GuardAccountLogin(login);

            ok &= Expect(report,
                !login.Accepted && login.RejectReason == ALRReason.Blocked,
                "the login guard refuses it even with Accepted set, as Blocked",
                "the login guard let an account login through");

            var game = new GameLoginEventArgs(null, FixtureAccount, "password");
            game.Accepted = true;
            NamedBots.GuardGameLogin(game);

            ok &= Expect(report,
                !game.Accepted,
                "and refuses the game-server login too",
                "the game login guard let it through");

            var control = new AccountLoginEventArgs(null, FixtureHuman, "fixture-password");
            NamedBots.GuardAccountLogin(control);

            ok &= Expect(report,
                control.Accepted,
                "while a person's account is left for the stock handler to judge",
                "the guard refused a person's account");

            return ok;
        }

        // ---- 4. the boot purge keeps a named bot ----

        private static bool FixtureBootPurgeKeepsNamed(List<string> report)
        {
            PlayerBot named = null;
            PlayerBot throwaway = null;

            try
            {
                named = MakeNamed();
                throwaway = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);
                throwaway.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);

                int kept;
                List<PlayerBot> stale = BotStartupPurge.Collect(new Mobile[] { named, throwaway }, out kept);

                bool ok = Expect(report,
                    kept == 1 && stale.Count == 1 && stale[0] == throwaway,
                    "the purge's own pass keeps the named bot and takes the throwaway",
                    String.Format("kept {0}, stale {1}", kept, stale.Count));

                ok &= Expect(report,
                    !BotStartupPurge.IsStale(named) && BotStartupPurge.IsStale(throwaway),
                    "IsStale says no to a named bot and yes to a throwaway",
                    "IsStale gave the wrong answer");

                ok &= Expect(report,
                    BotGoodsLedger.IsOffline(named),
                    "and the kept bot's holdings are held offline, not lost",
                    "a kept named bot is not in the ledger's offline bucket");

                return ok;
            }
            finally
            {
                Unmake(named);
                Unmake(throwaway);
            }
        }

        // ---- 5. a bank box survives a logout, the purge's decision and a login ----

        private static bool FixtureBankSurvivesTheSessionBoundary(List<string> report)
        {
            PlayerBot bot = null;
            Item marker = null;

            try
            {
                BotRosterEntry entry = Valid(Entry("Fixturebot", "Warrior", "brit-bank"));

                bot = MakeNamed();

                string error;
                Account account = NamedBots.EnsureAccount(FixtureAccount, out error);
                account[0] = bot;

                bool ok = Expect(report,
                    NamedBots.LogIn(bot, entry, "fixture") && NamedBots.IsOnline(bot)
                        && LiveRegistry.Snapshot().Contains(bot),
                    "a named bot logs in at its post and onto the live map",
                    "the named bot did not log in");

                marker = new BlankScroll();
                marker.Name = "GG fixture marker";
                bot.BankBox.DropItem(marker);

                BankBox box = bot.FindBankNoCreate();
                Point3D stood = bot.Location;
                Map map = bot.Map;

                ok &= Expect(report,
                    NamedBots.LogOut(bot, "fixture"),
                    "it logs out",
                    "LogOut refused an online named bot");

                ok &= Expect(report,
                    !bot.Deleted && bot.Map == Map.Internal && bot.LogoutLocation == stood && bot.LogoutMap == map
                        && !LiveRegistry.Snapshot().Contains(bot),
                    "and is OFFLINE, not deleted: on Map.Internal, logout location kept, off the live map",
                    "logging out did not leave the engine's offline state");

                ok &= Expect(report,
                    !marker.Deleted && marker.Parent == box && bot.FindBankNoCreate() == box,
                    "the marker is still in the same bank box, same serial",
                    "the marker or the bank box did not survive the logout");

                ok &= Expect(report,
                    BotGoodsLedger.IsOffline(bot) && !BotStartupPurge.IsStale(bot),
                    "its holdings are in the ledger's offline bucket and the boot purge would keep it",
                    "an offline named bot is not held, or would be purged");

                ok &= Expect(report,
                    bot.Account == account && account[0] == bot,
                    "and it is still its account's character",
                    "the account lost its character");

                ok &= Expect(report,
                    NamedBots.LogIn(bot, entry, "fixture") && NamedBots.IsOnline(bot)
                        && !marker.Deleted && marker.Parent == bot.FindBankNoCreate()
                        && !BotGoodsLedger.IsOffline(bot),
                    "logging back in finds the marker where it was, and takes it out of the offline bucket",
                    "the marker did not come back with the bot");

                report.Add("  note: the world save and World.Load half is proved live by [BotNamed mark, save, restart, [BotNamed bank - not here");

                return ok;
            }
            finally
            {
                if (bot != null && bot.Account is Account)
                {
                    var account = (Account)bot.Account;

                    for (int i = 0; i < account.Length; i++)
                    {
                        if (account[i] == bot)
                        {
                            account[i] = null;
                        }
                    }
                }

                Unmake(bot);

                if (marker != null && !marker.Deleted)
                {
                    marker.Delete();
                }
            }
        }

        // ---- 6. the regen touches only throwaway spawners ----

        private static bool FixtureRegenLeavesNamed(List<string> report)
        {
            BotRecipe recipe = BotPopulation.Build(BotPopulation.Facet);

            bool noFixed = true;

            foreach (BotSlot slot in recipe.Spawners)
            {
                if (slot.IsNamedPost || slot.Objects2.IndexOf("/Role/Fixed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    noFixed = false;
                }
            }

            bool ok = Expect(report,
                noFixed && recipe.Spawners.Count < recipe.Slots.Count,
                "[BotPopulationGen writes no fixed post as a spawner - the named posts are the roster's",
                "the generator would still write a fixed-role spawner");

            // [GG_Reimport and XmlSpawner.Respawn delete only what a spawner spawned
            // (RemoveSpawnObjects walks its own SpawnObjects lists), so a bot on no spawner is out of
            // their reach by construction. Asserted on every named bot in the world, not on a copy.
            var onSpawner = new List<string>();
            int looked = 0;

            foreach (BotRosterEntry entry in BotRoster.Store.Bots)
            {
                PlayerBot bot = NamedBots.Get(entry);

                if (bot == null)
                {
                    continue;
                }

                looked++;

                if (bot.Spawner != null)
                {
                    onSpawner.Add(bot.Name);
                }
            }

            ok &= Expect(report,
                onSpawner.Count == 0,
                String.Format("none of the {0} named bot(s) in the world is on a spawner, so a reimport cannot reach one", looked),
                "named bot(s) on a spawner: " + String.Join(", ", onSpawner.ToArray()));

            PlayerBot fresh = null;

            try
            {
                fresh = MakeNamed();

                ok &= Expect(report,
                    fresh.Spawner == null && fresh.Role == BotRole.Fixed && fresh.IsNamed,
                    "and a newly made named bot is on no spawner, fixed-role and named",
                    "a new named bot was not built as one");
            }
            finally
            {
                Unmake(fresh);
            }

            return ok;
        }

        // ---- 7. a person cannot take a named bot's name ----

        private static bool FixtureReservedName(List<string> report)
        {
            Mobile human = null;
            string reserved = BotRoster.Store.Bots.Count > 0 ? BotRoster.Store.Bots[0].Name : null;

            if (reserved == null)
            {
                report.Add("  FAIL: the roster is empty, so no name is reserved");
                return false;
            }

            try
            {
                human = new PlayerMobile();
                human.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);
                human.RawName = reserved.ToUpperInvariant();

                bool ok = Expect(report,
                    NamedBots.RenameIfReserved(human, "fixture") && human.RawName == NamedBots.FallbackName,
                    String.Format("a person's character called {0} is renamed {1}", reserved.ToUpperInvariant(), NamedBots.FallbackName),
                    "a person kept a named bot's name");

                ok &= Expect(report,
                    !NamePool.Claim(reserved),
                    "and no throwaway can be given it either",
                    "the name pool handed out a named bot's name");

                return ok;
            }
            finally
            {
                if (human != null && !human.Deleted)
                {
                    human.Delete();
                }
            }
        }

        // ---- 8. the INTERIM death rule: a named bot that dies comes back ----

        /// <summary>
        /// Killed for real at a real post, then the corpse run the one-second timer would do, run
        /// here instead so the smoke stays synchronous (the timer, when it fires, finds the bot gone).
        /// INTERIM, with the rule it proves: 7h Death replaces both.
        /// </summary>
        private static bool FixtureInterimDeath(List<string> report)
        {
            PlayerBot bot = null;
            Container corpse = null;

            try
            {
                BotRosterEntry entry = Valid(Entry("Fixturebot", "Warrior", "brit-bank"));

                bot = MakeNamed();

                // Marked BEFORE the death, so OnDeath knows it is a fixture's and BotDeaths is not told.
                bot.DeletionReason = NamedBots.FixtureReason;

                NamedBots.LogIn(bot, entry, "fixture");

                int wornBefore = Worn(bot);

                bot.Kill();

                corpse = bot.Corpse;

                bool ok = Expect(report,
                    !bot.Deleted && !bot.Alive && bot.CorpseRunPending && corpse != null,
                    "a named bot that dies is a ghost with a corpse, not a deleted mobile",
                    String.Format("after Kill: deleted {0}, alive {1}, pending {2}, corpse {3}",
                        bot.Deleted, bot.Alive, bot.CorpseRunPending, corpse != null));

                NamedBots.CorpseRun(bot);

                ok &= Expect(report,
                    !bot.Deleted && bot.Alive && !bot.CorpseRunPending && bot.Hits == bot.HitsMax,
                    "the corpse run resurrects it at full health (interim rule; 7h replaces it)",
                    String.Format("after the run: alive {0}, pending {1}, hits {2}/{3}",
                        bot.Alive, bot.CorpseRunPending, bot.Hits, bot.HitsMax));

                int wornAfter = Worn(bot);

                ok &= Expect(report,
                    wornAfter == wornBefore && !(bot.FindItemOnLayer(Layer.OuterTorso) is DeathRobe)
                        && (corpse == null || Remaining(corpse) == 0),
                    String.Format("and it is wearing what it wore ({0} item(s)), no death robe, nothing left on the corpse", wornAfter),
                    String.Format("worn {0} -> {1}, robe {2}, left on corpse {3}",
                        wornBefore, wornAfter, bot.FindItemOnLayer(Layer.OuterTorso) is DeathRobe,
                        corpse == null ? 0 : Remaining(corpse)));

                return ok;
            }
            finally
            {
                Unmake(bot);

                if (corpse != null && !corpse.Deleted)
                {
                    corpse.Delete();
                }
            }
        }

        private static int Worn(Mobile mobile)
        {
            int worn = 0;

            foreach (Item item in mobile.Items)
            {
                if (item.Layer != Layer.Backpack && item.Layer != Layer.Bank && item.Layer != Layer.Hair
                    && item.Layer != Layer.FacialHair && !(item is DeathRobe))
                {
                    worn++;
                }
            }

            return worn;
        }

        private static int Remaining(Container corpse)
        {
            int left = 0;

            foreach (Item item in corpse.Items)
            {
                if (item.Layer != Layer.Hair && item.Layer != Layer.FacialHair)
                {
                    left++;
                }
            }

            return left;
        }

        // ---- helpers ----

        private static BotRosterEntry Entry(string name, string cls, string post = "brit-bank")
        {
            return new BotRosterEntry { Name = name, Class = cls, Home = "britain", Post = post };
        }

        private static BotRosterEntry Valid(BotRosterEntry entry)
        {
            BotClass cls;

            if (BotRoster.TryParseClass(entry.Class, out cls))
            {
                entry.ParsedClass = cls;
            }

            return entry;
        }

        private static BotRosterStore Store(params BotRosterEntry[] entries)
        {
            return new BotRosterStore
            {
                Pool = new List<string> { "Drizzt", "Gimli", "Flint", "Regis" },
                Bots = new List<BotRosterEntry>(entries)
            };
        }

        private static string Errors(BotRosterStore store)
        {
            var errors = new ConfigErrors();

            store.Validate(errors);

            return String.Join(" | ", new List<string>(errors.Items).ToArray());
        }

        /// <summary>A named bot of the fixture's own, offline on Map.Internal, owning nothing.</summary>
        private static PlayerBot MakeNamed()
        {
            BotRosterEntry entry = Valid(Entry("Fixturebot", "Warrior"));
            entry.RenamedFrom = FixtureAccount;

            PlayerBot bot = PlayerBot.CreateNamed(entry);
            bot.MoveToWorld(new Point3D(0, 0, 0), Map.Internal);

            return bot;
        }

        private static void Unmake(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.DeletionReason = NamedBots.FixtureReason;
            bot.Delete();
        }

        private static void RemoveAccount(string username)
        {
            var account = Accounts.GetAccount(username) as Account;

            if (account == null)
            {
                return;
            }

            // Clear the slots first: Accounts.Remove alone would leave Mobile.Account pointing at an
            // account nothing holds any more.
            for (int i = 0; i < account.Length; i++)
            {
                account[i] = null;
            }

            Accounts.Remove(username);
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);

            return condition;
        }
    }
}
