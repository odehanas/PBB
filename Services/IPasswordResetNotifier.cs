using System;
using System.Threading.Tasks;

namespace GovBudget.Services
{
    public class PasswordResetNotification
    {
        public string UserName { get; set; } = "";
        public string? ContactInfo { get; set; }
        public string ResetUrl { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
    }

    // Abstraction so an SMTP/email implementation can be plugged in later
    // without changing any callers (controllers depend on this interface only).
    public interface IPasswordResetNotifier
    {
        Task NotifyLinkIssuedAsync(PasswordResetNotification notification);
    }
}
