using System;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    /// <summary>Pins the behaviour later tasks rely on in the in-memory lock repository.</summary>
    [TestFixture]
    public class InMemoryLockRepositoryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private InMemoryLockRepository _repo;

        [SetUp]
        public void SetUp()
        {
            _repo = new InMemoryLockRepository();
        }

        private static LockRecord L(LockType type, string target, string line = null, Lanes? lanes = null, long id = 0,
            string reason = "reason")
        {
            return new LockRecord
            {
                Id = id,
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                LinePath = line,
                Lanes = lanes,
                Reason = reason,
                Source = LockSource.Manual,
                CreatedBy = "tester",
                CreatedAtUtc = T0,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private static LockRecord Pu(string uid, long id = 0)
        {
            return L(LockType.Pu, uid, id: id);
        }

        // ---- Insert / identity ----

        [Test]
        public void Insert_assigns_increasing_ids_starting_at_one()
        {
            var input = Pu("A");

            var a = _repo.Insert(input);
            var b = _repo.Insert(Pu("B"));

            Assert.That(new[] { a.Id, b.Id }, Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(input.Id, Is.EqualTo(0), "the caller's object is not changed");
        }

        [Test]
        public void Insert_continues_after_rows_stored_with_their_own_ids()
        {
            _repo.InsertActive(Pu("A", 7));

            Assert.That(_repo.Insert(Pu("B")).Id, Is.EqualTo(8));
        }

        [Test]
        public void Insert_stores_a_copy_and_returns_a_copy()
        {
            var input = Pu("A");
            var returned = _repo.Insert(input);

            input.Reason = "changed after insert";
            returned.Reason = "changed on the result";

            Assert.That(_repo.Get(returned.Id).Reason, Is.EqualTo("reason"));
        }

        [Test]
        public void Reads_return_clones_so_stored_rows_cannot_be_changed_by_accident()
        {
            var id = _repo.Insert(L(LockType.Pn, "C1", line: "Lines\\L1")).Id;

            _repo.Get(id).Reason = "x";
            _repo.LoadActive().Single().Reason = "x";
            _repo.Query(new LockQuery()).Single().Reason = "x";
            _repo.FindActive(LockType.Pn, "C1", "Lines\\L1", null).Reason = "x";

            Assert.That(_repo.Get(id).Reason, Is.EqualTo("reason"));
        }

        [Test]
        public void Insert_of_an_identical_active_lock_throws_duplicate()
        {
            _repo.Insert(Pu("A"));

            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(Pu("A")));
            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
        }

        [Test]
        public void Identity_is_type_target_line_and_lanes()
        {
            _repo.Insert(L(LockType.Pn, "X", line: "Lines\\L1"));

            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Mpn, "X", line: "Lines\\L1")), "other type");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Pn, "Y", line: "Lines\\L1")), "other target");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Pn, "X", line: "Lines\\L2")), "other line");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Pn, "X")), "plant-wide is another scope");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Conveyor, "S1", lanes: Lanes.Left)));
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Conveyor, "S1", lanes: Lanes.Right)), "other lanes");

            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Conveyor, "S1", lanes: Lanes.Left)));
            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Pn, "X")), "null line equals null line");
        }

        [Test]
        public void Line_paths_are_compared_after_normalising()
        {
            _repo.Insert(L(LockType.Pn, "X", line: "Lines\\L1"));
            _repo.Insert(L(LockType.Pn, "Y"));

            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Pn, "X", line: " lines\\l1 ")));
            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Pn, "Y", line: "  ")), "blank line is plant-wide");
        }

        [Test]
        public void A_released_lock_does_not_block_a_new_active_one()
        {
            var first = _repo.Insert(Pu("A"));
            _repo.MarkReleased(first.Id, "qa1", T0, "ok");

            Assert.DoesNotThrow(() => _repo.Insert(Pu("A")));
        }

        [Test]
        public void FindActive_normalises_its_arguments_and_ignores_released_rows()
        {
            var active = _repo.Insert(L(LockType.Pn, "C1", line: "Lines\\L1"));
            var released = _repo.Insert(Pu("GONE"));
            _repo.MarkReleased(released.Id, "qa1", T0, "ok");

            Assert.That(_repo.FindActive(LockType.Pn, " c1\r\n", " LINES\\l1 ", null).Id, Is.EqualTo(active.Id));
            Assert.That(_repo.FindActive(LockType.Pn, "C1", "Lines\\L2", null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pn, "C1", null, null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Mpn, "C1", "Lines\\L1", null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pn, "C1", "Lines\\L1", Lanes.Both), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pu, "GONE", null, null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pu, " ", null, null), Is.Null);
        }

        [Test]
        public void FailNextInsertWithDuplicate_stores_the_existing_lock_then_throws_once()
        {
            _repo.FailNextInsertWithDuplicate(Pu("U1", 9));

            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(Pu("U1")));

            var existing = _repo.FindActive(LockType.Pu, "U1", null, null);
            Assert.That(existing.Id, Is.EqualTo(9));
            Assert.That(existing.Status, Is.EqualTo(LockStatus.Active));
            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
            Assert.That(_repo.Insert(Pu("OTHER")).Id, Is.EqualTo(10), "only the next insert fails; ids continue after the stored row");
        }

        // ---- test helpers that bypass the rules ----

        [Test]
        public void InsertActive_and_InsertReleased_store_rows_as_given_without_the_uniqueness_rule()
        {
            _repo.InsertActive(Pu("A", 1));
            _repo.InsertActive(Pu("A", 2));
            var released = Pu("B", 3);
            released.EnforceState = EnforceState.Pending;
            _repo.InsertReleased(released);

            Assert.That(_repo.LoadActive().Select(l => l.Id), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(_repo.Get(3).Status, Is.EqualTo(LockStatus.Released));
            Assert.That(_repo.LoadPendingReleases().Single().Id, Is.EqualTo(3));
        }

        [Test]
        public void InsertActive_with_id_zero_gets_the_next_id()
        {
            Assert.That(_repo.InsertActive(Pu("A")).Id, Is.EqualTo(1));
            Assert.That(_repo.InsertReleased(Pu("B")).Id, Is.EqualTo(2));
        }

        [Test]
        public void Storing_a_row_under_an_id_that_is_taken_is_a_test_bug()
        {
            _repo.InsertActive(Pu("A", 1));

            Assert.Throws<InvalidOperationException>(() => _repo.InsertActive(Pu("B", 1)));
        }

        // ---- release / enforce state ----

        [Test]
        public void MarkReleased_releases_the_lock_and_sets_pending()
        {
            var id = _repo.Insert(Pu("A")).Id;
            _repo.SetEnforceState(id, EnforceState.Applied, null);

            _repo.MarkReleased(id, "qa1", T0.AddMinutes(5), "fixed");

            var row = _repo.Get(id);
            Assert.That(row.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(row.ReleasedBy, Is.EqualTo("qa1"));
            Assert.That(row.ReleasedAtUtc, Is.EqualTo(T0.AddMinutes(5)));
            Assert.That(row.ReleaseReason, Is.EqualTo("fixed"));
            Assert.That(row.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_repo.LoadActive(), Is.Empty);
        }

        [Test]
        public void LoadPendingReleases_lists_released_rows_still_pending()
        {
            var a = _repo.Insert(Pu("A")).Id;
            var b = _repo.Insert(Pu("B")).Id;
            _repo.Insert(Pu("C"));
            _repo.MarkReleased(a, "qa1", T0, "ok");
            _repo.MarkReleased(b, "qa1", T0, "ok");

            _repo.SetEnforceState(b, EnforceState.Applied, null);

            Assert.That(_repo.LoadPendingReleases().Select(l => l.Id), Is.EqualTo(new[] { a }));
        }

        [Test]
        public void SetEnforceState_sets_state_and_error()
        {
            var id = _repo.Insert(Pu("A")).Id;

            _repo.SetEnforceState(id, EnforceState.Failed, "boom");

            Assert.That(_repo.Get(id).EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_repo.Get(id).EnforceError, Is.EqualTo("boom"));
        }

        [Test]
        public void Unknown_ids_read_as_missing_and_updates_do_nothing()
        {
            Assert.That(_repo.Get(42), Is.Null);
            Assert.DoesNotThrow(() => _repo.MarkReleased(42, "qa1", T0, "ok"));
            Assert.DoesNotThrow(() => _repo.SetEnforceState(42, EnforceState.Failed, "x"));
        }

        // ---- coverage by other locks ----

        [Test]
        public void Uid_is_covered_by_another_active_pu_lock()
        {
            _repo.InsertActive(Pu("U1", 1));

            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U1", 2), Is.True);
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U1", 1), Is.False, "the lock itself does not count");
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U2", 2), Is.False);
        }

        [Test]
        public void Uid_is_not_covered_by_a_released_pu_lock_or_a_lock_of_another_type()
        {
            _repo.InsertReleased(Pu("U1", 1));
            _repo.InsertActive(L(LockType.Pn, "U2", id: 2));

            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U1", 99), Is.False);
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U2", 99), Is.False);
        }

        [Test]
        public void Uid_is_covered_by_an_open_applied_row_of_another_active_lock()
        {
            var pn = _repo.Insert(L(LockType.Pn, "C1"));
            _repo.AddAppliedPu(pn.Id, "a1", T0);

            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", 999), Is.True, "uids are compared normalised");
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", pn.Id), Is.False);

            _repo.CloseAppliedPu(pn.Id, "a1", T0.AddMinutes(1));
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", 999), Is.False, "closed rows no longer cover");
        }

        [Test]
        public void An_applied_row_of_a_released_lock_does_not_cover()
        {
            var pn = _repo.Insert(L(LockType.Pn, "C1"));
            _repo.AddAppliedPu(pn.Id, "A1", T0);
            _repo.MarkReleased(pn.Id, "qa1", T0, "ok");

            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", 999), Is.False);
        }

        [Test]
        public void Applied_pus_are_listed_open_until_closed()
        {
            _repo.AddAppliedPu(1, "A", T0);
            _repo.AddAppliedPu(1, "B", T0);
            _repo.AddAppliedPu(2, "C", T0);

            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A", "B" }));

            _repo.CloseAppliedPu(1, " a ", T0.AddMinutes(1));

            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "B" }));
            Assert.That(_repo.GetOpenAppliedPus(2), Is.EqualTo(new[] { "C" }));
            Assert.That(_repo.GetOpenAppliedPus(3), Is.Empty);
        }

        // ---- release history ----

        [Test]
        public void GetLatestReleaseUtc_is_the_newest_release_of_that_type_and_target()
        {
            var first = _repo.Insert(Pu("A")).Id;
            _repo.MarkReleased(first, "qa1", T0.AddMinutes(1), "ok");
            var second = _repo.Insert(Pu("A")).Id;
            _repo.MarkReleased(second, "qa1", T0.AddMinutes(9), "ok");
            _repo.Insert(Pu("A"));
            var other = _repo.Insert(Pu("B")).Id;
            _repo.MarkReleased(other, "qa1", T0.AddHours(5), "ok");

            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, "A"), Is.EqualTo(T0.AddMinutes(9)));
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, " a "), Is.EqualTo(T0.AddMinutes(9)));
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pn, "A"), Is.Null);
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, "NEVER"), Is.Null);
        }

        // ---- query ----

        private void SeedForQuery()
        {
            _repo.Insert(L(LockType.Pu, "U1", reason: "Reel damaged"));
            _repo.Insert(L(LockType.Pn, "C1", line: "Lines\\L1", reason: "Wrong part"));
            var released = _repo.Insert(L(LockType.Pn, "C2", reason: "Old"));
            _repo.MarkReleased(released.Id, "qa1", T0, "done");
            var failed = _repo.Insert(L(LockType.Mpn, "M1", reason: "Bad lot"));
            _repo.SetEnforceState(failed.Id, EnforceState.Failed, "x");
        }

        [Test]
        public void Query_without_filters_returns_everything_ordered_by_id()
        {
            SeedForQuery();

            Assert.That(_repo.Query(new LockQuery()).Select(l => l.Id), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void Query_filters_by_status_type_enforce_state_and_line()
        {
            SeedForQuery();

            Assert.That(_repo.Query(new LockQuery { Status = LockStatus.Active, Type = LockType.Pn }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { Status = LockStatus.Released }).Select(l => l.Target), Is.EqualTo(new[] { "C2" }));
            Assert.That(_repo.Query(new LockQuery { EnforceState = EnforceState.Failed }).Select(l => l.Target), Is.EqualTo(new[] { "M1" }));
            Assert.That(_repo.Query(new LockQuery { LinePath = " lines\\l1 " }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { LinePath = "  " }).Count, Is.EqualTo(4), "a blank line filter means no filter");
        }

        [Test]
        public void Query_text_is_a_case_insensitive_substring_of_target_or_reason()
        {
            SeedForQuery();

            Assert.That(_repo.Query(new LockQuery { Text = "u1" }).Select(l => l.Target), Is.EqualTo(new[] { "U1" }));
            Assert.That(_repo.Query(new LockQuery { Text = "WRONG" }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { Text = "no such text" }), Is.Empty);
        }

        [Test]
        public void Query_is_capped_at_limit()
        {
            SeedForQuery();

            Assert.That(_repo.Query(new LockQuery { Limit = 2 }).Select(l => l.Id), Is.EqualTo(new long[] { 1, 2 }));
        }

        // ---- the database being down ----

        [Test]
        public void FailAddAppliedPu_fails_only_that_member()
        {
            _repo.FailAddAppliedPu = true;

            Assert.Throws<DbUnavailableException>(() => _repo.AddAppliedPu(1, "A", T0));
            Assert.That(_repo.GetOpenAppliedPus(1), Is.Empty);
            Assert.DoesNotThrow(() => _repo.LoadActive());

            _repo.FailAddAppliedPu = false;
            _repo.AddAppliedPu(1, "A", T0);
            Assert.That(_repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A" }));
        }

        [Test]
        public void Down_makes_every_member_throw_db_unavailable()
        {
            var calls = new System.Collections.Generic.Dictionary<string, Action>
            {
                { "LoadActive", () => _repo.LoadActive() },
                { "LoadPendingReleases", () => _repo.LoadPendingReleases() },
                { "Insert", () => _repo.Insert(Pu("A")) },
                { "FindActive", () => _repo.FindActive(LockType.Pu, "A", null, null) },
                { "Get", () => _repo.Get(1) },
                { "Query", () => _repo.Query(new LockQuery()) },
                { "MarkReleased", () => _repo.MarkReleased(1, "qa1", T0, "ok") },
                { "SetEnforceState", () => _repo.SetEnforceState(1, EnforceState.Applied, null) },
                { "AddAppliedPu", () => _repo.AddAppliedPu(1, "A", T0) },
                { "GetOpenAppliedPus", () => _repo.GetOpenAppliedPus(1) },
                { "CloseAppliedPu", () => _repo.CloseAppliedPu(1, "A", T0) },
                { "IsUidCoveredByOtherActiveLock", () => _repo.IsUidCoveredByOtherActiveLock("A", 1) },
                { "GetLatestReleaseUtc", () => _repo.GetLatestReleaseUtc(LockType.Pu, "A") }
            };
            _repo.Down = true;

            foreach (var call in calls)
                Assert.Throws<DbUnavailableException>(() => call.Value(), call.Key);

            _repo.Down = false;
            Assert.That(_repo.LoadActive(), Is.Empty, "nothing was stored while down");
        }
    }
}
