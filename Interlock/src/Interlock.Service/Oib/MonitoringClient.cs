using System;
using System.Collections.Generic;
using Asm.As.Oib.Monitoring.Proxy.Architecture.Objects;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using MonitoringConfiguration = Asm.As.Oib.Monitoring.Proxy.Business.Objects.Configuration;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// The stations of every line, from the Monitoring smart proxy. The proxy instance is the one
    /// <c>MonitoringManager.GetOrCreateInstance</c> returns; disposing it takes it out of the manager's cache, so a dropped
    /// proxy is really replaced. The failure policy is <see cref="OibConnection{TProxy}"/>'s.
    /// </summary>
    public sealed class MonitoringClient : IMonitoringClient, IDisposable
    {
        private const string ServiceName = "Monitoring";

        private readonly OibConnection<Monitoring> _connection;

        /// <param name="sendTimeout">How long a call may take; see <see cref="OibEndpoints.SecureBinding"/> (30 s when omitted).</param>
        public MonitoringClient(string endpointUri, IClock clock, TimeSpan? sendTimeout = null)
        {
            var address = OibEndpoints.SecureAddress(endpointUri);
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _connection = new OibConnection<Monitoring>(
                ServiceName,
                () => MonitoringManager.GetOrCreateInstance(address, OibEndpoints.SecureBinding(endpointUri, sendTimeout)),
                proxy => proxy.Dispose(),
                clock,
                proxy => proxy.GetConfiguration()); // a real read on the normal call path; also warms the proxy up
        }

        public IList<StationRef> GetStations()
        {
            return ToStations(_connection.Call("GetConfiguration", proxy => proxy.GetConfiguration()));
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        /// <summary>
        /// Every station that has a path, with the path of its line. The line path is the line's <c>FullPath</c>; when the line has
        /// none, the one the station reports for itself.
        /// </summary>
        internal static IList<StationRef> ToStations(MonitoringConfiguration configuration)
        {
            var stations = new List<StationRef>();
            if (configuration == null || configuration.Lines == null)
                return stations;

            foreach (var line in configuration.Lines)
            {
                if (line == null || line.Stations == null)
                    continue;
                foreach (var station in line.Stations)
                {
                    if (station == null || string.IsNullOrWhiteSpace(station.FullPath))
                        continue;
                    stations.Add(new StationRef
                    {
                        LinePath = string.IsNullOrWhiteSpace(line.FullPath) ? station.LineFullPath : line.FullPath,
                        StationPath = station.FullPath
                    });
                }
            }
            return stations;
        }
    }
}
