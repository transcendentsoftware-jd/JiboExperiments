namespace Jibo.Cloud.Infrastructure.Calendar;

internal static class CalendarLocalTime
{
    internal static TimeZoneInfo ResolveZone(string? zoneId)
    {
        if (!string.IsNullOrWhiteSpace(zoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(zoneId.Trim()); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }
}
