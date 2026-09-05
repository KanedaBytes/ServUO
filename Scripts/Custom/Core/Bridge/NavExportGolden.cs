using System;
using System.IO;

using Server.Commands;

namespace Server.Custom
{
    /// <summary>
    /// Writes the golden fixtures the editor's round-trip test asserts against.
    ///
    /// The bridge has to be able to write our JSON back in exactly the layout JsonConfig writes
    /// it, which means SerializeCompact's rule now exists twice: once in C# and once in the
    /// bridge's JavaScript. Nothing stops those drifting, and the symptom would not be a test
    /// failure - it would be a quietly reformatted or corrupted data file the first time the
    /// editor saves.
    ///
    /// So the C# writer is the authority and its output is committed. The node test asserts
    /// unproject(project(golden)) is byte-for-byte the golden, and a change to either writer
    /// fails that test instead of a file.
    ///
    /// The goldens are written from the LIVE store, not copied from disk, so they are what the
    /// shard would actually emit rather than what someone hand-typed.
    /// </summary>
    public static class NavExportGolden
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        public const string GoldenDirectory = "Data/Custom/golden";

        public static void Initialize()
        {
            CommandSystem.Register("NavExportGolden", AccessLevel.Administrator, Export_OnCommand);
        }

        [Usage("NavExportGolden")]
        [Description("Writes the golden JSON fixtures the editor's round-trip test compares against.")]
        private static void Export_OnCommand(CommandEventArgs e)
        {
            string error;

            if (!Export(out error))
            {
                e.Mobile.SendMessage(0x35, "Golden export failed: " + error);
                return;
            }

            e.Mobile.SendMessage("Golden fixtures written to " + GoldenDirectory + ".");
            e.Mobile.SendMessage("Commit them - they are the contract the bridge's writer is tested against.");

            CommandLogging.WriteLine(
                e.Mobile,
                String.Format("{0} {1} exporting the golden fixtures",
                    e.Mobile.AccessLevel, CommandLogging.Format(e.Mobile)));
        }

        public static bool Export(out string error)
        {
            error = null;

            string directory = Path.Combine(Core.BaseDirectory, GoldenDirectory);

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            // Through JsonConfig.TrySave, which is the same writer every real save uses. Going
            // via anything else would make the fixture a fiction.
            if (!JsonConfig.TrySave(
                GoldenDirectory + "/navigation.golden.json", NavigationSystem.Store, out error))
            {
                return false;
            }

            if (!JsonConfig.TrySave(
                GoldenDirectory + "/britain-daily-life.golden.json", DailyLifeSystem.Config, out error))
            {
                return false;
            }

            Log.Info("Golden fixtures written to {0}.", GoldenDirectory);
            return true;
        }
    }
}
