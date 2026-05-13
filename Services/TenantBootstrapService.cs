using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>
/// Fetches and caches tenant JSON from the ProAcc tenant-config API (same as eQuotation).
/// Resolves Firebird connection strings and optional <c>email</c> / <c>smtpAppPasswordSecretRef</c> (Secrets Manager).
/// </summary>
public sealed class TenantBootstrapService
{
    public const string HttpClientName = "TenantBootstrapConfig";
    public const string TenantQueryParameter = "tenantCode";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AppSettings> _appOptions;
    private readonly AwsSecretsReader _secretsReader;
    private readonly ILogger<TenantBootstrapService> _logger;

    private readonly ConcurrentDictionary<string, string> _rawJsonByTenant = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _firebirdConnectionByTenant = new(StringComparer.OrdinalIgnoreCase);

    public TenantBootstrapService(
        IConfiguration configuration,
        IHttpClientFactory httpFactory,
        IOptions<AppSettings> appOptions,
        AwsSecretsReader secretsReader,
        ILogger<TenantBootstrapService> logger)
    {
        _configuration = configuration;
        _httpFactory = httpFactory;
        _appOptions = appOptions;
        _secretsReader = secretsReader;
        _logger = logger;
    }

    public static bool IsTenantMode(IConfiguration configuration)
    {
        var tenantCode = (configuration["TENANT_CODE"] ?? configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        var apiUrl = (configuration["TenantBootstrap:AwsApiBaseUrl"] ?? "").Trim();
        return !string.IsNullOrWhiteSpace(tenantCode) && !string.IsNullOrWhiteSpace(apiUrl);
    }

    public async Task<string> GetConnectionStringForTenantAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            throw new InvalidOperationException("Tenant code is required.");

        if (_firebirdConnectionByTenant.TryGetValue(tenantCode, out var cached))
            return cached;

        var raw = await GetCachedRawJsonAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        using var parsed = TenantJsonEnvelope.Parse(raw);
        var root = parsed.Root;

        if (!TryFindSectionRecursive(root, "database", out var databaseAttr))
        {
            var healthHint = LooksLikeApiHealthEnvelope(raw, root)
                ? "This URL returned a health/status JSON instead of a tenant record. "
                : "";
            throw new InvalidOperationException(
                healthHint +
                $"Tenant JSON missing 'database' (after unwrapping). Raw: {Truncate(raw, 900)}");
        }

        var db = UnwrapDynamoMap(databaseAttr);
        var csb = await TryBuildDatabaseConnectionAsync(db, _appOptions.Value.Firebird, cancellationToken).ConfigureAwait(false);
        if (csb is null)
            throw new InvalidOperationException("Tenant JSON database section is missing dbHost/dbPath or could not be parsed.");

        var conn = csb.ConnectionString;
        _firebirdConnectionByTenant[tenantCode] = conn;
        return conn;
    }

    /// <summary>
    /// eQuotation-style <c>email</c> section: smtpHost, smtpPort, smtpSenderEmail, smtpAppPasswordSecretRef (Secrets Manager).
    /// Non-null fields override <see cref="SmtpOptions"/>; use null to keep appsettings value.
    /// </summary>
    public async Task<TenantSmtpOverrides?> GetTenantSmtpOverridesAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        var raw = await GetCachedRawJsonAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        using var parsed = TenantJsonEnvelope.Parse(raw);
        var root = parsed.Root;

        if (!TryFindSectionRecursive(root, "email", out var emailEl) || emailEl.ValueKind != JsonValueKind.Object)
            return null;

        var em = UnwrapDynamoMap(emailEl);

        string? smtpHost = null;
        int? smtpPort = null;
        string? fromEmail = null;
        string? smtpUser = null;
        string? smtpPassword = null;

        if (TryGetScalar(em, "smtpHost", out var host) && !string.IsNullOrWhiteSpace(host))
            smtpHost = host.Trim();

        if (TryGetScalar(em, "smtpPort", out var portStr) &&
            int.TryParse(portStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
            port > 0)
            smtpPort = port;

        if (TryGetScalar(em, "smtpSenderEmail", out var sender) && !string.IsNullOrWhiteSpace(sender))
        {
            var s = sender.Trim();
            fromEmail = s;
            smtpUser = s;
        }

        if (TryGetScalar(em, "smtpAppPasswordSecretRef", out var pwdRef) && !string.IsNullOrWhiteSpace(pwdRef))
        {
            pwdRef = pwdRef.Trim();
            _logger.LogInformation("Loading SMTP app password from Secrets Manager ({SecretRef}) per tenant email.smtpAppPasswordSecretRef.", pwdRef);
            var rawSecret = await _secretsReader.GetSecretStringAsync(pwdRef, cancellationToken).ConfigureAwait(false);
            var pw = AwsSecretsReader.CoercePasswordFromSecretString(rawSecret);
            if (!string.IsNullOrWhiteSpace(pw))
                smtpPassword = pw;
        }

        if (smtpHost is null && smtpPort is null && smtpUser is null && smtpPassword is null && fromEmail is null)
            return null;

        return new TenantSmtpOverrides
        {
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpUser = smtpUser,
            SmtpPassword = smtpPassword,
            FromEmail = fromEmail,
        };
    }

    private async Task<string> GetCachedRawJsonAsync(string tenantCode, CancellationToken cancellationToken)
    {
        if (_rawJsonByTenant.TryGetValue(tenantCode, out var existing))
            return existing;

        var gate = _fetchLocks.GetOrAdd(tenantCode, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rawJsonByTenant.TryGetValue(tenantCode, out existing))
                return existing;

            var baseUrl = (_configuration["TenantBootstrap:AwsApiBaseUrl"] ?? "").Trim();
            var apiKey = (_configuration["TenantBootstrap:AwsApiKey"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("TenantBootstrap:AwsApiBaseUrl is required when using tenant resolution.");

            var requestUri = AppendTenantQuery(baseUrl, TenantQueryParameter, tenantCode);
            var http = _httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);

            using var res = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var raw = (await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (raw.Length > 0 && raw[0] == '\uFEFF')
                raw = raw[1..];

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Tenant API failed ({(int)res.StatusCode}): {Truncate(raw, 2000)}");

            _rawJsonByTenant[tenantCode] = raw;
            return raw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FbConnectionStringBuilder?> TryBuildDatabaseConnectionAsync(
        JsonElement db,
        FirebirdOptions fbFallback,
        CancellationToken cancellationToken)
    {
        var csb = new FbConnectionStringBuilder { Pooling = true };

        if (!TryGetScalar(db, "dbPath", out var dbPath) || !TryGetScalar(db, "dbHost", out var dbHost))
            return null;

        dbPath = dbPath.Trim();
        dbHost = dbHost.Trim();
        if (string.IsNullOrWhiteSpace(dbPath) || string.IsNullOrWhiteSpace(dbHost))
            return null;

        string dbCharset;
        int dbPort = 0;
        int dbDialect = 0;

        var hasFull =
            TryGetScalar(db, "dbCharset", out var chRaw)
            && TryGetScalar(db, "dbPort", out var portText)
            && int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dbPort)
            && TryGetScalar(db, "dbDialect", out var dialectText)
            && int.TryParse(dialectText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dbDialect);

        if (hasFull)
        {
            dbCharset = chRaw.Trim();
            if (string.IsNullOrWhiteSpace(dbCharset) || dbPort <= 0 || dbDialect <= 0)
                return null;
        }
        else
        {
            dbCharset = string.IsNullOrWhiteSpace(fbFallback.Charset) ? "UTF8" : fbFallback.Charset.Trim();
            dbPort = fbFallback.DbPort > 0 ? fbFallback.DbPort : 2052;
            dbDialect = fbFallback.Dialect > 0 ? fbFallback.Dialect : 3;
        }

        var dbUser = TryGetScalar(db, "dbUser", out var u) ? u.Trim() : "";
        var dbPassword = TryGetScalar(db, "dbPassword", out var p) ? p : "";
        if (string.IsNullOrWhiteSpace(dbPassword))
        {
            var secretRef = TryGetFirstDbPasswordSecretRef(db);
            if (!string.IsNullOrWhiteSpace(secretRef))
            {
                var raw = await _secretsReader.GetSecretStringAsync(secretRef, cancellationToken).ConfigureAwait(false);
                dbPassword = AwsSecretsReader.CoercePasswordFromSecretString(raw) ?? "";
            }
        }

        if (string.IsNullOrWhiteSpace(dbUser))
            dbUser = (fbFallback.DbUser ?? "").Trim();
        if (string.IsNullOrWhiteSpace(dbPassword))
            dbPassword = fbFallback.DbPassword ?? "";

        if (string.IsNullOrWhiteSpace(dbUser) || string.IsNullOrWhiteSpace(dbPassword))
            return null;

        csb.UserID = dbUser;
        csb.Password = dbPassword;
        csb.DataSource = dbHost;
        csb.Port = dbPort;
        csb.Database = dbPath.Replace('\\', '/');
        csb.Dialect = dbDialect;
        csb.Charset = dbCharset;
        return csb;
    }

    private static string? TryGetFirstDbPasswordSecretRef(JsonElement db)
    {
        foreach (var key in new[] { "dbPasswordSecretRef", "databasePasswordSecretRef", "dbPasswordSecretArn" })
        {
            if (TryGetScalar(db, key, out var v))
            {
                var t = v.Trim();
                if (!string.IsNullOrEmpty(t))
                    return t;
            }
        }

        return null;
    }

    private static string AppendTenantQuery(string baseUrl, string paramName, string tenantCode)
    {
        var sep = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{sep}{Uri.EscapeDataString(paramName)}={Uri.EscapeDataString(tenantCode)}";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static bool LooksLikeApiHealthEnvelope(string raw, JsonElement root)
    {
        if (raw.Contains("\"database\"", StringComparison.OrdinalIgnoreCase))
            return false;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        var hasStatus = TryGetJsonPropertyIgnoreCase(root, "status", out _);
        var hasService = TryGetJsonPropertyIgnoreCase(root, "service", out _);
        return hasStatus && hasService;
    }

    private static bool TryFindSectionRecursive(JsonElement e, string sectionName, out JsonElement sectionAttr)
    {
        sectionAttr = default;
        if (e.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetJsonPropertyIgnoreCase(e, sectionName, out sectionAttr))
            return true;

        foreach (var p in e.EnumerateObject())
        {
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (TryFindSectionRecursive(p.Value, sectionName, out sectionAttr))
                        return true;
                    break;
                case JsonValueKind.String:
                    var s = p.Value.GetString();
                    if (string.IsNullOrWhiteSpace(s))
                        break;
                    var t = s.Trim();
                    if (!t.StartsWith('{'))
                        break;
                    try
                    {
                        using var nested = JsonDocument.Parse(t);
                        if (TryFindSectionRecursive(nested.RootElement, sectionName, out sectionAttr))
                            return true;
                    }
                    catch
                    {
                        /* ignore */
                    }

                    break;
            }
        }

        return false;
    }

    private static JsonElement UnwrapDynamoMap(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("M", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
            return inner;

        return el;
    }

    private static bool TryGetJsonPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetScalar(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!TryGetJsonPropertyIgnoreCase(parent, name, out var el))
            return false;
        return TryCoerceDynamoScalar(el, out value);
    }

    private static bool TryCoerceDynamoScalar(JsonElement el, out string value)
    {
        value = "";
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                value = el.GetString() ?? "";
                return true;
            case JsonValueKind.Number:
                value = el.GetRawText();
                return true;
            case JsonValueKind.True:
                value = "true";
                return true;
            case JsonValueKind.False:
                value = "false";
                return true;
            case JsonValueKind.Object:
                if (el.TryGetProperty("S", out var s))
                {
                    value = s.ValueKind == JsonValueKind.String ? (s.GetString() ?? "") : s.GetRawText();
                    return true;
                }

                if (el.TryGetProperty("N", out var n))
                {
                    value = n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "") : n.GetRawText();
                    return true;
                }

                if (el.TryGetProperty("BOOL", out var b))
                {
                    value = b.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => b.GetBoolean() ? "true" : "false",
                    };
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}

/// <summary>Non-null fields override appsettings SMTP for this send.</summary>
public sealed class TenantSmtpOverrides
{
    public string? SmtpHost { get; init; }
    public int? SmtpPort { get; init; }
    public string? SmtpUser { get; init; }
    public string? SmtpPassword { get; init; }
    public string? FromEmail { get; init; }
}

/// <summary>Owns all <see cref="JsonDocument"/> instances created while unwrapping API Gateway / Dynamo envelopes.</summary>
internal sealed class TenantJsonEnvelope : IDisposable
{
    private readonly List<JsonDocument> _documents = new();

    private TenantJsonEnvelope(JsonElement root) => Root = root;

    private TenantJsonEnvelope(JsonElement root, List<JsonDocument> documents)
    {
        Root = root;
        _documents.AddRange(documents);
    }

    public JsonElement Root { get; }

    public void Dispose()
    {
        foreach (var d in _documents)
            d.Dispose();
        _documents.Clear();
    }

    private void Track(JsonDocument doc)
    {
        _documents.Add(doc);
    }

    public static TenantJsonEnvelope Parse(string raw)
    {
        var primary = JsonDocument.Parse(raw);
        var root = primary.RootElement;
        var lease = new TenantJsonEnvelope(root);
        lease._documents.Add(primary);

        root = ResolveTenantPayloadIntoLease(root, lease);
        return new TenantJsonEnvelope(root, lease._documents);
    }

    private static JsonElement ResolveTenantPayloadIntoLease(JsonElement root, TenantJsonEnvelope lease)
    {
        if (TryGetJsonPropertyIgnoreCase(root, "body", out var bodyEl))
        {
            if (bodyEl.ValueKind == JsonValueKind.String)
            {
                var s = bodyEl.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    var text = MaybeDecodeApiGatewayBody(root, s.Trim());
                    var inner = JsonDocument.Parse(text);
                    lease.Track(inner);
                    root = inner.RootElement;
                }
            }
            else if (bodyEl.ValueKind == JsonValueKind.Object)
            {
                root = bodyEl;
            }
        }

        if (TryGetJsonPropertyIgnoreCase(root, "Item", out var item) && item.ValueKind == JsonValueKind.Object)
            root = item;

        if (TryGetJsonPropertyIgnoreCase(root, "data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
        {
            var s = dataEl.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                var inner = JsonDocument.Parse(s.Trim());
                lease.Track(inner);
                root = inner.RootElement;
            }
        }

        if (TryGetJsonPropertyIgnoreCase(root, "data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object)
            root = dataObj;

        return root;
    }

    private static bool TryGetJsonPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }

    private static string MaybeDecodeApiGatewayBody(JsonElement envelopeRoot, string bodyText)
    {
        if (!TryGetJsonPropertyIgnoreCase(envelopeRoot, "isBase64Encoded", out var b64))
            return bodyText;
        if (b64.ValueKind != JsonValueKind.True)
            return bodyText;
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bodyText));
        }
        catch
        {
            return bodyText;
        }
    }
}
