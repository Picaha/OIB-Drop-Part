using System;
using System.Collections.Generic;
using System.Globalization;

namespace Interlock.Core.Enforcement
{
    /// <summary>The most recent alarms for the operator UI. Keeps the last 50; safe to use from several threads.</summary>
    public sealed class AlarmBoard
    {
        private const int Capacity = 50;

        private readonly object _gate = new object();
        private readonly List<string> _newestFirst = new List<string>();

        public void Raise(string text, DateTime atUtc)
        {
            var line = atUtc.ToString("o", CultureInfo.InvariantCulture) + " " + text;
            lock (_gate)
            {
                _newestFirst.Insert(0, line);
                if (_newestFirst.Count > Capacity)
                    _newestFirst.RemoveAt(_newestFirst.Count - 1);
            }
        }

        /// <summary>Up to <paramref name="max"/> alarms, newest first, as <c>"&lt;ISO time&gt; &lt;text&gt;"</c>.</summary>
        public IList<string> Recent(int max)
        {
            lock (_gate)
                return max <= 0 ? new List<string>() : _newestFirst.GetRange(0, Math.Min(max, _newestFirst.Count));
        }
    }
}
