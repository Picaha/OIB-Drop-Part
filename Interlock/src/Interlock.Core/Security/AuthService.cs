using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Core.Security
{
    /// <summary>A successful login: the session token to send as a bearer token, the user's role and when the session ends.</summary>
    public sealed class LoginResult
    {
        public LoginResult(string token, Role role, DateTime expiresAtUtc)
        {
            Token = token;
            Role = role;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string Token { get; }

        public Role Role { get; }

        public DateTime ExpiresAtUtc { get; }
    }

    /// <summary>A new API key: the stored record and the plain key. The plain key exists only here; the database holds its hash.</summary>
    public sealed class ApiKeyCreated
    {
        public ApiKeyCreated(ApiKeyRecord record, string plainKey)
        {
            Record = record;
            PlainKey = plainKey;
        }

        public ApiKeyRecord Record { get; }

        public string PlainKey { get; }
    }

    /// <summary>
    /// Login with username and PIN, authentication of a bearer token or an API key, and the admin operations on users and API keys.
    /// Every admin operation needs <see cref="Permissions.CanAdmin"/> (otherwise <see cref="ErrorKind.Forbidden"/>), and a database
    /// outage reaches the caller as <see cref="ErrorKind.Unavailable"/>.
    /// </summary>
    public sealed class AuthService
    {
        private const int MinPinLength = 4;
        private const string PinRule = "PIN must be at least 4 characters";

        /// <summary>
        /// Checked against when the user is unknown or inactive, so such a login costs as much as one for a live user and the
        /// response time does not reveal which usernames exist.
        /// </summary>
        private static readonly string DummyPinHash = PinHasher.Hash("dummy-pin-for-unknown-users");

        private readonly IUserRepository _users;
        private readonly SessionStore _sessions;
        private readonly IClock _clock;

        // Serializes the read-then-write admin changes (user and API key saves) so the last-admin and duplicate-name checks hold.
        private readonly object _gate = new object();

        public AuthService(IUserRepository users, SessionStore sessions, IClock clock)
        {
            if (users == null) throw new ArgumentNullException(nameof(users));
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            _users = users;
            _sessions = sessions;
            _clock = clock;
        }

        /// <summary>
        /// Checks the PIN and starts a session. An unknown user, an inactive user and a wrong PIN all fail the same way
        /// (<see cref="ErrorKind.Unauthenticated"/>, "Invalid username or PIN"). The username is matched ignoring case.
        /// </summary>
        public LoginResult Login(string username, string pin)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin))
                throw InvalidLogin();

            AppUser user;
            try
            {
                user = _users.FindUser(username.Trim());
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }

            var usable = user != null && user.Active;
            var pinMatches = PinHasher.Verify(pin, usable ? user.PinHash : DummyPinHash);
            if (!usable || !pinMatches)
                throw InvalidLogin();

            var token = _sessions.Create(Principal.ForUser(user.Username, user.Role));
            return new LoginResult(token, user.Role, _sessions.ExpiresAtUtc(token));
        }

        /// <summary>
        /// Resolves the caller. A bearer token, when present, must be a live session: an invalid or expired one fails
        /// without trying the API key. Otherwise the API key must belong to an active key. Blank values count as absent.
        /// </summary>
        public Principal Authenticate(string bearerToken, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var session = _sessions.Resolve(bearerToken);
                if (session == null)
                    throw new InterlockException(ErrorKind.Unauthenticated, "Invalid or expired token");
                return session;
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                ApiKeyRecord key;
                try
                {
                    key = _users.FindApiKeyByHash(ApiKeyHasher.Hash(apiKey));
                }
                catch (DbUnavailableException)
                {
                    throw Unavailable();
                }

                if (key == null || !key.Active)
                    throw new InterlockException(ErrorKind.Unauthenticated, "Invalid API key");
                return Principal.ForApiKey(key.Name, key.CanUnlock);
            }

            throw new InterlockException(ErrorKind.Unauthenticated, "Authentication required");
        }

        /// <summary>All users, active or not. The PIN hashes are left out of the result.</summary>
        public IList<AppUser> ListUsers(Principal p)
        {
            RequireAdmin(p);

            try
            {
                return _users.ListUsers().Select(u => new AppUser
                {
                    Id = u.Id,
                    Username = u.Username,
                    PinHash = null,
                    Role = u.Role,
                    Active = u.Active,
                    CreatedAtUtc = u.CreatedAtUtc
                }).ToList();
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>
        /// Adds a user, or updates the existing user with this name (ignoring case; the stored spelling stays). A new user needs
        /// a PIN; for an existing user a null or blank <paramref name="pinOrNull"/> keeps the current PIN. The last active admin
        /// cannot be deactivated or demoted.
        /// </summary>
        /// <remarks>
        /// Saving a user does not touch that user's live sessions: a deactivated or demoted user keeps the access of the role
        /// they logged in with until the session expires.
        /// </remarks>
        public void SaveUser(Principal p, string username, Role role, bool active, string pinOrNull)
        {
            RequireAdmin(p);
            var name = CleanUsername(username);
            var newPinHash = HasPin(pinOrNull) ? HashPin(pinOrNull) : null;

            try
            {
                lock (_gate)
                {
                    var existing = _users.FindUser(name);
                    if (existing == null && newPinHash == null)
                        throw new InterlockException(ErrorKind.Validation, PinRule);

                    if (existing != null && existing.Role == Role.Admin && existing.Active
                        && (role != Role.Admin || !active) && !AnotherActiveAdminExists(existing.Username))
                        throw new InterlockException(ErrorKind.InvalidState, "Cannot remove the last active admin");

                    _users.SaveUser(new AppUser
                    {
                        Id = existing == null ? 0 : existing.Id,
                        Username = existing == null ? name : existing.Username,
                        PinHash = newPinHash ?? existing.PinHash,
                        Role = role,
                        Active = active,
                        CreatedAtUtc = existing == null ? _clock.UtcNow : existing.CreatedAtUtc
                    });
                }
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>Creates an API key and returns it with its plain key, which cannot be read again. Names are unique (ignoring case).</summary>
        public ApiKeyCreated CreateApiKey(Principal p, string name, bool canUnlock)
        {
            RequireAdmin(p);
            var keyName = name == null ? string.Empty : name.Trim();
            if (keyName.Length == 0)
                throw new InterlockException(ErrorKind.Validation, "A key name is required");

            var plainKey = ApiKeyHasher.NewKey();

            try
            {
                lock (_gate)
                {
                    if (_users.ListApiKeys().Any(k => string.Equals(k.Name, keyName, StringComparison.OrdinalIgnoreCase)))
                        throw new InterlockException(ErrorKind.Validation, "An API key with this name already exists");

                    var record = _users.InsertApiKey(new ApiKeyRecord
                    {
                        Name = keyName,
                        KeyHash = ApiKeyHasher.Hash(plainKey),
                        CanUnlock = canUnlock,
                        Active = true,
                        CreatedAtUtc = _clock.UtcNow
                    });
                    return new ApiKeyCreated(record, plainKey);
                }
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>All API keys, active or not.</summary>
        public IList<ApiKeyRecord> ListApiKeys(Principal p)
        {
            RequireAdmin(p);

            try
            {
                return _users.ListApiKeys();
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <exception cref="InterlockException"><see cref="ErrorKind.NotFound"/> when no key has this id.</exception>
        public void SetApiKeyActive(Principal p, long id, bool active)
        {
            RequireAdmin(p);

            try
            {
                if (!_users.ListApiKeys().Any(k => k.Id == id))
                    throw new InterlockException(ErrorKind.NotFound, "API key not found");

                _users.SetApiKeyActive(id, active);
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        /// <summary>
        /// Creates a user without a principal. Only the service's <c>--add-user</c> console command calls this, to create the
        /// first admin. Same checks as <see cref="SaveUser"/> for a new user, and an existing user is never overwritten.
        /// </summary>
        public void CreateInitialUser(string username, Role role, string pin)
        {
            var name = CleanUsername(username);
            if (!HasPin(pin))
                throw new InterlockException(ErrorKind.Validation, PinRule);
            var pinHash = HashPin(pin);

            try
            {
                lock (_gate)
                {
                    if (_users.FindUser(name) != null)
                        throw new InterlockException(ErrorKind.Validation, "A user with this name already exists");

                    _users.SaveUser(new AppUser
                    {
                        Username = name,
                        PinHash = pinHash,
                        Role = role,
                        Active = true,
                        CreatedAtUtc = _clock.UtcNow
                    });
                }
            }
            catch (DbUnavailableException)
            {
                throw Unavailable();
            }
        }

        private bool AnotherActiveAdminExists(string username)
        {
            return _users.ListUsers().Any(u => u.Role == Role.Admin && u.Active
                && !string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        private static void RequireAdmin(Principal p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (!Permissions.CanAdmin(p))
                throw new InterlockException(ErrorKind.Forbidden, "Admin rights required");
        }

        private static string CleanUsername(string username)
        {
            var name = username == null ? string.Empty : username.Trim();
            if (name.Length == 0)
                throw new InterlockException(ErrorKind.Validation, "A username is required");
            return name;
        }

        /// <summary>A blank PIN counts as no PIN.</summary>
        private static bool HasPin(string pin)
        {
            return !string.IsNullOrWhiteSpace(pin);
        }

        private static string HashPin(string pin)
        {
            if (pin.Length < MinPinLength)
                throw new InterlockException(ErrorKind.Validation, PinRule);
            return PinHasher.Hash(pin);
        }

        private static InterlockException InvalidLogin()
        {
            return new InterlockException(ErrorKind.Unauthenticated, "Invalid username or PIN");
        }

        private static InterlockException Unavailable()
        {
            return new InterlockException(ErrorKind.Unavailable, "Database unavailable");
        }
    }
}
