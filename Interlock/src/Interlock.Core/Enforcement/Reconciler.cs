using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// The catch-up at service start: work the database still owes OIB. Enforcement that was queued in memory when the
    /// service stopped is gone, but its state is stored, so it is queued again from there.
    /// </summary>
    public static class Reconciler
    {
        /// <summary>
        /// Queues an Apply job for every ACTIVE lock whose enforce state is PENDING or FAILED, and a Release job for every
        /// RELEASED lock whose unlock is still PENDING. All jobs are due now. Returns how many jobs it queued.
        /// <para>
        /// The worker runs an Apply job only while its lock is cached, so an Apply job's lock is put into the cache first, unless
        /// the cache holds a lock with that id already (that entry stays as it is). A Release job is never skipped by the worker,
        /// and its lock is not cached. Each job and the cache hold their own copy of the lock.
        /// </para>
        /// <para>
        /// Runs under <see cref="LockCache.WriteGate"/>, with both database reads first: when the database cannot be reached,
        /// <c>DbUnavailableException</c> reaches the caller (which decides whether to try again) and nothing has
        /// been cached or queued. A lock the database lists twice, or one that already has a queued job of that kind, is queued
        /// once per kind.
        /// </para>
        /// </summary>
        public static int QueueOnStartup(ILockRepository locks, LockCache cache, ActionQueue queue, IClock clock)
        {
            if (locks == null) throw new ArgumentNullException(nameof(locks));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            lock (cache.WriteGate)
            {
                var active = locks.LoadActive();
                var releases = locks.LoadPendingReleases();
                var now = clock.UtcNow;

                var queued = 0;
                var applying = new HashSet<long>();
                foreach (var l in active)
                {
                    var open = l.EnforceState == EnforceState.Pending || l.EnforceState == EnforceState.Failed;
                    if (!open || !applying.Add(l.Id) || HasQueued(queue, l.Id, JobKind.Apply))
                        continue;

                    if (!cache.Snapshot.Any(c => c.Id == l.Id))
                        cache.Upsert(l.Clone());
                    queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = JobKind.Apply, NotBeforeUtc = now });
                    queued++;
                }

                var releasing = new HashSet<long>();
                foreach (var l in releases)
                {
                    if (!releasing.Add(l.Id) || HasQueued(queue, l.Id, JobKind.Release))
                        continue;

                    queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = JobKind.Release, NotBeforeUtc = now });
                    queued++;
                }

                return queued;
            }
        }

        // A job of the same kind may be queued already, for example the Apply the cache reload queues for a flushed spool record.
        // The queue does not de-duplicate, and a second Apply could turn an applied lock into FAILED (a re-lock may not answer OK).
        private static bool HasQueued(ActionQueue queue, long lockId, JobKind kind)
        {
            return queue.Any(j => j.Kind == kind && j.Lock != null && j.Lock.Id == lockId);
        }
    }
}
