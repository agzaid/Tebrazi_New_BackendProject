namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published port onto outbound email, a direct port of
/// <c>server/src/services/emailService.js</c>'s <c>sendEmail</c>.
///
/// <para>Connections needs it for <c>POST /api/connections/invite-email</c>
/// (connections.js:984-998), the one endpoint in that router whose entire product is an email.
/// Identity's password-reset and Clinics' staff-invitation routes will want it too.</para>
///
/// <para><b>⚠ This port DOES throw, and the Node function it ports NEVER DOES.</b> That asymmetry
/// is the whole contract note, and an earlier version of this file got it backwards. The Node route
/// <c>await</c>s <c>sendEmail</c> with no <c>.catch</c> (connections.js:985-998), which reads like
/// "a transport failure is a 500" — but <c>sendEmail</c> cannot reject:
/// <c>emailService.js:92-96</c> opens with <c>await getTransport()</c>, whose every throwing step
/// (<c>require('resend')</c>, <c>new Resend</c>, <c>require('nodemailer')</c>,
/// <c>createTransport</c>) is individually try/caught at :26-64 and which always ends at the console
/// fallback at :60-65; the rest of the body is wrapped in a <c>try</c> closing at :140-143 with
/// <c>return { success: false, error }</c>, and the Resend branch returns that object directly at
/// :109 without throwing at all. The function always resolves, and the route discards its result.
/// <b>So every send outcome — success, a rejected Resend call, a thrown SMTP call, an unconfigured
/// transport — answers <c>200 {"success":true,"message":"Invitation sent"}</c>.</b></para>
///
/// <para><b>What a caller reproducing connections.js must therefore do:</b> wrap
/// <see cref="SendAsync"/> in a swallow-all block, log the exception, and return the route's success
/// body regardless. Letting the exception reach the named-500 guard would answer 500 where Node
/// answers 200 — and would do so only once a real transport is registered, because
/// <c>ConsoleEmailSender</c> never throws. A caller porting a DIFFERENT Node function must read that
/// function's own error handling; this note is about <c>sendEmail</c> and nothing else.</para>
///
/// <para><b>An unconfigured transport is SUCCESS, not failure.</b> Node auto-detects Resend, then
/// SMTP, then falls back to logging the message to the console and returning normally — so the
/// live Node backend answers 200 for this endpoint whether or not mail is configured, and the
/// default implementation reproduces exactly that.</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends one message. Throws when a CONFIGURED transport fails — see the type-level note: a
    /// caller reproducing <c>connections.js</c>'s <c>sendEmail</c> must swallow that throw.
    /// </summary>
    /// <param name="message">The message. See <see cref="EmailMessage"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <param name="To">The recipient address, unvalidated — Node passes the request body straight through.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Html">
/// The HTML body, already interpolated by the caller. <b>Not escaped anywhere in Node</b>: the
/// invite template drops the physician's display name straight into the markup
/// (connections.js:991), so the port must not start escaping it either — that would change the
/// bytes of every invite for a name containing an apostrophe.
/// </param>
/// <param name="Text">The plain-text alternative, or null.</param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string Html,
    string? Text = null);
