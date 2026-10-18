using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class FakeSetupCenterClientTests
    {
        private FakeSetupCenterClient _sc;

        [SetUp]
        public void SetUp()
        {
            _sc = new FakeSetupCenterClient();
        }

        [Test]
        public void Every_call_is_recorded_with_its_ids()
        {
            var feeder = new FeederMount { FeederId = "F1" };
            _sc.IsConnected();
            _sc.HasLicense();
            _sc.GetMachines();
            _sc.GetMountedPus(new[] { "m1", "m2" });
            _sc.GetLinePaths();
            _sc.FindMountedFeeder("F1");
            _sc.LockOnlineFeeder(feeder, "why");
            _sc.UnlockOnlineFeeder(feeder);
            _sc.LockPackagingUnits(new[] { "U1", "U2" }, "why");
            _sc.UnlockPackagingUnits(new[] { "U1" });

            Assert.That(_sc.Calls, Is.EqualTo(new[]
            {
                "IsConnected", "HasLicense", "GetMachines", "GetMountedPus:m1,m2", "GetLinePaths", "FindMountedFeeder:F1",
                "LockOnlineFeeder:F1", "UnlockOnlineFeeder:F1", "LockPackagingUnits:U1,U2", "UnlockPackagingUnits:U1"
            }));
        }

        [Test]
        public void Reads_answer_from_the_configured_state()
        {
            var mount = new FeederMount { FeederId = "F1", MachineId = "m1" };
            _sc.Mounted["F1"] = mount;
            _sc.Machines.Add(new MachineRef { MachineId = "m1", LinePath = "Lines\\L1" });
            _sc.Machines.Add(new MachineRef { MachineId = "m2", LinePath = "Lines\\L2" });
            _sc.Machines.Add(new MachineRef { MachineId = "m3", LinePath = "Lines\\L1" });

            Assert.That(_sc.IsConnected(), Is.True);
            Assert.That(_sc.HasLicense(), Is.True);
            Assert.That(_sc.FindMountedFeeder("F1"), Is.SameAs(mount));
            Assert.That(_sc.FindMountedFeeder("F2"), Is.Null);
            Assert.That(_sc.GetMachines().Select(m => m.MachineId), Is.EqualTo(new[] { "m1", "m2", "m3" }));
            Assert.That(_sc.GetLinePaths(), Is.EqualTo(new[] { "Lines\\L1", "Lines\\L2" }), "distinct lines of the machines");
        }

        [Test]
        public void GetMountedPus_returns_only_pus_on_the_requested_machines()
        {
            _sc.MountedPus.Add(new MountedPu { MachineId = "m1", Pu = new PuInfo { Uid = "A" } });
            _sc.MountedPus.Add(new MountedPu { MachineId = "m2", Pu = new PuInfo { Uid = "B" } });
            _sc.MountedPus.Add(new MountedPu { MachineId = "m1", Pu = new PuInfo { Uid = "C" } });

            Assert.That(_sc.GetMountedPus(new[] { "m1" }).Select(p => p.Pu.Uid), Is.EqualTo(new[] { "A", "C" }));
            Assert.That(_sc.GetMountedPus(new[] { "m1", "m2" }).Count, Is.EqualTo(3));
            Assert.That(_sc.GetMountedPus(new string[0]), Is.Empty);
            Assert.That(_sc.GetMountedPus(new[] { "m9" }), Is.Empty);
        }

        [Test]
        public void ThrowAlways_records_then_throws_on_write_calls_only()
        {
            var feeder = new FeederMount { FeederId = "F1" };
            _sc.ThrowAlways = true;

            Assert.Throws<OibCallException>(() => _sc.LockPackagingUnits(new[] { "U1" }, "why"));
            Assert.Throws<OibCallException>(() => _sc.UnlockPackagingUnits(new[] { "U1" }));
            Assert.Throws<OibCallException>(() => _sc.LockOnlineFeeder(feeder, "why"));
            Assert.Throws<OibCallException>(() => _sc.UnlockOnlineFeeder(feeder));

            Assert.DoesNotThrow(() => _sc.FindMountedFeeder("F1"));
            Assert.DoesNotThrow(() => _sc.GetMachines());
            Assert.DoesNotThrow(() => _sc.GetMountedPus(new[] { "m1" }));
            Assert.DoesNotThrow(() => _sc.IsConnected());
            Assert.That(_sc.Calls.Take(4), Is.EqualTo(new[]
            {
                "LockPackagingUnits:U1", "UnlockPackagingUnits:U1", "LockOnlineFeeder:F1", "UnlockOnlineFeeder:F1"
            }));
        }
    }
}
