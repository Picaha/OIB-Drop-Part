using System;
using Interlock.Core.Model;

namespace Interlock.Core.Enforcement
{
    public enum JobKind { Apply, Release, Reblock }

    /// <summary>One piece of OIB work for a lock. The worker raises <see cref="FailedAttempts"/> and <see cref="NotBeforeUtc"/> when it queues a retry.</summary>
    public sealed class EnforcementJob
    {
        public LockRecord Lock { get; set; }
        public JobKind Kind { get; set; }
        public int FailedAttempts { get; set; }

        /// <summary>The job is due once the clock reaches this time.</summary>
        public DateTime NotBeforeUtc { get; set; }
    }
}
