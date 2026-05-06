namespace Jibo.Cloud.Infrastructure.Weather;

public sealed class OpenWeatherOptions
{
    public string BaseUrl { get; set; } = "https://api.openweathermap.org";

    public string? ApiKey { get; set; }

    public string DefaultLocation { get; set; } = "Boston,US";

    public bool UseCelsius { get; set; }
}
