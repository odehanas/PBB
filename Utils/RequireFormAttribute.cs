using System;
using System.Threading.Tasks;
using GovBudget.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace GovBudget.Utils
{
    public enum FormAction
    {
        View,
        Add,
        Edit,
        Delete
    }

    // Gates a controller or action behind a form permission.
    //
    //   [RequireForm(AppForms.HrCosts)]                          // whole controller
    //   [RequireForm(AppForms.HrCosts, FormAction.Delete)]       // one action
    //
    // When no FormAction is given it is inferred from the HTTP method: a GET needs
    // CanView, and any write needs at least one of Add/Edit/Delete. That makes a
    // view-only role safe to grant without annotating every single action, while
    // specific actions can still be pinned to an exact right.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RequireFormAttribute : Attribute, IAsyncAuthorizationFilter
    {
        public string FormKey { get; }
        public FormAction? Action { get; }

        public RequireFormAttribute(string formKey)
        {
            FormKey = formKey;
            Action = null;
        }

        public RequireFormAttribute(string formKey, FormAction action)
        {
            FormKey = formKey;
            Action = action;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                context.Result = new ChallengeResult();
                return;
            }

            var permissions = context.HttpContext.RequestServices.GetRequiredService<IPermissionService>();
            var rights = await permissions.GetRightsAsync(user, FormKey);

            // Cache on the request so views and later filters do not query again.
            context.HttpContext.Items[FormRightsExtensions.ItemsKey] = rights;
            context.HttpContext.Items[FormRightsExtensions.ItemsFormKey] = FormKey;

            if (!rights.CanView)
            {
                context.Result = new ForbidResult();
                return;
            }

            var required = Action ?? InferFromMethod(context.HttpContext.Request.Method);

            var allowed = required switch
            {
                FormAction.View => rights.CanView,
                FormAction.Add => rights.CanAdd,
                FormAction.Edit => rights.CanEdit,
                FormAction.Delete => rights.CanDelete,
                _ => false
            };

            // Inferred writes accept any write right; explicit ones must match exactly.
            if (!Action.HasValue && required != FormAction.View)
            {
                allowed = rights.CanAdd || rights.CanEdit || rights.CanDelete;
            }

            if (!allowed)
            {
                context.Result = new ForbidResult();
            }
        }

        private static FormAction InferFromMethod(string method) =>
            HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
                ? FormAction.View
                : FormAction.Edit;
    }

    public static class FormRightsExtensions
    {
        internal const string ItemsKey = "__FormRights";
        internal const string ItemsFormKey = "__FormRightsKey";

        // Rights resolved by [RequireForm] for the current request. Views use this to hide
        // Create / Edit / Delete controls so a view-only user is not shown dead buttons.
        public static FormRights FormRights(this HttpContext ctx)
            => ctx.Items.TryGetValue(ItemsKey, out var v) && v is FormRights r
                ? r
                : Services.FormRights.None;

        public static bool CanAdd(this HttpContext ctx) => ctx.FormRights().CanAdd;
        public static bool CanEdit(this HttpContext ctx) => ctx.FormRights().CanEdit;
        public static bool CanDelete(this HttpContext ctx) => ctx.FormRights().CanDelete;
        public static bool IsViewOnly(this HttpContext ctx) => ctx.FormRights().IsViewOnly;
    }
}
