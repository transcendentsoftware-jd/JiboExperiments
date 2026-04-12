using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Tests.Fixtures;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class ProtocolFixtureReplayTests
{
    private readonly JiboCloudProtocolService _service = new(new InMemoryCloudStateStore());

    [Theory]
    [InlineData("fixtures\\create-hub-token.request.json")]
    [InlineData("fixtures\\new-robot-token.request.json")]
    public async Task FixtureRequest_ReplaysSuccessfully(string relativePath)
    {
        var fixture = ProtocolFixtureLoader.Load(relativePath);
        var result = await _service.DispatchAsync(fixture.Request);

        Assert.Equal(fixture.ExpectedStatusCode, result.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(result.BodyText));
    }
}
