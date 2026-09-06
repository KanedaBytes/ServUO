// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotHarvest.cs — finding something to swing at, and swinging at it for real.
//
// THIS FILE IS NOT A TRANSLATION. Upstream's GathererBehavior never touched a
// harvest system: AddYield simply created `new IronOre(2..6)` on a 35-second
// timer, and the site could be any waypoint on the road because the terrain was
// never consulted. Only their Fisherman drove a real engine.
//
// ServUO's harvest system is fully drivable headlessly, so we drive it:
//
//   HarvestSystem.StartHarvesting(from, tool, toHarvest)   HarvestSystem.cs:477
//
// is the entry point that takes an ALREADY-RESOLVED target. The client path
// (BeginHarvesting, :91) sets `from.Target` and needs a NetState; this one does
// not touch either. HarvestTarget.OnTarget is nothing but a dispatcher into it.
//
// The one piece ServUO keeps to itself is HarvestSystem.FindValidTile (:600) —
// `private static`, and the 3x3 scan that turns "where I am standing" into the
// StaticTarget or LandTarget the system wants. FindTarget below is that method's
// shape, widened to a configurable radius so a bot can find the rock face it is
// standing beside rather than only the tile under its feet.
//
// THREE CONSEQUENCES OF USING THE REAL SYSTEM, all of which matter:
//
//   1. A bot only ever gets PLAIN ORE. Mining.GetResourceType (Mining.cs:187)
//      falls through to resource.Types[0] for anything that is not a
//      PlayerMobile, and sand mining is PlayerMobile-gated outright
//      (Mining.cs:283). That happens to match upstream's IronOre yield exactly,
//      so nothing is lost — but a Miner will never bring home granite or gems.
//   2. A FULL PACK SILENTLY DESTROYS THE ORE. Neither Mining nor Lumberjacking
//      sets PlaceAtFeetIfFull, so HarvestSystem.cs:207-210 sends the pack-full
//      message and calls item.Delete(). The caller must check capacity before
//      every swing; GathererBehavior does.
//   3. Resource banks deplete and respawn for real (mining 10-34 per 8x8 bank
//      on a 10-20 minute timer; lumber 20-45 per 4x3 on 20-30). A bot that
//      stands on one tile mines it out, which is why the behaviour shuffles
//      along the face.

using System;

using Server.Engines.Harvest;
using Server.Targeting;

namespace Server.Custom
{
    public static class BotHarvest
    {
        /// <summary>How far around itself a bot will look for something to work. Cheap: (2r+1)^2 tiles.</summary>
        public const int SearchRadius = 2;

        /// <summary>The harvest system a class works with, or null if it is not a gatherer.</summary>
        public static HarvestSystem SystemFor(BotClass cls)
        {
            if (cls == BotClass.Miner)
            {
                return Mining.System;
            }

            if (cls == BotClass.Lumberjack)
            {
                return Lumberjacking.System;
            }

            return null;
        }

        /// <summary>The definition to validate tiles against, paired with SystemFor.</summary>
        public static HarvestDefinition DefinitionFor(BotClass cls)
        {
            if (cls == BotClass.Miner)
            {
                // OreAndStone, never Sand: Mining.CheckHarvest requires a PlayerMobile with the
                // SandMining flag for that one, so a bot asking for it would be refused every
                // swing and never learn why.
                return Mining.System.OreAndStone;
            }

            if (cls == BotClass.Lumberjack)
            {
                return Lumberjacking.System.Definition;
            }

            return null;
        }

        /// <summary>What this class hauls home. The delivery hook matches it against a trade's RawGood.</summary>
        public static Type YieldFor(BotClass cls)
        {
            if (cls == BotClass.Miner)
            {
                return typeof(Server.Items.IronOre);
            }

            if (cls == BotClass.Lumberjack)
            {
                return typeof(Server.Items.Log);
            }

            return null;
        }

        /// <summary>
        /// The bot's harvest tool: equipped or in the pack, whichever it has.
        ///
        /// Both systems accept either. Lumberjacking.CheckHarvest (:204) tests
        /// `tool.Parent != from &amp;&amp; !tool.IsChildOf(from.Backpack)`, and the stricter
        /// "you must equip that axe" message lives only in HarvestTarget, which
        /// StartHarvesting bypasses. EquipmentTable already equips a Pickaxe on a Miner and a
        /// Hatchet — or a veteran's ExecutionersAxe, which is also a BaseAxe — on a Lumberjack.
        ///
        /// The two classes look for different things on purpose. ServUO does not type-check the
        /// tool at all once StartHarvesting is called directly — CheckTool only asks whether it
        /// has uses left — so a Miner handed a hatchet would happily "mine" with it. Asking for
        /// the right family keeps that from being possible.
        /// </summary>
        public static Item FindTool(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return null;
            }

            Type toolType = bot.Class == BotClass.Miner
                ? typeof(Server.Items.Pickaxe)
                : typeof(Server.Items.BaseAxe);

            Item held = bot.FindItemOnLayer(Layer.OneHanded) ?? bot.FindItemOnLayer(Layer.TwoHanded);

            if (held != null && toolType.IsInstanceOfType(held))
            {
                return held;
            }

            return bot.Backpack == null ? null : bot.Backpack.FindItemByType(toolType);
        }

        /// <summary>
        /// Find something within reach that this definition accepts, or null.
        ///
        /// The shape of HarvestSystem.FindValidTile, which is private static, widened from its
        /// fixed 3x3 to SearchRadius. Statics are tested first and land second, in that order,
        /// because that is the order the engine itself uses and a tile can be both — a tree
        /// static standing on a mineable rock face would otherwise resolve differently here than
        /// in GetHarvestDetails, and the harvest would be refused after the behaviour had already
        /// committed to it.
        ///
        /// The 0x4000 bit is not decoration: GetHarvestDetails (HarvestSystem.cs:515) encodes a
        /// static's id as `(ItemID &amp; 0x3FFF) | 0x4000` before validating, which is why
        /// Lumberjacking's tile table reads 0x4CCA for what is really item 0x0CCA.
        /// </summary>
        public static object FindTarget(PlayerBot bot, HarvestDefinition definition)
        {
            if (bot == null || bot.Deleted || definition == null)
            {
                return null;
            }

            Map map = bot.Map;

            if (map == null || map == Map.Internal)
            {
                return null;
            }

            for (int x = bot.X - SearchRadius; x <= bot.X + SearchRadius; x++)
            {
                for (int y = bot.Y - SearchRadius; y <= bot.Y + SearchRadius; y++)
                {
                    StaticTile[] tiles = map.Tiles.GetStaticTiles(x, y, false);

                    for (int i = 0; i < tiles.Length; i++)
                    {
                        int id = (tiles[i].ID & 0x3FFF) | 0x4000;

                        if (definition.Validate(id))
                        {
                            return new StaticTarget(new Point3D(x, y, tiles[i].Z), tiles[i].ID);
                        }
                    }

                    LandTile land = map.Tiles.GetLandTile(x, y);

                    if (definition.Validate(land.ID))
                    {
                        return new LandTarget(new Point3D(x, y, land.Z), map);
                    }
                }
            }

            return null;
        }

        /// <summary>Is there anything here worth working at all? Used to validate a site at load.</summary>
        public static bool HasAnything(Map map, Point3D at, HarvestDefinition definition, int radius)
        {
            if (map == null || map == Map.Internal || definition == null)
            {
                return false;
            }

            for (int x = at.X - radius; x <= at.X + radius; x++)
            {
                for (int y = at.Y - radius; y <= at.Y + radius; y++)
                {
                    StaticTile[] tiles = map.Tiles.GetStaticTiles(x, y, false);

                    for (int i = 0; i < tiles.Length; i++)
                    {
                        if (definition.Validate((tiles[i].ID & 0x3FFF) | 0x4000))
                        {
                            return true;
                        }
                    }

                    if (definition.Validate(map.Tiles.GetLandTile(x, y).ID))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Swing once at whatever is in reach. True when a harvest actually started.
        ///
        /// Everything after this is the engine's: HarvestTimer runs the 1.6s effect, plays the
        /// animation and sound through Mobile.Animate / PlaySound so real players see a bot
        /// mining rather than standing still, then FinishHarvesting rolls the skill, consumes
        /// from the resource bank and puts the ore in the pack.
        ///
        /// GetLock is overridden to the system itself for both (Mining.cs:471,
        /// Lumberjacking.cs:258), so StartHarvesting takes `from.BeginAction(system)` and a
        /// second call while one is running is refused as a concurrent harvest. Harmless, but it
        /// is why the behaviour keeps its own swing clock rather than calling every tick.
        /// </summary>
        public static bool TrySwing(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !bot.Alive)
            {
                return false;
            }

            HarvestSystem system = SystemFor(bot.Class);
            HarvestDefinition definition = DefinitionFor(bot.Class);

            if (system == null || definition == null)
            {
                return false;
            }

            Item tool = FindTool(bot);

            if (tool == null || tool.Deleted)
            {
                return false;
            }

            object target = FindTarget(bot, definition);

            if (target == null)
            {
                return false;
            }

            // Mining refuses a mounted digger outright (Mining.cs:501). A bot is dismounted at
            // clock-in, so this is a guard against a mount arriving some other way rather than
            // an expected branch.
            if (bot.Mounted)
            {
                return false;
            }

            system.StartHarvesting(bot, tool, target);

            return true;
        }
    }
}
