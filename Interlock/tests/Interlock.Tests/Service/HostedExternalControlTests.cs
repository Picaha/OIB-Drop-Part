using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceModel;
using System.ServiceModel.Configuration;
using System.ServiceModel.Description;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Service.ExternalControl;
using Interlock.Tests.Fakes;
using NUnit.Framework;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Data;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Service;

namespace Interlock.Tests.Service
{
    /// <summary>
    /// Runs <see cref="ExternalControlHost"/> inside another application domain whose configuration file is the one the test hands
    /// over: WCF reads the service model from the configuration of the application domain it runs in, so this is the only way to
    /// drive the host from a configuration file other than the one of the test run. Only strings cross the boundary.
    /// </summary>
    public sealed class ExternalControlHostRunner : MarshalByRefObject
    {
        private ListLog _log;
        private LockCache _cache;
        private DecisionLogBuffer _decisions;
        private ExternalControlHost _host;

        private void Build()
        {
            if (_host != null)
                return;
            _log = new ListLog();
            _cache = new LockCache();
            _cache.Replace(new[] { ExternalControlData.L(5, LockType.Pu, "U1"), ExternalControlData.L(3, LockType.Feeder, "F1") }, ExternalControlData.T0);
            _decisions = new DecisionLogBuffer(100, _log);
            _host = new ExternalControlHost(NewScService(), NewFeederService(), _log);
        }

        private SetupCenterExternalControlService NewScService()
        {
            return new SetupCenterExternalControlService(_cache, _decisions, new SystemClock(), _log);
        }

        private FeederExternalControlService NewFeederService()
        {
            return new FeederExternalControlService(_cache, _decisions, new SystemClock(), _log);
        }

        public void Start()
        {
            Build();
            _host.Open();
        }

        /// <summary>The type name of the exception <c>Open</c> throws, or "none".</summary>
        public string TryStart()
        {
            Build();
            try
            {
                _host.Open();
                return "none";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        public string[] Infos()
        {
            return _log.Infos.ToArray();
        }

        public string[] Warnings()
        {
            return _log.Warnings.ToArray();
        }

        public string[] Errors()
        {
            return _log.Errors.ToArray();
        }

        public void Stop()
        {
            _host.Close();
        }

        /// <summary>
        /// What WCF makes of the configuration file for each service, without opening anything: one line per endpoint
        /// (service, contract, binding, address) and one per service for the metadata behavior.
        /// </summary>
        public string[] DescribeConfiguredEndpoints()
        {
            Build();
            var lines = new List<string>();
            foreach (var service in new object[] { NewScService(), NewFeederService() })
            {
                var host = new ServiceHost(service);
                try
                {
                    var name = service.GetType().Name;
                    foreach (var endpoint in host.Description.Endpoints)
                        lines.Add(name + "|" + endpoint.Contract.Name + "|" + endpoint.Binding.Name + "|" + endpoint.Address.Uri.AbsoluteUri);
                    lines.Add(name + "|httpGetEnabled|" + host.Description.Behaviors.Find<ServiceMetadataBehavior>().HttpGetEnabled);
                }
                finally
                {
                    host.Abort();
                }
            }
            return lines.ToArray();
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }

    /// <summary>An application domain with <see cref="ExternalControlHostRunner"/> in it, using the given configuration file.</summary>
    internal sealed class RunnerDomain : IDisposable
    {
        private readonly AppDomain _domain;

        public RunnerDomain(string configurationFile)
        {
            _domain = AppDomain.CreateDomain("external-control-host", null, new AppDomainSetup
            {
                ApplicationBase = Path.GetDirectoryName(new Uri(typeof(ExternalControlHostRunner).Assembly.CodeBase).LocalPath),
                ConfigurationFile = configurationFile
            });
            Runner = (ExternalControlHostRunner)_domain.CreateInstanceAndUnwrap(
                typeof(ExternalControlHostRunner).Assembly.FullName, typeof(ExternalControlHostRunner).FullName);
        }

        public ExternalControlHostRunner Runner { get; }

        public void Dispose()
        {
            AppDomain.Unload(_domain);
        }
    }

    /// <summary>
    /// The External Control services behind a real WCF <c>ServiceHost</c> and a real <c>ChannelFactory</c> (net.tcp on a free
    /// localhost port, so no URL ACL is needed), and the host class with the configuration it reads.
    /// </summary>
    [TestFixture]
    public class HostedExternalControlTests
    {
        private const string ScServiceName = "Interlock.Service.ExternalControl.SetupCenterExternalControlService";
        private const string FeederServiceName = "Interlock.Service.ExternalControl.FeederExternalControlService";

        private LockCache cache;
        private ListLog log;
        private DecisionLogBuffer decisions;
        private SetupCenterExternalControlService sc;
        private FeederExternalControlService feeder;

        [SetUp]
        public void SetUp()
        {
            cache = new LockCache();
            log = new ListLog();
            decisions = new DecisionLogBuffer(1000, log);
            sc = new SetupCenterExternalControlService(cache, decisions, new FakeClock(), log);
            feeder = new FeederExternalControlService(cache, decisions, new FakeClock(), log);
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>A service host on net.tcp with a client channel to it. Ignores the test when this machine cannot bind.</summary>
        private sealed class Hosted<T> : IDisposable where T : class
        {
            private readonly ServiceHost _host;
            private readonly ChannelFactory<T> _factory;

            public Hosted(object service)
            {
                ServiceHost host = null;
                Uri address = null;
                string failure = null;
                for (var attempt = 0; attempt < 5 && host == null; attempt++)
                {
                    address = new Uri("net.tcp://localhost:" + FreePort() + "/ExternalControl");
                    var candidate = new ServiceHost(service, address);
                    candidate.AddServiceEndpoint(typeof(T), new NetTcpBinding(SecurityMode.None), string.Empty);
                    try
                    {
                        candidate.Open();
                        host = candidate;
                    }
                    catch (AddressAlreadyInUseException ex)
                    {
                        candidate.Abort();
                        failure = ex.Message; // somebody took the port between probing and binding: try another one
                    }
                    catch (Exception ex) when (ex is AddressAccessDeniedException || ex is PlatformNotSupportedException)
                    {
                        candidate.Abort();
                        Assert.Ignore("net.tcp cannot be hosted on this machine: " + ex.Message);
                    }
                    catch (Exception)
                    {
                        candidate.Abort();
                        throw;
                    }
                }
                if (host == null)
                    Assert.Ignore("No free net.tcp port could be bound: " + failure);

                _host = host;
                _factory = new ChannelFactory<T>(new NetTcpBinding(SecurityMode.None), new EndpointAddress(address));
                Client = _factory.CreateChannel();
            }

            public T Client { get; }

            public void Dispose()
            {
                CloseQuietly((ICommunicationObject)Client);
                CloseQuietly(_factory);
                CloseQuietly(_host);
            }

            private static void CloseQuietly(ICommunicationObject o)
            {
                try
                {
                    o.Close();
                }
                catch (Exception)
                {
                    o.Abort();
                }
            }
        }

        // ---- the contract types over a real channel -------------------------------------------------------------------

        [Test]
        public void Setup_center_contract_round_trips_with_cache_answers()
        {
            cache.Replace(new[] { ExternalControlData.L(5, LockType.Pu, "U1") }, ExternalControlData.T0);
            var expiry = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var locked = new PackagingUnitLocation
            {
                PackagingUnit = new PackagingUnit
                {
                    UID = "U1",
                    ComponentName = "C1",
                    ManufacturePartNumber = "MPN-1",
                    Supplier = "SUP-1",
                    Quantity = 1500,
                    OriginalQuantity = 5000,
                    ExpiryDate = expiry
                },
                LinePath = "Lines\\L1",
                MachineId = "M1"
            };
            var free = ExternalControlData.Loc("U2", "C2");

            using (var endpoint = new Hosted<ISiplaceSetupCenterExternalControl>(sc))
            {
                Assert.That(endpoint.Client.Ping(), Is.True);

                var results = endpoint.Client.GetPackagingUnitControlStatus(new[] { locked, free, new PackagingUnitLocation() });

                Assert.That(results.Select(r => r.ResultState), Is.EqualTo(new[] { 2, 1, 1 }));
                Assert.That(results[0].Messages.Select(m => m.Message), Is.EqualTo(new[] { "Interlock: PU U1 locked – r5 (#5)" }));
                Assert.That(results[1].Messages, Is.Null.Or.Empty);
                var echoed = results[0].PackagingUnit;
                Assert.That(new object[] { echoed.UID, echoed.ComponentName, echoed.ManufacturePartNumber, echoed.Supplier, echoed.Quantity, echoed.OriginalQuantity },
                    Is.EqualTo(new object[] { "U1", "C1", "MPN-1", "SUP-1", 1500, 5000 }));
                Assert.That(echoed.ExpiryDate.ToUniversalTime(), Is.EqualTo(expiry));
                Assert.That(results[1].PackagingUnit.UID, Is.EqualTo("U2"));

                var extended = endpoint.Client.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest
                {
                    Reason = 3,
                    PackagingUnitLocations = new[] { free, locked }
                });
                Assert.That(extended.ExternalControlResults.Select(r => r.ResultState), Is.EqualTo(new[] { 1, 2 }));

                var unknown = endpoint.Client.GetNewPackagingUnitData(new[] { locked, free });
                Assert.That(unknown.Select(r => r.ResultState), Is.EqualTo(new[] { 0, 0 }));
                Assert.That(unknown.Select(r => r.PackagingUnit), Is.All.Null);

                Assert.That(endpoint.Client.GetComponentsStatus(new ComponentStatusRequest()), Is.Not.Null);
                Assert.That(endpoint.Client.GetTrayCarrierMappingData(new TrayCarrierMappingDataRequest()), Is.Not.Null);
                Assert.That(endpoint.Client.UpdateTrayCarrierMappingData(new UpdateTrayCarrierMappingDataRequest()), Is.Not.Null);
            }

            var rows = new InMemoryAuditRepository();
            decisions.Flush(rows);
            Assert.That(rows.Decisions.Select(d => d.Method).Distinct(),
                Is.EquivalentTo(new[] { "GetPackagingUnitControlStatus", "GetPackagingUnitControlStatusExtended", "GetNewPackagingUnitData" }));
        }

        [Test]
        public void Feeder_contract_round_trips_with_cache_answers()
        {
            cache.Replace(new[] { ExternalControlData.L(3, LockType.Feeder, "F1") }, ExternalControlData.T0);
            var locked = ExternalControlData.Fl("F1", track: 4, machine: "M5");
            locked.Divisions = new[] { new Division { DivisionNumber = 2, CycleCount = 9 } };
            locked.DockingStation = new DockingStation { DockingStationId = "D1" };
            locked.FeederTypeSpiOidLong = 123456789012L;

            using (var endpoint = new Hosted<ISiplaceFeederExternalControl>(feeder))
            {
                Assert.That(endpoint.Client.FeederExternalControlPing(new PingRequest()), Is.Not.Null);

                var response = endpoint.Client.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest
                {
                    FeederLocations = new[] { locked, ExternalControlData.Fl("F2", track: 5) }
                });

                var info = response.FeederMaintenanceStatusInfos.Single();
                Assert.That(new object[] { info.FeederId, info.Track, info.ResultState, info.Location, info.TableId, info.FeederTypeSpiOidLong },
                    Is.EqualTo(new object[] { "F1", 4, 1, 7, "T-9", 123456789012L }));
                Assert.That(info.Reason, Is.EqualTo("Interlock: FEEDER F1 locked – r3 (#3)"));
                Assert.That(info.Machine.MachineId, Is.EqualTo("M5"));
                Assert.That(info.Machine.LinePath, Is.EqualTo("Lines\\L1"));
                Assert.That(info.DockingStation.DockingStationId, Is.EqualTo("D1"));
                Assert.That(info.Divisions.Single().DivisionNumber, Is.EqualTo(2));
                Assert.That(info.Divisions.Single().CycleCount, Is.EqualTo(9));

                var none = endpoint.Client.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { ExternalControlData.Fl("F2") } });
                Assert.That(none.FeederMaintenanceStatusInfos, Is.Empty);
            }
        }

        [Test]
        public void An_unexpected_failure_reaches_the_client_as_a_typed_fault()
        {
            var failing = new SetupCenterExternalControlService(cache, decisions, new ExternalControlData.ThrowingClock(), log);

            using (var endpoint = new Hosted<ISiplaceSetupCenterExternalControl>(failing))
            {
                var fault = Assert.Throws<FaultException<SiplaceSetupCenterFault>>(
                    () => endpoint.Client.GetPackagingUnitControlStatus(new[] { ExternalControlData.Loc("U1", "C1") }));

                Assert.That(fault.Detail.ErrorCode, Is.EqualTo(20000));
                Assert.That(fault.Detail.ExtendedMessage, Is.EqualTo("GetPackagingUnitControlStatus failed: clock down"));
            }
            Assert.That(log.Errors.Count, Is.EqualTo(1));
        }

        // ---- ExternalControlHost and its configuration ----------------------------------------------------------------

        private static string ShippedConfigPath()
        {
            var path = typeof(ExternalControlHost).Assembly.Location + ".config";
            Assert.That(File.Exists(path), Is.True, "the Service project's App.config must reach the output folder as " + path);
            return path;
        }

        [Test]
        public void Shipped_config_declares_both_services_over_basic_http_with_a_mex_endpoint()
        {
            var config = ConfigurationManager.OpenMappedExeConfiguration(new ExeConfigurationFileMap { ExeConfigFilename = ShippedConfigPath() }, ConfigurationUserLevel.None);
            var model = ServiceModelSectionGroup.GetSectionGroup(config);
            var services = model.Services.Services.Cast<ServiceElement>().ToDictionary(s => s.Name);

            Assert.That(services.Keys, Is.EquivalentTo(new[] { ScServiceName, FeederServiceName }));
            Assert.That(ScServiceName, Is.EqualTo(typeof(SetupCenterExternalControlService).FullName));
            Assert.That(FeederServiceName, Is.EqualTo(typeof(FeederExternalControlService).FullName));

            AssertService(model, services[ScServiceName], "http://localhost:8091/Interlock/SetupCenterExternalControl", typeof(ISiplaceSetupCenterExternalControl));
            AssertService(model, services[FeederServiceName], "http://localhost:8091/Interlock/FeederExternalControl", typeof(ISiplaceFeederExternalControl));
        }

        private static void AssertService(ServiceModelSectionGroup model, ServiceElement service, string baseAddress, Type contract)
        {
            Assert.That(service.Host.BaseAddresses.Cast<BaseAddressElement>().Select(b => b.BaseAddress), Is.EqualTo(new[] { baseAddress }), service.Name);

            var endpoints = service.Endpoints.Cast<ServiceEndpointElement>().ToList();
            Assert.That(endpoints.Select(e => new[] { e.Address.OriginalString, e.Binding, e.Contract }),
                Is.EqualTo(new[]
                {
                    new[] { string.Empty, "basicHttpBinding", contract.FullName },
                    new[] { "mex", "mexHttpBinding", "IMetadataExchange" }
                }),
                service.Name);

            var behavior = model.Behaviors.ServiceBehaviors[service.BehaviorConfiguration];
            var metadata = behavior.OfType<ServiceMetadataPublishingElement>().Single();
            Assert.That(metadata.HttpGetEnabled, Is.True, service.Name);
        }

        /// <summary>
        /// The shipped configuration after the documented on-site steps for net.tcp, and nothing else: (1) delete the active
        /// services element, (2) remove the comment markers around NETTCP-ALTERNATIVE, (3) set the two ports.
        /// </summary>
        private static string WriteNetTcpConfig(int scPort, int feederPort)
        {
            var doc = XDocument.Load(ShippedConfigPath());
            var model = doc.Root.Element("system.serviceModel");

            model.Element("services").Remove();

            var comment = model.Nodes().OfType<XComment>().Single(c => c.Value.Contains("NETTCP-ALTERNATIVE"));
            var inner = comment.Value.Replace("NETTCP-ALTERNATIVE", string.Empty);
            var services = XElement.Parse(inner);
            comment.ReplaceWith(services);

            var ports = new Dictionary<string, int> { { ScServiceName, scPort }, { FeederServiceName, feederPort } };
            foreach (var service in services.Elements("service"))
            {
                foreach (var add in service.Element("host").Element("baseAddresses").Elements("add"))
                {
                    var uri = new UriBuilder((string)add.Attribute("baseAddress")) { Port = ports[(string)service.Attribute("name")] };
                    add.SetAttributeValue("baseAddress", uri.Uri.AbsoluteUri);
                }
            }

            var configPath = Path.Combine(Path.GetTempPath(), "interlock-external-control-" + Guid.NewGuid().ToString("N") + ".config");
            doc.Save(configPath);
            return configPath;
        }

        private static readonly Regex ListenLine = new Regex(@"^External Control listening: (?<contract>\S+) at (?<uri>\S+)$");

        [Test]
        public void Shipped_config_builds_the_http_endpoints_for_both_services()
        {
            using (var domain = new RunnerDomain(ShippedConfigPath()))
            {
                var lines = domain.Runner.DescribeConfiguredEndpoints();

                Assert.That(lines, Is.EqualTo(new[]
                {
                    "SetupCenterExternalControlService|ISiplaceSetupCenterExternalControl|BasicHttpBinding|http://localhost:8091/Interlock/SetupCenterExternalControl",
                    "SetupCenterExternalControlService|IMetadataExchange|MetadataExchangeHttpBinding|http://localhost:8091/Interlock/SetupCenterExternalControl/mex",
                    "SetupCenterExternalControlService|httpGetEnabled|True",
                    "FeederExternalControlService|ISiplaceFeederExternalControl|BasicHttpBinding|http://localhost:8091/Interlock/FeederExternalControl",
                    "FeederExternalControlService|IMetadataExchange|MetadataExchangeHttpBinding|http://localhost:8091/Interlock/FeederExternalControl/mex",
                    "FeederExternalControlService|httpGetEnabled|True"
                }));
            }
        }

        [Test]
        public void Host_opens_both_services_from_the_configuration_file_and_closes_them()
        {
            var configPath = WriteNetTcpConfig(FreePort(), FreePort());
            try
            {
                using (var domain = new RunnerDomain(configPath))
                {
                    var runner = domain.Runner;
                    try
                    {
                        runner.Start();
                    }
                    catch (Exception ex) when (ex is AddressAccessDeniedException || ex is PlatformNotSupportedException)
                    {
                        Assert.Ignore("net.tcp cannot be hosted on this machine: " + ex.Message);
                    }

                    Assert.That(runner.Errors(), Is.Empty);
                    Assert.That(runner.Warnings(), Is.Empty);
                    Assert.That(runner.TryStart(), Is.EqualTo("InvalidOperationException"), "a host that is open cannot be opened again");

                    var scUri = ListenUrisByPath(runner.Infos(), "/Interlock/SetupCenterExternalControl");
                    var feederUri = ListenUrisByPath(runner.Infos(), "/Interlock/FeederExternalControl");
                    Assert.That(scUri.Keys, Is.EquivalentTo(new[] { "ISiplaceSetupCenterExternalControl", "IMetadataExchange" }));
                    Assert.That(feederUri.Keys, Is.EquivalentTo(new[] { "ISiplaceFeederExternalControl", "IMetadataExchange" }));
                    Assert.That(scUri["IMetadataExchange"].AbsoluteUri, Does.EndWith("/Interlock/SetupCenterExternalControl/mex"));
                    Assert.That(feederUri["IMetadataExchange"].AbsoluteUri, Does.EndWith("/Interlock/FeederExternalControl/mex"));
                    Assert.That(scUri["ISiplaceSetupCenterExternalControl"].Host, Is.Not.EqualTo("localhost"), "localhost is replaced by the machine name");

                    var scFactory = new ChannelFactory<ISiplaceSetupCenterExternalControl>(new NetTcpBinding(SecurityMode.None), new EndpointAddress(scUri["ISiplaceSetupCenterExternalControl"]));
                    var feederFactory = new ChannelFactory<ISiplaceFeederExternalControl>(new NetTcpBinding(SecurityMode.None), new EndpointAddress(feederUri["ISiplaceFeederExternalControl"]));
                    try
                    {
                        var scClient = scFactory.CreateChannel();
                        var feederClient = feederFactory.CreateChannel();

                        Assert.That(scClient.Ping(), Is.True);
                        var states = scClient.GetPackagingUnitControlStatus(new[] { ExternalControlData.Loc("U1", "C1"), ExternalControlData.Loc("U2", "C1") });
                        Assert.That(states.Select(s => s.ResultState), Is.EqualTo(new[] { 2, 1 }));
                        var blocked = feederClient.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest
                        {
                            FeederLocations = new[] { ExternalControlData.Fl("F1"), ExternalControlData.Fl("F2") }
                        });
                        Assert.That(blocked.FeederMaintenanceStatusInfos.Select(i => i.FeederId), Is.EqualTo(new[] { "F1" }));

                        var mex = new MetadataExchangeClient(MetadataExchangeBindings.CreateMexTcpBinding());
                        Assert.That(mex.GetMetadata(scUri["IMetadataExchange"], MetadataExchangeClientMode.MetadataExchange).MetadataSections, Is.Not.Empty);
                        Assert.That(mex.GetMetadata(feederUri["IMetadataExchange"], MetadataExchangeClientMode.MetadataExchange).MetadataSections, Is.Not.Empty);
                        CloseQuietly((ICommunicationObject)scClient);
                        CloseQuietly((ICommunicationObject)feederClient);
                    }
                    finally
                    {
                        CloseQuietly(scFactory);
                        CloseQuietly(feederFactory);
                    }

                    runner.Stop();
                    runner.Stop();

                    Assert.That(runner.Errors(), Is.Empty);
                    Assert.That(runner.Warnings(), Is.Empty);
                    Assert.That(Listens(scUri["ISiplaceSetupCenterExternalControl"]), Is.False);
                    Assert.That(Listens(feederUri["ISiplaceFeederExternalControl"]), Is.False);
                }
            }
            finally
            {
                File.Delete(configPath);
            }
        }

        [Test]
        public void Host_that_cannot_open_its_second_service_logs_it_aborts_the_first_and_rethrows_it_as_is()
        {
            var scPort = FreePort();
            var feederPort = FreePort();
            var configPath = WriteNetTcpConfig(scPort, feederPort);
            var blockers = Occupy(feederPort);
            try
            {
                using (var domain = new RunnerDomain(configPath))
                {
                    var runner = domain.Runner;

                    Assert.That(runner.TryStart(), Is.EqualTo("AddressAlreadyInUseException"));

                    Assert.That(runner.Errors().Length, Is.EqualTo(1));
                    StringAssert.Contains("FeederExternalControlService", runner.Errors()[0]);
                    Assert.That(runner.Infos(), Is.Empty, "it must not claim to listen");
                    Assert.DoesNotThrow(() => Release(Occupy(scPort)), "the port of the first service is free again, so its host was aborted");

                    Release(blockers);
                    Assert.That(runner.TryStart(), Is.EqualTo("none"), "after the cause is gone the same host opens");
                    runner.Stop();
                }
            }
            finally
            {
                Release(blockers);
                File.Delete(configPath);
            }
        }

        /// <summary>Takes a port on every address family this machine has, so that a service host cannot bind to it.</summary>
        private static List<TcpListener> Occupy(int port)
        {
            var listeners = new List<TcpListener> { new TcpListener(IPAddress.Any, port) };
            if (Socket.OSSupportsIPv6)
                listeners.Add(new TcpListener(IPAddress.IPv6Any, port));
            foreach (var listener in listeners)
                listener.Start();
            return listeners;
        }

        private static void Release(IEnumerable<TcpListener> listeners)
        {
            foreach (var listener in listeners)
                listener.Stop();
        }

        /// <summary>True when something accepts a connection at the host and port of the address.</summary>
        private static bool Listens(Uri address)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    return client.ConnectAsync(address.Host, address.Port).Wait(TimeSpan.FromSeconds(5)) && client.Connected;
                }
                catch (AggregateException)
                {
                    return false;
                }
            }
        }

        private static Dictionary<string, Uri> ListenUrisByPath(IEnumerable<string> infos, string pathPrefix)
        {
            var uris = new Dictionary<string, Uri>();
            foreach (var info in infos)
            {
                var match = ListenLine.Match(info);
                if (match.Success)
                {
                    var uri = new Uri(match.Groups["uri"].Value);
                    if (uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal))
                        uris[match.Groups["contract"].Value] = uri;
                }
            }
            return uris;
        }

        private static void CloseQuietly(ICommunicationObject o)
        {
            try
            {
                o.Close();
            }
            catch (Exception)
            {
                o.Abort();
            }
        }

        [Test]
        public void Host_that_cannot_open_logs_the_failure_and_rethrows_it()
        {
            // The test run's own configuration holds no service model, so no endpoint is configured for the services.
            var host = new ExternalControlHost(sc, feeder, log);

            var first = Assert.Catch(() => host.Open());
            var second = Assert.Catch(() => host.Open());

            Assert.That(first, Is.InstanceOf<InvalidOperationException>());
            Assert.That(second, Is.InstanceOf<InvalidOperationException>(), "a failed Open leaves the host closed, so it can be tried again");
            Assert.That(log.Errors.Count, Is.EqualTo(2));
            StringAssert.Contains(first.Message, log.Errors[0]);
            Assert.That(log.Infos, Is.Empty);
            Assert.DoesNotThrow(host.Close);
            Assert.DoesNotThrow(host.Dispose);
        }

        [Test]
        public void Close_without_open_and_dispose_are_quiet()
        {
            var host = new ExternalControlHost(sc, feeder, log);

            Assert.DoesNotThrow(host.Close);
            Assert.DoesNotThrow(host.Close);
            Assert.DoesNotThrow(host.Dispose);
            Assert.That(log.Infos, Is.Empty);
            Assert.That(log.Warnings, Is.Empty);
            Assert.That(log.Errors, Is.Empty);
        }

        [Test]
        public void Host_requires_its_services_and_a_log()
        {
            Assert.Throws<ArgumentNullException>(() => new ExternalControlHost(null, feeder, log));
            Assert.Throws<ArgumentNullException>(() => new ExternalControlHost(sc, null, log));
            Assert.Throws<ArgumentNullException>(() => new ExternalControlHost(sc, feeder, null));
        }
    }
}
