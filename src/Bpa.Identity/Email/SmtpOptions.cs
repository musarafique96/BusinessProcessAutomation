namespace Bpa.Identity.Email;

/// <summary>
/// SMTP settings from configuration section "Smtp". Works with any provider that offers SMTP,
/// so changing provider means changing these values only.
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";

    public bool IsConfigured => Host.Length > 0 && From.Length > 0;
}
