using System;

namespace Server.Custom
{
    /// <summary>
    /// A temporary in-world marker showing where a piece of navigation data actually is.
    ///
    /// EPHEMERAL BY DESIGN. It deletes itself on world load and expires on a timer, so a
    /// forgotten [NavDebug can never leave the world littered with items that outlive the data
    /// they were describing. The JSON stays the single source of truth.
    /// </summary>
    public class NavDebugMarker : Item
    {
        /// <summary>Waypoint. Green.</summary>
        public const int WaypointHue = 0x40;

        /// <summary>Arrival point. Blue.</summary>
        public const int ArrivalHue = 0x9C2;

        /// <summary>Exclusive arrival point. Red.</summary>
        public const int ExclusiveHue = 0x26;

        /// <summary>Destination centre. Yellow.</summary>
        public const int DestinationHue = 0x35;

        /// <summary>Zone corner. Purple.</summary>
        public const int ZoneHue = 0x486;

        public NavDebugMarker(int hue, string label)
            : base(0x1F14)
        {
            Hue = hue;
            Name = label;
            Movable = false;
            Visible = true;
        }

        public NavDebugMarker(Serial serial)
            : base(serial)
        {
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

            // ServUO has no [AfterDeserialization]; this is the equivalent, and it is what
            // makes the marker ephemeral across a restart.
            Timer.DelayCall(Delete);
        }
    }
}
