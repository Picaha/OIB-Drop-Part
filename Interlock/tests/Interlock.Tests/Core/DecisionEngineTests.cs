using System.Collections.Generic;
using Interlock.Core.Decisions;
using Interlock.Core.Model;
using Interlock.Core.Text;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class DecisionEngineTests
    {
        private static LockRecord L(long id, LockType type, string target, string line = null)
        {
            return new LockRecord
            {
                Id = id,
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                LinePath = line,
                Reason = "r" + id,
                Status = LockStatus.Active
            };
        }

        [Test]
        public void Pu_lock_matches_uid_after_scanner_whitespace()
        {
            var d = DecisionEngine.Decide(new PuInfo { Uid = " uid-1\r\n" }, new[] { L(7, LockType.Pu, "UID-1") });
            Assert.That(d.Ok, Is.False);
            Assert.That(d.MatchedLockIds, Is.EqualTo(new long[] { 7 }));
            Assert.That(d.Message, Is.EqualTo("Interlock: PU UID-1 locked – r7 (#7)"));
        }

        [Test]
        public void Pn_mpn_supplier_match_their_own_fields()
        {
            var pu = new PuInfo { ComponentName = "pn-1", Mpn = "mpn-1", Supplier = "sup-1" };

            Assert.That(DecisionEngine.Decide(pu, new[] { L(1, LockType.Pn, "PN-1") }).MatchedLockIds, Is.EqualTo(new long[] { 1 }));
            Assert.That(DecisionEngine.Decide(pu, new[] { L(2, LockType.Mpn, "MPN-1") }).MatchedLockIds, Is.EqualTo(new long[] { 2 }));
            Assert.That(DecisionEngine.Decide(pu, new[] { L(3, LockType.Supplier, "SUP-1") }).MatchedLockIds, Is.EqualTo(new long[] { 3 }));

            // a PN lock whose value equals the MPN (or supplier) must not match, and so on for the other types
            Assert.That(DecisionEngine.Decide(pu, new[] { L(4, LockType.Pn, "MPN-1") }).Ok, Is.True);
            Assert.That(DecisionEngine.Decide(pu, new[] { L(5, LockType.Pn, "SUP-1") }).Ok, Is.True);
            Assert.That(DecisionEngine.Decide(pu, new[] { L(6, LockType.Mpn, "PN-1") }).Ok, Is.True);
            Assert.That(DecisionEngine.Decide(pu, new[] { L(7, LockType.Supplier, "MPN-1") }).Ok, Is.True);
        }

        [Test]
        public void Pu_lock_does_not_match_other_fields()
        {
            var pu = new PuInfo { Uid = "U", ComponentName = "C", Mpn = "M", Supplier = "S" };
            Assert.That(DecisionEngine.Decide(pu, new[] { L(1, LockType.Pu, "C"), L(2, LockType.Pu, "M"), L(3, LockType.Pu, "S") }).Ok, Is.True);
        }

        [Test]
        public void Feeder_and_conveyor_locks_never_match_a_scan()
        {
            var pu = new PuInfo { Uid = "X", ComponentName = "X", Mpn = "X", Supplier = "X" };
            Assert.That(DecisionEngine.Matches(L(1, LockType.Feeder, "X"), pu), Is.False);
            Assert.That(DecisionEngine.Matches(L(2, LockType.Conveyor, "X"), pu), Is.False);
            Assert.That(DecisionEngine.Decide(pu, new[] { L(1, LockType.Feeder, "X"), L(2, LockType.Conveyor, "X") }).Ok, Is.True);
        }

        [Test]
        public void Line_scoped_lock_ignores_other_line() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "Lines\\L2" }, new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }).Ok, Is.True);

        [Test]
        public void Line_scoped_lock_applies_when_pu_line_is_blank() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "" }, new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }).Ok, Is.False);

        [Test]
        public void Line_scoped_lock_applies_on_same_line_ignoring_case() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "lines\\l1" }, new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }).Ok, Is.False);

        [TestCase("")]
        [TestCase("  ")]
        public void Lock_with_blank_line_path_is_plant_wide(string lockLine) =>
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "Lines\\L2" }, new[] { L(1, LockType.Pn, "C1", lockLine) }).Ok, Is.False);

        [Test]
        public void Line_scoped_lock_applies_when_pu_line_is_whitespace() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "  " }, new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }).Ok, Is.False);

        [TestCase("Lines\\L1 ", "Lines\\L1")]
        [TestCase("Lines\\L1", " Lines\\L1  ")]
        [TestCase("Lines\\L1", "lines\\l1")]
        [TestCase(" LINES\\L1\t", "lines\\l1 ")]
        public void Line_paths_are_compared_trimmed_and_ignoring_case(string lockLine, string puLine) =>
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = puLine }, new[] { L(1, LockType.Pn, "C1", lockLine) }).Ok, Is.False);

        [Test]
        public void Mpn_line_scoped_lock_ignores_other_line() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { Mpn = "M1", LinePath = "Lines\\L2" }, new[] { L(1, LockType.Mpn, "M1", "Lines\\L1") }).Ok, Is.True);

        [Test]
        public void Supplier_line_scoped_lock_ignores_other_line() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { Supplier = "S1", LinePath = "Lines\\L2" }, new[] { L(1, LockType.Supplier, "S1", "Lines\\L1") }).Ok, Is.True);

        [TestCase(null, "Lines\\L1")]
        [TestCase("", "Lines\\L1")]
        [TestCase("  ", "Lines\\L1")]
        public void LineInScope_blank_lock_line_is_plant_wide(string lockLine, string otherLine) =>
            Assert.That(DecisionEngine.LineInScope(lockLine, otherLine), Is.True);

        [TestCase("Lines\\L1", null)]
        [TestCase("Lines\\L1", "")]
        [TestCase("Lines\\L1", " \r\n")]
        public void LineInScope_blank_other_line_matches_every_lock(string lockLine, string otherLine) =>
            Assert.That(DecisionEngine.LineInScope(lockLine, otherLine), Is.True);

        [TestCase("Lines\\L1", "Lines\\L1")]
        [TestCase("Lines\\L1 ", "Lines\\L1")]
        [TestCase("lines\\l1", " LINES\\L1")]
        public void LineInScope_equal_after_normalising(string lockLine, string otherLine) =>
            Assert.That(DecisionEngine.LineInScope(lockLine, otherLine), Is.True);

        [TestCase("Lines\\L1", "Lines\\L2")]
        [TestCase("Lines\\L1", "Lines\\L1\\Sub")]
        public void LineInScope_different_lines_are_out_of_scope(string lockLine, string otherLine) =>
            Assert.That(DecisionEngine.LineInScope(lockLine, otherLine), Is.False);

        [Test]
        public void Plant_wide_lock_applies_everywhere()
        {
            var locks = new[] { L(1, LockType.Pn, "C1") };
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "Lines\\L1" }, locks).Ok, Is.False);
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "Lines\\L9" }, locks).Ok, Is.False);
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = null }, locks).Ok, Is.False);
        }

        [Test]
        public void Null_fields_never_match() =>
            Assert.That(DecisionEngine.Decide(new PuInfo(), new[] { L(1, LockType.Mpn, "X") }).Ok, Is.True);

        [Test]
        public void Lock_with_unset_target_norm_never_matches()
        {
            var l = L(1, LockType.Pn, "C1");
            l.TargetNorm = null;
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1" }, new[] { l }).Ok, Is.True);
        }

        [Test]
        public void Several_matches_joined_in_id_order() =>
            Assert.That(DecisionEngine.Decide(new PuInfo { Uid = "U", ComponentName = "C" }, new[] { L(9, LockType.Pn, "C"), L(3, LockType.Pu, "U") }).Message,
                Is.EqualTo("Interlock: PU U locked – r3 (#3); Interlock: PN C locked – r9 (#9)"));

        [Test]
        public void Several_matches_list_ids_in_id_order()
        {
            var d = DecisionEngine.Decide(new PuInfo { Uid = "U", ComponentName = "C" }, new[] { L(9, LockType.Pn, "C"), L(3, LockType.Pu, "U") });
            Assert.That(d.MatchedLockIds, Is.EqualTo(new long[] { 3, 9 }));
        }

        [Test]
        public void Released_locks_are_ignored()
        {
            var released = L(1, LockType.Pn, "C1");
            released.Status = LockStatus.Released;
            Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1" }, new[] { released }).Ok, Is.True);

            var active = L(2, LockType.Pn, "C1");
            var d = DecisionEngine.Decide(new PuInfo { ComponentName = "C1" }, new[] { released, active });
            Assert.That(d.Ok, Is.False);
            Assert.That(d.MatchedLockIds, Is.EqualTo(new long[] { 2 }));
        }

        [Test]
        public void Ok_decision_has_null_message_and_empty_id_list()
        {
            var d = DecisionEngine.Decide(new PuInfo { Uid = "U" }, new List<LockRecord>());
            Assert.That(d.Ok, Is.True);
            Assert.That(d.Message, Is.Null);
            Assert.That(d.MatchedLockIds, Is.Not.Null);
            Assert.That(d.MatchedLockIds, Is.Empty);
        }

        [Test]
        public void Format_message_uses_db_type_target_reason_and_id() =>
            Assert.That(DecisionEngine.FormatMessage(L(12, LockType.Supplier, "Acme")), Is.EqualTo("Interlock: SUPPLIER Acme locked – r12 (#12)"));

        [Test]
        public void Feeder_lock_found_by_id()
        {
            Assert.That(DecisionEngine.FindFeederLock(" f-01 ", new[] { L(4, LockType.Feeder, "F-01") }).Id, Is.EqualTo(4));
            Assert.That(DecisionEngine.FindFeederLock("F-02", new[] { L(4, LockType.Feeder, "F-01") }), Is.Null);
        }

        [Test]
        public void Feeder_lock_lookup_takes_lowest_id_and_skips_non_feeder_and_released()
        {
            var released = L(1, LockType.Feeder, "F-01");
            released.Status = LockStatus.Released;
            var locks = new[]
            {
                L(2, LockType.Pn, "F-01"),
                L(8, LockType.Feeder, "F-01"),
                released,
                L(5, LockType.Feeder, "f-01")
            };
            Assert.That(DecisionEngine.FindFeederLock("F-01", locks).Id, Is.EqualTo(5));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void Feeder_lock_lookup_with_blank_id_finds_nothing(string feederId) =>
            Assert.That(DecisionEngine.FindFeederLock(feederId, new[] { L(4, LockType.Feeder, "F-01") }), Is.Null);
    }
}
