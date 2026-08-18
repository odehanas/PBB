using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class Kpis
{
    public long KpiId { get; set; }

    public int BudgetYear { get; set; }

    public string Period { get; set; } = "MidYear";

    public int EntityId { get; set; }

    public int? ProgramId { get; set; }

    public int? ActivityId { get; set; }

    public string KpiName { get; set; } = null!;

    public string? Unit { get; set; }

    // PBB classification. Type: Input | Output | Outcome. Dimension: Efficiency | Quality.
    // ReadingType: Cumulative | Rate. Nullable so existing rows remain valid.
    public string? KpiType { get; set; }

    public string? Dimension { get; set; }

    public string? ReadingType { get; set; }

    // Extended KPI definition fields (from the source KPI sheet). All nullable.
    public string? Priority { get; set; }

    public string? KpiCode { get; set; }

    public string? CalculationMethod { get; set; }

    public string? Scope { get; set; }

    public string? ProgramOwner { get; set; }

    // Long-horizon strategic target (e.g. 2029) distinct from the annual Target.
    public decimal? StrategicTarget2029 { get; set; }

    // Relative weight used to distribute the linked activity's cost across its KPIs.
    // When null/zero for all KPIs of an activity, the split falls back to equal.
    public decimal? CostWeight { get; set; }

    public string Direction { get; set; } = "UP";

    public decimal? Baseline { get; set; }

    public decimal? Target { get; set; }

    public decimal? ActualValue { get; set; }

    public string? Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public virtual Entities Entity { get; set; } = null!;

    public virtual Programs? Program { get; set; }

    public virtual Activities? Activity { get; set; }

    public virtual ICollection<KpiCostLinks> KpiCostLinks { get; set; } = new List<KpiCostLinks>();
}

public partial class KpiCostLinks
{
    public long KpiCostLinkId { get; set; }

    public long KpiId { get; set; }

    public int? ActivityId { get; set; }

    public int? ProgramId { get; set; }

    public decimal WeightPct { get; set; }

    public virtual Kpis Kpi { get; set; } = null!;

    public virtual Activities? Activity { get; set; }

    public virtual Programs? Program { get; set; }
}
