namespace SqlAccountingEmailWorker.Models;

/// <summary>One line of the outstanding-style report (matches SQL column aliases).</summary>
public sealed record DailyReportRow(string Document, DateTime? DocDate, int AgeDays, decimal Amount);
