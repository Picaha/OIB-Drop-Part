using System;
using System.Collections.Generic;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Text;

namespace Interlock.Core.Locks
{
    public static class LockValidator
    {
        /// <summary>Throws <see cref="InterlockException"/> when the request is not a valid lock.</summary>
        public static void Validate(CreateLockRequest r, IList<StationRef> knownStations)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));

            if (r.Type == null)
                throw Invalid("Type is required");
            var type = r.Type.Value;

            if (string.IsNullOrWhiteSpace(r.Target))
                throw Invalid("Target is required");
            if (string.IsNullOrWhiteSpace(r.Reason))
                throw Invalid("Reason is required");

            if (type == LockType.Conveyor)
            {
                if (r.Lanes == null)
                    throw Invalid("Lanes is required for a CONVEYOR lock");
            }
            else if (r.Lanes != null)
            {
                throw Invalid("Lanes is only allowed for a CONVEYOR lock");
            }

            if (Normalizer.Norm(r.LinePath) != null && !ScopesByLine(type))
                throw Invalid("LinePath is only allowed for PN, MPN and SUPPLIER locks");

            if (type == LockType.Conveyor)
                RequireKnownStation(r.Target, knownStations);
        }

        private static bool ScopesByLine(LockType type)
        {
            return type == LockType.Pn || type == LockType.Mpn || type == LockType.Supplier;
        }

        private static void RequireKnownStation(string target, IList<StationRef> knownStations)
        {
            if (knownStations == null || knownStations.Count == 0)
                throw new InterlockException(ErrorKind.Unavailable, "Station list not available");

            var wanted = Normalizer.Norm(target);
            foreach (var station in knownStations)
            {
                if (station != null && Normalizer.Norm(station.StationPath) == wanted)
                    return;
            }
            throw Invalid("Unknown station: " + target);
        }

        private static InterlockException Invalid(string message)
        {
            return new InterlockException(ErrorKind.Validation, message);
        }
    }
}
