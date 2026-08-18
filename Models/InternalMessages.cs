using System;
using System.Collections.Generic;

namespace GovBudget.Models;

public partial class InternalMessages
{
    public long MessageId { get; set; }

    public string FromUser { get; set; } = null!;

    public string? FromEntityCode { get; set; }

    public string? FromDeptCode { get; set; }

    public string Subject { get; set; } = null!;

    public string Body { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }

    public string? ReadBy { get; set; }

    public string? AdminResponse { get; set; }

    public DateTime? RespondedAt { get; set; }

    public string? RespondedBy { get; set; }
}
