using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class AppUsers
{
    public int UserId { get; set; }

    public string UserName { get; set; } = null!;

    public string Password { get; set; } = null!;

    public string Role { get; set; } = null!;

    public int? EntityId { get; set; }

    public int? DepartmentId { get; set; }

    public bool IsActive { get; set; }

    public virtual Entities? Entity { get; set; }

    public virtual Departments? Department { get; set; }
}
