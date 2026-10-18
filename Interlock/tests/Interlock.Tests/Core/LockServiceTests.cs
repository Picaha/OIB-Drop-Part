using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Core.Text;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class LockServiceTests
    {
        private static readonly Principal Qa = Principal.ForUser("qa1", Role.Qa);
        private static readonly Principal Op = Principal.ForUser("op1", Role.Operator);

        private InMemoryLockRepository _repo;
        private InMemoryAuditRepository _audit;
        private FakeStationDirectory _stations;
        private MemorySpool _spool;
        private FakeClock _clock;
        private ListLog _log;
        private LockCache _cache;
        private ActionQueue _queue;
        private LockService _svc;

        [SetUp]
        public void SetUp()
        {
            _repo = new InMemoryLockRepository();
            _audit = new InMemoryAuditRepository();
            _stations = new FakeStationDirectory();
            _spool = new MemorySpool();
            _clock = new FakeClock();
            _log = new ListLog();
            _cache = new LockCache();
            _queue = new ActionQueue();
            _svc = new LockService(_repo, _audit, _cache, _queue, _stations, _spool, _clock, _log);
        }

        // ---- helpers ----

        private static CreateLockRequest PuReq(string uid, string reason = "Reel damaged")
        {
            return new CreateLockRequest { Type = LockType.Pu, Target = uid, Reason = reason };
        }

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
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private long CreatePu(string uid)
        {
            return _svc.CreateLock(Qa, PuReq(uid)).Lock.Id;
        }

        private IList<EnforcementJob> TakeJobs()
        {
            return _queue.TakeDue(_clock.UtcNow);
        }

        private static ErrorKind KindOf(TestDelegate call)
        {
            return Assert.Throws<InterlockException>(call).Kind;
        }

        // ---- create ----

        [Test]
        public void Create_inserts_caches_and_queues_apply()
        {
            var r = _svc.CreateLock(Qa, PuReq("U1"));

            Assert.That(r.Created, Is.True);
            Assert.That(r.Lock.Id, Is.GreaterThan(0));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(r.Lock.Id));
            Assert.That(_repo.Get(r.Lock.Id), Is.Not.Null);
            var job = TakeJobs().Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
            Assert.That(job.Lock.Id, Is.EqualTo(r.Lock.Id));
            Assert.That(r.Lock.Source, Is.EqualTo(LockSource.Manual));
        }

        [Test]
        public void Create_stores_the_trimmed_target_its_norm_and_the_creator()
        {
            _clock.UtcNow = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

            var l = _svc.CreateLock(Qa, PuReq("  u1 ", "Reel damaged")).Lock;

            var stored = _repo.Get(l.Id);
            foreach (var row in new[] { l, stored })
            {
                Assert.That(row.Type, Is.EqualTo(LockType.Pu));
                Assert.That(row.Target, Is.EqualTo("u1"));
                Assert.That(row.TargetNorm, Is.EqualTo("U1"));
                Assert.That(row.Reason, Is.EqualTo("Reel damaged"));
                Assert.That(row.CreatedBy, Is.EqualTo("qa1"));
                Assert.That(row.CreatedAtUtc, Is.EqualTo(_clock.UtcNow));
                Assert.That(row.Status, Is.EqualTo(LockStatus.Active));
                Assert.That(row.EnforceState, Is.EqualTo(EnforceState.Pending));
                Assert.That(row.Lanes, Is.Null);
            }
        }

        [Test]
        public void Create_stores_a_blank_line_path_as_null_and_a_set_one_trimmed()
        {
            var blank = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "C1", Reason = "r", LinePath = "   " });
            var set = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "C2", Reason = "r", LinePath = " Lines\\L1 " });

            Assert.That(blank.Lock.LinePath, Is.Null);
            Assert.That(_repo.Get(blank.Lock.Id).LinePath, Is.Null);
            Assert.That(set.Lock.LinePath, Is.EqualTo("Lines\\L1"));
            Assert.That(_repo.Get(set.Lock.Id).LinePath, Is.EqualTo("Lines\\L1"));
        }

        [Test]
        public void Api_key_create_has_api_source()
        {
            var r = _svc.CreateLock(Principal.ForApiKey("mes", false), PuReq("U1"));

            Assert.That(r.Lock.Source, Is.EqualTo(LockSource.Api));
            Assert.That(r.Lock.CreatedBy, Is.EqualTo("mes"));
        }

        [Test]
        public void An_operator_may_create_a_lock()
        {
            var r = _svc.CreateLock(Op, PuReq("U1"));

            Assert.That(r.Created, Is.True);
            Assert.That(r.Lock.CreatedBy, Is.EqualTo("op1"));
        }

        [Test]
        public void Create_returns_copies_and_queues_a_copy_of_the_record()
        {
            var r = _svc.CreateLock(Qa, PuReq("U1"));
            var cached = _cache.Snapshot.Single();
            var job = TakeJobs().Single();

            Assert.That(r.Lock, Is.Not.SameAs(cached));
            Assert.That(job.Lock, Is.Not.SameAs(cached));
            Assert.That(job.Lock, Is.Not.SameAs(r.Lock));

            job.Lock.EnforceState = EnforceState.Applied;
            job.Lock.Reason = "changed by the worker";

            Assert.That(cached.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(cached.Reason, Is.EqualTo("Reel damaged"));
        }

        [Test]
        public void Identical_active_lock_returns_existing()
        {
            var a = _svc.CreateLock(Qa, PuReq("U1"));
            var b = _svc.CreateLock(Qa, PuReq(" u1 "));

            Assert.That(b.Created, Is.False);
            Assert.That(b.Lock.Id, Is.EqualTo(a.Lock.Id));
            Assert.That(_repo.Query(new LockQuery()).Count, Is.EqualTo(1));
            Assert.That(TakeJobs().Count, Is.EqualTo(1), "no second Apply job for the existing lock");
        }

        [Test]
        public void The_existing_lock_is_returned_as_a_copy()
        {
            _svc.CreateLock(Qa, PuReq("U1"));

            var existing = _svc.CreateLock(Qa, PuReq("U1")).Lock;
            existing.Reason = "tampered";

            Assert.That(_cache.Snapshot.Single().Reason, Is.EqualTo("Reel damaged"));
        }

        [Test]
        public void Identical_active_lock_that_only_the_database_knows_is_returned()
        {
            _repo.InsertActive(Pu(7, "U1"));

            var r = _svc.CreateLock(Qa, PuReq("u1"));

            Assert.That(r.Created, Is.False);
            Assert.That(r.Lock.Id, Is.EqualTo(7));
            Assert.That(TakeJobs(), Is.Empty);
        }

        [Test]
        public void Locks_with_another_line_or_lanes_are_not_identical()
        {
            var l1 = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "C1", Reason = "r", LinePath = "Lines\\L1" });
            var l2 = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "C1", Reason = "r", LinePath = "Lines\\L2" });
            var plant = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "C1", Reason = "r" });
            var again = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "c1", Reason = "r", LinePath = " lines\\l1" });

            Assert.That(new[] { l1, l2, plant }.All(r => r.Created), Is.True);
            Assert.That(again.Created, Is.False);
            Assert.That(again.Lock.Id, Is.EqualTo(l1.Lock.Id));

            _stations.Stations = new List<StationRef> { new StationRef { StationPath = "Lines\\L1\\S1" } };
            var left = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Conveyor, Target = "Lines\\L1\\S1", Lanes = Lanes.Left, Reason = "r" });
            var right = _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Conveyor, Target = "Lines\\L1\\S1", Lanes = Lanes.Right, Reason = "r" });
            Assert.That(left.Created && right.Created, Is.True);
        }

        [Test]
        public void Concurrent_duplicate_insert_returns_existing()
        {
            _repo.FailNextInsertWithDuplicate(existing: Pu(9, "U1"));

            var r = _svc.CreateLock(Qa, PuReq("U1"));

            Assert.That(r.Created, Is.False);
            Assert.That(r.Lock.Id, Is.EqualTo(9));
            Assert.That(TakeJobs(), Is.Empty, "the creator of that lock queues its Apply job");
        }

        [Test]
        public void A_released_lock_does_not_block_a_new_one()
        {
            var first = CreatePu("U1");
            _svc.Release(Qa, first, "ok");

            var again = _svc.CreateLock(Qa, PuReq("U1"));

            Assert.That(again.Created, Is.True);
            Assert.That(again.Lock.Id, Is.Not.EqualTo(first));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(again.Lock.Id));
        }

        [Test]
        public void An_invalid_request_is_rejected_and_nothing_is_stored()
        {
            Assert.That(KindOf(() => _svc.CreateLock(Qa, PuReq("U1", "  "))), Is.EqualTo(ErrorKind.Validation));
            Assert.That(KindOf(() => _svc.CreateLock(Qa, PuReq(" "))), Is.EqualTo(ErrorKind.Validation));
            Assert.That(KindOf(() => _svc.CreateLock(Qa, new CreateLockRequest { Target = "U1", Reason = "r" })), Is.EqualTo(ErrorKind.Validation));

            Assert.That(_repo.Query(new LockQuery()), Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_conveyor_lock_needs_a_known_station()
        {
            var request = new CreateLockRequest { Type = LockType.Conveyor, Target = " lines\\l1\\s1 ", Lanes = Lanes.Both, Reason = "Board stuck" };

            Assert.That(KindOf(() => _svc.CreateLock(Qa, request)), Is.EqualTo(ErrorKind.Unavailable), "no station list yet");

            _stations.Stations = new List<StationRef> { new StationRef { LinePath = "Lines\\L1", StationPath = "Lines\\L1\\S2" } };
            Assert.That(KindOf(() => _svc.CreateLock(Qa, request)), Is.EqualTo(ErrorKind.Validation));
            Assert.That(_repo.Query(new LockQuery()), Is.Empty);

            _stations.Stations = new List<StationRef> { new StationRef { LinePath = "Lines\\L1", StationPath = "Lines\\L1\\S1" } };
            var r = _svc.CreateLock(Qa, request);

            Assert.That(r.Created, Is.True);
            Assert.That(r.Lock.Type, Is.EqualTo(LockType.Conveyor));
            Assert.That(r.Lock.Target, Is.EqualTo("lines\\l1\\s1"));
            Assert.That(r.Lock.TargetNorm, Is.EqualTo("LINES\\L1\\S1"));
            Assert.That(r.Lock.Lanes, Is.EqualTo(Lanes.Both));
        }

        [Test]
        public void Db_down_create_is_unavailable()
        {
            _repo.Down = true;

            var ex = Assert.Throws<InterlockException>(() => _svc.CreateLock(Qa, PuReq("U1")));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
            Assert.That(ex.Message, Is.EqualTo("Database unavailable"));
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Concurrent_identical_creates_make_one_lock()
        {
            var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => _svc.CreateLock(Qa, PuReq("U1")))).ToArray();
            Task.WaitAll(calls);

            Assert.That(calls.Count(c => c.Result.Created), Is.EqualTo(1));
            Assert.That(calls.Select(c => c.Result.Lock.Id).Distinct().Count(), Is.EqualTo(1));
            Assert.That(_repo.Query(new LockQuery()).Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        // ---- write gate ----

        /// <summary>
        /// Runs <paramref name="call"/> on another thread while this test holds the cache's write gate, and checks that it
        /// waits for the gate and has changed nothing (<paramref name="untouched"/>) until the gate is let go.
        /// </summary>
        private void AssertWaitsForTheWriteGate(Action call, Func<bool> untouched)
        {
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                lock (_cache.WriteGate)
                {
                    entered.Set();
                    release.Wait();
                }
            });

            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the holder never took the gate");

                var caller = Task.Run(call);
                Assert.That(caller.Wait(TimeSpan.FromMilliseconds(300)), Is.False, "the call did not wait for the write gate");
                Assert.That(untouched(), Is.True, "the call changed something before it had the write gate");

                release.Set();
                Assert.That(caller.Wait(TimeSpan.FromSeconds(5)), Is.True, "the call never finished after the gate was released");
            }
            finally
            {
                release.Set();
                holder.Wait();
            }
        }

        [Test]
        public void CreateLock_waits_while_another_thread_holds_the_write_gate()
        {
            AssertWaitsForTheWriteGate(
                () => _svc.CreateLock(Qa, PuReq("U1")),
                () => _repo.Query(new LockQuery()).Count == 0 && _cache.Snapshot.Count == 0 && _queue.Count == 0);

            Assert.That(_cache.Snapshot.Single().TargetNorm, Is.EqualTo("U1"));
        }

        [Test]
        public void CreateFromSetupCenter_waits_while_another_thread_holds_the_write_gate()
        {
            AssertWaitsForTheWriteGate(
                () => _svc.CreateFromSetupCenter("U1", "Setup Center component mismatch"),
                () => _repo.Query(new LockQuery()).Count == 0 && _cache.Snapshot.Count == 0 && _queue.Count == 0);

            Assert.That(_cache.Snapshot.Single().Source, Is.EqualTo(LockSource.SetupCenter));
        }

        [Test]
        public void Release_waits_while_another_thread_holds_the_write_gate()
        {
            var id = CreatePu("U1");

            AssertWaitsForTheWriteGate(
                () => _svc.Release(Qa, id, "ok"),
                () => _repo.Get(id).Status == LockStatus.Active && _cache.Snapshot.Count == 1);

            Assert.That(_repo.Get(id).Status, Is.EqualTo(LockStatus.Released));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Retry_waits_while_another_thread_holds_the_write_gate()
        {
            var id = CreatePu("U1");
            _repo.SetEnforceState(id, EnforceState.Failed, "x");

            AssertWaitsForTheWriteGate(
                () => _svc.Retry(Op, id),
                () => _repo.Get(id).EnforceState == EnforceState.Failed);

            Assert.That(_repo.Get(id).EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void A_lock_created_while_a_reload_holds_the_gate_is_not_dropped_by_that_reload()
        {
            Task<CreateLockResult> create;
            lock (_cache.WriteGate)
            {
                var loadedBeforeTheInsert = _repo.LoadActive();
                create = Task.Run(() => _svc.CreateLock(Qa, PuReq("U1")));
                Assert.That(create.Wait(TimeSpan.FromMilliseconds(200)), Is.False);

                _cache.Replace(loadedBeforeTheInsert, _clock.UtcNow);
            }

            Assert.That(create.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(create.Result.Lock.Id));
            Assert.That(TakeJobs().Single().Kind, Is.EqualTo(JobKind.Apply));
        }

        // ---- release ----

        [Test]
        public void Operator_cannot_release()
        {
            var id = CreatePu("U1");

            Assert.That(KindOf(() => _svc.Release(Op, id, "ok")), Is.EqualTo(ErrorKind.Forbidden));

            Assert.That(_repo.Get(id).Status, Is.EqualTo(LockStatus.Active));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
        }

        [Test]
        public void Release_marks_removes_cancels_and_queues_release()
        {
            var id = CreatePu("U1");

            var r = _svc.Release(Qa, id, "ok");

            Assert.That(r.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(r.ReleasedBy, Is.EqualTo("qa1"));
            Assert.That(r.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(TakeJobs().Select(j => j.Kind), Is.EqualTo(new[] { JobKind.Release }), "the Apply job was cancelled");
        }

        [Test]
        public void Release_returns_the_stored_row()
        {
            var id = CreatePu("U1");
            _clock.Advance(TimeSpan.FromMinutes(7));

            var r = _svc.Release(Qa, id, "Reel replaced");

            var stored = _repo.Get(id);
            Assert.That(r.Id, Is.EqualTo(id));
            Assert.That(r.ReleaseReason, Is.EqualTo("Reel replaced"));
            Assert.That(r.ReleasedAtUtc, Is.EqualTo(_clock.UtcNow));
            Assert.That(r.Reason, Is.EqualTo("Reel damaged"), "the lock's own reason is kept");
            Assert.That(stored.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(stored.ReleaseReason, Is.EqualTo("Reel replaced"));
            Assert.That(r, Is.Not.SameAs(stored));
        }

        [Test]
        public void Release_queues_a_copy_of_the_released_lock()
        {
            var id = CreatePu("U1");

            var r = _svc.Release(Qa, id, "ok");
            var job = TakeJobs().Single();

            Assert.That(job.Lock.Id, Is.EqualTo(id));
            Assert.That(job.Lock.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(job.Lock, Is.Not.SameAs(r));

            job.Lock.EnforceState = EnforceState.Applied;
            Assert.That(r.EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void Release_twice_is_invalid_state()
        {
            var id = CreatePu("U1");
            _svc.Release(Qa, id, "ok");
            TakeJobs();

            Assert.That(KindOf(() => _svc.Release(Qa, id, "ok")), Is.EqualTo(ErrorKind.InvalidState));

            Assert.That(_queue.Count, Is.EqualTo(0), "no second Release job");
            Assert.That(_repo.Get(id).ReleasedBy, Is.EqualTo("qa1"));
        }

        [Test]
        public void Release_needs_a_reason()
        {
            var id = CreatePu("U1");

            Assert.That(KindOf(() => _svc.Release(Qa, id, " ")), Is.EqualTo(ErrorKind.Validation));
            Assert.That(KindOf(() => _svc.Release(Qa, id, null)), Is.EqualTo(ErrorKind.Validation));
            Assert.That(_repo.Get(id).Status, Is.EqualTo(LockStatus.Active));
        }

        [Test]
        public void Release_checks_the_permission_before_anything_else()
        {
            Assert.That(KindOf(() => _svc.Release(Op, 12345, " ")), Is.EqualTo(ErrorKind.Forbidden));
        }

        [Test]
        public void Release_of_an_unknown_lock_is_not_found()
        {
            Assert.That(KindOf(() => _svc.Release(Qa, 12345, "ok")), Is.EqualTo(ErrorKind.NotFound));
        }

        [Test]
        public void An_api_key_releases_only_with_the_unlock_flag()
        {
            var id = CreatePu("U1");

            Assert.That(KindOf(() => _svc.Release(Principal.ForApiKey("mes", false), id, "ok")), Is.EqualTo(ErrorKind.Forbidden));

            var r = _svc.Release(Principal.ForApiKey("mes-admin", true), id, "ok");
            Assert.That(r.ReleasedBy, Is.EqualTo("mes-admin"));
        }

        [Test]
        public void Release_works_for_a_lock_the_cache_does_not_have()
        {
            var id = _repo.InsertActive(Pu(0, "U1")).Id;

            var r = _svc.Release(Qa, id, "ok");

            Assert.That(r.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(TakeJobs().Single().Kind, Is.EqualTo(JobKind.Release));
        }

        [Test]
        public void Db_down_release_is_unavailable_and_keeps_the_lock_cached()
        {
            var id = CreatePu("U1");
            TakeJobs();
            _repo.Down = true;

            var ex = Assert.Throws<InterlockException>(() => _svc.Release(Qa, id, "ok"));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
            Assert.That(ex.Message, Is.EqualTo("Database unavailable"));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_release_whose_read_back_fails_still_removes_the_lock_and_queues_the_release()
        {
            var id = CreatePu("U1");
            TakeJobs();
            var failing = new ReadBackFailsRepository(_repo);
            var svc = new LockService(failing, _audit, _cache, _queue, _stations, _spool, _clock, _log);

            Assert.That(KindOf(() => svc.Release(Qa, id, "ok")), Is.EqualTo(ErrorKind.Unavailable));

            Assert.That(_repo.Get(id).Status, Is.EqualTo(LockStatus.Released));
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(TakeJobs().Single().Kind, Is.EqualTo(JobKind.Release));
        }

        // ---- retry ----

        [Test]
        public void Retry_only_failed()
        {
            var id = CreatePu("U1");

            Assert.That(KindOf(() => _svc.Retry(Qa, id)), Is.EqualTo(ErrorKind.InvalidState));
            _repo.SetEnforceState(id, EnforceState.Failed, "x");

            Assert.That(_svc.Retry(Op, id).EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void Retry_of_an_active_failed_lock_resets_it_updates_the_cache_and_queues_apply()
        {
            var id = CreatePu("U1");
            TakeJobs();
            _repo.SetEnforceState(id, EnforceState.Failed, "OIB down");
            _cache.UpsertIfPresent(_repo.Get(id));

            var r = _svc.Retry(Op, id);

            Assert.That(r.Id, Is.EqualTo(id));
            Assert.That(r.Status, Is.EqualTo(LockStatus.Active));
            Assert.That(r.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(r.EnforceError, Is.Null);
            var stored = _repo.Get(id);
            Assert.That(stored.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(stored.EnforceError, Is.Null);
            var cached = _cache.Snapshot.Single();
            Assert.That(cached.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(cached.EnforceError, Is.Null);

            var job = TakeJobs().Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
            Assert.That(job.FailedAttempts, Is.EqualTo(0));
            Assert.That(job.Lock.Id, Is.EqualTo(id));
            Assert.That(job.Lock, Is.Not.SameAs(cached));
            Assert.That(job.Lock, Is.Not.SameAs(r));
        }

        [Test]
        public void Retry_of_released_failed_needs_release_permission()
        {
            var id = CreatePu("U1");
            _svc.Release(Qa, id, "ok");
            _repo.SetEnforceState(id, EnforceState.Failed, "OIB down");
            TakeJobs();

            Assert.That(KindOf(() => _svc.Retry(Op, id)), Is.EqualTo(ErrorKind.Forbidden));
            Assert.That(_repo.Get(id).EnforceState, Is.EqualTo(EnforceState.Failed), "a refused retry changes nothing");
            Assert.That(_queue.Count, Is.EqualTo(0));

            var r = _svc.Retry(Qa, id);

            Assert.That(r.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(r.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_repo.Get(id).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_repo.Get(id).EnforceError, Is.Null);
            var job = TakeJobs().Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Release));
            Assert.That(job.Lock.Id, Is.EqualTo(id));
            Assert.That(job.Lock.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(_cache.Snapshot, Is.Empty, "a released lock never goes back into the cache");
        }

        [Test]
        public void Retry_checks_the_state_before_the_permission()
        {
            var id = CreatePu("U1");
            _svc.Release(Qa, id, "ok");

            Assert.That(KindOf(() => _svc.Retry(Op, id)), Is.EqualTo(ErrorKind.InvalidState));
        }

        [Test]
        public void An_api_key_may_retry_an_active_lock_but_a_released_one_needs_the_unlock_flag()
        {
            var active = CreatePu("U1");
            var released = CreatePu("U2");
            _svc.Release(Qa, released, "ok");
            _repo.SetEnforceState(active, EnforceState.Failed, "x");
            _repo.SetEnforceState(released, EnforceState.Failed, "x");
            var key = Principal.ForApiKey("mes", false);

            Assert.That(_svc.Retry(key, active).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(KindOf(() => _svc.Retry(key, released)), Is.EqualTo(ErrorKind.Forbidden));
            Assert.That(_svc.Retry(Principal.ForApiKey("mes-admin", true), released).EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void Retry_of_an_unknown_lock_is_not_found()
        {
            Assert.That(KindOf(() => _svc.Retry(Qa, 12345)), Is.EqualTo(ErrorKind.NotFound));
        }

        [Test]
        public void Retry_does_not_put_a_lock_back_into_the_cache()
        {
            var id = _repo.InsertActive(Pu(0, "U1")).Id;
            _repo.SetEnforceState(id, EnforceState.Failed, "x");

            var r = _svc.Retry(Qa, id);

            Assert.That(r.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(TakeJobs().Single().Kind, Is.EqualTo(JobKind.Apply));
        }

        [Test]
        public void Db_down_retry_is_unavailable()
        {
            var id = CreatePu("U1");
            _repo.SetEnforceState(id, EnforceState.Failed, "x");
            TakeJobs();
            _repo.Down = true;

            var ex = Assert.Throws<InterlockException>(() => _svc.Retry(Qa, id));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
            Assert.That(ex.Message, Is.EqualTo("Database unavailable"));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        // ---- query / get ----

        [Test]
        public void Query_returns_the_matching_locks_to_any_role()
        {
            CreatePu("U1");
            CreatePu("U2");
            _svc.CreateLock(Qa, new CreateLockRequest { Type = LockType.Pn, Target = "C1", Reason = "Wrong part" });

            var pus = _svc.Query(Op, new LockQuery { Type = LockType.Pu });
            var all = _svc.Query(Principal.ForApiKey("mes", false), null);

            Assert.That(pus.Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(all.Count, Is.EqualTo(3));
        }

        [Test]
        public void Query_includes_released_locks_unless_filtered()
        {
            var id = CreatePu("U1");
            CreatePu("U2");
            _svc.Release(Qa, id, "ok");

            Assert.That(_svc.Query(Op, new LockQuery { Status = LockStatus.Active }).Select(l => l.TargetNorm), Is.EqualTo(new[] { "U2" }));
            Assert.That(_svc.Query(Op, new LockQuery { Status = LockStatus.Released }).Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1" }));
            Assert.That(_svc.Query(Op, new LockQuery()).Count, Is.EqualTo(2));
        }

        [Test]
        public void Get_returns_the_lock_with_its_actions()
        {
            var id = CreatePu("U1");
            var other = CreatePu("U2");
            _audit.AddAction(new ActionLogEntry { LockId = id, Action = Actions.LOCK_PU, Success = true, Attempt = 1 });
            _audit.AddAction(new ActionLogEntry { LockId = id, Action = Actions.UNLOCK_PU, Success = false, Attempt = 1 });
            _audit.AddAction(new ActionLogEntry { LockId = other, Action = Actions.LOCK_PU, Success = true, Attempt = 1 });

            var detail = _svc.Get(Op, id);

            Assert.That(detail.Lock.Id, Is.EqualTo(id));
            Assert.That(detail.Lock.TargetNorm, Is.EqualTo("U1"));
            Assert.That(detail.Actions.Select(a => a.Action), Is.EqualTo(new[] { Actions.LOCK_PU, Actions.UNLOCK_PU }));
        }

        [Test]
        public void Get_of_an_unknown_lock_is_not_found()
        {
            Assert.That(KindOf(() => _svc.Get(Qa, 12345)), Is.EqualTo(ErrorKind.NotFound));
        }

        [Test]
        public void Db_down_query_and_get_are_unavailable()
        {
            var id = CreatePu("U1");

            _repo.Down = true;
            Assert.That(KindOf(() => _svc.Query(Qa, new LockQuery())), Is.EqualTo(ErrorKind.Unavailable));
            Assert.That(KindOf(() => _svc.Get(Qa, id)), Is.EqualTo(ErrorKind.Unavailable));

            _repo.Down = false;
            _audit.Down = true;
            var ex = Assert.Throws<InterlockException>(() => _svc.Get(Qa, id));
            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
            Assert.That(ex.Message, Is.EqualTo("Database unavailable"));
        }

        // ---- guards ----

        [Test]
        public void A_null_principal_or_request_is_rejected()
        {
            var id = CreatePu("U1");

            Assert.Throws<ArgumentNullException>(() => _svc.CreateLock(null, PuReq("U2")));
            Assert.Throws<ArgumentNullException>(() => _svc.CreateLock(Qa, null));
            Assert.Throws<ArgumentNullException>(() => _svc.Release(null, id, "ok"));
            Assert.Throws<ArgumentNullException>(() => _svc.Retry(null, id));
            Assert.Throws<ArgumentNullException>(() => _svc.Query(null, new LockQuery()));
            Assert.Throws<ArgumentNullException>(() => _svc.Get(null, id));
        }

        [Test]
        public void The_constructor_rejects_missing_collaborators()
        {
            Assert.Throws<ArgumentNullException>(() => new LockService(null, _audit, _cache, _queue, _stations, _spool, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, null, _cache, _queue, _stations, _spool, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, _audit, null, _queue, _stations, _spool, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, _audit, _cache, null, _stations, _spool, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, _audit, _cache, _queue, null, _spool, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, _audit, _cache, _queue, _stations, null, _clock, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, _audit, _cache, _queue, _stations, _spool, null, _log));
            Assert.Throws<ArgumentNullException>(() => new LockService(_repo, _audit, _cache, _queue, _stations, _spool, _clock, null));
        }

        // ---- Setup Center ----

        [Test]
        public void CreateFromSetupCenter_creates_a_setup_center_pu_lock()
        {
            var l = _svc.CreateFromSetupCenter(" u1 ", "Setup Center component mismatch: wrong reel");

            Assert.That(l.Id, Is.GreaterThan(0));
            Assert.That(l.Type, Is.EqualTo(LockType.Pu));
            Assert.That(l.Target, Is.EqualTo("u1"));
            Assert.That(l.TargetNorm, Is.EqualTo("U1"));
            Assert.That(l.Reason, Is.EqualTo("Setup Center component mismatch: wrong reel"));
            Assert.That(l.Source, Is.EqualTo(LockSource.SetupCenter));
            Assert.That(l.CreatedBy, Is.EqualTo("SetupCenter"));
            Assert.That(l.Status, Is.EqualTo(LockStatus.Active));
            Assert.That(l.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_repo.Get(l.Id).Source, Is.EqualTo(LockSource.SetupCenter));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(l.Id));
            var job = TakeJobs().Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
            Assert.That(job.Lock, Is.Not.SameAs(_cache.Snapshot.Single()));
        }

        [Test]
        public void CreateFromSetupCenter_returns_the_existing_lock_for_the_same_pu()
        {
            var first = CreatePu("U1");
            TakeJobs();

            var l = _svc.CreateFromSetupCenter("u1", "Setup Center component mismatch");

            Assert.That(l.Id, Is.EqualTo(first));
            Assert.That(l.Source, Is.EqualTo(LockSource.Manual));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Query(new LockQuery()).Count, Is.EqualTo(1));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void CreateFromSetupCenter_needs_a_uid(string uid)
        {
            Assert.Throws<ArgumentException>(() => _svc.CreateFromSetupCenter(uid, "r"));
            Assert.That(_repo.Query(new LockQuery()), Is.Empty);
        }

        [Test]
        public void CreateFromSetupCenter_lets_a_database_outage_through_for_the_caller_to_spool()
        {
            _repo.Down = true;

            Assert.Throws<DbUnavailableException>(() => _svc.CreateFromSetupCenter("U1", "r"));

            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        // ---- test doubles ----

        /// <summary>Works like the wrapped repository, but <see cref="Get"/> fails after a lock was released through it.</summary>
        private sealed class ReadBackFailsRepository : ILockRepository
        {
            private readonly InMemoryLockRepository _inner;
            private bool _released;

            public ReadBackFailsRepository(InMemoryLockRepository inner)
            {
                _inner = inner;
            }

            public LockRecord Get(long id)
            {
                if (_released)
                    throw new DbUnavailableException("The database went away after the release.");
                return _inner.Get(id);
            }

            public void MarkReleased(long id, string by, DateTime atUtc, string reason)
            {
                _inner.MarkReleased(id, by, atUtc, reason);
                _released = true;
            }

            public IList<LockRecord> LoadActive() { return _inner.LoadActive(); }
            public IList<LockRecord> LoadPendingReleases() { return _inner.LoadPendingReleases(); }
            public LockRecord Insert(LockRecord l) { return _inner.Insert(l); }
            public LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes) { return _inner.FindActive(t, targetNorm, linePath, lanes); }
            public IList<LockRecord> Query(LockQuery q) { return _inner.Query(q); }
            public void SetEnforceState(long id, EnforceState s, string error) { _inner.SetEnforceState(id, s, error); }
            public void AddAppliedPu(long lockId, string uid, DateTime atUtc) { _inner.AddAppliedPu(lockId, uid, atUtc); }
            public IList<string> GetOpenAppliedPus(long lockId) { return _inner.GetOpenAppliedPus(lockId); }
            public void CloseAppliedPu(long lockId, string uid, DateTime atUtc) { _inner.CloseAppliedPu(lockId, uid, atUtc); }
            public bool IsUidCoveredByOtherActiveLock(string uidNorm, long exceptLockId) { return _inner.IsUidCoveredByOtherActiveLock(uidNorm, exceptLockId); }
            public DateTime? GetLatestReleaseUtc(LockType t, string targetNorm) { return _inner.GetLatestReleaseUtc(t, targetNorm); }
        }
    }
}
