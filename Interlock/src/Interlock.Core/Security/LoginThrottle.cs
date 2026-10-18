using System;
using System.Collections.Generic;
using Interlock.Core.Ports;

namespace Interlock.Core.Security
{
    /// <summary>
    /// Slows down guessing of short PINs: after <see cref="MaxFailures"/> failed logins within <see cref="Window"/> for the same
    /// username (ignoring case) or the same remote address, further logins for that key are refused until the window since the
    /// last failure has passed. There is no permanent lock-out, a success clears the username's counter, and expired entries are
    /// dropped lazily. In memory and safe to use from several threads.
    /// </summary>
    public sealed class LoginThrottle
    {
        public const int MaxFailures = 5;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        private const int SweepAboveCount = 256;

        private readonly IClock _clock;
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<DateTime>> _failures = new Dictionary<string, List<DateTime>>();

        public LoginThrottle(IClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _clock = clock;
        }

        /// <summary>True when this username or this address has used up its failed attempts. Either may be null or blank (not checked).</summary>
        public bool IsBlocked(string username, string address)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                return Blocked(UserKey(username), now) || Blocked(AddressKey(address), now);
            }
        }

        public void RecordFailure(string username, string address)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                if (_failures.Count > SweepAboveCount)
                    Sweep(now);
                Add(UserKey(username), now);
                Add(AddressKey(address), now);
            }
        }

        /// <summary>Clears the username's counter. The address keeps its count, so one valid account cannot reset the guessing of others.</summary>
        public void RecordSuccess(string username)
        {
            lock (_gate)
            {
                var key = UserKey(username);
                if (key != null)
                    _failures.Remove(key);
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

        private bool Blocked(string key, DateTime now)
        {
            List<DateTime> list;
            if (key == null || !_failures.TryGetValue(key, out list))
                return false;
            Prune(list, now);
            if (list.Count == 0)
            {
                _failures.Remove(key);
                return false;
            }
            return list.Count >= MaxFailures;
        }

        private void Add(string key, DateTime now)
        {
            if (key == null)
                return;
            List<DateTime> list;
            if (!_failures.TryGetValue(key, out list))
            {
                list = new List<DateTime>();
                _failures[key] = list;
            }
            Prune(list, now);
            list.Add(now);
        }

        private static void Prune(List<DateTime> list, DateTime now)
        {
            list.RemoveAll(t => now - t >= Window);
        }

        private void Sweep(DateTime now)
        {
            var dead = new List<string>();
            foreach (var pair in _failures)
            {
                Prune(pair.Value, now);
                if (pair.Value.Count == 0)
                    dead.Add(pair.Key);
            }
            foreach (var key in dead)
                _failures.Remove(key);
        }
    }
}
