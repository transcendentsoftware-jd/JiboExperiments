namespace Jibo.Runtime.Abstractions;

public interface IWeatherCapability : ICapability
{
    Task<WeatherResult> GetWeatherAsync(WeatherRequest request, CancellationToken cancellationToken = default);
}