using Microsoft.Extensions.Hosting.WindowsServices;
using SqlAccountingEmailWorker.Services;

namespace SqlAccountingEmailWorker;

/// <summary>Shared DI registration for the worker and <c>--preview-so</c> tooling.</summary>
public static class WorkerHostConfigurator
{
    /// <summary>SMTP, Firebird, report builders, etc. Does not start scheduled worker loops.</summary>
    public static void AddCoreServices(HostApplicationBuilder builder)
    {
        builder.Services.Configure<Models.AppSettings>(builder.Configuration.GetSection(Models.AppSettings.SectionName));
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
        builder.Services.AddSingleton<DailyReportExcelPdfGenerator>();
        builder.Services.AddSingleton<SoTransferOutstandingReportService>();
        builder.Services.AddSingleton<SoTransferOutstandingExportBuilder>();
        builder.Services.AddSingleton<EmailSender>();
        builder.Services.AddSingleton<EmailLogService>();
        builder.Services.AddSingleton<DbInitializer>();
    }

    /// <summary>Hosted background services (normal worker process only).</summary>
    public static void AddWorkerHostedServices(HostApplicationBuilder builder)
    {
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DbInitializer>());
        builder.Services.AddSingleton<Worker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
    }
}
