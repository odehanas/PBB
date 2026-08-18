using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class WhatIfScenarios
{
    public int ScenarioId { get; set; }

    public int BudgetYear { get; set; }

    public int? EntityId { get; set; }

    public int? DepartmentId { get; set; }

    public string ScenarioName { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public virtual WhatIfScenarioDefaults? WhatIfScenarioDefaults { get; set; }

    public virtual ICollection<WhatIfScenarioProjectRates> WhatIfScenarioProjectRates { get; set; } = new List<WhatIfScenarioProjectRates>();
}

public partial class WhatIfScenarioDefaults
{
    public int ScenarioId { get; set; }

    public decimal CostInflationRate { get; set; }

    public decimal RevenueGrowthRate { get; set; }

    public virtual WhatIfScenarios Scenario { get; set; } = null!;
}

public partial class WhatIfScenarioProjectRates
{
    public long ScenarioProjectRateId { get; set; }

    public int ScenarioId { get; set; }

    public int ProjectId { get; set; }

    public decimal? CostInflationRate { get; set; }

    public decimal? RevenueGrowthRate { get; set; }

    public virtual WhatIfScenarios Scenario { get; set; } = null!;

    public virtual Projects Project { get; set; } = null!;
}
