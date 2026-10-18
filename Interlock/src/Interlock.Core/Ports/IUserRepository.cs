using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>Stores users and API keys. Every member throws <c>DbUnavailableException</c> when the database cannot be reached.</summary>
    public interface IUserRepository
    {
        /// <summary>Looks the user up ignoring case; null when unknown.</summary>
        AppUser FindUser(string username);

        IList<AppUser> ListUsers();

        /// <summary>Adds the user, or updates the user with the same name (ignoring case).</summary>
        void SaveUser(AppUser u);

        /// <summary>The key with this hash, active or not; null when unknown.</summary>
        ApiKeyRecord FindApiKeyByHash(string keyHash);

        IList<ApiKeyRecord> ListApiKeys();
        ApiKeyRecord InsertApiKey(ApiKeyRecord k);
        void SetApiKeyActive(long id, bool active);
    }
}
