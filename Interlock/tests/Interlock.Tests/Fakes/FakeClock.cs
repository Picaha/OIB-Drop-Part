using System;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>A clock tests can set and advance by hand.</summary>
    public sealed class FakeClock : IClock
    {
        public FakeClock()
        {
            UtcNow = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        }

        public DateTime UtcNow { get; set; }

        public void Advance(TimeSpan by)
        {
            UtcNow = UtcNow + by;
        }
    }
}
