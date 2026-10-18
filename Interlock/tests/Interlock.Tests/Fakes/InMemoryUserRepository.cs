using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Tests.Fakes
{
    /// <summary>A user and API key repository that lives in memory. Stored rows are copies, so callers cannot change them by accident.</summary>
    public sealed class InMemoryUserRepository : IUserRepository
    {
        private readonly object _gate = new object();
        private readonly List<AppUser> _users = new List<AppUser>();
        private readonly List<ApiKeyRecord> _keys = new List<ApiKeyRecord>();
        private long _lastUserId;
        private long _lastKeyId;

        /// <summary>While true, every <see cref="IUserRepository"/> member throws <see cref="DbUnavailableException"/>.</summary>
        public bool Down { get; set; }

        public AppUser FindUser(string username)
        {
            CheckUp();
            lock (_gate)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                return user == null ? null : Copy(user);
            }
        }

        public IList<AppUser> ListUsers()
        {
            CheckUp();
            lock (_gate)
                return _users.OrderBy(u => u.Id).Select(Copy).ToList();
        }

        /// <summary>Adds the user with the next id, or replaces the user with the same name (ignoring case), keeping its id.</summary>
        public void SaveUser(AppUser u)
        {
            if (u == null)
                throw new ArgumentNullException(nameof(u));
            CheckUp();
            lock (_gate)
            {
                var stored = Copy(u);
                var index = _users.FindIndex(e => string.Equals(e.Username, u.Username, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    stored.Id = _users[index].Id;
                    _users[index] = stored;
                }
                else
                {
                    stored.Id = ++_lastUserId;
                    _users.Add(stored);
                }
            }
        }

        /// <summary>Finds the key whether or not it is active; callers check <c>Active</c>.</summary>
        public ApiKeyRecord FindApiKeyByHash(string keyHash)
        {
            CheckUp();
            lock (_gate)
            {
                var key = _keys.FirstOrDefault(k => k.KeyHash == keyHash);
                return key == null ? null : Copy(key);
            }
        }

        public IList<ApiKeyRecord> ListApiKeys()
        {
            CheckUp();
            lock (_gate)
                return _keys.OrderBy(k => k.Id).Select(Copy).ToList();
        }

        public ApiKeyRecord InsertApiKey(ApiKeyRecord k)
        {
            if (k == null)
                throw new ArgumentNullException(nameof(k));
            CheckUp();
            lock (_gate)
            {
                var stored = Copy(k);
                stored.Id = ++_lastKeyId;
                _keys.Add(stored);
                return Copy(stored);
            }
        }

        public void SetApiKeyActive(long id, bool active)
        {
            CheckUp();
            lock (_gate)
            {
                var key = _keys.FirstOrDefault(k => k.Id == id);
                if (key != null)
                    key.Active = active;
            }
        }

        private void CheckUp()
        {
            if (Down)
                throw new DbUnavailableException("The in-memory user repository is down.");
        }

        private static AppUser Copy(AppUser u)
        {
            return new AppUser
            {
                Id = u.Id,
                Username = u.Username,
                PinHash = u.PinHash,
                Role = u.Role,
                Active = u.Active,
                CreatedAtUtc = u.CreatedAtUtc
            };
        }

        private static ApiKeyRecord Copy(ApiKeyRecord k)
        {
            return new ApiKeyRecord
            {
                Id = k.Id,
                Name = k.Name,
                KeyHash = k.KeyHash,
                CanUnlock = k.CanUnlock,
                Active = k.Active,
                CreatedAtUtc = k.CreatedAtUtc
            };
        }
    }
}
