using System;
using System.Collections.Generic;
using Interlock.Core.Ports;

namespace Interlock.Core.Security
{
    /// <summary>
    /// Slows down guessing of short PINs. A login first reserves a slot with <see cref="TryBegin"/>: attempts still in flight
    /// count toward the limit together with the failures of the last <see cref="Window"/>, so a burst of parallel requests cannot
    /// slip past the check before the first failure is recorded. At <see cref="MaxFailures"/> the key (username ignoring case, or
    /// remote address) is refused until <see cref="Window"/> after the failure that reached the limit (and after any later
    /// failure). There is no permanent lock-out, a success clears the username's failures, and idle entries are dropped lazily.
    /// In memory and safe to use from several threads.
    /// </summary>
    public sealed class LoginThrottle
    {
        public const int MaxFailures = 5;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        private const int SweepAboveCount = 256;

        private readonly IClock _clock;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();

        public LoginThrottle(IClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _clock = clock;
        }

        /// <summary>
        /// Reserves an attempt for this username and address (either may be null or blank: not checked). Returns false when the
        /// limit is reached, and then nothing is reserved. After true, exactly one of <see cref="RecordFailure"/>,
        /// <see cref="RecordSuccess"/> or <see cref="Abandon"/> must follow, in a <c>finally</c>.
        /// </summary>
        public bool TryBegin(string username, string address)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                if (_entries.Count > SweepAboveCount)
                    Sweep(now);

                var user = UserKey(username);
                var addr = AddressKey(address);
                if (Full(user, now) || Full(addr, now))
                    return false;

                Reserve(user);
                Reserve(addr);
                return true;
            }
        }

        /// <summary>Ends a reserved attempt as a failed login.</summary>
        public void RecordFailure(string username, string address)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                Fail(UserKey(username), now);
                Fail(AddressKey(address), now);
            }
        }

        /// <summary>Ends a reserved attempt as a successful login and clears the username's failures. The address keeps its count, so one valid account cannot reset the guessing of others.</summary>
        public void RecordSuccess(string username, string address)
        {
            lock (_gate)
            {
                Release(UserKey(username));
                Release(AddressKey(address));
                var user = UserKey(username);
                Entry e;
                if (user != null && _entries.TryGetValue(user, out e))
                {
                    e.Failures.Clear();
                    e.BlockedUntil = DateTime.MinValue;
                    Drop(user, e);
                }
            }
        }

        /// <summary>Ends a reserved attempt that was neither a failure nor a success (the login errored for another reason).</summary>
        public void Abandon(string username, string address)
        {
            lock (_gate)
            {
                Release(UserKey(username));
                Release(AddressKey(address));
            }
        }

        private static string UserKey(string username)
        {
            return string.IsNullOrWhiteSpace(username) ? null : "u:" + username.Trim().ToUpperInvariant();
        }

        private static string AddressKey(string address)
        {
            return string.IsNullOrWhiteSpace(address) ? null : "a:" + address.Trim();
        }

        private bool Full(string key, DateTime now)
        {
            Entry e;
            if (key == null || !_entries.TryGetValue(key, out e))
                return false;
            Prune(e, now);
            var full = now < e.BlockedUntil || e.Failures.Count + e.InFlight >= MaxFailures;
            if (!full)
                Drop(key, e);
            return full;
        }

        private void Reserve(string key)
        {
            if (key == null)
                return;
            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                e = new Entry();
                _entries[key] = e;
            }
            e.InFlight++;
        }

        private void Release(string key)
        {
            Entry e;
            if (key != null && _entries.TryGetValue(key, out e) && e.InFlight > 0)
                e.InFlight--;
        }

        private void Fail(string key, DateTime now)
        {
            if (key == null)
                return;
            Release(key);
            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                e = new Entry();
                _entries[key] = e;
            }
            Prune(e, now);
            e.Failures.Add(now);
            if (e.Failures.Count >= MaxFailures)
                e.BlockedUntil = now + Window;
        }

        private static void Prune(Entry e, DateTime now)
        {
            e.Failures.RemoveAll(t => now - t >= Window);
        }

        private void Drop(string key, Entry e)
        {
            if (e.Failures.Count == 0 && e.InFlight == 0)
                _entries.Remove(key);
        }

        private void Sweep(DateTime now)
        {
            foreach (var key in new List<string>(_entries.Keys))
            {
                var e = _entries[key];
                Prune(e, now);
                if (now >= e.BlockedUntil)
                    Drop(key, e);
            }
        }

        private sealed class Entry
        {
            public readonly List<DateTime> Failures = new List<DateTime>();
            public int InFlight;
            public DateTime BlockedUntil = DateTime.MinValue;
        }
    }
}
