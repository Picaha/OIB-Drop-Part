using System;

namespace Interlock.Core.Model
{
    /// <summary>The names written to <c>action_log.action</c>.</summary>
    public static class Actions
    {
        public const string LOCK_PU = "LOCK_PU";
        public const string UNLOCK_PU = "UNLOCK_PU";
        public const string LOCK_FEEDER_ONLINE = "LOCK_FEEDER_ONLINE";
        public const string UNLOCK_FEEDER_ONLINE = "UNLOCK_FEEDER_ONLINE";
        public const string BLOCK_CONVEYOR = "BLOCK_CONVEYOR";
        public const string UNBLOCK_CONVEYOR = "UNBLOCK_CONVEYOR";
        public const string SWEEP = "SWEEP";
        public const string REBLOCK_CONVEYOR = "REBLOCK_CONVEYOR";
    }

    /// <summary>One OIB call made on behalf of a lock. <see cref="LockId"/> is null for calls that belong to no lock.</summary>
    public sealed class ActionLogEntry
    {
        public long Id { get; set; }
        public long? LockId { get; set; }
        public string Action { get; set; }
        public string RequestJson { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Attempt { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>One External Control answer. <see cref="MatchedLockIds"/> is the ids joined as text.</summary>
    public sealed class DecisionLogEntry
    {
        public string Method { get; set; }
        public int? ReasonCode { get; set; }
        public string Uid { get; set; }
        public string Pn { get; set; }
        public string Mpn { get; set; }
        public string Supplier { get; set; }
        public string LinePath { get; set; }
        public string MachineId { get; set; }
        public string FeederId { get; set; }
        public string Result { get; set; }
        public string Message { get; set; }
        public string MatchedLockIds { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>Filters for the decision log. A null or blank text filter means no filter.</summary>
    public sealed class DecisionQuery
    {
        public string Uid { get; set; }
        public string Pn { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public int Limit { get; set; } = 500;
    }
}
