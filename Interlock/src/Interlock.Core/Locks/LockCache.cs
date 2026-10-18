using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Interlock.Core.Model;
using Interlock.Core.Text;

namespace Interlock.Core.Locks
{
    /// <summary>
    /// The active locks External Control answers from. Readers take <see cref="Snapshot"/> without locking;
    /// writers serialise on a lock and publish a new immutable array each time.
    /// </summary>
    public sealed class LockCache
    {
        private readonly object _writeGate = new object();
        private LockRecord[] _items = new LockRecord[0];
        private long _loadedAtTicks;

        /// <summary>
        /// Held by whoever must change the database and this cache as one step (creating, releasing or retrying a lock,
        /// reloading the cache), so a reload can never drop a lock created meanwhile. It is not the cache's own lock:
        /// the cache's members never take it, and holding it does not block them.
        /// </summary>
        public object WriteGate { get; } = new object();

        public IReadOnlyCollection<LockRecord> Snapshot
        {
            get { return Volatile.Read(ref _items); }
        }

        public DateTime? LoadedAtUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _loadedAtTicks);
                return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public void Replace(IEnumerable<LockRecord> active, DateTime nowUtc)
        {
            if (active == null)
                throw new ArgumentNullException(nameof(active));

            lock (_writeGate)
            {
                Publish(active.ToArray());
                Interlocked.Exchange(ref _loadedAtTicks, nowUtc.Ticks);
            }
        }

        /// <summary>Replaces the entry with the same <c>Id</c>; for <c>Id == 0</c> (spooled) the entry with the same identity.</summary>
        public void Upsert(LockRecord l)
        {
            if (l == null)
                throw new ArgumentNullException(nameof(l));

            lock (_writeGate)
            {
                var items = new List<LockRecord>(_items);
                var index = IndexOf(items, l);
                if (index >= 0)
                    items[index] = l;
                else
                    items.Add(l);
                Publish(items.ToArray());
            }
        }

        /// <summary>
        /// Like <see cref="Upsert"/>, but never adds: replaces the entry with the same <c>Id</c> (for <c>Id == 0</c> the entry
        /// with the same identity) and returns true, or changes nothing and returns false. The check and the write are one
        /// step, so a lock removed meanwhile is not put back.
        /// </summary>
        public bool UpsertIfPresent(LockRecord l)
        {
            if (l == null)
                throw new ArgumentNullException(nameof(l));

            lock (_writeGate)
            {
                var items = new List<LockRecord>(_items);
                var index = IndexOf(items, l);
                if (index < 0)
                    return false;
                items[index] = l;
                Publish(items.ToArray());
                return true;
            }
        }

        public void Remove(long id)
        {
            lock (_writeGate)
            {
                var items = new List<LockRecord>(_items);
                if (items.RemoveAll(e => e.Id == id) > 0)
                    Publish(items.ToArray());
            }
        }

        /// <summary>The active lock with this type, target, line and lanes, or null. Target and line are normalised first.</summary>
        public LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes)
        {
            var target = Normalizer.Norm(targetNorm);
            if (target == null)
                return null;
            var line = Normalizer.Norm(linePath);

            foreach (var e in Volatile.Read(ref _items))
            {
                if (e.Status == LockStatus.Active
                    && e.Type == t
                    && e.TargetNorm == target
                    && Normalizer.Norm(e.LinePath) == line
                    && e.Lanes == lanes)
                    return e;
            }
            return null;
        }

        private void Publish(LockRecord[] items)
        {
            Volatile.Write(ref _items, items);
        }

        private static int IndexOf(List<LockRecord> items, LockRecord l)
        {
            return items.FindIndex(e => l.Id != 0 ? e.Id == l.Id : SameIdentity(e, l));
        }

        private static bool SameIdentity(LockRecord a, LockRecord b)
        {
            return a.Type == b.Type
                && a.TargetNorm == b.TargetNorm
                && Normalizer.Norm(a.LinePath) == Normalizer.Norm(b.LinePath)
                && a.Lanes == b.Lanes;
        }
    }
}
