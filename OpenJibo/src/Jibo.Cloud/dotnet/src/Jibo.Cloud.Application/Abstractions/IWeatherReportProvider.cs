namespace Jibo.Cloud.Application.Abstractions;

public interface IWeatherReportProvider
{
    Task<WeatherReportSnapshot?> GetReportAsync(
        WeatherReportRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WeatherReportRequest(
    string? LocationQuery,
    double? Latitude,
    double? Longitude,
    bool IsTomorrow,
    bool? UseCelsius);

public sealed record WeatherReportSnapshot(
    string LocationName,
    string Summary,
    int Temperature,
    int? HighTemperature,
    int? LowTemperature,
    string? Condition,
    bool UseCelsius);
