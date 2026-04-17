using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class JiboInteractionServiceTests
{
    [Fact]
    public async Task BuildDecisionAsync_Joke_UsesCatalogBackedRandomContent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me a joke",
            NormalizedTranscript = "tell me a joke"
        });

        Assert.Equal("joke", decision.IntentName);
        Assert.Equal("@be/joke", decision.SkillName);
        Assert.Equal("Why did the robot cross the road? Because it was programmed by the chicken.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Dance_UsesCatalogBackedAnimation()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do a dance",
            NormalizedTranscript = "do a dance"
        });

        Assert.Equal("dance", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("Okay. Watch this.", decision.ReplyText);
        Assert.Equal("<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, rom-upbeat' /></speak>", decision.SkillPayload!["esml"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluAskForDate_MapsToDateIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "askForDate"
            }
        });

        Assert.Equal("date", decision.IntentName);
        Assert.Contains("Today is", decision.ReplyText, StringComparison.Ordinal);
    }

    private static JiboInteractionService CreateService()
    {
        return new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer());
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[0];
        }
    }
}
