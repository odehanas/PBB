using System;
using System.Linq;
using System.Text;

namespace GovBudget.Utils
{
    // Single place that defines what an acceptable password is, so the login, the admin
    // user screens and the self-service reset page cannot drift apart.
    public static class PasswordPolicy
    {
        public const int MinLength = 8;
        public const int MaxLength = 128;

        public const string Summary =
            "At least 8 characters and include three of: uppercase letter, lowercase letter, digit, symbol. "
            + "It must not be the username or a common password such as Password1 or Admin@123.";

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

            var core = Core(password);

            // A long username anywhere in the password gives it away; a short one (e.g. "GRA")
            // only counts when it is the whole word, so "Program#26" is not refused for "gra".
            var name = (userName ?? "").Trim();
            if (name.Length > 0
                && ((name.Length >= 4 && password.Contains(name, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(core, Core(name), StringComparison.Ordinal)))
            {
                error = "Password must not be based on the username.";
                return false;
            }

            if (IsCommon(password, core))
            {
                error = "That password is too common. Choose something harder to guess.";
                return false;
            }

            return true;
        }

        // The words people actually pick. A password is refused when it is one of these with
        // only digits and symbols added around it (Password1, Admin@123, P@ssw0rd!), not when
        // the word merely appears inside something longer.
        private static readonly string[] Common =
        {
            "password", "passwd", "welcome", "qwerty", "qwertyuiop", "asdfgh", "letmein", "admin",
            "administrator", "govbudget", "changeme", "iloveyou", "rakdof", "user", "test", "login",
            "abc", "abcd", "abcdef"
        };

        private static bool IsCommon(string password, string core)
        {
            if (core.Length == 0)
            {
                // Digits and symbols only: refuse runs and repeats such as 12345678 or 11111111.
                var digits = new string(password.Where(char.IsDigit).ToArray());
                return digits.Length >= 6 && (IsRun(digits) || digits.Distinct().Count() <= 2);
            }

            return Common.Contains(core, StringComparer.Ordinal);
        }

        /// <summary>
        /// The word part of a password: digits and symbols trimmed from both ends, common
        /// letter substitutions undone (@ to a, 0 to o, ...), anything else non-letter dropped.
        /// </summary>
        private static string Core(string value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && !char.IsLetter(value[start])) start++;
            while (end > start && !char.IsLetter(value[end - 1])) end--;
            var trimmed = value[start..end];

            var sb = new StringBuilder(trimmed.Length);
            foreach (var c in trimmed.ToLowerInvariant())
            {
                var mapped = c switch
                {
                    '@' or '4' => 'a',
                    '0' => 'o',
                    '1' or '!' => 'i',
                    '3' => 'e',
                    '$' or '5' => 's',
                    '7' => 't',
                    _ => c
                };
                if (char.IsLetter(mapped)) sb.Append(mapped);
            }
            return sb.ToString();
        }

        private static bool IsRun(string digits)
        {
            bool Step(int d) => Enumerable.Range(1, digits.Length - 1).All(i => digits[i] - digits[i - 1] == d);
            return Step(1) || Step(-1);
        }
    }
}
