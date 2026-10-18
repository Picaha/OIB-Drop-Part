using System;
using System.Threading;
using Interlock.Core.Ports;

namespace Interlock.Service
{
    /// <summary>
    /// Runs an action on a thread-pool timer. A run that finds the previous one still active is skipped, so the action never
    /// overlaps itself. Every exception is caught and logged. <see cref="Stop"/> waits for a run in flight.
    /// </summary>
    internal sealed class GuardedTimer
    {
        private readonly string _name;
        private readonly Action _action;
        private readonly ILog _log;
        private readonly Timer _timer;
        private readonly object _gate = new object();
        private int _running;
        private volatile bool _stopped;
        private bool _disposed;

        /// <param name="dueTime">The first run; <see cref="TimeSpan.Zero"/> runs right away on the thread pool.</param>
        public GuardedTimer(string name, TimeSpan dueTime, TimeSpan period, Action action, ILog log)
        {
            _name = name;
            _action = action;
            _log = log;
            _timer = new Timer(_ => Run(), null, dueTime, period);
        }

        public string Name
        {
            get { return _name; }
        }

        /// <summary>Stops the timer and waits up to <paramref name="timeout"/> for a run in flight. False when it was still running.</summary>
        public bool Stop(TimeSpan timeout)
        {
            lock (_gate)
            {
                _stopped = true;
                if (_disposed)
                    return true;
                _disposed = true;
            }

            // Dispose(WaitHandle) signals once every callback that already started has returned. The event is closed only after
            // it was signalled: a run that outlives the timeout still signals it later.
            var done = new ManualResetEvent(false);
            if (!_timer.Dispose(done))
            {
                done.Close();
                return true;
            }
            var finished = done.WaitOne(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
            if (finished)
                done.Close();
            else
                _log.Warn("Timer '" + _name + "' was still running when the service stopped.");
            return finished;
        }

        private void Run()
        {
            if (_stopped || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return;
            try
            {
                if (!_stopped)
                    _action();
            }
            catch (Exception ex)
            {
                try
                {
                    _log.Error("Timer '" + _name + "' failed.", ex);
                }
                catch (Exception)
                {
                    // Nothing must leave a thread-pool callback.
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }
}
