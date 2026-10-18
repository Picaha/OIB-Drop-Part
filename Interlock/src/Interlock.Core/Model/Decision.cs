using System.Collections.Generic;

namespace Interlock.Core.Model
{
    /// <summary>Result of a scan check. <see cref="Message"/> is null when <see cref="Ok"/>; <see cref="MatchedLockIds"/> is never null.</summary>
    public sealed class Decision
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public IList<long> MatchedLockIds { get; set; } = new List<long>();
    }
}
