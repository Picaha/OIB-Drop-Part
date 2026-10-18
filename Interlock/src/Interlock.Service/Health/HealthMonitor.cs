using System;
using System.Linq;
using System.Text.RegularExpressions;
using Interlock.Core.Api;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service.Health
{
    /// <summary>
    /// The health of the service for <c>GET /health</c> and the status bar. <see cref="Tick"/> (every 30 s, from the service's
    /// timer) does the slow work: the OIB calls, which may take seconds or throw during a reconnect wait, and the count of
    /// FAILED locks. <see cref="Snapshot"/> never calls OIB and never blocks: it returns what the last tick found, plus the
    /// values the service holds in memory (database state, queue, cache, alarms).
    /// </summary>
    public sealed class HealthMonitor
    {
        private const int AlarmCount = 20;

        private readonly ISetupCenterClient _setupCenter;
        private readonly ILineControlClient _lineControl;
        private readonly IMonitoringClient _monitoring;
        private readonly ILockRepository _locks;
        private readonly CacheRefresher _refresher;
        private readonly LockCache _cache;
        private readonly ActionQueue _queue;
        private readonly AlarmBoard _alarms;
        private readonly IClock _clock;
        private readonly ILog _log;
        private volatile Checked _last = new Checked();

        public HealthMonitor(ISetupCenterClient setupCenter, ILineControlClient lineControl, IMonitoringClient monitoring,
            ILockRepository locks, CacheRefresher refresher, LockCache cache, ActionQueue queue, AlarmBoard alarms,
            IClock clock, ILog log)
        {
            if (setupCenter == null) throw new ArgumentNullException(nameof(setupCenter));
            if (lineControl == null) throw new ArgumentNullException(nameof(lineControl));
            if (monitoring == null) throw new ArgumentNullException(nameof(monitoring));
            if (locks == null) throw new ArgumentNullException(nameof(locks));
            if (refresher == null) throw new ArgumentNullException(nameof(refresher));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (alarms == null) throw new ArgumentNullException(nameof(alarms));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _setupCenter = setupCenter;
            _lineControl = lineControl;
            _monitoring = monitoring;
            _locks = locks;
            _refresher = refresher;
            _cache = cache;
            _queue = queue;
            _alarms = alarms;
            _clock = clock;
            _log = log;
        }

        /// <summary>
        /// Checks OIB and counts the FAILED locks, and keeps the result for <see cref="Snapshot"/>. Each call is isolated: one that
        /// throws (<c>OibCallException</c> while OIB is unreachable, or anything else) reports false, or unknown (null) for a
        /// license, and the others still run. A license call is never taken as a sign of connectivity. Never throws.
        /// </summary>
        public void Tick()
        {
            var previous = _last;
            var next = new Checked
            {
                SetupCenterConnected = Try(() => _setupCenter.IsConnected(), false, "Setup Center connection check"),
                SetupCenterLicense = Try(() => (bool?)_setupCenter.HasLicense(), null, "Setup Center license check"),
                LineControlLicense = Try(() => (bool?)_lineControl.HasLicense(), null, "Line Control license check"),
                MonitoringOk = Try(() => _monitoring.GetStations() != null, false, "Monitoring check"),
            };

            // A database that is down keeps the previous count; the dbOk flag tells the story.
            next.FailedLocks = Try(
                () => _locks.Query(new LockQuery { EnforceState = EnforceState.Failed }).Count,
                previous.FailedLocks,
                "FAILED lock count");

            _last = next;
        }

        /// <summary>The last tick's OIB results with the current in-memory state. Never calls OIB. Before the first tick OIB reads as down.</summary>
        public HealthDto Snapshot()
        {
            var last = _last;
            var failures = _refresher.ConsecutiveFailures;
            return new HealthDto
            {
                setupCenterConnected = last.SetupCenterConnected,
                setupCenterLicense = last.SetupCenterLicense,
                lineControlLicense = last.LineControlLicense,
                monitoringOk = last.MonitoringOk,
                dbOk = _refresher.DbHealthy,
                queueLength = _queue.Count,
                cacheLoadedAt = DtoMapper.Iso(_cache.LoadedAtUtc),
                lockCount = _cache.Snapshot.Count,
                failedLocks = last.FailedLocks,
                cacheRefreshFailures = failures,
                cacheRefreshError = failures == 0 ? null : PublicError(_refresher.LastError),
                alarms = _alarms.Recent(AlarmCount).Select(PublicAlarm).ToArray()
            };
        }

        private const string GenericError = "Database error";
        private static readonly Regex TypeName = new Regex(@"^[A-Za-z_][A-Za-z0-9_.]{0,99}$", RegexOptions.Compiled);

        // /health needs no login, so the error is only the exception type name (the text before the first ':'), never the
        // message: that can carry hosts, ports or user names. The full text stays in the log.
        private static string PublicError(string lastError)
        {
            if (lastError == null)
                return GenericError;
            var colon = lastError.IndexOf(':');
            var name = (colon < 0 ? lastError : lastError.Substring(0, colon)).Trim();
            return TypeName.IsMatch(name) ? name : GenericError;
        }

        // The detail of a FAILED alarm is an OIB error message (it can name hosts and endpoints); /health keeps the lock only.
        private static string PublicAlarm(string alarm)
        {
            const string marker = " FAILED: ";
            var at = alarm.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? alarm : alarm.Substring(0, at) + " FAILED";
        }

        private T Try<T>(Func<T> call, T fallback, string what)
        {
            try
            {
                return call();
            }
            catch (Exception ex)
            {
                _log.Warn("Health: " + what + " failed: " + ex.GetType().Name + ": " + ex.Message);
                return fallback;
            }
        }

        private sealed class Checked
        {
            public bool SetupCenterConnected;
            public bool? SetupCenterLicense;
            public bool? LineControlLicense;
            public bool MonitoringOk;
            public int FailedLocks;
        }
    }
}
