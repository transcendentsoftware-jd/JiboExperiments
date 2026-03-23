namespace Jibo.Runtime.Abstractions;

public sealed class WeatherResult
{
    public string Summary { get; init; } = string.Empty;
    public int? CurrentTemperatureF { get; init; }
    public int? HighTodayF { get; init; }
    public int? LowTonightF { get; init; }
    public string? Conditions { get; init; }
    public string? LocationLabel { get; init; }
    public IDictionary<string, object?> Raw { get; init; } = new Dictionary<string, object?>();
}