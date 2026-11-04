using System.Globalization;

namespace Jibo.Cloud.Infrastructure.Calendar;

/// <summary>A bounded weekly RRULE subset; expands in the event's own time zone.</summary>
internal static class WeeklyIcalRecurrence
{
    internal sealed record Occurrence(DateTime StartLocal, bool IsAllDay);
    private static readonly string[] TimeFormats = ["yyyyMMdd'T'HHmmss","yyyyMMdd'T'HHmm"];
    private static readonly HashSet<string> SupportedParts = new(StringComparer.OrdinalIgnoreCase)
        { "FREQ","INTERVAL","COUNT","UNTIL","BYDAY","WKST" };

    internal static bool IsWeekly(string? rule) => rule is not null &&
        rule.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Any(part =>
        {
            var split = part.Split('=',2,StringSplitOptions.TrimEntries);
            return split.Length == 2 && split[0].Equals("FREQ",StringComparison.OrdinalIgnoreCase) &&
                   split[1].Equals("WEEKLY",StringComparison.OrdinalIgnoreCase);
        });

    internal static IReadOnlyList<Occurrence> Expand(string startRaw, string rule, DateOnly today,
        DateOnly tomorrow, TimeZoneInfo displayZone)
    {
        var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rule.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=',2,StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !SupportedParts.Contains(pair[0]) || !fields.TryAdd(pair[0],pair[1])) return [];
        }
        if (fields.Count > 6 || !fields.TryGetValue("FREQ",out var frequency) ||
            !frequency.Equals("WEEKLY",StringComparison.OrdinalIgnoreCase) ||
            !TryStart(startRaw,displayZone,out var start,out var allDay,out var sourceZone,out var floating)) return [];
        var interval = 1;
        if (fields.TryGetValue("INTERVAL",out var intervalRaw) && !Positive(intervalRaw,out interval)) return [];
        int? count = null;
        if (fields.TryGetValue("COUNT",out var countRaw))
        {
            if (!Positive(countRaw,out var number)) return [];
            count = number;
        }
        var weekStart = DayOfWeek.Monday;
        if (fields.TryGetValue("WKST",out var weekRaw) && !TryDay(weekRaw,out weekStart)) return [];
        var days = new HashSet<DayOfWeek>();
        if (fields.TryGetValue("BYDAY",out var daysRaw))
        {
            foreach (var token in daysRaw.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            {
                if (!TryDay(token,out var day)) return [];
                days.Add(day);
            }
            if (days.Count == 0) return [];
        }
        else days.Add(start.DayOfWeek);
        // RFC 5545 leaves a DTSTART that does not match its rule undefined.
        if (!days.Contains(start.DayOfWeek)) return [];
        DateTime? until = null;
        var untilUtc = false;
        if (fields.TryGetValue("UNTIL",out var untilRaw))
        {
            if (count is not null) return [];
            untilUtc = untilRaw.EndsWith('Z');
            var value = untilUtc ? untilRaw[..^1] : untilRaw;
            if (allDay)
            {
                if (untilUtc || !DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)) return [];
                until = date;
            }
            else
            {
                if (untilUtc == floating || !DateTime.TryParseExact(value,TimeFormats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)) return [];
                until = untilUtc ? DateTime.SpecifyKind(time,DateTimeKind.Utc) : time;
            }
        }
        var startDate = DateOnly.FromDateTime(start);
        var startOffset = Offset(start.DayOfWeek,weekStart);
        var origin = startDate.DayNumber-startOffset;
        var offsets = days.Select(day => Offset(day,weekStart)).Order().ToArray();
        var firstWeekCount = offsets.Count(offset => offset >= startOffset);
        var candidateDays = new HashSet<int>();
        foreach (var target in new[] { today,tomorrow })
            for (var delta = -2; delta <= 2; delta++)
            {
                var day = target.DayNumber+delta;
                if (day >= 0 && day <= DateOnly.MaxValue.DayNumber) candidateDays.Add(day);
            }
        var latest = DateOnly.FromDayNumber(candidateDays.Max());
        if (latest < startDate) return [];
        var invalidDays = count is not null && !allDay
            ? InvalidCountDays(sourceZone,start,latest,days,origin,interval) : [];
        if (invalidDays is null) return [];
        var result = new List<Occurrence>();
        foreach (var dayNumber in candidateDays.Order())
        {
            var date = DateOnly.FromDayNumber(dayNumber);
            var wall = date.ToDateTime(TimeOnly.FromDateTime(start));
            if (wall < start || !Matches(date,startDate,days,origin,interval)) continue;
            if (!allDay && sourceZone.IsInvalidTime(wall)) continue;
            var week = (long)(dayNumber-origin)/7/interval;
            var position = Offset(date.DayOfWeek,weekStart);
            var ordinal = week == 0 ? offsets.Count(offset => offset >= startOffset && offset <= position)
                : firstWeekCount+(week-1)*offsets.Length+offsets.Count(offset => offset <= position);
            ordinal -= invalidDays.Count(day => day < dayNumber);
            if (count is not null && ordinal > count.Value) continue;
            var utc = allDay ? wall : ToUtc(wall,sourceZone);
            if (until is not null && (untilUtc ? utc : wall) > until.Value) continue;
            var local = allDay ? wall : TimeZoneInfo.ConvertTimeFromUtc(utc,displayZone);
            var localDate = DateOnly.FromDateTime(local);
            if (localDate == today || localDate == tomorrow) result.Add(new Occurrence(local,allDay));
        }
        return result.OrderBy(item => item.StartLocal).ToArray();
    }

    private static bool TryStart(string raw, TimeZoneInfo display, out DateTime start, out bool allDay,
        out TimeZoneInfo zone, out bool floating)
    {
        start = default; allDay = false; zone = display; floating = true;
        var separator = raw.LastIndexOf(':');
        if (separator < 0) return false;
        var value = raw[(separator+1)..].Trim();
        var parameters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw[..separator].Split(';').Skip(1))
        {
            var pair = part.Split('=',2,StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !parameters.TryAdd(pair[0],pair[1].Trim('"'))) return false;
        }
        parameters.TryGetValue("TZID",out var id);
        parameters.TryGetValue("VALUE",out var type);
        allDay = value.Length == 8;
        if (allDay)
            return id is null && (type is null || type.Equals("DATE",StringComparison.OrdinalIgnoreCase)) &&
                   DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out start);
        if (type is not null && !type.Equals("DATE-TIME",StringComparison.OrdinalIgnoreCase)) return false;
        var utc = value.EndsWith('Z');
        if (utc && id is not null) return false;
        if (!DateTime.TryParseExact(utc ? value[..^1] : value,TimeFormats,CultureInfo.InvariantCulture,DateTimeStyles.None,out start)) return false;
        floating = !utc && id is null;
        if (utc) zone = TimeZoneInfo.Utc;
        else if (id is not null)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { return false; }
            catch (InvalidTimeZoneException) { return false; }
        }
        return !zone.IsInvalidTime(start);
    }

    private static DateTime ToUtc(DateTime wall, TimeZoneInfo zone) => zone.IsAmbiguousTime(wall)
        ? new DateTimeOffset(wall,zone.GetAmbiguousTimeOffsets(wall).Max()).UtcDateTime
        : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall,DateTimeKind.Unspecified),zone);

    private static int Offset(DayOfWeek day, DayOfWeek weekStart) => ((int)day-(int)weekStart+7)%7;
    private static bool Positive(string raw, out int number) => int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out number) && number > 0;
    private static bool TryDay(string value, out DayOfWeek day)
    {
        day = value.ToUpperInvariant() switch { "MO"=>DayOfWeek.Monday,"TU"=>DayOfWeek.Tuesday,"WE"=>DayOfWeek.Wednesday,
            "TH"=>DayOfWeek.Thursday,"FR"=>DayOfWeek.Friday,"SA"=>DayOfWeek.Saturday,"SU"=>DayOfWeek.Sunday,_=>(DayOfWeek)(-1) };
        return (int)day >= 0;
    }
    private static bool Matches(DateOnly date, DateOnly start, HashSet<DayOfWeek> days, int origin, int interval) =>
        date >= start && days.Contains(date.DayOfWeek) && ((long)date.DayNumber-origin)/7%interval == 0;

    private static HashSet<int>? InvalidCountDays(TimeZoneInfo zone, DateTime start, DateOnly latest,
        HashSet<DayOfWeek> days, int origin, int interval)
    {
        var invalid = new HashSet<int>();
        var rules = zone.GetAdjustmentRules().Where(rule => rule.DaylightDelta != TimeSpan.Zero &&
            rule.DateEnd >= start.Date && DateOnly.FromDateTime(rule.DateStart) <= latest && GapCouldContain(rule,start.TimeOfDay)).ToArray();
        if (rules.Length == 0) return invalid;
        // Ordinary series use direct week arithmetic. Only DST-gap-sensitive
        // COUNT needs bounded transition history, not an occurrence-by-occurrence scan.
        if (latest.Year-start.Year > 400) return null;
        foreach (var rule in rules)
            for (var year = Math.Max(start.Year,rule.DateStart.Year); year <= Math.Min(latest.Year,rule.DateEnd.Year); year++)
            {
                var transition = rule.DaylightDelta > TimeSpan.Zero ? rule.DaylightTransitionStart : rule.DaylightTransitionEnd;
                var transitionDate = TransitionDate(year,transition);
                for (var delta = -1; delta <= 1; delta++)
                {
                    var number = transitionDate.DayNumber+delta;
                    if (number < 0 || number > DateOnly.MaxValue.DayNumber) continue;
                    var date = DateOnly.FromDayNumber(number); var wall = date.ToDateTime(TimeOnly.FromDateTime(start));
                    if (wall < start || date > latest || wall.Date < rule.DateStart || wall.Date > rule.DateEnd ||
                        !Matches(date,DateOnly.FromDateTime(start),days,origin,interval)) continue;
                    if (zone.IsInvalidTime(wall)) invalid.Add(number);
                }
            }
        return invalid;
    }

    private static bool GapCouldContain(TimeZoneInfo.AdjustmentRule rule, TimeSpan time)
    {
        var transition = rule.DaylightDelta > TimeSpan.Zero ? rule.DaylightTransitionStart : rule.DaylightTransitionEnd;
        var distance = time-transition.TimeOfDay.TimeOfDay;
        if (distance < TimeSpan.Zero) distance += TimeSpan.FromDays(1);
        return distance < rule.DaylightDelta.Duration();
    }

    private static DateOnly TransitionDate(int year, TimeZoneInfo.TransitionTime transition)
    {
        var last = DateTime.DaysInMonth(year,transition.Month);
        if (transition.IsFixedDateRule) return new DateOnly(year,transition.Month,Math.Min(last,transition.Day));
        if (transition.Week == 5)
        {
            var end = new DateOnly(year,transition.Month,last);
            return end.AddDays(-Offset(end.DayOfWeek,transition.DayOfWeek));
        }
        var first = new DateOnly(year,transition.Month,1);
        return first.AddDays(Offset(transition.DayOfWeek,first.DayOfWeek)+7*(transition.Week-1));
    }
}
