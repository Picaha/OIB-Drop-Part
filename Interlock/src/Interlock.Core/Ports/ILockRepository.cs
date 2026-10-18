using System;
using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>Filters for <see cref="ILockRepository.Query"/>. Null filters are not applied.</summary>
    public sealed class LockQuery
    {
        public LockStatus? Status { get; set; }
        public LockType? Type { get; set; }

        /// <summary>Compared after <c>Normalizer.Norm</c>; a blank value is no filter.</summary>
        public string LinePath { get; set; }

        /// <summary>Case-insensitive substring of the target or the reason.</summary>
        public string Text { get; set; }

        public EnforceState? EnforceState { get; set; }
        public int Limit { get; set; } = 500;
    }

    /// <summary>Stores locks. Every member throws <c>DbUnavailableException</c> when the database cannot be reached.</summary>
    public interface ILockRepository
    {
        IList<LockRecord> LoadActive();

        /// <summary>RELEASED locks whose <c>enforce_state</c> is still PENDING.</summary>
        IList<LockRecord> LoadPendingReleases();

        /// <summary>Returns the stored row with its <c>Id</c>. Throws <c>DuplicateActiveLockException</c> when the unique index fires.</summary>
        LockRecord Insert(LockRecord l);

        /// <summary>The ACTIVE lock with this type, normalised target, normalised line and lanes, or null.</summary>
        LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes);

        LockRecord Get(long id);
        IList<LockRecord> Query(LockQuery q);

        /// <summary>Releases the lock and sets <c>enforce_state</c> to PENDING so the unlock is enforced.</summary>
        void MarkReleased(long id, string by, DateTime atUtc, string reason);

        void SetEnforceState(long id, EnforceState s, string error);

        void AddAppliedPu(long lockId, string uid, DateTime atUtc);
        IList<string> GetOpenAppliedPus(long lockId);
        void CloseAppliedPu(long lockId, string uid, DateTime atUtc);

        /// <summary>True when another ACTIVE lock still covers the UID: a PU lock on it, or an open applied-PU row of that lock.</summary>
        bool IsUidCoveredByOtherActiveLock(string uidNorm, long exceptLockId);

        /// <summary>The newest release time among RELEASED locks of this type and normalised target, or null.</summary>
        DateTime? GetLatestReleaseUtc(LockType t, string targetNorm);
    }
}
