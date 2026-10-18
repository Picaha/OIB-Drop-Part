using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>Client-side checks of the admin dialog (a convenience; the service decides).</summary>
    public static class AdminRules
    {
        public const int MinPin = 4;
        public static readonly string[] Roles = { "OPERATOR", "QA", "ENGINEER", "ADMIN" };

        public static bool CanOpen(Session session)
        {
            return session != null && session.IsAdmin;
        }

        /// <summary>Null when fine. A new user needs a PIN; when editing, an empty PIN keeps the current one.</summary>
        public static string ValidateUser(bool isNew, string username, string role, string pin)
        {
            if (string.IsNullOrWhiteSpace(username)) return "Enter the username";
            if (string.IsNullOrWhiteSpace(role)) return "Choose a role";
            var length = pin == null ? 0 : pin.Length;
            if (isNew && length < MinPin) return "A new user needs a PIN of at least " + MinPin + " characters";
            if (!isNew && length > 0 && length < MinPin) return "The PIN must have at least " + MinPin + " characters";
            return null;
        }

        /// <summary>The PIN is only sent when one was typed.</summary>
        public static UserDto ToUserDto(string username, string role, bool active, string pin)
        {
            return new UserDto
            {
                username = username == null ? null : username.Trim(),
                role = role,
                active = active,
                pin = string.IsNullOrEmpty(pin) ? null : pin
            };
        }

        public static string ValidateKeyName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? "Enter a name for the key" : null;
        }
    }

    /// <summary>Holds a freshly created API key until the dialog drops it; it is never stored anywhere else.</summary>
    public sealed class KeyOnceDisplay
    {
        public const string Warning = "Copy this key now. It cannot be shown again.";

        public string Key { get; private set; }
        public bool HasKey { get { return !string.IsNullOrEmpty(Key); } }

        public void Show(string key)
        {
            Key = string.IsNullOrEmpty(key) ? null : key;
        }

        public void Clear()
        {
            Key = null;
        }
    }
}
