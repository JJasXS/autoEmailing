using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class EmailSender
{
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
        CancellationToken cancellationToken)
    {
        var smtp = await BuildEffectiveSmtpAsync(cancellationToken).ConfigureAwait(false);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipient.Email));
        message.Subject = "Daily SQL Accounting Notification";

        var body = BuildHtmlBody(recipient, scheduleDate);
        message.Body = new TextPart("html") { Text = body };

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

    private static string BuildHtmlBody(EmailRecipient recipient, DateOnly scheduleDate)
    {
        // Layout preview (open in browser): EmailFormats/preview.html — section "Daily batch".
        var safeName = System.Net.WebUtility.HtmlEncode(recipient.Name);
        var safeCode = System.Net.WebUtility.HtmlEncode(recipient.Code);
        var dateStr = scheduleDate.ToString("yyyy-MM-dd", null);

        return $"""
            <html><body style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222;">
            <p>Hello {safeName},</p>
            <p>This is your scheduled <strong>Daily SQL Accounting Notification</strong>.</p>
            <ul>
              <li><strong>User code:</strong> {safeCode}</li>
              <li><strong>Date ({dateStr}):</strong> calendar date in your configured schedule time zone</li>
            </ul>
            <p>If you have questions, contact your system administrator.</p>
            <p style="color:#666;font-size:12px;">Message generated automatically. Please do not reply.</p>
            </body></html>
            """;
    }
}
