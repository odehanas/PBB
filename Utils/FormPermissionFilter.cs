using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GovBudget.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GovBudget.Utils
{
    // Global guard that makes the Roles & Rights matrix binding without having to annotate
    // every action. It maps each controller to the form(s) it serves and then:
    //   * blocks every write request (POST/PUT/PATCH/DELETE) unless the role holds the
    //     matching Add / Edit / Delete right,
    //   * blocks the GET of a data-entry screen (Create / Edit / Delete / Import / Upload)
    //     for the same reason, so a view-only user never reaches a form they cannot save.
    // Plain read screens are left to the sidebar filtering, which keeps navigation working
    // for roles that only hold CanView.
    public sealed class FormPermissionFilter : IAsyncAuthorizationFilter
    {
        private readonly IPermissionService _permissions;

        public FormPermissionFilter(IPermissionService permissions)
        {
            _permissions = permissions;
        }

        // A controller can serve more than one form (HR costs admin + the user-facing HR
        // allocation screen, for example). The request is allowed when any mapped form
        // grants the required right.
        private static readonly Dictionary<string, string[]> FormsByController =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Activities"] = new[] { AppForms.Activities },
                ["Actuals"] = new[] { AppForms.Actuals },
                ["Admin"] = new[] { AppForms.AdminRoom },
                ["Allocation"] = new[] { AppForms.Allocation },
                ["AppUsers"] = new[] { AppForms.Users },
                ["AuditLogs"] = new[] { AppForms.AuditLog },
                ["BudgetLines"] = new[] { AppForms.BudgetEntry },
                ["Departments"] = new[] { AppForms.Departments },
                ["Entities"] = new[] { AppForms.Entities },
                ["GLAccounts"] = new[] { AppForms.GlAccounts },
                ["Guides"] = new[] { AppForms.Guides },
                ["HrCosts"] = new[] { AppForms.HrCosts, AppForms.HrAllocation },
                ["InternalMessages"] = new[] { AppForms.Requests },
                ["Items"] = new[] { AppForms.Items },
                ["ManagementReview"] = new[] { AppForms.ManagementReview },
                ["MidYear"] = new[] { AppForms.MidYear },
                ["Performance"] = new[] { AppForms.Performance },
                ["Programs"] = new[] { AppForms.Programs },
                ["Projects"] = new[] { AppForms.Projects },
                ["Reports"] = new[] { AppForms.Reports, AppForms.BudgetVsActual },
                ["RolePermissions"] = new[] { AppForms.Roles },
                ["WhatIf"] = new[] { AppForms.WhatIf }
            };

        // Sign-in, password reset and the year/entity/cost-center picker only touch the
        // session or the caller's own account, so they are never gated by form rights.
        private static readonly HashSet<string> ExemptControllers =
            new(StringComparer.OrdinalIgnoreCase) { "Account", "Context", "Home" };

        // GET screens that exist purely to write data.
        private static readonly HashSet<string> AddScreens =
            new(StringComparer.OrdinalIgnoreCase) { "Create", "Add", "New", "Import", "Upload" };

        private static readonly HashSet<string> EditScreens =
            new(StringComparer.OrdinalIgnoreCase) { "Edit", "Update" };

        private static readonly HashSet<string> DeleteScreens =
            new(StringComparer.OrdinalIgnoreCase) { "Delete", "Remove", "Deactivate" };

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            if (context.ActionDescriptor is not ControllerActionDescriptor descriptor) return;

            // Anything already carrying [RequireForm] enforces itself.
            if (descriptor.MethodInfo.IsDefined(typeof(RequireFormAttribute), inherit: true)
                || descriptor.ControllerTypeInfo.IsDefined(typeof(RequireFormAttribute), inherit: true))
            {
                return;
            }

            if (context.ActionDescriptor.EndpointMetadata.Any(m => m is IAllowAnonymous)) return;

            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return;

            if (ExemptControllers.Contains(descriptor.ControllerName)) return;
            if (!FormsByController.TryGetValue(descriptor.ControllerName, out var formKeys)) return;

            var rights = new List<FormRights>(formKeys.Length);
            foreach (var key in formKeys)
            {
                rights.Add(await _permissions.GetRightsAsync(user, key));
            }

            // Expose the strongest matching rights to the views so they can hide controls.
            var merged = new FormRights(
                rights.Any(r => r.CanView),
                rights.Any(r => r.CanAdd),
                rights.Any(r => r.CanEdit),
                rights.Any(r => r.CanDelete));

            context.HttpContext.Items[FormRightsExtensions.ItemsKey] = merged;
            context.HttpContext.Items[FormRightsExtensions.ItemsFormKey] = formKeys[0];

            // Reports / dashboards / audit log are read-only screens; their POSTs are filters
            // and exports, not data changes, so they are not gated by write rights.
            if (formKeys.All(k => AppForms.Find(k)?.ViewOnlyByNature == true)) return;

            var required = RequiredAction(context.HttpContext.Request.Method, descriptor.ActionName);
            if (required == null) return;

            var allowed = required switch
            {
                FormAction.Add => merged.CanAdd,
                FormAction.Edit => merged.CanEdit,
                FormAction.Delete => merged.CanDelete,
                FormAction.View => merged.CanView,
                _ => false
            };

            // A generic write (Save, Submit, Approve, ...) is accepted with any write right,
            // because the matrix cannot know which verb a custom action represents.
            if (required == FormAction.Edit && !allowed)
            {
                allowed = merged.CanAdd || merged.CanDelete;
            }

            if (!allowed)
            {
                context.Result = Deny(context.HttpContext);
            }
        }

        // Null means "no right needed" (a plain read screen).
        private static FormAction? RequiredAction(string method, string actionName)
        {
            var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

            if (isRead)
            {
                if (StartsWithAny(actionName, DeleteScreens)) return FormAction.Delete;
                if (StartsWithAny(actionName, AddScreens)) return FormAction.Add;
                if (StartsWithAny(actionName, EditScreens)) return FormAction.Edit;
                return null;
            }

            if (StartsWithAny(actionName, DeleteScreens)) return FormAction.Delete;
            if (StartsWithAny(actionName, AddScreens)) return FormAction.Add;
            return FormAction.Edit;
        }

        private static bool StartsWithAny(string actionName, HashSet<string> names)
        {
            foreach (var n in names)
            {
                if (actionName.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static IActionResult Deny(HttpContext ctx)
        {
            var requestedWith = ctx.Request.Headers["X-Requested-With"].ToString();
            var accept = ctx.Request.Headers["Accept"].ToString();
            var isAjax = string.Equals(requestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
                         || accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);

            if (isAjax)
            {
                return new ObjectResult(new { error = "Your role does not allow this action." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
            }

            return new ForbidResult();
        }
    }
}
