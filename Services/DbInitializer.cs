namespace SqlAccountingEmailWorker.Services;

/// <summary>
/// Startup checks: local send-history folder (no ERP writes), optional read-only Firebird probe.
/// SQL Accounting remains SELECT-only; this does not create or migrate ERP schema.
/// </summary>
public sealed class DbInitializer : IHostedService
{
    private readonly EmailLogService _emailLog;
    private readonly FirebirdUserReader _firebird;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(
        EmailLogService emailLog,
        FirebirdUserReader firebird,
        ILogger<DbInitializer> logger)
    {
        _emailLog = emailLog;
        _firebird = firebird;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _emailLog.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Send-history path ready: {Path}", _emailLog.HistoryFilePath);

        var ok = await _firebird.TryProbeConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (ok)
            _logger.LogInformation("Firebird read-only connection probe succeeded.");
        else
            _logger.LogWarning(
                "Firebird probe failed; fix tenant API / App:Firebird before sends. The worker will still run and surface errors when a batch runs.");
    }
}
