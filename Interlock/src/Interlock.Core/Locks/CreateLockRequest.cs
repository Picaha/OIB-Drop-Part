using Interlock.Core.Model;

namespace Interlock.Core.Locks
{
    /// <summary>A request to create a lock, as received from the UI or the API. <see cref="LockValidator"/> checks it.</summary>
    public sealed class CreateLockRequest
    {
        public LockType? Type { get; set; }
        public string Target { get; set; }
        public Lanes? Lanes { get; set; }
        public string LinePath { get; set; }
        public string Reason { get; set; }
    }
}
