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
    public class FakeLineControlClientTests
    {
        private FakeLineControlClient _lc;

        [SetUp]
        public void SetUp()
        {
            _lc = new FakeLineControlClient();
        }

        [Test]
        public void Every_call_is_recorded_with_path_and_lanes()
        {
            _lc.HasLicense();
            _lc.IsBlockSupported("S1");
            _lc.Block("S1", Lanes.Both, "why");
            _lc.Unblock("S1", Lanes.Left, "why");
            _lc.GetState("S1");

            Assert.That(_lc.Calls, Is.EqualTo(new[] { "HasLicense", "IsBlockSupported:S1", "Block:S1:BOTH", "Unblock:S1:LEFT", "GetState:S1" }));
        }

        [Test]
        public void Reads_answer_from_the_configured_state()
        {
            var state = new ConveyorState { LeftBlocked = true };
            _lc.States["S1"] = state;

            Assert.That(_lc.HasLicense(), Is.True);
            Assert.That(_lc.IsBlockSupported("S1"), Is.True);
            Assert.That(_lc.GetState("S1"), Is.SameAs(state));
            Assert.That(_lc.GetState("unknown").IsBlocked(Lanes.Left), Is.False, "an unknown station reads as not blocked");

            _lc.Supported = false;
            Assert.That(_lc.IsBlockSupported("S1"), Is.False);
        }

        [Test]
        public void OnCall_sees_each_recorded_call_before_it_can_throw()
        {
            var seen = new List<string>();
            _lc.OnCall = c => seen.Add(c + "|" + string.Join(",", _lc.Calls));
            _lc.ThrowAlways = true;

            Assert.Throws<OibCallException>(() => _lc.Block("S1", Lanes.Both, "why"));
            Assert.Throws<OibCallException>(() => _lc.GetState("S1"));

            Assert.That(seen, Is.EqualTo(new[]
            {
                "Block:S1:BOTH|Block:S1:BOTH",
                "GetState:S1|Block:S1:BOTH,GetState:S1"
            }));
        }

        [Test]
        public void ThrowAlways_makes_every_member_throw_after_recording_the_call()
        {
            _lc.ThrowAlways = true;
            var calls = new[]
            {
                Tuple.Create<string, Action>("HasLicense", () => _lc.HasLicense()),
                Tuple.Create<string, Action>("IsBlockSupported:S1", () => _lc.IsBlockSupported("S1")),
                Tuple.Create<string, Action>("Block:S1:RIGHT", () => _lc.Block("S1", Lanes.Right, "why")),
                Tuple.Create<string, Action>("Unblock:S1:BOTH", () => _lc.Unblock("S1", Lanes.Both, "why")),
                Tuple.Create<string, Action>("GetState:S1", () => _lc.GetState("S1"))
            };

            foreach (var call in calls)
                Assert.Throws<OibCallException>(() => call.Item2(), call.Item1);

            Assert.That(_lc.Calls, Is.EqualTo(calls.Select(c => c.Item1).ToArray()));
        }
    }
}
