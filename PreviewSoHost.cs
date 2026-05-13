using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SqlAccountingEmailWorker.Services;

namespace SqlAccountingEmailWorker;

/// <summary>Minimal HTTP listener for <c>--preview-so</c> (default port 2086; override with <c>PREVIEW_SO_PORT</c>).</summary>
/// <remarks>
/// By default only <c>127.0.0.1</c> and <c>localhost</c> prefixes are registered — using the EC2 public DNS or IP in the
/// browser sends a different Host header and http.sys returns <c>400 Bad Request - Invalid Hostname</c>.
/// Set <c>PREVIEW_SO_ANY_HOST=1</c> to add <c>http://+:port/</c> (all interfaces, any Host). On Windows this usually requires
/// a one-time URL reservation, e.g. <c>netsh http add urlacl url=http://+:2086/ user=Everyone</c> (run elevated), or run the process as Administrator.
/// </remarks>
public static class PreviewSoHost
{
    public static void Run(string contentRoot, IServiceProvider? services = null)
    {
        var formatsRoot = Path.Combine(contentRoot, "EmailFormats");
        if (!Directory.Exists(formatsRoot))
        {
            Console.Error.WriteLine($"EmailFormats folder not found: {formatsRoot}");
            Environment.ExitCode = 1;
            return;
        }

        var formatsFull = Path.GetFullPath(formatsRoot);
        var port = 2086;
        var envPort = Environment.GetEnvironmentVariable("PREVIEW_SO_PORT");
        if (int.TryParse(envPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 65536)
            port = parsed;

        var anyHost = IsTruthyEnv("PREVIEW_SO_ANY_HOST");
        var wildcardPrefix = $"http://+:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        if (anyHost)
            listener.Prefixes.Add(wildcardPrefix);

        var listenAllHosts = false;
        try
        {
            listener.Start();
            listenAllHosts = anyHost;
        }
        catch (HttpListenerException ex)
        {
            if (!anyHost)
            {
                Console.Error.WriteLine(
                    $"Could not listen on port {port}: {ex.Message}\n" +
                    "Set PREVIEW_SO_PORT=2086 (or another free port) and try again.");
                Environment.ExitCode = 1;
                return;
            }

            Console.Error.WriteLine(
                $"Could not bind {wildcardPrefix}: {ex.Message}\n" +
                "Windows requires a URL ACL for http://+ (any hostname / EC2 URL). Run once in elevated CMD, then retry:\n" +
                $"  netsh http add urlacl url={wildcardPrefix} user=Everyone\n" +
                $"Or remove PREVIEW_SO_ANY_HOST and browse only via http://127.0.0.1:{port}/ from this machine.");
            listener.Prefixes.Remove(wildcardPrefix);
            try
            {
                listener.Start();
                Console.Error.WriteLine(
                    $"Fell back to loopback only: http://127.0.0.1:{port}/ (EC2/public hostname in the URL will still fail).");
            }
            catch (HttpListenerException ex2)
            {
                Console.Error.WriteLine(
                    $"Could not listen on port {port}: {ex2.Message}\n" +
                    "Set PREVIEW_SO_PORT=2086 (or another free port) and try again.");
                Environment.ExitCode = 1;
                return;
            }
        }

        Console.WriteLine(
            $"previewSO: serving {formatsFull} at http://127.0.0.1:{port}/ (default: so-transfer-preview.html). Ctrl+C to stop.");
        if (listenAllHosts)
            Console.WriteLine(
                $"PREVIEW_SO_ANY_HOST: listening on all interfaces — use http://<this-host-ip-or-dns>:{port}/ from the network.");
        if (services is not null)
            Console.WriteLine("SO transfer report: http://127.0.0.1:{0}/so-transfer", port);
        Console.WriteLine("Default preview port is 2086. Override with PREVIEW_SO_PORT if needed.");
        if (!anyHost)
            Console.WriteLine(
                "Tip: HTTP 400 Invalid Hostname when using EC2 URL? Set PREVIEW_SO_ANY_HOST=1 and add netsh urlacl (see PreviewSoHost remarks).");

        while (true)
        {
            var ctx = listener.GetContext();
            try
            {
                HandleRequest(ctx, formatsFull, services);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static void HandleRequest(HttpListenerContext ctx, string formatsRoot, IServiceProvider? services)
    {
        var rawPath = ctx.Request.Url?.AbsolutePath ?? "/";
        if (string.Equals(rawPath, "/healthz", StringComparison.OrdinalIgnoreCase))
        {
            WriteText(ctx, 200, "ok", "text/plain; charset=utf-8");
            return;
        }

        if (string.Equals(rawPath, "/so-transfer", StringComparison.OrdinalIgnoreCase))
        {
            ServeStaticFile(ctx, formatsRoot, "so-transfer-preview.html");
            return;
        }

        if (string.Equals(rawPath, "/so-transfer.xlsx", StringComparison.OrdinalIgnoreCase))
        {
            if (services is null)
            {
                WriteText(ctx, 503, "Report export requires full preview host (services not configured).", "text/plain; charset=utf-8");
                return;
            }

            try
            {
                var report = services.GetRequiredService<SoTransferOutstandingReportService>();
                var export = services.GetRequiredService<SoTransferOutstandingExportBuilder>();
                var blocks = report.LoadReportAsync(CancellationToken.None).GetAwaiter().GetResult();
                var bytes = export.BuildExcel(blocks);
                WriteBytes(ctx, 200, bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "so-transfer-outstanding.xlsx");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                WriteText(ctx, 500, "Report failed: " + ex.Message, "text/plain; charset=utf-8");
            }

            return;
        }

        if (string.Equals(rawPath, "/so-transfer.pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (services is null)
            {
                WriteText(ctx, 503, "Report export requires full preview host (services not configured).", "text/plain; charset=utf-8");
                return;
            }

            try
            {
                var report = services.GetRequiredService<SoTransferOutstandingReportService>();
                var export = services.GetRequiredService<SoTransferOutstandingExportBuilder>();
                var blocks = report.LoadReportAsync(CancellationToken.None).GetAwaiter().GetResult();
                var bytes = export.BuildPdf(blocks);
                WriteBytes(ctx, 200, bytes, "application/pdf", "so-transfer-outstanding.pdf");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                WriteText(ctx, 500, "Report failed: " + ex.Message, "text/plain; charset=utf-8");
            }

            return;
        }

        var rel = rawPath.TrimStart('/').Replace('\\', '/');
        if (string.IsNullOrEmpty(rel) || rel == "/")
            rel = "so-transfer-preview.html";

        if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            WriteText(ctx, 400, "Bad path", "text/plain; charset=utf-8");
            return;
        }

        ServeStaticFile(ctx, formatsRoot, rel);
    }

    private static void ServeStaticFile(HttpListenerContext ctx, string formatsRoot, string relativeName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(formatsRoot, relativeName));
        if (!fullPath.StartsWith(formatsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            WriteText(ctx, 404, "Not found", "text/plain; charset=utf-8");
            return;
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        using var fs = File.OpenRead(fullPath);
        fs.CopyTo(ctx.Response.OutputStream);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteText(HttpListenerContext ctx, int status, string body, string contentType)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteBytes(HttpListenerContext ctx, int status, byte[] body, string contentType, string downloadName)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{downloadName}\"");
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.OutputStream.Close();
    }

    private static bool IsTruthyEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
