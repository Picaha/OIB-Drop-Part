using System.Collections.Generic;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>A station directory whose stations and lines are whatever the test puts in.</summary>
    public sealed class FakeStationDirectory : IStationDirectory
    {
        public IList<StationRef> Stations { get; set; } = new List<StationRef>();

        public IList<string> LinePaths { get; set; } = new List<string>();
    }
}
