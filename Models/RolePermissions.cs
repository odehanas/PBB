using System;
using System.Collections.Generic;

namespace GovBudget.Models;

// What a role may do on one form (screen). FormKey matches a key in Utils.AppForms;
// a missing row means "no access at all" for that role/form pair.
//
// CanView is the master switch: without it the form cannot be opened and the other
// three flags are irrelevant. Granting CanView alone produces a review-only user.
public partial class RolePermissions
{
    public int RolePermissionId { get; set; }

    public int RoleId { get; set; }

    public string FormKey { get; set; } = null!;

    public bool CanView { get; set; }

    public bool CanAdd { get; set; }

    public bool CanEdit { get; set; }

    public bool CanDelete { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public virtual AppRoles Role { get; set; } = null!;
}
