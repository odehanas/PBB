using System;

namespace GovBudget.Models;

/// <summary>Lookup of allocation drivers (e.g. headcount, floor area, transaction volume).</summary>
public partial class AllocationDrivers
{
    public int DriverId { get; set; }

    public string DriverCode { get; set; } = "";

    public string DriverName { get; set; } = "";

    public string? Unit { get; set; }

    public bool IsActive { get; set; } = true;
}
