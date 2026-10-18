using System;
using System.Collections.Generic;
using System.Linq;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// The in-process queue of OIB work. External Control callbacks and the REST API enqueue; one worker
    /// takes what is due. Safe to use from several threads.
    /// </summary>
    public sealed class ActionQueue
    {
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private long _sequence;

        public int Count
        {
            get
            {
                lock (_gate)
                    return _entries.Count;
            }
        }

        /// <summary>Queues the job. Its due time is read now; to change it, take the job out and enqueue it again.</summary>
        public void Enqueue(EnforcementJob j)
        {
            if (j == null)
                throw new ArgumentNullException(nameof(j));
            if (j.Lock == null)
                throw new ArgumentException("A job needs a lock.", nameof(j));

            lock (_gate)
                _entries.Add(new Entry(j, j.NotBeforeUtc, ++_sequence));
        }

        /// <summary>Removes and returns the jobs due at <paramref name="nowUtc"/>, ordered by due time and then by insertion.</summary>
        public IList<EnforcementJob> TakeDue(DateTime nowUtc)
        {
            lock (_gate)
            {
                var due = new List<Entry>();
                var waiting = new List<Entry>();
                foreach (var e in _entries)
                {
                    if (e.NotBeforeUtc <= nowUtc)
                        due.Add(e);
                    else
                        waiting.Add(e);
                }

                if (due.Count == 0)
                    return new List<EnforcementJob>();

                _entries.Clear();
                _entries.AddRange(waiting);
                return due.OrderBy(e => e.NotBeforeUtc).ThenBy(e => e.Sequence).Select(e => e.Job).ToList();
            }
        }

        /// <summary>Drops every queued job of the lock. Id 0 (a spooled lock, not stored yet) is never cancelled.</summary>
        public void Cancel(long lockId)
        {
            if (lockId == 0)
                return;

            lock (_gate)
                _entries.RemoveAll(e => e.Job.Lock.Id == lockId);
        }

        private sealed class Entry
        {
            public Entry(EnforcementJob job, DateTime notBeforeUtc, long sequence)
            {
                Job = job;
                NotBeforeUtc = notBeforeUtc;
                Sequence = sequence;
            }

            public EnforcementJob Job { get; }
            public DateTime NotBeforeUtc { get; }
            public long Sequence { get; }
        }
    }
}
