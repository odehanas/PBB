using System;
using System.Linq;

namespace GovBudget.Utils
{
    // Single place that defines what an acceptable password is, so the login, the admin
    // user screens and the self-service reset page cannot drift apart.
    public static class PasswordPolicy
    {
        public const int MinLength = 12;
        public const int MaxLength = 128;

        public const string Summary =
            "At least 12 characters and include three of: uppercase letter, lowercase letter, digit, symbol. "
            + "It must not contain the username.";

        public static bool Validate(string? password, string? userName, out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(password))
            {
                error = "Password is required.";
                return false;
            }

            if (password.Length < MinLength)
            {
                error = $"Password must be at least {MinLength} characters.";
                return false;
            }

            if (password.Length > MaxLength)
            {
                error = $"Password must be {MaxLength} characters or fewer.";
                return false;
            }

            var classes = 0;
            if (password.Any(char.IsUpper)) classes++;
            if (password.Any(char.IsLower)) classes++;
            if (password.Any(char.IsDigit)) classes++;
            if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;

            if (classes < 3)
            {
                error = "Password must include at least three of: uppercase letter, lowercase letter, digit, symbol.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(userName)
                && userName.Trim().Length >= 3
                && password.Contains(userName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error = "Password must not contain the username.";
                return false;
            }

            if (IsCommon(password))
            {
                error = "That password is too common. Choose something harder to guess.";
                return false;
            }

            return true;
        }

        // Short deny list of the patterns people actually pick. Not a substitute for a
        // breached-password service, but it stops the obvious cases.
        private static readonly string[] Common =
        {
            "password", "passw0rd", "welcome", "qwerty", "123456", "abc123",
            "letmein", "admin", "govbudget", "changeme", "iloveyou", "rakdof"
        };

        private static bool IsCommon(string password)
        {
            var lower = password.ToLowerInvariant();
            return Common.Any(c => lower.Contains(c, StringComparison.Ordinal));
        }
    }
}
