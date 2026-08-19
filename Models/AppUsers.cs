using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class AppUsers
{
    public int UserId { get; set; }

    public string UserName { get; set; } = null!;

    // Legacy clear-text column. Kept only so existing rows can be migrated on first run;
    // it is cleared once PasswordHash is populated and must never be written again.
    public string? Password { get; set; }

    // PBKDF2-SHA256 hash in the format produced by Services.PasswordHasher.
    public string? PasswordHash { get; set; }

    public DateTime? PasswordUpdatedAt { get; set; }

    // Set when an administrator issues a password; forces a change at next sign-in.
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTime? LockoutEndUtc { get; set; }

    public DateTime? LastLoginAt { get; set; }

    // Changed whenever credentials, role or scope change; live sessions carrying an old
    // stamp are rejected on the next request.
    public string? SecurityStamp { get; set; }

    public string Role { get; set; } = null!;

    public int? EntityId { get; set; }

    public int? DepartmentId { get; set; }

    public bool IsActive { get; set; }

    public virtual Entities? Entity { get; set; }

    public virtual Departments? Department { get; set; }
}
