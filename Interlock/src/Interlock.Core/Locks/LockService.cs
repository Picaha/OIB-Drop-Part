using System;
using System.Collections.Generic;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Core.Text;

namespace Interlock.Core.Locks
{
    /// <summary>The outcome of <see cref="LockService.CreateLock"/>: the lock, and whether this call created it.</summary>
    public sealed class CreateLockResult
    {
        public CreateLockResult(LockRecord lockRecord, bool created)
        {
            Lock = lockRecord;
            Created = created;
        }

        public LockRecord Lock { get; }

        /// <summary>False when an identical ACTIVE lock already existed and <see cref="Lock"/> is that one.</summary>
        public bool Created { get; }
    }

    /// <summary>A lock with the OIB calls that were made for it.</summary>
    public sealed class LockDetail
    {
        public LockDetail(LockRecord lockRecord, IList<ActionLogEntry> actions)
        {
            Lock = lockRecord;
            Actions = actions;
        }

        public LockRecord Lock { get; }

        public IList<ActionLogEntry> Actions { get; }
    }

    /// <summary>
    /// What the REST API and the UI do with locks: create, release, retry and read them. Each change to the database, the
    /// <see cref="LockCache"/> and the <see cref="ActionQueue"/> runs under <see cref="LockCache.WriteGate"/>, so a cache
    /// reload can never drop a lock created meanwhile. A database outage reaches the caller as <see cref="ErrorKind.Unavailable"/>.
    /// </summary>
    public sealed class LockService
    {
        private const string SetupCenterName = "SetupCenter";

        private readonly ILockRepository _locks;
        private readonly IAuditRepository _audit;
        private readonly LockCache _cache;
        private readonly ActionQueue _queue;
        private readonly IStationDirectory _stations;
        private readonly ILockSpool _spool;
        private readonly IClock _clock;
        private readonly ILog _log;

        public LockService(ILockRepository locks, IAuditRepository audit, LockCache cache, ActionQueue queue,
            IStationDirectory stations, ILockSpool spool, IClock clock, ILog log)
        {
            if (locks == null) throw new ArgumentNullException(nameof(locks));
            if (audit == null) throw new ArgumentNullException(nameof(audit));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (stations == null) throw new ArgumentNullException(nameof(stations));
            if (spool == null) throw new ArgumentNullException(nameof(spool));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _locks = locks;
            _audit = audit;
            _cache = cache;
            _queue = queue;
            _stations = stations;
            _spool = spool;
            _clock = clock;
            _log = log;
        }

        /// <summary>
        /// Creates the lock and queues its Apply job. When an identical ACTIVE lock already exists (also one a concurrent
        /// writer inserted first), that lock is returned with <c>Created == false</c> and nothing is queued.
        /// </summary>
        public CreateLockResult CreateLock(Principal p, CreateLockRequest r)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (r == null) throw new ArgumentNullException(nameof(r));

            if (!Permissions.CanLock(p))
                throw new InterlockException(ErrorKind.Forbidden, "Not allowed to create locks");
            LockValidator.Validate(r, _stations.Stations);

            var l = NewLock(
                r.Type.Value,
                r.Target,
                string.IsNullOrWhiteSpace(r.LinePath) ? null : r.LinePath.Trim(),
                r.Lanes,
                r.Reason,
                p.IsApiKey ? LockSource.Api : LockSource.Manual,
                p.Name);

            try
            {
                return Store(l);
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>
        /// Creates (or finds) the PU lock for a Setup Center component mismatch. Unlike <see cref="CreateLock"/> it lets
        /// <see cref="DbUnavailableException"/> through: the caller spools the lock while the database is down.
        /// </summary>
        internal LockRecord CreateFromSetupCenter(string uid, string reason)
        {
            if (string.IsNullOrWhiteSpace(uid))
                throw new ArgumentException("A UID is required.", nameof(uid));

            var l = NewLock(LockType.Pu, uid, null, null, reason, LockSource.SetupCenter, SetupCenterName);
            return Store(l).Lock;
        }

        /// <summary>
        /// Releases the lock: marks it released, takes it out of the cache, drops its queued jobs and queues a Release job.
        /// Returns the stored row.
        /// </summary>
        public LockRecord Release(Principal p, long id, string reason)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            if (!Permissions.CanRelease(p))
                throw new InterlockException(ErrorKind.Forbidden, "Not allowed to release locks");
            if (string.IsNullOrWhiteSpace(reason))
                throw new InterlockException(ErrorKind.Validation, "Reason is required");
            var releaseReason = reason.Trim();

            try
            {
                lock (_cache.WriteGate)
                {
                    var l = _locks.Get(id);
                    if (l == null)
                        throw NotFound();
                    if (l.Status == LockStatus.Released)
                        throw new InterlockException(ErrorKind.InvalidState, "Lock is already released");

                    var now = _clock.UtcNow;
                    _locks.MarkReleased(id, p.Name, now, releaseReason);

                    // Once the row is released the cache and the queue must follow, even if reading the row back fails.
                    l.Status = LockStatus.Released;
                    l.ReleasedBy = p.Name;
                    l.ReleasedAtUtc = now;
                    l.ReleaseReason = releaseReason;
                    l.EnforceState = EnforceState.Pending;
                    _cache.Remove(id);
                    _queue.Cancel(id);
                    Enqueue(JobKind.Release, l);
                    _log.Info("Lock #" + id + " released by " + p.Name);

                    var stored = _locks.Get(id);
                    if (stored == null)
                        throw NotFound();
                    return stored;
                }
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>
        /// Queues the OIB work of a FAILED lock again: Apply for an ACTIVE lock, Release for a RELEASED one. The lock goes
        /// back to PENDING first. An ACTIVE lock is put into the cache (the worker skips Apply jobs of uncached locks); a
        /// RELEASED lock never is. Returns the updated row.
        /// </summary>
        public LockRecord Retry(Principal p, long id)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            try
            {
                lock (_cache.WriteGate)
                {
                    var l = _locks.Get(id);
                    if (l == null)
                        throw NotFound();
                    if (l.EnforceState != EnforceState.Failed)
                        throw new InterlockException(ErrorKind.InvalidState, "Only a FAILED lock can be retried");

                    var active = l.Status == LockStatus.Active;
                    if (!(active ? Permissions.CanLock(p) : Permissions.CanRelease(p)))
                        throw new InterlockException(ErrorKind.Forbidden, "Not allowed to retry this lock");

                    _locks.SetEnforceState(id, EnforceState.Pending, null);
                    l.EnforceState = EnforceState.Pending;
                    l.EnforceError = null;
                    if (active)
                    {
                        // The row was just read under the gate, so it is ACTIVE now and no in-process release or reload can
                        // interleave: caching it cannot bring a released lock back. The worker skips Apply jobs of uncached locks.
                        _cache.Upsert(l.Clone());
                    }
                    Enqueue(active ? JobKind.Apply : JobKind.Release, l);
                    _log.Info("Lock #" + id + " retried by " + p.Name);
                    return l;
                }
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>The stored locks that match the filter (no filter when <paramref name="q"/> is null), for any signed-in caller.</summary>
        public IList<LockRecord> Query(Principal p, LockQuery q)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            try
            {
                return _locks.Query(q ?? new LockQuery());
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>One lock with its action log, for any signed-in caller.</summary>
        public LockDetail Get(Principal p, long id)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            try
            {
                var l = _locks.Get(id);
                if (l == null)
                    throw NotFound();
                return new LockDetail(l, _audit.GetActions(id));
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        private LockRecord NewLock(LockType type, string target, string linePath, Lanes? lanes, string reason,
            LockSource source, string createdBy)
        {
            var trimmed = target.Trim();
            return new LockRecord
            {
                Type = type,
                Target = trimmed,
                TargetNorm = Normalizer.Norm(trimmed),
                Lanes = lanes,
                LinePath = linePath,
                Reason = reason == null ? null : reason.Trim(),
                Source = source,
                CreatedBy = createdBy,
                CreatedAtUtc = _clock.UtcNow,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        /// <summary>The shared create path: dedupe, insert, cache, queue Apply. Lets <see cref="DbUnavailableException"/> through.</summary>
        private CreateLockResult Store(LockRecord l)
        {
            lock (_cache.WriteGate)
            {
                var existing = _cache.FindActive(l.Type, l.TargetNorm, l.LinePath, l.Lanes)
                    ?? _locks.FindActive(l.Type, l.TargetNorm, l.LinePath, l.Lanes);
                if (existing != null)
                    return new CreateLockResult(existing.Clone(), false);

                LockRecord stored;
                try
                {
                    stored = _locks.Insert(l);
                }
                catch (DuplicateActiveLockException)
                {
                    // A concurrent writer inserted the same lock first; theirs is the lock.
                    var winner = _locks.FindActive(l.Type, l.TargetNorm, l.LinePath, l.Lanes);
                    if (winner == null)
                        throw;
                    return new CreateLockResult(winner, false);
                }

                _cache.Upsert(stored);
                Enqueue(JobKind.Apply, stored);
                _log.Info("Lock #" + stored.Id + " " + DbEnum.ToDb(stored.Type) + " " + stored.Target + " created by " + stored.CreatedBy);
                return new CreateLockResult(stored.Clone(), true);
            }
        }

        /// <summary>Queues a job for the lock. The job gets its own copy: the worker changes <c>job.Lock</c>.</summary>
        private void Enqueue(JobKind kind, LockRecord l)
        {
            _queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = kind, NotBeforeUtc = _clock.UtcNow });
        }

        private static InterlockException NotFound()
        {
            return new InterlockException(ErrorKind.NotFound, "Lock not found");
        }

        private static InterlockException Unavailable()
        {
            return new InterlockException(ErrorKind.Unavailable, "Database unavailable");
        }
    }
}
