using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Placeholders;

/// <summary>
/// The default <see cref="IEmailSender"/>: it logs the message and returns.
///
/// <para><b>This is not a stub standing in for missing behaviour — it is a faithful port of
/// Node's own default.</b> <c>server/src/services/emailService.js</c> auto-detects Resend, then
/// SMTP, then falls back to a console transport that prints the message and resolves
/// successfully. With neither <c>RESEND_API_KEY</c> nor <c>SMTP_HOST</c> set — which is the state
/// of every development environment on this project — the live Node backend logs and answers 200,
/// and so does this.</para>
///
/// <para>It logs at INFORMATION rather than warning, for the same reason: nothing is being
/// skipped that a configured deployment would have done differently. Configuring real mail means
/// registering a real <see cref="IEmailSender"/> AFTER <c>AddInfrastructureShared</c>, whose
/// registration then wins.</para>
///
/// <para>The body is deliberately NOT logged. An invite link is a credential-shaped URL and the
/// message body can carry clinical context; the recipient, subject and size are enough to confirm
/// the send happened.</para>
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        logger.LogInformation(
            "[Email] Console transport: message to {To}, subject {Subject} ({HtmlLength} bytes of "
            + "HTML). No mail provider is configured, which is Node's own default behaviour — the "
            + "endpoint reports success exactly as the Node backend does.",
            message.To,
            message.Subject,
            message.Html.Length);

        return Task.CompletedTask;
    }
}
