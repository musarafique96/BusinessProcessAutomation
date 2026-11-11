namespace Bpa.Identity.Email;

/// <summary>
/// Sends email. When no mail server is configured, IsConfigured is false and nothing is sent,
/// so admins keep sharing links by hand.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Used when no SMTP server is configured.</summary>
public sealed class NoEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Email is not configured.");
}
