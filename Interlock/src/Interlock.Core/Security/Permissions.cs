using System;
using Interlock.Core.Model;

namespace Interlock.Core.Security
{
    public static class Permissions
    {
        /// <summary>Any authenticated principal may create a lock.</summary>
        public static bool CanLock(Principal p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            return true;
        }

        /// <summary>Qa, Engineer and Admin users, and API keys flagged <see cref="Principal.CanUnlock"/>.</summary>
        public static bool CanRelease(Principal p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (p.IsApiKey)
                return p.CanUnlock;

            return p.Role == Role.Qa || p.Role == Role.Engineer || p.Role == Role.Admin;
        }

        /// <summary>Admin users only; API keys never.</summary>
        public static bool CanAdmin(Principal p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            return !p.IsApiKey && p.Role == Role.Admin;
        }
    }
}
