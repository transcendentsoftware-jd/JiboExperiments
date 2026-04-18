using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using System.Text.Json;

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
        var isYesNoTurn = IsYesNoTurn(turn);

        var semanticIntent = ResolveSemanticIntent(lowered, clientIntent, isYesNoTurn);
        return semanticIntent switch
        {
            "joke" => BuildJokeDecision(catalog),
            "dance" => BuildDanceDecision(catalog),
            "time" => new JiboInteractionDecision("time", $"It is {DateTime.Now:hh:mm tt}."),
            "date" => new JiboInteractionDecision("date", $"Today is {DateTime.Now:dddd, MMMM d}."),
            "hello" => new JiboInteractionDecision("hello", randomizer.Choose(catalog.GreetingReplies)),
            "how_are_you" => new JiboInteractionDecision("how_are_you", randomizer.Choose(catalog.HowAreYouReplies)),
            "yes" => new JiboInteractionDecision("yes", "Yes."),
            "no" => new JiboInteractionDecision("no", "No."),
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

        return lowered.Contains("good night", StringComparison.Ordinal)
            ? "Good night. Sleep tight."
            : randomizer.Choose(catalog.GenericFallbackReplies)
                .Replace("{transcript}", transcript, StringComparison.Ordinal);
    }

    private static string ResolveSemanticIntent(string loweredTranscript, string? clientIntent, bool isYesNoTurn)
    {
        if (string.Equals(clientIntent, "askForTime", StringComparison.OrdinalIgnoreCase))
        {
            return "time";
        }

        if (string.Equals(clientIntent, "askForDate", StringComparison.OrdinalIgnoreCase))
        {
            return "date";
        }

        if (MatchesAny(loweredTranscript, "joke", "funny", "make me laugh"))
        {
            return "joke";
        }

        if (MatchesAny(loweredTranscript, "dance", "boogie"))
        {
            return "dance";
        }

        if (MatchesAny(loweredTranscript, "surprise", "surprise me", "show me something fun"))
        {
            return "surprise";
        }

        if (MatchesAny(loweredTranscript, "personal report", "my report", "daily report", "my update"))
        {
            return "personal_report";
        }

        if (MatchesAny(loweredTranscript, "weather", "forecast", "weather report", "is it raining"))
        {
            return "weather";
        }

        if (MatchesAny(loweredTranscript, "calendar", "schedule", "what's on my calendar", "what is on my calendar"))
        {
            return "calendar";
        }

        if (MatchesAny(loweredTranscript, "commute", "traffic", "drive to work", "how long to work"))
        {
            return "commute";
        }

        if (MatchesAny(loweredTranscript, "news", "headlines", "news update", "tell me the news"))
        {
            return "news";
        }

        if (MatchesAny(loweredTranscript, "how are you", "what's up", "what s up", "what up"))
        {
            return "how_are_you";
        }

        if (MatchesAny(loweredTranscript, "hello", "hi", "hey"))
        {
            return "hello";
        }

        if (isYesNoTurn && MatchesAny(loweredTranscript, "yes", "yeah", "yup", "sure", "uh huh"))
        {
            return "yes";
        }

        if (isYesNoTurn && MatchesAny(loweredTranscript, "no", "nope", "nah"))
        {
            return "no";
        }

        if (MatchesAny(loweredTranscript, "what time is it", "current time", "the time", "time is it") ||
            loweredTranscript.Contains("time", StringComparison.Ordinal))
        {
            return "time";
        }

        if (MatchesAny(loweredTranscript, "what day is it", "what is the date", "today s date", "today's date") ||
            loweredTranscript.Contains("date", StringComparison.Ordinal) ||
            loweredTranscript.Contains("day", StringComparison.Ordinal))
        {
            return "date";
        }

        return "chat";
    }

    private static bool IsYesNoTurn(TurnContext turn)
    {
        return ReadRules(turn, "listenRules").Concat(ReadRules(turn, "clientRules"))
            .Any(static rule =>
                string.Equals(rule, "$YESNO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReadRules(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IReadOnlyList<string> typed => typed,
            IEnumerable<string> strings => strings,
            JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty),
            _ => []
        };
    }

    private static bool MatchesAny(string loweredTranscript, params string[] candidates)
    {
        return candidates.Any(candidate => loweredTranscript.Contains(candidate, StringComparison.Ordinal));
    }
}

public sealed record JiboInteractionDecision(
    string IntentName,
    string ReplyText,
    string? SkillName = null,
    IDictionary<string, object?>? SkillPayload = null);
