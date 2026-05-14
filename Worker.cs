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
    private readonly ScheduledTestEmailOptions _scheduledTest;
    private readonly DailyAttachmentReportOptions _dailyAttachmentReport;
    private readonly SoTransferOutstandingReportOptions _soTransferReport;
    private readonly DailyReportExcelPdfGenerator _reportAttachmentGenerator;
    private readonly SoTransferOutstandingReportService _soTransferOutstandingReport;
    private readonly SoTransferOutstandingExportBuilder _soTransferOutstandingExport;

    public Worker(
        ILogger<Worker> logger,
        FirebirdUserReader firebird,
        EmailSender emailSender,
        EmailLogService emailLog,
        ScheduleService schedule,
        DailyReportExcelPdfGenerator reportAttachmentGenerator,
        SoTransferOutstandingReportService soTransferOutstandingReport,
        SoTransferOutstandingExportBuilder soTransferOutstandingExport,
        IOptions<AppSettings> appOptions)
    {
        _logger = logger;
        _firebird = firebird;
        _emailSender = emailSender;
        _emailLog = emailLog;
        _schedule = schedule;
        _reportAttachmentGenerator = reportAttachmentGenerator;
        _soTransferOutstandingReport = soTransferOutstandingReport;
        _soTransferOutstandingExport = soTransferOutstandingExport;
        _scheduledTest = appOptions.Value.ScheduledTestEmail;
        _dailyAttachmentReport = appOptions.Value.DailyAttachmentReport;
        _soTransferReport = appOptions.Value.SoTransferOutstandingReport;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SQL Accounting email worker service started.");
        _logger.LogInformation("Send history file (append-only audit, does not block sends): {Path}", _emailLog.HistoryFilePath);

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
            const string lf = "yyyy-MM-dd HH:mm:ss";
            var sendTimeStr = _schedule.GetSendTimeOfDay().ToString("HH:mm", CultureInfo.InvariantCulture);
            _logger.LogInformation(
                "Next send when local clock reaches {SendTime} in {TzId}: now {NowLocal} → next {NextLocal} (~{Delay}).",
                sendTimeStr,
                tz.Id,
                nowLocal.ToString(lf, CultureInfo.InvariantCulture),
                nextLocal.ToString(lf, CultureInfo.InvariantCulture),
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

            var wokeAt = DateTimeOffset.UtcNow;
            if (wokeAt < next - TimeSpan.FromSeconds(1))
            {
                _logger.LogWarning(
                    "Skipping batch: woke before scheduled instant (now {Woke:o}, next {Next:o}). Recomputing sleep.",
                    wokeAt,
                    next);
                continue;
            }

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
        var sendTimeSlot = _schedule.GetSendTimeOfDay().ToString("HH:mm", CultureInfo.InvariantCulture);

        _logger.LogInformation(
            "Starting email batch ({Mode}). Schedule date (local): {ScheduleDate}.",
            sendNow ? "manual --send-now" : "scheduled",
            scheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (!sendNow && _scheduledTest.Enabled && !string.IsNullOrWhiteSpace(_scheduledTest.PlainBody))
        {
            await SendScheduledTestEmailsAsync(cancellationToken).ConfigureAwait(false);
            if (_scheduledTest.SkipDailyBatch)
            {
                _logger.LogInformation(
                    "ScheduledTestEmail:SkipDailyBatch is true — skipping main scheduled send (SO / attachments) for this tick.");
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

        var batchAttachments = new List<EmailAttachment>();
        if (_dailyAttachmentReport.Enabled && !string.IsNullOrWhiteSpace(_dailyAttachmentReport.Sql))
        {
            try
            {
                var rows = await _firebird.GetDailyReportRowsAsync(_dailyAttachmentReport.Sql, cancellationToken)
                    .ConfigureAwait(false);
                batchAttachments.AddRange(_reportAttachmentGenerator.BuildAttachments(rows, _dailyAttachmentReport, scheduleDate));
                _logger.LogInformation(
                    "Daily attachment report: built Excel + PDF ({RowCount} row(s)).",
                    rows.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily attachment report failed; continuing without those attachments.");
            }
        }

        if (_soTransferReport.Enabled)
        {
            try
            {
                var blocks = await _soTransferOutstandingReport.LoadReportAsync(cancellationToken).ConfigureAwait(false);
                var stamp = scheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var xlsx = _soTransferOutstandingExport.BuildExcel(blocks, scheduleDate);
                var pdf = _soTransferOutstandingExport.BuildPdf(blocks, scheduleDate);
                batchAttachments.Add(new EmailAttachment(
                    $"SO-transfer-outstanding_{stamp}.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    xlsx));
                batchAttachments.Add(new EmailAttachment(
                    $"SO-transfer-outstanding_{stamp}.pdf",
                    "application/pdf",
                    pdf));
                _logger.LogInformation(
                    "SO transfer outstanding report: built Excel + PDF ({BlockCount} SO line block(s)).",
                    blocks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SO transfer outstanding report failed; continuing without those attachments.");
            }
        }

        var batchAttachmentsFinal = batchAttachments.Count > 0 ? (IReadOnlyList<EmailAttachment>?)batchAttachments : null;

        if (_soTransferReport.Enabled)
        {
            if (!EmailSender.HasSoTransferOutstandingAttachmentPair(batchAttachmentsFinal))
            {
                _logger.LogInformation(
                    "Skipping recipient emails: SO transfer outstanding is enabled but the Excel/PDF pair was not produced (report disabled, failed, or empty). Not sending a generic SQL notification.");
                return;
            }
        }
        else if (batchAttachmentsFinal is null || batchAttachmentsFinal.Count == 0)
        {
            _logger.LogInformation("Skipping recipient emails: no attachments were built and SO transfer report is disabled.");
            return;
        }

        var sentCount = 0;

        foreach (var r in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(r.Code))
            {
                _logger.LogWarning("Skipping user with empty CODE, email {Email}.", r.Email);
                continue;
            }

            try
            {
                await _emailSender.SendScheduledNotificationAsync(r, scheduleDate, batchAttachmentsFinal, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Email sent successfully to {Email} (user {Code}, {Name}).",
                    r.Email, r.Code, r.Name);
                await _emailLog.RecordSuccessAsync(r.Code, scheduleDate, sendTimeSlot, r.Email, cancellationToken)
                    .ConfigureAwait(false);
                sentCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Email failed for {Email} (user {Code}, {Name}).",
                    r.Email, r.Code, r.Name);
                await _emailLog.RecordFailureAsync(r.Code, scheduleDate, sendTimeSlot, r.Email, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "Email batch completed. Sent this run: {Sent}. (Send history is logged per attempt; sends are not limited by history.)",
            sentCount);
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
