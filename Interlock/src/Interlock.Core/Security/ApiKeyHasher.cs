using System;
using System.Security.Cryptography;
using System.Text;

namespace Interlock.Core.Security
{
    /// <summary>API keys are random 256-bit values shown once; only their lower-case hex SHA-256 is stored.</summary>
    public static class ApiKeyHasher
    {
        /// <summary>A new key: 64 lower-case hex characters from a cryptographic random source.</summary>
        public static string NewKey()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return ToHex(bytes);
        }

        public static string Hash(string key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(key)));
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
