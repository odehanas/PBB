using System;

namespace GovBudget.Models;

public partial class AuditLogs
{
    public long AuditLogId { get; set; }

    public DateTime Timestamp { get; set; }

    public string UserName { get; set; } = null!;

    public string Action { get; set; } = null!;

    public string? EntityName { get; set; }

    public string? RecordId { get; set; }

    public string? Details { get; set; }
}
