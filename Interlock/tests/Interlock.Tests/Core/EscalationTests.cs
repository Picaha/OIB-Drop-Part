using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Core.Text;
using Interlock.Tests.Fakes;
using Interlock.Tests.Support;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class EscalationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);
        private static readonly Principal Qa = Principal.ForUser("qa1", Role.Qa);

        private InMemoryLockRepository _repo;
        private InMemoryAuditRepository _audit;
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
            _spool = new MemorySpool();
            _clock = new FakeClock { UtcNow = T0 };
            _log = new ListLog();
            _cache = new LockCache();
            _queue = new ActionQueue();
            _svc = NewService(_spool);
        }

        // ---- helpers ----

        private LockService NewService(ILockSpool spool)
        {
            return new LockService(_repo, _audit, _cache, _queue, new FakeStationDirectory(), spool, _clock, _log);
        }

        private static LockInfoRef Info(int reason, DateTime date, int source = 0, string message = null)
        {
            return new LockInfoRef { Source = source, Reason = reason, DateUtc = date, Message = message };
        }

        private static PuLockReport Report(string uid, params LockInfoRef[] infos)
        {
            return new PuLockReport { Uid = uid, LockInfos = infos };
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

        private IEnumerable<LockRecord> SetupCenterPuLocks()
        {
            return _repo.LoadActive().Where(l => l.Type == LockType.Pu && l.Source == LockSource.SetupCenter);
        }

        private IList<EnforcementJob> TakeJobs()
        {
            return _queue.TakeDue(_clock.UtcNow.AddHours(1));
        }

        // ---- EscalationFilter ----

        [Test]
        public void Own_locks_do_not_escalate()
        {
            Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(0, T0, source: 4), Info(28, T0, source: 9)), null), Is.Null);
        }

        [Test]
        public void Stale_mismatch_after_release_is_ignored()
        {
            Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(1, T0)), lastReleaseUtc: T0.AddMinutes(5)), Is.Null);
        }

        [Test]
        public void New_mismatch_after_release_escalates()
        {
            Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(1, T0.AddMinutes(10))), T0.AddMinutes(5)), Is.Not.Null);
        }

        [Test]
        public void A_mismatch_dated_exactly_at_the_release_is_not_new()
        {
            Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(1, T0.AddMinutes(5))), T0.AddMinutes(5)), Is.Null);
        }

        [Test]
        public void Without_a_release_any_mismatch_escalates()
        {
            var hit = EscalationFilter.FindNewMismatch(Report("U1", Info(1, T0)), null);

            Assert.That(hit, Is.Not.Null);
            Assert.That(hit.Reason, Is.EqualTo(1));
        }

        [Test]
        public void The_newest_mismatch_is_returned_whatever_the_order()
        {
            var oldest = Info(1, T0, message: "old");
            var newest = Info(1, T0.AddMinutes(20), message: "new");
            var middle = Info(1, T0.AddMinutes(10), message: "mid");

            var hit = EscalationFilter.FindNewMismatch(Report("U1", oldest, newest, middle), null);

            Assert.That(hit, Is.SameAs(newest));
        }

        [Test]
        public void Only_mismatches_newer_than_the_release_count_and_the_newest_of_them_wins()
        {
            var stale = Info(1, T0.AddMinutes(1), message: "stale");
            var fresh1 = Info(1, T0.AddMinutes(8), message: "fresh1");
            var fresh2 = Info(1, T0.AddMinutes(9), message: "fresh2");

            var hit = EscalationFilter.FindNewMismatch(Report("U1", fresh1, stale, fresh2), T0.AddMinutes(5));

            Assert.That(hit, Is.SameAs(fresh2));
        }

        [Test]
        public void A_mismatch_from_any_source_counts_but_other_reasons_do_not()
        {
            var mismatch = Info(1, T0, source: 7);
            var report = Report("U1", Info(0, T0.AddMinutes(30), source: 1), mismatch, Info(2, T0.AddMinutes(40), source: 1));

            Assert.That(EscalationFilter.FindNewMismatch(report, null), Is.SameAs(mismatch));
        }

        [Test]
        public void A_missing_report_or_info_list_has_no_mismatch()
        {
            Assert.That(EscalationFilter.FindNewMismatch(null, null), Is.Null);
            Assert.That(EscalationFilter.FindNewMismatch(new PuLockReport { Uid = "U1", LockInfos = null }, null), Is.Null);
            Assert.That(EscalationFilter.FindNewMismatch(Report("U1"), null), Is.Null);
        }

        [Test]
        public void A_missing_entry_in_the_info_list_is_skipped()
        {
            var mismatch = Info(1, T0);

            var hit = EscalationFilter.FindNewMismatch(Report("U1", null, mismatch, null), null);

            Assert.That(hit, Is.SameAs(mismatch));
        }

        // ---- LockService.OnLockStateChanged ----

        [Test]
        public void Mismatch_escalates_once()
        {
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(_repo.LoadActive().Count(l => l.Type == LockType.Pu && l.Source == LockSource.SetupCenter), Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void An_escalated_lock_is_a_stored_setup_center_pu_lock_that_is_cached_and_queued()
        {
            _svc.OnLockStateChanged(new[] { Report("  u1 ", Info(1, T0, message: "Wrong component")) });

            var stored = SetupCenterPuLocks().Single();
            Assert.That(stored.Id, Is.GreaterThan(0));
            Assert.That(stored.Type, Is.EqualTo(LockType.Pu));
            Assert.That(stored.Target, Is.EqualTo("u1"));
            Assert.That(stored.TargetNorm, Is.EqualTo("U1"));
            Assert.That(stored.Source, Is.EqualTo(LockSource.SetupCenter));
            Assert.That(stored.CreatedBy, Is.EqualTo("SetupCenter"));
            Assert.That(stored.Reason, Is.EqualTo("Setup Center component mismatch: Wrong component"));
            Assert.That(stored.Status, Is.EqualTo(LockStatus.Active));
            Assert.That(stored.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(stored.LinePath, Is.Null);
            Assert.That(stored.Lanes, Is.Null);
            Assert.That(stored.CreatedAtUtc, Is.EqualTo(T0));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(stored.Id));
            var job = TakeJobs().Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
            Assert.That(job.Lock.Id, Is.EqualTo(stored.Id));
            Assert.That(_spool.ReadAll(), Is.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void A_blank_message_leaves_the_reason_without_a_suffix(string message)
        {
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0, message: message)) });

            Assert.That(SetupCenterPuLocks().Single().Reason, Is.EqualTo("Setup Center component mismatch"));
        }

        [Test]
        public void The_reason_uses_the_message_of_the_newest_mismatch()
        {
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0, message: "old"), Info(1, T0.AddMinutes(3), message: "new")) });

            Assert.That(SetupCenterPuLocks().Single().Reason, Is.EqualTo("Setup Center component mismatch: new"));
        }

        [Test]
        public void Reports_that_are_not_mismatches_create_nothing()
        {
            _svc.OnLockStateChanged(new[] { Report("U1", Info(0, T0, source: 4), Info(28, T0, source: 9)) });

            Assert.That(_repo.LoadActive(), Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Each_uid_in_one_call_gets_its_own_lock()
        {
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)), Report("U2", Info(1, T0)), Report("U3", Info(0, T0)) });

            Assert.That(SetupCenterPuLocks().Select(l => l.TargetNorm).OrderBy(t => t), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(_queue.Count, Is.EqualTo(2));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void A_report_with_a_blank_uid_is_ignored_without_touching_the_database(string uid)
        {
            _repo.Down = true;

            _svc.OnLockStateChanged(new[] { Report(uid, Info(1, T0)) });

            Assert.That(_spool.ReadAll(), Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Warnings, Is.Empty);
        }

        [Test]
        public void A_missing_report_list_or_entry_is_ignored()
        {
            Assert.DoesNotThrow(() => _svc.OnLockStateChanged(null));
            Assert.DoesNotThrow(() => _svc.OnLockStateChanged(new PuLockReport[] { null }));
            Assert.DoesNotThrow(() => _svc.OnLockStateChanged(new[] { new PuLockReport { Uid = "U1", LockInfos = null } }));
            Assert.DoesNotThrow(() => _svc.OnLockStateChanged(new PuLockReport[0]));

            Assert.That(_repo.LoadActive(), Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_missing_report_does_not_stop_the_others()
        {
            _svc.OnLockStateChanged(new[] { null, Report("U1", Info(1, T0)) });

            Assert.That(SetupCenterPuLocks().Single().TargetNorm, Is.EqualTo("U1"));
        }

        [Test]
        public void A_uid_with_an_active_pu_lock_in_the_cache_is_skipped_without_touching_the_database()
        {
            _cache.Replace(new[] { Pu(5, "u1") }, T0);
            var dbCalls = new List<string>();
            _repo.OnCall = dbCalls.Add;

            _svc.OnLockStateChanged(new[] { Report(" U1 ", Info(1, T0)) });

            Assert.That(dbCalls, Is.Empty, "the database was used for a UID that is already locked");
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(5));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_uid_with_a_spooled_lock_in_the_cache_is_skipped_too()
        {
            _repo.Down = true;
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });
            var dbCalls = new List<string>();
            _repo.OnCall = dbCalls.Add;

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0.AddMinutes(1))) });

            Assert.That(dbCalls, Is.Empty);
            Assert.That(_spool.ReadAll().Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void An_active_pu_lock_that_only_the_database_knows_is_not_duplicated()
        {
            _repo.InsertActive(Pu(0, "U1"));

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0), "nothing new to enforce");
        }

        [Test]
        public void A_lock_of_another_type_for_the_same_target_does_not_block_the_escalation()
        {
            var feeder = Pu(5, "U1");
            feeder.Type = LockType.Feeder;
            _cache.Replace(new[] { feeder }, T0);

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(SetupCenterPuLocks().Single().TargetNorm, Is.EqualTo("U1"));
        }

        [Test]
        public void A_mismatch_older_than_the_last_release_is_ignored_and_a_newer_one_escalates_again()
        {
            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });
            var first = _cache.Snapshot.Single();
            _clock.Advance(TimeSpan.FromMinutes(5));
            _svc.Release(Qa, first.Id, "checked");
            TakeJobs();

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });
            Assert.That(_repo.LoadActive(), Is.Empty, "the old mismatch must not lock the reel again");
            Assert.That(_cache.Snapshot, Is.Empty);

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0.AddMinutes(10))) });
            var second = SetupCenterPuLocks().Single();
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(second.Id));
        }

        [Test]
        public void The_release_time_is_read_for_the_normalised_uid()
        {
            _repo.InsertReleased(new LockRecord
            {
                Type = LockType.Pu,
                Target = "u1",
                TargetNorm = "U1",
                Source = LockSource.Manual,
                CreatedBy = "x",
                ReleasedAtUtc = T0.AddMinutes(5),
                EnforceState = EnforceState.Applied
            });

            _svc.OnLockStateChanged(new[] { Report(" u1 ", Info(1, T0)) });

            Assert.That(_repo.LoadActive(), Is.Empty);
        }

        // ---- database down ----

        [Test]
        public void Db_down_spools_caches_and_queues()
        {
            _repo.Down = true;

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(_spool.ReadAll().Single().TargetNorm, Is.EqualTo("U1"));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(0));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_spooled_lock_has_id_zero_the_setup_center_fields_and_an_apply_job()
        {
            _repo.Down = true;

            _svc.OnLockStateChanged(new[] { Report(" u1 ", Info(1, T0, message: "Wrong component")) });

            var spooled = _spool.ReadAll().Single();
            Assert.That(spooled.Id, Is.EqualTo(0));
            Assert.That(spooled.Type, Is.EqualTo(LockType.Pu));
            Assert.That(spooled.Target, Is.EqualTo("u1"));
            Assert.That(spooled.TargetNorm, Is.EqualTo("U1"));
            Assert.That(spooled.Source, Is.EqualTo(LockSource.SetupCenter));
            Assert.That(spooled.CreatedBy, Is.EqualTo("SetupCenter"));
            Assert.That(spooled.Reason, Is.EqualTo("Setup Center component mismatch: Wrong component"));
            Assert.That(spooled.Status, Is.EqualTo(LockStatus.Active));
            Assert.That(spooled.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(spooled.CreatedAtUtc, Is.EqualTo(T0));

            var cached = _cache.Snapshot.Single();
            Assert.That(cached.Id, Is.EqualTo(0));
            Assert.That(cached.TargetNorm, Is.EqualTo("U1"));
            Assert.That(cached.Source, Is.EqualTo(LockSource.SetupCenter));
            Assert.That(cached, Is.Not.SameAs(spooled));

            var job = TakeJobs().Single();
            Assert.That(job.Kind, Is.EqualTo(JobKind.Apply));
            Assert.That(job.Lock.Id, Is.EqualTo(0));
            Assert.That(job.Lock.TargetNorm, Is.EqualTo("U1"));
            Assert.That(job.Lock, Is.Not.SameAs(cached), "the job gets its own copy");
            Assert.That(job.NotBeforeUtc, Is.EqualTo(T0));
        }

        [Test]
        public void Db_down_logs_a_warning()
        {
            _repo.Down = true;

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(_log.Warnings.Any(w => w.Contains("U1") && w.IndexOf("spool", StringComparison.OrdinalIgnoreCase) >= 0), Is.True,
                "expected a warning that names the UID and the spool, got: " + string.Join(" | ", _log.Warnings));
            Assert.That(_log.Errors, Is.Empty);
        }

        [Test]
        public void With_the_database_down_a_mismatch_with_an_unknown_release_time_is_still_spooled()
        {
            _repo.InsertReleased(new LockRecord
            {
                Type = LockType.Pu,
                Target = "U1",
                TargetNorm = "U1",
                Source = LockSource.Manual,
                CreatedBy = "x",
                ReleasedAtUtc = T0.AddMinutes(5),
                EnforceState = EnforceState.Applied
            });
            _repo.Down = true;

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(_spool.ReadAll().Count, Is.EqualTo(1));
        }

        [Test]
        public void The_database_going_down_between_the_release_lookup_and_the_insert_still_spools()
        {
            _repo.OnCall = c =>
            {
                if (c == "FindActive")
                    _repo.Down = true;
            };

            _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(_spool.ReadAll().Single().TargetNorm, Is.EqualTo("U1"));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(0));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void Concurrent_identical_reports_make_one_lock()
        {
            var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) }))).ToArray();
            Task.WaitAll(calls);

            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void Concurrent_identical_reports_with_the_database_down_spool_one_lock()
        {
            _repo.Down = true;

            // Many rounds with a common start: the race is between the threads that all saw an empty cache.
            for (var round = 0; round < 40; round++)
            {
                var spool = new MemorySpool();
                var cache = new LockCache();
                var queue = new ActionQueue();
                var svc = new LockService(_repo, _audit, cache, queue, new FakeStationDirectory(), spool, _clock, _log);
                var start = new ManualResetEventSlim();

                var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });
                })).ToArray();
                start.Set();
                Task.WaitAll(calls);

                Assert.That(spool.ReadAll().Count, Is.EqualTo(1), "round " + round + ": spooled");
                Assert.That(cache.Snapshot.Count, Is.EqualTo(1), "round " + round + ": cached");
                Assert.That(queue.Count, Is.EqualTo(1), "round " + round + ": queued");
            }
        }

        [Test]
        public void The_spool_is_written_under_the_write_gate()
        {
            _repo.Down = true;
            var probe = new GateProbeSpool(_cache.WriteGate);
            var svc = NewService(probe);

            svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) });

            Assert.That(probe.Appends, Is.EqualTo(1));
            Assert.That(probe.AllUnderGate, Is.True, "the spool was written without the write gate");
        }

        // ---- never throws ----

        [Test]
        public void A_spool_that_fails_is_logged_and_swallowed_and_leaves_no_half_made_lock()
        {
            _repo.Down = true;
            var svc = NewService(new ThrowingSpool(_ => true));

            Assert.DoesNotThrow(() => svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) }));

            Assert.That(_cache.Snapshot, Is.Empty, "the cache must not hold a lock that was never spooled");
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Errors, Is.Not.Empty);
        }

        [Test]
        public void A_report_that_fails_does_not_stop_the_next_one()
        {
            _repo.Down = true;
            var svc = NewService(new ThrowingSpool(l => l.TargetNorm == "U1"));

            Assert.DoesNotThrow(() => svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)), Report("U2", Info(1, T0)) }));

            Assert.That(_cache.Snapshot.Single().TargetNorm, Is.EqualTo("U2"));
            Assert.That(_log.Errors, Is.Not.Empty);
        }

        // ---- write gate ----

        [Test]
        public void OnLockStateChanged_waits_for_the_write_gate_while_the_database_is_up()
        {
            WriteGateProbe.AssertWaits(
                _cache,
                () => _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) }),
                () => _repo.LoadActive().Count == 0 && _cache.Snapshot.Count == 0 && _queue.Count == 0);

            Assert.That(_cache.Snapshot.Single().Source, Is.EqualTo(LockSource.SetupCenter));
        }

        [Test]
        public void OnLockStateChanged_waits_for_the_write_gate_before_it_spools()
        {
            _repo.Down = true;

            WriteGateProbe.AssertWaits(
                _cache,
                () => _svc.OnLockStateChanged(new[] { Report("U1", Info(1, T0)) }),
                () => _spool.ReadAll().Count == 0 && _cache.Snapshot.Count == 0 && _queue.Count == 0);

            Assert.That(_spool.ReadAll().Single().TargetNorm, Is.EqualTo("U1"));
            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(0));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        /// <summary>Records whether each append happened while the calling thread held the given gate.</summary>
        private sealed class GateProbeSpool : ILockSpool
        {
            private readonly object _gate;

            public GateProbeSpool(object gate)
            {
                _gate = gate;
                AllUnderGate = true;
            }

            public int Appends { get; private set; }

            public bool AllUnderGate { get; private set; }

            public void Append(LockRecord l)
            {
                Appends++;
                AllUnderGate &= Monitor.IsEntered(_gate);
            }

            public IList<LockRecord> ReadAll()
            {
                return new List<LockRecord>();
            }

            public void Clear()
            {
            }
        }

        /// <summary>Appends fine unless the predicate says the lock must fail, then throws an IO error.</summary>
        private sealed class ThrowingSpool : ILockSpool
        {
            private readonly Func<LockRecord, bool> _fails;

            public ThrowingSpool(Func<LockRecord, bool> fails)
            {
                _fails = fails;
            }

            public void Append(LockRecord l)
            {
                if (_fails(l))
                    throw new IOException("The spool disk is full.");
            }

            public IList<LockRecord> ReadAll()
            {
                return new List<LockRecord>();
            }

            public void Clear()
            {
            }
        }
    }
}
