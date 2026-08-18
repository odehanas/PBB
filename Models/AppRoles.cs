using System;
using System.Collections.Generic;

namespace GovBudget.Models;

// A login role. SYSADMIN / ADMIN / USER are seeded as built-in roles and cannot be
// deleted; a system admin may add further roles (e.g. a review-only role) and grant
// them form-level rights on the Roles & Permissions screen.
public partial class AppRoles
{
    public int RoleId { get; set; }

    // Stored on AppUsers.Role and issued as the role claim, so always upper-case.
    public string RoleCode { get; set; } = null!;

    public string RoleName { get; set; } = null!;

    public string? Description { get; set; }

    // Built-in roles the application logic depends on; they cannot be renamed or removed.
    public bool IsSystem { get; set; }

    // Entity-scoped roles are always restricted to the user's own entity and can never
    // browse other entities. Only a non-scoped role (SYSADMIN) sees every entity.
    public bool IsEntityScoped { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<RolePermissions> RolePermissions { get; set; } = new List<RolePermissions>();
}
