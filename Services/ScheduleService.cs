using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class ScheduleService
{
    /// <summary>Time-of-day for sends; set via environment <c>App__Schedule__SendTime=HH:mm</c> (or same key in JSON under <c>App</c>).</summary>
    public const string SendTimeConfigurationKey = "App:Schedule:SendTime";

    private readonly ScheduleOptions _options;
    private readonly IConfiguration _configuration;

    public ScheduleService(IOptions<AppSettings> appOptions, IConfiguration configuration)
    {
        _options = appOptions.Value.Schedule;
        _configuration = configuration;
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
        var raw = (_configuration[SendTimeConfigurationKey] ?? "").Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException(
                $"Set {SendTimeConfigurationKey} (e.g. in .env: App__Schedule__SendTime=10:18). No default is applied.");

        // Common mistake: 11.03 instead of 11:03 (dot as separator).
        if (raw.Contains('.', StringComparison.Ordinal) && !raw.Contains(':', StringComparison.Ordinal))
            raw = raw.Replace('.', ':');

        if (!TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            throw new InvalidOperationException(
                $"{SendTimeConfigurationKey} '{raw}' is invalid. Use HH:mm (colon), e.g. 11:03 — not 11.03.");
        return t;
    }

    public bool IsWeeklySchedule()
    {
        var f = (_options.SendFrequency ?? "").Trim();
        return f.Equals("Weekly", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Next UTC instant to run: weekly uses <see cref="ScheduleOptions"/>; otherwise the next <see cref="GetSendTimeOfDay"/>
    /// on the local clock in <see cref="GetTimeZone"/> that is still on or after <paramref name="utcNow"/> (computed from “right now” in that zone).
    /// </summary>
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
            localNext = GetNextWallClockSendOnOrAfterLocal(localNow, sendTime);

        var utcNext = TimeZoneInfo.ConvertTimeToUtc(localNext, tz);
        return new DateTimeOffset(utcNext, TimeSpan.Zero);
    }

    /// <summary>Earliest local instant at <paramref name="sendTime"/> that is still on or after <paramref name="localNow"/> (same calendar day, or next day).</summary>
    private static DateTime GetNextWallClockSendOnOrAfterLocal(DateTime localNow, TimeOnly sendTime)
    {
        var slotToday = localNow.Date.Add(sendTime.ToTimeSpan());
        return localNow <= slotToday ? slotToday : slotToday.AddDays(1);
    }

    /// <summary>Calendar date in the configured schedule time zone (batch stamp / history).</summary>
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
