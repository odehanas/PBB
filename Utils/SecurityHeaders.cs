using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GovBudget.Utils
{
    // Baseline browser-side protections: clickjacking, MIME sniffing, referrer leakage and
    // a content policy that pins script/style/frame sources.
    //
    // The policy still allows 'unsafe-inline' for scripts because several views declare
    // inline handlers; removing that is tracked as a follow-up (nonce per request).
    public static class SecurityHeaders
    {
        // Every front-end library is now served from wwwroot/lib, so no external origin is
        // allowed at all. The browser therefore cannot be made to fetch a script, stylesheet
        // or font from the internet - which is both a hardening win and what lets the app run
        // unchanged inside an air-gapped environment.
        // 'unsafe-inline' stays because several views declare inline <script>/<style> blocks.
        public const string DefaultContentSecurityPolicy =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "font-src 'self' data:; " +
            "img-src 'self' data:; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "object-src 'none'";

        public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, string? contentSecurityPolicy = null)
        {
            var csp = string.IsNullOrWhiteSpace(contentSecurityPolicy)
                ? DefaultContentSecurityPolicy
                : contentSecurityPolicy!;

            return app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;

                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["X-Permitted-Cross-Domain-Policies"] = "none";

                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers["Content-Security-Policy"] = csp;
                }

                // Budget figures and salary data must never be cached by a shared proxy.
                // Static assets keep their normal caching.
                if (context.User?.Identity?.IsAuthenticated == true && !IsStaticAsset(context.Request.Path))
                {
                    headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                    headers["Pragma"] = "no-cache";
                }

                await next();
            });
        }

        private static bool IsStaticAsset(PathString path)
        {
            var value = path.Value;
            if (string.IsNullOrEmpty(value)) return false;

            var dot = value.LastIndexOf('.');
            if (dot < 0) return false;

            var ext = value[dot..].ToLowerInvariant();
            return ext is ".css" or ".js" or ".map" or ".png" or ".jpg" or ".jpeg" or ".gif"
                or ".svg" or ".ico" or ".woff" or ".woff2" or ".ttf" or ".eot";
        }
    }
}
