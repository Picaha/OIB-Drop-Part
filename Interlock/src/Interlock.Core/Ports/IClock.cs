using System;

namespace Interlock.Core.Ports
{
    /// <summary>The current time, as a port so tests can control it.</summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow
        {
            get { return DateTime.UtcNow; }
        }
    }
}
