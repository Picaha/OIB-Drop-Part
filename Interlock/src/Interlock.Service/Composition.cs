using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Service.Health;
using Interlock.Service.Rest;

namespace Interlock.Service
{
    /// <summary>How often the background work runs. <see cref="Default"/> is the production schedule.</summary>
    internal sealed class CompositionTimings
    {
        public TimeSpan Worker = TimeSpan.FromMilliseconds(500);
        public TimeSpan CacheRefresh = TimeSpan.FromSeconds(30);
        public TimeSpan Watchdog = TimeSpan.FromSeconds(60);
        public TimeSpan DecisionFlush = TimeSpan.FromSeconds(5);
        public TimeSpan Health = TimeSpan.FromSeconds(30);
        public TimeSpan StationRefresh = TimeSpan.FromMinutes(5);
        public TimeSpan Subscriber = TimeSpan.FromSeconds(30);
        public TimeSpan StopBudget = TimeSpan.FromSeconds(25);

        /// <summary>The least time a stop step gets even when the budget is used up, so the later steps (the flush) still run.</summary>
        public TimeSpan StopStepFloor = TimeSpan.FromMilliseconds(500);

        public static CompositionTimings Default
        {
            get { return new CompositionTimings(); }
        }
    }

    /// <summary>
    /// The composition root: builds every object of the service, starts it in a fixed order and stops it in the reverse one.
    /// <para>
    /// Start: log, repositories and clients, the spool seeded into cache and queue (so spooled Setup Center locks are enforced
    /// even when the database is down), one cache reload (a failure leaves <c>DbHealthy</c> false), the startup reconcile when the
    /// database answered (otherwise it runs after the first good reload), External Control, REST, the Setup Center event
    /// subscription, then the timers. If External Control or REST cannot listen, start-up fails: what was opened is closed and the
    /// exception reaches the caller. An interlock that cannot answer Setup Center must not look as if it were running.
    /// </para>
    /// <para>Stop (idempotent, about 25 s at most): timers, subscription, REST, External Control, escalation thread, a last
    /// decision-log flush, then everything disposable.</para>
    /// </summary>
    public sealed class Composition : IDisposable
    {
        private const int DecisionBufferCapacity = 10000;
        private const int EscalationCapacity = 1000;
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

        private readonly ICompositionDeps _deps;
        private readonly CompositionTimings _timings;
        private readonly object _gate = new object();
        private readonly List<GuardedTimer> _timers = new List<GuardedTimer>();
        private readonly List<object> _owned = new List<object>();
        private bool _started;
        private bool _stopped;
        private volatile bool _reconciled;

        private ILog _log;
        private IClock _clock;
        private CompositionRepositories _repos;
        private ILockSpool _spool;
        private CompositionClients _clients;
        private LockCache _cache;
        private ActionQueue _queue;
        private AlarmBoard _alarms;
        private DecisionLogBuffer _decisions;
        private IRefreshableStationDirectory _stations;
        private LockService _lockService;
        private EnforcementWorker _worker;
        private CacheRefresher _refresher;
        private ConveyorWatchdog _watchdog;
        private HealthMonitor _health;
        private IListener _externalControl;
        private IListener _rest;
        private EscalationQueue _escalation;
        private ILockEventSource _subscriber;

        public Composition(ServiceSettings s)
            : this(new ProductionDeps(s, false))
        {
        }

        public Composition(ICompositionDeps deps)
            : this(deps, CompositionTimings.Default)
        {
        }

        internal Composition(ICompositionDeps deps, CompositionTimings timings)
        {
            if (deps == null) throw new ArgumentNullException(nameof(deps));
            if (timings == null) throw new ArgumentNullException(nameof(timings));
            _deps = deps;
            _timings = timings;
        }

        // For tests and diagnostics.
        internal LockCache Cache { get { return _cache; } }
        internal ActionQueue Queue { get { return _queue; } }
        internal CacheRefresher Refresher { get { return _refresher; } }
        internal HealthMonitor Health { get { return _health; } }
        internal DecisionLogBuffer Decisions { get { return _decisions; } }
        internal bool Reconciled { get { return _reconciled; } }
        internal int TimerCount { get { lock (_gate) return _timers.Count; } }

        internal Action<IList<PuLockReport>> OnSetupCenterReports
        {
            get { return _escalation == null ? (Action<IList<PuLockReport>>)null : _escalation.Add; }
        }

        /// <summary>Builds and starts everything. Throws when the service cannot run (configuration, listeners); then nothing is left open.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                    throw new InvalidOperationException("The composition was started already; create a new one.");
                _started = true;
            }

            try
            {
                StartCore();
            }
            catch (OperationCanceledException) when (IsStopping)
            {
                Info("Start-up cancelled: the service was stopped meanwhile.");
                throw;
            }
            catch (Exception ex)
            {
                if (_log != null)
                    _log.Error("Interlock start-up failed; stopping what was started.", ex);
                Stop();
                throw;
            }
        }

        /// <summary>True once <see cref="Stop"/> was called.</summary>
        public bool IsStopping
        {
            get { lock (_gate) return _stopped; }
        }

        private void StartCore()
        {
            _clock = _deps.Clock;
            _log = _deps.CreateLog();
            Own(_log);
            _log.Info("Interlock starting.");

            _repos = _deps.CreateRepositories(_log);
            _spool = _deps.CreateSpool(_log);
            Own(_spool);
            _clients = _deps.CreateClients(_log);
            Own(_clients.SetupCenter);
            Own(_clients.LineControl);
            Own(_clients.Monitoring);

            _cache = new LockCache();
            _queue = new ActionQueue();
            _alarms = new AlarmBoard();
            _decisions = new DecisionLogBuffer(DecisionBufferCapacity, _log);
            _stations = _deps.CreateStations(_clients.Monitoring, _clients.SetupCenter, _log);
            Own(_stations);

            var sessions = new SessionStore(_clock, SessionLifetime);
            var auth = new AuthService(_repos.Users, sessions, _clock);
            _lockService = new LockService(_repos.Locks, _repos.Audit, _cache, _queue, _stations, _spool, _clock, _log);
            _worker = new EnforcementWorker(_queue, _repos.Locks, _repos.Audit, _clients.SetupCenter, _clients.LineControl, _cache, _alarms, _clock, _log);
            _refresher = new CacheRefresher(_repos.Locks, _spool, _cache, _queue, _clock, _log);
            _watchdog = new ConveyorWatchdog(_cache, _clients.LineControl, _queue, _clock, _log);
            _health = new HealthMonitor(_clients.SetupCenter, _clients.LineControl, _clients.Monitoring, _repos.Locks, _refresher,
                _cache, _queue, _alarms, _clock, _log);
            var api = new InterlockRestApi(_lockService, auth, _repos.Audit, _stations, _health, new LoginThrottle(_clock), _log);

            SeedFromSpool();
            RefreshCache();
            if (!_refresher.DbHealthy)
                _log.Warn("Database not reachable at start-up: answering from the spool only; the reload retries every "
                    + Seconds(_timings.CacheRefresh) + ".");

            _externalControl = _deps.CreateExternalControl(_cache, _decisions, _clock, _log);
            Own(_externalControl);
            _externalControl.Open();
            ClosedIfStopping(_externalControl);

            _rest = _deps.CreateRest(api, _log);
            Own(_rest);
            _rest.Open();
            ClosedIfStopping(_rest);

            _escalation = new EscalationQueue(_lockService.OnLockStateChanged, EscalationCapacity, _log);
            _escalation.Start();
            _subscriber = _deps.CreateSubscriber(_escalation.Add, _log);
            Own(_subscriber);
            _subscriber.Start();

            lock (_gate)
            {
                if (_stopped)
                    throw new OperationCanceledException("The service was stopped during start-up.");
                AddTimer("enforcement worker", _timings.Worker, _timings.Worker, () => _worker.RunDue());
                AddTimer("cache refresh", _timings.CacheRefresh, _timings.CacheRefresh, RefreshCache);
                AddTimer("conveyor watchdog", _timings.Watchdog, _timings.Watchdog, () => _watchdog.Tick());
                AddTimer("decision flush", _timings.DecisionFlush, _timings.DecisionFlush, () => _decisions.Flush(_repos.Audit));
                AddTimer("health", TimeSpan.Zero, _timings.Health, () => _health.Tick());
                AddTimer("station directory", TimeSpan.Zero, _timings.StationRefresh, () => _stations.Refresh());
                AddTimer("event subscription", _timings.Subscriber, _timings.Subscriber, () => _subscriber.Tick());
            }

            _log.Info("Interlock started: " + _cache.Snapshot.Count.ToString(CultureInfo.InvariantCulture) + " lock(s) cached, "
                + _queue.Count.ToString(CultureInfo.InvariantCulture) + " job(s) queued, database " + (_refresher.DbHealthy ? "reachable" : "NOT reachable") + ".");
        }

        /// <summary>
        /// The spooled Setup Center locks (stored while the database was down) go into the cache with an Apply job each, under the
        /// write gate, before anything else reads the cache. The first good reload stores them and replaces these jobs.
        /// </summary>
        private void SeedFromSpool()
        {
            try
            {
                var now = _clock.UtcNow;
                int seeded = 0;
                lock (_cache.WriteGate)
                {
                    foreach (var l in _spool.ReadAll())
                    {
                        _cache.Upsert(l.Clone());
                        _queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = JobKind.Apply, NotBeforeUtc = now });
                        seeded++;
                    }
                }
                if (seeded > 0)
                    _log.Warn("Seeded " + seeded.ToString(CultureInfo.InvariantCulture) + " spooled lock(s) into the cache and queue.");
            }
            catch (Exception ex)
            {
                _log.Error("Could not read the lock spool at start-up.", ex);
            }
        }

        /// <summary>One cache reload, then the startup reconcile once the database has answered for the first time.</summary>
        internal void RefreshCache()
        {
            _refresher.Tick();
            if (_reconciled || !_refresher.DbHealthy)
                return;

            try
            {
                var queued = Reconciler.QueueOnStartup(_repos.Locks, _cache, _queue, _clock);
                _reconciled = true;
                _log.Info("Startup reconcile queued " + queued.ToString(CultureInfo.InvariantCulture) + " job(s).");
            }
            catch (DbUnavailableException ex)
            {
                _log.Warn("Startup reconcile skipped, database not reachable (" + ex.GetType().Name + "); it runs after the next good reload.");
            }
            catch (Exception ex)
            {
                _log.Error("Startup reconcile failed; it runs again after the next good reload.", ex);
            }
        }

        /// <summary>
        /// Stops everything in order within one budget (about 25 s): each step gets what is left, at least
        /// <see cref="CompositionTimings.StopStepFloor"/>. A step that runs over is abandoned (logged) and the next one runs. Safe to
        /// call more than once, after a failed start and while <see cref="Start"/> still runs on another thread.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped)
                    return;
                _stopped = true;
            }

            var budget = Stopwatch.StartNew();
            Func<TimeSpan> left = () =>
            {
                var remaining = _timings.StopBudget - budget.Elapsed;
                return remaining > _timings.StopStepFloor ? remaining : _timings.StopStepFloor;
            };

            Step("Stopping timers", () =>
            {
                GuardedTimer[] timers;
                lock (_gate)
                    timers = _timers.ToArray();
                foreach (var t in timers)
                    t.Stop(left());
            });
            var subscriber = _subscriber;
            if (subscriber != null)
                Bounded("Unsubscribing from Setup Center events", () => subscriber.Stop(), left());
            var rest = _rest;
            if (rest != null)
                Step("Closing REST", () => rest.Close(left()));
            var externalControl = _externalControl;
            if (externalControl != null)
                Step("Closing External Control", () => externalControl.Close(left()));
            var escalation = _escalation;
            if (escalation != null)
                Step("Stopping the escalation thread", () => escalation.Stop(left()));
            var decisions = _decisions;
            var repos = _repos;
            if (decisions != null && repos != null)
            {
                Bounded("Flushing the decision log", () =>
                {
                    decisions.Flush(repos.Audit);
                    if (decisions.Count > 0)
                        Warn(decisions.Count.ToString(CultureInfo.InvariantCulture) + " decision log row(s) could not be written and are lost.");
                }, left());
            }

            Info("Interlock stopped (" + budget.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s).");
            DisposeOwned();
        }

        public void Dispose()
        {
            Stop();
        }

        private void AddTimer(string name, TimeSpan due, TimeSpan period, Action action)
        {
            _timers.Add(new GuardedTimer(name, due, period, action, _log));
        }

        /// <summary>Takes ownership; when the service is stopping already, the object is disposed and start-up ends.</summary>
        private void Own(object o)
        {
            if (o == null)
                return;
            lock (_gate)
            {
                if (!_stopped)
                {
                    if (!_owned.Contains(o))
                        _owned.Add(o);
                    return;
                }
            }
            var d = o as IDisposable;
            if (d != null && !ReferenceEquals(o, _log))
                d.Dispose();
            throw new OperationCanceledException("The service was stopped during start-up.");
        }

        /// <summary>A listener that opened after <see cref="Stop"/> had already closed the listeners is closed here.</summary>
        private void ClosedIfStopping(IListener listener)
        {
            if (!IsStopping)
                return;
            listener.Close();
            throw new OperationCanceledException("The service was stopped during start-up.");
        }

        private void DisposeOwned()
        {
            object[] owned;
            lock (_gate)
            {
                owned = _owned.ToArray();
                _owned.Clear();
            }

            // Newest first; the log goes last so the other disposals can still log.
            for (var i = owned.Length - 1; i >= 0; i--)
            {
                var d = owned[i] as IDisposable;
                if (d == null)
                    continue;
                try
                {
                    d.Dispose();
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(d, _log) && _log != null)
                        _log.Warn("Disposing " + d.GetType().Name + " failed: " + ex.Message);
                }
            }
        }

        /// <summary>Runs the step on its own thread and waits at most <paramref name="timeout"/>; a step that runs over is abandoned.</summary>
        private void Bounded(string what, Action action, TimeSpan timeout)
        {
            Info(what + ".");
            var worker = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    if (_log != null)
                        _log.Error(what + " failed.", ex);
                }
            }) { IsBackground = true, Name = "Interlock stop: " + what };
            worker.Start();
            if (!worker.Join(timeout))
                Warn(what + " did not finish within " + timeout.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s and was abandoned.");
        }

        private void Step(string what, Action action)
        {
            Info(what + ".");
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (_log != null)
                    _log.Error(what + " failed.", ex);
            }
        }

        private void Info(string m)
        {
            if (_log != null)
                _log.Info(m);
        }

        private void Warn(string m)
        {
            if (_log != null)
                _log.Warn(m);
        }

        private static string Seconds(TimeSpan t)
        {
            return t.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        }
    }
}
