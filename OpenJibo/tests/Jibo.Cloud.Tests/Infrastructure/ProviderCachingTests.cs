using System.Net;
using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.News;
using Jibo.Cloud.Infrastructure.Weather;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class ProviderCachingTests
{
    [Fact]
    public async Task OpenWeatherReportProvider_ReusesCachedWeatherAndGeocodeResponses()
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/geo/1.0/direct" => JsonResponse(
                    """[{"name":"Boston","state":"Massachusetts","country":"US","lat":42.3601,"lon":-71.0589}]"""),
                "/data/2.5/weather" => JsonResponse(
                    """{"name":"Boston","weather":[{"main":"Clouds","description":"overcast clouds"}],"main":{"temp":70.2,"temp_max":72.9,"temp_min":66.1}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new OpenWeatherReportProvider(
            new HttpClient(handler),
            new OpenWeatherOptions
            {
                ApiKey = "test-key",
                CurrentCacheTtlSeconds = 300,
                GeocodeCacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<OpenWeatherReportProvider>.Instance);

        var request = new WeatherReportRequest("Boston,US", null, null, false, false, 0);
        var first = await provider.GetReportAsync(request);
        var second = await provider.GetReportAsync(request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, handler.GetCallCount("/geo/1.0/direct"));
        Assert.Equal(1, handler.GetCallCount("/data/2.5/weather"));
    }

    [Fact]
    public async Task NewsApiBriefingProvider_ReusesCachedHeadlinesForIdenticalRequests()
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/v2/top-headlines" => JsonResponse(
                    """{"status":"ok","articles":[{"title":"Robotics team wins regional title","description":"A big local victory.","source":{"name":"AP News"},"url":"https://example.com/a"}]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new NewsApiBriefingProvider(
            new HttpClient(handler),
            new NewsApiOptions
            {
                ApiKey = "test-key",
                CacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<NewsApiBriefingProvider>.Instance);

        var request = new NewsBriefingRequest(["sports"], 3);
        var first = await provider.GetBriefingAsync(request);
        var second = await provider.GetBriefingAsync(request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, handler.GetCallCount("/v2/top-headlines"));
    }

    [Fact]
    public async Task NewsApiBriefingProvider_FallsBackToUncategorizedHeadlines_WhenCategoryReturnsEmpty()
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            if (!string.Equals(path, "/v2/top-headlines", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var query = message.RequestUri?.Query ?? string.Empty;
            if (query.Contains("category=sports", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"status":"ok","articles":[]}""");
            }

            return JsonResponse(
                """{"status":"ok","articles":[{"title":"General robotics update","description":"Top story","source":{"name":"AP News"},"url":"https://example.com/general"}]}""");
        });
        var provider = new NewsApiBriefingProvider(
            new HttpClient(handler),
            new NewsApiOptions
            {
                ApiKey = "test-key",
                CacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<NewsApiBriefingProvider>.Instance);

        var result = await provider.GetBriefingAsync(new NewsBriefingRequest(["sports"], 3));

        Assert.NotNull(result);
        Assert.Single(result!.Headlines);
        Assert.Equal("General robotics update", result.Headlines[0].Title);
        Assert.Equal(2, handler.GetCallCount("/v2/top-headlines"));
    }

    [Fact]
    public async Task NewsApiBriefingProvider_FallsBackToEverything_WhenTopHeadlinesAreEmpty()
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/v2/top-headlines" => JsonResponse("""{"status":"ok","articles":[]}"""),
                "/v2/everything" => JsonResponse(
                    """{"status":"ok","articles":[{"title":"Robotics breakthrough announced","description":"Lab unveils a new platform.","source":{"name":"Science Daily"},"url":"https://example.com/robotics"}]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new NewsApiBriefingProvider(
            new HttpClient(handler),
            new NewsApiOptions
            {
                ApiKey = "test-key",
                DefaultCategories = ["general"],
                CacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<NewsApiBriefingProvider>.Instance);

        var result = await provider.GetBriefingAsync(new NewsBriefingRequest([], 3));

        Assert.NotNull(result);
        Assert.Single(result!.Headlines);
        Assert.Equal("Robotics breakthrough announced", result.Headlines[0].Title);
        Assert.Equal(2, handler.GetCallCount("/v2/top-headlines"));
        Assert.Equal(1, handler.GetCallCount("/v2/everything"));
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        private readonly Dictionary<string, int> callsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();

        public int GetCallCount(string path)
        {
            lock (gate)
            {
                return callsByPath.TryGetValue(path, out var count) ? count : 0;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            lock (gate)
            {
                callsByPath[path] = callsByPath.TryGetValue(path, out var count) ? count + 1 : 1;
            }

            return Task.FromResult(responseFactory(request));
        }
    }
}
