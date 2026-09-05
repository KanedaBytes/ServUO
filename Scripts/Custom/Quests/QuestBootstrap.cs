using System;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Wiring for the custom quest layer.
    ///
    /// There is deliberately no CustomPersistence store here. Quest state is entirely ServUO's:
    /// active quests live in MondainQuestData (Saves/Quests/MLQuests.bin) and the once-per-
    /// character record lives in PlayerMobile.DoneQuests, inside the PlayerMobile save. Adding a
    /// third store would be a second source of truth for the same facts.
    ///
    /// Kept separate from AutoCollectSystem for the same reason JailBootstrap is separate from
    /// JailSystem: a static Configure()/Initialize() on a system class gives that type two
    /// unrelated meanings for the name.
    /// </summary>
    public static class QuestBootstrap
    {
        /// <summary>
        /// Nothing to build before World.Load - the quest engine registers its own WorldSave and
        /// WorldLoad handlers. Kept so the next person looks for the wiring where it usually is.
        /// </summary>
        public static void Configure()
        {
        }

        public static void Initialize()
        {
            AutoCollectSystem.Start();

            ResetQuestCommands.Initialize();
            GGSpawnCommands.Initialize();

            HealthCheck.Register("Quests.AutoCollect", AutoCollectSystem.BuildHealthResult);
            HealthCheck.Register("Quests.Marta", BuildMartaHealthResult);
        }

        /// <summary>
        /// Reports whether Old Marta's spawner was imported and whether she is actually standing
        /// there.
        /// </summary>
        private static HealthResult BuildMartaHealthResult()
        {
            XmlSpawner spawner = null;

            // A World.Items walk, which custom code otherwise avoids (CLAUDE.md section 15). It is
            // justified here because health checks run only on demand - from [CoreSmoke or the
            // admin API - never on a timer, and there is no index of spawners by name.
            foreach (Item item in World.Items.Values)
            {
                var candidate = item as XmlSpawner;

                if (candidate != null && candidate.Name == GGSpawnCommands.MartaSpawnerName)
                {
                    spawner = candidate;
                    break;
                }
            }

            if (spawner == null)
            {
                return HealthResult.Fail(
                    String.Format(
                        "no '{0}' spawner in the world - run [GG_Reimport to import Spawns/Custom",
                        GGSpawnCommands.MartaSpawnerName));
            }

            if (spawner.TotalSpawnedObjects <= 0)
            {
                // Expected for a few seconds straight after [GG_Reimport - a freshly created
                // spawner has not had its first respawn tick yet.
                return HealthResult.Warn(
                    String.Format(
                        "'{0}' exists at {1} on {2} but has nothing spawned yet",
                        spawner.Name,
                        spawner.Location,
                        spawner.Map));
            }

            return HealthResult.Ok(
                String.Format("'{0}' active at {1} on {2}", spawner.Name, spawner.Location, spawner.Map));
        }
    }
}
