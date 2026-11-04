using System.Globalization;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Infrastructure.Calendar;

public static class IcalCalendarParser
{
    private static readonly Regex FoldedLinePattern = new(
        @"\r?\n[ \t]",
        RegexOptions.Compiled);

    public static IReadOnlyList<ParsedIcalEvent> ParseEventsForWindow(
        string icsBody,
        DateOnly today,
        DateOnly tomorrow,
        TimeZoneInfo localTimeZone)
    {
        if (string.IsNullOrWhiteSpace(icsBody)) return [];

        var unfolded = FoldedLinePattern.Replace(icsBody, string.Empty);
        var events = new List<ParsedIcalEvent>();

        foreach (var veventBlock in SplitComponents(unfolded, "VEVENT"))
        {
            var properties = ParseProperties(veventBlock);
            if (!properties.TryGetValue("SUMMARY", out var summaryRaw))
                summaryRaw = "Calendar event";
            var summary = UnescapeText(summaryRaw).Trim();
            if (string.IsNullOrWhiteSpace(summary)) summary = "Calendar event";

            if (!properties.TryGetValue("DTSTART", out var dtStartRaw))
                continue;

            properties.TryGetValue("DTEND", out var dtEndRaw);
            properties.TryGetValue("RRULE", out var rruleRaw);
            properties.TryGetValue("STATUS", out var status);
            if (!string.IsNullOrWhiteSpace(status) &&
                status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
                continue;

            if (WeeklyIcalRecurrence.IsWeekly(rruleRaw))
            {
                foreach (var occurrence in WeeklyIcalRecurrence.Expand(dtStartRaw,rruleRaw!,today,tomorrow,localTimeZone))
                    events.Add(new ParsedIcalEvent(summary,DateOnly.FromDateTime(occurrence.StartLocal),occurrence.IsAllDay,
                        occurrence.IsAllDay ? "all day" : FormatTimeLabel(occurrence.StartLocal),occurrence.StartLocal));
                continue;
            }

            if (!TryParseDateTimeProperty(dtStartRaw, localTimeZone, out var startLocal, out var isAllDay))
                continue;

            DateTime? endLocal = null;
            if (!string.IsNullOrWhiteSpace(dtEndRaw) &&
                TryParseDateTimeProperty(dtEndRaw, localTimeZone, out var parsedEnd, out _))
                endLocal = parsedEnd;

            foreach (var occurrenceStart in ExpandOccurrences(startLocal, endLocal, isAllDay, rruleRaw, today, tomorrow))
            {
                var date = DateOnly.FromDateTime(occurrenceStart);
                if (date != today && date != tomorrow) continue;

                events.Add(new ParsedIcalEvent(
                    summary,
                    date,
                    isAllDay,
                    isAllDay ? "all day" : FormatTimeLabel(occurrenceStart),
                    occurrenceStart));
            }
        }

        return events
            .OrderBy(static item => item.StartLocal)
            .ThenBy(static item => item.Summary, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<DateTime> ExpandOccurrences(
        DateTime startLocal,
        DateTime? endLocal,
        bool isAllDay,
        string? rruleRaw,
        DateOnly today,
        DateOnly tomorrow)
    {
        var windowStart = today.ToDateTime(TimeOnly.MinValue);
        var windowEnd = tomorrow.ToDateTime(new TimeOnly(23, 59, 59));

        if (string.IsNullOrWhiteSpace(rruleRaw))
        {
            if (isAllDay)
            {
                var startDate = DateOnly.FromDateTime(startLocal);
                var endDate = endLocal is null
                    ? startDate.AddDays(1)
                    : DateOnly.FromDateTime(endLocal.Value);
                for (var day = startDate; day < endDate; day = day.AddDays(1))
                    if (day == today || day == tomorrow)
                        yield return day.ToDateTime(TimeOnly.MinValue);
                yield break;
            }

            if (startLocal >= windowStart && startLocal <= windowEnd)
                yield return startLocal;
            yield break;
        }

        if (!TryParseRrule(rruleRaw, out var freq, out var interval, out var until, out var count, out var byDays))
            yield break;

        var emitted = 0;
        var cursor = startLocal;
        var hardStop = windowEnd.AddDays(1);
        var maxIterations = 366;
        for (var i = 0; i < maxIterations; i++)
        {
            if (until is not null && cursor > until.Value) break;
            if (count is not null && emitted >= count.Value) break;
            if (cursor > hardStop) break;

            var include = byDays.Count == 0 || byDays.Contains(cursor.DayOfWeek);
            if (include && cursor >= windowStart && cursor <= windowEnd)
            {
                yield return cursor;
                emitted++;
            }
            else if (include)
            {
                emitted++;
            }

            cursor = freq switch
            {
                "DAILY" => cursor.AddDays(Math.Max(1, interval)),
                "WEEKLY" => cursor.AddDays(7 * Math.Max(1, interval)),
                "MONTHLY" => cursor.AddMonths(Math.Max(1, interval)),
                _ => cursor.AddDays(Math.Max(1, interval))
            };
        }
    }

    private static bool TryParseRrule(
        string rruleRaw,
        out string freq,
        out int interval,
        out DateTime? until,
        out int? count,
        out HashSet<DayOfWeek> byDays)
    {
        freq = "DAILY";
        interval = 1;
        until = null;
        count = null;
        byDays = [];

        foreach (var part in rruleRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim().ToUpperInvariant();
            var value = part[(separator + 1)..].Trim();
            switch (key)
            {
                case "FREQ":
                    freq = value.ToUpperInvariant();
                    break;
                case "INTERVAL" when int.TryParse(value, out var parsedInterval):
                    interval = Math.Max(1, parsedInterval);
                    break;
                case "COUNT" when int.TryParse(value, out var parsedCount):
                    count = Math.Max(1, parsedCount);
                    break;
                case "UNTIL":
                    if (TryParseBasicDateTime(value, TimeZoneInfo.Utc, out var untilUtc, out _))
                        until = TimeZoneInfo.ConvertTimeFromUtc(
                            DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc),
                            TimeZoneInfo.Local);
                    break;
                case "BYDAY":
                    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var dayToken = Regex.Replace(token, @"^[+-]?\d+", string.Empty).ToUpperInvariant();
                        if (TryMapByDay(dayToken, out var dayOfWeek))
                            byDays.Add(dayOfWeek);
                    }

                    break;
            }
        }

        return freq is "DAILY" or "WEEKLY" or "MONTHLY";
    }

    private static bool TryMapByDay(string token, out DayOfWeek dayOfWeek)
    {
        dayOfWeek = token switch
        {
            "SU" => DayOfWeek.Sunday,
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            _ => (DayOfWeek)(-1)
        };
        return (int)dayOfWeek >= 0;
    }

    private static bool TryParseDateTimeProperty(
        string raw,
        TimeZoneInfo localTimeZone,
        out DateTime local,
        out bool isAllDay)
    {
        local = default;
        isAllDay = false;
        var value = raw;
        string? tzId = null;

        var colon = raw.LastIndexOf(':');
        if (colon >= 0)
        {
            var meta = raw[..colon];
            value = raw[(colon + 1)..].Trim();
            foreach (var part in meta.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
                    tzId = part["TZID=".Length..].Trim().Trim('"');
                if (part.Equals("VALUE=DATE", StringComparison.OrdinalIgnoreCase))
                    isAllDay = true;
            }
        }

        return TryParseBasicDateTime(value, localTimeZone, out local, out isAllDay, tzId, isAllDay);
    }

    private static bool TryParseBasicDateTime(
        string value,
        TimeZoneInfo localTimeZone,
        out DateTime local,
        out bool isAllDay,
        string? tzId = null,
        bool forceAllDay = false)
    {
        local = default;
        isAllDay = forceAllDay || value.Length == 8;
        value = value.Trim();

        if (value.Length == 8 &&
            DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            local = dateOnly.Date;
            isAllDay = true;
            return true;
        }

        var isUtc = value.EndsWith('Z');
        var normalized = isUtc ? value.TrimEnd('Z') : value;
        string[] formats = ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"];
        if (!DateTime.TryParseExact(
                normalized,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            return false;

        if (isUtc)
        {
            local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Utc), localTimeZone);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(tzId))
            try
            {
                var sourceZone = ResolveTimeZone(tzId);
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), sourceZone);
                local = TimeZoneInfo.ConvertTimeFromUtc(utc, localTimeZone);
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }

        local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    private static TimeZoneInfo ResolveTimeZone(string tzId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Common Google Calendar IANA ids on Windows may need mapping; fall back to local.
            return TimeZoneInfo.Local;
        }
    }

    private static IEnumerable<string> SplitComponents(string body, string componentName)
    {
        var begin = $"BEGIN:{componentName}";
        var end = $"END:{componentName}";
        var index = 0;
        while (true)
        {
            var start = body.IndexOf(begin, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0) yield break;
            start += begin.Length;
            var stop = body.IndexOf(end, start, StringComparison.OrdinalIgnoreCase);
            if (stop < 0) yield break;
            yield return body[start..stop];
            index = stop + end.Length;
        }
    }

    private static Dictionary<string, string> ParseProperties(string componentBody)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(componentBody);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var namePart = line[..separator];
            var value = line[(separator + 1)..];
            var name = namePart.Split(';', 2)[0].Trim().ToUpperInvariant();
            // Keep parameter metadata for DTSTART/DTEND by storing the full "params:value" form.
            properties[name] = name is "DTSTART" or "DTEND"
                ? line
                : value;
        }

        return properties;
    }

    private static string UnescapeText(string value)
    {
        return value
            .Replace("\\n", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string FormatTimeLabel(DateTime localStart)
    {
        var hour = localStart.Hour;
        var minute = localStart.Minute;
        var period = hour >= 12 ? "p.m." : "a.m.";
        var hour12 = hour % 12;
        if (hour12 == 0) hour12 = 12;

        return minute == 0
            ? $"at {hour12} {period}"
            : $"at {hour12}:{minute:00} {period}";
    }
}

public sealed record ParsedIcalEvent(
    string Summary,
    DateOnly Date,
    bool IsAllDay,
    string TimeLabel,
    DateTime StartLocal);
