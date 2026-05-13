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

    /// <summary>Next instant when the daily job should run, in UTC.</summary>
    public DateTimeOffset GetNextSendUtc(DateTimeOffset utcNow)
    {
        var tz = GetTimeZone();
        var sendTime = GetSendTimeOfDay();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, tz);
        var todaySend = localNow.Date.Add(sendTime.ToTimeSpan());
        var localNext = localNow <= todaySend ? todaySend : todaySend.AddDays(1);
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
}
