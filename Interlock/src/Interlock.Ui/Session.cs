using System;
using Interlock.Core.Api;
using Interlock.Core.Model;
using Interlock.Core.Security;

namespace Interlock.Ui
{
    /// <summary>Who is logged in and what the screen may offer. The service still checks every call; this only hides buttons.</summary>
    public sealed class Session
    {
        private Session(string username, Role role)
        {
            Username = username;
            Role = role;
            var p = Principal.ForUser(username, role);
            CanRelease = Permissions.CanRelease(p);
            IsAdmin = Permissions.CanAdmin(p);
        }

        public string Username { get; }
        public Role Role { get; }
        public bool CanRelease { get; }
        public bool IsAdmin { get; }

        /// <exception cref="InvalidOperationException">The login answer carries an unknown role.</exception>
        public static Session FromLogin(string username, SessionDto dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            Role role;
            try { role = DbEnum.ParseRole(dto.role); }
            catch (Exception ex) { throw new InvalidOperationException("The service answered with an unknown role", ex); }
            return new Session(username ?? "", role);
        }
    }
}
