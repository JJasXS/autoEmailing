using System.Globalization;
using DotNetEnv;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker;
using SqlAccountingEmailWorker.Models;
using SqlAccountingEmailWorker.Services;

// Use executable directory so appsettings.json and local logs work when running as a Windows Service.
var contentRoot = AppContext.BaseDirectory;

// Same idea as eQuotation: optional .env (e.g. TENANT_CODE=TNT10005). Later files override earlier.
foreach (var envPath in new[]
         {
             Path.Combine(contentRoot, ".env"),
             Path.Combine(Directory.GetCurrentDirectory(), ".env")
         })
{
    if (File.Exists(envPath))
        Env.Load(envPath);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = contentRoot,
    Args = args
});

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection(AppSettings.SectionName));
builder.Services.AddWindowsService(options => options.ServiceName = "SQL Accounting Email Worker");

builder.Services.AddSingleton<AwsSecretsReader>();
builder.Services.AddHttpClient(TenantFirebirdConnectionResolver.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<TenantFirebirdConnectionResolver>();
builder.Services.AddSingleton<TenantBootstrapService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<FirebirdUserReader>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<EmailLogService>();
builder.Services.AddSingleton<DbInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DbInitializer>());
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var testSmtpIdx = Array.FindIndex(args, static a => string.Equals(a, "--test-smtp", StringComparison.OrdinalIgnoreCase));
if (testSmtpIdx >= 0)
{
    if (testSmtpIdx + 1 >= args.Length || string.IsNullOrWhiteSpace(args[testSmtpIdx + 1]))
    {
        Console.Error.WriteLine("Usage: dotnet run -- --test-smtp your-email@example.com");
        Environment.ExitCode = 1;
        return;
    }

    var testTo = args[testSmtpIdx + 1].Trim();
    using var host = builder.Build();
    var emailSender = host.Services.GetRequiredService<EmailSender>();
    try
    {
        await emailSender.SendTestMessageAsync(testTo, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"SMTP OK — test message sent to {testTo}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("SMTP test failed:");
        Console.Error.WriteLine(ex);
        Environment.ExitCode = 1;
    }

    return;
}

// Immediate SY_USER + UDF_AEMAIL test using ScheduledTestEmail subject/body (no schedule wait, no send history).
if (args.Contains("--sy-user-test-email", StringComparer.OrdinalIgnoreCase))
{
    using var host = builder.Build();
    var te = host.Services.GetRequiredService<IOptions<AppSettings>>().Value.ScheduledTestEmail;
    if (string.IsNullOrWhiteSpace(te.PlainBody))
    {
        Console.Error.WriteLine(
            "Set App:ScheduledTestEmail:PlainBody (e.g. App__ScheduledTestEmail__PlainBody in .env). Subject comes from App:ScheduledTestEmail:Subject.");
        Environment.ExitCode = 1;
        return;
    }

    await host.Services.GetRequiredService<DbInitializer>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    var emailSender = host.Services.GetRequiredService<EmailSender>();
    var firebird = host.Services.GetRequiredService<FirebirdUserReader>();
    var subject = string.IsNullOrWhiteSpace(te.Subject) ? "Auto emailing test" : te.Subject.Trim();

    if (!string.IsNullOrWhiteSpace(te.To))
    {
        Console.WriteLine($"ScheduledTestEmail:To is set — sending only to {te.To} (SY_USER list not used).");
        await emailSender.SendPlainTextAsync(te.To, subject, te.PlainBody, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("Done.");
        return;
    }

    var recipients = await firebird.GetRecipientsAsync(CancellationToken.None).ConfigureAwait(false);
    if (recipients.Count == 0)
    {
        Console.Error.WriteLine("No recipients: SY_USER needs non-empty EMAIL and truthy UDF_AEMAIL.");
        Environment.ExitCode = 1;
        return;
    }

    var sent = 0;
    foreach (var r in recipients)
    {
        if (string.IsNullOrWhiteSpace(r.Code))
            continue;
        await emailSender.SendPlainTextAsync(r.Email, subject, te.PlainBody, CancellationToken.None).ConfigureAwait(false);
        sent++;
    }

    Console.WriteLine($"SY_USER test: sent to {sent} recipient(s) (EMAIL + UDF_AEMAIL).");
    return;
}

if (args.Contains("--wait-scheduled-test", StringComparer.OrdinalIgnoreCase))
{
    using var host = builder.Build();
    var schedule = host.Services.GetRequiredService<ScheduleService>();
    var emailSender = host.Services.GetRequiredService<EmailSender>();
    var te = host.Services.GetRequiredService<IOptions<AppSettings>>().Value.ScheduledTestEmail;
    if (string.IsNullOrWhiteSpace(te.PlainBody))
    {
        Console.Error.WriteLine("Set App:ScheduledTestEmail:PlainBody (e.g. App__ScheduledTestEmail__PlainBody in .env).");
        Environment.ExitCode = 1;
        return;
    }

    var next = schedule.GetNextSendUtc(DateTimeOffset.UtcNow);
    var delay = next - DateTimeOffset.UtcNow;
    if (delay < TimeSpan.Zero)
        delay = TimeSpan.Zero;

    var tz = schedule.GetTimeZone();
    var nowUtc = DateTimeOffset.UtcNow.UtcDateTime;
    var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
    var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(next.UtcDateTime, tz);
    const string lf = "yyyy-MM-dd HH:mm:ss";
    var sendTimeOfDay = schedule.GetSendTimeOfDay().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    Console.WriteLine(
        $"Now local: {nowLocal.ToString(lf, CultureInfo.InvariantCulture)} ({tz.Id}) | " +
        $"Next send local: {nextLocal.ToString(lf, CultureInfo.InvariantCulture)} | " +
        $"Next UTC: {next.UtcDateTime:o} | Configured time-of-day: {sendTimeOfDay} | Waiting {delay} …");
    try
    {
        await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
    }
    catch (TaskCanceledException)
    {
        return;
    }

    var subject = string.IsNullOrWhiteSpace(te.Subject) ? "Auto emailing test" : te.Subject.Trim();

    try
    {
        if (!string.IsNullOrWhiteSpace(te.To))
        {
            await emailSender.SendPlainTextAsync(te.To, subject, te.PlainBody, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Scheduled test email sent to {te.To}.");
        }
        else
        {
            await host.Services.GetRequiredService<DbInitializer>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var firebird = host.Services.GetRequiredService<FirebirdUserReader>();
            var recipients = await firebird.GetRecipientsAsync(CancellationToken.None).ConfigureAwait(false);
            if (recipients.Count == 0)
            {
                Console.Error.WriteLine("No recipients (SY_USER with non-empty EMAIL and UDF_AEMAIL enabled).");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var r in recipients)
            {
                if (string.IsNullOrWhiteSpace(r.Code))
                    continue;
                await emailSender.SendPlainTextAsync(r.Email, subject, te.PlainBody, CancellationToken.None).ConfigureAwait(false);
            }

            Console.WriteLine($"Scheduled test email sent to {recipients.Count} recipient(s) from SY_USER (UDF_AEMAIL).");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Scheduled test email failed:");
        Console.Error.WriteLine(ex);
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Contains("--send-now", StringComparer.OrdinalIgnoreCase))
{
    using var host = builder.Build();
    await host.Services.GetRequiredService<DbInitializer>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    var worker = host.Services.GetRequiredService<Worker>();
    await worker.RunOnceNowAsync(CancellationToken.None).ConfigureAwait(false);
    return;
}

var runHost = builder.Build();
runHost.Run();
