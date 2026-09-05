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
