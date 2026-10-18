using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class InMemoryAuditRepositoryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private InMemoryAuditRepository _audit;

        [SetUp]
        public void SetUp()
        {
            _audit = new InMemoryAuditRepository();
        }

        private static ActionLogEntry ActionEntry(long? lockId, string action = Actions.LOCK_PU)
        {
            return new ActionLogEntry { LockId = lockId, Action = action, RequestJson = "{}", Success = true, Attempt = 1, CreatedAtUtc = T0 };
        }

        private static DecisionLogEntry Decision(string uid, string pn = null, int minutes = 0)
        {
            return new DecisionLogEntry { Method = "GetPackagingUnitControlStatus", Uid = uid, Pn = pn, Result = "OK", CreatedAtUtc = T0.AddMinutes(minutes) };
        }

        [Test]
        public void Actions_are_stored_with_ids_and_listed_per_lock_in_order()
        {
            _audit.AddAction(ActionEntry(1, Actions.LOCK_PU));
            _audit.AddAction(ActionEntry(2, Actions.LOCK_FEEDER_ONLINE));
            _audit.AddAction(ActionEntry(1, Actions.UNLOCK_PU));
            _audit.AddAction(ActionEntry(null, Actions.SWEEP));

            Assert.That(_audit.Actions.Select(a => a.Id), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
            Assert.That(_audit.GetActions(1).Select(a => a.Action), Is.EqualTo(new[] { "LOCK_PU", "UNLOCK_PU" }));
            Assert.That(_audit.GetActions(9), Is.Empty);
        }

        [Test]
        public void Decisions_are_added_in_a_batch()
        {
            _audit.AddDecisions(new List<DecisionLogEntry> { Decision("U1"), Decision("U2") });

            Assert.That(_audit.Decisions.Select(d => d.Uid), Is.EqualTo(new[] { "U1", "U2" }));
        }

        [Test]
        public void QueryDecisions_filters_by_uid_pn_and_time_range()
        {
            _audit.AddDecisions(new List<DecisionLogEntry>
            {
                Decision("U1", "P1", 0), Decision("U2", "P1", 10), Decision("U3", "P2", 20)
            });

            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = " u1 " }).Select(d => d.Uid), Is.EqualTo(new[] { "U1" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Pn = "p1" }).Select(d => d.Uid), Is.EqualTo(new[] { "U2", "U1" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { FromUtc = T0.AddMinutes(10) }).Select(d => d.Uid), Is.EqualTo(new[] { "U3", "U2" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { ToUtc = T0.AddMinutes(10) }).Select(d => d.Uid), Is.EqualTo(new[] { "U2", "U1" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = "  " }).Count, Is.EqualTo(3), "a blank filter means no filter");
        }

        [Test]
        public void QueryDecisions_returns_the_newest_rows_first_capped_at_limit()
        {
            _audit.AddDecisions(new List<DecisionLogEntry> { Decision("U1", minutes: 0), Decision("U2", minutes: 10), Decision("U3", minutes: 5) });

            Assert.That(_audit.QueryDecisions(new DecisionQuery()).Select(d => d.Uid), Is.EqualTo(new[] { "U2", "U3", "U1" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Limit = 2 }).Select(d => d.Uid), Is.EqualTo(new[] { "U2", "U3" }),
                "the newest rows, not the oldest");
        }

        [Test]
        public void QueryDecisions_breaks_a_time_tie_by_the_later_insert()
        {
            _audit.AddDecisions(new List<DecisionLogEntry> { Decision("U1"), Decision("U2") });
            _audit.AddDecisions(new List<DecisionLogEntry> { Decision("U3") });

            Assert.That(_audit.QueryDecisions(new DecisionQuery()).Select(d => d.Uid), Is.EqualTo(new[] { "U3", "U2", "U1" }));
        }

        [Test]
        public void GetActions_is_ordered_by_id()
        {
            var late = ActionEntry(1, Actions.UNLOCK_PU);
            late.Id = 5;
            var early = ActionEntry(1, Actions.LOCK_PU);
            early.Id = 3;
            _audit.AddAction(late);
            _audit.AddAction(early);

            Assert.That(_audit.GetActions(1).Select(a => a.Id), Is.EqualTo(new long[] { 3, 5 }));
        }

        [Test]
        public void Actions_are_copied_on_write_and_on_read()
        {
            var input = ActionEntry(1);
            _audit.AddAction(input);

            input.Error = "changed after add";
            _audit.GetActions(1).Single().Error = "changed after read";

            Assert.That(input.Id, Is.EqualTo(0), "the caller's entry is not changed");
            Assert.That(_audit.Actions.Single().Id, Is.EqualTo(1));
            Assert.That(_audit.GetActions(1).Single().Error, Is.Null);
        }

        [Test]
        public void Decisions_are_copied_on_write_and_on_read()
        {
            var input = Decision("U1");
            _audit.AddDecisions(new List<DecisionLogEntry> { input });

            input.Message = "changed after add";
            _audit.QueryDecisions(new DecisionQuery()).Single().Message = "changed after read";

            Assert.That(_audit.Decisions.Single(), Is.Not.SameAs(input));
            Assert.That(_audit.QueryDecisions(new DecisionQuery()).Single().Message, Is.Null);
        }

        [Test]
        public void Down_makes_every_member_throw_db_unavailable_and_stores_nothing()
        {
            _audit.Down = true;

            Assert.Throws<DbUnavailableException>(() => _audit.AddAction(ActionEntry(1)));
            Assert.Throws<DbUnavailableException>(() => _audit.GetActions(1));
            Assert.Throws<DbUnavailableException>(() => _audit.AddDecisions(new List<DecisionLogEntry> { Decision("U1") }));
            Assert.Throws<DbUnavailableException>(() => _audit.QueryDecisions(new DecisionQuery()));
            Assert.That(_audit.Actions, Is.Empty);
            Assert.That(_audit.Decisions, Is.Empty);
        }
    }
}
