using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>
    /// The Setup Center calls the service makes. A failed call throws <c>OibCallException</c>.
    /// Never call this from an External Control callback.
    /// </summary>
    public interface ISetupCenterClient
    {
        bool IsConnected();
        bool HasLicense();
        void LockPackagingUnits(IList<string> uids, string reason);
        void UnlockPackagingUnits(IList<string> uids);

        /// <summary>The mount of this feeder, or null when it is not mounted.</summary>
        FeederMount FindMountedFeeder(string feederId);

        void LockOnlineFeeder(FeederMount m, string reason);
        void UnlockOnlineFeeder(FeederMount m);
        IList<MachineRef> GetMachines();
        IList<MountedPu> GetMountedPus(IList<string> machineIds);
        IList<string> GetLinePaths();
    }
}
