using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>Holds locks created while the database was down (<c>Id == 0</c>) until it is back.</summary>
    public interface ILockSpool
    {
        void Append(LockRecord l);
        IList<LockRecord> ReadAll();
        void Clear();
    }
}
