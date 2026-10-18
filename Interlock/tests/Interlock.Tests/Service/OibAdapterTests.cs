using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Asm.As.Oib.SiplacePro.LineControl.Contracts;
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
        public void The_binding_opens_and_closes_within_bounded_times_but_keeps_the_oib_send_and_receive_timeouts()
        {
            var parameters = new Asm.As.Oib.Common.Utilities.BindingParameters(SetupCenterUri, true);

            var binding = OibEndpoints.SecureBinding(SetupCenterUri);

            Assert.That(binding.OpenTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(binding.CloseTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(binding.SendTimeout, Is.EqualTo(parameters.SendTimeout));
            Assert.That(binding.ReceiveTimeout, Is.EqualTo(parameters.ReceiveTimeout));
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

            using (var sc = new SetupCenterClient(SetupCenterUri, clock))
            using (var lc = new LineControlClient(LineControlUri, clock))
            using (var mon = new MonitoringClient(SetupCenterUri, clock))
            {
                Assert.That(sc, Is.Not.Null);
                Assert.That(lc, Is.Not.Null);
                Assert.That(mon, Is.Not.Null);
            }
        }
    }
}
