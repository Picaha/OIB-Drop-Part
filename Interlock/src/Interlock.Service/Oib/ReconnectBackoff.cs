using System;
using Interlock.Core.Ports;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// How long to leave an OIB service alone after its connection failed. After failure 1, 2, 3, ... the wait is 5 s, 10 s,
    /// 20 s, ... and never more than 5 min. <see cref="Succeeded"/> starts the schedule over. Thread-safe.
    /// </summary>
    public sealed class ReconnectBackoff
    {
        internal static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

        private readonly IClock _clock;
        private readonly object _gate = new object();
        private int _failures;
        private DateTime _retryAtUtc = DateTime.MinValue;

        public ReconnectBackoff(IClock clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));
            _clock = clock;
        }

        /// <summary>True when no wait is running: either nothing failed, or the wait after the latest failure is over.</summary>
        public bool CanTry
        {
            get
            {
                lock (_gate)
                    return _clock.UtcNow >= _retryAtUtc;
            }
        }

        /// <summary>Records a failed connection and starts the next wait.</summary>
        public void Failed()
        {
            lock (_gate)
            {
                if (_failures < int.MaxValue)
                    _failures++;
                _retryAtUtc = _clock.UtcNow + WaitAfter(_failures);
            }
        }

        /// <summary>Records a working connection: the next failure waits 5 s again.</summary>
        public void Succeeded()
        {
            lock (_gate)
            {
                _failures = 0;
                _retryAtUtc = DateTime.MinValue;
            }
        }

        private static TimeSpan WaitAfter(int failures)
        {
            // 5 s * 2^(n-1); 2^7 * 5 s already exceeds the cap, so larger exponents are not computed.
            if (failures > 7)
                return MaxWait;
            var wait = TimeSpan.FromTicks(FirstWait.Ticks << (failures - 1));
            return wait < MaxWait ? wait : MaxWait;
        }
    }
}
