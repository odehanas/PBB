namespace GovBudget.Models
{
    public class ErrorViewModel
    {
        public string? RequestId { get; set; }

        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

        // Populated only for admins to diagnose deployed-site errors.
        public string? Path { get; set; }
        public string? ExceptionType { get; set; }
        public string? Message { get; set; }
        public string? StackTrace { get; set; }

        public bool ShowDetails => !string.IsNullOrEmpty(Message);
    }
}
