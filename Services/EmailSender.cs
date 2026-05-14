using System.Globalization;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class EmailSender
{
    /// <summary>Content-Id values for SO outstanding attachments (HTML <c>cid:</c> links).</summary>
    private const string SoOutstandingXlsxContentId = "so-outstanding-xlsx@autoemailing.local";
    private const string SoOutstandingPdfContentId = "so-outstanding-pdf@autoemailing.local";

    private static readonly CultureInfo SubjectCulture = CultureInfo.GetCultureInfo("en-GB");

    private readonly SmtpOptions _smtp;
    private readonly IConfiguration _configuration;
    private readonly TenantBootstrapService _tenantBootstrap;
    private readonly AwsSecretsReader _secretsReader;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(
        IOptions<AppSettings> appOptions,
        IConfiguration configuration,
        TenantBootstrapService tenantBootstrap,
        AwsSecretsReader secretsReader,
        ILogger<EmailSender> logger)
    {
        _smtp = appOptions.Value.Smtp;
        _configuration = configuration;
        _tenantBootstrap = tenantBootstrap;
        _secretsReader = secretsReader;
        _logger = logger;
    }

    public async Task SendDailyNotificationAsync(
        EmailRecipient recipient,
        DateOnly scheduleDate,
        IReadOnlyList<EmailAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var smtp = await BuildEffectiveSmtpAsync(cancellationToken).ConfigureAwait(false);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipient.Email));
        message.Subject = HasSoOutstandingAttachments(attachments)
            ? $"Sales outstanding as of {scheduleDate.ToString("dddd, d MMMM yyyy", SubjectCulture)}"
            : "Scheduled SQL Accounting notification";

        var body = BuildHtmlBody(recipient, scheduleDate, attachments);
        message.Body = BuildMessageBody(body, attachments);

        using var client = CreateSmtpClient();
        var secure = ResolveSecureSocketOption(smtp.EnableSsl, smtp.Port);

        await client.ConnectAsync(smtp.Host, smtp.Port, secure, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(smtp.User))
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? "", cancellationToken).ConfigureAwait(false);

        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("SMTP send completed for {Email}", recipient.Email);
    }

    /// <summary>Connects with current <see cref="SmtpOptions"/> and sends a short plain-text message (for <c>--test-smtp</c>).</summary>
    public async Task SendTestMessageAsync(string toEmail, CancellationToken cancellationToken)
    {
        var smtp = await BuildEffectiveSmtpAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(toEmail))
            throw new ArgumentException("Recipient email is required.", nameof(toEmail));

        var otp = Random.Shared.Next(100000, 1_000_000).ToString("D6", null);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail.Trim()));
        message.Subject = "Your OTP code";
        message.Body = new TextPart("plain")
        {
            Text =
                $"Your OTP code is: {otp}\r\n\r\n" +
                "This is a manual SMTP test from the SQL Accounting Email Worker. Do not share this code; it is not tied to any login."
        };

        using var client = CreateSmtpClient();
        var secure = ResolveSecureSocketOption(smtp.EnableSsl, smtp.Port);
        _logger.LogInformation(
            "SMTP test: connecting to {Host}:{Port} (timeout {TimeoutMs} ms, SSL mode {Secure})…",
            smtp.Host,
            smtp.Port,
            client.Timeout,
            secure);
        await client.ConnectAsync(smtp.Host, smtp.Port, secure, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(smtp.User))
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? "", cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("OTP test email sent to {Email} (code {Otp}).", toEmail, otp);
    }

    /// <summary>Sends a plain-text message (used by scheduled test and <c>--wait-scheduled-test</c>).</summary>
    public async Task SendPlainTextAsync(string toEmail, string subject, string plainBody, CancellationToken cancellationToken)
    {
        var smtp = await BuildEffectiveSmtpAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(toEmail))
            throw new ArgumentException("Recipient email is required.", nameof(toEmail));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail.Trim()));
        message.Subject = string.IsNullOrWhiteSpace(subject) ? "Auto emailing test" : subject.Trim();
        message.Body = new TextPart("plain") { Text = plainBody ?? "" };

        using var client = CreateSmtpClient();
        var secure = ResolveSecureSocketOption(smtp.EnableSsl, smtp.Port);
        _logger.LogInformation(
            "Plain-text send: connecting to {Host}:{Port} (timeout {TimeoutMs} ms)…",
            smtp.Host,
            smtp.Port,
            client.Timeout);
        await client.ConnectAsync(smtp.Host, smtp.Port, secure, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(smtp.User))
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? "", cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Plain-text email sent to {Email}.", toEmail);
    }

    private readonly record struct EffectiveSmtp(
        string Host,
        int Port,
        bool EnableSsl,
        string User,
        string? Password,
        string FromAddress,
        string FromName);

    /// <summary>Merges <see cref="SmtpOptions"/> with tenant API <c>email</c> when <see cref="TenantBootstrapService.IsTenantMode"/>.</summary>
    private async Task<EffectiveSmtp> BuildEffectiveSmtpAsync(CancellationToken cancellationToken)
    {
        var o = _smtp;
        var host = (o.SmtpHost ?? "").Trim();
        var port = o.SmtpPort;
        var enableSsl = o.EnableSsl;
        var user = (o.SmtpUser ?? "").Trim();
        var fromEmail = (o.FromEmail ?? "").Trim();
        var fromName = string.IsNullOrWhiteSpace(o.FromName) ? "SQL Accounting" : o.FromName;
        string? inlinePassword = string.IsNullOrWhiteSpace(o.SmtpPassword) ? null : o.SmtpPassword;
        var appSecretRef = (o.SmtpPasswordSecretRef ?? "").Trim();

        if (TenantBootstrapService.IsTenantMode(_configuration))
        {
            var tenantCode = (_configuration["TENANT_CODE"] ?? _configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(tenantCode))
            {
                var ov = await _tenantBootstrap.GetTenantSmtpOverridesAsync(tenantCode, cancellationToken).ConfigureAwait(false);
                if (ov != null)
                {
                    if (!string.IsNullOrWhiteSpace(ov.SmtpHost))
                        host = ov.SmtpHost.Trim();
                    if (ov.SmtpPort is { } p and > 0)
                        port = p;
                    if (!string.IsNullOrWhiteSpace(ov.SmtpUser))
                        user = ov.SmtpUser.Trim();
                    if (!string.IsNullOrWhiteSpace(ov.FromEmail))
                        fromEmail = ov.FromEmail.Trim();
                    if (!string.IsNullOrWhiteSpace(ov.SmtpPassword))
                        inlinePassword = ov.SmtpPassword;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                "Smtp:SmtpHost is required (set App__Smtp__SmtpHost or tenant JSON email.smtpHost).");
        }

        string password;
        if (!string.IsNullOrWhiteSpace(inlinePassword))
            password = inlinePassword!;
        else if (!string.IsNullOrEmpty(appSecretRef))
        {
            _logger.LogDebug("Loading SMTP password from Secrets Manager secret {SecretRef}.", appSecretRef);
            var raw = await _secretsReader.GetSecretStringAsync(appSecretRef, cancellationToken).ConfigureAwait(false);
            password = AwsSecretsReader.CoercePasswordFromSecretString(raw) ?? "";
        }
        else
            password = "";

        var fromAddress = !string.IsNullOrWhiteSpace(fromEmail) ? fromEmail : user;
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException(
                "A sender address is required: set App__Smtp__FromEmail and/or App__Smtp__SmtpUser in .env or appsettings.json, " +
                "or define tenant JSON email.smtpSenderEmail (with smtpAppPasswordSecretRef) when using TENANT_CODE.");
        }

        return new EffectiveSmtp(host, port, enableSsl, user, password, fromAddress, fromName);
    }

    private SmtpClient CreateSmtpClient()
    {
        var client = new SmtpClient();
        var ms = _smtp.SmtpTimeoutMs <= 0 ? 30_000 : _smtp.SmtpTimeoutMs;
        client.Timeout = Math.Clamp(ms, 5_000, 300_000);
        return client;
    }

    private static SecureSocketOptions ResolveSecureSocketOption(bool enableSsl, int port)
    {
        if (!enableSsl)
            return SecureSocketOptions.None;

        if (port == 465)
            return SecureSocketOptions.SslOnConnect;

        return SecureSocketOptions.StartTlsWhenAvailable;
    }

    private static bool HasSoOutstandingAttachments(IReadOnlyList<EmailAttachment>? attachments) =>
        FindSoOutstanding(attachments, ".xlsx") is not null && FindSoOutstanding(attachments, ".pdf") is not null;

    private static EmailAttachment? FindSoOutstanding(IReadOnlyList<EmailAttachment>? attachments, string ext)
    {
        if (attachments is null)
            return null;
        foreach (var a in attachments)
        {
            if (!a.FileName.StartsWith("SO-transfer-outstanding_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (a.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        return null;
    }

    private static string BuildHtmlBody(EmailRecipient recipient, DateOnly scheduleDate, IReadOnlyList<EmailAttachment>? attachments)
    {
        var hasAttachments = attachments is { Count: > 0 };
        var soXlsx = FindSoOutstanding(attachments, ".xlsx");
        var soPdf = FindSoOutstanding(attachments, ".pdf");
        var hasSoButtons = soXlsx is not null && soPdf is not null;

        // Outstanding report layout: EmailFormats/outstanding-report.template.html + OutstandingReportEmailTemplate (separate from this HTML stub).
        var safeName = System.Net.WebUtility.HtmlEncode(recipient.Name);
        var safeCode = System.Net.WebUtility.HtmlEncode(recipient.Code);
        var dateStr = scheduleDate.ToString("yyyy-MM-dd", null);

        var attachNote = hasAttachments && !hasSoButtons
            ? "<p><strong>Attachments:</strong> Excel (<code>.xlsx</code>) and PDF (<code>.pdf</code>) for the same schedule date.</p>"
            : hasAttachments && hasSoButtons
                ? "<p><strong>Sales outstanding</strong> (as of the date shown below) is attached as Excel and PDF. Use the buttons below, or open the files from your mail app’s attachment list.</p>"
                : "";

        var soButtonRow = "";
        if (hasSoButtons)
        {
            var xName = System.Net.WebUtility.HtmlEncode(soXlsx!.FileName);
            var pName = System.Net.WebUtility.HtmlEncode(soPdf!.FileName);
            soButtonRow = $"""
                <div style="margin:18px 0 8px 0;">
                  <a href="cid:{SoOutstandingXlsxContentId}" style="display:inline-block;padding:12px 22px;margin:0 8px 8px 0;background:#1a365d;color:#ffffff !important;text-decoration:none;border-radius:8px;font-weight:600;font-size:14px;">Download Excel</a>
                  <a href="cid:{SoOutstandingPdfContentId}" style="display:inline-block;padding:12px 22px;margin:0 0 8px 0;background:#2b6cb0;color:#ffffff !important;text-decoration:none;border-radius:8px;font-weight:600;font-size:14px;">Download PDF</a>
                </div>
                <p style="font-size:12px;color:#555;margin:0 0 12px 0;">Filenames: <code>{xName}</code> and <code>{pName}</code>. Some mail clients only support downloads from the attachment list.</p>
                """;
        }
        else if (hasAttachments)
        {
            soButtonRow = "<p style=\"font-size:12px;color:#555;\">See attached files at the bottom of this message.</p>";
        }

        return $"""
            <html><body style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222;">
            <p>Hello {safeName},</p>
            <p>This is your scheduled <strong>SQL Accounting notification</strong> (sent at your configured send time in the schedule time zone).</p>
            {attachNote}
            {soButtonRow}
            <ul>
              <li><strong>User code:</strong> {safeCode}</li>
              <li><strong>Date ({dateStr}):</strong> reference date in the configured schedule time zone</li>
            </ul>
            <p>If you have questions, contact your system administrator.</p>
            <p style="color:#666;font-size:12px;">Message generated automatically. Please do not reply.</p>
            </body></html>
            """;
    }

    private static MimeEntity BuildMessageBody(string htmlBody, IReadOnlyList<EmailAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return new TextPart("html") { Text = htmlBody };

        var mixed = new Multipart("mixed");
        mixed.Add(new TextPart("html") { Text = htmlBody });

        var soXlsxPart = FindSoOutstanding(attachments, ".xlsx");
        var soPdfPart = FindSoOutstanding(attachments, ".pdf");

        foreach (var a in attachments)
        {
            var slash = a.ContentType.IndexOf('/');
            var major = slash > 0 ? a.ContentType[..slash] : "application";
            var minor = slash > 0 && slash + 1 < a.ContentType.Length ? a.ContentType[(slash + 1)..] : "octet-stream";
            var part = new MimePart(major, minor)
            {
                Content = new MimeContent(new MemoryStream(a.Content, writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = a.FileName
            };

            if (soXlsxPart is not null && string.Equals(a.FileName, soXlsxPart.FileName, StringComparison.OrdinalIgnoreCase))
                part.ContentId = SoOutstandingXlsxContentId;
            else if (soPdfPart is not null && string.Equals(a.FileName, soPdfPart.FileName, StringComparison.OrdinalIgnoreCase))
                part.ContentId = SoOutstandingPdfContentId;

            mixed.Add(part);
        }

        return mixed;
    }
}
