using System.Collections.Generic;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>
    /// A Line Control client for tests. Every call is recorded in <see cref="Calls"/> (<c>"Block:S1:BOTH"</c>,
    /// <c>"GetState:S1"</c>, <c>"HasLicense"</c> and so on) before it can fail. The client is always licensed.
    /// </summary>
    public sealed class FakeLineControlClient : ILineControlClient
    {
        public List<string> Calls { get; } = new List<string>();

        /// <summary>The state <see cref="GetState"/> returns per station path. A station not listed reads as not blocked.</summary>
        public Dictionary<string, ConveyorState> States { get; set; } = new Dictionary<string, ConveyorState>();

        /// <summary>The answer of <see cref="IsBlockSupported"/> for every station.</summary>
        public bool Supported { get; set; } = true;

        /// <summary>When true, every member (reads too) records itself and then throws <see cref="OibCallException"/>.</summary>
        public bool ThrowAlways { get; set; }

        public bool HasLicense()
        {
            Record("HasLicense");
            return true;
        }

        public bool IsBlockSupported(string stationPath)
        {
            Record("IsBlockSupported:" + stationPath);
            return Supported;
        }

        public void Block(string stationPath, Lanes lanes, string details)
        {
            Record("Block:" + stationPath + ":" + DbEnum.ToDb(lanes));
        }

        public void Unblock(string stationPath, Lanes lanes, string details)
        {
            Record("Unblock:" + stationPath + ":" + DbEnum.ToDb(lanes));
        }

        public ConveyorState GetState(string stationPath)
        {
            Record("GetState:" + stationPath);
            ConveyorState state;
            return stationPath != null && States.TryGetValue(stationPath, out state) ? state : new ConveyorState();
        }

        private void Record(string call)
        {
            Calls.Add(call);
            if (ThrowAlways)
                throw new OibCallException("Line Control fake: " + call + " failed.");
        }
    }
}
