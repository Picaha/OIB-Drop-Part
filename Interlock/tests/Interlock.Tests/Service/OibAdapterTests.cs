using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Asm.As.Oib.SiplacePro.LineControl.Contracts;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Data;
using Interlock.Core.Model;
using Interlock.Service.Oib;
using Interlock.Tests.Fakes;
using NUnit.Framework;
using MonitoringConfiguration = Asm.As.Oib.Monitoring.Proxy.Business.Objects.Configuration;
using MonitoringLine = Asm.As.Oib.Monitoring.Proxy.Business.Objects.Line;
using MonitoringStation = Asm.As.Oib.Monitoring.Proxy.Business.Objects.Station;

namespace Interlock.Tests.Service
{
    /// <summary>The pure parts of the OIB endpoint helper and the Line Control and Monitoring clients. No endpoint is called.</summary>
    [TestFixture]
    public class OibAdapterTests
    {
        private const string SetupCenterUri = "net.tcp://adapterhost:1406/Asm.As.Oib.SetupCenter/SiplaceSetupCenter/Reliable";
        private const string LineControlUri = "net.tcp://adapterhost:1406/Asm.As.Oib.SiplacePro.LineControl/Secure";

        // ---- OibEndpoints ----

        [Test]
        public void The_secure_address_carries_the_asm_service_dns_identity()
        {
            var address = OibEndpoints.SecureAddress(SetupCenterUri);

            Assert.That(address.Uri, Is.EqualTo(new Uri(SetupCenterUri)));
            Assert.That(address.Identity, Is.EqualTo(EndpointIdentity.CreateDnsIdentity("ASM.SW.Service")));
        }

        [Test]
        public void A_blank_or_malformed_endpoint_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => OibEndpoints.SecureAddress(null));
            Assert.Throws<ArgumentException>(() => OibEndpoints.SecureAddress("  "));
            Assert.Throws<UriFormatException>(() => OibEndpoints.SecureAddress("not a uri"));
            Assert.Throws<ArgumentException>(() => OibEndpoints.SecureBinding(" "));
        }

        [Test]
        public void The_binding_follows_the_endpoint_name()
        {
            var reliable = (NetTcpBinding)OibEndpoints.SecureBinding(SetupCenterUri);
            var secure = (NetTcpBinding)OibEndpoints.SecureBinding(LineControlUri);

            Assert.That(reliable.ReliableSession.Enabled, Is.True);
            Assert.That(secure.Security.Mode, Is.EqualTo(SecurityMode.Transport));
        }

        [Test]
        public void The_binding_opens_closes_and_sends_within_bounded_times_and_leaves_the_receive_timeout_alone()
        {
            var parameters = new Asm.As.Oib.Common.Utilities.BindingParameters(SetupCenterUri, true);

            var binding = OibEndpoints.SecureBinding(SetupCenterUri);

            Assert.That(binding.OpenTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(binding.CloseTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(binding.SendTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)), "30 s unless asked otherwise");
            Assert.That(binding.ReceiveTimeout, Is.EqualTo(parameters.ReceiveTimeout));
        }

        [TestCase(60, 60)]
        [TestCase(30, 30)]
        [TestCase(5, 5)]
        [TestCase(120, 120)]
        [TestCase(1, 5)]
        [TestCase(0, 5)]
        [TestCase(-3, 5)]
        [TestCase(121, 120)]
        [TestCase(600, 120)]
        public void The_send_timeout_asked_for_is_kept_between_five_and_120_seconds(int requestedSeconds, int expectedSeconds)
        {
            var binding = OibEndpoints.SecureBinding(LineControlUri, TimeSpan.FromSeconds(requestedSeconds));

            Assert.That(binding.SendTimeout, Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
            Assert.That(OibEndpoints.ClampSendTimeout(TimeSpan.FromSeconds(requestedSeconds)), Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
        }

        [Test]
        public void No_send_timeout_asked_for_means_thirty_seconds()
        {
            Assert.That(OibEndpoints.ClampSendTimeout(null), Is.EqualTo(TimeSpan.FromSeconds(30)));
        }

        [Test]
        public void The_send_timeout_is_only_ever_shortened()
        {
            var oibDefault = new Asm.As.Oib.Common.Utilities.BindingParameters(SetupCenterUri, true).SendTimeout;

            var binding = OibEndpoints.SecureBinding(SetupCenterUri, TimeSpan.FromSeconds(120));

            Assert.That(oibDefault, Is.GreaterThan(TimeSpan.FromSeconds(120)), "the OIB default is long, so the cap applies");
            Assert.That(binding.SendTimeout, Is.EqualTo(TimeSpan.FromSeconds(120)));
        }

        [Test]
        public void Every_call_gets_its_own_binding_object()
        {
            Assert.That(OibEndpoints.SecureBinding(SetupCenterUri), Is.Not.SameAs(OibEndpoints.SecureBinding(SetupCenterUri)));
        }

        // ---- LineControlClient ----

        [Test]
        public void Lanes_map_to_the_conveyor_lanes_of_line_control()
        {
            Assert.That((int)LineControlClient.ToConveyorLanes(Lanes.Right), Is.EqualTo(0));
            Assert.That((int)LineControlClient.ToConveyorLanes(Lanes.Left), Is.EqualTo(1));
            Assert.That((int)LineControlClient.ToConveyorLanes(Lanes.Both), Is.EqualTo(2));
            Assert.That(LineControlClient.ToConveyorLanes(Lanes.Right), Is.EqualTo(ConveyorLanes.Right));
            Assert.That(LineControlClient.ToConveyorLanes(Lanes.Left), Is.EqualTo(ConveyorLanes.Left));
            Assert.That(LineControlClient.ToConveyorLanes(Lanes.Both), Is.EqualTo(ConveyorLanes.Both));
        }

        [Test]
        public void An_undefined_lanes_value_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LineControlClient.ToConveyorLanes((Lanes)99));
        }

        // ---- MonitoringClient ----

        private static MonitoringLine LineOf(string fullPath, params MonitoringStation[] stations)
        {
            var line = new MonitoringLine { FullPath = fullPath };
            foreach (var station in stations)
                line.Stations.Add(station);
            return line;
        }

        private static MonitoringConfiguration ConfigOf(params MonitoringLine[] lines)
        {
            var configuration = new MonitoringConfiguration();
            foreach (var line in lines)
                configuration.Lines.Add(line);
            return configuration;
        }

        [Test]
        public void Stations_come_from_the_lines_of_the_monitoring_configuration()
        {
            var c = ConfigOf(
                LineOf("Lines\\L1", new MonitoringStation { FullPath = "Lines\\L1\\S1" }, new MonitoringStation { FullPath = "Lines\\L1\\S2" }),
                LineOf("Lines\\L2", new MonitoringStation { FullPath = "Lines\\L2\\S1" }));

            var stations = MonitoringClient.ToStations(c);

            Assert.That(stations.Select(s => s.LinePath + "|" + s.StationPath), Is.EqualTo(new[] { "Lines\\L1|Lines\\L1\\S1", "Lines\\L1|Lines\\L1\\S2", "Lines\\L2|Lines\\L2\\S1" }));
        }

        [Test]
        public void Stations_skip_blank_paths_and_tolerate_missing_parts()
        {
            var c = ConfigOf(
                null,
                LineOf("Lines\\L0"),
                LineOf("Lines\\L1", null, new MonitoringStation { FullPath = " " }, new MonitoringStation { FullPath = null }, new MonitoringStation { FullPath = "Lines\\L1\\S1" }));

            Assert.That(MonitoringClient.ToStations(c).Select(s => s.StationPath), Is.EqualTo(new[] { "Lines\\L1\\S1" }));
            Assert.That(MonitoringClient.ToStations(null), Is.Empty);
            Assert.That(MonitoringClient.ToStations(new MonitoringConfiguration()), Is.Empty);
        }

        [Test]
        public void A_station_falls_back_to_the_line_path_the_station_reports_itself()
        {
            var c = ConfigOf(LineOf(" ", new MonitoringStation { FullPath = "Lines\\L1\\S1", LineFullPath = "Lines\\L1" }));

            Assert.That(MonitoringClient.ToStations(c).Single().LinePath, Is.EqualTo("Lines\\L1"));
        }

        // ---- SetupCenterClient ----

        private static LockFeederLocationInfo LockInfoOn(MachineLocator machine)
        {
            return new LockFeederLocationInfo { FeederId = "F1", Machine = machine };
        }

        [Test]
        public void A_feeder_lock_on_a_machine_without_a_line_is_logged()
        {
            var log = new ListLog();

            SetupCenterClient.WarnIfNoLine(LockInfoOn(new MachineLocator { MachineId = "m1" }), log);

            Assert.That(log.Warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("F1", log.Warnings[0]);
        }

        [Test]
        public void A_feeder_lock_on_a_machine_with_a_line_is_not_logged()
        {
            var log = new ListLog();

            SetupCenterClient.WarnIfNoLine(LockInfoOn(new MachineLocator { LinePath = "Lines\\L1" }), log);
#pragma warning disable CS0618
            SetupCenterClient.WarnIfNoLine(LockInfoOn(new MachineLocator { LineName = "L1" }), log);
#pragma warning restore CS0618

            Assert.That(log.Warnings, Is.Empty);
        }

        [Test]
        public void The_line_warning_needs_no_log()
        {
            Assert.DoesNotThrow(() => SetupCenterClient.WarnIfNoLine(LockInfoOn(new MachineLocator()), null));
        }

        // ---- constructors validate and never connect ----

        [Test]
        public void The_clients_check_their_arguments_and_do_not_connect_when_built()
        {
            var clock = new FakeClock();

            Assert.Throws<ArgumentException>(() => new SetupCenterClient(" ", clock));
            Assert.Throws<ArgumentNullException>(() => new SetupCenterClient(SetupCenterUri, null));
            Assert.Throws<ArgumentException>(() => new LineControlClient(null, clock));
            Assert.Throws<ArgumentNullException>(() => new LineControlClient(LineControlUri, null));
            Assert.Throws<ArgumentException>(() => new MonitoringClient("", clock));
            Assert.Throws<ArgumentNullException>(() => new MonitoringClient(SetupCenterUri, null));

            using (var sc = new SetupCenterClient(SetupCenterUri, clock, TimeSpan.FromSeconds(45), new ListLog()))
            using (var lc = new LineControlClient(LineControlUri, clock, TimeSpan.FromSeconds(45)))
            using (var mon = new MonitoringClient(SetupCenterUri, clock, TimeSpan.FromSeconds(45)))
            {
                Assert.That(sc, Is.Not.Null);
                Assert.That(lc, Is.Not.Null);
                Assert.That(mon, Is.Not.Null);
            }
        }
    }
}
