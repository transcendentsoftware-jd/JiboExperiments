using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

internal static class ChitchatStateMachine
{
    internal const string StateMetadataKey = "chitchatState";
    internal const string RouteMetadataKey = "chitchatRoute";
    internal const string EmotionMetadataKey = "chitchatEmotion";

    internal const string IdleState = "idle";
    private const string IntentSplitState = "intent_split";
    private const string ProcessQueryState = "process_query";
    private const string CompleteState = "complete";

    private const string ScriptedResponseRoute = "ScriptedResponse";
    private const string EmotionQueryRoute = "EmotionQuery";
    private const string EmotionCommandRoute = "EmotionCommand";
    private const string ErrorResponseRoute = "ErrorResponse";

    private static readonly string[] EmotionQueryPhrases =
    [
        "how are you feeling",
        "how do you feel",
        "what mood are you in",
        "what is your mood",
        "what's your mood",
        "are you happy",
        "are you sad",
        "are you excited",
        "do you have emotions"
    ];

    private static readonly (string Emotion, string[] Phrases)[] EmotionCommandPhrases =
    [
        ("happy", ["smile", "be happy", "look happy", "cheer up"]),
        ("sad", ["be sad", "look sad"]),
        ("excited", ["be excited", "get excited", "act excited"]),
        ("calm", ["be calm", "relax"])
    ];

    private static readonly string[] EmotionCommandReplies =
    [
        "I can do that mood. Watch this.",
        "Switching mood now.",
        "Okay, mood change activated."
    ];

    public static JiboInteractionDecision? TryBuildDecision(
        string semanticIntent,
        string transcript,
        string loweredTranscript,
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        Func<string> buildErrorResponse)
    {
        switch (semanticIntent)
        {
            case "hello":
                return BuildScriptedResponseDecision(
                    "hello",
                    randomizer.Choose(catalog.GreetingReplies));
            case "robot_personality":
                return BuildScriptedResponseDecision(
                    "robot_personality",
                    randomizer.Choose(catalog.PersonalityReplies));
            case "how_are_you":
                return BuildEmotionQueryDecision(
                    "how_are_you",
                    randomizer.Choose(catalog.HowAreYouReplies));
            case "chat":
                if (IsEmotionQuery(loweredTranscript))
                {
                    return BuildEmotionQueryDecision(
                        "emotion_query",
                        randomizer.Choose(catalog.HowAreYouReplies));
                }

                if (TryResolveEmotionCommand(loweredTranscript, out var emotion))
                {
                    return BuildEmotionCommandDecision(randomizer, emotion!);
                }

                return BuildErrorResponseDecision(
                    "chat",
                    buildErrorResponse(),
                    transcript);
            default:
                return null;
        }
    }

    public static bool IsLikelyEmotionUtterance(string normalizedLoweredTranscript)
    {
        return IsEmotionQuery(normalizedLoweredTranscript) ||
               TryResolveEmotionCommand(normalizedLoweredTranscript, out _);
    }

    private static JiboInteractionDecision BuildScriptedResponseDecision(string intentName, string replyText)
    {
        return new JiboInteractionDecision(
            intentName,
            replyText,
            ContextUpdates: BuildContextUpdates(
                ScriptedResponseRoute,
                emotion: null));
    }

    private static JiboInteractionDecision BuildEmotionQueryDecision(string intentName, string replyText)
    {
        return new JiboInteractionDecision(
            intentName,
            replyText,
            ContextUpdates: BuildContextUpdates(
                EmotionQueryRoute,
                emotion: null));
    }

    private static JiboInteractionDecision BuildEmotionCommandDecision(IJiboRandomizer randomizer, string emotion)
    {
        var (esmlEmotion, responseSuffix) = emotion switch
        {
            "happy" => ("happy", "I am feeling happy."),
            "sad" => ("sad", "I can do a thoughtful mood too."),
            "excited" => ("happy", "I am feeling excited."),
            "calm" => ("neutral", "I am in a calmer mood."),
            _ => ("neutral", "Mood updated.")
        };

        return new JiboInteractionDecision(
            "emotion_command",
            randomizer.Choose(EmotionCommandReplies),
            "chitchat-skill",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["esml"] = $"<speak><es cat='{esmlEmotion}' filter='!ssa-only, !sfx-only' endNeutral='true'>{responseSuffix}</es></speak>",
                ["mim_id"] = "runtime-chat",
                ["mim_type"] = "announcement",
                ["prompt_id"] = "RUNTIME_EMOTION_COMMAND",
                ["prompt_sub_category"] = "AN"
            },
            ContextUpdates: BuildContextUpdates(
                EmotionCommandRoute,
                emotion));
    }

    private static JiboInteractionDecision BuildErrorResponseDecision(string intentName, string replyText, string transcript)
    {
        var normalizedTranscript = string.IsNullOrWhiteSpace(transcript)
            ? string.Empty
            : transcript.Trim();
        return new JiboInteractionDecision(
            intentName,
            replyText,
            ContextUpdates: BuildContextUpdates(
                ErrorResponseRoute,
                emotion: null,
                rawTranscript: normalizedTranscript));
    }

    private static IDictionary<string, object?> BuildContextUpdates(
        string route,
        string? emotion,
        string? rawTranscript = null)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [StateMetadataKey] = CompleteState,
            [RouteMetadataKey] = route,
            [EmotionMetadataKey] = emotion ?? string.Empty,
            ["chitchatLastState"] = IntentSplitState,
            ["chitchatProcessState"] = ProcessQueryState,
            ["chitchatRawTranscript"] = rawTranscript ?? string.Empty
        };
    }

    private static bool IsEmotionQuery(string loweredTranscript)
    {
        return ContainsAnyPhrase(loweredTranscript, EmotionQueryPhrases);
    }

    private static bool TryResolveEmotionCommand(string loweredTranscript, out string? emotion)
    {
        emotion = null;
        foreach (var mapping in EmotionCommandPhrases)
        {
            if (!ContainsAnyPhrase(loweredTranscript, mapping.Phrases))
            {
                continue;
            }

            emotion = mapping.Emotion;
            return true;
        }

        return false;
    }

    private static bool ContainsAnyPhrase(string loweredTranscript, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            if (string.Equals(loweredTranscript, phrase, StringComparison.Ordinal) ||
                loweredTranscript.StartsWith($"{phrase} ", StringComparison.Ordinal) ||
                loweredTranscript.Contains($" {phrase} ", StringComparison.Ordinal) ||
                loweredTranscript.EndsWith($" {phrase}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
