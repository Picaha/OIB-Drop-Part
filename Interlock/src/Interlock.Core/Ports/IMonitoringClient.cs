using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    public interface IMonitoringClient
    {
        IList<StationRef> GetStations();
    }
}
