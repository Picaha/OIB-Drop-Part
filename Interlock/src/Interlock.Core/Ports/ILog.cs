using System;

namespace Interlock.Core.Ports
{
    public interface ILog
    {
        void Info(string m);
        void Warn(string m);
        void Error(string m, Exception ex);
    }
}
