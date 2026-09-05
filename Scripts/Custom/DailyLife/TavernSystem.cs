using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Fills the tavern after dark and empties it at dawn.
    ///
    /// Patrons are tracked in a plain in-memory list rather than persisted. That is deliberate:
    /// a restart deletes any saved patrons (see DailyLifePatron) and this list comes back empty,
    /// so ApplyPhase simply rebuilds the correct crowd for whatever phase it is - no orphans, no
    /// reconciliation.
    /// </summary>
    public static class TavernSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        private static readonly List<DailyLifePatron> _patrons = new List<DailyLifePatron>();

        public static int PatronCount
        {
            get { return _patrons.Count; }
        }

        public static void Initialize()
        {
            DayCycleSystem.PhaseChanged += OnPhaseChanged;

            // Apply the current phase rather than waiting for a transition, so a restart at
            // midnight has a full tavern immediately.
            ApplyPhase(DayCycleSystem.Current);
        }

        private static void OnPhaseChanged(DayPhase oldPhase, DayPhase newPhase)
        {
            ApplyPhase(newPhase);
        }

        public static void ApplyPhase(DayPhase phase)
        {
            if (phase.IsAfterDark())
            {
                Fill();
            }
            else
            {
                Empty();
            }
        }

        /// <summary>
        /// Rebuilds the crowd from the current config.
        ///
        /// Empties first rather than just re-applying: Fill only tops up to the wanted count, so
        /// lowering patronCount or moving the tavern would otherwise have no visible effect.
        /// </summary>
        public static void Reload()
        {
            Empty();
            ApplyPhase(DayCycleSystem.Current);
        }

        private static void Fill()
        {
            TavernConfig tavern = DailyLifeSystem.Config.Tavern;

            if (tavern == null)
            {
                return;
            }

            NavDestination destination = Nav.Destination(tavern.Destination);

            if (destination == null || destination.Map == null || destination.Map == Map.Internal)
            {
                return;
            }

            Prune();

            for (int i = _patrons.Count; i < tavern.PatronCount; i++)
            {
                var patron = new DailyLifePatron();

                Point3D spot;

                // The arrival picker does the spreading: several arrival points, chosen at
                // random, each scattered a little. No two patrons land on one tile unless the
                // destination only has one place to stand.
                if (!NavArrivals.TryPick(destination, patron, out spot))
                {
                    patron.Delete();

                    Log.Warn(
                        "Could not place a tavern patron at '{0}'; check its arrival points.",
                        destination.Id);
                    break;
                }

                // A small wander radius keeps them milling about inside rather than pinned to a
                // tile or drifting out of the door.
                patron.Home = spot;
                patron.RangeHome = 3;

                patron.MoveToWorld(spot, destination.Map);

                _patrons.Add(patron);
            }
        }

        private static void Empty()
        {
            for (int i = _patrons.Count - 1; i >= 0; i--)
            {
                DailyLifePatron patron = _patrons[i];

                if (patron != null && !patron.Deleted)
                {
                    patron.Delete();
                }
            }

            _patrons.Clear();
        }

        private static void Prune()
        {
            for (int i = _patrons.Count - 1; i >= 0; i--)
            {
                if (_patrons[i] == null || _patrons[i].Deleted)
                {
                    _patrons.RemoveAt(i);
                }
            }
        }
    }
}
