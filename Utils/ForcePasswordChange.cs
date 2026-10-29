using System;
using System.Threading.Tasks;
using GovBudget.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GovBudget.Utils
{
    // When an administrator sets or resets a password, the account is flagged
    // MustChangePassword. Until the user picks their own password, every request is funnelled
    // to Account/ChangePassword so the administrator-known password cannot be used to work
    // in the system.
    public static class ForcePasswordChange
    {
        public static IApplicationBuilder UseForcePasswordChange(this IApplicationBuilder app)
        {
            return app.Use(async (context, next) =>
            {
                var user = context.User;

                if (user?.Identity?.IsAuthenticated == true
                    && string.Equals(user.FindFirst(CookieSecurityValidator.MustChangePasswordClaim)?.Value, "1", StringComparison.Ordinal)
                    && !IsAllowed(context.Request.Path))
                {
                    context.Response.Redirect("/Account/ChangePassword?forced=1");
                    return;
                }

                await next();
            });
        }

        private static bool IsAllowed(PathString path)
        {
            var value = path.Value ?? "";

            return value.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/Account/Logout", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/Account/Denied", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase);
        }
    }
}
