using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>The Line Control conveyor calls. A failed call throws <c>OibCallException</c>.</summary>
    public interface ILineControlClient
    {
        bool HasLicense();
        bool IsBlockSupported(string stationPath);
        void Block(string stationPath, Lanes lanes, string details);
        void Unblock(string stationPath, Lanes lanes, string details);
        ConveyorState GetState(string stationPath);
    }
}
