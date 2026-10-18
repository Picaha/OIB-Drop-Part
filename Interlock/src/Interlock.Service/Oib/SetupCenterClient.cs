using System;
using System.Collections.Generic;
using System.Linq;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Data;
using Asm.As.Oib.SiplaceSetupCenter.Proxy.Business.Objects;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// The Setup Center calls the service makes, over the smart proxy. The proxy is created on the first call; the failure policy
    /// (backoff, dropping a dead proxy, wrapping every exception in <c>OibCallException</c>) is <see cref="OibConnection{TProxy}"/>.
    /// All mapping is in <see cref="SetupCenterMapper"/>. A <c>ResultState</c> other than OK is a business failure: it throws
    /// <c>OibCallException</c> but does not count as a lost connection.
    /// Never call this from an External Control callback.
    /// </summary>
    public sealed class SetupCenterClient : ISetupCenterClient, IDisposable
    {
        private const string ServiceName = "Setup Center";

        private readonly OibConnection<SiplaceSetupCenter> _connection;
        private readonly ILog _log;

        /// <param name="sendTimeout">How long a call may take; see <see cref="OibEndpoints.SecureBinding"/> (30 s when omitted).</param>
        /// <param name="log">Where warnings go; none when omitted.</param>
        public SetupCenterClient(string endpointUri, IClock clock, TimeSpan? sendTimeout = null, ILog log = null)
        {
            var address = OibEndpoints.SecureAddress(endpointUri);
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _log = log;
            _connection = new OibConnection<SiplaceSetupCenter>(
                ServiceName,
                () => new SiplaceSetupCenter(address, OibEndpoints.SecureBinding(endpointUri, sendTimeout)),
                proxy => proxy.Dispose(),
                clock,
                proxy => proxy.IsConnected()); // the first call opens the channel; do it once, before other threads share the proxy
        }

        /// <summary>
        /// True only when Setup Center and its database are connected. When the service is down the proxy answers false without
        /// throwing, so a false answer proves nothing about the connection and leaves the backoff alone.
        /// </summary>
        public bool IsConnected()
        {
            return _connection.Call("IsConnected", proxy => proxy.IsConnected(), connected => connected);
        }

        // OIB 6.6 and later need no adapter license, so the OIB marks HasAdapterLicense obsolete. The port still reports it for the
        // health check; what a current core answers is to be confirmed on the line.
        public bool HasLicense()
        {
#pragma warning disable CS0618
            // The answer says nothing about the connection (a dead endpoint still answers true), so it never resets the backoff.
            return _connection.Call("HasAdapterLicense", proxy => proxy.HasAdapterLicense(), licensed => false);
#pragma warning restore CS0618
        }

        public void LockPackagingUnits(IList<string> uids, string reason)
        {
            var units = ToUnits(uids);
            if (units.Count == 0)
                return;
            var response = _connection.Call("LockPackagingUnits", proxy => proxy.LockPackagingUnits(units, reason));
            SetupCenterMapper.ThrowIfFailed("LockPackagingUnits", response, units.Select(u => u.UID));
        }

        public void UnlockPackagingUnits(IList<string> uids)
        {
            var units = ToUnits(uids);
            if (units.Count == 0)
                return;
            var response = _connection.Call("UnlockPackagingUnits", proxy => proxy.UnlockPackagingUnits(units));
            SetupCenterMapper.ThrowIfFailed("UnlockPackagingUnits", response, units.Select(u => u.UID));
        }

        public FeederMount FindMountedFeeder(string feederId)
        {
            if (string.IsNullOrWhiteSpace(feederId))
                return null;
            var request = new GetPackagingUnitLocationsOnFeedersRequest { FeederIds = new List<string> { feederId.Trim() } };
            var response = _connection.Call("GetPackagingUnitLocationsOnFeeders", proxy => proxy.GetPackagingUnitLocationsOnFeeders(request));
            return SetupCenterMapper.ToFeederMount(feederId, response);
        }

        public void LockOnlineFeeder(FeederMount m, string reason)
        {
            var info = SetupCenterMapper.ToLockInfo(m, reason);
            WarnIfNoLine(info, _log);
            var request = new LockOnlineFeedersRequest { LockFeederLocationInfos = new List<LockFeederLocationInfo> { info } };
            var response = _connection.Call("LockOnlineFeeders", proxy => proxy.LockOnlineFeeders(request));
            SetupCenterMapper.ThrowIfFailed("LockOnlineFeeders", response);
        }

        public void UnlockOnlineFeeder(FeederMount m)
        {
            var request = new UnlockOnlineFeedersRequest { FeederLocations = new List<FeederLocation> { SetupCenterMapper.ToFeederLocation(m) } };
            var response = _connection.Call("UnlockOnlineFeeders", proxy => proxy.UnlockOnlineFeeders(request));
            SetupCenterMapper.ThrowIfFailed("UnlockOnlineFeeders", response);
        }

        public IList<MachineRef> GetMachines()
        {
            return SetupCenterMapper.ToMachines(GetConfiguration());
        }

        public IList<MountedPu> GetMountedPus(IList<string> machineIds)
        {
            var ids = (machineIds ?? new string[0]).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count == 0)
                return new List<MountedPu>();
            var setups = _connection.Call("GetActualSetups", proxy => proxy.GetActualSetups(ids));
            return SetupCenterMapper.ToMountedPus(setups);
        }

        public IList<string> GetLinePaths()
        {
            return SetupCenterMapper.ToLinePaths(GetConfiguration());
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        /// <summary>
        /// Setup Center documents the machine's line name as mandatory for locking a feeder, but only the External Control path is
        /// known to fill it. A location with neither a line name nor a line path is logged before the call, so a refusal is explained.
        /// </summary>
        internal static void WarnIfNoLine(LockFeederLocationInfo info, ILog log)
        {
            if (log != null && info != null && !SetupCenterMapper.HasLine(info))
                log.Warn("Feeder " + info.FeederId + " has neither a line name nor a line path on its Setup Center machine; LockOnlineFeeders may refuse it.");
        }

        private Configuration GetConfiguration()
        {
            return _connection.Call("GetConfiguration", proxy => proxy.GetConfiguration());
        }

        private static List<PackagingUnit> ToUnits(IList<string> uids)
        {
            return (uids ?? new string[0])
                .Where(uid => !string.IsNullOrWhiteSpace(uid))
                .Select(uid => new PackagingUnit { UID = uid })
                .ToList();
        }
    }
}
