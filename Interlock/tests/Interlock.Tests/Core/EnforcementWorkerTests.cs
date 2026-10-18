using System;
using System.Linq;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class EnforcementWorkerTests
    {
        private ActionQueue _queue;
        private InMemoryLockRepository _repo;
        private InMemoryAuditRepository _audit;
        private FakeSetupCenterClient _sc;
        private FakeLineControlClient _lc;
        private LockCache _cache;
        private AlarmBoard _alarms;
        private FakeClock _clock;
        private ListLog _log;
        private EnforcementWorker _worker;

        [SetUp]
        public void SetUp()
        {
            _queue = new ActionQueue();
            _repo = new InMemoryLockRepository();
            _audit = new InMemoryAuditRepository();
            _sc = new FakeSetupCenterClient();
            _lc = new FakeLineControlClient();
            _cache = new LockCache();
            _alarms = new AlarmBoard();
            _clock = new FakeClock();
            _log = new ListLog();
            _worker = new EnforcementWorker(_queue, _repo, _audit, _sc, _lc, _cache, _alarms, _clock, _log);
        }

        // ---- helpers ----

        private LockRecord MakeLock(long id, LockType type, string target, Lanes? lanes = null)
        {
            return new LockRecord
            {
                Id = id,
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                Lanes = lanes,
                Reason = "Reason " + id,
                Source = LockSource.Manual,
                CreatedBy = "tester",
                CreatedAtUtc = _clock.UtcNow,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private LockRecord Pu(long id, string uid)
        {
            return MakeLock(id, LockType.Pu, uid);
        }

        private LockRecord Feeder(long id, string feederId)
        {
            return MakeLock(id, LockType.Feeder, feederId);
        }

        private LockRecord Conv(long id, string station, Lanes lanes)
        {
            return MakeLock(id, LockType.Conveyor, station, lanes);
        }

        /// <summary>Stores the lock (ACTIVE and cached, or RELEASED for a release job) and queues a job that is due now.</summary>
        private void Enqueue(LockRecord l, JobKind kind)
        {
            if (kind == JobKind.Release)
            {
                l.Status = LockStatus.Released;
                l.ReleasedBy = "tester";
                l.ReleasedAtUtc = _clock.UtcNow;
                l.ReleaseReason = "done";
                _repo.InsertReleased(l);
            }
            else
            {
                _repo.InsertActive(l);
                _cache.Upsert(l.Clone());
            }

            _queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = kind, NotBeforeUtc = _clock.UtcNow });
        }

        // ---- PU ----

        [Test]
        public void Apply_pu_locks_and_marks_applied()
        {
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.Get(1).EnforceError, Is.Null);
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo("LOCK_PU"));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Apply_pu_writes_one_action_row_and_updates_the_cache()
        {
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            _worker.RunDue();

            var row = _audit.Actions.Single();
            Assert.That(row.LockId, Is.EqualTo(1));
            Assert.That(row.Action, Is.EqualTo(Actions.LOCK_PU));
            Assert.That(row.RequestJson, Is.EqualTo("{\"target\":\"U1\",\"lanes\":null}"));
            Assert.That(row.Success, Is.True);
            Assert.That(row.Error, Is.Null);
            Assert.That(row.Attempt, Is.EqualTo(1));
            Assert.That(row.CreatedAtUtc, Is.EqualTo(_clock.UtcNow));

            var cached = _cache.Snapshot.Single();
            Assert.That(cached.Id, Is.EqualTo(1));
            Assert.That(cached.EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(cached.EnforceError, Is.Null);
        }

        [Test]
        public void Request_json_escapes_quotes_and_backslashes_in_the_target()
        {
            Enqueue(Pu(1, "A\"B\\C"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_audit.Actions.Single().RequestJson, Is.EqualTo("{\"target\":\"A\\\"B\\\\C\",\"lanes\":null}"));
        }

        [Test]
        public void Failure_retries_at_5s_30s_2min_then_failed()
        {
            _sc.ThrowAlways = true;
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));

            // the first retry is due 5 s after the first failure
            _clock.Advance(TimeSpan.FromSeconds(4));
            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(1));
            _clock.Advance(TimeSpan.FromSeconds(1));
            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(2));

            // the second one 30 s after the second failure
            _clock.Advance(TimeSpan.FromSeconds(29));
            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(2));
            _clock.Advance(TimeSpan.FromSeconds(1));
            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(3));
            Assert.That(_queue.Count, Is.EqualTo(1));

            // the third one 2 min after the third failure; the fourth failed call ends it
            _clock.Advance(TimeSpan.FromSeconds(119));
            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(3));
            _clock.Advance(TimeSpan.FromSeconds(1));
            _worker.RunDue();

            Assert.That(_sc.Calls.Count, Is.EqualTo(4));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_repo.Get(1).EnforceError, Does.Contain("LockPackagingUnits failed"));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_alarms.Recent(1)[0], Does.Contain("Lock #1 FAILED"));
            Assert.That(_alarms.Recent(1)[0], Does.Contain("LockPackagingUnits failed"));
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Failed));

            _clock.Advance(TimeSpan.FromMinutes(10));
            _worker.RunDue();
            Assert.That(_sc.Calls.Count, Is.EqualTo(4));
        }

        [Test]
        public void Every_failed_call_writes_an_action_row_with_its_attempt_number()
        {
            _sc.ThrowAlways = true;
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            _worker.RunDue();
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();
            _clock.Advance(TimeSpan.FromSeconds(30));
            _worker.RunDue();
            _clock.Advance(TimeSpan.FromMinutes(2));
            _worker.RunDue();

            Assert.That(_audit.Actions.Select(a => a.Attempt), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(_audit.Actions.All(a => !a.Success && a.Action == Actions.LOCK_PU && a.LockId == 1), Is.True);
            Assert.That(_audit.Actions.All(a => a.Error != null && a.Error.Contains("LockPackagingUnits failed")), Is.True);
        }

        [Test]
        public void Success_after_a_retry_clears_the_error_and_logs_the_second_attempt()
        {
            var l = Pu(1, "U1");
            l.EnforceError = "old error";
            _sc.ThrowAlways = true;
            Enqueue(l, JobKind.Apply);

            _worker.RunDue();
            _sc.ThrowAlways = false;
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls.Count, Is.EqualTo(2));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.Get(1).EnforceError, Is.Null);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_audit.Actions.Select(a => a.Success), Is.EqualTo(new[] { false, true }));
            Assert.That(_audit.Actions.Select(a => a.Attempt), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(_alarms.Recent(1), Is.Empty);
        }

        [Test]
        public void Release_pu_unlocks_the_uid_and_leaves_the_cache_alone()
        {
            Enqueue(Pu(1, "U1"), JobKind.Release);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:U1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo(Actions.UNLOCK_PU));
            Assert.That(_audit.Actions.Single().Success, Is.True);
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Release_pu_skipped_when_other_lock_covers_uid()
        {
            _repo.InsertActive(Pu(2, "U1"));
            Enqueue(Pu(1, "U1"), JobKind.Release);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Release_pu_is_retried_when_the_coverage_check_cannot_reach_the_database()
        {
            Enqueue(Pu(1, "U1"), JobKind.Release);
            _repo.Down = true;

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_audit.Actions.Single().Success, Is.False);
            Assert.That(_audit.Actions.Single().Attempt, Is.EqualTo(1));

            _repo.Down = false;
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:U1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        // ---- FEEDER ----

        [Test]
        public void Feeder_not_mounted_is_not_needed()
        {
            Enqueue(Feeder(1, "F1"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "FindMountedFeeder:F1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.NotNeeded));
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.NotNeeded));
        }

        [Test]
        public void Feeder_mounted_is_locked_online()
        {
            _sc.Mounted["F1"] = new FeederMount { FeederId = "F1" };
            Enqueue(Feeder(1, "F1"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "FindMountedFeeder:F1", "LockOnlineFeeder:F1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo(Actions.LOCK_FEEDER_ONLINE));
            Assert.That(row.Success, Is.True);
            Assert.That(row.RequestJson, Is.EqualTo("{\"target\":\"F1\",\"lanes\":null}"));
        }

        [Test]
        public void Release_feeder_unlocks_it_when_mounted()
        {
            _sc.Mounted["F1"] = new FeederMount { FeederId = "F1" };
            Enqueue(Feeder(1, "F1"), JobKind.Release);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "FindMountedFeeder:F1", "UnlockOnlineFeeder:F1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo(Actions.UNLOCK_FEEDER_ONLINE));
        }

        [Test]
        public void Release_feeder_not_mounted_is_applied_without_a_write_call()
        {
            Enqueue(Feeder(1, "F1"), JobKind.Release);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "FindMountedFeeder:F1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions, Is.Empty);
        }

        // ---- CONVEYOR ----

        [Test]
        public void Apply_conveyor_checks_support_then_blocks()
        {
            Enqueue(Conv(1, "S1", Lanes.Left), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "IsBlockSupported:S1", "Block:S1:LEFT" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo(Actions.BLOCK_CONVEYOR));
            Assert.That(row.RequestJson, Is.EqualTo("{\"target\":\"S1\",\"lanes\":\"LEFT\"}"));
            Assert.That(row.Success, Is.True);
        }

        [Test]
        public void Conveyor_not_supported_fails_without_retry()
        {
            _lc.Supported = false;
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "IsBlockSupported:S1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_repo.Get(1).EnforceError, Is.EqualTo("Conveyor block not supported"));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_alarms.Recent(1)[0], Does.Contain("Lock #1 FAILED: Conveyor block not supported"));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo(Actions.BLOCK_CONVEYOR));
            Assert.That(row.Success, Is.False);
            Assert.That(row.Error, Is.EqualTo("Conveyor block not supported"));
            Assert.That(row.Attempt, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Failed));
        }

        [Test]
        public void Conveyor_without_lanes_fails_without_retry_and_without_an_oib_call()
        {
            Enqueue(MakeLock(1, LockType.Conveyor, "S1"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.Empty);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_repo.Get(1).EnforceError, Is.EqualTo("Conveyor lock has no lanes"));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_alarms.Recent(1)[0], Does.Contain("Lock #1 FAILED: Conveyor lock has no lanes"));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo(Actions.BLOCK_CONVEYOR));
            Assert.That(row.Success, Is.False);
            Assert.That(row.Error, Is.EqualTo("Conveyor lock has no lanes"));
            Assert.That(row.RequestJson, Is.EqualTo("{\"target\":\"S1\",\"lanes\":null}"));
        }

        [Test]
        public void Failing_conveyor_block_is_retried()
        {
            _lc.ThrowAlways = true;
            Enqueue(Conv(1, "S1", Lanes.Right), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "IsBlockSupported:S1" }));
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_audit.Actions.Single().Success, Is.False);
        }

        [Test]
        public void Release_conveyor_unblocks_the_lanes()
        {
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Release);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "Unblock:S1:BOTH" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo(Actions.UNBLOCK_CONVEYOR));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Reblock_blocks_logs_and_raises_alarm()
        {
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Reblock);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "Block:S1:BOTH" }));
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo("REBLOCK_CONVEYOR"));
            Assert.That(_audit.Actions.Single().Success, Is.True);
            Assert.That(_alarms.Recent(1)[0], Does.Contain("Conveyor re-blocked: S1 (#1)"));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
        }

        [Test]
        public void Failed_reblock_raises_no_reblocked_alarm_and_is_retried()
        {
            _lc.ThrowAlways = true;
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Reblock);

            _worker.RunDue();

            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo(Actions.REBLOCK_CONVEYOR));
            Assert.That(_audit.Actions.Single().Success, Is.False);
            Assert.That(_alarms.Recent(5), Is.Empty);
        }

        // ---- cache, and locks released while their job waits or runs ----

        [Test]
        public void Apply_job_for_a_lock_that_left_the_cache_makes_no_oib_call()
        {
            Enqueue(Pu(1, "U1"), JobKind.Apply);
            _cache.Remove(1);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_alarms.Recent(5), Is.Empty);
            Assert.That(_log.Infos, Has.Some.Contains("no longer cached"));
        }

        [Test]
        public void Reblock_job_for_a_lock_that_left_the_cache_makes_no_oib_call()
        {
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Reblock);
            _cache.Remove(1);

            _worker.RunDue();

            Assert.That(_lc.Calls, Is.Empty);
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_alarms.Recent(5), Is.Empty);
            Assert.That(_log.Infos, Has.Some.Contains("no longer cached"));
        }

        [Test]
        public void Spooled_apply_job_for_a_lock_that_left_the_cache_makes_no_oib_call()
        {
            _queue.Enqueue(new EnforcementJob { Lock = Pu(0, "U1"), Kind = JobKind.Apply, NotBeforeUtc = _clock.UtcNow });

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Release_job_runs_when_its_lock_is_not_cached()
        {
            Enqueue(Pu(1, "U1"), JobKind.Release);
            Assert.That(_cache.Snapshot, Is.Empty);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:U1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
        }

        [Test]
        public void Failed_release_is_retried_although_its_lock_is_not_cached()
        {
            _sc.ThrowAlways = true;
            Enqueue(Pu(1, "U1"), JobKind.Release);

            _worker.RunDue();
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:U1", "UnlockPackagingUnits:U1" }));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void Lock_released_during_a_successful_apply_call_is_not_put_back_in_the_cache()
        {
            _sc.OnCall = c => _cache.Remove(1);
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Failing_apply_is_not_retried_when_the_lock_left_the_cache_during_the_call()
        {
            _sc.ThrowAlways = true;
            _sc.OnCall = c => _cache.Remove(1);
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            _worker.RunDue();
            Assert.That(_queue.Count, Is.EqualTo(0), "no retry queued");
            _clock.Advance(TimeSpan.FromMinutes(10));
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1" }));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_alarms.Recent(5), Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_audit.Actions.Single().Success, Is.False);
            Assert.That(_log.Infos, Has.Some.Contains("no retry"));
        }

        [Test]
        public void Conveyor_apply_that_cannot_be_retried_does_not_end_failed_when_the_lock_left_the_cache()
        {
            _lc.Supported = false;
            _lc.OnCall = c => _cache.Remove(1);
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_alarms.Recent(5), Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_audit.Actions.Single().Error, Is.EqualTo("Conveyor block not supported"));
        }

        [Test]
        public void Failing_reblock_is_not_retried_when_the_lock_left_the_cache_during_the_call()
        {
            _lc.ThrowAlways = true;
            _lc.OnCall = c => _cache.Remove(1);
            Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Reblock);

            _worker.RunDue();
            Assert.That(_queue.Count, Is.EqualTo(0), "no retry queued");
            _clock.Advance(TimeSpan.FromMinutes(10));
            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "Block:S1:BOTH" }));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_alarms.Recent(5), Is.Empty);
        }

        [Test]
        public void Queued_retry_is_skipped_when_the_lock_is_released_before_it_is_due()
        {
            _sc.ThrowAlways = true;
            Enqueue(Pu(1, "U1"), JobKind.Apply);
            _worker.RunDue();
            Assert.That(_queue.Count, Is.EqualTo(1));

            // the operator releases the lock: the cache entry goes, the release job is queued
            _cache.Remove(1);
            _repo.MarkReleased(1, "operator", _clock.UtcNow, "done");
            var released = Pu(1, "U1");
            released.Status = LockStatus.Released;
            _queue.Enqueue(new EnforcementJob { Lock = released, Kind = JobKind.Release, NotBeforeUtc = _clock.UtcNow });
            _sc.ThrowAlways = false;

            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1", "UnlockPackagingUnits:U1" }));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Get(1).Status, Is.EqualTo(LockStatus.Released));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Other_cached_locks_are_left_alone_when_a_job_finishes()
        {
            Enqueue(Pu(1, "U1"), JobKind.Apply);
            _cache.Upsert(Pu(2, "U2"));

            _worker.RunDue();

            Assert.That(_cache.Snapshot.Select(e => e.Id), Is.EquivalentTo(new long[] { 1, 2 }));
            Assert.That(_cache.Snapshot.Single(e => e.Id == 1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_cache.Snapshot.Single(e => e.Id == 2).EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        // ---- database trouble, unhandled kinds, queue ----

        [Test]
        public void Db_down_does_not_stop_enforcement()
        {
            _repo.Down = true;
            _audit.Down = true;
            Enqueue(Pu(1, "U1"), JobKind.Apply);

            Assert.DoesNotThrow(() => _worker.RunDue());

            Assert.That(_sc.Calls.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Warnings, Is.Not.Empty);
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_alarms.Recent(5), Is.Empty);
        }

        [Test]
        public void Db_down_does_not_stop_the_remaining_jobs()
        {
            _repo.Down = true;
            _audit.Down = true;
            Enqueue(Pu(1, "U1"), JobKind.Apply);
            Enqueue(Pu(2, "U2"), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1", "LockPackagingUnits:U2" }));
        }

        [Test]
        public void Lock_without_an_id_is_enforced_but_not_written_to_the_database()
        {
            var spooled = Pu(0, "U1");
            _cache.Upsert(spooled.Clone());
            _queue.Enqueue(new EnforcementJob { Lock = spooled.Clone(), Kind = JobKind.Apply, NotBeforeUtc = _clock.UtcNow });

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1" }));
            Assert.That(_repo.Query(new LockQuery()), Is.Empty);
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_log.Warnings, Has.Some.Contains("no id"));
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Reblock_of_a_non_conveyor_lock_is_dropped_with_a_warning()
        {
            Enqueue(Pu(1, "U1"), JobKind.Reblock);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void Jobs_that_are_not_due_stay_in_the_queue()
        {
            var l = Pu(1, "U1");
            _repo.InsertActive(l);
            _cache.Upsert(l.Clone());
            _queue.Enqueue(new EnforcementJob { Lock = l.Clone(), Kind = JobKind.Apply, NotBeforeUtc = _clock.UtcNow.AddSeconds(10) });

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(1));

            _clock.Advance(TimeSpan.FromSeconds(10));
            _worker.RunDue();

            Assert.That(_sc.Calls.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void One_run_handles_all_due_jobs_in_order()
        {
            _sc.Mounted["F1"] = new FeederMount { FeederId = "F1" };
            Enqueue(Pu(1, "U1"), JobKind.Apply);
            Enqueue(Feeder(2, "F1"), JobKind.Apply);
            Enqueue(Conv(3, "S1", Lanes.Both), JobKind.Apply);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1", "FindMountedFeeder:F1", "LockOnlineFeeder:F1" }));
            Assert.That(_lc.Calls, Is.EqualTo(new[] { "IsBlockSupported:S1", "Block:S1:BOTH" }));
            Assert.That(_repo.Query(new LockQuery()).Select(l => l.EnforceState), Is.All.EqualTo(EnforceState.Applied));
        }
    }
}
