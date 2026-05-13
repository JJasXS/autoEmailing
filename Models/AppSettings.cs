namespace SqlAccountingEmailWorker.Models;

public sealed class AppSettings
{
    public const string SectionName = "App";

    public FirebirdOptions Firebird { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
    public ScheduleOptions Schedule { get; set; } = new();
    public LocalLogOptions LocalLog { get; set; } = new();

    /// <summary>Optional one-off / scheduled-tick plain-text test (see Worker and <c>--wait-scheduled-test</c>).</summary>
    public ScheduledTestEmailOptions ScheduledTestEmail { get; set; } = new();
}

public sealed class FirebirdOptions
{
    public string DbHost { get; set; } = "localhost";
    public int DbPort { get; set; } = 2052;
    public string DbPath { get; set; } = "";
    public string DbUser { get; set; } = "";
    public string DbPassword { get; set; } = "";
    public string Charset { get; set; } = "UTF8";

    /// <summary>Firebird SQL dialect (default 3). Used when tenant JSON omits <c>dbDialect</c>.</summary>
    public int Dialect { get; set; } = 3;
}

public sealed class SmtpOptions
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";

    /// <summary>If set and <see cref="SmtpPassword"/> is empty, password is loaded from AWS Secrets Manager.</summary>
    public string SmtpPasswordSecretRef { get; set; } = "";

    /// <summary>Envelope From address. If empty, <see cref="SmtpUser"/> is used for the From header.</summary>
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "SQL Accounting";
    public bool EnableSsl { get; set; } = true;

    /// <summary>Socket I/O timeout in ms (MailKit). Lower for faster failure when host/port is wrong or blocked. Default 30s.</summary>
    public int SmtpTimeoutMs { get; set; } = 30_000;
}

public sealed class ScheduleOptions
{
    /// <summary>Local time-of-day in <see cref="TimeZone"/> (e.g. 08:00).</summary>
    public string SendTime { get; set; } = "08:00";

    /// <summary>IANA time zone id (e.g. Asia/Kuala_Lumpur).</summary>
    public string TimeZone { get; set; } = "Asia/Kuala_Lumpur";
}

public sealed class LocalLogOptions
{
    /// <summary>Path to JSON send history. Relative paths are under the app base directory.</summary>
    public string HistoryFilePath { get; set; } = "Data/email-send-history.json";
}

public sealed class ScheduledTestEmailOptions
{
    /// <summary>When true, the scheduled worker tick sends <see cref="PlainBody"/> (see <see cref="To"/>).</summary>
    public bool Enabled { get; set; }

    /// <summary>If true, skip the normal Firebird recipient batch on that tick after the test sends.</summary>
    public bool SkipDailyBatch { get; set; } = true;

    /// <summary>
    /// If set, send the test only to this address. If empty, sends to every <c>SY_USER</c> row returned by
    /// <see cref="FirebirdUserReader.GetRecipientsAsync"/> (non-empty <c>EMAIL</c> and truthy <c>UDF_AEMAIL</c>).
    /// </summary>
    public string To { get; set; } = "";

    public string Subject { get; set; } = "Auto emailing test";
    public string PlainBody { get; set; } = "";
}
