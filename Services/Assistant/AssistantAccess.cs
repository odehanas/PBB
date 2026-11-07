using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Answers "may this user reach that screen action?" the way the app itself would: the
    /// Roles &amp; Rights entry for the form, plus the [Authorize] roles or policy on the action.
    /// The assistant uses it so it never offers something that would answer "Access denied".
    /// The screens still enforce everything themselves.
    /// </summary>
    public sealed class AssistantAccess
    {
        public enum Right { View, Add }

        private readonly IPermissionService _permissions;
        private readonly IHttpContextAccessor _http;
        private readonly IActionDescriptorCollectionProvider _actions;
        private readonly IAuthorizationPolicyProvider _policies;
        private readonly IAuthorizationService _authorization;
        private IReadOnlyDictionary<string, FormRights>? _rights;

        public AssistantAccess(
            IPermissionService permissions,
            IHttpContextAccessor http,
            IActionDescriptorCollectionProvider actions,
            IAuthorizationPolicyProvider policies,
            IAuthorizationService authorization)
        {
            _permissions = permissions;
            _http = http;
            _actions = actions;
            _policies = policies;
            _authorization = authorization;
        }

        public ClaimsPrincipal? User =>
            _http.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

        /// <summary>True when any of the form keys grants the right (as the permission filter merges them).</summary>
        public async Task<bool> HasRightAsync(IEnumerable<string> formKeys, Right right)
        {
            var user = User;
            if (user is null) return false;
            _rights ??= await _permissions.GetAllRightsAsync(user);
            return formKeys.Any(k => _rights.TryGetValue(k, out var r) && (right == Right.Add ? r.CanAdd : r.CanView));
        }

        /// <summary>Whether the [Authorize] roles or policy on Controller/Action (for the HTTP method) let the user in.</summary>
        public async Task<bool> PassesAuthorizeAsync(string controller, string action, string httpMethod = "GET")
        {
            var user = User;
            if (user is null) return false;

            var descriptor = _actions.ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .Where(d => d.ControllerName.Equals(controller, StringComparison.OrdinalIgnoreCase)
                    && d.ActionName.Equals(action, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(d => d.EndpointMetadata.OfType<IHttpMethodMetadata>()
                    .All(m => m.HttpMethods.Count == 0 || m.HttpMethods.Contains(httpMethod, StringComparer.OrdinalIgnoreCase)));
            if (descriptor is null) return false; // the action no longer exists

            if (descriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return true;
            var policy = await AuthorizationPolicy.CombineAsync(_policies, descriptor.EndpointMetadata.OfType<IAuthorizeData>());
            return policy is null || (await _authorization.AuthorizeAsync(user, policy)).Succeeded;
        }

        public string PathBase => _http.HttpContext?.Request.PathBase.Value ?? "";

        public ISession? Session => _http.HttpContext?.Session;
    }
}
