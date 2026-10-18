using System;

namespace Interlock.Core.Model
{
    /// <summary>A station known to Setup Center, with the line it belongs to.</summary>
    public sealed class StationRef
    {
        public string LinePath { get; set; }
        public string StationPath { get; set; }
    }

    /// <summary>A machine known to Setup Center, with the line it belongs to.</summary>
    public sealed class MachineRef
    {
        public string MachineId { get; set; }
        public string LinePath { get; set; }
    }

    /// <summary>A packaging unit (reel) currently mounted on a machine.</summary>
    public sealed class MountedPu
    {
        public PuInfo Pu { get; set; }
        public string MachineId { get; set; }
    }

    /// <summary>A feeder as mounted on a machine. <see cref="Native"/> is the adapter's own object, handed back to it unchanged.</summary>
    public sealed class FeederMount
    {
        public string FeederId { get; set; }
        public string MachineId { get; set; }
        public string LinePath { get; set; }
        public object Native { get; set; }
    }

    /// <summary>Which conveyor lanes of a station are blocked.</summary>
    public sealed class ConveyorState
    {
        public bool RightBlocked { get; set; }
        public bool LeftBlocked { get; set; }

        /// <summary><see cref="Lanes.Both"/> is blocked only when both sides are.</summary>
        public bool IsBlocked(Lanes lanes)
        {
            switch (lanes)
            {
                case Lanes.Left: return LeftBlocked;
                case Lanes.Right: return RightBlocked;
                case Lanes.Both: return LeftBlocked && RightBlocked;
                default: throw new ArgumentOutOfRangeException("lanes", lanes, "Undefined Lanes value.");
            }
        }
    }
}
