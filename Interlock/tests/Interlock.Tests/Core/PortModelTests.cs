using System;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class PortModelTests
    {
        [TestCase(false, false, Lanes.Left, false)]
        [TestCase(false, true, Lanes.Left, true)]
        [TestCase(true, false, Lanes.Left, false)]
        [TestCase(true, false, Lanes.Right, true)]
        [TestCase(false, true, Lanes.Right, false)]
        [TestCase(true, true, Lanes.Left, true)]
        [TestCase(true, true, Lanes.Right, true)]
        public void Single_lane_is_blocked_when_that_side_is_blocked(bool right, bool left, Lanes lanes, bool expected)
        {
            var state = new ConveyorState { RightBlocked = right, LeftBlocked = left };

            Assert.That(state.IsBlocked(lanes), Is.EqualTo(expected));
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, true)]
        public void Both_lanes_are_blocked_only_when_both_sides_are(bool right, bool left, bool expected)
        {
            var state = new ConveyorState { RightBlocked = right, LeftBlocked = left };

            Assert.That(state.IsBlocked(Lanes.Both), Is.EqualTo(expected));
        }

        [Test]
        public void Undefined_lanes_value_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConveyorState().IsBlocked((Lanes)99));
        }

        [Test]
        public void Action_names_are_the_database_values()
        {
            Assert.That(
                new[]
                {
                    Actions.LOCK_PU, Actions.UNLOCK_PU, Actions.LOCK_FEEDER_ONLINE, Actions.UNLOCK_FEEDER_ONLINE,
                    Actions.BLOCK_CONVEYOR, Actions.UNBLOCK_CONVEYOR, Actions.SWEEP, Actions.REBLOCK_CONVEYOR
                },
                Is.EqualTo(new[]
                {
                    "LOCK_PU", "UNLOCK_PU", "LOCK_FEEDER_ONLINE", "UNLOCK_FEEDER_ONLINE",
                    "BLOCK_CONVEYOR", "UNBLOCK_CONVEYOR", "SWEEP", "REBLOCK_CONVEYOR"
                }));
        }

        [Test]
        public void Query_defaults_to_500_rows()
        {
            Assert.That(new Interlock.Core.Ports.LockQuery().Limit, Is.EqualTo(500));
            Assert.That(new DecisionQuery().Limit, Is.EqualTo(500));
        }

        [Test]
        public void Repository_exceptions_carry_message_and_inner_exception()
        {
            var inner = new InvalidOperationException("inner");

            Assert.That(new DbUnavailableException("db").Message, Is.EqualTo("db"));
            Assert.That(new DbUnavailableException("db", inner).InnerException, Is.SameAs(inner));
            Assert.That(new DuplicateActiveLockException("dup").Message, Is.EqualTo("dup"));
            Assert.That(new DuplicateActiveLockException("dup", inner).InnerException, Is.SameAs(inner));
            Assert.That(new OibCallException("oib").Message, Is.EqualTo("oib"));
            Assert.That(new OibCallException("oib", inner).InnerException, Is.SameAs(inner));
        }
    }
}
