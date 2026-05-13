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
/// Resolves Firebird connection strings from the same ProAcc tenant-config API as eQuotation / ApprovalPO
/// (<c>?tenantCode=</c>). Dynamo <c>database</c> supplies host/path; optional <c>dbUser</c>/<c>dbPassword</c> override
/// <see cref="FirebirdOptions"/> when present. When <c>dbPassword</c> is empty, optional
/// <c>dbPasswordSecretRef</c> / <c>databasePasswordSecretRef</c> / <c>dbPasswordSecretArn</c> is loaded from AWS Secrets Manager
/// (requires <c>AWS:Region</c> or <c>AWS_REGION</c> / <c>TENANT_BOOTSTRAP_SECRETS_REGION</c> and default AWS credentials).
/// </summary>
public sealed class TenantFirebirdConnectionResolver
{
    public const string HttpClientName = "TenantBootstrapConfig";
    public const string TenantQueryParameter = "tenantCode";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AppSettings> _appOptions;
    private readonly AwsSecretsReader _secretsReader;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TenantFirebirdConnectionResolver(
        IConfiguration configuration,
        IHttpClientFactory httpFactory,
        IOptions<AppSettings> appOptions,
        AwsSecretsReader secretsReader)
    {
        _configuration = configuration;
        _httpFactory = httpFactory;
        _appOptions = appOptions;
        _secretsReader = secretsReader;
    }

    public async Task<string> GetConnectionStringForTenantAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            throw new InvalidOperationException("Tenant code is required.");

        if (_cache.TryGetValue(tenantCode, out var cached))
            return cached;

        var baseUrl = (_configuration["TenantBootstrap:AwsApiBaseUrl"] ?? "").Trim();
        var apiKey = (_configuration["TenantBootstrap:AwsApiKey"] ?? "").Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("TenantBootstrap:AwsApiBaseUrl is required when using tenant resolution.");

        var fbFallback = _appOptions.Value.Firebird;
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

        using var json = JsonDocument.Parse(raw);
        JsonDocument? innerDoc = null;
        try
        {
            var root = ResolveTenantPayload(json.RootElement, ref innerDoc);
            if (!TryFindDatabaseAttribute(root, out var databaseAttr))
            {
                var healthHint = LooksLikeApiHealthEnvelope(raw, root)
                    ? "This URL returned a health/status JSON instead of a tenant record. Point TenantBootstrap:AwsApiBaseUrl at the invoke URL that returns the full tenant payload (including database / dbHost / dbPath). "
                    : "";
                throw new InvalidOperationException(
                    healthHint +
                    $"Tenant JSON missing 'database' (after unwrapping). Request used ?{TenantQueryParameter}=. Raw: {Truncate(raw, 900)}");
            }

            var db = UnwrapDynamoMap(databaseAttr);
            var csb = await TryBuildDatabaseConnectionAsync(db, fbFallback, cancellationToken).ConfigureAwait(false);
            if (csb is null)
                throw new InvalidOperationException("Tenant JSON database section is missing dbHost/dbPath or could not be parsed.");

            var conn = csb.ConnectionString;
            _cache[tenantCode] = conn;
            return conn;
        }
        finally
        {
            innerDoc?.Dispose();
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
            var secretRef = TryGetFirstSecretRef(db);
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

    private static string? TryGetFirstSecretRef(JsonElement db)
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

    private static JsonElement ResolveTenantPayload(JsonElement root, ref JsonDocument? innerDoc)
    {
        if (TryGetJsonPropertyIgnoreCase(root, "body", out var bodyEl))
        {
            if (bodyEl.ValueKind == JsonValueKind.String)
            {
                var s = bodyEl.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    var text = MaybeDecodeApiGatewayBody(root, s.Trim());
                    innerDoc = JsonDocument.Parse(text);
                    root = innerDoc.RootElement;
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
                innerDoc?.Dispose();
                innerDoc = JsonDocument.Parse(s.Trim());
                root = innerDoc.RootElement;
            }
        }

        if (TryGetJsonPropertyIgnoreCase(root, "data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object)
            root = dataObj;

        return root;
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

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static bool TryFindDatabaseAttribute(JsonElement root, out JsonElement databaseAttr) =>
        TryFindDatabaseRecursive(root, depth: 0, maxDepth: 8, out databaseAttr);

    private static bool TryFindDatabaseRecursive(JsonElement e, int depth, int maxDepth, out JsonElement databaseAttr)
    {
        databaseAttr = default;
        if (depth > maxDepth || e.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetJsonPropertyIgnoreCase(e, "database", out databaseAttr))
            return true;

        foreach (var p in e.EnumerateObject())
        {
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (TryFindDatabaseRecursive(p.Value, depth + 1, maxDepth, out databaseAttr))
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
                        if (TryFindDatabaseRecursive(nested.RootElement, depth + 1, maxDepth, out databaseAttr))
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

    private static JsonElement UnwrapDynamoMap(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("M", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
            return inner;

        return el;
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
