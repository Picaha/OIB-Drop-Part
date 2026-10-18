using System;
using System.Linq;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class FakeSupportTests
    {
        [Test]
        public void Station_directory_exposes_what_the_test_puts_in()
        {
            var dir = new FakeStationDirectory();
            dir.Stations.Add(new StationRef { LinePath = "Lines\\L1", StationPath = "Lines\\L1\\S1" });
            dir.LinePaths.Add("Lines\\L1");

            IStationDirectory port = dir;
            Assert.That(port.Stations.Single().StationPath, Is.EqualTo("Lines\\L1\\S1"));
            Assert.That(port.LinePaths, Is.EqualTo(new[] { "Lines\\L1" }));
        }

        [Test]
        public void Spool_keeps_appended_locks_in_order_and_clears()
        {
            var spool = new MemorySpool();
            var first = new LockRecord { TargetNorm = "U1", Reason = "r" };
            spool.Append(first);
            spool.Append(new LockRecord { TargetNorm = "U2" });

            first.Reason = "changed after append";
            spool.ReadAll()[0].Reason = "changed after read";

            Assert.That(spool.ReadAll().Select(l => l.TargetNorm), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(spool.ReadAll()[0].Reason, Is.EqualTo("r"));

            spool.Clear();
            Assert.That(spool.ReadAll(), Is.Empty);
        }

        [Test]
        public void List_log_sorts_messages_by_level()
        {
            var log = new ListLog();

            log.Info("i1");
            log.Warn("w1");
            log.Error("e1", new InvalidOperationException("boom"));
            log.Info("i2");

            Assert.That(log.Infos, Is.EqualTo(new[] { "i1", "i2" }));
            Assert.That(log.Warnings, Is.EqualTo(new[] { "w1" }));
            Assert.That(log.Errors, Is.EqualTo(new[] { "e1" }));
        }
    }
}
