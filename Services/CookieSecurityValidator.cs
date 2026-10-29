using System;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GovBudget.Services
{
    // Re-checks the signed-in user against the database while the session is alive, so that
    // deactivating an account, changing its role/scope or resetting its password takes
    // effect on the next request instead of when the cookie happens to expire.
    public static class CookieSecurityValidator
    {
        public const string SecurityStampClaim = "SecurityStamp";
        public const string MustChangePasswordClaim = "MustChangePassword";

        private const string LastCheckKey = "gb:stampChecked";
        private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(2);

        public static async Task ValidateAsync(CookieValidatePrincipalContext context)
        {
            var principal = context.Principal;
            var userName = principal?.Identity?.Name;

            if (string.IsNullOrWhiteSpace(userName))
            {
                await RejectAsync(context);
                return;
            }

            // Throttle the database round-trip; every couple of minutes is enough to make
            // revocation effective without adding a query to every request.
            var last = context.Properties.GetString(LastCheckKey);
            if (DateTimeOffset.TryParse(last, out var checkedAt)
                && DateTimeOffset.UtcNow - checkedAt < RecheckInterval)
            {
                return;
            }

            var services = context.HttpContext.RequestServices;
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("CookieSecurityValidator");

            try
            {
                var db = services.GetRequiredService<GovBudgetContext>();

                var user = await db.AppUsers
                    .AsNoTracking()
                    .Where(u => u.UserName == userName)
                    .Select(u => new { u.IsActive, u.Role, u.SecurityStamp, u.MustChangePassword })
                    .FirstOrDefaultAsync();

                if (user == null || !user.IsActive)
                {
                    await RejectAsync(context);
                    return;
                }

                var cookieRole = (principal!.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "").Trim();
                var dbRole = (user.Role ?? "").Trim();
                if (!string.Equals(cookieRole, dbRole, StringComparison.OrdinalIgnoreCase))
                {
                    // Role changed: force a fresh sign-in so the new rights are picked up.
                    await RejectAsync(context);
                    return;
                }

                var cookieStamp = principal.FindFirst(SecurityStampClaim)?.Value;
                if (!string.IsNullOrEmpty(user.SecurityStamp)
                    && !string.Equals(cookieStamp, user.SecurityStamp, StringComparison.Ordinal))
                {
                    // Password reset or forced sign-out elsewhere.
                    await RejectAsync(context);
                    return;
                }

                context.Properties.SetString(LastCheckKey, DateTimeOffset.UtcNow.ToString("o"));
                context.ShouldRenew = true;
            }
            catch (Exception ex)
            {
                // A transient database fault must not sign everybody out.
                logger?.LogWarning(ex, "Session revalidation skipped for {User}.", userName);
            }
        }

        private static async Task RejectAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
