using System;
using System.Collections.Generic;

using Server.Commands;

namespace Server.Custom
{
    /// <summary>Staff commands for managing restricted zones. All write the JSON and reload.</summary>
    public static class RestrictedZoneCommands
    {
        public static void Initialize()
        {
            CommandSystem.Register("RestrictZone", AccessLevel.GameMaster, RestrictZone_OnCommand);
            CommandSystem.Register("UnrestrictZone", AccessLevel.GameMaster, UnrestrictZone_OnCommand);
            CommandSystem.Register("ListRestrictedZones", AccessLevel.GameMaster, ListRestrictedZones_OnCommand);
            CommandSystem.Register("RestrictedZonesReload", AccessLevel.GameMaster, RestrictedZonesReload_OnCommand);
        }

        [Usage("RestrictZone <name>")]
        [Description("Marks a rectangular area as a restricted zone by targeting two corners.")]
        private static void RestrictZone_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length != 1)
            {
                from.SendMessage("Usage: [RestrictZone <name>");
                return;
            }

            string name = e.GetString(0);

            if (RestrictedZoneSystem.Find(name) != null)
            {
                from.SendMessage(String.Format("A restricted zone named '{0}' already exists.", name));
                return;
            }

            // ServUO's picker takes a delegate plus a state object - there is no lambda overload,
            // so the zone name rides along as the state. It prompts for both corners, rejects
            // corners on different maps, and normalises them with Utility.FixPoints before the
            // callback, which is what makes the inclusive arithmetic below safe.
            BoundingBoxPicker.Begin(from, new BoundingBoxCallback(OnBoundsPicked), name);
        }

        private static void OnBoundsPicked(Mobile from, Map map, Point3D start, Point3D end, object state)
        {
            var name = state as string;

            if (name == null)
            {
                return;
            }

            if (map == null || map == Map.Internal)
            {
                from.SendMessage("That is not a valid map for a restricted zone.");
                return;
            }

            // Re-check: the staff member may have taken a while over the two targets.
            if (RestrictedZoneSystem.Find(name) != null)
            {
                from.SendMessage(String.Format("A restricted zone named '{0}' already exists.", name));
                return;
            }

            // Inclusive of both corners.
            var bounds = new Rectangle2D(start.X, start.Y, end.X - start.X + 1, end.Y - start.Y + 1);
            var record = new RestrictedZoneRecord(name, map, bounds);

            if (!RestrictedZoneSystem.Add(record))
            {
                from.SendMessage(String.Format("A restricted zone named '{0}' already exists.", name));
                return;
            }

            from.SendMessage(
                String.Format(
                    "Restricted zone '{0}' created on {1} covering {2}x{3} tiles at {4}, {5}.",
                    name,
                    map,
                    bounds.Width,
                    bounds.Height,
                    bounds.X,
                    bounds.Y));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} creating restricted zone '{2}' on {3} at {4}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    name,
                    map,
                    bounds));
        }

        [Usage("UnrestrictZone <name>")]
        [Description("Removes a restricted zone by name.")]
        private static void UnrestrictZone_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length != 1)
            {
                from.SendMessage("Usage: [UnrestrictZone <name>");
                return;
            }

            string name = e.GetString(0);
            RestrictedZoneRecord record = RestrictedZoneSystem.Find(name);

            if (record == null)
            {
                from.SendMessage(String.Format("No restricted zone named '{0}' exists.", name));
                return;
            }

            RestrictedZoneSystem.Remove(record);

            from.SendMessage(String.Format("Restricted zone '{0}' removed.", name));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} removing restricted zone '{2}'",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    name));
        }

        [Usage("ListRestrictedZones")]
        [Description("Lists every restricted zone.")]
        private static void ListRestrictedZones_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            IList<RestrictedZoneRecord> zones = RestrictedZoneSystem.Zones;

            if (zones.Count == 0)
            {
                from.SendMessage("There are no restricted zones.");
                return;
            }

            from.SendMessage(String.Format("{0} restricted zone(s):", zones.Count));

            for (int i = 0; i < zones.Count; i++)
            {
                RestrictedZoneRecord record = zones[i];
                Rectangle2D bounds = record.Bounds;

                from.SendMessage(
                    String.Format(
                        "{0}: {1}, {2}x{3} at {4}, {5}",
                        record.Name,
                        record.MapName,
                        bounds.Width,
                        bounds.Height,
                        bounds.X,
                        bounds.Y));
            }
        }

        [Usage("RestrictedZonesReload")]
        [Aliases("ReloadRestrictedZones")]
        [Description("Re-reads restricted-zones.json and rebuilds every zone region.")]
        private static void RestrictedZonesReload_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            string error;

            if (!RestrictedZoneSystem.TryReload(out error))
            {
                // The previous zones are still live - nothing was torn down.
                from.SendMessage(0x35, String.Format("Restricted zones NOT reloaded: {0}", error));
                from.SendMessage(0x35, "The shard is still running on the previously loaded zones.");
                return;
            }

            from.SendMessage(
                String.Format("Restricted zones reloaded: {0} zone(s).", RestrictedZoneSystem.Zones.Count));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} reloading the restricted zone file",
                    from.AccessLevel,
                    CommandLogging.Format(from)));
        }
    }
}
