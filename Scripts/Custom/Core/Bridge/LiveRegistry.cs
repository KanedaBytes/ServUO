using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// The custom mobiles the editor cares about, tracked as they come and go.
    ///
    /// This exists so the live snapshot never walks World.Mobiles. There are around twenty
    /// thousand mobiles in this world and about twenty of them are ours; scanning the lot every
    /// two seconds to find them would be the most expensive thing the shard does, and the house
    /// rule (CLAUDE.md section 15) is to justify any World.Mobiles walk at the call site rather
    /// than reach for one by habit.
    ///
    /// Actors register from BOTH OnAfterSpawn and the tail of Deserialize. The daily-life actors
    /// are ephemeral and only ever spawn; the GG vendors persist across a restart, where
    /// OnAfterSpawn does not fire and only the load path would catch them.
    /// </summary>
    public static class LiveRegistry
    {
        private static readonly List<Mobile> _tracked = new List<Mobile>();

        public static int Count
        {
            get { return _tracked.Count; }
        }

        public static void Register(Mobile mobile)
        {
            if (mobile == null || mobile.Deleted || _tracked.Contains(mobile))
            {
                return;
            }

            _tracked.Add(mobile);
        }

        public static void Unregister(Mobile mobile)
        {
            if (mobile != null)
            {
                _tracked.Remove(mobile);
            }
        }

        /// <summary>
        /// A snapshot of the live list, pruned of anything deleted.
        ///
        /// Pruning here rather than trusting Unregister: a mobile deleted by a path that does not
        /// route through OnDelete would otherwise linger in the list for ever, and the editor
        /// would draw a ghost.
        /// </summary>
        public static List<Mobile> Snapshot()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (_tracked[i] == null || _tracked[i].Deleted)
                {
                    _tracked.RemoveAt(i);
                }
            }

            return new List<Mobile>(_tracked);
        }
    }
}
