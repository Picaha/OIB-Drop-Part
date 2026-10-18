using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Model;
using Interlock.Core.Text;

namespace Interlock.Core.Decisions
{
    /// <summary>Scan-check rules: decides OK / NotOk for a scanned PU against the active locks.</summary>
    public static class DecisionEngine
    {
        public static Decision Decide(PuInfo pu, IReadOnlyCollection<LockRecord> active)
        {
            if (pu == null) throw new ArgumentNullException(nameof(pu));
            if (active == null) throw new ArgumentNullException(nameof(active));

            var matched = active
                .Where(l => l.Status == LockStatus.Active && Matches(l, pu))
                .OrderBy(l => l.Id)
                .ToList();

            return new Decision
            {
                Ok = matched.Count == 0,
                Message = matched.Count == 0 ? null : string.Join("; ", matched.Select(FormatMessage)),
                MatchedLockIds = matched.Select(l => l.Id).ToList()
            };
        }

        public static bool Matches(LockRecord l, PuInfo pu)
        {
            if (l == null) throw new ArgumentNullException(nameof(l));
            if (pu == null) throw new ArgumentNullException(nameof(pu));

            switch (l.Type)
            {
                case LockType.Pu: return SameValue(l, pu.Uid);
                case LockType.Pn: return InScope(l, pu) && SameValue(l, pu.ComponentName);
                case LockType.Mpn: return InScope(l, pu) && SameValue(l, pu.Mpn);
                case LockType.Supplier: return InScope(l, pu) && SameValue(l, pu.Supplier);
                default: return false; // FEEDER and CONVEYOR locks are enforced on the machine, not by scan check
            }
        }

        public static string FormatMessage(LockRecord l)
        {
            if (l == null) throw new ArgumentNullException(nameof(l));
            return "Interlock: " + DbEnum.ToDb(l.Type) + " " + l.Target + " locked \u2013 " + l.Reason + " (#" + l.Id + ")";
        }

        public static LockRecord FindFeederLock(string feederId, IReadOnlyCollection<LockRecord> active)
        {
            if (active == null) throw new ArgumentNullException(nameof(active));

            var norm = Normalizer.Norm(feederId);
            if (norm == null)
                return null;

            return active
                .Where(l => l.Status == LockStatus.Active && l.Type == LockType.Feeder && l.TargetNorm == norm)
                .OrderBy(l => l.Id)
                .FirstOrDefault();
        }

        private static bool SameValue(LockRecord l, string value)
        {
            var norm = Normalizer.Norm(value);
            return norm != null && l.TargetNorm != null && l.TargetNorm == norm;
        }

        /// <summary>Plant-wide lock, PU without a line path (spec section 6 rule 4), or the same line.</summary>
        private static bool InScope(LockRecord l, PuInfo pu)
        {
            return l.LinePath == null
                || string.IsNullOrWhiteSpace(pu.LinePath)
                || string.Equals(l.LinePath, pu.LinePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
