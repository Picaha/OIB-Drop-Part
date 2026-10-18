using System;

namespace Interlock.Core.Model
{
    /// <summary>One lock row. <see cref="Id"/> == 0 means the lock is not stored yet (spooled).</summary>
    public sealed class LockRecord
    {
        public long Id { get; set; }
        public LockType Type { get; set; }
        public string Target { get; set; }
        public string TargetNorm { get; set; }
        public Lanes? Lanes { get; set; }
        public string LinePath { get; set; }
        public string Reason { get; set; }
        public LockSource Source { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public LockStatus Status { get; set; }
        public string ReleasedBy { get; set; }
        public DateTime? ReleasedAtUtc { get; set; }
        public string ReleaseReason { get; set; }
        public EnforceState EnforceState { get; set; }
        public string EnforceError { get; set; }

        /// <summary>Shallow copy; all members are value types or immutable strings.</summary>
        public LockRecord Clone()
        {
            return (LockRecord)MemberwiseClone();
        }
    }
}
