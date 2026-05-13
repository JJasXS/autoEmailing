using System.Data.Common;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class FirebirdUserReader
{
    private readonly FirebirdOptions _fb;
    private readonly IConfiguration _configuration;
    private readonly TenantFirebirdConnectionResolver _tenantResolver;
    private readonly ILogger<FirebirdUserReader> _logger;

    public FirebirdUserReader(
        IOptions<AppSettings> appOptions,
        IConfiguration configuration,
        TenantFirebirdConnectionResolver tenantResolver,
        ILogger<FirebirdUserReader> logger)
    {
        _fb = appOptions.Value.Firebird;
        _configuration = configuration;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <summary>
    /// When <c>TENANT_CODE</c> (or <c>TenantBootstrap:TenantCode</c>) and <c>TenantBootstrap:AwsApiBaseUrl</c> are set,
    /// uses the same ProAcc tenant-config API as eQuotation. Otherwise uses <c>App:Firebird</c> only (manual mode).
    /// </summary>
    public async Task<string> ResolveConnectionStringAsync(CancellationToken cancellationToken)
    {
        var tenantCode = (_configuration["TENANT_CODE"] ?? _configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        var apiUrl = (_configuration["TenantBootstrap:AwsApiBaseUrl"] ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(tenantCode) && !string.IsNullOrWhiteSpace(apiUrl))
        {
            _logger.LogInformation("Resolving Firebird via tenant API (tenant {TenantCode}).", tenantCode);
            return await _tenantResolver.GetConnectionStringForTenantAsync(tenantCode, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Using manual App:Firebird settings (no tenant API).");
        return BuildManualConnectionString();
    }

    /// <summary>Opens a read-only connection and runs <c>SELECT 1 FROM RDB$DATABASE</c> (no ERP table writes).</summary>
    public async Task<bool> TryProbeConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cs = await ResolveConnectionStringAsync(cancellationToken).ConfigureAwait(false);
            await using var conn = new FbConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new FbCommand("SELECT 1 FROM RDB$DATABASE", conn);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird startup probe failed.");
            return false;
        }
    }

    private string BuildManualConnectionString()
    {
        if (string.IsNullOrWhiteSpace(_fb.DbPath))
            throw new InvalidOperationException("App:Firebird:DbPath is required when TENANT_CODE / tenant API is not used.");

        var csb = new FbConnectionStringBuilder
        {
            DataSource = _fb.DbHost,
            Port = _fb.DbPort,
            Database = _fb.DbPath.Trim(),
            UserID = _fb.DbUser,
            Password = _fb.DbPassword,
            Charset = string.IsNullOrWhiteSpace(_fb.Charset) ? "UTF8" : _fb.Charset,
            Dialect = _fb.Dialect > 0 ? _fb.Dialect : 3,
            Pooling = true
        };
        return csb.ConnectionString;
    }

    /// <summary>
    /// Read-only: loads users with email; UDF_AEMAIL is evaluated in .NET so BOOLEAN and text values both work.
    /// </summary>
    public async Task<IReadOnlyList<EmailRecipient>> GetRecipientsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CODE, NAME, EMAIL, UDF_AEMAIL
            FROM SY_USER
            WHERE COALESCE(EMAIL, '') <> ''
            """;

        var cs = await ResolveConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = new FbConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<EmailRecipient>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var code = reader["CODE"]?.ToString()?.Trim() ?? "";
            var name = reader["NAME"]?.ToString()?.Trim() ?? "";
            var email = reader["EMAIL"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(email))
                continue;

            var udf = reader["UDF_AEMAIL"];
            if (!IsAutoEmailEnabled(udf))
                continue;

            list.Add(new EmailRecipient { Code = code, Name = name, Email = email });
        }

        return list;
    }

    /// <summary>
    /// Read-only: runs configured report SQL. Columns <c>DOCUMENT</c>, <c>DOC_DATE</c>, <c>AGE_DAYS</c>, <c>AMOUNT</c> are required (any case).
    /// </summary>
    public async Task<IReadOnlyList<DailyReportRow>> GetDailyReportRowsAsync(string sql, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Array.Empty<DailyReportRow>();

        var cs = await ResolveConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = new FbConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new FbCommand(sql.Trim(), conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        static int Ordinal(DbDataReader r, string name)
        {
            for (var i = 0; i < r.FieldCount; i++)
            {
                if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            throw new InvalidOperationException(
                "Report SQL must return columns DOCUMENT, DOC_DATE, AGE_DAYS, and AMOUNT (see EmailFormats/previewSO.html).");
        }

        var list = new List<DailyReportRow>();
        var ordDoc = -1;
        var ordDate = -1;
        var ordAge = -1;
        var ordAmt = -1;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ordDoc < 0)
            {
                ordDoc = Ordinal(reader, "DOCUMENT");
                ordDate = Ordinal(reader, "DOC_DATE");
                ordAge = Ordinal(reader, "AGE_DAYS");
                ordAmt = Ordinal(reader, "AMOUNT");
            }

            var doc = reader.GetValue(ordDoc)?.ToString()?.Trim() ?? "";
            DateTime? docDate = null;
            if (!reader.IsDBNull(ordDate))
            {
                var v = reader.GetValue(ordDate);
                docDate = v switch
                {
                    DateTime dt => dt,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    _ => DateTime.TryParse(v?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed
                        : null
                };
            }

            var ageDays = reader.IsDBNull(ordAge)
                ? 0
                : Convert.ToInt32(reader.GetValue(ordAge), CultureInfo.InvariantCulture);
            var amount = reader.IsDBNull(ordAmt)
                ? 0m
                : Convert.ToDecimal(reader.GetValue(ordAmt), CultureInfo.InvariantCulture);

            list.Add(new DailyReportRow(doc, docDate, ageDays, amount));
        }

        return list;
    }

    private static bool IsAutoEmailEnabled(object? raw)
    {
        if (raw is null or DBNull)
            return false;

        switch (raw)
        {
            case bool b:
                return b;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToInt64(raw, null) != 0;
            case string s:
                return IsTruthyString(s);
            default:
                return IsTruthyString(raw.ToString() ?? "");
        }
    }

    private static bool IsTruthyString(string s)
    {
        var t = s.Trim();
        if (t.Length == 0)
            return false;
        return t.ToUpperInvariant() switch
        {
            "T" or "Y" or "YES" or "TRUE" or "1" => true,
            _ => false
        };
    }
}
