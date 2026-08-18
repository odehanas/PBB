using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class vw_GL_CashBasis
{
    public int BudgetYear { get; set; }

    public string EntityCode { get; set; } = null!;

    public string EntityName { get; set; } = null!;

    public string DeptCode { get; set; } = null!;

    public string DeptName { get; set; } = null!;

    public string CategoryCode { get; set; } = null!;

    public string GLCode { get; set; } = null!;

    public string GLName { get; set; } = null!;

    public string GLType { get; set; } = null!;

    public decimal AnnualAmount { get; set; }

    public decimal? DistributedAmount { get; set; }

    public decimal M01 { get; set; }

    public decimal M02 { get; set; }

    public decimal M03 { get; set; }

    public decimal M04 { get; set; }

    public decimal M05 { get; set; }

    public decimal M06 { get; set; }

    public decimal M07 { get; set; }

    public decimal M08 { get; set; }

    public decimal M09 { get; set; }

    public decimal M10 { get; set; }

    public decimal M11 { get; set; }

    public decimal M12 { get; set; }
}
