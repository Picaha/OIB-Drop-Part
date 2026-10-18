using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;

namespace Interlock.Tests.Fakes
{
    /// <summary>
    /// A lock repository that lives in memory and follows the rules of the real one: ids count up from 1,
    /// reads return copies, and an ACTIVE lock with the same identity is a duplicate. Updates to an unknown id
    /// do nothing, like an SQL UPDATE that matches no row.
    /// </summary>
    public sealed class InMemoryLockRepository : ILockRepository
    {
        private readonly object _gate = new object();
        private readonly List<LockRecord> _rows = new List<LockRecord>();
        private readonly List<AppliedPuRow> _appliedPus = new List<AppliedPuRow>();
        private long _lastId;
        private LockRecord _duplicateForNextInsert;

        /// <summary>While true, every <see cref="ILockRepository"/> member throws <see cref="DbUnavailableException"/>.</summary>
        public bool Down { get; set; }

        // ---- test helpers: they ignore Down and the uniqueness rule ----

        /// <summary>Stores the row as given, as ACTIVE. An <c>Id</c> of 0 gets the next id.</summary>
        public LockRecord InsertActive(LockRecord l)
        {
            lock (_gate)
                return StoreAsGiven(l, LockStatus.Active);
        }

        /// <summary>Stores the row as given, as RELEASED. An <c>Id</c> of 0 gets the next id.</summary>
        public LockRecord InsertReleased(LockRecord l)
        {
            lock (_gate)
                return StoreAsGiven(l, LockStatus.Released);
        }

        /// <summary>
        /// The next <see cref="Insert"/> models a concurrent writer winning the race: it stores
        /// <paramref name="existing"/> as an ACTIVE lock (keeping its id) and then throws <see cref="DuplicateActiveLockException"/>.
        /// </summary>
        public void FailNextInsertWithDuplicate(LockRecord existing)
        {
            if (existing == null)
                throw new ArgumentNullException(nameof(existing));
            lock (_gate)
                _duplicateForNextInsert = existing.Clone();
        }

        // ---- ILockRepository ----

        public IList<LockRecord> LoadActive()
        {
            CheckUp();
            lock (_gate)
                return Copies(_rows.Where(r => r.Status == LockStatus.Active));
        }

        public IList<LockRecord> LoadPendingReleases()
        {
            CheckUp();
            lock (_gate)
                return Copies(_rows.Where(r => r.Status == LockStatus.Released && r.EnforceState == EnforceState.Pending));
        }

        public LockRecord Insert(LockRecord l)
        {
            if (l == null)
                throw new ArgumentNullException(nameof(l));
            CheckUp();
            lock (_gate)
            {
                if (_duplicateForNextInsert != null)
                {
                    var existing = _duplicateForNextInsert;
                    _duplicateForNextInsert = null;
                    StoreAsGiven(existing, LockStatus.Active);
                    throw new DuplicateActiveLockException("An active lock with the same identity already exists.");
                }

                if (l.Status == LockStatus.Active && _rows.Any(r => r.Status == LockStatus.Active && SameIdentity(r, l)))
                    throw new DuplicateActiveLockException("An active lock with the same identity already exists.");

                var row = l.Clone();
                row.Id = ++_lastId;
                _rows.Add(row);
                return row.Clone();
            }
        }

        public LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes)
        {
            CheckUp();
            var target = Normalizer.Norm(targetNorm);
            if (target == null)
                return null;
            var line = Normalizer.Norm(linePath);

            lock (_gate)
            {
                var row = _rows.FirstOrDefault(r => r.Status == LockStatus.Active
                    && r.Type == t
                    && r.TargetNorm == target
                    && Normalizer.Norm(r.LinePath) == line
                    && r.Lanes == lanes);
                return row == null ? null : row.Clone();
            }
        }

        public LockRecord Get(long id)
        {
            CheckUp();
            lock (_gate)
            {
                var row = _rows.FirstOrDefault(r => r.Id == id);
                return row == null ? null : row.Clone();
            }
        }

        public IList<LockRecord> Query(LockQuery q)
        {
            CheckUp();
            q = q ?? new LockQuery();
            var line = Normalizer.Norm(q.LinePath);
            var text = string.IsNullOrWhiteSpace(q.Text) ? null : q.Text;

            lock (_gate)
            {
                return Copies(_rows.Where(r =>
                        (q.Status == null || r.Status == q.Status)
                        && (q.Type == null || r.Type == q.Type)
                        && (q.EnforceState == null || r.EnforceState == q.EnforceState)
                        && (line == null || Normalizer.Norm(r.LinePath) == line)
                        && (text == null || ContainsIgnoreCase(r.Target, text) || ContainsIgnoreCase(r.Reason, text))),
                    q.Limit);
            }
        }

        public void MarkReleased(long id, string by, DateTime atUtc, string reason)
        {
            CheckUp();
            lock (_gate)
            {
                var row = _rows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                    return;
                row.Status = LockStatus.Released;
                row.ReleasedBy = by;
                row.ReleasedAtUtc = atUtc;
                row.ReleaseReason = reason;
                row.EnforceState = EnforceState.Pending;
            }
        }

        public void SetEnforceState(long id, EnforceState s, string error)
        {
            CheckUp();
            lock (_gate)
            {
                var row = _rows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                    return;
                row.EnforceState = s;
                row.EnforceError = error;
            }
        }

        public void AddAppliedPu(long lockId, string uid, DateTime atUtc)
        {
            CheckUp();
            lock (_gate)
                _appliedPus.Add(new AppliedPuRow { LockId = lockId, Uid = uid });
        }

        public IList<string> GetOpenAppliedPus(long lockId)
        {
            CheckUp();
            lock (_gate)
                return _appliedPus.Where(a => a.LockId == lockId && !a.Closed).Select(a => a.Uid).ToList();
        }

        public void CloseAppliedPu(long lockId, string uid, DateTime atUtc)
        {
            CheckUp();
            var norm = Normalizer.Norm(uid);
            if (norm == null)
                return;

            lock (_gate)
            {
                foreach (var open in _appliedPus.Where(a => a.LockId == lockId && !a.Closed && Normalizer.Norm(a.Uid) == norm))
                    open.Closed = true;
            }
        }

        public bool IsUidCoveredByOtherActiveLock(string uidNorm, long exceptLockId)
        {
            CheckUp();
            var uid = Normalizer.Norm(uidNorm);
            if (uid == null)
                return false;

            lock (_gate)
            {
                if (_rows.Any(r => r.Status == LockStatus.Active && r.Type == LockType.Pu && r.TargetNorm == uid && r.Id != exceptLockId))
                    return true;

                return _appliedPus.Any(a => !a.Closed
                    && a.LockId != exceptLockId
                    && Normalizer.Norm(a.Uid) == uid
                    && _rows.Any(r => r.Id == a.LockId && r.Status == LockStatus.Active));
            }
        }

        public DateTime? GetLatestReleaseUtc(LockType t, string targetNorm)
        {
            CheckUp();
            var target = Normalizer.Norm(targetNorm);
            if (target == null)
                return null;

            lock (_gate)
            {
                return _rows
                    .Where(r => r.Status == LockStatus.Released && r.Type == t && r.TargetNorm == target)
                    .Select(r => r.ReleasedAtUtc)
                    .Max();
            }
        }

        // ---- internals ----

        private void CheckUp()
        {
            if (Down)
                throw new DbUnavailableException("The in-memory lock repository is down.");
        }

        private LockRecord StoreAsGiven(LockRecord given, LockStatus status)
        {
            var row = given.Clone();
            row.Status = status;
            if (row.Id == 0)
                row.Id = ++_lastId;
            else if (_rows.Any(r => r.Id == row.Id))
                throw new InvalidOperationException("A lock with id " + row.Id + " is already stored.");
            else if (row.Id > _lastId)
                _lastId = row.Id;

            _rows.Add(row);
            return row.Clone();
        }

        private static bool SameIdentity(LockRecord a, LockRecord b)
        {
            return a.Type == b.Type
                && a.TargetNorm == b.TargetNorm
                && Normalizer.Norm(a.LinePath) == Normalizer.Norm(b.LinePath)
                && a.Lanes == b.Lanes;
        }

        private static bool ContainsIgnoreCase(string value, string part)
        {
            return value != null && value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Copies of the rows ordered by id, at most <paramref name="limit"/> of them.</summary>
        private static IList<LockRecord> Copies(IEnumerable<LockRecord> rows, int limit = int.MaxValue)
        {
            return rows.OrderBy(r => r.Id).Take(limit).Select(r => r.Clone()).ToList();
        }

        private sealed class AppliedPuRow
        {
            public long LockId { get; set; }
            public string Uid { get; set; }
            public bool Closed { get; set; }
        }
    }
}
