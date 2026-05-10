using System.Globalization;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Weather;

public sealed class OpenWeatherReportProvider(
    HttpClient httpClient,
    OpenWeatherOptions options,
    ILogger<OpenWeatherReportProvider> logger)
    : IWeatherReportProvider
{
    public async Task<WeatherReportSnapshot?> GetReportAsync(
        WeatherReportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return null;
        }

        try
        {
            var location = await ResolveLocationAsync(request, cancellationToken);
            if (location is null)
            {
                return null;
            }

            var useCelsius = request.UseCelsius ?? options.UseCelsius;
            var forecastDayOffset = request.ForecastDayOffset ?? (request.IsTomorrow ? 1 : 0);
            if (forecastDayOffset <= 0)
            {
                return await GetCurrentWeatherAsync(location.Value, useCelsius, cancellationToken);
            }

            if (forecastDayOffset > MaxForecastDayOffset)
            {
                return null;
            }

            return await GetForecastForDayOffsetAsync(location.Value, useCelsius, forecastDayOffset, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "OpenWeather lookup failed.");
            return null;
        }
    }

    private async Task<LocationPoint?> ResolveLocationAsync(
        WeatherReportRequest request,
        CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(request.LocationQuery)
            ? null
            : request.LocationQuery.Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            if (request is { Latitude: not null, Longitude: not null })
            {
                return new LocationPoint(request.Latitude.Value, request.Longitude.Value, null);
            }

            query = options.DefaultLocation;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var geocodeUri = BuildRequestUri(
            "/geo/1.0/direct",
            ("q", query),
            ("limit", "1"),
            ("appid", options.ApiKey!));
        using var response = await httpClient.GetAsync(geocodeUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var location = document.RootElement[0];
        if (!TryReadDouble(location, "lat", out var latitude) ||
            !TryReadDouble(location, "lon", out var longitude))
        {
            return null;
        }

        var displayName = BuildLocationDisplayName(location);
        return new LocationPoint(latitude, longitude, displayName);
    }

    private async Task<WeatherReportSnapshot?> GetCurrentWeatherAsync(
        LocationPoint location,
        bool useCelsius,
        CancellationToken cancellationToken)
    {
        var weatherUri = BuildRequestUri(
            "/data/2.5/weather",
            ("lat", location.Latitude.ToString(CultureInfo.InvariantCulture)),
            ("lon", location.Longitude.ToString(CultureInfo.InvariantCulture)),
            ("units", useCelsius ? "metric" : "imperial"),
            ("appid", options.ApiKey!));
        using var response = await httpClient.GetAsync(weatherUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("main", out var main))
        {
            return null;
        }

        var locationName = ReadNonEmptyString(root, "name") ?? location.DisplayName ?? options.DefaultLocation;
        var summary = TryReadWeatherSummary(root);
        var condition = TryReadWeatherCondition(root);
        var temperature = TryReadInt(main, "temp");
        var high = TryReadInt(main, "temp_max");
        var low = TryReadInt(main, "temp_min");
        if (temperature is null && high is null && low is null)
        {
            return null;
        }

        var resolvedTemperature = temperature ?? high ?? low ?? 0;
        return new WeatherReportSnapshot(
            locationName,
            summary ?? "partly cloudy",
            resolvedTemperature,
            high,
            low,
            condition,
            useCelsius);
    }

    private async Task<WeatherReportSnapshot?> GetForecastForDayOffsetAsync(
        LocationPoint location,
        bool useCelsius,
        int forecastDayOffset,
        CancellationToken cancellationToken)
    {
        var forecastUri = BuildRequestUri(
            "/data/2.5/forecast",
            ("lat", location.Latitude.ToString(CultureInfo.InvariantCulture)),
            ("lon", location.Longitude.ToString(CultureInfo.InvariantCulture)),
            ("units", useCelsius ? "metric" : "imperial"),
            ("appid", options.ApiKey!));
        using var response = await httpClient.GetAsync(forecastUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var offset = TryReadForecastOffset(root);
        var targetDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(offset).DateTime.AddDays(forecastDayOffset));
        var entries = new List<ForecastEntry>();
        foreach (var item in list.EnumerateArray())
        {
            if (!TryReadLong(item, "dt", out var unixSeconds))
            {
                continue;
            }

            var localTimestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(offset);
            if (DateOnly.FromDateTime(localTimestamp.DateTime) != targetDate)
            {
                continue;
            }

            if (!item.TryGetProperty("main", out var main))
            {
                continue;
            }

            entries.Add(new ForecastEntry(
                localTimestamp,
                TryReadInt(main, "temp"),
                TryReadInt(main, "temp_max"),
                TryReadInt(main, "temp_min"),
                TryReadWeatherSummary(item),
                TryReadWeatherCondition(item)));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var selectedEntry = entries
            .OrderBy(entry => Math.Abs((entry.LocalTime.TimeOfDay - TimeSpan.FromHours(12)).TotalMinutes))
            .First();
        var highs = entries
            .Where(entry => entry.HighTemperature is not null)
            .Select(entry => entry.HighTemperature!.Value)
            .ToArray();
        var lows = entries
            .Where(entry => entry.LowTemperature is not null)
            .Select(entry => entry.LowTemperature!.Value)
            .ToArray();

        var locationName = ReadForecastLocationName(root) ?? location.DisplayName ?? options.DefaultLocation;
        var high = highs.Length > 0 ? highs.Max() : selectedEntry.HighTemperature;
        var low = lows.Length > 0 ? lows.Min() : selectedEntry.LowTemperature;
        var temperature = selectedEntry.Temperature ?? high ?? low ?? 0;

        return new WeatherReportSnapshot(
            locationName,
            selectedEntry.Summary ?? "partly cloudy",
            temperature,
            high,
            low,
            selectedEntry.Condition,
            useCelsius);
    }

    private Uri BuildRequestUri(string path, params (string Key, string Value)[] queryParts)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var query = string.Join(
            "&",
            queryParts.Select(part =>
                $"{Uri.EscapeDataString(part.Key)}={Uri.EscapeDataString(part.Value)}"));
        return new Uri($"{baseUrl}{path}?{query}");
    }

    private static TimeSpan TryReadForecastOffset(JsonElement root)
    {
        if (!root.TryGetProperty("city", out var city))
        {
            return TimeSpan.Zero;
        }

        var timezoneSeconds = TryReadInt(city, "timezone");
        if (timezoneSeconds is null)
        {
            return TimeSpan.Zero;
        }

        var seconds = Math.Clamp(timezoneSeconds.Value, -50400, 50400);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? ReadForecastLocationName(JsonElement root)
    {
        if (!root.TryGetProperty("city", out var city))
        {
            return null;
        }

        var name = ReadNonEmptyString(city, "name");
        var country = ReadNonEmptyString(city, "country");
        return string.IsNullOrWhiteSpace(country) ? name : $"{name}, {country}";
    }

    private static string? BuildLocationDisplayName(JsonElement location)
    {
        var name = ReadNonEmptyString(location, "name");
        var state = ReadNonEmptyString(location, "state");
        var country = ReadNonEmptyString(location, "country");
        if (!string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(state) &&
            !string.IsNullOrWhiteSpace(country))
        {
            return $"{name}, {state}, {country}";
        }

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(country))
        {
            return $"{name}, {country}";
        }

        return name;
    }

    private static string? TryReadWeatherSummary(JsonElement root)
    {
        return TryReadWeatherProperty(root, "description");
    }

    private static string? TryReadWeatherCondition(JsonElement root)
    {
        var main = TryReadWeatherProperty(root, "main");
        if (string.IsNullOrWhiteSpace(main))
        {
            return null;
        }

        var normalized = main.Trim().ToLowerInvariant();
        return normalized switch
        {
            "rain" or "drizzle" or "thunderstorm" => "rain",
            "snow" => "snow",
            "clear" => "sunny",
            "clouds" => "cloudy",
            "mist" or "smoke" or "haze" or "fog" => "fog",
            _ => normalized
        };
    }

    private static string? TryReadWeatherProperty(JsonElement root, string key)
    {
        if (!root.TryGetProperty("weather", out var weather) ||
            weather.ValueKind != JsonValueKind.Array ||
            weather.GetArrayLength() == 0)
        {
            return null;
        }

        var first = weather[0];
        return ReadNonEmptyString(first, key);
    }

    private static string? ReadNonEmptyString(JsonElement source, string key)
    {
        return source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryReadDouble(JsonElement source, string key, out double value)
    {
        value = 0;
        return source.TryGetProperty(key, out var element) && element.TryGetDouble(out value);
    }

    private static bool TryReadLong(JsonElement source, string key, out long value)
    {
        value = 0;
        return source.TryGetProperty(key, out var element) && element.TryGetInt64(out value);
    }

    private static int? TryReadInt(JsonElement source, string key)
    {
        if (!source.TryGetProperty(key, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numeric))
        {
            return (int)Math.Round(numeric, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private readonly record struct LocationPoint(double Latitude, double Longitude, string? DisplayName);

    private sealed record ForecastEntry(
        DateTimeOffset LocalTime,
        int? Temperature,
        int? HighTemperature,
        int? LowTemperature,
        string? Summary,
        string? Condition);

    private const int MaxForecastDayOffset = 5;
}
