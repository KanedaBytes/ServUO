using System;
using System.Collections.Generic;
using System.IO;

using Server.Commands;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Staff tooling for this shard's own XmlSpawner definitions.
    ///
    /// Every custom spawner is named with the GG_ prefix (for "Goblin Gang"), which is not
    /// decoration: both [XmlLoad and [XmlUnLoad take an optional second argument that is an
    /// ordinal Name.StartsWith filter, so the prefix is what makes the custom spawners
    /// addressable as a set without touching the ~2,500 stock ones.
    /// </summary>
    public static class GGSpawnCommands
    {
        /// <summary>The name prefix every spawner under Spawns/Custom must carry.</summary>
        public const string Prefix = "GG_";

        /// <summary>Repo-root relative. [XmlLoad recurses directories.</summary>
        public const string SpawnRoot = "Spawns/Custom";

        /// <summary>Named here so the quest health check and the command agree on one string.</summary>
        public const string MartaSpawnerName = "GG_OldMarta";

        private static readonly CustomLogger Log = CustomLogger.For("Spawns");

        public static void Initialize()
        {
            // Both names are registered explicitly. [Aliases] is documentation only - HelpInfo and
            // Docs read it, but CommandSystem.Handle looks up registered names alone.
            CommandSystem.Register("GG_Reimport", AccessLevel.Administrator, GGReimport_OnCommand);
            CommandSystem.Register("GGReimport", AccessLevel.Administrator, GGReimport_OnCommand);
        }

        [Usage("GG_Reimport")]
        [Aliases("GGReimport")]
        [Description("Deletes every GG_-prefixed XmlSpawner and re-imports Spawns/Custom from disk.")]
        private static void GGReimport_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            string summary;
            string error;

            if (!TryReimport(from, out summary, out error))
            {
                from.SendMessage(0x35, error);
                return;
            }

            from.SendMessage(summary);

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} reimporting {2}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    SpawnRoot));
        }

        /// <summary>
        /// The reimport itself, callable without a Mobile.
        ///
        /// The editor bridge drops a request token and nobody is holding the command, so this
        /// has to work headless. XmlSpawner supports that - it has a Mobile-free overload and
        /// guards every from.SendMessage with a null check - but the six-argument overload the
        /// command used dereferences from.Location unconditionally, so the null path has to pick
        /// the other one deliberately.
        /// </summary>
        public static bool TryReimport(Mobile from, out string summary, out string error)
        {
            summary = null;
            error = null;

            string path = Path.Combine(Core.BaseDirectory, SpawnRoot);

            if (!Directory.Exists(path))
            {
                error = String.Format("{0} does not exist. Nothing to import.", SpawnRoot);
                return false;
            }

            // VALIDATE BEFORE DELETING, which every other reload path in this shard does and this
            // one did not.
            //
            // The delete used to run outside the try, and XmlLoadFromStream does not throw on a
            // malformed file - it logs, sets a flag and returns with zero spawners
            // (XmlSpawner2.cs:6141-6153). So a bad XML file produced a cheerful
            // "deleted 7, imported 0" and an empty world, and the catch below caught nothing at
            // all. Reading each file the way the loader will is a faithful dry run of exactly that
            // failure.
            int expected;

            if (!TryCountSpawnPoints(path, out expected, out error))
            {
                return false;
            }

            int deleted = DeleteExisting();

            int maps = 0;
            int spawners = 0;

            try
            {
                // The public loader, rather than re-entering the command parser. loadrelative
                // false, maxrange 0, loadnew false - loadnew would mint fresh GUIDs and append a
                // suffix to every name, duplicating rather than replacing.
                if (from != null)
                {
                    XmlSpawner.XmlLoadFromFile(SpawnRoot, Prefix, from, false, 0, false, out maps, out spawners);
                }
                else
                {
                    XmlSpawner.XmlLoadFromFile(SpawnRoot, Prefix, false, out maps, out spawners);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GG_Reimport failed while loading {0}.", SpawnRoot);
                error = String.Format("Import failed: {0}", ex.Message);
                return false;
            }

            // XmlLoadFromFile does not surface its own per-row bad_spawner counters, so the only
            // way to notice that some rows did not make it is to have counted them first.
            if (spawners < expected)
            {
                error = String.Format(
                    "Imported {0} spawner(s) but {1} are in the files - {2} were rejected. "
                    + "See badxml.log; the world now has only the ones that loaded.",
                    spawners,
                    expected,
                    expected - spawners);

                Log.Error(error);
                return false;
            }

            summary = String.Format(
                "GG_Reimport: deleted {0} existing '{1}' spawner(s), imported {2} spawner(s) across {3} map(s) from {4}.",
                deleted,
                Prefix,
                spawners,
                maps,
                SpawnRoot);

            Log.Info(summary);

            // The spawners are new, so their NPCs know nothing about the phase the town is
            // currently in. Without this a reimport at night leaves six shopkeepers standing in
            // their shops until dawn, because the only thing that ever moves them is a phase
            // transition - and they were not there for the last one.
            DailyLifeCommands.Reconcile();
            // And fill the bot spawners now rather than on their own five-to-fifteen minute
            // timers. Same argument BotStartupManager makes upstream, in the same words: a
            // reimport that leaves the towns empty for a quarter of an hour reads as a
            // reimport that did not work. RespawnAll only touches GG_BotPop_ spawners.
            BotPopulation.RespawnAll();

            // Same reason the per-file path writes one: without it the editor's view of the world
            // is up to a minute stale immediately after the one command that changed it most.
            SpawnerSnapshot.Write();

            return true;
        }

        /// <summary>
        /// Reloads ONE spawn file, which is what the editor does on every save.
        ///
        /// [GG_Reimport is the wrong tool for a save: it deletes every GG_ spawner in the world
        /// and re-imports the whole tree, so editing Old Marta would make the six shopkeepers
        /// vanish and reappear. This touches only the spawners in one file.
        ///
        /// UNLOAD FROM THE BACKUP, LOAD FROM THE FILE. [XmlLoad replaces by UniqueId, so loading
        /// alone is an upsert - a spawner the editor DELETED is no longer named by the new file
        /// and would be orphaned in the world forever. The bridge writes a .bak before every save,
        /// and that is precisely the old GUID set, so unloading from it removes exactly what the
        /// file used to contain. A missing .bak means a brand-new file with nothing to unload,
        /// which is reported rather than guessed at.
        ///
        /// `relative` is a file NAME under Spawns/Custom, never a path. It is resolved against
        /// that root and anything landing outside is refused - the same check the bridge's
        /// whitelist makes, enforced here too rather than trusted from there.
        /// </summary>
        public static bool TryReloadFile(string relative, out string summary, out string error)
        {
            summary = null;
            error = null;

            string file;

            if (!TryResolve(relative, out file, out error))
            {
                return false;
            }

            int expected;

            if (!TryCountSpawnPoints(file, out expected, out error))
            {
                return false;
            }

            string backup = file + ".bak";
            bool hadBackup = File.Exists(backup);
            int removed = 0;

            try
            {
                if (hadBackup)
                {
                    int unloadedMaps;

                    // Mobile-free by passing null: XmlUnLoadFromStream null-checks every
                    // SendMessage, unlike the six-argument load overload.
                    XmlSpawner.XmlUnLoadFromFile(backup, Prefix, null, out unloadedMaps, out removed);
                }

                int maps;
                int spawners;

                XmlSpawner.XmlLoadFromFile(file, Prefix, false, out maps, out spawners);

                if (spawners < expected)
                {
                    error = String.Format(
                        "Imported {0} spawner(s) but {1} are in {2} - {3} were rejected. See badxml.log.",
                        spawners, expected, relative, expected - spawners);

                    Log.Error(error);
                    return false;
                }

                summary = String.Format(
                    "{0}: removed {1}, imported {2} spawner(s){3}",
                    relative,
                    removed,
                    spawners,
                    hadBackup ? "" : " (no .bak yet, so nothing was removed first)");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reloading {0} failed.", relative);
                error = String.Format("Reloading {0} failed: {1}", relative, ex.Message);
                return false;
            }

            Log.Info(summary);

            // Same reason [GG_Reimport does it: the spawners are new, so their NPCs know nothing
            // about the phase the town is currently in.
            DailyLifeCommands.Reconcile();
            // And fill the bot spawners now rather than on their own five-to-fifteen minute
            // timers. Same argument BotStartupManager makes upstream, in the same words: a
            // reimport that leaves the towns empty for a quarter of an hour reads as a
            // reimport that did not work. RespawnAll only touches GG_BotPop_ spawners.
            BotPopulation.RespawnAll();

            SpawnerSnapshot.Write();

            return true;
        }

        /// <summary>
        /// Resolves a spawn file name under Spawns/Custom, refusing anything that leaves it.
        ///
        /// GetFullPath rather than a string test, so `a/../../x` and every other spelling of the
        /// same idea collapse to one comparison. The trailing separator matters: without it a
        /// sibling directory whose name merely starts with the root would pass.
        /// </summary>
        private static bool TryResolve(string relative, out string file, out string error)
        {
            file = null;
            error = null;

            if (String.IsNullOrWhiteSpace(relative))
            {
                error = "No spawn file named.";
                return false;
            }

            if (relative.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                error = String.Format("'{0}' is not a spawn file name.", relative);
                return false;
            }

            string root = Path.GetFullPath(Path.Combine(Core.BaseDirectory, SpawnRoot))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            string full;

            try
            {
                full = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (Exception ex)
            {
                error = String.Format("'{0}' is not a spawn file name: {1}", relative, ex.Message);
                return false;
            }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = String.Format("'{0}' is outside {1}.", relative, SpawnRoot);
                return false;
            }

            if (!full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                error = String.Format("'{0}' is not an .xml file.", relative);
                return false;
            }

            if (!File.Exists(full))
            {
                error = String.Format("'{0}' does not exist.", relative);
                return false;
            }

            file = full;
            return true;
        }

        /// <summary>
        /// Counts the spawn points in a file or directory, the way the loader will read them.
        ///
        /// A DataSet read is the same thing XmlLoadFromStream does, so this fails on exactly the
        /// files that would fail there - which is the point: it turns a silent zero-import into a
        /// refusal that happens BEFORE anything is deleted.
        /// </summary>
        private static bool TryCountSpawnPoints(string path, out int count, out string error)
        {
            count = 0;
            error = null;

            var files = new List<string>();

            if (Directory.Exists(path))
            {
                files.AddRange(Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories));
            }
            else
            {
                files.Add(path);
            }

            foreach (string file in files)
            {
                try
                {
                    var set = new System.Data.DataSet("Spawns");

                    using (var stream = File.OpenRead(file))
                    {
                        set.ReadXml(stream);
                    }

                    if (set.Tables["Points"] != null)
                    {
                        count += set.Tables["Points"].Rows.Count;
                    }
                }
                catch (Exception ex)
                {
                    error = String.Format(
                        "{0} cannot be read, so nothing was changed: {1}",
                        Path.GetFileName(file),
                        ex.Message);

                    Log.Error(error);
                    return false;
                }
            }

            if (count == 0)
            {
                error = String.Format(
                    "{0} has no spawn points, so nothing was changed. An empty import would have "
                    + "deleted the existing spawners and replaced them with nothing.",
                    Directory.Exists(path) ? SpawnRoot : Path.GetFileName(path));

                return false;
            }

            return true;
        }

        /// <summary>
        /// Deletes every GG_-prefixed spawner before the import. This is what makes the command a
        /// reimport rather than an upsert: [XmlLoad replaces by UniqueId, so a spawn point deleted
        /// from the XML would otherwise be left orphaned in the world forever.
        ///
        /// XmlSpawner.OnDelete calls RemoveSpawnObjects(), so the spawned NPCs go with it. That is
        /// safe for players mid-quest - QuestHelper.InProgress matches on quest.QuesterType
        /// against the NPC's Type, not against the instance.
        /// </summary>
        private static int DeleteExisting()
        {
            var doomed = new List<XmlSpawner>();

            // A World.Items walk, which custom code otherwise avoids (CLAUDE.md section 15).
            // Justified: there is no index of spawners by name, and this runs only when an
            // administrator types the command.
            foreach (Item item in World.Items.Values)
            {
                var spawner = item as XmlSpawner;

                if (spawner == null || spawner.Deleted || spawner.Name == null)
                {
                    continue;
                }

                // Ordinal StartsWith, matching XmlSpawner's own SpawnerPrefixFilter test.
                if (spawner.Name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    doomed.Add(spawner);
                }
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                doomed[i].Delete();
            }

            return doomed.Count;
        }
    }
}
