using System;
using System.Collections.Generic;
using System.Globalization;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// Holds the External Control answers until a timer writes them to the decision log, so a scan check never waits for the
    /// database. <see cref="Add"/> is called from many WCF threads; <see cref="Flush"/> from the timer. When the buffer is
    /// full the oldest row is dropped to make room. The rows stay in order and a row leaves the buffer only after the
    /// database has taken it.
    /// </summary>
    public sealed class DecisionLogBuffer
    {
        private const int BatchSize = 500;

        private readonly object _gate = new object();
        private readonly object _flushGate = new object();
        private readonly Queue<Item> _items = new Queue<Item>();
        private readonly int _capacity;
        private readonly ILog _log;
        private long _lastSequence;
        private bool _overflowReported;

        public DecisionLogBuffer(int capacity, ILog log)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be at least 1.");
            if (log == null)
                throw new ArgumentNullException(nameof(log));

            _capacity = capacity;
            _log = log;
        }

        /// <summary>How many rows wait for the database.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                    return _items.Count;
            }
        }

        /// <summary>
        /// Appends the row. A full buffer drops its oldest row first and logs a warning once; the next warning comes only after
        /// a flush has written rows to the database.
        /// </summary>
        public void Add(DecisionLogEntry e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));

            var warn = false;
            lock (_gate)
            {
                if (_items.Count >= _capacity)
                {
                    _items.Dequeue();
                    warn = !_overflowReported;
                    _overflowReported = true;
                }
                _items.Enqueue(new Item(++_lastSequence, e));
            }

            // Outside the lock: a log call must never hold up the WCF threads that wait to add.
            if (warn)
                _log.Warn("Decision log buffer is full (" + _capacity.ToString(CultureInfo.InvariantCulture)
                    + " rows): the oldest rows are dropped until the database takes them.");
        }

        /// <summary>
        /// Writes the rows to <paramref name="audit"/>, oldest first, in batches of at most 500, until the buffer is empty or
        /// a batch fails, and returns how many rows it wrote. A batch leaves the buffer only after <c>AddDecisions</c> accepted it,
        /// so a failed batch stays where it is. A database that cannot be reached ends the flush quietly; any other failure is
        /// logged as an error. Rows added meanwhile are kept, in order. Two flushes never overlap: the second waits for the first.
        /// </summary>
        public int Flush(IAuditRepository audit)
        {
            if (audit == null)
                throw new ArgumentNullException(nameof(audit));

            lock (_flushGate)
            {
                var written = 0;
                while (true)
                {
                    var batch = new List<DecisionLogEntry>();
                    long lastSequence;
                    lock (_gate)
                    {
                        if (_items.Count == 0)
                            return written;

                        lastSequence = 0;
                        foreach (var item in _items)
                        {
                            if (batch.Count == BatchSize)
                                break;
                            batch.Add(item.Entry);
                            lastSequence = item.Sequence;
                        }
                    }

                    try
                    {
                        audit.AddDecisions(batch);
                    }
                    catch (DbUnavailableException)
                    {
                        return written;
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Could not write " + batch.Count.ToString(CultureInfo.InvariantCulture)
                            + " decision log row(s); they stay in the buffer.", ex);
                        return written;
                    }

                    lock (_gate)
                    {
                        // The batch was the front of the buffer; an overflow meanwhile may have dropped some of its rows already.
                        while (_items.Count > 0 && _items.Peek().Sequence <= lastSequence)
                            _items.Dequeue();
                        _overflowReported = false;
                    }
                    written += batch.Count;
                }
            }
        }

        private struct Item
        {
            public Item(long sequence, DecisionLogEntry entry)
            {
                Sequence = sequence;
                Entry = entry;
            }

            public long Sequence { get; }

            public DecisionLogEntry Entry { get; }
        }
    }
}
