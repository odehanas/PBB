using System;

namespace GovBudget.Models;

public partial class PasswordResetRequests
{
    public long ResetRequestId { get; set; }

    public string UserName { get; set; } = null!;

    public int? UserId { get; set; }

    public int? EntityId { get; set; }

    public string? ContactInfo { get; set; }

    public string? Note { get; set; }

    // Pending, LinkIssued, Completed, Rejected
    public string Status { get; set; } = "Pending";

    // Login, Admin
    public string? RequestSource { get; set; }

    public DateTime RequestedAt { get; set; }

    // Legacy clear-text token column. No longer written; kept for existing rows.
    public string? Token { get; set; }

    // SHA-256 digest of the issued token. The clear-text token only ever exists in the
    // link handed to the user.
    public string? TokenHash { get; set; }

    public DateTime? TokenExpiresAt { get; set; }

    public DateTime? TokenUsedAt { get; set; }

    public DateTime? IssuedAt { get; set; }

    public string? IssuedBy { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? RejectedAt { get; set; }

    public string? RejectedBy { get; set; }

    public string? AdminNote { get; set; }
}
