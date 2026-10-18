using System;
using Interlock.Core.Model;

namespace Interlock.Core.Security
{
    /// <summary>
    /// The authenticated caller: either a user (has a <see cref="Role"/>) or an API key (no role;
    /// <see cref="CanUnlock"/> says whether it may release locks).
    /// </summary>
    public sealed class Principal
    {
        private Principal(string name, Role? role, bool isApiKey, bool canUnlock)
        {
            Name = name;
            Role = role;
            IsApiKey = isApiKey;
            CanUnlock = canUnlock;
        }

        public string Name { get; }

        /// <summary>The user's role; <c>null</c> for an API key.</summary>
        public Role? Role { get; }

        public bool IsApiKey { get; }

        /// <summary>Whether an API key may release locks. Always <c>false</c> for users (their rights come from the role).</summary>
        public bool CanUnlock { get; }

        public static Principal ForUser(string name, Role role)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            return new Principal(name, role, false, false);
        }

        public static Principal ForApiKey(string name, bool canUnlock)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            return new Principal(name, null, true, canUnlock);
        }
    }
}
