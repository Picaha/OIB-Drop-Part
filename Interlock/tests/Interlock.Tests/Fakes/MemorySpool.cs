using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>A lock spool that keeps copies of the appended locks in memory.</summary>
    public sealed class MemorySpool : ILockSpool
    {
        private readonly object _gate = new object();
        private readonly List<LockRecord> _locks = new List<LockRecord>();

        public void Append(LockRecord l)
        {
            lock (_gate)
                _locks.Add(l.Clone());
        }

        public IList<LockRecord> ReadAll()
        {
            lock (_gate)
                return _locks.Select(l => l.Clone()).ToList();
        }

        public void Clear()
        {
            lock (_gate)
                _locks.Clear();
        }
    }
}
