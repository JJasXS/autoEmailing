using System.Text.Json;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class EmailLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly LocalLogOptions _options;
    private readonly ILogger<EmailLogService> _logger;
    private readonly string _fullPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EmailLogService(IOptions<AppSettings> appOptions, ILogger<EmailLogService> logger)
    {
        _options = appOptions.Value.LocalLog;
        _logger = logger;
        var path = _options.HistoryFilePath;
        _fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    public string HistoryFilePath => _fullPath;

    /// <summary>Creates the directory for <see cref="HistoryFilePath"/> if needed (idempotent).</summary>
    public Task EnsureStorageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(_fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            _logger.LogDebug("Send history directory ensured: {Directory}", dir);
        }

        return Task.CompletedTask;
    }

    public async Task RecordSuccessAsync(
        string userCode,
        DateOnly scheduleDate,
        string sendTimeSlot,
        string toEmail,
        CancellationToken cancellationToken)
    {
        await AppendEntryAsync(new SendHistoryEntry
        {
            UserCode = userCode.Trim(),
            ScheduleDate = scheduleDate.ToString("yyyy-MM-dd", null),
            SendTimeSlot = (sendTimeSlot ?? "").Trim(),
            ToEmail = toEmail,
            Success = true,
            Error = null,
            RecordedUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Send history recorded (success): user {UserCode}, date {ScheduleDate}, sendTime {SendTime}, to {ToEmail}",
            userCode,
            scheduleDate.ToString("yyyy-MM-dd", null),
            (sendTimeSlot ?? "").Trim(),
            toEmail);
    }

    public async Task RecordFailureAsync(
        string userCode,
        DateOnly scheduleDate,
        string sendTimeSlot,
        string toEmail,
        string error,
        CancellationToken cancellationToken)
    {
        await AppendEntryAsync(new SendHistoryEntry
        {
            UserCode = userCode.Trim(),
            ScheduleDate = scheduleDate.ToString("yyyy-MM-dd", null),
            SendTimeSlot = (sendTimeSlot ?? "").Trim(),
            ToEmail = toEmail,
            Success = false,
            Error = error,
            RecordedUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Send history recorded (failure): user {UserCode}, date {ScheduleDate}, sendTime {SendTime}, to {ToEmail}, error {Error}",
            userCode,
            scheduleDate.ToString("yyyy-MM-dd", null),
            (sendTimeSlot ?? "").Trim(),
            toEmail,
            error);
    }

    private async Task AppendEntryAsync(SendHistoryEntry entry, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
            store.Entries.Add(entry);
            await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SendHistoryStore> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_fullPath))
            return new SendHistoryStore();

        await using var stream = File.OpenRead(_fullPath);
        try
        {
            var store = await JsonSerializer.DeserializeAsync<SendHistoryStore>(stream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            return store ?? new SendHistoryStore();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Corrupt send history JSON at {Path}; starting fresh in memory (file not overwritten until next save).", _fullPath);
            return new SendHistoryStore();
        }
    }

    private async Task SaveAsync(SendHistoryStore store, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_fullPath);
        await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private sealed class SendHistoryStore
    {
        public List<SendHistoryEntry> Entries { get; set; } = new();
    }

    private sealed class SendHistoryEntry
    {
        public string UserCode { get; set; } = "";
        public string ScheduleDate { get; set; } = "";
        /// <summary>Optional <c>HH:mm</c> from schedule for audit only (not used to block sends).</summary>
        public string? SendTimeSlot { get; set; }
        public string ToEmail { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset RecordedUtc { get; set; }
    }
}
