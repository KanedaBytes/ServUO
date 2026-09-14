// SaveIntegrity.cs - a world save must not change the world.
//
// WHAT THIS CATCHES, AND WHY IT WAS NOT CAUGHT
// --------------------------------------------
// World.Save is synchronous on the core thread and fires EventSink.WorldSave once the strategy has
// finished serializing (Server/World.cs:1170). Anything a handler does at that moment happens to a
// world that has already been written down. The engine knows this and says so - World.AddItem and
// World.AddMobile refuse to add during a save, print
//
//     Warning: Attempted to add 0x... "BankBox" during world save.
//
// and defer the entity onto _addQueue, which ProcessSafetyQueues drains at Server/World.cs:1187
// (Server/World.cs:1247-1290, the message at :1001-1029).
//
// BotOrphans.WriteCensus did exactly that on every bot at every save for three days - 1,796 times
// across 11 and 12 September 2026 - because Mobile.BankBox is a CREATING getter
// (Server/Mobile.cs:10498-10516) and the census read it. Nothing failed. The console said so 1,796
// times and nobody's build went red, which is the whole reason this file exists: the engine's
// warning is a print, not an assertion, and a print scrolls past.
//
// THE THREE SIGNALS, AND WHY IT TAKES THREE
// -----------------------------------------
// 1. Serial.LastItem / Serial.LastMobile (Server/Serial.cs:14-15). These are monotonic allocation
//    counters, moved only by Serial.NewItem / NewMobile, which are read only from the Item and
//    Mobile constructors. Nothing else can advance them and deletion never does, so a non-zero
//    delta means an entity was constructed - a clean detector with no confound. This is the
//    primary assertion.
//
//    The SIZE of that delta is an upper bound rather than a count, and the report says so. Nothing
//    resets these counters at World.Load, so they start at 0x40000000 each boot and NewItem walks
//    forward past every serial already in use (Server/Serial.cs:33-42) - the delta therefore counts
//    the skips as well as the allocations. Measured on the defect this file was written for: 57
//    bank boxes constructed, counter advanced 74. The detector is exact; the tally is not, and
//    signal 3 is the one that counts.
//
// 2. The length of world-save-errors.log (Server/World.cs:1020). Append-only, and it contains
//    nothing but save-safety violations, so any growth across the window is one. It catches the
//    delete side too, which the counters cannot see.
//
// 3. The console tap, scanned for the warning's own words. This is the one that NAMES the entity,
//    which is what a reader actually needs, and it is scoped exactly by sampling
//    ConsoleTap.Sequence either side of the window.
//
// WHY NOT SIMPLY "World.Items.Count IS THE SAME BEFORE AND AFTER"
// --------------------------------------------------------------
// Because it legitimately is not. strategy.ProcessDecay() runs at Server/World.cs:1189 - after the
// deferred-add queue drains and before AfterWorldSave - and deletes every item the strategy queued
// as decayed. So the count can fall across the window on a perfectly healthy shard. It can never
// RISE, though: the game thread is frozen for the whole bracket (NetState.Pause at
// Server/World.cs:1112, Resume at :1198) and the Timer thread idles through it, so the only code
// that can add anything is a save handler. The count is therefore reported both ways and asserted
// in one: an increase fails, a decrease is named as decay.
//
// THE BRACKET is BeforeWorldSave to AfterWorldSave, the same pair ProcessHealth.cs:45-46 uses, and
// for the same reason - it is the only pair whose ends are unambiguous. The WorldSave event itself
// is no good here: handler order across reflection-invoked Configure() methods is undefined, so a
// sample taken there would be measuring some of its peers and not others.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Server.Custom
{
    public static class SaveIntegrity
    {
        /// <summary>
        /// The engine's own words, trimmed to the part both messages share -
        /// "Attempted to add" and "Attempted to delete" (Server/World.cs:1001-1012).
        /// </summary>
        private const string SafetyNeedle = "Attempted to ";

        /// <summary>Written from Core.BaseDirectory by World.AppendSafetyLog (Server/World.cs:1020).</summary>
        private const string SafetyLogFile = "world-save-errors.log";

        /// <summary>
        /// A ceiling on how much of the ring we will ask for. The tap holds Custom.ConsoleTapLines
        /// (2000 by default); a window larger than that cannot be scoped, and saying so is better
        /// than silently scanning the wrong lines.
        /// </summary>
        private const int MaxWindowLines = 100000;

        private static bool _armed;

        private static int _itemsBefore;
        private static int _mobilesBefore;
        private static int _lastItemBefore;
        private static int _lastMobileBefore;
        private static long _safetyBytesBefore;
        private static long _tapSequenceBefore;

        private static HealthResult _last;

        /// <summary>How many saves this check has bracketed end to end this boot.</summary>
        public static int Saves { get; private set; }

        /// <summary>
        /// How far the entity serial counters moved across the last observed save. Zero is the
        /// contract; a non-zero value is an upper bound on how many entities were constructed.
        /// </summary>
        public static int LastCreated { get; private set; }

        /// <summary>Save-safety warnings printed during the last observed save. Zero is the contract.</summary>
        public static int LastWarnings { get; private set; }

        public static void Configure()
        {
            EventSink.BeforeWorldSave += OnBefore;
            EventSink.AfterWorldSave += OnAfter;

            HealthCheck.Register("Core.SaveIntegrity", BuildHealthResult);
        }

        private static void OnBefore(BeforeWorldSaveEventArgs e)
        {
            _itemsBefore = World.Items.Count;
            _mobilesBefore = World.Mobiles.Count;
            _lastItemBefore = Serial.LastItem.Value;
            _lastMobileBefore = Serial.LastMobile.Value;
            _safetyBytesBefore = SafetyLogBytes();
            _tapSequenceBefore = ConsoleTap.Sequence;

            _armed = true;
        }

        private static void OnAfter(AfterWorldSaveEventArgs e)
        {
            if (!_armed)
            {
                // A save that began before this handler was wired up. Nothing to compare against,
                // and inventing a baseline would report a number nobody measured.
                return;
            }

            _armed = false;
            Saves++;

            int itemsMade = Serial.LastItem.Value - _lastItemBefore;
            int mobilesMade = Serial.LastMobile.Value - _lastMobileBefore;
            int itemDelta = World.Items.Count - _itemsBefore;
            int mobileDelta = World.Mobiles.Count - _mobilesBefore;

            // Both ends or neither. SafetyLogBytes returns -1 for "could not read", and subtracting
            // that from a real length either way invents a number - an unreadable BEFORE against a
            // readable AFTER would report the whole log as this save's growth and fail a clean save.
            // An axis that could not be measured makes no claim; the serial counters carry the test.
            long safetyAfter = SafetyLogBytes();
            long safetyGrew = _safetyBytesBefore >= 0 && safetyAfter >= 0
                ? safetyAfter - _safetyBytesBefore
                : 0L;

            string firstOffender;
            int warned;
            bool scanned = TryScanTap(out warned, out firstOffender);

            LastCreated = itemsMade + mobilesMade;
            LastWarnings = warned;

            _last = Judge(
                itemsMade, mobilesMade, itemDelta, mobileDelta,
                safetyGrew, warned, scanned, firstOffender);
        }

        /// <summary>
        /// The verdict. Fail is reserved for a contract that is broken NOW - something was created,
        /// the engine logged a safety violation, or the world grew. Warn is for an axis that could
        /// not be measured, which is a different thing from one that came out clean.
        /// </summary>
        private static HealthResult Judge(
            int itemsMade, int mobilesMade, int itemDelta, int mobileDelta,
            long safetyGrew, int warned, bool scanned, string firstOffender)
        {
            var faults = new List<string>();

            if (itemsMade != 0 || mobilesMade != 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "an entity was constructed during the save - the serial counter advanced by "
                    + "{0} item(s) and {1} mobile(s), which is an upper bound (NewItem skips "
                    + "serials already in use)",
                    itemsMade, mobilesMade));
            }

            if (safetyGrew > 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "{0} byte(s) appended to {1}", safetyGrew, SafetyLogFile));
            }

            if (warned > 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "{0} save-safety warning(s) on the console; first: {1}",
                    warned, firstOffender ?? "(not captured)"));
            }

            if (itemDelta > 0 || mobileDelta > 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "the world grew across the save (+{0} item(s), +{1} mobile(s))",
                    Math.Max(0, itemDelta), Math.Max(0, mobileDelta)));
            }

            if (faults.Count > 0)
            {
                return HealthResult.Fail(String.Join("; ", faults.ToArray())
                    + ". A save handler is mutating the world - see Scripts/Custom/Core/SaveIntegrity.cs.");
            }

            string detail = String.Format(
                CultureInfo.InvariantCulture,
                "save {0}: nothing constructed, no safety warning, {1}",
                Saves, DescribeDelta(itemDelta, mobileDelta));

            if (!scanned)
            {
                return HealthResult.Warn(detail
                    + "; the console tap could not scope this save, so only the serial counters and "
                    + SafetyLogFile + " were read");
            }

            return HealthResult.Ok(detail);
        }

        private static string DescribeDelta(int itemDelta, int mobileDelta)
        {
            if (itemDelta == 0 && mobileDelta == 0)
            {
                return "world unchanged";
            }

            // Only a fall reaches here; a rise is a fault above. ProcessDecay is the one thing in
            // the bracket entitled to delete, and it runs at Server/World.cs:1189.
            return String.Format(
                CultureInfo.InvariantCulture,
                "{0} item(s) and {1} mobile(s) decayed away (ProcessDecay)",
                -itemDelta, -mobileDelta);
        }

        /// <summary>
        /// The console lines this save produced, counted for the engine's warning.
        ///
        /// Exact rather than approximate: AfterWorldSave runs on the game thread immediately after
        /// the save, so nothing has written since, and the newest (Sequence - before) lines ARE the
        /// window. False when the tap is off or the window outran the ring.
        /// </summary>
        private static bool TryScanTap(out int warned, out string first)
        {
            warned = 0;
            first = null;

            if (!ConsoleTap.Installed)
            {
                return false;
            }

            long produced = ConsoleTap.Sequence - _tapSequenceBefore;

            if (produced <= 0)
            {
                return true;
            }

            if (produced > MaxWindowLines)
            {
                return false;
            }

            IList<string> window = ConsoleTap.Tail((int)produced);

            if (window.Count < produced)
            {
                // The ring dropped some of the window. Whatever is left may still name the fault,
                // but the count would be a floor rather than a count, so it is not reported as one.
                return false;
            }

            for (int i = 0; i < window.Count; i++)
            {
                string line = window[i];

                if (line == null || line.IndexOf(SafetyNeedle, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                warned++;

                if (first == null)
                {
                    first = FirstLine(line);
                }
            }

            return true;
        }

        /// <summary>
        /// The safety banner is one WriteLine carrying three lines of text, so a report that quotes
        /// it whole is unreadable. The first line is the one with the serial and the type on it.
        /// </summary>
        private static string FirstLine(string text)
        {
            int cut = text.IndexOfAny(new[] { '\r', '\n' });

            return cut < 0 ? text : text.Substring(0, cut);
        }

        private static long SafetyLogBytes()
        {
            try
            {
                var info = new FileInfo(Path.Combine(Core.BaseDirectory, SafetyLogFile));

                return info.Exists ? info.Length : 0L;
            }
            catch
            {
                // Unreadable is not the same as absent, but neither gives a number to compare, and
                // a save must not be judged on one this check could not take.
                return -1L;
            }
        }

        private static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                // Deliberately not Ok. A check that has observed nothing has nothing to stand on,
                // and saying "Ok" would let a clean [CoreSmoke] certify a contract never tested.
                return HealthResult.Warn("no world save observed yet this boot");
            }

            return _last;
        }
    }
}
