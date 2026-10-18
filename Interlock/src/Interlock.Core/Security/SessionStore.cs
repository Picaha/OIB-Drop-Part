using System;
using System.Collections.Generic;
using Interlock.Core.Ports;

namespace Interlock.Core.Security
{
    /// <summary>In-memory login sessions. Thread-safe: REST requests resolve and create sessions concurrently.</summary>
    public sealed class SessionStore
    {
        private sealed class Session
        {
            public Principal Principal;
            public DateTime ExpiresAtUtc;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly IClock _clock;
        private readonly TimeSpan _lifetime;

        public SessionStore(IClock clock, TimeSpan lifetime)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));
            if (lifetime <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(lifetime), "Session lifetime must be positive.");

            _clock = clock;
            _lifetime = lifetime;
        }

        /// <summary>Starts a session and returns its token (64 lower-case hex characters).</summary>
        public string Create(Principal p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            // Same shape as an API key: 256 random bits, hex encoded.
            var token = ApiKeyHasher.NewKey();
            var session = new Session { Principal = p, ExpiresAtUtc = _clock.UtcNow + _lifetime };

            lock (_gate)
                _sessions[token] = session;

            return token;
        }

        /// <summary>The principal for a live session; <c>null</c> for a blank, unknown or expired token (expired ones are removed).</summary>
        public Principal Resolve(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            lock (_gate)
            {
                Session session;
                if (!_sessions.TryGetValue(token, out session))
                    return null;

                if (_clock.UtcNow >= session.ExpiresAtUtc)
                {
                    _sessions.Remove(token);
                    return null;
                }

                return session.Principal;
            }
        }

        /// <exception cref="KeyNotFoundException">The token is unknown.</exception>
        public DateTime ExpiresAtUtc(string token)
        {
            lock (_gate)
            {
                Session session;
                if (token == null || !_sessions.TryGetValue(token, out session))
                    throw new KeyNotFoundException("Unknown session token.");

                return session.ExpiresAtUtc;
            }
        }
    }
}
