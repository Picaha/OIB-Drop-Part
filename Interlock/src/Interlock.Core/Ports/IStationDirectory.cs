using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>The stations and lines the service last learned from OIB; reading it never calls OIB.</summary>
    public interface IStationDirectory
    {
        IList<StationRef> Stations { get; }
        IList<string> LinePaths { get; }
    }
}
