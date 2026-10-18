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
                case LockType.Pn: return LineInScope(l.LinePath, pu.LinePath) && SameValue(l, pu.ComponentName);
                case LockType.Mpn: return LineInScope(l.LinePath, pu.LinePath) && SameValue(l, pu.Mpn);
                case LockType.Supplier: return LineInScope(l.LinePath, pu.LinePath) && SameValue(l, pu.Supplier);
                default: return false; // FEEDER and CONVEYOR locks are enforced on the machine, not by scan check
            }
        }

        /// <summary>
        /// Line scope of a lock. Both paths are compared with <see cref="Normalizer.Norm"/>: a blank lock path is plant-wide,
        /// a blank other path (PU without a line, spec section 6 rule 4) is in scope of every lock, otherwise the lines must be equal.
        /// </summary>
        public static bool LineInScope(string lockLinePath, string otherLinePath)
        {
            var lockLine = Normalizer.Norm(lockLinePath);
            var otherLine = Normalizer.Norm(otherLinePath);
            return lockLine == null || otherLine == null || lockLine == otherLine;
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
    }
}
