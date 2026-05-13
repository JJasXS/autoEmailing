using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class ScheduleService
{
    private readonly ScheduleOptions _options;

    public ScheduleService(IOptions<AppSettings> appOptions)
    {
        _options = appOptions.Value.Schedule;
    }

    public TimeZoneInfo GetTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InvalidOperationException(
                $"Schedule:TimeZone '{_options.TimeZone}' was not found. Use an IANA id on Linux/macOS or the Windows registry id on Windows.");
        }
    }

    public TimeOnly GetSendTimeOfDay()
    {
        if (!TimeOnly.TryParse(_options.SendTime, out var t))
            throw new InvalidOperationException($"Schedule:SendTime '{_options.SendTime}' is invalid. Use HH:mm or H:mm.");
        return t;
    }

    public bool IsWeeklySchedule()
    {
        var f = (_options.SendFrequency ?? "").Trim();
        return f.Equals("Weekly", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Next instant when the job should run, in UTC (daily or weekly per <see cref="ScheduleOptions"/>).</summary>
    public DateTimeOffset GetNextSendUtc(DateTimeOffset utcNow)
    {
        var tz = GetTimeZone();
        var sendTime = GetSendTimeOfDay();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, tz);

        DateTime localNext;
        if (IsWeeklySchedule())
        {
            var dow = ParseSendDayOfWeek();
            localNext = GetNextWeeklySendLocal(localNow, sendTime, dow);
        }
        else
        {
            var todaySend = localNow.Date.Add(sendTime.ToTimeSpan());
            localNext = localNow <= todaySend ? todaySend : todaySend.AddDays(1);
        }

        var utcNext = TimeZoneInfo.ConvertTimeToUtc(localNext, tz);
        return new DateTimeOffset(utcNext, TimeSpan.Zero);
    }

    /// <summary>Calendar date in the configured schedule time zone (for duplicate checks).</summary>
    public DateOnly GetScheduleDateLocal(DateTimeOffset utcNow)
    {
        var tz = GetTimeZone();
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, tz);
        return DateOnly.FromDateTime(local.Date);
    }

    private static DateTime GetNextWeeklySendLocal(DateTime localNow, TimeOnly sendTime, DayOfWeek targetDow)
    {
        var timeSpan = sendTime.ToTimeSpan();
        for (var i = 0; i < 14; i++)
        {
            var day = localNow.Date.AddDays(i);
            var localInstant = day + timeSpan;
            if (day.DayOfWeek == targetDow && localInstant > localNow)
                return localInstant;
        }

        throw new InvalidOperationException("Could not resolve next weekly send time (internal).");
    }

    private DayOfWeek ParseSendDayOfWeek()
    {
        var s = (_options.SendDayOfWeek ?? "").Trim();
        if (s.Length == 0)
            return DayOfWeek.Monday;

        if (Enum.TryParse(s, true, out DayOfWeek dow) && Enum.IsDefined(dow))
            return dow;

        throw new InvalidOperationException(
            $"Schedule:SendDayOfWeek '{_options.SendDayOfWeek}' is invalid. Use an English day name (e.g. Monday).");
    }
}
