using System.Globalization;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker;
using SqlAccountingEmailWorker.Models;
using SqlAccountingEmailWorker.Services;

// QuestPDF: community license (see https://www.questpdf.com/pricing.html).
QuestPDF.Settings.License = LicenseType.Community;

// Use executable directory so appsettings.json and local logs work when running as a Windows Service.
var contentRoot = AppContext.BaseDirectory;

// Optional .env in cwd, then next to the executable — last file wins so bin\...\ .env always overrides a stray .env in another working directory.
foreach (var envPath in new[]
         {
             Path.Combine(Directory.GetCurrentDirectory(), ".env"),
             Path.Combine(contentRoot, ".env")
         })
{
    if (File.Exists(envPath))
        Env.Load(envPath);
}

var previewByArg = args.Contains("--preview-so", StringComparer.OrdinalIgnoreCase);

if (previewByArg)
{
    var previewBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        ContentRootPath = contentRoot,
        Args = args
    });
    WorkerHostConfigurator.AddCoreServices(previewBuilder);
    previewBuilder.Logging.ClearProviders();
    previewBuilder.Logging.AddConsole();
    using var previewHost = previewBuilder.Build();
    await previewHost.Services.GetRequiredService<DbInitializer>().InitializeAsync(CancellationToken.None)
        .ConfigureAwait(false);
    PreviewSoHost.Run(contentRoot, previewHost.Services);
    return;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = contentRoot,
    Args = args
});

WorkerHostConfigurator.AddCoreServices(builder);
WorkerHostConfigurator.AddWorkerHostedServices(builder);

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
