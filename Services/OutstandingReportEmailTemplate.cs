namespace SqlAccountingEmailWorker.Services;

/// <summary>
/// Loads <c>EmailFormats/outstanding-report.template.html</c> from the app base directory and replaces <c>{{Name}}</c> tokens.
/// </summary>
public static class OutstandingReportEmailTemplate
{
    public const string RelativePath = "EmailFormats/outstanding-report.template.html";

    public static string GetTemplatePath() =>
        Path.Combine(AppContext.BaseDirectory, RelativePath);

    public static async Task<string> LoadRawAsync(CancellationToken cancellationToken = default)
    {
        var path = GetTemplatePath();
        if (!File.Exists(path))
            throw new FileNotFoundException($"Outstanding report template not found: {path}", path);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces <c>{{Key}}</c> with values. By default values are HTML-encoded except <c>OutstandingRows</c> (expects pre-built <c>&lt;tr&gt;…&lt;/tr&gt;</c> from your report query — ensure that HTML is trusted).</summary>
    public static string Apply(string template, IReadOnlyDictionary<string, string> values, IReadOnlySet<string>? allowUnencodedKeys = null)
    {
        allowUnencodedKeys ??= DefaultRawHtmlKeys;
        var result = template;
        foreach (var kv in values)
        {
            var token = "{{" + kv.Key + "}}";
            var value = kv.Value ?? "";
            if (!allowUnencodedKeys.Contains(kv.Key))
                value = System.Net.WebUtility.HtmlEncode(value);
            result = result.Replace(token, value, StringComparison.Ordinal);
        }

        return result;
    }

    private static readonly HashSet<string> DefaultRawHtmlKeys = new(StringComparer.OrdinalIgnoreCase) { "OutstandingRows" };
}
