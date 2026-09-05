using System;

namespace Server.Custom
{
    /// <summary>
    /// One character's jail history and current sentence.
    ///
    /// Not an entity - these live in <see cref="JailSystem"/>'s dictionary and are written as
    /// part of its persistence blob.
    /// </summary>
    public class JailRecord
    {
        /// <summary>
        /// Lifetime offence count. Incremented BEFORE the sentence is calculated, so the first
        /// jail is offence #1. Never reset - the escalation is meant to be permanent.
        /// </summary>
        public int JailCount { get; set; }

        /// <summary>
        /// Absolute UTC. The single definition of "jailed": a strict greater-than against the
        /// clock, so at exactly this instant the player is already free.
        ///
        /// Absolute rather than remaining-time on purpose: real time passes while the server is
        /// down, so a sentence can expire during an outage. That is the intended behaviour, and
        /// it is why JailSystem has a login safety net.
        /// </summary>
        public DateTime JailEndTimeUtc { get; set; }

        /// <summary>Display only.</summary>
        public DateTime LastJailedUtc { get; set; }

        public string LastReason { get; set; }

        /// <summary>The staff member responsible, or null for an automatic jail.</summary>
        public Mobile JailedBy { get; set; }

        /// <summary>
        /// The facet the player was standing on when jailed, captured before the teleport so
        /// release can return them where they were taken from.
        /// </summary>
        public Map OriginMap { get; set; }

        public JailRecord()
        {
            LastReason = String.Empty;
        }

        public bool IsCurrentlyJailed
        {
            get { return JailEndTimeUtc > DateTime.UtcNow; }
        }

        /// <summary>Remaining sentence, floored at zero.</summary>
        public TimeSpan Remaining
        {
            get
            {
                TimeSpan remaining = JailEndTimeUtc - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

            writer.Write(JailCount);
            writer.Write(JailEndTimeUtc);
            writer.Write(LastJailedUtc);
            writer.Write(LastReason);
            writer.Write(JailedBy);
            writer.Write(OriginMap);
        }

        public void Deserialize(GenericReader reader)
        {
            int version = reader.ReadInt();

            switch (version)
            {
                case 0:
                    {
                        JailCount = reader.ReadInt();
                        JailEndTimeUtc = reader.ReadDateTime();
                        LastJailedUtc = reader.ReadDateTime();

                        // ReadString legitimately returns null for a null that was written.
                        LastReason = reader.ReadString() ?? String.Empty;

                        // Null if the staff member's character has since been deleted.
                        JailedBy = reader.ReadMobile();

                        OriginMap = reader.ReadMap();
                    }
                    break;
            }
        }
    }
}
