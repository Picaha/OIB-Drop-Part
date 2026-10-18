using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Service;
using Interlock.Service.Rest;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public sealed class CompositionTests
    {
        private Deps _deps;
        private Composition _composition;

        [SetUp]
        public void SetUp()
        {
            _deps = new Deps();
        }

        [TearDown]
        public void TearDown()
        {
            if (_composition != null)
                _composition.Dispose();
        }

        private static CompositionTimings Slow()
        {
            var hour = TimeSpan.FromHours(1);
            return new CompositionTimings
            {
                Worker = hour, CacheRefresh = hour, Watchdog = hour, DecisionFlush = hour, Health = hour, StationRefresh = hour, Subscriber = hour,
                StopBudget = TimeSpan.FromSeconds(10)
            };
        }

        private Composition Start(CompositionTimings timings = null)
        {
            _composition = new Composition(_deps, timings ?? Slow());
            _composition.Start();
            return _composition;
        }

        private static LockRecord Pu(string uid, long id = 0, EnforceState state = EnforceState.Pending)
        {
            return new LockRecord
            {
                Id = id, Type = LockType.Pu, Target = uid, TargetNorm = Normalizer.Norm(uid), Reason = "r", Source = LockSource.SetupCenter,
                CreatedBy = "SetupCenter", CreatedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Status = LockStatus.Active,
                EnforceState = state
            };
        }

        private static void WaitUntil(Func<bool> condition, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(10))
                    Assert.Fail("Timed out waiting for " + what);
                Thread.Sleep(20);
            }
        }

        private int IndexOf(string e)
        {
            var events = _deps.Events.ToList();
            var i = events.IndexOf(e);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), "event '" + e + "' missing; events: " + string.Join(", ", events));
            return i;
        }

        private void AssertOrder(params string[] events)
        {
            for (var i = 1; i < events.Length; i++)
                Assert.That(IndexOf(events[i - 1]), Is.LessThan(IndexOf(events[i])), events[i - 1] + " before " + events[i]);
        }

        // ---- start ----

        [Test]
        public void Start_runs_in_the_documented_order()
        {
            Start();

            AssertOrder("log", "repositories", "spool", "clients", "stations", "spool.ReadAll", "db.LoadActive",
                "ec.Open", "rest.Open", "subscriber.Start");
            Assert.That(_composition.TimerCount, Is.EqualTo(7));
            Assert.That(_composition.Refresher.DbHealthy, Is.True);
        }

        [Test]
        public void A_reachable_database_is_reconciled_at_start_up_once()
        {
            _deps.Locks.InsertActive(Pu("U1", state: EnforceState.Failed));
            Start();

            Assert.That(_composition.Reconciled, Is.True);
            Assert.That(_deps.Count("db.LoadPendingReleases"), Is.EqualTo(1));
            Assert.That(_composition.Queue.Any(j => j.Kind == JobKind.Apply && j.Lock.Target == "U1"), Is.True);

            _composition.RefreshCache();
            Assert.That(_deps.Count("db.LoadPendingReleases"), Is.EqualTo(1));
        }

        [Test]
        public void Spooled_locks_are_cached_and_queued_when_the_database_is_down()
        {
            _deps.Spool.Append(Pu("SPOOLED-1"));
            _deps.Locks.Down = true;

            Start();

            Assert.That(_composition.Refresher.DbHealthy, Is.False);
            Assert.That(_composition.Cache.Snapshot.Any(l => l.Id == 0 && l.TargetNorm == "SPOOLED-1"), Is.True);
            Assert.That(_composition.Queue.Any(j => j.Kind == JobKind.Apply && j.Lock.Id == 0 && j.Lock.TargetNorm == "SPOOLED-1"), Is.True);
            Assert.That(_composition.Reconciled, Is.False);
            Assert.That(_deps.Ec.Opened, Is.True, "External Control answers from the cache while the DB is down");
            Assert.That(_deps.Rest.Opened, Is.True);
        }

        [Test]
        public void Spool_seeding_happens_under_the_write_gate_before_the_first_reload()
        {
            _deps.Spool.Append(Pu("SPOOLED-1"));
            _deps.Spool.GateOwner = () => _composition.Cache;
            Start();

            Assert.That(_deps.Spool.ReadAllUnderGate.First(), Is.True);
            AssertOrder("spool.ReadAll", "db.LoadActive");
        }

        [Test]
        public void Reconcile_runs_once_after_the_database_comes_back()
        {
            _deps.Locks.InsertActive(Pu("U1", state: EnforceState.Pending));
            _deps.Locks.Down = true;
            Start();
            Assert.That(_composition.Reconciled, Is.False);

            _composition.RefreshCache(); // still down
            Assert.That(_composition.Reconciled, Is.False);
            Assert.That(_deps.Count("db.LoadPendingReleases"), Is.EqualTo(0));

            _deps.Locks.Down = false;
            _composition.RefreshCache();
            Assert.That(_composition.Reconciled, Is.True);
            Assert.That(_deps.Count("db.LoadPendingReleases"), Is.EqualTo(1));
            Assert.That(_composition.Queue.Any(j => j.Kind == JobKind.Apply && j.Lock.Target == "U1"), Is.True);

            _composition.RefreshCache();
            _composition.RefreshCache();
            Assert.That(_deps.Count("db.LoadPendingReleases"), Is.EqualTo(1));
        }

        [Test]
        public void A_spooled_lock_is_stored_by_the_first_good_reload()
        {
            _deps.Spool.Append(Pu("SPOOLED-1"));
            _deps.Locks.Down = true;
            Start();

            _deps.Locks.Down = false;
            _composition.RefreshCache();

            Assert.That(_deps.Spool.ReadAll(), Is.Empty);
            Assert.That(_composition.Cache.Snapshot.Single(l => l.TargetNorm == "SPOOLED-1").Id, Is.GreaterThan(0));
            Assert.That(_composition.Queue.Any(j => j.Lock.Id == 0), Is.False, "the Id-0 job was replaced by one for the stored lock");
        }

        [Test]
        public void A_failing_external_control_aborts_start_up_and_opens_nothing_else()
        {
            _deps.Ec.FailOpen = true;
            _composition = new Composition(_deps, Slow());

            Assert.Throws<InvalidOperationException>(() => _composition.Start());

            Assert.That(_deps.Rest, Is.Not.Null.And.Property("Created").False);
            Assert.That(_deps.Events, Has.No.Member("rest.Open"));
            Assert.That(_deps.Events, Has.No.Member("subscriber.Start"));
            Assert.That(_composition.TimerCount, Is.EqualTo(0));
            Assert.That(_deps.Ec.Disposed, Is.True);
            Assert.That(_deps.Sc.Disposed, Is.True, "clients are disposed");
            Assert.That(_deps.Log.Errors.Any(e => e.Contains("start-up failed")), Is.True);
        }

        [Test]
        public void A_failing_rest_host_aborts_start_up_and_closes_external_control()
        {
            _deps.Rest.FailOpen = true;
            _composition = new Composition(_deps, Slow());

            Assert.Throws<InvalidOperationException>(() => _composition.Start());

            AssertOrder("ec.Open", "rest.Open", "ec.Close");
            Assert.That(_deps.Ec.Opened, Is.False);
            Assert.That(_deps.Events, Has.No.Member("subscriber.Start"));
            Assert.That(_composition.TimerCount, Is.EqualTo(0));
            Assert.DoesNotThrow(() => _composition.Stop());
            Assert.DoesNotThrow(() => _composition.Dispose());
        }

        [Test]
        public void Start_can_only_run_once()
        {
            Start();
            Assert.Throws<InvalidOperationException>(() => _composition.Start());
        }

        // ---- stop ----

        [Test]
        public void Stop_runs_in_the_documented_order_and_only_once()
        {
            Start();
            var before = _deps.Events.Count;

            _composition.Stop();

            AssertOrder("subscriber.Stop", "rest.Close", "ec.Close", "dispose.Subscriber", "dispose.Clients");
            Assert.That(IndexOf("subscriber.Stop"), Is.GreaterThanOrEqualTo(before));
            Assert.That(_deps.Log.Infos.Last(), Is.EqualTo("Interlock stopped."));
            Assert.That(_deps.Log.Disposed, Is.True);

            var after = _deps.Events.Count;
            _composition.Stop();
            _composition.Dispose();
            Assert.That(_deps.Events.Count, Is.EqualTo(after));
        }

        [Test]
        public void Stop_writes_the_buffered_decisions()
        {
            Start();
            _composition.Decisions.Add(new DecisionLogEntry { Method = "Ping", Result = "OK", CreatedAtUtc = DateTime.UtcNow });

            _composition.Stop();

            Assert.That(_deps.Audit.Decisions.Count, Is.EqualTo(1));
        }

        [Test]
        public void Stop_without_start_is_quiet()
        {
            _composition = new Composition(_deps, Slow());
            Assert.DoesNotThrow(() => _composition.Stop());
            Assert.That(_deps.Events, Is.Empty);
        }

        // ---- escalation and timers ----

        [Test]
        public void Setup_center_reports_are_handled_off_the_callback_thread()
        {
            Start();
            var callbackThread = Thread.CurrentThread.ManagedThreadId;
            var handlerThread = 0;
            _deps.Locks.OnCall = m =>
            {
                if (m == "GetLatestReleaseUtc")
                    handlerThread = Thread.CurrentThread.ManagedThreadId;
                _deps.Events.Enqueue("db." + m);
            };

            _deps.OnReports(new List<PuLockReport>
            {
                new PuLockReport { Uid = "WRONG-1", LockInfos = new List<LockInfoRef> { new LockInfoRef { Reason = 1, Source = 1, DateUtc = DateTime.UtcNow } } }
            });

            WaitUntil(() => _composition.Cache.Snapshot.Any(l => l.TargetNorm == "WRONG-1"), "the escalated lock");
            Assert.That(handlerThread, Is.Not.EqualTo(0).And.Not.EqualTo(callbackThread));
            Assert.That(_deps.Locks.Query(new LockQuery()).Any(l => l.TargetNorm == "WRONG-1" && l.Source == LockSource.SetupCenter), Is.True);
        }

        [Test]
        public void The_timers_drive_the_worker_the_flush_the_health_and_the_station_directory()
        {
            _deps.Locks.InsertActive(Pu("U1", state: EnforceState.Pending));
            var timings = Slow();
            timings.Worker = TimeSpan.FromMilliseconds(50);
            timings.DecisionFlush = TimeSpan.FromMilliseconds(50);
            Start(timings);

            WaitUntil(() => _deps.Count("sc.LockPackagingUnits:U1") > 0, "the worker to apply the reconciled lock");
            _composition.Decisions.Add(new DecisionLogEntry { Method = "Ping", Result = "OK", CreatedAtUtc = DateTime.UtcNow });
            WaitUntil(() => _composition.Decisions.Count == 0, "the decision flush");
            WaitUntil(() => _deps.Count("stations.Refresh") > 0, "the station refresh at start-up");
            WaitUntil(() => _deps.Count("sc.IsConnected") > 0, "the health tick at start-up");
        }

        [Test]
        public void A_timer_never_overlaps_itself_and_survives_exceptions()
        {
            var log = new ListLog();
            var running = 0;
            var maxRunning = 0;
            var runs = 0;
            var timer = new GuardedTimer("t", TimeSpan.Zero, TimeSpan.FromMilliseconds(10), () =>
            {
                var now = Interlocked.Increment(ref running);
                lock (log)
                    maxRunning = Math.Max(maxRunning, now);
                Thread.Sleep(60);
                Interlocked.Decrement(ref running);
                if (Interlocked.Increment(ref runs) % 2 == 0)
                    throw new InvalidOperationException("boom");
            }, log);

            WaitUntil(() => Volatile.Read(ref runs) >= 4, "four runs");
            Assert.That(timer.Stop(TimeSpan.FromSeconds(5)), Is.True);
            var afterStop = Volatile.Read(ref runs);
            Thread.Sleep(100);

            Assert.That(maxRunning, Is.EqualTo(1));
            Assert.That(Volatile.Read(ref runs), Is.EqualTo(afterStop));
            Assert.That(log.Errors.Count, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void The_escalation_queue_drops_the_oldest_notice_when_full()
        {
            var log = new ListLog();
            var handled = new ConcurrentQueue<string>();
            var q = new EscalationQueue(r => handled.Enqueue(r[0].Uid), 2, log);
            q.Add(new List<PuLockReport> { new PuLockReport { Uid = "A" } });
            q.Add(new List<PuLockReport> { new PuLockReport { Uid = "B" } });
            q.Add(new List<PuLockReport> { new PuLockReport { Uid = "C" } });

            q.Start();
            q.Stop(TimeSpan.FromSeconds(5));

            Assert.That(handled, Is.EqualTo(new[] { "B", "C" }));
            Assert.That(log.Warnings.Single(), Does.Contain("dropped the oldest").And.Contain("UID A"));
            Assert.DoesNotThrow(() => q.Add(new List<PuLockReport> { new PuLockReport { Uid = "D" } }));
        }

        // ---- fakes ----

        private sealed class RecordingLog : ILog, IDisposable
        {
            private readonly ListLog _inner = new ListLog();
            public List<string> Infos { get { return _inner.Infos; } }
            public List<string> Warnings { get { return _inner.Warnings; } }
            public List<string> Errors { get { return _inner.Errors; } }
            public bool Disposed { get; private set; }
            public void Info(string m) { _inner.Info(m); }
            public void Warn(string m) { _inner.Warn(m); }
            public void Error(string m, Exception ex) { _inner.Error(m + (ex == null ? "" : " " + ex.Message), ex); }
            public void Dispose() { Disposed = true; }
        }

        private sealed class RecordingSpool : ILockSpool
        {
            private readonly MemorySpool _inner = new MemorySpool();
            private readonly ConcurrentQueue<string> _events;
            public RecordingSpool(ConcurrentQueue<string> events) { _events = events; }
            public Func<LockCache> GateOwner { get; set; }
            public List<bool> ReadAllUnderGate { get; } = new List<bool>();
            public void Append(LockRecord l) { _inner.Append(l); }
            public IList<LockRecord> ReadAll()
            {
                _events.Enqueue("spool.ReadAll");
                if (GateOwner != null)
                    ReadAllUnderGate.Add(GateOwner() != null && Monitor.IsEntered(GateOwner().WriteGate));
                return _inner.ReadAll();
            }
            public void Clear() { _inner.Clear(); }
        }

        private sealed class DisposableSc : ISetupCenterClient, IDisposable
        {
            private readonly FakeSetupCenterClient _inner;
            private readonly ConcurrentQueue<string> _events;
            public DisposableSc(FakeSetupCenterClient inner, ConcurrentQueue<string> events) { _inner = inner; _events = events; }
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; _events.Enqueue("dispose.Clients"); }
            public bool IsConnected() { return _inner.IsConnected(); }
            public bool HasLicense() { return _inner.HasLicense(); }
            public void LockPackagingUnits(IList<string> uids, string reason) { _inner.LockPackagingUnits(uids, reason); }
            public void UnlockPackagingUnits(IList<string> uids) { _inner.UnlockPackagingUnits(uids); }
            public FeederMount FindMountedFeeder(string feederId) { return _inner.FindMountedFeeder(feederId); }
            public void LockOnlineFeeder(FeederMount m, string reason) { _inner.LockOnlineFeeder(m, reason); }
            public void UnlockOnlineFeeder(FeederMount m) { _inner.UnlockOnlineFeeder(m); }
            public IList<MachineRef> GetMachines() { return _inner.GetMachines(); }
            public IList<MountedPu> GetMountedPus(IList<string> machineIds) { return _inner.GetMountedPus(machineIds); }
            public IList<string> GetLinePaths() { return _inner.GetLinePaths(); }
        }

        private sealed class StationsFake : IRefreshableStationDirectory
        {
            private readonly ConcurrentQueue<string> _events;
            public StationsFake(ConcurrentQueue<string> events) { _events = events; }
            public IList<StationRef> Stations { get; } = new List<StationRef>();
            public IList<string> LinePaths { get; } = new List<string>();
            public void Refresh() { _events.Enqueue("stations.Refresh"); }
        }

        private sealed class Listener : IListener
        {
            private readonly string _name;
            private readonly ConcurrentQueue<string> _events;
            public Listener(string name, ConcurrentQueue<string> events) { _name = name; _events = events; }
            public bool Created { get; set; }
            public bool FailOpen { get; set; }
            public bool Opened { get; private set; }
            public bool Disposed { get; private set; }
            public void Open()
            {
                _events.Enqueue(_name + ".Open");
                if (FailOpen)
                    throw new InvalidOperationException(_name + " cannot listen");
                Opened = true;
            }
            public void Close() { _events.Enqueue(_name + ".Close"); Opened = false; }
            public void Dispose() { Disposed = true; Close(); }
        }

        private sealed class Subscriber : ILockEventSource
        {
            private readonly ConcurrentQueue<string> _events;
            public Subscriber(ConcurrentQueue<string> events) { _events = events; }
            public void Start() { _events.Enqueue("subscriber.Start"); }
            public void Tick() { _events.Enqueue("subscriber.Tick"); }
            public void Stop() { _events.Enqueue("subscriber.Stop"); }
            public void Dispose() { _events.Enqueue("dispose.Subscriber"); }
        }

        private sealed class Deps : ICompositionDeps
        {
            public readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
            public readonly InMemoryLockRepository Locks = new InMemoryLockRepository();
            public readonly InMemoryAuditRepository Audit = new InMemoryAuditRepository();
            public readonly InMemoryUserRepository Users = new InMemoryUserRepository();
            public readonly RecordingSpool Spool;
            public readonly FakeSetupCenterClient ScInner = new FakeSetupCenterClient();
            public readonly DisposableSc Sc;
            public readonly FakeLineControlClient Lc = new FakeLineControlClient();
            public readonly FakeMonitoringClient Monitoring = new FakeMonitoringClient();
            public readonly RecordingLog Log = new RecordingLog();
            public readonly Listener Ec;
            public readonly Listener Rest;
            public Action<IList<PuLockReport>> OnReports;

            public Deps()
            {
                Spool = new RecordingSpool(Events);
                Sc = new DisposableSc(ScInner, Events);
                Ec = new Listener("ec", Events);
                Rest = new Listener("rest", Events);
                Locks.OnCall = m => Events.Enqueue("db." + m);
                ScInner.OnCall = m => Events.Enqueue("sc." + m);
            }

            public int Count(string e)
            {
                return Events.Count(x => x == e);
            }

            public IClock Clock { get; } = new SystemClock();

            public ILog CreateLog() { Events.Enqueue("log"); return Log; }

            public CompositionRepositories CreateRepositories(ILog log)
            {
                Events.Enqueue("repositories");
                return new CompositionRepositories { Locks = Locks, Audit = Audit, Users = Users };
            }

            public ILockSpool CreateSpool(ILog log) { Events.Enqueue("spool"); return Spool; }

            public CompositionClients CreateClients(ILog log)
            {
                Events.Enqueue("clients");
                return new CompositionClients { SetupCenter = Sc, LineControl = Lc, Monitoring = Monitoring };
            }

            public IRefreshableStationDirectory CreateStations(IMonitoringClient monitoring, ISetupCenterClient setupCenter, ILog log)
            {
                Events.Enqueue("stations");
                return new StationsFake(Events);
            }

            public IListener CreateExternalControl(LockCache cache, DecisionLogBuffer decisions, IClock clock, ILog log)
            {
                Ec.Created = true;
                return Ec;
            }

            public IListener CreateRest(IInterlockRestApi api, ILog log)
            {
                Rest.Created = true;
                return Rest;
            }

            public ILockEventSource CreateSubscriber(Action<IList<PuLockReport>> onReports, ILog log)
            {
                OnReports = onReports;
                return new Subscriber(Events);
            }
        }
    }
}
