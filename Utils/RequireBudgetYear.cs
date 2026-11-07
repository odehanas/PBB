using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GovBudget.Utils
{
    // Screens open on the year chosen on Context/Year. Without one they would fall back to the
    // calendar year and could look empty, so a signed-in user with no chosen year is sent to the
    // picker first. That covers a bookmark as well as a session lost to an app-pool recycle.
    //
    // Only full-page navigations are redirected. Form posts, AJAX calls and downloads carry on
    // as before, so losing the session mid-task cannot discard what the user was submitting.
    public static class RequireBudgetYear
    {
        public static IApplicationBuilder UseRequireBudgetYear(this IApplicationBuilder app)
        {
            return app.Use(async (context, next) =>
            {
                if (context.User?.Identity?.IsAuthenticated == true
                    && HttpMethods.IsGet(context.Request.Method)
                    && !IsExempt(context.Request.Path)
                    && IsPageNavigation(context.Request)
                    && context.Session.GetInt("ctxYear") == null)
                {
                    var target = context.Request.Path + context.Request.QueryString;
                    context.Response.Redirect(target == "/"
                        ? "/Context/Year"
                        : "/Context/Year?returnUrl=" + Uri.EscapeDataString(target));
                    return;
                }

                await next();
            });
        }

        private static bool IsExempt(PathString path)
        {
            var value = path.Value ?? "";

            return value.StartsWith("/Context/Year", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/Home/Error", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPageNavigation(HttpRequest request)
        {
            var mode = request.Headers["Sec-Fetch-Mode"].ToString();
            if (!string.IsNullOrEmpty(mode))
            {
                return string.Equals(mode, "navigate", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(request.Headers["Sec-Fetch-Dest"].ToString(), "document", StringComparison.OrdinalIgnoreCase);
            }

            // Browsers or proxies that do not send the Sec-Fetch headers.
            return request.Headers["Accept"].ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        }
    }
}
