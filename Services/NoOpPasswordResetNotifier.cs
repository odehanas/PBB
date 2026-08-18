using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GovBudget.Services
{
    // Default notifier used until email (SMTP) is configured.
    // The admin delivers the reset link manually; this just records that a link was issued.
    // To enable email later, add an SmtpPasswordResetNotifier : IPasswordResetNotifier
    // and swap the DI registration in Program.cs.
    public class NoOpPasswordResetNotifier : IPasswordResetNotifier
    {
        private readonly ILogger<NoOpPasswordResetNotifier> _logger;

        public NoOpPasswordResetNotifier(ILogger<NoOpPasswordResetNotifier> logger)
        {
            _logger = logger;
        }

        public Task NotifyLinkIssuedAsync(PasswordResetNotification notification)
        {
            _logger.LogInformation(
                "Password reset link issued for user '{User}' (contact: {Contact}). Deliver the link manually until email is configured.",
                notification.UserName,
                string.IsNullOrWhiteSpace(notification.ContactInfo) ? "n/a" : notification.ContactInfo);
            return Task.CompletedTask;
        }
    }
}
