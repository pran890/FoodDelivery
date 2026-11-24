using System.Data.Common;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FoodDelivery.Identity.Features.Auth;

/// <summary>How a code reaches the user. A WhatsApp sender could be added later.</summary>
internal interface IOtpSender
{
    Task SendAsync(string email, string code, CancellationToken ct);
}

internal sealed class EmailOtpSender(IConfiguration configuration, IOptions<OtpOptions> options) : IOtpSender
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    public async Task SendAsync(string email, string code, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.Value.From));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = "Your sign-in code";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Your code is {code}

                It is valid for {options.Value.Lifetime.TotalMinutes:0} minutes and can be used once.
                If you didn't ask for it, ignore this email and don't share the code.
                """,
        };

        var server = SmtpServer();
        using var smtp = new SmtpClient();
        // Encrypted when the server offers it. Mailpit on localhost doesn't; a real provider must,
        // and would be configured with StartTls (required) instead.
        await smtp.ConnectAsync(server.Host, server.Port, SecureSocketOptions.StartTlsWhenAvailable, timeout.Token);
        await smtp.SendAsync(new SendRequest(message), timeout.Token);
        await smtp.DisconnectAsync(quit: true, timeout.Token);
    }

    // The AppHost injects "Endpoint=smtp://host:port".
    private Uri SmtpServer()
    {
        var connectionString = new DbConnectionStringBuilder { ConnectionString = configuration.GetConnectionString("mailpit") };
        return new Uri((string)connectionString["Endpoint"]);
    }
}
