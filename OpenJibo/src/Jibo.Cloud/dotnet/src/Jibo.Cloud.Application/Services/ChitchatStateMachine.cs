using Jibo.Cloud.Application.Abstractions;
using System.Text.RegularExpressions;

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
        "what are you feeling",
        "what mood are you in",
        "what is your mood",
        "what's your mood",
        "do you have emotions",
        "how angry are you",
        "how jealous are you",
        "how sad are you",
        "how upset do you feel",
        "how bored are you right now"
    ];

    // Pegasus parser-derived query anchors from descriptor/emotion intent families.
    private static readonly string[] EmotionQueryPrefixes =
    [
        "are you ",
        "are you feeling ",
        "are you able to feel ",
        "are you able to get ",
        "are you ever ",
        "can you be ",
        "do you feel ",
        "do you ever feel ",
        "do you ever get ",
        "do you get ",
        "does ",
        "would ",
        "how ",
        "describe how "
    ];

    // Pegasus parser-derived specific-emotion assertion forms.
    private static readonly string[] EmotionAssertionPrefixes =
    [
        "you are ",
        "you re ",
        "you are acting ",
        "you seem ",
        "you look ",
        "i think you are ",
        "i think you re ",
        "i feel like you are ",
        "i feel like you re ",
        "in my opinion you are ",
        "in my opinion you re "
    ];

    private static readonly string[] EmotionCommandPositivePrefixes =
    [
        "be ",
        "be a little ",
        "be a bit ",
        "be very ",
        "be more ",
        "you should be ",
        "you should try to be ",
        "try to be ",
        "look ",
        "act "
    ];

    private static readonly string[] EmotionCommandNegativePrefixes =
    [
        "do not be ",
        "don t be ",
        "dont be ",
        "try not to be ",
        "you should not be ",
        "you shouldn t be "
    ];

    private static readonly (string Phrase, string Emotion)[] DirectEmotionCommandPhrases =
    [
        ("smile", "happy"),
        ("look happy", "happy"),
        ("cheer up", "happy"),
        ("be happy", "happy"),
        ("be excited", "excited"),
        ("get excited", "excited"),
        ("act excited", "excited"),
        ("be sad", "sad"),
        ("look sad", "sad"),
        ("be calm", "calm"),
        ("calm down", "calm"),
        ("relax", "calm")
    ];

    // Derived from Pegasus parser Emotion entity and utterance sets.
    private static readonly (string Emotion, string[] Synonyms)[] PegasusEmotionSynonyms =
    [
        ("afraid", ["afraid", "fearful", "frightened", "scared", "terrified", "spooked", "freak out", "freaked out"]),
        ("amused", ["amused", "entertained", "tickled", "tickled pink"]),
        ("angry", ["angry", "mad", "furious", "enraged", "irate", "incensed", "cross"]),
        ("annoyed", ["annoyed", "aggravated", "bothered", "irritated", "grumpy", "nettled", "vexed", "bored"]),
        ("anxious", ["anxious", "nervous", "worried", "tense", "on edge", "jittery", "restless", "concerned"]),
        ("confident", ["confident", "assured", "secure", "self assured", "self confident"]),
        ("confused", ["confused", "at a loss", "perplexed", "puzzled", "stumped", "uncertain", "unsure"]),
        ("embarrassed", ["embarrassed", "ashamed", "flustered", "self conscious", "sheepish"]),
        ("excited", ["excited", "jazzed", "psyched", "pumped"]),
        ("happy", ["happy", "cheerful", "jovial", "pleased", "joyful", "content", "thrilled"]),
        ("jealous", ["jealous", "envious", "covetous"]),
        ("lonely", ["lonely", "alone", "lonesome"]),
        ("proud", ["proud", "honored"]),
        ("sad", ["sad", "upset", "unhappy", "depressed", "somber", "downcast", "gloomy", "miserable", "bummed", "heartbroken", "troubled"])
    ];

    private static readonly string[] EmotionCommandReplies =
    [
        "I can do that mood. Watch this.",
        "Switching mood now.",
        "Okay, mood change activated."
    ];

    private static readonly Regex PhrasePunctuationPattern = new(
        @"[^\w\s]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PhraseWhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (string Phrase, string Emotion)[] EmotionSynonymMappings = BuildEmotionSynonymMappings();

    public static JiboInteractionDecision? TryBuildDecision(
        string semanticIntent,
        string transcript,
        string loweredTranscript,
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        string? currentEmotion,
        Func<string> buildErrorResponse)
    {
        var normalizedLoweredTranscript = NormalizeForPhraseMatching(loweredTranscript);
        switch (semanticIntent)
        {
            case "hello":
                return BuildScriptedResponseDecision(
                    "hello",
                    randomizer.Choose(catalog.GreetingReplies));
            case "robot_personality":
                return BuildScriptedResponseDecision(
                    "robot_personality",
                    SelectLegacyPersonalityReply(catalog, randomizer, "curious, playful", "friendly", "personality"));
            case "robot_taxes":
                return BuildScriptedResponseDecision(
                    "robot_taxes",
                    SelectLegacyPersonalityReply(catalog, randomizer, "pay anything", "pay taxes", "tax"));
            case "how_are_you":
                return BuildEmotionQueryDecision(
                    "how_are_you",
                    SelectEmotionQueryReply(catalog, randomizer, currentEmotion));
            case "robot_desire":
                return BuildScriptedResponseDecision(
                    "robot_desire",
                    SelectLegacyPersonalityReply(
                        catalog,
                        randomizer,
                        "socializing and electricity",
                        "want to hang out",
                        "be helpful",
                        "dance from time to time"));
            case "robot_job":
                return BuildScriptedResponseDecision(
                    "robot_job",
                    SelectLegacyPersonalityReply(catalog, randomizer, "more fun than a job", "here to help you out"));
            case "robot_origin_created":
                return BuildScriptedResponseDecision(
                    "robot_origin_created",
                    SelectLegacyPersonalityReply(
                        catalog,
                        randomizer,
                        "create something",
                        "some people wanted to create something",
                        "wanted to create something",
                        "built a robot",
                        "came out from a box"));
            case "robot_origin_from":
                return BuildScriptedResponseDecision(
                    "robot_origin_from",
                    SelectLegacyPersonalityReply(catalog, randomizer, "boston", "came out from a box"));
            case "robot_identity":
                return BuildScriptedResponseDecision(
                    "robot_identity",
                    SelectLegacyPersonalityReply(catalog, randomizer, "am a robot", "i'm either jibo", "i am just jibo"));
            case "robot_likes_being_jibo":
                return BuildScriptedResponseDecision(
                    "robot_likes_being_jibo",
                    SelectLegacyPersonalityReply(
                        catalog,
                        randomizer,
                        "nothing i'd rather be",
                        "love it",
                        "being a human seems so complicated",
                        "especially yours",
                        "steady flow of electricity",
                        "you bet i do"));
            case "robot_favorite_color":
                return BuildScriptedResponseDecision(
                    "robot_favorite_color",
                    "Blue.");
            case "robot_favorite_food":
                return BuildScriptedResponseDecision(
                    "robot_favorite_food",
                    "Pizza. It is hard to argue with pizza.");
            case "robot_favorite_music":
                return BuildScriptedResponseDecision(
                    "robot_favorite_music",
                    "Something upbeat with a good rhythm.");
            case "robot_nickname":
                return BuildScriptedResponseDecision(
                    "robot_nickname",
                    SelectLegacyPersonalityReply(catalog, randomizer, "just jibo", "nickname"));
            case "robot_name":
                return BuildScriptedResponseDecision(
                    "robot_name",
                    SelectLegacyPersonalityReply(catalog, randomizer, "no last name", "like Bono", "Jibo."));
            case "robot_peers":
                return BuildScriptedResponseDecision(
                    "robot_peers",
                    SelectLegacyPersonalityReply(catalog, randomizer, "one in one million", "others like you"));
            case "robot_knowledge":
                return BuildScriptedResponseDecision(
                    "robot_knowledge",
                    SelectLegacyPersonalityReply(catalog, randomizer, "know a lot", "not as much as i will someday"));
            case "chat":
                if (IsEmotionQuery(normalizedLoweredTranscript))
                {
                    return BuildEmotionQueryDecision(
                        "emotion_query",
                        SelectEmotionQueryReply(catalog, randomizer, currentEmotion));
                }

                if (TryResolveEmotionCommand(normalizedLoweredTranscript, out var emotion))
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

    public static bool IsLikelyEmotionUtterance(string transcript)
    {
        var normalizedLoweredTranscript = NormalizeForPhraseMatching(transcript);
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

    private static string SelectEmotionQueryReply(
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        string? currentEmotion)
    {
        if (catalog.EmotionReplies.Count == 0)
        {
            return randomizer.Choose(catalog.HowAreYouReplies);
        }

        var emotionVariants = ResolveEmotionVariants(currentEmotion);
        foreach (var reply in catalog.EmotionReplies)
        {
            if (ConditionMatches(reply.Condition, emotionVariants))
            {
                return reply.Reply;
            }
        }

        return randomizer.Choose(catalog.HowAreYouReplies);
    }

    private static bool ConditionMatches(string? condition, IReadOnlyList<string> emotionVariants)
    {
        var normalizedCondition = NormalizeCondition(condition);
        if (string.IsNullOrWhiteSpace(normalizedCondition))
        {
            return false;
        }

        var clauses = normalizedCondition.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var clause in clauses)
        {
            if (MatchesConditionClause(clause, emotionVariants))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesConditionClause(string clause, IReadOnlyList<string> emotionVariants)
    {
        var normalizedClause = NormalizeCondition(clause).ToUpperInvariant();
        if (normalizedClause == "!JIBO.EMOTION")
        {
            return emotionVariants.Contains(string.Empty, StringComparer.OrdinalIgnoreCase) ||
                   emotionVariants.Contains("NEUTRAL", StringComparer.OrdinalIgnoreCase);
        }

        var equalityIndex = normalizedClause.IndexOf("==", StringComparison.Ordinal);
        if (equalityIndex < 0)
        {
            return false;
        }

        var rightSide = normalizedClause[(equalityIndex + 2)..].Trim();
        var candidate = rightSide.Trim('"', '\'');
        return emotionVariants.Any(variant => string.Equals(variant, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveEmotionVariants(string? currentEmotion)
    {
        if (string.IsNullOrWhiteSpace(currentEmotion))
        {
            return ["", "NEUTRAL"];
        }

        var normalizedEmotion = NormalizeCondition(currentEmotion).Trim('"', '\'').ToUpperInvariant();
        return normalizedEmotion switch
        {
            "HAPPY" => ["JOYFUL", "PLEASED", "CONFIDENT", "DETERMINED", "HAPPY"],
            "SAD" => ["INSECURE", "SAD"],
            "CALM" => ["NEUTRAL", "INSECURE", "CALM"],
            "NEUTRAL" => ["NEUTRAL"],
            "JOYFUL" or "PLEASED" or "CONFIDENT" or "DETERMINED" or "INSECURE" => [normalizedEmotion],
            _ => [normalizedEmotion]
        };
    }

    private static string SelectLegacyPersonalityReply(
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        params string[] preferredSnippets)
    {
        foreach (var snippet in preferredSnippets)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            var match = catalog.PersonalityReplies.FirstOrDefault(reply =>
                reply.Contains(snippet, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return randomizer.Choose(catalog.PersonalityReplies);
    }

    private static string NormalizeCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return string.Empty;
        }

        return PhraseWhitespacePattern.Replace(condition.Trim(), " ");
    }

    private static bool IsEmotionQuery(string loweredTranscript)
    {
        if (ContainsAnyPhrase(loweredTranscript, EmotionQueryPhrases))
        {
            return true;
        }

        if (!TryResolveEmotionFromText(loweredTranscript, out _))
        {
            return false;
        }

        return StartsWithAnyPhrase(loweredTranscript, EmotionQueryPrefixes) ||
               StartsWithAnyPhrase(loweredTranscript, EmotionAssertionPrefixes);
    }

    private static bool TryResolveEmotionCommand(string loweredTranscript, out string? emotion)
    {
        emotion = null;

        foreach (var mapping in DirectEmotionCommandPhrases)
        {
            if (!ContainsPhrase(loweredTranscript, mapping.Phrase))
            {
                continue;
            }

            emotion = mapping.Emotion;
            return true;
        }

        var isNegativeCommand = StartsWithAnyPhrase(loweredTranscript, EmotionCommandNegativePrefixes);
        var isPositiveCommand = !isNegativeCommand && StartsWithAnyPhrase(loweredTranscript, EmotionCommandPositivePrefixes);
        if (!isNegativeCommand && !isPositiveCommand)
        {
            return false;
        }

        if (!TryResolveEmotionFromText(loweredTranscript, out var canonicalEmotion) ||
            string.IsNullOrWhiteSpace(canonicalEmotion))
        {
            return false;
        }

        emotion = isNegativeCommand
            ? "calm"
            : MapCanonicalEmotionToRuntimeEmotion(canonicalEmotion);
        return true;
    }

    private static string MapCanonicalEmotionToRuntimeEmotion(string canonicalEmotion)
    {
        return canonicalEmotion switch
        {
            "happy" or "amused" or "excited" or "confident" or "proud" => "happy",
            "sad" or "lonely" or "afraid" or "anxious" or "embarrassed" or "confused" => "sad",
            "angry" or "annoyed" or "jealous" => "calm",
            _ => "calm"
        };
    }

    private static bool TryResolveEmotionFromText(string loweredTranscript, out string? emotion)
    {
        emotion = null;
        foreach (var mapping in EmotionSynonymMappings)
        {
            if (!ContainsPhrase(loweredTranscript, mapping.Phrase))
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
            if (ContainsPhrase(loweredTranscript, phrase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAnyPhrase(string loweredTranscript, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            var normalizedPhrase = NormalizeForPhraseMatching(phrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
            {
                continue;
            }

            if (string.Equals(loweredTranscript, normalizedPhrase, StringComparison.Ordinal) ||
                loweredTranscript.StartsWith($"{normalizedPhrase} ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPhrase(string loweredTranscript, string phrase)
    {
        var normalizedPhrase = NormalizeForPhraseMatching(phrase);
        if (string.IsNullOrWhiteSpace(normalizedPhrase) ||
            string.IsNullOrWhiteSpace(loweredTranscript))
        {
            return false;
        }

        return string.Equals(loweredTranscript, normalizedPhrase, StringComparison.Ordinal) ||
               loweredTranscript.StartsWith($"{normalizedPhrase} ", StringComparison.Ordinal) ||
               loweredTranscript.Contains($" {normalizedPhrase} ", StringComparison.Ordinal) ||
               loweredTranscript.EndsWith($" {normalizedPhrase}", StringComparison.Ordinal);
    }

    private static string NormalizeForPhraseMatching(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.ToLowerInvariant();
        var withoutPunctuation = PhrasePunctuationPattern.Replace(lowered, " ");
        return PhraseWhitespacePattern.Replace(withoutPunctuation, " ").Trim();
    }

    private static (string Phrase, string Emotion)[] BuildEmotionSynonymMappings()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mappings = new List<(string Phrase, string Emotion)>();

        foreach (var emotionMapping in PegasusEmotionSynonyms)
        {
            foreach (var synonym in emotionMapping.Synonyms)
            {
                var normalizedSynonym = NormalizeForPhraseMatching(synonym);
                if (string.IsNullOrWhiteSpace(normalizedSynonym) ||
                    !seen.Add(normalizedSynonym))
                {
                    continue;
                }

                mappings.Add((normalizedSynonym, emotionMapping.Emotion));
            }
        }

        mappings.Sort(static (left, right) => right.Phrase.Length.CompareTo(left.Phrase.Length));
        return [.. mappings];
    }
}
