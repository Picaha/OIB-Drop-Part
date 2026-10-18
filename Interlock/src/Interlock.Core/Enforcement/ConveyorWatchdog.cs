using System;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// Checks the conveyors Interlock keeps blocked. Someone can lift a block at the machine, so each tick reads the real
    /// state of every ACTIVE conveyor lock that is APPLIED and queues a Reblock job for each one whose lanes are no longer
    /// blocked. A lock that is not APPLIED yet is the worker's business, and a lock without lanes cannot be checked.
    /// Run it from one thread (it is meant to tick every 60 s).
    /// </summary>
    public sealed class ConveyorWatchdog
    {
        private readonly LockCache _cache;
        private readonly ILineControlClient _lc;
        private readonly ActionQueue _queue;
        private readonly IClock _clock;
        private readonly ILog _log;

        public ConveyorWatchdog(LockCache cache, ILineControlClient lc, ActionQueue queue, IClock clock, ILog log)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (lc == null) throw new ArgumentNullException(nameof(lc));
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _cache = cache;
            _lc = lc;
            _queue = queue;
            _clock = clock;
            _log = log;
        }

        /// <summary>
        /// Reads the state of each checked station, one after the other, and queues a Reblock job (with its own copy of the lock)
        /// when the lock's lanes are not blocked. The cache's write gate is not held while OIB is called. A lock that already has
        /// a job in the queue is left alone: a Reblock that failed waits there for its retry, and the tick must not pile a
        /// second one on top. A station that cannot be read is logged (one warning) and the next one is checked. Never throws.
        /// </summary>
        public void Tick()
        {
            foreach (var l in _cache.Snapshot)
            {
                if (l.Status != LockStatus.Active || l.Type != LockType.Conveyor
                    || l.EnforceState != EnforceState.Applied || l.Lanes == null)
                    continue;

                try
                {
                    Check(l);
                }
                catch (Exception ex)
                {
                    _log.Warn("Conveyor watchdog: could not check " + l.Target + " (lock #" + l.Id + "): " + ex.Message);
                }
            }
        }

        private void Check(LockRecord l)
        {
            var state = _lc.GetState(l.Target);
            if (state == null)
                throw new InvalidOperationException("Line Control returned no conveyor state.");
            if (state.IsBlocked(l.Lanes.Value))
                return;

            // Checked after the OIB call, right before queueing, so the answer is as fresh as it can be.
            if (_queue.Any(j => IsJobOf(j, l)))
                return;

            _queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = JobKind.Reblock, NotBeforeUtc = _clock.UtcNow });
        }

        /// <summary>The job belongs to the lock: same id, and for a spooled lock (Id 0) the same identity, as no id tells those apart.</summary>
        private static bool IsJobOf(EnforcementJob job, LockRecord l)
        {
            return job.Lock.Id == l.Id && (l.Id != 0 || LockCache.SameIdentity(job.Lock, l));
        }
    }
}
