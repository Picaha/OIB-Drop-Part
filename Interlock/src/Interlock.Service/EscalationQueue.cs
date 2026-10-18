using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service
{
    /// <summary>
    /// Takes Setup Center lock reports off the OIB callback thread. <see cref="Add"/> only queues (bounded; a full queue drops
    /// its oldest notice with a warning) and never throws or blocks; one background thread hands each notice to the handler
    /// (<c>LockService.OnLockStateChanged</c>, which makes database calls with the write gate held).
    /// </summary>
    internal sealed class EscalationQueue : IDisposable
    {
        private readonly BlockingCollection<IList<PuLockReport>> _items;
        private readonly Action<IList<PuLockReport>> _handler;
        private readonly ILog _log;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly object _gate = new object();
        private Thread _thread;
        private bool _stopped;

        public EscalationQueue(Action<IList<PuLockReport>> handler, int capacity, ILog log)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (log == null) throw new ArgumentNullException(nameof(log));
            _handler = handler;
            _log = log;
            _items = new BlockingCollection<IList<PuLockReport>>(capacity);
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null || _stopped)
                    return;
                _thread = new Thread(Consume) { IsBackground = true, Name = "Interlock escalation" };
                _thread.Start();
            }
        }

        /// <summary>Queues the reports; runs on the OIB thread, so it never throws and never waits.</summary>
        public void Add(IList<PuLockReport> reports)
        {
            if (reports == null || reports.Count == 0)
                return;
            try
            {
                while (!_items.TryAdd(reports))
                {
                    IList<PuLockReport> dropped;
                    if (_items.TryTake(out dropped))
                        _log.Warn("Escalation queue full: dropped the oldest Setup Center notice (" + Uids(dropped) + ").");
                }
            }
            catch (InvalidOperationException)
            {
                _log.Warn("Setup Center notice arrived while the service was stopping and was not handled (" + Uids(reports) + ").");
            }
            catch (Exception ex)
            {
                try
                {
                    _log.Error("Could not queue a Setup Center notice.", ex);
                }
                catch (Exception)
                {
                    // Never into the OIB thread.
                }
            }
        }

        /// <summary>Stops taking notices, lets the thread finish the queued ones for up to <paramref name="timeout"/>, then gives up on the rest.</summary>
        public void Stop(TimeSpan timeout)
        {
            Thread thread;
            lock (_gate)
            {
                if (_stopped)
                    return;
                _stopped = true;
                thread = _thread;
            }

            _items.CompleteAdding();
            if (thread == null)
                return;
            if (!thread.Join(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout))
            {
                _cancel.Cancel();
                thread.Join(TimeSpan.FromSeconds(1));
                _log.Warn("Escalation thread did not finish in time; " + _items.Count.ToString(CultureInfo.InvariantCulture)
                    + " Setup Center notice(s) were not handled.");
            }
        }

        public void Dispose()
        {
            Stop(TimeSpan.FromSeconds(5));
        }

        private void Consume()
        {
            try
            {
                foreach (var reports in _items.GetConsumingEnumerable(_cancel.Token))
                {
                    try
                    {
                        _handler(reports);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Handling a Setup Center notice failed.", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop gave up waiting.
            }
            catch (Exception ex)
            {
                _log.Error("The escalation thread stopped unexpectedly.", ex);
            }
        }

        private static string Uids(IList<PuLockReport> reports)
        {
            var uids = new List<string>();
            foreach (var r in reports)
            {
                if (r != null)
                    uids.Add(r.Uid);
            }
            return "UID " + string.Join(", ", uids);
        }
    }
}
