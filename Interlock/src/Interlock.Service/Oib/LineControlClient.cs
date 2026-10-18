using System;
using Asm.As.Oib.SiplacePro.LineControl.Contracts;
using Asm.As.Oib.SiplacePro.LineControl.Proxy.Business.Objects;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// The Line Control conveyor calls, over the smart proxy. The failure policy is <see cref="OibConnection{TProxy}"/>'s. A
    /// block or unblock the proxy answers with <c>false</c> throws <c>OibCallException</c>, but is not a lost connection.
    /// </summary>
    public sealed class LineControlClient : ILineControlClient, IDisposable
    {
        private const string ServiceName = "Line Control";

        private readonly OibConnection<LineControl> _connection;

        /// <param name="sendTimeout">How long a call may take; see <see cref="OibEndpoints.SecureBinding"/> (30 s when omitted).</param>
        public LineControlClient(string endpointUri, IClock clock, TimeSpan? sendTimeout = null)
        {
            var address = OibEndpoints.SecureAddress(endpointUri);
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _connection = new OibConnection<LineControl>(
                ServiceName,
                () => new LineControl(address, OibEndpoints.SecureBinding(endpointUri, sendTimeout)),
                proxy => proxy.Dispose(),
                clock,
                proxy => proxy.IsServiceAlive()); // the first call opens the channel; do it once, before other threads share the proxy
        }

        // OIB 6.6 and later need no adapter license, so the OIB marks HasAdapterLicense obsolete. The port still reports it for the
        // health check; what a current core answers is to be confirmed on the line.
        public bool HasLicense()
        {
#pragma warning disable CS0618
            // The answer says nothing about the connection, so it never resets the backoff.
            return _connection.Call("HasAdapterLicense", proxy => proxy.HasAdapterLicense(), licensed => false);
#pragma warning restore CS0618
        }

        public bool IsBlockSupported(string stationPath)
        {
            RequireStation(stationPath);
            return _connection.Call("IsStationConveyorBlockSupported", proxy => proxy.IsStationConveyorBlockSupported(stationPath));
        }

        public void Block(string stationPath, Lanes lanes, string details)
        {
            RequireStation(stationPath);
            var conveyorLanes = ToConveyorLanes(lanes);
            var done = _connection.Call("BlockStationConveyor", proxy => proxy.BlockStationConveyor(stationPath, conveyorLanes, details));
            if (!done)
                throw new OibCallException("Line Control refused to block the " + lanes + " conveyor of " + stationPath);
        }

        public void Unblock(string stationPath, Lanes lanes, string details)
        {
            RequireStation(stationPath);
            var conveyorLanes = ToConveyorLanes(lanes);
            var done = _connection.Call("UnblockStationConveyor", proxy => proxy.UnblockStationConveyor(stationPath, conveyorLanes, details));
            if (!done)
                throw new OibCallException("Line Control refused to unblock the " + lanes + " conveyor of " + stationPath);
        }

        public ConveyorState GetState(string stationPath)
        {
            RequireStation(stationPath);
            return _connection.Call("IsStationConveyorBlocked", proxy =>
            {
                bool rightBlocked;
                bool leftBlocked;
                proxy.IsStationConveyorBlocked(stationPath, out rightBlocked, out leftBlocked);
                return new ConveyorState { RightBlocked = rightBlocked, LeftBlocked = leftBlocked };
            });
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        /// <summary>Line Control numbers the lanes Right 0, Left 1, Both 2.</summary>
        internal static ConveyorLanes ToConveyorLanes(Lanes lanes)
        {
            switch (lanes)
            {
                case Lanes.Right: return ConveyorLanes.Right;
                case Lanes.Left: return ConveyorLanes.Left;
                case Lanes.Both: return ConveyorLanes.Both;
                default: throw new ArgumentOutOfRangeException(nameof(lanes), lanes, "Undefined Lanes value.");
            }
        }

        private static void RequireStation(string stationPath)
        {
            if (string.IsNullOrWhiteSpace(stationPath))
                throw new ArgumentException("A station path is required.", nameof(stationPath));
        }
    }
}
