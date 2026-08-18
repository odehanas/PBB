namespace GovBudget.Models;

public partial class CostShapeMap
{
    public int CostShapeMapId { get; set; }

    public string? GLCode { get; set; }

    public string? MatchKeyword { get; set; }

    public string Bucket { get; set; } = null!;

    public int Priority { get; set; }

    public bool IsActive { get; set; }
}
