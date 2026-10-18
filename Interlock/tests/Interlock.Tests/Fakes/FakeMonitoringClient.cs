using System;
using System.Collections.Generic;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>A Monitoring client for tests: answers with <see cref="Stations"/>, or throws <see cref="OibCallException"/> when <see cref="Down"/>.</summary>
    public sealed class FakeMonitoringClient : IMonitoringClient
    {
        public int Calls { get; private set; }

        public bool Down { get; set; }

        public List<StationRef> Stations { get; set; } = new List<StationRef>();

        public IList<StationRef> GetStations()
        {
            Calls++;
            if (Down)
                throw new OibCallException("Monitoring is down.");
            return Stations;
        }
    }
}
