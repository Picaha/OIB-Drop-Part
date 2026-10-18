using System;
using System.Collections.Generic;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>A log that keeps the messages of each level in a list, so tests can assert on them.</summary>
    public sealed class ListLog : ILog
    {
        private readonly object _gate = new object();

        public List<string> Infos { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        public void Info(string m)
        {
            lock (_gate)
                Infos.Add(m);
        }

        public void Warn(string m)
        {
            lock (_gate)
                Warnings.Add(m);
        }

        /// <summary>Keeps the message only; the exception is not stored.</summary>
        public void Error(string m, Exception ex)
        {
            lock (_gate)
                Errors.Add(m);
        }
    }
}
