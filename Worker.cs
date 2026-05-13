using System.Globalization;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker.Models;
using SqlAccountingEmailWorker.Services;

namespace SqlAccountingEmailWorker;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly FirebirdUserReader _firebird;
    private readonly EmailSender _emailSender;
    private readonly EmailLogService _emailLog;
    private readonly ScheduleService _schedule;
    private readonly ScheduleOptions _scheduleOptions;
    private readonly ScheduledTestEmailOptions _scheduledTest;

    public Worker(
        ILogger<Worker> logger,
        FirebirdUserReader firebird,
        EmailSender emailSender,
        EmailLogService emailLog,
        ScheduleService schedule,
        IOptions<AppSettings> appOptions)
    {
        _logger = logger;
        _firebird = firebird;
        _emailSender = emailSender;
        _emailLog = emailLog;
        _schedule = schedule;
        _scheduleOptions = appOptions.Value.Schedule;
        _scheduledTest = appOptions.Value.ScheduledTestEmail;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SQL Accounting email worker service started.");
        _logger.LogInformation("Send history file: {Path}", _emailLog.HistoryFilePath);

        var freq = (_scheduleOptions.SendFrequency ?? "Daily").Trim();
        if (_schedule.IsWeeklySchedule())
            _logger.LogInformation(
                "Schedule mode: Weekly on {Day} at {SendTime} ({TzId}).",
                _scheduleOptions.SendDayOfWeek,
                _schedule.GetSendTimeOfDay().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                _scheduleOptions.TimeZone);
        else
            _logger.LogInformation(
                "Schedule mode: Daily at {SendTime} ({TzId}). SendFrequency={Freq}.",
                _schedule.GetSendTimeOfDay().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                _scheduleOptions.TimeZone,
                string.IsNullOrEmpty(freq) ? "Daily" : freq);

        var next = _schedule.GetNextSendUtc(DateTimeOffset.UtcNow);
        var tz = _schedule.GetTimeZone();
        var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(next.UtcDateTime, tz);
        _logger.LogInformation(
            "Next scheduled send — local: {NextLocal} ({TzId}) | UTC: {NextUtc:o} | Configured time-of-day: {SendTime}",
            nextLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            tz.Id,
            next.UtcDateTime,
            _schedule.GetSendTimeOfDay().ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = _schedule.GetNextSendUtc(now);
            var delay = next - now;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            var tz = _schedule.GetTimeZone();
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, tz);
            var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(next.UtcDateTime, tz);
            const string localFmt = "yyyy-MM-dd HH:mm:ss";
            _logger.LogInformation(
                "Now local: {NowLocal} ({TzId}) | Next send local: {NextLocal} | Next UTC: {NextUtc:o} | Sleep: {Delay}",
                nowLocal.ToString(localFmt, CultureInfo.InvariantCulture),
                tz.Id,
                nextLocal.ToString(localFmt, CultureInfo.InvariantCulture),
                next.UtcDateTime,
                delay);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            await RunSendBatchAsync(sendNow: false, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Manual test / one-shot send (--send-now).</summary>
    public Task RunOnceNowAsync(CancellationToken cancellationToken) =>
        RunSendBatchAsync(sendNow: true, cancellationToken);

    private async Task RunSendBatchAsync(bool sendNow, CancellationToken cancellationToken)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var scheduleDate = _schedule.GetScheduleDateLocal(utcNow);

        _logger.LogInformation(
            "Starting email batch ({Mode}). Schedule date (local): {ScheduleDate}.",
            sendNow ? "manual --send-now" : "scheduled",
            scheduleDate);

        if (!sendNow && _scheduledTest.Enabled && !string.IsNullOrWhiteSpace(_scheduledTest.PlainBody))
        {
            await SendScheduledTestEmailsAsync(cancellationToken).ConfigureAwait(false);
            if (_scheduledTest.SkipDailyBatch)
            {
                _logger.LogInformation("ScheduledTestEmail:SkipDailyBatch is true — skipping Firebird batch for this tick.");
                return;
            }
        }

        IReadOnlyList<EmailRecipient> recipients;
        try
        {
            recipients = await _firebird.GetRecipientsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recipients from Firebird.");
            return;
        }

        _logger.LogInformation("Recipients found (after UDF filter): {Count}.", recipients.Count);

        foreach (var r in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(r.Code))
            {
                _logger.LogWarning("Skipping user with empty CODE, email {Email}.", r.Email);
                continue;
            }

            if (await _emailLog.WasAlreadySentAsync(r.Code, scheduleDate, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Skipping {Code} ({Email}): already sent successfully for {ScheduleDate}.",
                    r.Code, r.Email, scheduleDate);
                continue;
            }

            try
            {
                await _emailSender.SendDailyNotificationAsync(r, scheduleDate, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Email sent successfully to {Email} (user {Code}, {Name}).",
                    r.Email, r.Code, r.Name);
                await _emailLog.RecordSuccessAsync(r.Code, scheduleDate, r.Email, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Email failed for {Email} (user {Code}, {Name}).",
                    r.Email, r.Code, r.Name);
                await _emailLog.RecordFailureAsync(r.Code, scheduleDate, r.Email, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Email batch completed.");
    }

    private async Task SendScheduledTestEmailsAsync(CancellationToken cancellationToken)
    {
        var subject = string.IsNullOrWhiteSpace(_scheduledTest.Subject)
            ? "Auto emailing test"
            : _scheduledTest.Subject.Trim();
        var body = _scheduledTest.PlainBody;

        if (!string.IsNullOrWhiteSpace(_scheduledTest.To))
        {
            try
            {
                await _emailSender.SendPlainTextAsync(_scheduledTest.To, subject, body, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Scheduled test email sent (single override) to {To}.", _scheduledTest.To);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled test email failed for {To}.", _scheduledTest.To);
            }

            return;
        }

        IReadOnlyList<EmailRecipient> recipients;
        try
        {
            recipients = await _firebird.GetRecipientsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled test: failed to load SY_USER recipients from Firebird.");
            return;
        }

        if (recipients.Count == 0)
        {
            _logger.LogWarning("Scheduled test: no recipients (SY_USER with EMAIL and UDF_AEMAIL enabled).");
            return;
        }

        _logger.LogInformation("Scheduled test: sending to {Count} SY_USER recipient(s) (UDF_AEMAIL).", recipients.Count);

        foreach (var r in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(r.Code))
            {
                _logger.LogWarning("Scheduled test: skipping user with empty CODE, email {Email}.", r.Email);
                continue;
            }

            try
            {
                await _emailSender.SendPlainTextAsync(r.Email, subject, body, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Scheduled test email sent to {Email} (user {Code}).", r.Email, r.Code);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled test email failed for {Email} (user {Code}).", r.Email, r.Code);
            }
        }
    }
}
