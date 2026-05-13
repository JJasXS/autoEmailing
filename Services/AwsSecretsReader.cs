using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace SqlAccountingEmailWorker.Services;

/// <summary>
/// Reads secrets from AWS Secrets Manager using the default credential chain
/// (environment keys, shared credentials file, IAM role on EC2/ECS/Lambda, SSO profile when configured, etc.).
/// Region is resolved from <c>AWS:Region</c>, <c>AWS_REGION</c>, or <c>TENANT_BOOTSTRAP_SECRETS_REGION</c> (same names as eQuotation).
/// </summary>
public sealed class AwsSecretsReader
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AwsSecretsReader> _logger;

    public AwsSecretsReader(IConfiguration configuration, ILogger<AwsSecretsReader> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string? ResolveRegion()
    {
        var r = (_configuration["AWS:Region"] ?? _configuration["AWS_REGION"] ?? _configuration["TENANT_BOOTSTRAP_SECRETS_REGION"] ?? "").Trim();
        return string.IsNullOrEmpty(r) ? null : r;
    }

    public async Task<string?> GetSecretStringAsync(string secretId, CancellationToken cancellationToken = default)
    {
        secretId = (secretId ?? "").Trim();
        if (string.IsNullOrEmpty(secretId))
            return null;

        var region = ResolveRegion();
        if (string.IsNullOrEmpty(region))
        {
            _logger.LogError(
                "Cannot read Secrets Manager secret {SecretId}: no region. Set AWS:Region in appsettings.json, or environment AWS_REGION / TENANT_BOOTSTRAP_SECRETS_REGION.",
                secretId);
            throw new InvalidOperationException(
                "AWS region is required to read Secrets Manager. Set AWS:Region, AWS_REGION, or TENANT_BOOTSTRAP_SECRETS_REGION.");
        }

        try
        {
            var endpoint = RegionEndpoint.GetBySystemName(region);
            using var client = new AmazonSecretsManagerClient(endpoint);
            var resp = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, cancellationToken)
                .ConfigureAwait(false);
            return resp.SecretString;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Secrets Manager GetSecretValue failed for {SecretId} in region {Region}.", secretId, region);
            throw;
        }
    }

    /// <summary>Plain text secret, or JSON with password/dbPassword/smtpPassword/smtpAppPassword.</summary>
    public static string? CoercePasswordFromSecretString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var t = raw.Trim();
        if (!t.StartsWith('{'))
            return t;

        try
        {
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            foreach (var name in new[] { "password", "dbPassword", "smtpPassword", "smtpAppPassword", "SecretString", "value" })
            {
                if (TryGetPropertyIgnoreCase(root, name, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s.Trim();
                }
            }
        }
        catch
        {
            return t;
        }

        return t;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
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
}
