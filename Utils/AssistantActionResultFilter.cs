using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace GovBudget.Utils
{
    // When the assistant widget submits a confirmed change it posts to the screen's own action,
    // so every validation rule, lock, permission and audit entry still applies. Screens answer
    // with a redirect plus a TempData message, or re-render the form with model errors; this
    // filter turns either into { ok, message } JSON for requests flagged with the header below.
    public sealed class AssistantActionResultFilter : IAsyncAlwaysRunResultFilter
    {
        public const string HeaderName = "X-Assistant-Action";

        private static readonly string[] ErrorKeys = { "Error", "ActualError", "KpiImportError", "ImportErrors" };
        private static readonly string[] SuccessKeys = { "Success", "ActualResult", "AllocMsg" };

        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            var request = context.HttpContext.Request;
            if (!HttpMethods.IsPost(request.Method) || request.Headers[HeaderName] != "1")
            {
                await next();
                return;
            }

            var tempData = context.HttpContext.RequestServices
                .GetRequiredService<ITempDataDictionaryFactory>()
                .GetTempData(context.HttpContext);

            string? Message(string[] keys) => keys
                .Select(k => tempData[k] as string)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

            var error = Message(ErrorKeys);
            var success = Message(SuccessKeys);

            context.Result = context.Result switch
            {
                RedirectResult or RedirectToActionResult or RedirectToRouteResult or LocalRedirectResult
                    => Outcome(error is null, error ?? success ?? "Done."),

                ViewResult or PartialViewResult when !context.ModelState.IsValid
                    => Outcome(false, string.Join(" ", context.ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "A value is not valid." : e.ErrorMessage)
                        .Distinct())),

                ViewResult or PartialViewResult => Outcome(error is null, error ?? success ?? "Done."),

                ForbidResult or ChallengeResult => Outcome(false, "Your role does not allow this action.", StatusCodes.Status403Forbidden),

                IStatusCodeActionResult { StatusCode: >= 400 } status and not ObjectResult and not JsonResult
                    => Outcome(false, $"The screen rejected the request (HTTP {status.StatusCode}).", status.StatusCode),

                _ => context.Result
            };

            await next();
        }

        private static JsonResult Outcome(bool ok, string message, int? statusCode = null) =>
            new(new { ok, message }) { StatusCode = statusCode };
    }
}
