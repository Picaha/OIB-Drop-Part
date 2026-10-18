using System;
using Interlock.Core.Model;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// Decides which Setup Center lock entries Interlock turns into a PU lock of its own: component mismatches
    /// (<c>Reason == 1</c>), whatever their source. Entries older than the last release of the PU were already dealt with.
    /// </summary>
    public static class EscalationFilter
    {
        /// <summary>Setup Center's <c>LockInfo.Reason</c> for "the mounted component does not match".</summary>
        public const int ComponentMismatchReason = 1;

        /// <summary>
        /// The newest component-mismatch entry of the report, or null. With <paramref name="lastReleaseUtc"/> set, only entries
        /// dated strictly after it count. A missing report, a missing entry list or a missing entry yields no match.
        /// </summary>
        public static LockInfoRef FindNewMismatch(PuLockReport r, DateTime? lastReleaseUtc)
        {
            if (r == null || r.LockInfos == null)
                return null;

            LockInfoRef newest = null;
            foreach (var info in r.LockInfos)
            {
                if (info == null || info.Reason != ComponentMismatchReason)
                    continue;
                if (lastReleaseUtc.HasValue && info.DateUtc <= lastReleaseUtc.Value)
                    continue;
                if (newest == null || info.DateUtc > newest.DateUtc)
                    newest = info;
            }
            return newest;
        }
    }
}
