using System;
using System.Globalization;
using System.Security.Cryptography;

namespace Interlock.Core.Security
{
    /// <summary>PBKDF2-SHA256 PIN hashing. Stored form: <c>pbkdf2-sha256$100000$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>.</summary>
    public static class PinHasher
    {
        private const string Scheme = "pbkdf2-sha256";
        private const int Iterations = 100000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const int MinSaltBytes = 8; // Rfc2898DeriveBytes rejects shorter salts

        public static string Hash(string pin)
        {
            if (pin == null)
                throw new ArgumentNullException(nameof(pin));

            var salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            var hash = Derive(pin, salt, Iterations, HashBytes);
            return Scheme + "$" + Iterations.ToString(CultureInfo.InvariantCulture)
                + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        /// <summary>Checks a PIN against a stored hash in constant time. A malformed <paramref name="stored"/> never verifies.</summary>
        public static bool Verify(string pin, string stored)
        {
            if (pin == null || stored == null)
                return false;

            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Scheme)
                return false;

            int iterations;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations <= 0)
                return false;

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            if (salt.Length < MinSaltBytes || expected.Length == 0)
                return false;

            var actual = Derive(pin, salt, iterations, expected.Length);
            return ConstantTimeEquals(actual, expected);
        }

        private static byte[] Derive(string pin, byte[] salt, int iterations, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < a.Length && i < b.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
