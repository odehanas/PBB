using Microsoft.AspNetCore.Http;

namespace GovBudget.Utils
{
    /// <summary>
    /// Tiny helpers to store/retrieve integers in ASP.NET Core Session
    /// without repeating parsing logic everywhere.
    /// </summary>
    public static class SessionExtensions
    {
        /// <summary>
        /// Save an integer to Session as a string.
        /// </summary>
        public static void SetInt(this ISession session, string key, int value)
            => session.SetString(key, value.ToString());

        /// <summary>
        /// Read an integer from Session (returns null if missing or not an int).
        /// </summary>
        public static int? GetInt(this ISession session, string key)
            => int.TryParse(session.GetString(key), out var v) ? v : (int?)null;
    }
}