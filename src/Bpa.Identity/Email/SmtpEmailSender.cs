using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Bpa.Identity.Email;

/// <summary>Sends plain-text email through an SMTP server, using STARTTLS.</summary>
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public bool IsConfigured => options.IsConfigured;

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, cancellationToken);
        if (options.User.Length > 0)
        {
            await client.AuthenticateAsync(options.User, options.Password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
