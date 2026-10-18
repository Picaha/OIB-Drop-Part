using System;

namespace Interlock.Core.Model
{
    public sealed class AppUser
    {
        public long Id { get; set; }
        public string Username { get; set; }
        public string PinHash { get; set; }
        public Role Role { get; set; }
        public bool Active { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public sealed class ApiKeyRecord
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string KeyHash { get; set; }
        public bool CanUnlock { get; set; }
        public bool Active { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
