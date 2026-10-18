using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Interlock.Core.Enforcement;
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
    public class CacheRefresherTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private InMemoryLockRepository _repo;
        private FaultySpool _spool;
        private FakeClock _clock;
        private ListLog _log;
        private LockCache _cache;
        private ActionQueue _queue;
        private LockService _svc;
        private CacheRefresher _refresher;

        [SetUp]
        public void SetUp()
        {
            _repo = new InMemoryLockRepository();
            _spool = new FaultySpool();
            _clock = new FakeClock { UtcNow = T0 };
            _log = new ListLog();
            _cache = new LockCache();
            _queue = new ActionQueue();
            _svc = new LockService(_repo, new InMemoryAuditRepository(), _cache, _queue, new FakeStationDirectory(), _spool, _clock, _log);
            _refresher = new CacheRefresher(_repo, _spool, _cache, _queue, _clock, _log);
        }

        // ---- helpers ----

        private static LockRecord Pu(long id, string uid)
        {
            return new LockRecord
            {
                Id = id,
                Type = LockType.Pu,
                Target = uid,
                TargetNorm = Normalizer.Norm(uid),
                Reason = "Reel damaged",
                Source = LockSource.Manual,
                CreatedBy = "someone",
                CreatedAtUtc = T0,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        /// <summary>Reports a Setup Center mismatch for each UID while the database is down: one spooled, cached, queued Id-0 lock each.</summary>
        private void SpoolDuringOutage(params string[] uids)
        {
            _repo.Down = true;
            _svc.OnLockStateChanged(uids.Select(uid => new PuLockReport
            {
                Uid = uid,
                LockInfos = new[] { new LockInfoRef { Reason = 1, DateUtc = T0, Message = "Wrong component" } }
            }).ToList());
            _repo.Down = false;
        }

        /// <summary>The queued jobs, left in the queue.</summary>
        private IList<EnforcementJob> PeekJobs()
        {
            var jobs = _queue.TakeDue(DateTime.MaxValue);
            foreach (var job in jobs)
                _queue.Enqueue(job);
            return jobs;
        }

        private static EnforcementJob QueuedJob(LockRecord l, JobKind kind = JobKind.Apply)
        {
            return new EnforcementJob { Lock = l, Kind = kind, NotBeforeUtc = T0 };
        }

        // ---- reload ----

        [Test]
        public void Refresher_flushes_spool_then_reloads()
        {
            _spool.Append(Pu(0, "U1"));

            _refresher.Tick();

            Assert.That(_spool.ReadAll(), Is.Empty);
            Assert.That(_cache.Snapshot.Single().Id, Is.GreaterThan(0));
            Assert.That(_refresher.DbHealthy, Is.True);
            Assert.That(_repo.LoadActive().Single().TargetNorm, Is.EqualTo("U1"));
        }

        [Test]
        public void Refresher_keeps_cache_when_db_down()
        {
            _cache.Replace(new[] { Pu(5, "A") }, T0);
            _spool.Append(Pu(0, "U1"));
            _repo.Down = true;

            _refresher.Tick();

            Assert.That(_refresher.DbHealthy, Is.False);
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(5));
            Assert.That(_spool.ReadAll().Count, Is.EqualTo(1));
        }

        [Test]
        public void A_reload_replaces_the_cache_with_the_active_locks_and_stamps_the_time()
        {
            _repo.InsertActive(Pu(0, "A"));
            _repo.InsertActive(Pu(0, "B"));
            _repo.InsertReleased(Pu(0, "C"));
            _cache.Replace(new[] { Pu(99, "STALE") }, T0.AddHours(-1));
            _clock.UtcNow = T0.AddMinutes(30);

            _refresher.Tick();

            Assert.That(_cache.Snapshot.Select(l => l.TargetNorm).OrderBy(t => t), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(_cache.LoadedAtUtc, Is.EqualTo(T0.AddMinutes(30)));
            Assert.That(_refresher.DbHealthy, Is.True);
        }

        [Test]
        public void A_reload_with_nothing_to_flush_changes_no_queued_job()
        {
            _repo.InsertActive(Pu(0, "A"));
            var job = QueuedJob(Pu(1, "A"));
            _queue.Enqueue(job);

            _refresher.Tick();

            Assert.That(PeekJobs(), Is.EqualTo(new[] { job }));
        }

        [Test]
        public void DbHealthy_is_false_until_a_tick_reaches_the_database_and_follows_it_afterwards()
        {
            Assert.That(_refresher.DbHealthy, Is.False, "nothing has reached the database yet");

            _refresher.Tick();
            Assert.That(_refresher.DbHealthy, Is.True);

            _repo.Down = true;
            _refresher.Tick();
            Assert.That(_refresher.DbHealthy, Is.False);

            _repo.Down = false;
            _refresher.Tick();
            Assert.That(_refresher.DbHealthy, Is.True);
        }

        [Test]
        public void A_tick_with_the_database_down_logs_a_warning_and_does_not_throw()
        {
            _repo.Down = true;

            Assert.DoesNotThrow(() => _refresher.Tick());

            Assert.That(_log.Warnings, Is.Not.Empty);
            Assert.That(_log.Errors, Is.Empty);
        }

        // ---- flushing the spool ----

        [Test]
        public void Every_spooled_lock_is_inserted_with_its_fields()
        {
            var first = Pu(0, "U1");
            first.Source = LockSource.SetupCenter;
            first.CreatedBy = "SetupCenter";
            first.Reason = "Setup Center component mismatch: Wrong component";
            first.CreatedAtUtc = T0.AddMinutes(-3);
            _spool.Append(first);
            _spool.Append(Pu(0, "U2"));

            _refresher.Tick();

            var rows = _repo.LoadActive();
            Assert.That(rows.Select(r => r.TargetNorm), Is.EqualTo(new[] { "U1", "U2" }));
            var stored = rows[0];
            Assert.That(stored.Source, Is.EqualTo(LockSource.SetupCenter));
            Assert.That(stored.CreatedBy, Is.EqualTo("SetupCenter"));
            Assert.That(stored.Reason, Is.EqualTo("Setup Center component mismatch: Wrong component"));
            Assert.That(stored.CreatedAtUtc, Is.EqualTo(T0.AddMinutes(-3)));
            Assert.That(_cache.Snapshot.Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(_cache.Snapshot.All(l => l.Id > 0), Is.True);
        }

        [Test]
        public void A_flushed_lock_replaces_its_queued_id_zero_job_with_an_apply_job_for_the_stored_lock()
        {
            SpoolDuringOutage("U1");
            Assert.That(PeekJobs().Single().Lock.Id, Is.EqualTo(0), "the outage queued an Id 0 job");
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(0));

            _refresher.Tick();

            var stored = _repo.LoadActive().Single();
            var job = _queue.TakeDue(DateTime.MaxValue).Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
            Assert.That(job.Lock.Id, Is.EqualTo(stored.Id));
            Assert.That(job.Lock.Id, Is.Not.EqualTo(0));
            Assert.That(job.Lock.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(job.Lock.TargetNorm, Is.EqualTo("U1"));
            Assert.That(job.FailedAttempts, Is.EqualTo(0));
            Assert.That(job.NotBeforeUtc, Is.EqualTo(T0));
            var cached = _cache.Snapshot.Single();
            Assert.That(cached.Id, Is.EqualTo(stored.Id));
            Assert.That(job.Lock, Is.Not.SameAs(cached), "the job gets its own copy of the stored lock");
            Assert.That(_spool.ReadAll(), Is.Empty);
        }

        [Test]
        public void Each_flushed_lock_gets_exactly_one_apply_job_and_no_id_zero_job_survives()
        {
            SpoolDuringOutage("U1", "U2");
            Assert.That(PeekJobs().Count, Is.EqualTo(2));

            _refresher.Tick();

            var jobs = _queue.TakeDue(DateTime.MaxValue);
            var rows = _repo.LoadActive();
            Assert.That(jobs.Count, Is.EqualTo(2));
            Assert.That(jobs.All(j => j.Kind == JobKind.Apply && j.Lock.Id != 0), Is.True);
            Assert.That(jobs.Select(j => j.Lock.Id).OrderBy(id => id), Is.EqualTo(rows.Select(r => r.Id).OrderBy(id => id)));
        }

        [Test]
        public void Queued_jobs_of_other_locks_are_left_alone()
        {
            SpoolDuringOutage("U1");
            var release = QueuedJob(Pu(7, "OTHER"), JobKind.Release);
            var orphan = QueuedJob(Pu(0, "NOT-IN-THE-SPOOL"));
            _queue.Enqueue(release);
            _queue.Enqueue(orphan);

            _refresher.Tick();

            var jobs = _queue.TakeDue(DateTime.MaxValue);
            Assert.That(jobs, Does.Contain(release));
            Assert.That(jobs, Does.Contain(orphan));
            Assert.That(jobs.Count(j => j.Lock.Id == 0), Is.EqualTo(1), "only the job of the flushed lock was replaced");
            Assert.That(jobs.Count(j => j.Lock.TargetNorm == "U1"), Is.EqualTo(1));
            Assert.That(jobs.Single(j => j.Lock.TargetNorm == "U1").Lock.Id, Is.Not.EqualTo(0));
        }

        [Test]
        public void A_queued_id_zero_job_with_another_identity_is_left_alone()
        {
            _spool.Append(Pu(0, "U1"));
            var scoped = Pu(0, "U1");
            scoped.LinePath = "Line 2";
            var otherIdentity = QueuedJob(scoped);
            _queue.Enqueue(otherIdentity);

            _refresher.Tick();

            Assert.That(_queue.TakeDue(DateTime.MaxValue), Does.Contain(otherIdentity));
        }

        [TestCase(EnforceState.Pending, true)]
        [TestCase(EnforceState.Failed, true)]
        [TestCase(EnforceState.Applied, false)]
        [TestCase(EnforceState.NotNeeded, false)]
        public void A_duplicate_on_flush_queues_the_existing_row_only_while_its_enforcement_is_open(EnforceState state, bool queued)
        {
            SpoolDuringOutage("U1");
            var existing = Pu(0, "U1");
            existing.Source = LockSource.Manual;
            existing.EnforceState = state;
            var existingId = _repo.InsertActive(existing).Id;

            _refresher.Tick();

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1), "no second row");
            Assert.That(_spool.ReadAll(), Is.Empty);
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(existingId));
            var jobs = _queue.TakeDue(DateTime.MaxValue);
            Assert.That(jobs.Any(j => j.Lock.Id == 0), Is.False, "the Id 0 job is dropped either way");
            if (queued)
            {
                var job = jobs.Single();
                Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
                Assert.That(job.Lock.Id, Is.EqualTo(existingId));
            }
            else
            {
                Assert.That(jobs, Is.Empty);
            }
        }

        [Test]
        public void A_lock_inserted_by_a_concurrent_writer_is_the_lock_of_the_flush()
        {
            _spool.Append(Pu(0, "U1"));
            var winner = Pu(41, "U1");
            winner.Source = LockSource.Manual;
            _repo.FailNextInsertWithDuplicate(winner);

            _refresher.Tick();

            Assert.That(_repo.LoadActive().Single().Id, Is.EqualTo(41));
            Assert.That(_spool.ReadAll(), Is.Empty);
            Assert.That(_queue.TakeDue(DateTime.MaxValue).Single().Lock.Id, Is.EqualTo(41));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(41));
        }

        [Test]
        public void The_same_lock_spooled_twice_is_stored_and_queued_once()
        {
            _spool.Append(Pu(0, "U1"));
            _spool.Append(Pu(0, " u1 "));

            _refresher.Tick();

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
            Assert.That(_queue.TakeDue(DateTime.MaxValue).Count, Is.EqualTo(1));
            Assert.That(_spool.ReadAll(), Is.Empty);
            Assert.That(_refresher.DbHealthy, Is.True);
        }

        // ---- failures ----

        [Test]
        public void A_flush_that_fails_part_way_keeps_the_spool_the_cache_and_the_queue_and_the_next_tick_finishes_it()
        {
            SpoolDuringOutage("U1", "U2");
            var cachedBefore = _cache.Snapshot.ToList();
            var jobsBefore = PeekJobs();
            var inserts = 0;
            _repo.OnCall = c =>
            {
                if (c == "Insert" && ++inserts == 2)
                    _repo.Down = true;
            };

            _refresher.Tick();

            Assert.That(_refresher.DbHealthy, Is.False);
            Assert.That(_spool.ReadAll().Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1", "U2" }), "the whole spool is kept");
            Assert.That(_cache.Snapshot, Is.EqualTo(cachedBefore), "the cache is not touched");
            Assert.That(PeekJobs(), Is.EqualTo(jobsBefore), "the queued Id 0 jobs are not touched");
            _repo.OnCall = null;
            _repo.Down = false;
            Assert.That(_repo.LoadActive().Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1" }), "the first lock was inserted before the outage");

            _refresher.Tick();

            Assert.That(_refresher.DbHealthy, Is.True);
            Assert.That(_spool.ReadAll(), Is.Empty);
            var rows = _repo.LoadActive();
            Assert.That(rows.Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1", "U2" }), "U1 was found as a duplicate, not inserted twice");
            Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EqualTo(rows.Select(r => r.Id)));
            var jobs = _queue.TakeDue(DateTime.MaxValue);
            Assert.That(jobs.Count, Is.EqualTo(2));
            Assert.That(jobs.All(j => j.Kind == JobKind.Apply && j.Lock.Id != 0), Is.True);
            Assert.That(jobs.Select(j => j.Lock.Id).OrderBy(id => id), Is.EqualTo(rows.Select(r => r.Id)));
        }

        [Test]
        public void A_reload_that_fails_after_the_flush_keeps_the_spool_the_cache_and_the_queue()
        {
            SpoolDuringOutage("U1");
            var jobsBefore = PeekJobs();
            _repo.OnCall = c =>
            {
                if (c == "LoadActive")
                    _repo.Down = true;
            };

            _refresher.Tick();

            Assert.That(_refresher.DbHealthy, Is.False);
            Assert.That(_spool.ReadAll().Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(0));
            Assert.That(PeekJobs(), Is.EqualTo(jobsBefore));
            _repo.OnCall = null;
            _repo.Down = false;

            _refresher.Tick();

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single().Id, Is.GreaterThan(0));
            var job = _queue.TakeDue(DateTime.MaxValue).Single();
            Assert.That(job.Lock.Id, Is.GreaterThan(0));
            Assert.That(_spool.ReadAll(), Is.Empty);
        }

        [Test]
        public void An_unexpected_error_is_logged_and_leaves_the_health_the_spool_and_the_cache_alone()
        {
            _refresher.Tick();
            Assert.That(_refresher.DbHealthy, Is.True);
            _cache.Replace(new[] { Pu(5, "A") }, T0);
            _spool.Append(Pu(0, "U1"));
            _spool.FailRead = true;

            Assert.DoesNotThrow(() => _refresher.Tick());

            Assert.That(_log.Errors, Is.Not.Empty);
            Assert.That(_refresher.DbHealthy, Is.True, "an IO error says nothing about the database");
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(5));
            _spool.FailRead = false;
            Assert.That(_spool.ReadAll().Count, Is.EqualTo(1));
        }

        [Test]
        public void An_unexpected_error_while_the_database_is_unhealthy_does_not_make_it_healthy()
        {
            _repo.Down = true;
            _refresher.Tick();
            _repo.Down = false;
            _spool.FailRead = true;

            _refresher.Tick();

            Assert.That(_refresher.DbHealthy, Is.False);
            Assert.That(_log.Errors, Is.Not.Empty);
        }

        [Test]
        public void A_spool_that_cannot_be_cleared_keeps_the_cache_and_the_queue_and_the_next_tick_recovers()
        {
            SpoolDuringOutage("U1");
            var cachedBefore = _cache.Snapshot.ToList();
            var jobsBefore = PeekJobs();
            _spool.FailClear = true;

            Assert.DoesNotThrow(() => _refresher.Tick());

            Assert.That(_log.Errors, Is.Not.Empty);
            Assert.That(_spool.ReadAll().Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot, Is.EqualTo(cachedBefore));
            Assert.That(PeekJobs(), Is.EqualTo(jobsBefore));
            _spool.FailClear = false;

            _refresher.Tick();

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1), "the lock written by the failed tick is a duplicate now");
            Assert.That(_spool.ReadAll(), Is.Empty);
            var job = _queue.TakeDue(DateTime.MaxValue).Single();
            Assert.That(job.Lock.Id, Is.GreaterThan(0));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(job.Lock.Id));
        }

        // ---- write gate ----

        [Test]
        public void Tick_waits_while_another_thread_holds_the_write_gate()
        {
            SpoolDuringOutage("U1");

            WriteGateProbe.AssertWaits(
                _cache,
                () => _refresher.Tick(),
                () => _spool.ReadAll().Count == 1 && _repo.LoadActive().Count == 0 && _cache.Snapshot.Single().Id == 0);

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single().Id, Is.GreaterThan(0));
        }

        [Test]
        public void A_lock_created_while_a_tick_is_reading_the_database_is_not_dropped_by_that_tick()
        {
            _spool.Append(Pu(0, "U1"));
            Task create = null;
            var blocked = false;
            _repo.OnCall = c =>
            {
                if (c != "LoadActive" || create != null)
                    return;
                // A report for another UID arrives while the reload is under way: it must wait for the gate.
                create = Task.Run(() => _svc.OnLockStateChanged(new[]
                {
                    new PuLockReport { Uid = "U9", LockInfos = new[] { new LockInfoRef { Reason = 1, DateUtc = T0 } } }
                }));
                blocked = !create.Wait(TimeSpan.FromMilliseconds(300));
            };

            _refresher.Tick();

            Assert.That(blocked, Is.True, "the write did not wait for the tick to finish");
            Assert.That(create.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(_cache.Snapshot.Select(l => l.TargetNorm).OrderBy(t => t), Is.EqualTo(new[] { "U1", "U9" }));
            Assert.That(_cache.Snapshot.All(l => l.Id > 0), Is.True);
            var jobs = _queue.TakeDue(DateTime.MaxValue);
            Assert.That(jobs.Select(j => j.Lock.TargetNorm).OrderBy(t => t), Is.EqualTo(new[] { "U1", "U9" }));
        }

        // ---- construction ----

        [Test]
        public void The_constructor_rejects_missing_arguments()
        {
            Assert.Throws<ArgumentNullException>(() => new CacheRefresher(null, _spool, _cache, _queue, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new CacheRefresher(_repo, null, _cache, _queue, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new CacheRefresher(_repo, _spool, null, _queue, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new CacheRefresher(_repo, _spool, _cache, null, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new CacheRefresher(_repo, _spool, _cache, _queue, null, _log));
            Assert.Throws<ArgumentNullException>(() => new CacheRefresher(_repo, _spool, _cache, _queue, _clock, null));
        }

        /// <summary>A memory spool whose reads and clears can be made to fail with an IO error.</summary>
        private sealed class FaultySpool : ILockSpool
        {
            private readonly MemorySpool _inner = new MemorySpool();

            public bool FailRead { get; set; }

            public bool FailClear { get; set; }

            public void Append(LockRecord l)
            {
                _inner.Append(l);
            }

            public IList<LockRecord> ReadAll()
            {
                if (FailRead)
                    throw new IOException("The spool file cannot be read.");
                return _inner.ReadAll();
            }

            public void Clear()
            {
                if (FailClear)
                    throw new IOException("The spool file cannot be deleted.");
                _inner.Clear();
            }
        }
    }
}
