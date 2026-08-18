using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GovBudget.Services
{
    // What a role may do on one form.
    public sealed record FormRights(bool CanView, bool CanAdd, bool CanEdit, bool CanDelete)
    {
        public static readonly FormRights None = new(false, false, false, false);
        public static readonly FormRights Full = new(true, true, true, true);

        // A user who can open the form but change nothing — the "view rights" case.
        public bool IsViewOnly => CanView && !CanAdd && !CanEdit && !CanDelete;
    }

    public interface IPermissionService
    {
        Task<FormRights> GetRightsAsync(ClaimsPrincipal user, string formKey);
        Task<bool> CanViewAsync(ClaimsPrincipal user, string formKey);
        Task<IReadOnlyDictionary<string, FormRights>> GetAllRightsAsync(ClaimsPrincipal user);
        void InvalidateCache();
    }

    public class PermissionService : IPermissionService
    {
        // SYSADMIN is deliberately hard-wired to full access. Permissions are editable from
        // inside the application, so without this a bad save could lock everyone out with no
        // way back in.
        public const string SuperRole = "SYSADMIN";

        private const string CacheKey = "role-permissions-map";
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

        private readonly GovBudgetContext _db;
        private readonly IMemoryCache _cache;

        public PermissionService(GovBudgetContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public void InvalidateCache() => _cache.Remove(CacheKey);

        private static string RoleOf(ClaimsPrincipal user) =>
            (user.FindFirst(ClaimTypes.Role)?.Value ?? "").Trim().ToUpperInvariant();

        // roleCode -> formKey -> rights
        private async Task<Dictionary<string, Dictionary<string, FormRights>>> GetMapAsync()
        {
            if (_cache.TryGetValue(CacheKey, out Dictionary<string, Dictionary<string, FormRights>>? cached)
                && cached != null)
            {
                return cached;
            }

            var rows = await _db.RolePermissions
                .AsNoTracking()
                .Include(p => p.Role)
                .Where(p => p.Role.IsActive)
                .Select(p => new
                {
                    RoleCode = p.Role.RoleCode,
                    p.FormKey,
                    p.CanView,
                    p.CanAdd,
                    p.CanEdit,
                    p.CanDelete
                })
                .ToListAsync();

            var map = new Dictionary<string, Dictionary<string, FormRights>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var code = (r.RoleCode ?? "").Trim().ToUpperInvariant();
                if (!map.TryGetValue(code, out var forms))
                {
                    forms = new Dictionary<string, FormRights>(StringComparer.OrdinalIgnoreCase);
                    map[code] = forms;
                }

                forms[r.FormKey] = new FormRights(r.CanView, r.CanAdd, r.CanEdit, r.CanDelete);
            }

            _cache.Set(CacheKey, map, CacheFor);
            return map;
        }

        public async Task<FormRights> GetRightsAsync(ClaimsPrincipal user, string formKey)
        {
            if (user?.Identity?.IsAuthenticated != true) return FormRights.None;

            var role = RoleOf(user);
            if (role == SuperRole) return FormRights.Full;

            Dictionary<string, Dictionary<string, FormRights>> map;
            try
            {
                map = await GetMapAsync();
            }
            catch
            {
                // If the permission tables are unreachable, fail closed for everyone except
                // SYSADMIN (handled above) so access is never granted by accident.
                return FormRights.None;
            }

            if (map.TryGetValue(role, out var forms) && forms.TryGetValue(formKey, out var rights))
            {
                return rights;
            }

            // No row configured for this role/form pair means no access.
            return FormRights.None;
        }

        public async Task<bool> CanViewAsync(ClaimsPrincipal user, string formKey)
            => (await GetRightsAsync(user, formKey)).CanView;

        public async Task<IReadOnlyDictionary<string, FormRights>> GetAllRightsAsync(ClaimsPrincipal user)
        {
            var result = new Dictionary<string, FormRights>(StringComparer.OrdinalIgnoreCase);
            if (user?.Identity?.IsAuthenticated != true) return result;

            var role = RoleOf(user);

            if (role == SuperRole)
            {
                foreach (var f in Utils.AppForms.All) result[f.Key] = FormRights.Full;
                return result;
            }

            try
            {
                var map = await GetMapAsync();
                if (map.TryGetValue(role, out var forms))
                {
                    foreach (var kv in forms) result[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // fail closed
            }

            return result;
        }
    }
}
