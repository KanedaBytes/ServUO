using System;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>How fast a bot should be moving right now.</summary>
    public enum BotPace
    {
        /// <summary>Lingering, sitting, working, standing about a bank.</summary>
        Walk,

        /// <summary>Going somewhere: a Traveler leg, or a commute to a work site.</summary>
        Run
    }

    /// <summary>
    /// How a bot moves: at a player's pace, and on a horse when it owns one.
    ///
    /// Bots walked everywhere at exactly half a second a step, which is the one speed nothing in
    /// UO actually moves at - a player walks at 0.4 and runs at 0.2, and on a mount 0.2 and 0.1.
    /// The half-second came from `SpeedInfo.MaxDelay`: `GetSpeedsNew` derives a creature's speed
    /// from its Dex, `PassiveSpeed` is twice `ActiveSpeed`, and `TransformMoveDelay` then clamps
    /// the result to 0.5. Every bot in the world was sitting on that clamp, which is why they all
    /// moved at the same speed regardless of Dex and why none of them ever played a run animation.
    ///
    /// The delay is not decoration. `BaseAI.DoMoveImpl` decides whether a step is a RUN from it:
    ///
    ///     running = CanRun &amp;&amp; (mounted ? delay &lt; Mobile.WalkMount : delay &lt; Mobile.WalkFoot)
    ///
    /// so 200ms on foot is a run and 400ms is a walk, and the numbers here are exactly the four
    /// the engine ships (`Mobile.WalkFoot` and friends) rather than four of our own that happen
    /// to look similar. Set 400 on foot and the bot walks; set 200 and it runs, animation and all.
    ///
    /// GAME THREAD ONLY.
    /// </summary>
    public static class BotMovement
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// A trip shorter than this is walked. uo-offline `TravelerBehavior.cs:1803`, used at
        /// `:1927` as `running = forceRunning || dist > RunThresholdTiles`.
        ///
        /// Upstream measures it per LEG, between two of its destination waypoints. We cannot: a
        /// leg here is one graph hop and `Custom.NavHopMaxTiles` caps those at twelve, so the
        /// same comparison would never once be true and no bot would ever run. Applied instead to
        /// the whole route, which is what one of their legs actually corresponds to and preserves
        /// what the number is for - a short errand is walked, a journey is run.
        /// </summary>
        public const int RunThresholdTiles = 25;

        /// <summary>
        /// The step delay, in seconds, for a pace and a mount state.
        ///
        /// Read off the engine's own constants so a shard that retunes movement retunes bots with
        /// it. They are 400 / 200 / 200 / 100 by default, walk and run, foot and mount - and they
        /// are the same four numbers upstream uses, because upstream reads them from the same
        /// place: `using MoveDelays = Server.Movement.Movement` (`TravelerBehavior.cs:23`), whose
        /// defaults are set in ModernUO's `Server/Mobiles/Movement.cs:33-36`. ServUO keeps them
        /// in `Server/Mobile.cs:3063-3071`. Nothing here is a number of our own.
        /// </summary>
        public static double DelayFor(BotPace pace, bool mounted)
        {
            int ms = mounted
                ? (pace == BotPace.Run ? Mobile.RunMount : Mobile.WalkMount)
                : (pace == BotPace.Run ? Mobile.RunFoot : Mobile.WalkFoot);

            return ms / 1000.0;
        }

        /// <summary>
        /// Move this bot at a pace, mounted or not.
        ///
        /// Both speeds are set, not just the active one. `BaseAI` picks between ActiveSpeed and
        /// PassiveSpeed by its own ActionType, and a Traveler crossing the map is "wandering" as
        /// far as the AI is concerned - so setting only ActiveSpeed left the bot on PassiveSpeed,
        /// which is twice as slow and clamped to the half-second again.
        ///
        /// Writing CurrentSpeed restarts the AI's think timer (`OnCurrentSpeedChanged`), so it is
        /// only written when it actually changes. Called every tick without that guard, the timer
        /// would be stopped and restarted ten times a second and the bot would never think.
        /// </summary>
        public static void SetPace(PlayerBot bot, BotPace pace)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            double delay = DelayFor(pace, bot.Mounted);

            bot.ActiveSpeed = delay;
            bot.PassiveSpeed = delay;

            if (Math.Abs(bot.CurrentSpeed - delay) > 0.0001)
            {
                bot.CurrentSpeed = delay;
            }
        }

        /// <summary>
        /// The mount pool, carried over verbatim from uo-offline's `BotMountHelper._mountTypes`
        /// (`CustomBots/BotMountHelper.cs:28-43`), weighting included: Horse appears four times
        /// because it is meant to be the common one.
        ///
        /// Pack animals are deliberately absent, and for their reason as well as ours: you cannot
        /// ride one, and `BotPackAnimals` already gives them to gatherers as cargo followers.
        /// </summary>
        private static readonly Type[] MountTypes =
        {
            typeof(Horse),
            typeof(Horse),
            typeof(Horse),
            typeof(Horse),
            typeof(ForestOstard),
            typeof(DesertOstard),
            typeof(FrenziedOstard),
            typeof(Llama)
        };

        /// <summary>
        /// The share of eligible bots that own a mount. uo-offline `PlayerBot.cs:751`.
        /// </summary>
        public const double MountChance = 0.70;

        /// <summary>
        /// Whether this class rides at all. uo-offline `PlayerBot.cs:749-750`.
        ///
        /// Class only - there is no tier component upstream, and inventing one here would make
        /// our bots and theirs disagree about who owns a horse for no reason anybody could point
        /// at. A Fisherman works the dock edge on foot because casting a line from horseback
        /// looks absurd; a gatherer's animal is the pack beast, which nobody can ride.
        /// </summary>
        public static bool CanOwnMount(BotClass cls)
        {
            return cls != BotClass.Fisherman && !BotClassHelper.IsGatherer(cls);
        }

        /// <summary>
        /// The spawn-time roll: most bots own a horse. uo-offline `PlayerBot.cs:743-753`.
        /// </summary>
        public static void RollMount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !CanOwnMount(bot.Class))
            {
                return;
            }

            if (Utility.RandomDouble() >= MountChance)
            {
                return;
            }

            TryMount(bot);
        }

        /// <summary>
        /// Puts a bot on a mount rolled from the pool, if it is not already riding.
        ///
        /// Five attempts, as upstream (`BotMountHelper.cs:66-100`), because a type that will not
        /// instantiate should cost one reroll rather than the whole mount.
        /// </summary>
        public static bool TryMount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Mounted || bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var mount = Activator.CreateInstance(
                        MountTypes[Utility.Random(MountTypes.Length)]) as BaseMount;

                    if (mount == null)
                    {
                        continue;
                    }

                    // 75% keep their default coat, 25% take a muted variation - upstream's
                    // RollHorseHue (`BotMountHelper.cs:50-54`), which exists to avoid electric
                    // greens that read as magical rather than as a horse.
                    if (mount is Horse && Utility.RandomDouble() >= 0.75)
                    {
                        mount.Hue = Utility.RandomNeutralHue();
                    }

                    // Rider= requires the mount to be in the world on the same map first.
                    mount.MoveToWorld(bot.Location, bot.Map);
                    mount.Rider = bot;

                    // The pace changes with the mount: the same "run" is 200ms on foot and 100ms
                    // in the saddle, and forgetting this leaves a mounted bot running at a walk.
                    SetPace(bot, CurrentPace(bot));

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "{0} could not be mounted.", bot.Name);
                }
            }

            return false;
        }

        /// <summary>
        /// Walk a short trip, run a long one - upstream's rule, against the whole route rather
        /// than one hop. See RunThresholdTiles for why the unit had to change.
        /// </summary>
        public static BotPace PaceForRoute(NavRoute route)
        {
            return RouteTiles(route) > RunThresholdTiles ? BotPace.Run : BotPace.Walk;
        }

        /// <summary>
        /// The length of a route in tiles, summed hop by hop.
        ///
        /// Not the step count, which counts a twelve-tile hop and a one-tile hop the same, and not
        /// NavRoute.Cost, which is weighted by road tags for the SEARCH and so is not a distance
        /// at all - a road hop costs 0.9 of what it measures.
        /// </summary>
        public static int RouteTiles(NavRoute route)
        {
            if (route == null || route.Steps == null || route.Steps.Count < 2)
            {
                return 0;
            }

            int tiles = 0;

            for (int i = 1; i < route.Steps.Count; i++)
            {
                Point3D a = route.Steps[i - 1].Point;
                Point3D b = route.Steps[i].Point;

                tiles += Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            }

            return tiles;
        }

        /// <summary>The pace a bot is already moving at, so remounting does not change it.</summary>
        private static BotPace CurrentPace(PlayerBot bot)
        {
            return bot.CurrentSpeed <= DelayFor(BotPace.Run, false) + 0.0001
                ? BotPace.Run
                : BotPace.Walk;
        }

        /// <summary>
        /// Settle a bot where it has arrived: off the horse, down to a walk.
        ///
        /// OUR ADDITION, not upstream's. There a mount is only given up at the stables, on death,
        /// or by a gatherer about to swing a pick; a BankSitter stands at the counter in the
        /// saddle. On this shard that reads wrong for the same reason the arrival points do:
        /// a mounted bot is a larger obstacle in a crowd that already jams, and a bank full of
        /// horses is not what a bank looks like. Stabling is a delete rather than a stable record,
        /// which is the same thing upstream's DismountAndDelete does.
        /// </summary>
        public static void Settle(PlayerBot bot)
        {
            Dismount(bot);
            SetPace(bot, BotPace.Walk);
        }

        /// <summary>
        /// Takes a bot off its mount and removes the animal.
        ///
        /// Deleted rather than released, which is what "stabled" means here: there is no stable
        /// record to keep and a loose horse outside every bank is worse than no horse at all.
        /// </summary>
        public static void Dismount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !bot.Mounted)
            {
                return;
            }

            try
            {
                IMount mount = bot.Mount;

                if (mount != null)
                {
                    mount.Rider = null;

                    var beast = mount as BaseMount;

                    if (beast != null && !beast.Deleted)
                    {
                        beast.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{0} could not be dismounted.", bot.Name);
            }

            SetPace(bot, BotPace.Walk);
        }
    }
}
