using System;

using Server.Items;

namespace Server.Custom
{
    /// <summary>
    /// A lantern-carrying watchman who patrols, or stands a post, after dark.
    ///
    /// Visual only: the watch has no guard behaviour. Britain's real guards are conjured on
    /// demand by GuardedRegion.MakeGuard (Scripts/Regions/GuardedRegion.cs:124) and delete
    /// themselves when idle, so this is an addition to the town rather than a replacement.
    /// </summary>
    public class DailyLifeWatchman : DailyLifeTownsfolk
    {
        private static readonly TimeSpan MinChatterDelay = TimeSpan.FromSeconds(40.0);
        private static readonly TimeSpan MaxChatterDelay = TimeSpan.FromSeconds(90.0);

        private long _nextChatter;

        public DailyLifeWatchman()
        {
            Title = "the night watch";

            // Movable false and on the two-handed layer: this is the light source, not a weapon.
            AddItem(new Lantern { Movable = false, Layer = Layer.TwoHanded });
            AddItem(new LeatherChest());
            AddItem(new LeatherLegs());

            ScheduleNextChatter();
        }

        public DailyLifeWatchman(Serial serial)
            : base(serial)
        {
        }

        /// <summary>
        /// Gated on its own deadline rather than the think cadence: OnThink fires at least once
        /// per cadence but can fire more often, and extra calls must never buy an extra line.
        /// Compared in subtraction form for wraparound safety.
        /// </summary>
        public override void OnThink()
        {
            base.OnThink();

            if (Core.TickCount - _nextChatter < 0)
            {
                return;
            }

            ScheduleNextChatter();
            Chatter();
        }

        private void ScheduleNextChatter()
        {
            _nextChatter = Core.TickCount + Utility.RandomMinMax(
                (int)MinChatterDelay.TotalMilliseconds,
                (int)MaxChatterDelay.TotalMilliseconds);
        }

        private void Chatter()
        {
            string line = DailyLifeSystem.RandomWatchLine();

            if (line != null)
            {
                Say(line);
            }
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();

            // The base class already schedules the self-delete; nothing else to restore.
        }
    }
}
