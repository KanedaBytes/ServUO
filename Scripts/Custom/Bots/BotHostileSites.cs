// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotHostileSites.cs - the destinations a bot that cannot fight must not pick.
//
// FOUND IN GAME, not in a probe: bots walked to the Britain graveyard, the undead killed them, and
// until the mount fix their FrenziedOstards went wild and killed the next arrivals too. Nothing in
// a bot can fight yet - that is session 7g - so a destination whose area holds a hostile spawn is
// closed to every class without combat, which today is every class.
//
// WHAT UPSTREAM DOES, and why this is wider. uo-offline has a hard exclusion for trader classes
// (Behaviors/DestinationType.cs:369-378: Crafter never weighs Dungeon, DungeonEntrance or Graveyard)
// and a leave-at-once for graveyards (TravelerBehavior.cs:2228-2244). Their gatherers still weigh a
// graveyard at 0.02, as ours did. This generalises the trader rule twice: to every class without
// combat, and from a list of types to the spawn data - because on this shard five SHRINES stand
// inside spawners that are all orcs, ettins and giant spiders, and no type list would name them.
//
// THE RULE.
//   A creature type is HOSTILE when its Karma is negative and its FightMode is Closest, Strongest
//   or Weakest - it attacks unprovoked. Upstream's own filter (TravelerBehavior.cs:2921-2940:
//   Karma >= 0 skipped, FightMode None skipped), narrowed to leave out Aggressor, which only fights
//   back: a town rat must not close a forge.
//   A spawner is HOSTILE when hostile types are at least a quarter of what it can put out (summed
//   MaxCount). The quarter is Sean's call, 22 September 2026: the Yew animals box is 200x200 over
//   the whole town and 10 of its 250 are dire wolves, and closing Yew's only bank to every class
//   over a 4% wolf would strand Yew's residents. Every graveyard and shrine spawner is 67-100%.
//   A destination is CLOSED to a class without combat when its own tile or any of its arrival
//   points lies inside a hostile spawner's spawn area.
//
// THE DESTINATION STAYS ON THE GRAPH. This is a weight, like an excluded forge - nothing is removed,
// the editor still shows it, [NavAudit does not change, and a player can still walk there.
//
// WHEN 7g LANDS the exclusion comes off one class at a time: BotClassHelper.HasCombat is the switch.
//
// Computed with BotWorkSites.Validate - boot at CallPriority 920, [BotsReload, the bridge reload -
// from the spawners actually in the world rather than the XML they were once imported from.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Mobiles;

namespace Server.Custom
{
    public static class BotHostileSites
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>The hostile share at or above which a spawner closes what it covers.</summary>
        public const double HostileShare = 0.25;

        // destination id -> "Graveyards#0: Spectre, Wraith, Skeleton, Zombie"
        private static readonly Dictionary<string, string> _closed = new Dictionary<string, string>(StringComparer.Ordinal);

        // type -> hostile? Cached for the boot; a type's constructor does not change under it.
        private static readonly Dictionary<Type, bool> _hostileType = new Dictionary<Type, bool>();

        /// <summary>Spawn type names the last validation could not resolve to a creature.</summary>
        public static int Unresolved { get; private set; }

        /// <summary>Destination id to the reason it is closed, for Bots.Work.</summary>
        public static IDictionary<string, string> Closed
        {
            get { return _closed; }
        }

        /// <summary>Whether this destination is closed to this class. THE gate.</summary>
        public static bool ClosedTo(string destinationId, BotClass cls)
        {
            return destinationId != null
                && !BotClassHelper.HasCombat(cls)
                && _closed.ContainsKey(destinationId);
        }

        /// <summary>Whether a hostile spawn covers this destination at all, for any class.</summary>
        public static bool IsHostile(string destinationId)
        {
            return destinationId != null && _closed.ContainsKey(destinationId);
        }

        /// <summary>The Bots.Work finding entries, sorted.</summary>
        public static List<string> Describe()
        {
            var list = new List<string>();

            foreach (var pair in _closed)
            {
                list.Add(pair.Key + " (" + pair.Value + ")");
            }

            list.Sort(StringComparer.Ordinal);

            return list;
        }

        public static void Validate(Map map)
        {
            _closed.Clear();
            Unresolved = 0;

            if (map == null || map == Map.Internal)
            {
                return;
            }

            List<HostileArea> areas = FindHostileAreas(map);

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                var hits = new List<string>();

                for (int i = 0; i < areas.Count; i++)
                {
                    if (Covers(areas[i].Bounds, destination))
                    {
                        hits.Add(areas[i].Describe());
                    }
                }

                if (hits.Count > 0)
                {
                    _closed[destination.Id] = String.Join(" + ", hits.ToArray());
                }
            }

            List<string> closed = Describe();

            Log.Info(
                "Hostile: {0} destination(s) closed to classes without combat{1}{2}",
                closed.Count,
                closed.Count == 0 ? "." : ": " + String.Join("; ", closed.ToArray()),
                Unresolved == 0 ? "" : String.Format(" ({0} spawn type name(s) unresolved)", Unresolved));
        }

        private static bool Covers(Rectangle2D bounds, NavDestination destination)
        {
            if (bounds.Contains(destination.Location))
            {
                return true;
            }

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                if (bounds.Contains(arrival.Location))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class HostileArea
        {
            public string Name;
            public Rectangle2D Bounds;
            public List<string> Types = new List<string>();

            public string Describe()
            {
                return Name + ": " + String.Join(", ", Types.ToArray());
            }
        }

        /// <summary>
        /// Every spawner on the facet whose hostile share is at least a quarter.
        ///
        /// Justified World.Items walk (CLAUDE.md section 15): once per validation - boot and an
        /// explicit reload, never on a timer - and there is no registry of spawners to consult.
        /// SpawnerSnapshot makes the same walk every minute for the editor.
        /// </summary>
        private static List<HostileArea> FindHostileAreas(Map map)
        {
            var spawners = new List<Item>();

            foreach (Item item in World.Items.Values)
            {
                if ((item is XmlSpawner || item is Spawner) && !item.Deleted && item.Map == map)
                {
                    spawners.Add(item);
                }
            }

            var areas = new List<HostileArea>();

            foreach (Item item in spawners)
            {
                HostileArea area = null;
                int total = 0;
                int hostile = 0;

                var xml = item as XmlSpawner;

                if (xml != null)
                {
                    XmlSpawner.SpawnObject[] objects = xml.SpawnObjects;

                    for (int i = 0; objects != null && i < objects.Length; i++)
                    {
                        Count(objects[i].TypeName, objects[i].MaxCount, objects[i].SpawnedObjects,
                            ref total, ref hostile, ref area);
                    }

                    if (area != null)
                    {
                        area.Name = xml.Name;
                        area.Bounds = xml.SpawnerBounds;
                    }
                }
                else
                {
                    var stock = (Spawner)item;

                    foreach (SpawnObject so in stock.SpawnObjects)
                    {
                        Count(so.SpawnName, so.MaxCount, so.SpawnedObjects, ref total, ref hostile, ref area);
                    }

                    if (area != null)
                    {
                        int r = Math.Max(0, stock.HomeRange);

                        area.Name = stock.Name;
                        area.Bounds = new Rectangle2D(stock.X - r, stock.Y - r, (r * 2) + 1, (r * 2) + 1);
                    }
                }

                if (area != null && total > 0 && hostile >= total * HostileShare)
                {
                    areas.Add(area);
                }
            }

            return areas;
        }

        private static void Count<T>(
            string typeName,
            int maxCount,
            IList<T> spawned,
            ref int total,
            ref int hostile,
            ref HostileArea area)
        {
            if (maxCount <= 0)
            {
                return;
            }

            total += maxCount;

            Type type = Resolve(typeName);

            if (type == null)
            {
                return;
            }

            if (!IsHostileType(type, spawned))
            {
                return;
            }

            hostile += maxCount;

            if (area == null)
            {
                area = new HostileArea();
            }

            if (!area.Types.Contains(type.Name))
            {
                area.Types.Add(type.Name);
            }
        }

        /// <summary>
        /// A spawn entry's creature type. Spawn strings carry arguments and properties after the
        /// name ("Orc/Name/Grak", "Skeletrex,{RND,4,8}"); anything that is not a creature - an
        /// item, a keyword - is not a spawn that can attack, and is neither counted nor reported.
        /// </summary>
        private static Type Resolve(string typeName)
        {
            if (String.IsNullOrEmpty(typeName))
            {
                return null;
            }

            int cut = typeName.IndexOfAny(new[] { '/', ',', ' ', ':', '{', '(' });
            string name = (cut >= 0 ? typeName.Substring(0, cut) : typeName).Trim();

            if (name.Length == 0)
            {
                return null;
            }

            Type type = ScriptCompiler.FindTypeByName(name);

            if (type == null)
            {
                Unresolved++;
                return null;
            }

            return typeof(BaseCreature).IsAssignableFrom(type) ? type : null;
        }

        /// <summary>
        /// Karma and FightMode are set in each creature's constructor, so the answer is read off an
        /// instance: a live one the spawner already made when there is one, or else one built on
        /// Map.Internal, read and deleted. Once per type per boot.
        /// </summary>
        private static bool IsHostileType<T>(Type type, IList<T> spawned)
        {
            bool known;

            if (_hostileType.TryGetValue(type, out known))
            {
                return known;
            }

            BaseCreature sample = null;

            for (int i = 0; spawned != null && i < spawned.Count; i++)
            {
                var creature = spawned[i] as BaseCreature;

                if (creature != null && !creature.Deleted && creature.GetType() == type)
                {
                    sample = creature;
                    break;
                }
            }

            bool hostile = false;

            if (sample != null)
            {
                hostile = IsHostile(sample);
            }
            else
            {
                BaseCreature probe = null;

                try
                {
                    probe = Activator.CreateInstance(type) as BaseCreature;

                    hostile = probe != null && IsHostile(probe);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not classify spawn type {0}: {1}", type.Name, ex.Message);
                }
                finally
                {
                    if (probe != null && !probe.Deleted)
                    {
                        probe.Delete();
                    }
                }
            }

            _hostileType[type] = hostile;

            return hostile;
        }

        /// <summary>The rule, in one place. See the file header.</summary>
        internal static bool IsHostile(BaseCreature creature)
        {
            if (creature == null || creature.Karma >= 0)
            {
                return false;
            }

            FightMode mode = creature.FightMode;

            return mode == FightMode.Closest || mode == FightMode.Strongest || mode == FightMode.Weakest;
        }
    }
}
