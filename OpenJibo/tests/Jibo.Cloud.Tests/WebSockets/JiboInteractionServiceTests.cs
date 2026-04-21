using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Runtime.Abstractions;
using System.Text.Json;

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

    [Fact]
    public async Task BuildDecisionAsync_YesNoFollowUp_MapsShortAffirmationToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yeah",
            NormalizedTranscript = "yeah",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "create/is_it_a_keeper" }
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_YesNoFollowUp_FromAsrHints_MapsShortDenialToNoIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "surprises-ota/want_to_download_now" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.Equal("No.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SettingsDownloadPrompt_MapsShortDenialToNoIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "No.",
            NormalizedTranscript = "No.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "settings/download_now_later", "globals/global_commands_launch" }
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.Equal("No.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SurprisesDateOfferPrompt_MapsShortAffirmationToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Yes!",
            NormalizedTranscript = "Yes!",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "surprises-date/offer_date_fact", "globals/global_commands_launch" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SkillPhraseVariant_MapsToKnownIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "make me laugh",
            NormalizedTranscript = "make me laugh"
        });

        Assert.Equal("joke", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenTheRadio_MapsToRadioLaunchIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open the radio",
            NormalizedTranscript = "open the radio"
        });

        Assert.Equal("radio", decision.IntentName);
        Assert.Equal("@be/radio", decision.SkillName);
        Assert.Equal("@be/radio", decision.SkillPayload!["skillId"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PlayCountryMusic_MapsToRadioGenreLaunchIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "play country music",
            NormalizedTranscript = "play country music"
        });

        Assert.Equal("radio_genre", decision.IntentName);
        Assert.Equal("@be/radio", decision.SkillName);
        Assert.Equal("Country", decision.SkillPayload!["station"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_UsesStructuredClientNluGuess()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "guess",
            NormalizedTranscript = "guess",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "guess",
                ["clientRules"] = new[] { "word-of-the-day/puzzle" },
                ["clientEntities"] = JsonDocument.Parse("""{"guess":"pastoral"}""").RootElement.Clone()
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard pastoral.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_UsesSpokenTranscriptDuringPuzzleTurn()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "pastoral",
            NormalizedTranscript = "pastoral",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "word-of-the-day/puzzle" }
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard pastoral.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayStartPhrase_MapsToSkillIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "start word of the day",
            NormalizedTranscript = "start word of the day"
        });

        Assert.Equal("word_of_the_day", decision.IntentName);
        Assert.Equal("Starting word of the day.", decision.ReplyText);
        Assert.Equal("@be/word-of-the-day", decision.SkillName);
        Assert.Equal("word-of-the-day", decision.SkillPayload!["domain"]);
        Assert.Equal("@be/word-of-the-day", decision.SkillPayload["skillId"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_LineNumberUsesListenHints()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Two.",
            NormalizedTranscript = "Two.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "word-of-the-day/puzzle" },
                ["listenAsrHints"] = new[] { "doodad", "pastoral", "escarpment" }
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard pastoral.", decision.ReplyText);
        Assert.Equal("@be/word-of-the-day", decision.SkillName);
        Assert.Equal("pastoral", decision.SkillPayload!["guess"]);
        Assert.Equal("@be/word-of-the-day", decision.SkillPayload["skillId"]);
        Assert.Equal("completion_only", decision.SkillPayload["cloudResponseMode"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_FuzzyMatchesClosestHint()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Haglet.",
            NormalizedTranscript = "Haglet.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "word-of-the-day/puzzle" },
                ["listenAsrHints"] = new[] { "aglet", "hovel", "wisenheimer" }
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard aglet.", decision.ReplyText);
        Assert.Equal("aglet", decision.SkillPayload!["guess"]);
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
