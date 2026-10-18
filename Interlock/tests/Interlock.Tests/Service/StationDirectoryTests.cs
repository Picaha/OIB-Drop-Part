using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Service.Oib;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public class StationDirectoryTests
    {
        private sealed class FakeMonitoring : IMonitoringClient
        {
            public List<StationRef> Stations = new List<StationRef>();
            public bool Fail;
            public int Calls;

            public IList<StationRef> GetStations()
            {
                Calls++;
                if (Fail)
                    throw new OibCallException("Monitoring not connected");
                return Stations.ToList();
            }
        }

        private FakeMonitoring _monitoring;
        private FakeSetupCenterClient _sc;
        private ListLog _log;
        private StationDirectory _dir;

        [SetUp]
        public void SetUp()
        {
            _monitoring = new FakeMonitoring();
            _sc = new FakeSetupCenterClient();
            _log = new ListLog();
            _dir = new StationDirectory(_monitoring, _sc, _log);
        }

        private static StationRef St(string line, string station)
        {
            return new StationRef { LinePath = line, StationPath = station };
        }

        private static MachineRef Mc(string id, string line)
        {
            return new MachineRef { MachineId = id, LinePath = line };
        }

        [Test]
        public void Before_the_first_refresh_the_lists_are_empty_and_nothing_was_called()
        {
            Assert.That(_dir.Stations, Is.Empty);
            Assert.That(_dir.LinePaths, Is.Empty);
            Assert.That(_monitoring.Calls, Is.EqualTo(0));
            Assert.That(_sc.Calls, Is.Empty);
        }

        [Test]
        public void Refresh_publishes_the_stations_and_the_union_of_line_paths_sorted_ignoring_case()
        {
            _monitoring.Stations.Add(St("Lines\\L2", "Lines\\L2\\S1"));
            _monitoring.Stations.Add(St("Lines\\L2", "Lines\\L2\\S2"));
            _monitoring.Stations.Add(St("lines\\l1", "lines\\l1\\S1"));
            _sc.Machines.Add(Mc("m1", "Lines\\L1"));
            _sc.Machines.Add(Mc("m3", "Lines\\L3"));

            _dir.Refresh();

            Assert.That(_dir.Stations.Select(s => s.StationPath), Is.EqualTo(new[] { "Lines\\L2\\S1", "Lines\\L2\\S2", "lines\\l1\\S1" }));
            Assert.That(_dir.LinePaths, Is.EqualTo(new[] { "lines\\l1", "Lines\\L2", "Lines\\L3" }), "L1 appears once, whichever source named it first");
        }

        [Test]
        public void Blank_line_paths_are_left_out()
        {
            _monitoring.Stations.Add(St(null, "S0"));
            _monitoring.Stations.Add(St(" ", "S1"));
            _monitoring.Stations.Add(St("Lines\\L1", "S2"));

            _dir.Refresh();

            Assert.That(_dir.LinePaths, Is.EqualTo(new[] { "Lines\\L1" }));
            Assert.That(_dir.Stations, Has.Count.EqualTo(3), "stations are kept as Monitoring reported them");
        }

        [Test]
        public void The_published_lists_are_snapshots_that_later_refreshes_and_callers_cannot_change()
        {
            _monitoring.Stations.Add(St("Lines\\L1", "S1"));
            _dir.Refresh();
            var stations = _dir.Stations;
            var lines = _dir.LinePaths;

            _monitoring.Stations.Add(St("Lines\\L2", "S2"));
            _dir.Refresh();

            Assert.That(stations, Has.Count.EqualTo(1), "the earlier snapshot is unchanged");
            Assert.That(lines, Is.EqualTo(new[] { "Lines\\L1" }));
            Assert.That(_dir.Stations, Has.Count.EqualTo(2));
            Assert.That(stations.IsReadOnly, Is.True);
            Assert.That(lines.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => stations.Add(St("x", "y")));
            Assert.Throws<NotSupportedException>(() => lines.Add("x"));
        }

        [Test]
        public void A_failed_monitoring_refresh_keeps_the_last_good_stations_and_warns()
        {
            _monitoring.Stations.Add(St("Lines\\L1", "S1"));
            _dir.Refresh();
            _monitoring.Fail = true;
            _monitoring.Stations.Clear();

            Assert.DoesNotThrow(() => _dir.Refresh());

            Assert.That(_dir.Stations.Select(s => s.StationPath), Is.EqualTo(new[] { "S1" }));
            Assert.That(_dir.LinePaths, Is.EqualTo(new[] { "Lines\\L1" }));
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("Monitoring", _log.Warnings[0]);
        }

        [Test]
        public void When_setup_center_never_answers_only_the_station_lines_are_known()
        {
            var dir = new StationDirectory(_monitoring, new ThrowingLines(), _log);
            _monitoring.Stations.Add(St("Lines\\L1", "S1"));
            _monitoring.Stations.Add(St("Lines\\L2", "S2"));

            Assert.DoesNotThrow(dir.Refresh);

            Assert.That(dir.Stations, Has.Count.EqualTo(2));
            Assert.That(dir.LinePaths, Is.EqualTo(new[] { "Lines\\L1", "Lines\\L2" }));
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("Setup Center", _log.Warnings[0]);
        }

        [Test]
        public void After_a_setup_center_failure_the_earlier_setup_center_lines_stay()
        {
            var lines = new SwitchableLines { Paths = new List<string> { "Lines\\L3" } };
            var dir = new StationDirectory(_monitoring, lines, _log);
            _monitoring.Stations.Add(St("Lines\\L1", "S1"));
            dir.Refresh();
            lines.Fail = true;
            _monitoring.Stations.Add(St("Lines\\L2", "S2"));

            dir.Refresh();

            Assert.That(dir.Stations, Has.Count.EqualTo(2), "Monitoring still updates");
            Assert.That(dir.LinePaths, Is.EqualTo(new[] { "Lines\\L1", "Lines\\L2", "Lines\\L3" }));
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_later_good_refresh_replaces_the_old_lists()
        {
            _monitoring.Stations.Add(St("Lines\\L1", "S1"));
            _dir.Refresh();
            _monitoring.Fail = true;
            _dir.Refresh();
            _monitoring.Fail = false;
            _monitoring.Stations.Clear();
            _monitoring.Stations.Add(St("Lines\\L9", "S9"));

            _dir.Refresh();

            Assert.That(_dir.Stations.Select(s => s.StationPath), Is.EqualTo(new[] { "S9" }));
            Assert.That(_dir.LinePaths, Is.EqualTo(new[] { "Lines\\L9" }));
        }

        [Test]
        public void Null_stations_in_the_answer_are_ignored()
        {
            _monitoring.Stations.Add(null);
            _monitoring.Stations.Add(St("Lines\\L1", "S1"));

            _dir.Refresh();

            Assert.That(_dir.Stations.Select(s => s.StationPath), Is.EqualTo(new[] { "S1" }));
        }

        [Test]
        public void Arguments_are_checked()
        {
            Assert.Throws<ArgumentNullException>(() => new StationDirectory(null, _sc, _log));
            Assert.Throws<ArgumentNullException>(() => new StationDirectory(_monitoring, null, _log));
            Assert.Throws<ArgumentNullException>(() => new StationDirectory(_monitoring, _sc, null));
        }

        // ---- Setup Center stand-ins that only answer GetLinePaths ----

        private sealed class SwitchableLines : SetupCenterStub
        {
            public List<string> Paths = new List<string>();
            public bool Fail;

            public override IList<string> GetLinePaths()
            {
                if (Fail)
                    throw new OibCallException("Setup Center not connected");
                return Paths.ToList();
            }
        }

        private sealed class ThrowingLines : SetupCenterStub
        {
            public override IList<string> GetLinePaths()
            {
                throw new OibCallException("Setup Center not connected");
            }
        }

        private abstract class SetupCenterStub : ISetupCenterClient
        {
            public abstract IList<string> GetLinePaths();

            public bool IsConnected() { throw new NotSupportedException(); }
            public bool HasLicense() { throw new NotSupportedException(); }
            public void LockPackagingUnits(IList<string> uids, string reason) { throw new NotSupportedException(); }
            public void UnlockPackagingUnits(IList<string> uids) { throw new NotSupportedException(); }
            public FeederMount FindMountedFeeder(string feederId) { throw new NotSupportedException(); }
            public void LockOnlineFeeder(FeederMount m, string reason) { throw new NotSupportedException(); }
            public void UnlockOnlineFeeder(FeederMount m) { throw new NotSupportedException(); }
            public IList<MachineRef> GetMachines() { throw new NotSupportedException(); }
            public IList<MountedPu> GetMountedPus(IList<string> machineIds) { throw new NotSupportedException(); }
        }
    }
}
