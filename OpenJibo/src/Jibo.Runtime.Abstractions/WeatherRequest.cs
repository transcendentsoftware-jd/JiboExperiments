namespace Jibo.Runtime.Abstractions;

public sealed class WeatherRequest
{
    public string? LocationName { get; init; }
    public string? TimeZone { get; init; }
    public bool IncludeHourly { get; init; }
    public bool IncludeDaily { get; init; }
}