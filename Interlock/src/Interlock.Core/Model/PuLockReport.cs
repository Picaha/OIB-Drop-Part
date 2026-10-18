using System;
using System.Collections.Generic;

namespace Interlock.Core.Model
{
    /// <summary>
    /// One lock entry Setup Center holds for a packaging unit (<c>LockInfo</c>). <see cref="Source"/> and <see cref="Reason"/>
    /// are Setup Center's own codes; <see cref="Reason"/> 1 is a component mismatch.
    /// </summary>
    public sealed class LockInfoRef
    {
        public int Source { get; set; }
        public int Reason { get; set; }
        public DateTime DateUtc { get; set; }
        public string Message { get; set; }
    }

    /// <summary>What Setup Center reports about the locks of one packaging unit when its lock state changes.</summary>
    public sealed class PuLockReport
    {
        public string Uid { get; set; }
        public IList<LockInfoRef> LockInfos { get; set; }
    }
}
