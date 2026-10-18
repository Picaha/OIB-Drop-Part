using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Tests.Fakes;
using Interlock.Tests.Support;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class BackgroundTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private InMemoryLockRepository _repo;
        private InMemoryAuditRepository _audit;
        private FakeSetupCenterClient _sc;
        private FakeLineControlClient _lc;
        private LockCache _cache;
        private ActionQueue _queue;
        private AlarmBoard _alarms;
        private FakeClock _clock;
        private ListLog _log;
        private EnforcementWorker _worker;
        private ConveyorWatchdog _watchdog;

        [SetUp]
        public void SetUp()
        {
            _repo = new InMemoryLockRepository();
            _audit = new InMemoryAuditRepository();
            _sc = new FakeSetupCenterClient();
            _lc = new FakeLineControlClient();
            _cache = new LockCache();
            _queue = new ActionQueue();
            _alarms = new AlarmBoard();
            _clock = new FakeClock { UtcNow = T0 };
            _log = new ListLog();
            _worker = new EnforcementWorker(_queue, _repo, _audit, _sc, _lc, _cache, _alarms, _clock, _log);
            _watchdog = new ConveyorWatchdog(_cache, _lc, _queue, _clock, _log);
        }

        // ---- helpers ----

        private static LockRecord MakeLock(long id, LockType type, string target, Lanes? lanes, EnforceState state)
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
                CreatedAtUtc = T0,
                Status = LockStatus.Active,
                EnforceState = state
            };
        }

        private static LockRecord Pu(long id, string uid, EnforceState state)
        {
            return MakeLock(id, LockType.Pu, uid, null, state);
        }

        private static LockRecord Conv(long id, string station, Lanes? lanes, EnforceState state)
        {
            return MakeLock(id, LockType.Conveyor, station, lanes, state);
        }

        private static DecisionLogEntry Entry(int i)
        {
            return new DecisionLogEntry
            {
                Method = "ScanCheck",
                Uid = i.ToString(CultureInfo.InvariantCulture),
                Result = "OK",
                CreatedAtUtc = T0
            };
        }

        private static long[] Ids(IEnumerable<EnforcementJob> jobs, JobKind kind)
        {
            return jobs.Where(j => j.Kind == kind).Select(j => j.Lock.Id).OrderBy(id => id).ToArray();
        }

        // ---- Reconciler ----

        [Test]
        public void Reconcile_queues_pending_failed_and_pending_releases()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _repo.InsertActive(Pu(2, "B", EnforceState.Failed));
            _repo.InsertActive(Pu(3, "C", EnforceState.Applied));
            _repo.InsertReleased(Pu(4, "D", EnforceState.Pending));
            _repo.InsertReleased(Pu(5, "E", EnforceState.Applied));

            Assert.That(Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock), Is.EqualTo(3));

            var jobs = _queue.TakeDue(_clock.UtcNow);
            Assert.That(jobs.Where(j => j.Kind == JobKind.Apply).Select(j => j.Lock.Id), Is.EquivalentTo(new long[] { 1, 2 }));
            Assert.That(jobs.Single(j => j.Kind == JobKind.Release).Lock.Id, Is.EqualTo(4));
            Assert.That(jobs.Count, Is.EqualTo(3));
        }

        [Test]
        public void Reconcile_skips_a_lock_that_already_has_a_queued_job_of_the_same_kind()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _repo.InsertActive(Pu(2, "B", EnforceState.Failed));
            _repo.InsertReleased(Pu(3, "C", EnforceState.Pending));
            _repo.InsertReleased(Pu(4, "D", EnforceState.Pending));
            _cache.Upsert(Pu(1, "A", EnforceState.Pending));
            _queue.Enqueue(new EnforcementJob { Lock = Pu(1, "A", EnforceState.Pending), Kind = JobKind.Apply, NotBeforeUtc = T0 });
            _queue.Enqueue(new EnforcementJob { Lock = Pu(3, "C", EnforceState.Pending), Kind = JobKind.Release, NotBeforeUtc = T0 });
            // A Release job for lock 2 does not stand in for its Apply.
            _queue.Enqueue(new EnforcementJob { Lock = Pu(2, "B", EnforceState.Failed), Kind = JobKind.Release, NotBeforeUtc = T0 });

            Assert.That(Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock), Is.EqualTo(2));

            var jobs = _queue.TakeDue(_clock.UtcNow);
            Assert.That(Ids(jobs, JobKind.Apply), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(Ids(jobs, JobKind.Release), Is.EqualTo(new long[] { 2, 3, 4 }));
        }

        [Test]
        public void Reconcile_with_no_open_locks_queues_nothing()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Applied));
            _repo.InsertActive(Pu(2, "B", EnforceState.NotNeeded));
            _repo.InsertReleased(Pu(3, "C", EnforceState.Applied));

            Assert.That(Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock), Is.EqualTo(0));

            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Reconcile_jobs_are_due_now_and_start_without_failed_attempts()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Failed));
            _repo.InsertReleased(Pu(2, "B", EnforceState.Pending));

            Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock);

            var jobs = _queue.TakeDue(_clock.UtcNow);
            Assert.That(jobs.Count, Is.EqualTo(2));
            Assert.That(jobs.All(j => j.NotBeforeUtc == T0), Is.True);
            Assert.That(jobs.All(j => j.FailedAttempts == 0), Is.True);
        }

        [Test]
        public void Reconcile_caches_the_open_active_locks_and_a_worker_run_enforces_them()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _repo.InsertActive(Pu(2, "B", EnforceState.Failed));
            _repo.InsertActive(Pu(3, "C", EnforceState.Applied));
            _repo.InsertReleased(Pu(4, "D", EnforceState.Pending));

            Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock);

            // The worker drops an Apply job whose lock is not cached, so the Pending and Failed locks are cached; a release is not.
            Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EquivalentTo(new long[] { 1, 2 }));

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EquivalentTo(new[] { "LockPackagingUnits:A", "LockPackagingUnits:B", "UnlockPackagingUnits:D" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.Get(2).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.Get(4).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EquivalentTo(new long[] { 1, 2 }));
            Assert.That(_cache.Snapshot.All(l => l.EnforceState == EnforceState.Applied), Is.True);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Warnings, Is.Empty);
        }

        [Test]
        public void Reconcile_keeps_a_lock_that_is_cached_already()
        {
            var cached = Pu(1, "A", EnforceState.Pending);
            cached.Reason = "cached copy";
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _cache.Upsert(cached);

            Assert.That(Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock), Is.EqualTo(1));

            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single(), Is.SameAs(cached));
            Assert.That(_queue.TakeDue(_clock.UtcNow).Single().Kind, Is.EqualTo(JobKind.Apply));
        }

        [Test]
        public void Reconcile_gives_each_job_and_the_cache_their_own_copy_of_the_lock()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _repo.InsertReleased(Pu(2, "B", EnforceState.Pending));

            Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock);

            var apply = _queue.TakeDue(_clock.UtcNow).Single(j => j.Kind == JobKind.Apply);
            Assert.That(apply.Lock, Is.Not.SameAs(_cache.Snapshot.Single()));
            apply.Lock.EnforceState = EnforceState.Applied;
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void Reconcile_with_the_database_down_propagates_and_changes_nothing()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _repo.Down = true;

            Assert.Throws<DbUnavailableException>(() => Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock));

            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Reconcile_that_loses_the_database_before_the_releases_changes_nothing()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));
            _repo.InsertReleased(Pu(2, "B", EnforceState.Pending));
            _repo.OnCall = call =>
            {
                if (call == "LoadPendingReleases")
                    _repo.Down = true;
            };

            Assert.Throws<DbUnavailableException>(() => Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock));

            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Reconcile_does_not_queue_the_same_lock_twice()
        {
            var stub = new StubLockRepository();
            stub.Active.Add(Pu(1, "A", EnforceState.Pending));
            stub.Active.Add(Pu(1, "A", EnforceState.Pending));
            stub.Active.Add(Pu(2, "B", EnforceState.Failed));
            stub.Releases.Add(Pu(3, "C", EnforceState.Pending));
            stub.Releases.Add(Pu(3, "C", EnforceState.Pending));

            Assert.That(Reconciler.QueueOnStartup(stub, _cache, _queue, _clock), Is.EqualTo(3));

            var jobs = _queue.TakeDue(_clock.UtcNow);
            Assert.That(Ids(jobs, JobKind.Apply), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(Ids(jobs, JobKind.Release), Is.EqualTo(new long[] { 3 }));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(2));
        }

        [Test]
        public void Reconcile_takes_the_cache_write_gate()
        {
            _repo.InsertActive(Pu(1, "A", EnforceState.Pending));

            WriteGateProbe.AssertWaits(_cache,
                () => Reconciler.QueueOnStartup(_repo, _cache, _queue, _clock),
                () => _queue.Count == 0 && _cache.Snapshot.Count == 0);

            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
        }

        [Test]
        public void Reconcile_rejects_missing_arguments()
        {
            Assert.Throws<ArgumentNullException>(() => Reconciler.QueueOnStartup(null, _cache, _queue, _clock));
            Assert.Throws<ArgumentNullException>(() => Reconciler.QueueOnStartup(_repo, null, _queue, _clock));
            Assert.Throws<ArgumentNullException>(() => Reconciler.QueueOnStartup(_repo, _cache, null, _clock));
            Assert.Throws<ArgumentNullException>(() => Reconciler.QueueOnStartup(_repo, _cache, _queue, null));
        }

        // ---- ConveyorWatchdog ----

        [Test]
        public void Watchdog_queues_reblock_when_conveyor_unblocked()
        {
            _cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, T0);
            _lc.States["S1"] = new ConveyorState { RightBlocked = true, LeftBlocked = false };

            _watchdog.Tick();

            var job = _queue.TakeDue(_clock.UtcNow).Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Reblock));
            Assert.That(job.Lock.Id, Is.EqualTo(1));
            Assert.That(job.NotBeforeUtc, Is.EqualTo(T0));
            Assert.That(job.FailedAttempts, Is.EqualTo(0));
            Assert.That(job.Lock, Is.Not.SameAs(_cache.Snapshot.Single()));
            Assert.That(_lc.Calls, Is.EqualTo(new[] { "GetState:S1" }));
        }

        [Test]
        public void Watchdog_ignores_blocked_and_unapplied()
        {
            _cache.Replace(new[] { Conv(1, "S1", Lanes.Left, EnforceState.Applied), Conv(2, "S2", Lanes.Both, EnforceState.Pending) }, T0);
            _lc.States["S1"] = new ConveyorState { LeftBlocked = true };
            _lc.States["S2"] = new ConveyorState();

            _watchdog.Tick();

            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_lc.Calls, Is.EqualTo(new[] { "GetState:S1" }), "the Pending lock is not checked");
        }

        [Test]
        public void Watchdog_checks_only_applied_active_conveyor_locks_with_lanes()
        {
            var released = Conv(5, "S5", Lanes.Both, EnforceState.Applied);
            released.Status = LockStatus.Released;
            _cache.Replace(new[]
            {
                Conv(1, "S1", null, EnforceState.Applied),
                Conv(2, "S2", Lanes.Both, EnforceState.Pending),
                Conv(3, "S3", Lanes.Both, EnforceState.Failed),
                Conv(4, "S4", Lanes.Both, EnforceState.NotNeeded),
                released,
                Pu(6, "U6", EnforceState.Applied)
            }, T0);

            _watchdog.Tick();

            Assert.That(_lc.Calls, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Watchdog_checks_every_applied_conveyor_one_station_at_a_time()
        {
            _cache.Replace(new[]
            {
                Conv(1, "S1", Lanes.Left, EnforceState.Applied),
                Conv(2, "S2", Lanes.Right, EnforceState.Applied),
                Conv(3, "S3", Lanes.Both, EnforceState.Applied)
            }, T0);
            _lc.States["S1"] = new ConveyorState { LeftBlocked = true };
            _lc.States["S2"] = new ConveyorState { LeftBlocked = true };
            _lc.States["S3"] = new ConveyorState { LeftBlocked = true, RightBlocked = true };

            _watchdog.Tick();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "GetState:S1", "GetState:S2", "GetState:S3" }));
            Assert.That(_queue.TakeDue(_clock.UtcNow).Select(j => j.Lock.Id), Is.EqualTo(new long[] { 2 }));
        }

        [Test]
        public void Watchdog_survives_get_state_error()
        {
            _lc.ThrowAlways = true;
            _cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, T0);

            Assert.DoesNotThrow(() => _watchdog.Tick());

            Assert.That(_log.Warnings.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Watchdog_logs_one_warning_per_failing_lock_and_goes_on_with_the_next_one()
        {
            _cache.Replace(new[]
            {
                Conv(1, "S1", Lanes.Both, EnforceState.Applied),
                Conv(2, "S2", Lanes.Both, EnforceState.Applied),
                Conv(3, "S3", Lanes.Both, EnforceState.Applied)
            }, T0);
            _lc.OnCall = call =>
            {
                if (call == "GetState:S1" || call == "GetState:S2")
                    throw new OibCallException("Line Control is not answering.");
            };

            _watchdog.Tick();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "GetState:S1", "GetState:S2", "GetState:S3" }));
            Assert.That(_log.Warnings.Count, Is.EqualTo(2));
            Assert.That(_log.Warnings[0], Does.Contain("S1"));
            Assert.That(_log.Warnings[1], Does.Contain("S2"));
            Assert.That(_queue.TakeDue(_clock.UtcNow).Select(j => j.Lock.Id), Is.EqualTo(new long[] { 3 }), "the healthy station is still checked");
        }

        [Test]
        public void Watchdog_does_not_queue_a_second_reblock_while_one_is_queued()
        {
            _cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, T0);

            _watchdog.Tick();
            _watchdog.Tick();
            Assert.That(_queue.Count, Is.EqualTo(1));

            // taken (and not queued again by the worker): the next tick may queue a new one
            _queue.TakeDue(_clock.UtcNow);
            _watchdog.Tick();
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void Watchdog_waits_for_a_reblock_that_is_queued_for_a_retry()
        {
            _cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, T0);
            _queue.Enqueue(new EnforcementJob
            {
                Lock = Conv(1, "S1", Lanes.Both, EnforceState.Applied),
                Kind = JobKind.Reblock,
                FailedAttempts = 1,
                NotBeforeUtc = T0.AddSeconds(30)
            });

            _watchdog.Tick();

            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_queue.TakeDue(T0.AddSeconds(30)).Single().FailedAttempts, Is.EqualTo(1));
        }

        [Test]
        public void Watchdog_checks_a_lock_whose_neighbour_has_a_job_queued()
        {
            _cache.Replace(new[]
            {
                Conv(1, "S1", Lanes.Both, EnforceState.Applied),
                Conv(2, "S2", Lanes.Both, EnforceState.Applied)
            }, T0);
            _queue.Enqueue(new EnforcementJob { Lock = Conv(1, "S1", Lanes.Both, EnforceState.Applied), Kind = JobKind.Reblock, NotBeforeUtc = T0 });

            _watchdog.Tick();

            Assert.That(_queue.TakeDue(_clock.UtcNow).Select(j => j.Lock.Id), Is.EqualTo(new long[] { 1, 2 }));
        }

        [Test]
        public void Watchdog_tells_spooled_locks_apart_by_identity()
        {
            // two spooled conveyor locks (Id 0): the queued job of one must not hide the other
            _cache.Replace(new[]
            {
                Conv(0, "S1", Lanes.Both, EnforceState.Applied),
                Conv(0, "S2", Lanes.Both, EnforceState.Applied)
            }, T0);
            _queue.Enqueue(new EnforcementJob { Lock = Conv(0, "S1", Lanes.Both, EnforceState.Applied), Kind = JobKind.Reblock, NotBeforeUtc = T0 });

            _watchdog.Tick();

            var jobs = _queue.TakeDue(_clock.UtcNow);
            Assert.That(jobs.Select(j => j.Lock.TargetNorm), Is.EqualTo(new[] { "S1", "S2" }));
        }

        [Test]
        public void Watchdog_does_not_hold_the_cache_write_gate_while_it_calls_oib()
        {
            _cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, T0);
            var gateFree = new List<bool>();
            _lc.OnCall = call =>
            {
                gateFree.Add(Task.Run(() =>
                {
                    if (!Monitor.TryEnter(_cache.WriteGate))
                        return false;
                    Monitor.Exit(_cache.WriteGate);
                    return true;
                }).Result);
            };

            _watchdog.Tick();

            Assert.That(gateFree, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void Watchdog_reblock_job_is_run_by_the_worker()
        {
            var conveyor = Conv(1, "S1", Lanes.Both, EnforceState.Applied);
            _repo.InsertActive(conveyor);
            _cache.Replace(new[] { conveyor.Clone() }, T0);

            _watchdog.Tick();
            _worker.RunDue();

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "GetState:S1", "Block:S1:BOTH" }));
            Assert.That(_audit.Actions.Single().Action, Is.EqualTo(Actions.REBLOCK_CONVEYOR));
            Assert.That(_alarms.Recent(10).Single(), Does.Contain("Conveyor re-blocked: S1 (#1)"));
        }

        [Test]
        public void Watchdog_rejects_missing_arguments()
        {
            Assert.Throws<ArgumentNullException>(() => new ConveyorWatchdog(null, _lc, _queue, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new ConveyorWatchdog(_cache, null, _queue, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new ConveyorWatchdog(_cache, _lc, null, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new ConveyorWatchdog(_cache, _lc, _queue, null, _log));
            Assert.Throws<ArgumentNullException>(() => new ConveyorWatchdog(_cache, _lc, _queue, _clock, null));
        }

        // ---- DecisionLogBuffer ----

        [Test]
        public void Buffer_drops_oldest_when_full()
        {
            var b = new DecisionLogBuffer(3, _log);
            for (int i = 0; i < 5; i++)
                b.Add(Entry(i));

            Assert.That(b.Count, Is.EqualTo(3));
            Assert.That(b.Flush(_audit), Is.EqualTo(3));
            Assert.That(_audit.Decisions.Select(d => d.Uid), Is.EqualTo(new[] { "2", "3", "4" }));
            Assert.That(_log.Warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Buffer_keeps_rows_when_db_down()
        {
            var b = new DecisionLogBuffer(10, _log);
            _audit.Down = true;
            b.Add(Entry(1));

            Assert.That(b.Flush(_audit), Is.EqualTo(0));
            Assert.That(b.Count, Is.EqualTo(1));
            Assert.That(_audit.Decisions, Is.Empty);

            _audit.Down = false;
            Assert.That(b.Flush(_audit), Is.EqualTo(1));
            Assert.That(b.Count, Is.EqualTo(0));
            Assert.That(_audit.Decisions.Select(d => d.Uid), Is.EqualTo(new[] { "1" }));
        }

        [Test]
        public void Buffer_flush_of_an_empty_buffer_writes_nothing()
        {
            var b = new DecisionLogBuffer(10, _log);
            var audit = new ScriptedAudit();

            Assert.That(b.Flush(audit), Is.EqualTo(0));
            Assert.That(audit.BatchSizes, Is.Empty);
        }

        [Test]
        public void Buffer_flush_writes_batches_of_at_most_500_in_order()
        {
            var b = new DecisionLogBuffer(10000, _log);
            for (int i = 0; i < 1200; i++)
                b.Add(Entry(i));
            var audit = new ScriptedAudit();

            Assert.That(b.Flush(audit), Is.EqualTo(1200));

            Assert.That(audit.BatchSizes, Is.EqualTo(new[] { 500, 500, 200 }));
            Assert.That(audit.Written.Select(d => d.Uid), Is.EqualTo(Enumerable.Range(0, 1200).Select(i => i.ToString(CultureInfo.InvariantCulture))));
            Assert.That(b.Count, Is.EqualTo(0));
        }

        [Test]
        public void Buffer_flush_that_fails_midway_returns_what_was_written_and_keeps_the_rest()
        {
            var b = new DecisionLogBuffer(10000, _log);
            for (int i = 0; i < 1200; i++)
                b.Add(Entry(i));
            var audit = new ScriptedAudit();
            audit.OnAdd = batch =>
            {
                if (audit.BatchSizes.Count == 1)
                    throw new DbUnavailableException("gone");
            };

            Assert.That(b.Flush(audit), Is.EqualTo(500));
            Assert.That(b.Count, Is.EqualTo(700));

            audit.OnAdd = null;
            Assert.That(b.Flush(audit), Is.EqualTo(700));
            Assert.That(audit.Written.Select(d => d.Uid), Is.EqualTo(Enumerable.Range(0, 1200).Select(i => i.ToString(CultureInfo.InvariantCulture))));
            Assert.That(_log.Errors, Is.Empty, "a database that is down is not an error");
        }

        [Test]
        public void Buffer_logs_other_failures_as_errors_and_keeps_the_batch()
        {
            var b = new DecisionLogBuffer(10, _log);
            b.Add(Entry(1));
            b.Add(Entry(2));
            var audit = new ScriptedAudit { OnAdd = batch => { throw new InvalidOperationException("bad row"); } };

            Assert.That(b.Flush(audit), Is.EqualTo(0));

            Assert.That(b.Count, Is.EqualTo(2));
            Assert.That(_log.Errors.Count, Is.EqualTo(1));

            audit.OnAdd = null;
            Assert.That(b.Flush(audit), Is.EqualTo(2));
            Assert.That(audit.Written.Select(d => d.Uid), Is.EqualTo(new[] { "1", "2" }));
        }

        [Test]
        public void Buffer_warns_once_per_overflow_episode()
        {
            var b = new DecisionLogBuffer(2, _log);
            for (int i = 0; i < 6; i++)
                b.Add(Entry(i));
            Assert.That(_log.Warnings.Count, Is.EqualTo(1));

            // a flush that fails does not end the episode
            _audit.Down = true;
            b.Flush(_audit);
            b.Add(Entry(6));
            Assert.That(_log.Warnings.Count, Is.EqualTo(1));

            // a flush that succeeds does
            _audit.Down = false;
            b.Flush(_audit);
            for (int i = 7; i < 12; i++)
                b.Add(Entry(i));
            Assert.That(_log.Warnings.Count, Is.EqualTo(2));
        }

        [Test]
        public void Buffer_does_not_warn_while_it_has_room()
        {
            var b = new DecisionLogBuffer(3, _log);
            for (int i = 0; i < 3; i++)
                b.Add(Entry(i));

            Assert.That(_log.Warnings, Is.Empty);
        }

        [Test]
        public void Buffer_keeps_rows_added_while_a_batch_is_being_written_and_in_order()
        {
            var b = new DecisionLogBuffer(10, _log);
            b.Add(Entry(1));
            b.Add(Entry(2));
            var audit = new ScriptedAudit();
            var first = true;
            audit.OnAdd = batch =>
            {
                if (first)
                {
                    first = false;
                    b.Add(Entry(3));
                    throw new DbUnavailableException("gone while writing");
                }
            };

            Assert.That(b.Flush(audit), Is.EqualTo(0));
            Assert.That(b.Count, Is.EqualTo(3));

            Assert.That(b.Flush(audit), Is.EqualTo(3));
            Assert.That(audit.Written.Select(d => d.Uid), Is.EqualTo(new[] { "1", "2", "3" }));
        }

        [Test]
        public void Buffer_removes_only_the_written_rows_when_an_overflow_dropped_some_of_them_meanwhile()
        {
            var b = new DecisionLogBuffer(3, _log);
            b.Add(Entry(1));
            b.Add(Entry(2));
            b.Add(Entry(3));
            var audit = new ScriptedAudit();
            audit.OnAdd = batch =>
            {
                if (audit.BatchSizes.Count == 0)
                {
                    // 4 and 5 push 1 and 2 out of the full buffer while they are being written
                    b.Add(Entry(4));
                    b.Add(Entry(5));
                }
            };

            Assert.That(b.Flush(audit), Is.EqualTo(5));

            Assert.That(audit.Written.Select(d => d.Uid), Is.EqualTo(new[] { "1", "2", "3", "4", "5" }));
            Assert.That(b.Count, Is.EqualTo(0));
        }

        [Test]
        public void Buffer_rejects_a_capacity_below_one_and_missing_arguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionLogBuffer(0, _log));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionLogBuffer(-1, _log));
            Assert.Throws<ArgumentNullException>(() => new DecisionLogBuffer(1, null));

            var b = new DecisionLogBuffer(1, _log);
            Assert.Throws<ArgumentNullException>(() => b.Add(null));
            Assert.Throws<ArgumentNullException>(() => b.Flush(null));
        }

        [Test]
        public void Buffer_loses_nothing_when_rows_are_added_from_many_threads_while_flushes_run()
        {
            const int producers = 4;
            const int perProducer = 2000;
            var b = new DecisionLogBuffer(producers * perProducer, _log);
            var finished = 0;

            var adders = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++)
                    b.Add(new DecisionLogEntry { Uid = p.ToString(CultureInfo.InvariantCulture) + ":" + i.ToString(CultureInfo.InvariantCulture) });
            })).ToArray();

            // two flushers: a second flush must wait for the first, not write the same rows again
            var flushers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                while (Volatile.Read(ref finished) == 0)
                    b.Flush(_audit);
            })).ToArray();

            Task.WaitAll(adders);
            Volatile.Write(ref finished, 1);
            Task.WaitAll(flushers);
            b.Flush(_audit);

            Assert.That(b.Count, Is.EqualTo(0));
            Assert.That(_audit.Decisions.Count, Is.EqualTo(producers * perProducer));
            Assert.That(_audit.Decisions.Select(d => d.Uid).Distinct().Count(), Is.EqualTo(producers * perProducer));
            for (var p = 0; p < producers; p++)
            {
                var prefix = p.ToString(CultureInfo.InvariantCulture) + ":";
                var indexes = _audit.Decisions.Where(d => d.Uid.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(d => int.Parse(d.Uid.Substring(prefix.Length), CultureInfo.InvariantCulture));
                Assert.That(indexes, Is.EqualTo(Enumerable.Range(0, perProducer)), "producer " + p + " was reordered or lost rows");
            }
            Assert.That(_log.Warnings, Is.Empty);
        }

        // ---- test doubles ----

        /// <summary>A lock repository whose two startup lists the test sets directly (the in-memory one cannot hold a lock twice).</summary>
        private sealed class StubLockRepository : ILockRepository
        {
            public List<LockRecord> Active { get; } = new List<LockRecord>();

            public List<LockRecord> Releases { get; } = new List<LockRecord>();

            public IList<LockRecord> LoadActive() { return Active.Select(l => l.Clone()).ToList(); }

            public IList<LockRecord> LoadPendingReleases() { return Releases.Select(l => l.Clone()).ToList(); }

            public LockRecord Insert(LockRecord l) { throw new NotSupportedException(); }

            public LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes) { throw new NotSupportedException(); }

            public LockRecord Get(long id) { throw new NotSupportedException(); }

            public IList<LockRecord> Query(LockQuery q) { throw new NotSupportedException(); }

            public void MarkReleased(long id, string by, DateTime atUtc, string reason) { throw new NotSupportedException(); }

            public void SetEnforceState(long id, EnforceState s, string error) { throw new NotSupportedException(); }

            public void AddAppliedPu(long lockId, string uid, DateTime atUtc) { throw new NotSupportedException(); }

            public IList<string> GetOpenAppliedPus(long lockId) { throw new NotSupportedException(); }

            public void CloseAppliedPu(long lockId, string uid, DateTime atUtc) { throw new NotSupportedException(); }

            public bool IsUidCoveredByOtherActiveLock(string uidNorm, long exceptLockId) { throw new NotSupportedException(); }

            public DateTime? GetLatestReleaseUtc(LockType t, string targetNorm) { throw new NotSupportedException(); }
        }

        /// <summary>An audit repository that records the batches it is given. <see cref="OnAdd"/> runs first and may throw, so the batch is then not recorded.</summary>
        private sealed class ScriptedAudit : IAuditRepository
        {
            public List<int> BatchSizes { get; } = new List<int>();

            public List<DecisionLogEntry> Written { get; } = new List<DecisionLogEntry>();

            public Action<IList<DecisionLogEntry>> OnAdd { get; set; }

            public void AddDecisions(IList<DecisionLogEntry> e)
            {
                OnAdd?.Invoke(e);
                BatchSizes.Add(e.Count);
                Written.AddRange(e);
            }

            public void AddAction(ActionLogEntry e) { throw new NotSupportedException(); }

            public IList<ActionLogEntry> GetActions(long lockId) { throw new NotSupportedException(); }

            public IList<DecisionLogEntry> QueryDecisions(DecisionQuery q) { throw new NotSupportedException(); }
        }
    }
}
