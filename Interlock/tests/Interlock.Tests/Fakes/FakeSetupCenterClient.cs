using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>
    /// A Setup Center client for tests. Every call is recorded in <see cref="Calls"/> as <c>"Method"</c> or
    /// <c>"Method:id1,id2"</c> (UIDs, the feeder id, or machine ids). Reads answer from <see cref="Mounted"/>,
    /// <see cref="Machines"/> and <see cref="MountedPus"/>; the client is always connected and licensed.
    /// </summary>
    public sealed class FakeSetupCenterClient : ISetupCenterClient
    {
        public List<string> Calls { get; } = new List<string>();

        /// <summary>When true, the write calls (lock and unlock) record themselves and then throw <see cref="OibCallException"/>. Reads are not affected.</summary>
        public bool ThrowAlways { get; set; }

        /// <summary>Mounted feeders by feeder id.</summary>
        public Dictionary<string, FeederMount> Mounted { get; set; } = new Dictionary<string, FeederMount>();

        /// <summary>Called with the recorded text of every call (the entry just added to <see cref="Calls"/>), before the call can throw. Lets a test change the world while a call is in flight.</summary>
        public Action<string> OnCall { get; set; }

        public List<MachineRef> Machines { get; set; } = new List<MachineRef>();

        public List<MountedPu> MountedPus { get; set; } = new List<MountedPu>();

        public bool IsConnected()
        {
            Record("IsConnected");
            return true;
        }

        public bool HasLicense()
        {
            Record("HasLicense");
            return true;
        }

        public void LockPackagingUnits(IList<string> uids, string reason)
        {
            Record("LockPackagingUnits", uids);
            ThrowIfWritesFail("LockPackagingUnits");
        }

        public void UnlockPackagingUnits(IList<string> uids)
        {
            Record("UnlockPackagingUnits", uids);
            ThrowIfWritesFail("UnlockPackagingUnits");
        }

        public FeederMount FindMountedFeeder(string feederId)
        {
            Record("FindMountedFeeder", new[] { feederId });
            FeederMount mount;
            return feederId != null && Mounted.TryGetValue(feederId, out mount) ? mount : null;
        }

        public void LockOnlineFeeder(FeederMount m, string reason)
        {
            Record("LockOnlineFeeder", new[] { m.FeederId });
            ThrowIfWritesFail("LockOnlineFeeder");
        }

        public void UnlockOnlineFeeder(FeederMount m)
        {
            Record("UnlockOnlineFeeder", new[] { m.FeederId });
            ThrowIfWritesFail("UnlockOnlineFeeder");
        }

        public IList<MachineRef> GetMachines()
        {
            Record("GetMachines");
            return Machines.ToList();
        }

        /// <summary>Only the mounted PUs on the requested machines, like the real Setup Center query.</summary>
        public IList<MountedPu> GetMountedPus(IList<string> machineIds)
        {
            Record("GetMountedPus", machineIds);
            var wanted = new HashSet<string>(machineIds ?? new string[0]);
            return MountedPus.Where(p => wanted.Contains(p.MachineId)).ToList();
        }

        /// <summary>The distinct, non-blank lines of <see cref="Machines"/>.</summary>
        public IList<string> GetLinePaths()
        {
            Record("GetLinePaths");
            return Machines.Select(m => m.LinePath).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
        }

        private void Record(string method, IEnumerable<string> ids = null)
        {
            var call = ids == null ? method : method + ":" + string.Join(",", ids);
            Calls.Add(call);
            OnCall?.Invoke(call);
        }

        private void ThrowIfWritesFail(string method)
        {
            if (ThrowAlways)
                throw new OibCallException("Setup Center fake: " + method + " failed.");
        }
    }
}
