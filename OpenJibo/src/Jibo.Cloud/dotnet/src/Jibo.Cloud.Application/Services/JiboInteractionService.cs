using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboInteractionService(
    JiboExperienceContentCache contentCache,
    IJiboRandomizer randomizer)
{
    public async Task<JiboInteractionDecision> BuildDecisionAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var catalog = await contentCache.GetCatalogAsync(cancellationToken);
        var transcript = (turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty).Trim();
        var lowered = transcript.ToLowerInvariant();
        var clientIntent = turn.Attributes.TryGetValue("clientIntent", out var rawClientIntent)
            ? rawClientIntent?.ToString()
            : null;

        var semanticIntent = ResolveSemanticIntent(lowered, clientIntent);
        return semanticIntent switch
        {
            "joke" => BuildJokeDecision(catalog),
            "dance" => BuildDanceDecision(catalog),
            "time" => new JiboInteractionDecision("time", $"It is {DateTime.Now:hh:mm tt}."),
            "date" => new JiboInteractionDecision("date", $"Today is {DateTime.Now:dddd, MMMM d}."),
            "hello" => new JiboInteractionDecision("hello", randomizer.Choose(catalog.GreetingReplies)),
            "how_are_you" => new JiboInteractionDecision("how_are_you", randomizer.Choose(catalog.HowAreYouReplies)),
            "surprise" => new JiboInteractionDecision("surprise", randomizer.Choose(catalog.SurpriseReplies)),
            "personal_report" => new JiboInteractionDecision("personal_report", randomizer.Choose(catalog.PersonalReportReplies)),
            "weather" => new JiboInteractionDecision("weather", randomizer.Choose(catalog.WeatherReplies)),
            "calendar" => new JiboInteractionDecision("calendar", randomizer.Choose(catalog.CalendarReplies)),
            "commute" => new JiboInteractionDecision("commute", randomizer.Choose(catalog.CommuteReplies)),
            "news" => new JiboInteractionDecision("news", randomizer.Choose(catalog.NewsReplies)),
            _ => new JiboInteractionDecision("chat", BuildGenericReply(catalog, transcript, lowered))
        };
    }

    private JiboInteractionDecision BuildJokeDecision(JiboExperienceCatalog catalog)
    {
        var joke = randomizer.Choose(catalog.Jokes);
        return new JiboInteractionDecision(
            "joke",
            joke,
            "@be/joke",
            new Dictionary<string, object?>
            {
                ["replyType"] = "joke"
            });
    }

    private JiboInteractionDecision BuildDanceDecision(JiboExperienceCatalog catalog)
    {
        var dance = randomizer.Choose(catalog.DanceAnimations);
        return new JiboInteractionDecision(
            "dance",
            "Okay. Watch this.",
            "chitchat-skill",
            new Dictionary<string, object?>
            {
                ["esml"] = $"<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, {dance}' /></speak>",
                ["mim_id"] = "runtime-chat",
                ["mim_type"] = "announcement"
            });
    }

    private string BuildGenericReply(JiboExperienceCatalog catalog, string transcript, string lowered)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return "I am listening.";
        }

        if (lowered.Contains("good morning", StringComparison.Ordinal))
        {
            return "Good morning! It is nice to hear your voice.";
        }

        if (lowered.Contains("good afternoon", StringComparison.Ordinal))
        {
            return "Good afternoon. I am happy to be here.";
        }

        if (lowered.Contains("good night", StringComparison.Ordinal))
        {
            return "Good night. Sleep tight.";
        }

        return randomizer.Choose(catalog.GenericFallbackReplies).Replace("{transcript}", transcript, StringComparison.Ordinal);
    }

    private static string ResolveSemanticIntent(string loweredTranscript, string? clientIntent)
    {
        if (string.Equals(clientIntent, "askForTime", StringComparison.OrdinalIgnoreCase))
        {
            return "time";
        }

        if (string.Equals(clientIntent, "askForDate", StringComparison.OrdinalIgnoreCase))
        {
            return "date";
        }

        if (loweredTranscript.Contains("joke", StringComparison.Ordinal))
        {
            return "joke";
        }

        if (loweredTranscript.Contains("dance", StringComparison.Ordinal))
        {
            return "dance";
        }

        if (loweredTranscript.Contains("surprise", StringComparison.Ordinal))
        {
            return "surprise";
        }

        if (loweredTranscript.Contains("personal report", StringComparison.Ordinal))
        {
            return "personal_report";
        }

        if (loweredTranscript.Contains("weather", StringComparison.Ordinal))
        {
            return "weather";
        }

        if (loweredTranscript.Contains("calendar", StringComparison.Ordinal))
        {
            return "calendar";
        }

        if (loweredTranscript.Contains("commute", StringComparison.Ordinal))
        {
            return "commute";
        }

        if (loweredTranscript.Contains("news", StringComparison.Ordinal))
        {
            return "news";
        }

        if (loweredTranscript.Contains("how are you", StringComparison.Ordinal) ||
            loweredTranscript.Contains("what's up", StringComparison.Ordinal) ||
            loweredTranscript.Contains("what s up", StringComparison.Ordinal))
        {
            return "how_are_you";
        }

        if (loweredTranscript.Contains("hello", StringComparison.Ordinal) ||
            loweredTranscript.Contains("hi", StringComparison.Ordinal) ||
            loweredTranscript.Contains("hey", StringComparison.Ordinal))
        {
            return "hello";
        }

        if (loweredTranscript.Contains("time", StringComparison.Ordinal))
        {
            return "time";
        }

        if (loweredTranscript.Contains("date", StringComparison.Ordinal) || loweredTranscript.Contains("day", StringComparison.Ordinal))
        {
            return "date";
        }

        return "chat";
    }
}

public sealed record JiboInteractionDecision(
    string IntentName,
    string ReplyText,
    string? SkillName = null,
    IDictionary<string, object?>? SkillPayload = null);
