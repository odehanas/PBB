using System;
using System.Security.Cryptography;

namespace GovBudget.Utils
{
    public static class ResetTokens
    {
        // Cryptographically-strong, URL-safe token (no padding, no reserved chars).
        public static string Generate()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }
    }
}
