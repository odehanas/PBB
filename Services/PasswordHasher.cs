using System;
using System.Security.Cryptography;
using System.Text;

namespace GovBudget.Services
{
    // Salted PBKDF2-SHA256 password hashing.
    //
    // Stored format (single column, self-describing so the work factor can be raised later
    // without invalidating existing hashes):
    //     PBKDF2-SHA256$<iterations>$<base64 salt>$<base64 key>
    public static class PasswordHasher
    {
        public const string Prefix = "PBKDF2-SHA256";

        private const int SaltSize = 16;   // 128-bit salt
        private const int KeySize = 32;    // 256-bit derived key
        private const int CurrentIterations = 210_000; // OWASP 2023 guidance for PBKDF2-SHA256

        public static string Hash(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                CurrentIterations,
                HashAlgorithmName.SHA256,
                KeySize);

            return string.Join('$',
                Prefix,
                CurrentIterations.ToString(),
                Convert.ToBase64String(salt),
                Convert.ToBase64String(key));
        }

        public static bool IsHashed(string? stored)
            => !string.IsNullOrWhiteSpace(stored) && stored.StartsWith(Prefix + "$", StringComparison.Ordinal);

        // Verifies a candidate password against a stored hash. needsRehash is true when the
        // hash is valid but was produced with a lower work factor than the current one.
        public static bool Verify(string? password, string? stored, out bool needsRehash)
        {
            needsRehash = false;

            if (string.IsNullOrEmpty(password) || !IsHashed(stored)) return false;

            var parts = stored!.Split('$');
            if (parts.Length != 4) return false;

            if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
            if (ok && iterations < CurrentIterations) needsRehash = true;
            return ok;
        }

        // Reset tokens are single-use, short-lived secrets. Only their SHA-256 digest is
        // stored, so a leaked database row cannot be replayed as a reset link.
        public static string HashToken(string token)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""));
            return Convert.ToBase64String(digest);
        }

        public static string NewSecurityStamp()
            => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    }
}
