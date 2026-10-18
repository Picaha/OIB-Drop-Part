using System;
using System.Linq;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Service.Health;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public class HealthMonitorTests
    {
        private FakeSetupCenterClient _sc;
        private FakeLineControlClient _lc;
        private FakeMonitoringClient _mon;
        private InMemoryLockRepository _repo;
        private MemorySpool _spool;
        private LockCache _cache;
        private ActionQueue _queue;
        private AlarmBoard _alarms;
        private FakeClock _clock;
        private ListLog _log;
        private CacheRefresher _refresher;
        private HealthMonitor _health;

        [SetUp]
        public void SetUp()
        {
            _sc = new FakeSetupCenterClient();
            _lc = new FakeLineControlClient();
            _mon = new FakeMonitoringClient();
            _repo = new InMemoryLockRepository();
            _spool = new MemorySpool();
            _cache = new LockCache();
            _queue = new ActionQueue();
            _alarms = new AlarmBoard();
            _clock = new FakeClock();
            _log = new ListLog();
            _refresher = new CacheRefresher(_repo, _spool, _cache, _queue, _clock, _log);
            _health = new HealthMonitor(_sc, _lc, _mon, _repo, _refresher, _cache, _queue, _alarms, _clock, _log);
        }

        private static LockRecord Pu(string uid)
        {
            return new LockRecord
            {
                Type = LockType.Pu,
                Target = uid,
                TargetNorm = uid,
                Reason = "bad",
                Source = LockSource.Manual,
                CreatedBy = "qa1",
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        [Test]
        public void Before_the_first_tick_oib_reads_as_down_and_licenses_as_unknown()
        {
            var s = _health.Snapshot();

            Assert.That(s.setupCenterConnected, Is.False);
            Assert.That(s.setupCenterLicense, Is.Null);
            Assert.That(s.lineControlLicense, Is.Null);
            Assert.That(s.monitoringOk, Is.False);
            Assert.That(s.dbOk, Is.False);
            Assert.That(s.cacheLoadedAt, Is.Null);
            Assert.That(s.cacheRefreshFailures, Is.EqualTo(0));
            Assert.That(s.cacheRefreshError, Is.Null);
            Assert.That(s.alarms, Is.Empty);
        }

        [Test]
        public void Tick_records_the_oib_state_and_snapshot_never_calls_oib()
        {
            _health.Tick();
            _sc.Calls.Clear();
            _lc.Calls.Clear();
            var monitoringCalls = _mon.Calls;

            var s = _health.Snapshot();
            _health.Snapshot();

            Assert.That(s.setupCenterConnected, Is.True);
            Assert.That(s.setupCenterLicense, Is.True);
            Assert.That(s.lineControlLicense, Is.True);
            Assert.That(s.monitoringOk, Is.True);
            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_lc.Calls, Is.Empty);
            Assert.That(_mon.Calls, Is.EqualTo(monitoringCalls));
        }

        [Test]
        public void A_failing_connection_check_reports_false_and_the_license_is_not_used_as_connectivity()
        {
            _sc.OnCall = c => { if (c == "IsConnected") throw new OibCallException("reconnecting"); };

            Assert.DoesNotThrow(() => _health.Tick());
            var s = _health.Snapshot();

            Assert.That(s.setupCenterConnected, Is.False);
            Assert.That(s.setupCenterLicense, Is.True);
            Assert.That(s.lineControlLicense, Is.True);
            Assert.That(_log.Warnings.Any(w => w.Contains("Setup Center connection check")), Is.True);
        }

        [Test]
        public void A_failing_license_call_is_unknown_and_does_not_flip_the_connection()
        {
            _sc.OnCall = c => { if (c == "HasLicense") throw new OibCallException("busy"); };
            _lc.OnCall = c => { if (c == "HasLicense") throw new TimeoutException("slow"); };

            _health.Tick();
            var s = _health.Snapshot();

            Assert.That(s.setupCenterConnected, Is.True);
            Assert.That(s.setupCenterLicense, Is.Null);
            Assert.That(s.lineControlLicense, Is.Null);
        }

        [Test]
        public void A_monitoring_failure_is_reported_and_the_other_checks_still_run()
        {
            _mon.Down = true;

            _health.Tick();
            var s = _health.Snapshot();

            Assert.That(s.monitoringOk, Is.False);
            Assert.That(s.setupCenterConnected, Is.True);
            Assert.That(s.lineControlLicense, Is.True);
        }

        [Test]
        public void The_next_tick_recovers()
        {
            _sc.OnCall = c => { throw new OibCallException("down"); };
            _health.Tick();
            _sc.OnCall = null;

            _health.Tick();

            Assert.That(_health.Snapshot().setupCenterConnected, Is.True);
            Assert.That(_health.Snapshot().setupCenterLicense, Is.True);
        }

        [Test]
        public void Failed_locks_are_counted_and_kept_when_the_database_is_down()
        {
            var a = _repo.InsertActive(Pu("U1"));
            _repo.InsertActive(Pu("U2"));
            var released = _repo.InsertReleased(Pu("U3"));
            _repo.SetEnforceState(a.Id, EnforceState.Failed, "x");
            _repo.SetEnforceState(released.Id, EnforceState.Failed, "y");

            _health.Tick();
            Assert.That(_health.Snapshot().failedLocks, Is.EqualTo(2));

            _repo.Down = true;
            Assert.DoesNotThrow(() => _health.Tick());
            Assert.That(_health.Snapshot().failedLocks, Is.EqualTo(2));
        }

        [Test]
        public void Snapshot_reports_the_in_memory_state_live()
        {
            _repo.InsertActive(Pu("U1"));
            _refresher.Tick();
            _queue.Enqueue(new EnforcementJob { Lock = Pu("U1"), Kind = JobKind.Apply, NotBeforeUtc = _clock.UtcNow });
            _alarms.Raise("OIB down", _clock.UtcNow);

            var s = _health.Snapshot();

            Assert.That(s.dbOk, Is.True);
            Assert.That(s.lockCount, Is.EqualTo(1));
            Assert.That(s.queueLength, Is.EqualTo(1));
            Assert.That(s.cacheLoadedAt, Is.EqualTo("2026-10-18T00:00:00.0000000Z"));
            Assert.That(s.alarms, Has.Length.EqualTo(1));
            StringAssert.EndsWith("OIB down", s.alarms[0]);
        }

        [Test]
        public void Cache_refresh_failures_and_their_error_are_reported_and_clear_on_recovery()
        {
            _repo.Down = true;
            _refresher.Tick();
            var s = _health.Snapshot();

            Assert.That(s.dbOk, Is.False);
            Assert.That(s.cacheRefreshFailures, Is.EqualTo(1));
            Assert.That(s.cacheRefreshError, Is.Not.Null.And.Contains("DbUnavailableException"));

            _repo.Down = false;
            _refresher.Tick();
            s = _health.Snapshot();

            Assert.That(s.dbOk, Is.True);
            Assert.That(s.cacheRefreshFailures, Is.EqualTo(0));
            Assert.That(s.cacheRefreshError, Is.Null);
        }
    }
}
