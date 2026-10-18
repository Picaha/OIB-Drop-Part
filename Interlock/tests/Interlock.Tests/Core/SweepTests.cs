using System;
using System.Linq;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    /// <summary>PN, MPN and SUPPLIER locks: the worker sweeps the mounted reels, records what it locked and unlocks only what no other lock covers.</summary>
    [TestFixture]
    public class SweepTests
    {
        private const string L1 = "Lines\\L1";
        private const string L2 = "Lines\\L2";

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

        private LockRecord MakeLock(long id, LockType type, string target, string line)
        {
            return new LockRecord
            {
                Id = id,
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                LinePath = line,
                Reason = "Reason " + id,
                Source = LockSource.Manual,
                CreatedBy = "tester",
                CreatedAtUtc = _clock.UtcNow,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private LockRecord Pn(long id, string pn, string line = null)
        {
            return MakeLock(id, LockType.Pn, pn, line);
        }

        private LockRecord Mpn(long id, string mpn, string line = null)
        {
            return MakeLock(id, LockType.Mpn, mpn, line);
        }

        private LockRecord Supplier(long id, string supplier, string line = null)
        {
            return MakeLock(id, LockType.Supplier, supplier, line);
        }

        private LockRecord Pu(long id, string uid)
        {
            return MakeLock(id, LockType.Pu, uid, null);
        }

        private static MachineRef M(string machineId, string line)
        {
            return new MachineRef { MachineId = machineId, LinePath = line };
        }

        private static MountedPu Mounted(string machineId, string uid, string pn = null, string mpn = null, string supplier = null, string line = null)
        {
            return new MountedPu
            {
                MachineId = machineId,
                Pu = new PuInfo { Uid = uid, ComponentName = pn, Mpn = mpn, Supplier = supplier, LinePath = line }
            };
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

        // ---- apply: which reels are locked ----

        [Test]
        public void Sweep_locks_matching_mounted_pus_in_scope()
        {
            _sc.Machines.AddRange(new[] { M("m1", L1), M("m2", L2) });
            _sc.MountedPus.AddRange(new[]
            {
                Mounted("m1", "A", pn: "C1", line: L1),
                Mounted("m1", "B", pn: "C2", line: L1),
                Mounted("m2", "C", pn: "C1", line: L2)
            });

            Enqueue(Pn(1, "C1", line: L1), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.Get(1).EnforceError, Is.Null);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Applied));
        }

        [Test]
        public void Sweep_with_no_match_is_applied_without_lock_call()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "B", pn: "C2"));

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1" }));
            Assert.That(_sc.Calls.Any(c => c.StartsWith("LockPackagingUnits")), Is.False);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo("SWEEP"));
            Assert.That(row.Success, Is.True);
            Assert.That(row.RequestJson, Is.EqualTo("{\"machines\":1,\"matched\":[]}"));
        }

        [Test]
        public void Sweep_with_no_machine_in_scope_skips_the_mounted_query_and_is_applied()
        {
            _sc.Machines.Add(M("m2", L2));
            _sc.MountedPus.Add(Mounted("m2", "A", pn: "C1"));

            Enqueue(Pn(1, "C1", line: L1), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo("SWEEP"));
            Assert.That(row.RequestJson, Is.EqualTo("{\"machines\":0,\"matched\":[]}"));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Mpn_and_supplier_sweep_use_their_fields()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.AddRange(new[]
            {
                Mounted("m1", "A", mpn: "M1"),
                Mounted("m1", "B", supplier: "S1"),
                Mounted("m1", "C", pn: "M1")
            });

            Enqueue(Mpn(1, "M1"), JobKind.Apply);
            _worker.RunDue();
            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }), "MPN lock takes only the reel with that MPN");
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A" }));

            _sc.Calls.Clear();
            Enqueue(Supplier(2, "S1"), JobKind.Apply);
            _worker.RunDue();
            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:B" }), "SUPPLIER lock takes only the reel from that supplier");
            Assert.That(_repo.GetOpenAppliedPus(2), Is.EqualTo(new[] { "B" }));

            _sc.Calls.Clear();
            Enqueue(Pn(3, "M1"), JobKind.Apply);
            _worker.RunDue();
            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:C" }), "PN lock reads the part number, not the MPN");
            Assert.That(_repo.GetOpenAppliedPus(3), Is.EqualTo(new[] { "C" }));

            Assert.That(_repo.Query(new LockQuery()).Select(l => l.EnforceState), Is.All.EqualTo(EnforceState.Applied));
        }

        [Test]
        public void Sweep_matches_ignoring_case_and_blanks_and_locks_every_matching_reel_in_one_call()
        {
            _sc.Machines.AddRange(new[] { M("m1", L1), M("m2", L1) });
            _sc.MountedPus.AddRange(new[]
            {
                Mounted("m1", "A", pn: " c1 "),
                Mounted("m2", "B", pn: "C1"),
                Mounted("m2", "D", pn: "C2")
            });

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1,m2", "LockPackagingUnits:A,B" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A", "B" }));
        }

        [Test]
        public void Sweep_locks_each_uid_once_and_ignores_reels_without_a_uid()
        {
            _sc.Machines.AddRange(new[] { M("m1", L1), M("m2", L1) });
            _sc.MountedPus.AddRange(new[]
            {
                Mounted("m1", "A", pn: "C1"),
                Mounted("m2", " a ", pn: "C1"),
                Mounted("m2", null, pn: "C1"),
                Mounted("m2", "  ", pn: "C1"),
                Mounted("m2", "B", pn: "C1")
            });

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls.Last(), Is.EqualTo("LockPackagingUnits:A,B"));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A", "B" }));
        }

        // ---- apply: which machines are swept ----

        [Test]
        public void Lock_without_a_line_sweeps_every_machine_once_in_order()
        {
            _sc.Machines.AddRange(new[] { M("m2", L2), M("m1", L1), M("m2", L2), M("m3", null) });
            _sc.MountedPus.AddRange(new[]
            {
                Mounted("m1", "A", pn: "C1"),
                Mounted("m2", "B", pn: "C1"),
                Mounted("m3", "C", pn: "C1")
            });

            Enqueue(Pn(1, "C1", line: "  "), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m2,m1,m3", "LockPackagingUnits:A,B,C" }));
            Assert.That(_audit.Actions.Last().RequestJson, Is.EqualTo("{\"machines\":3,\"matched\":[\"A\",\"B\",\"C\"]}"));
        }

        [Test]
        public void Machine_without_a_line_is_swept_by_a_line_scoped_lock()
        {
            _sc.Machines.AddRange(new[] { M("m1", L1), M("m2", null), M("m3", "   "), M("m4", L2) });
            _sc.MountedPus.AddRange(new[]
            {
                Mounted("m1", "A", pn: "C1"),
                Mounted("m2", "B", pn: "C1"),
                Mounted("m3", "C", pn: "C1"),
                Mounted("m4", "D", pn: "C1")
            });

            Enqueue(Pn(1, "C1", line: L1), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1,m2,m3", "LockPackagingUnits:A,B,C" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A", "B", "C" }));
        }

        [Test]
        public void Line_scope_ignores_case_and_surrounding_blanks()
        {
            _sc.Machines.AddRange(new[] { M("m1", " lines\\l1 "), M("m2", L2) });
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));

            Enqueue(Pn(1, "C1", line: "LINES\\L1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }));
        }

        [Test]
        public void Machine_without_an_id_is_not_queried()
        {
            _sc.Machines.AddRange(new[] { M(null, L1), M("  ", L1), M("m1", L1) });
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }));
            Assert.That(_audit.Actions.Last().RequestJson, Is.EqualTo("{\"machines\":1,\"matched\":[\"A\"]}"));
        }

        // ---- apply: action log ----

        [Test]
        public void Sweep_writes_a_lock_pu_row_and_a_sweep_row()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.AddRange(new[] { Mounted("m1", "A", pn: "C1"), Mounted("m1", "B", pn: "C1") });

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_audit.Actions.Select(a => a.Action), Is.EqualTo(new[] { Actions.LOCK_PU, Actions.SWEEP }));
            Assert.That(_audit.Actions.All(a => a.LockId == 1 && a.Success && a.Error == null && a.Attempt == 1 && a.CreatedAtUtc == _clock.UtcNow), Is.True);
            Assert.That(_audit.Actions[0].RequestJson, Is.EqualTo("{\"uids\":[\"A\",\"B\"]}"));
            Assert.That(_audit.Actions[1].RequestJson, Is.EqualTo("{\"machines\":1,\"matched\":[\"A\",\"B\"]}"));
        }

        [Test]
        public void Request_json_escapes_quotes_and_backslashes_in_uids()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "A\"B\\C", pn: "C1"));

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_audit.Actions.Last().RequestJson, Is.EqualTo("{\"machines\":1,\"matched\":[\"A\\\"B\\\\C\"]}"));
        }

        // ---- apply: failures ----

        [Test]
        public void Failing_lock_call_retries_the_sweep_and_records_no_applied_pu()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));
            _sc.ThrowAlways = true;

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_audit.Actions.Select(a => a.Action), Is.EqualTo(new[] { Actions.LOCK_PU, Actions.SWEEP }));
            Assert.That(_audit.Actions.Select(a => a.Success), Is.All.False);
            Assert.That(_audit.Actions.Select(a => a.Attempt), Is.All.EqualTo(1));
            Assert.That(_audit.Actions.Select(a => a.Error), Has.All.Contains("LockPackagingUnits failed"));
            Assert.That(_audit.Actions.Last().RequestJson, Is.EqualTo("{\"machines\":1,\"matched\":[\"A\"]}"));

            // the retry sweeps again from the start and records the reel once the lock call works
            _sc.ThrowAlways = false;
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls.Count, Is.EqualTo(6));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A" }));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_repo.Get(1).EnforceError, Is.Null);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_audit.Actions.Select(a => a.Success), Is.EqualTo(new[] { false, false, true, true }));
            Assert.That(_audit.Actions.Select(a => a.Attempt), Is.EqualTo(new[] { 1, 1, 2, 2 }));
        }

        [Test]
        public void Failing_machine_query_writes_a_failed_sweep_row_and_retries()
        {
            _sc.OnCall = c =>
            {
                if (c == "GetMachines")
                    throw new OibCallException("Setup Center unreachable");
            };

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines" }));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo(Actions.SWEEP));
            Assert.That(row.Success, Is.False);
            Assert.That(row.Error, Is.EqualTo("Setup Center unreachable"));
            Assert.That(row.RequestJson, Is.EqualTo("{\"machines\":0,\"matched\":[]}"));
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void Sweep_that_keeps_failing_ends_failed_with_an_alarm_and_records_nothing()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));
            _sc.ThrowAlways = true;

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();
            _clock.Advance(TimeSpan.FromSeconds(30));
            _worker.RunDue();
            _clock.Advance(TimeSpan.FromMinutes(2));
            _worker.RunDue();

            Assert.That(_sc.Calls.Count(c => c.StartsWith("LockPackagingUnits")), Is.EqualTo(4));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_repo.Get(1).EnforceError, Does.Contain("LockPackagingUnits failed"));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_alarms.Recent(1)[0], Does.Contain("Lock #1 FAILED"));
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_audit.Actions.Count(a => a.Action == Actions.SWEEP), Is.EqualTo(4));
        }

        [Test]
        public void Sweep_apply_for_a_lock_that_left_the_cache_makes_no_oib_call()
        {
            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _cache.Remove(1);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
        }

        [Test]
        public void Failing_sweep_is_not_retried_when_the_lock_left_the_cache_during_the_call()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));
            _sc.ThrowAlways = true;
            _sc.OnCall = c =>
            {
                if (c.StartsWith("LockPackagingUnits"))
                    _cache.Remove(1);
            };

            Enqueue(Pn(1, "C1"), JobKind.Apply);
            _worker.RunDue();
            _clock.Advance(TimeSpan.FromMinutes(10));
            _worker.RunDue();

            Assert.That(_sc.Calls.Count(c => c.StartsWith("LockPackagingUnits")), Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_alarms.Recent(5), Is.Empty);
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_log.Infos, Has.Some.Contains("no retry"));
        }

        [Test]
        public void Db_down_during_a_sweep_does_not_stop_the_oib_lock()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));
            _repo.Down = true;
            _audit.Down = true;
            Enqueue(Pn(1, "C1"), JobKind.Apply);

            Assert.DoesNotThrow(() => _worker.RunDue());

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Warnings, Is.Not.Empty);
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_alarms.Recent(5), Is.Empty);
        }

        [Test]
        public void Lock_without_an_id_is_swept_but_nothing_is_written_to_the_database()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.Add(Mounted("m1", "A", pn: "C1"));
            var spooled = Pn(0, "C1");
            _cache.Upsert(spooled.Clone());
            _queue.Enqueue(new EnforcementJob { Lock = spooled.Clone(), Kind = JobKind.Apply, NotBeforeUtc = _clock.UtcNow });

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(0), Is.Empty);
            Assert.That(_repo.Query(new LockQuery()), Is.Empty);
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_cache.Snapshot.Single().EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        // ---- release ----

        [Test]
        public void Release_unlocks_only_uncovered_uids_and_closes_all()
        {
            _repo.AddAppliedPu(1, "A", _clock.UtcNow);
            _repo.AddAppliedPu(1, "B", _clock.UtcNow);
            _repo.InsertActive(Pu(2, "B")); // B is still covered by PU lock #2

            Enqueue(Pn(1, "C1"), JobKind.Release);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_queue.Count, Is.EqualTo(0));
            var row = _audit.Actions.Single();
            Assert.That(row.Action, Is.EqualTo(Actions.UNLOCK_PU));
            Assert.That(row.Success, Is.True);
            Assert.That(row.RequestJson, Is.EqualTo("{\"uids\":[\"A\"]}"));
        }

        [Test]
        public void Release_keeps_a_uid_that_another_sweep_lock_still_covers()
        {
            _repo.AddAppliedPu(1, "A", _clock.UtcNow);
            _repo.AddAppliedPu(1, "B", _clock.UtcNow);
            var other = _repo.InsertActive(Mpn(2, "M1"));
            _repo.AddAppliedPu(other.Id, "B", _clock.UtcNow);

            Enqueue(Pn(1, "C1"), JobKind.Release);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_repo.GetOpenAppliedPus(2), Is.EqualTo(new[] { "B" }), "the other lock keeps its own record");
        }

        [Test]
        public void Release_of_a_lock_with_every_uid_covered_makes_no_oib_call()
        {
            _repo.AddAppliedPu(1, "A", _clock.UtcNow);
            _repo.InsertActive(Pu(2, "A"));

            Enqueue(Pn(1, "C1"), JobKind.Release);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Release_of_a_lock_that_locked_nothing_is_applied_without_a_call()
        {
            Enqueue(Pn(1, "C1"), JobKind.Release);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Release_of_a_lock_without_an_id_needs_no_database()
        {
            var spooled = Pn(0, "C1");
            spooled.Status = LockStatus.Released;
            _queue.Enqueue(new EnforcementJob { Lock = spooled, Kind = JobKind.Release, NotBeforeUtc = _clock.UtcNow });
            _repo.Down = true;

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_alarms.Recent(5), Is.Empty);
        }

        [Test]
        public void Release_unlocks_a_uid_once_with_its_stored_text_and_closes_every_row()
        {
            _repo.AddAppliedPu(1, "a-1", _clock.UtcNow);
            _repo.AddAppliedPu(1, "A-1", _clock.UtcNow);
            _repo.AddAppliedPu(1, "b-2", _clock.UtcNow);

            Enqueue(Pn(1, "C1"), JobKind.Release);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:a-1,b-2" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
        }

        [Test]
        public void Release_whose_unlock_fails_keeps_the_rows_open_and_retries()
        {
            _repo.AddAppliedPu(1, "A", _clock.UtcNow);
            _sc.ThrowAlways = true;

            Enqueue(Pn(1, "C1"), JobKind.Release);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A" }));
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Pending));
            var failed = _audit.Actions.Single();
            Assert.That(failed.Action, Is.EqualTo(Actions.UNLOCK_PU));
            Assert.That(failed.Success, Is.False);
            Assert.That(failed.Error, Does.Contain("UnlockPackagingUnits failed"));
            Assert.That(failed.RequestJson, Is.EqualTo("{\"uids\":[\"A\"]}"));

            _sc.ThrowAlways = false;
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A", "UnlockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_audit.Actions.Select(a => a.Success), Is.EqualTo(new[] { false, true }));
            Assert.That(_audit.Actions.Select(a => a.Attempt), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Release_is_retried_when_the_applied_pus_cannot_be_read()
        {
            _repo.AddAppliedPu(1, "A", _clock.UtcNow);
            Enqueue(Pn(1, "C1"), JobKind.Release);
            _repo.Down = true;

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(1));
            var failed = _audit.Actions.Single();
            Assert.That(failed.Action, Is.EqualTo(Actions.UNLOCK_PU));
            Assert.That(failed.Success, Is.False);

            _repo.Down = false;
            _clock.Advance(TimeSpan.FromSeconds(5));
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Release_job_runs_although_its_lock_is_not_cached()
        {
            _repo.AddAppliedPu(1, "A", _clock.UtcNow);

            Enqueue(Supplier(1, "S1"), JobKind.Release);
            Assert.That(_cache.Snapshot, Is.Empty);
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" }));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        [Test]
        public void Release_after_an_apply_unlocks_what_the_sweep_locked()
        {
            _sc.Machines.Add(M("m1", L1));
            _sc.MountedPus.AddRange(new[] { Mounted("m1", "A", mpn: "M1"), Mounted("m1", "B", mpn: "M2") });
            var l = Mpn(1, "M1");
            Enqueue(l, JobKind.Apply);
            _worker.RunDue();
            _sc.Calls.Clear();

            _cache.Remove(1);
            _repo.MarkReleased(1, "operator", _clock.UtcNow, "done");
            var released = l.Clone();
            released.Status = LockStatus.Released;
            _queue.Enqueue(new EnforcementJob { Lock = released, Kind = JobKind.Release, NotBeforeUtc = _clock.UtcNow });
            _worker.RunDue();

            Assert.That(_sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" }));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.That(_repo.Get(1).Status, Is.EqualTo(LockStatus.Released));
            Assert.That(_repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied));
            Assert.That(_cache.Snapshot, Is.Empty);
        }

        // ---- reblock ----

        [TestCase(LockType.Pn)]
        [TestCase(LockType.Mpn)]
        [TestCase(LockType.Supplier)]
        public void Reblock_of_a_sweep_lock_is_dropped_with_a_warning(LockType type)
        {
            Enqueue(MakeLock(1, type, "X1", null), JobKind.Reblock);

            _worker.RunDue();

            Assert.That(_sc.Calls, Is.Empty);
            Assert.That(_lc.Calls, Is.Empty);
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));
        }
    }
}
