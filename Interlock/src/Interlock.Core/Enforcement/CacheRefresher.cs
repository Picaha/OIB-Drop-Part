using System;
using System.Collections.Generic;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// The periodic reload of the <see cref="LockCache"/> from the database. A reload first stores the locks that were spooled
    /// while the database was down, then replaces the cache with the active locks. The whole tick holds
    /// <see cref="LockCache.WriteGate"/>, so no lock can be created, spooled or released in between and then be dropped by the
    /// reload. Every database call comes first; the spool, the queue and the cache change only after all of them succeeded,
    /// so a tick that fails leaves all three as they were and the next tick starts over (a lock that was stored already is
    /// found as a duplicate). Run it from one thread.
    /// </summary>
    public sealed class CacheRefresher
    {
        private readonly ILockRepository _locks;
        private readonly ILockSpool _spool;
        private readonly LockCache _cache;
        private readonly ActionQueue _queue;
        private readonly IClock _clock;
        private readonly ILog _log;
        private volatile bool _dbHealthy;

        public CacheRefresher(ILockRepository locks, ILockSpool spool, LockCache cache, ActionQueue queue, IClock clock, ILog log)
        {
            if (locks == null) throw new ArgumentNullException(nameof(locks));
            if (spool == null) throw new ArgumentNullException(nameof(spool));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _locks = locks;
            _spool = spool;
            _cache = cache;
            _queue = queue;
            _clock = clock;
            _log = log;
        }

        /// <summary>True once a tick has reached the database, until a tick finds it unreachable. False before the first tick.</summary>
        public bool DbHealthy
        {
            get { return _dbHealthy; }
        }

        /// <summary>
        /// Stores the spooled locks, empties the spool, and replaces the cache with the active locks. Each stored lock gets an
        /// Apply job for the stored row in place of its queued Id 0 job. When the database cannot be reached, <see cref="DbHealthy"/>
        /// turns false and the spool, the queue and the cache stay as they were. Any other failure is logged and changes nothing.
        /// Never throws.
        /// </summary>
        public void Tick()
        {
            try
            {
                lock (_cache.WriteGate)
                {
                    Refresh();
                }
            }
            catch (DbUnavailableException ex)
            {
                _dbHealthy = false;
                _log.Warn("Database unavailable, the lock cache and the spool are kept: " + ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error("The lock cache reload failed; the cache and the spool are kept.", ex);
            }
        }

        private void Refresh()
        {
            var spooled = _spool.ReadAll();

            // 1. All database calls. Nothing outside the database has changed until they have all succeeded.
            var stored = new List<StoredSpooled>();
            foreach (var l in spooled)
                stored.Add(Store(l));
            var active = _locks.LoadActive();

            // 2. The spool goes first: if it cannot be emptied, the queue and the cache are still untouched.
            if (spooled.Count > 0)
                _spool.Clear();

            // 3. Queue and cache, in memory, under the gate. A spooled lock was queued as an Apply job with Id 0, which the worker
            //    can neither record nor find in the reloaded cache: it is replaced by a job for the stored row.
            var now = _clock.UtcNow;
            var queuedIds = new HashSet<long>();
            foreach (var s in stored)
            {
                _queue.CancelWhere(j => j.Lock.Id == 0 && SameIdentity(j.Lock, s.Spooled));
                if (s.NeedsApply && queuedIds.Add(s.Row.Id))
                    _queue.Enqueue(new EnforcementJob { Lock = s.Row.Clone(), Kind = JobKind.Apply, NotBeforeUtc = now });
            }
            _cache.Replace(active, now);

            if (!_dbHealthy)
                _log.Info("Database reachable.");
            _dbHealthy = true;
            if (spooled.Count > 0)
                _log.Info(spooled.Count + " spooled lock(s) stored.");
        }

        /// <summary>
        /// Inserts the spooled lock. When an ACTIVE lock with the same identity exists already (stored by an earlier tick that
        /// failed later, or by another writer), that row is the lock; it needs an Apply job only while its enforcement is open.
        /// </summary>
        private StoredSpooled Store(LockRecord spooled)
        {
            try
            {
                return new StoredSpooled(spooled, _locks.Insert(spooled), true);
            }
            catch (DuplicateActiveLockException)
            {
                var existing = _locks.FindActive(spooled.Type, spooled.TargetNorm, spooled.LinePath, spooled.Lanes);
                if (existing == null)
                    throw; // released meanwhile: leave the lock in the spool, the next tick stores it
                var open = existing.EnforceState == EnforceState.Pending || existing.EnforceState == EnforceState.Failed;
                return new StoredSpooled(spooled, existing, open);
            }
        }

        private static bool SameIdentity(LockRecord a, LockRecord b)
        {
            return a.Type == b.Type
                && a.TargetNorm == b.TargetNorm
                && Normalizer.Norm(a.LinePath) == Normalizer.Norm(b.LinePath)
                && a.Lanes == b.Lanes;
        }

        private sealed class StoredSpooled
        {
            public StoredSpooled(LockRecord spooled, LockRecord row, bool needsApply)
            {
                Spooled = spooled;
                Row = row;
                NeedsApply = needsApply;
            }

            public LockRecord Spooled { get; }

            /// <summary>The stored row: the inserted one, or the existing ACTIVE duplicate.</summary>
            public LockRecord Row { get; }

            public bool NeedsApply { get; }
        }
    }
}
