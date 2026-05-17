using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

internal static class PersonalReportOrchestrator
{
    internal const string StateMetadataKey = "personalReportState";
    internal const string NoMatchCountMetadataKey = "personalReportNoMatchCount";
    internal const string NoInputCountMetadataKey = "personalReportNoInputCount";
    internal const string UserNameMetadataKey = "personalReportUserName";
    internal const string UserVerifiedMetadataKey = "personalReportUserVerified";
    internal const string WeatherEnabledMetadataKey = "personalReportWeatherEnabled";
    internal const string CalendarEnabledMetadataKey = "personalReportCalendarEnabled";
    internal const string CommuteEnabledMetadataKey = "personalReportCommuteEnabled";
    internal const string NewsEnabledMetadataKey = "personalReportNewsEnabled";
    internal const string LastServiceErrorMetadataKey = "personalReportLastServiceError";

    internal const string IdleState = "idle";
    private const string AwaitingOptInState = "awaiting_opt_in";
    private const string AwaitingIdentityConfirmationState = "awaiting_identity_confirmation";
    private const string AwaitingIdentityNameState = "awaiting_identity_name";

    private const int MaxNoMatchCount = 2;
    private const int MaxNoInputCount = 2;

    private static readonly string[] CancelPhrases =
    [
        "cancel",
        "stop",
        "never mind",
        "nevermind",
        "forget it"
    ];

    private static readonly string[] AffirmativePhrases =
    [
        "yes",
        "yeah",
        "yep",
        "yup",
        "sure",
        "ok",
        "okay",
        "do it",
        "please do",
        "go ahead"
    ];

    private static readonly string[] NegativePhrases =
    [
        "no",
        "nah",
        "nope",
        "not now",
        "maybe later"
    ];

    public static async Task<JiboInteractionDecision?> TryBuildDecisionAsync(
        TurnContext turn,
        string semanticIntent,
        string transcript,
        string loweredTranscript,
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        IPersonalMemoryStore personalMemoryStore,
        Func<TurnContext, string, CancellationToken, Task<JiboInteractionDecision>> buildWeatherDecisionAsync,
        Func<TurnContext, PersonalMemoryTenantScope> tenantScopeResolver,
        CancellationToken cancellationToken)
    {
        var state = ReadState(turn);
        var isActiveState = !string.Equals(state, IdleState, StringComparison.OrdinalIgnoreCase);
        if (!isActiveState && !string.Equals(semanticIntent, "personal_report", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var toggles = ApplyInlineToggleHints(
            ReadServiceToggles(turn),
            loweredTranscript,
            out var inlineToggleSummary);

        if (ContainsAnyPhrase(loweredTranscript, CancelPhrases))
        {
            return BuildCancelledDecision(toggles);
        }

        if (!isActiveState)
        {
            var contextUpdates = BuildContextUpdates(
                AwaitingOptInState,
                noMatchCount: 0,
                noInputCount: 0,
                toggles,
                userName: ReadString(turn, UserNameMetadataKey),
                userVerified: ReadBool(turn, UserVerifiedMetadataKey) ?? false,
                lastServiceError: string.Empty);

            var reply = string.IsNullOrWhiteSpace(inlineToggleSummary)
                ? "Would you like your personal report now?"
                : $"{inlineToggleSummary} Would you like your personal report now?";

            return new JiboInteractionDecision(
                "personal_report_opt_in",
                reply,
                ContextUpdates: contextUpdates);
        }

        if (string.IsNullOrWhiteSpace(loweredTranscript))
        {
            return BuildNoInputDecision(turn, state, toggles);
        }

        switch (state)
        {
            case AwaitingOptInState:
                if (IsAffirmativeReply(loweredTranscript))
                {
                    var scope = tenantScopeResolver(turn);
                    var knownName = ReadString(turn, UserNameMetadataKey) ?? personalMemoryStore.GetName(scope);
                    if (!string.IsNullOrWhiteSpace(knownName))
                    {
                        return new JiboInteractionDecision(
                            "personal_report_verify_user",
                            $"I think this is {knownName}. Is that right?",
                            ContextUpdates: BuildContextUpdates(
                                AwaitingIdentityConfirmationState,
                                noMatchCount: 0,
                                noInputCount: 0,
                                toggles,
                                userName: knownName,
                                userVerified: false,
                                lastServiceError: string.Empty));
                    }

                    return new JiboInteractionDecision(
                        "personal_report_request_name",
                        "Who is this?",
                        ContextUpdates: BuildContextUpdates(
                            AwaitingIdentityNameState,
                            noMatchCount: 0,
                            noInputCount: 0,
                            toggles,
                            userName: null,
                            userVerified: false,
                            lastServiceError: string.Empty));
                }

                if (IsNegativeReply(loweredTranscript))
                {
                    return BuildDeclinedDecision(toggles);
                }

                if (!string.IsNullOrWhiteSpace(inlineToggleSummary))
                {
                    return new JiboInteractionDecision(
                        "personal_report_opt_in",
                        $"{inlineToggleSummary} Would you like your personal report now?",
                        ContextUpdates: BuildContextUpdates(
                            AwaitingOptInState,
                            noMatchCount: 0,
                            noInputCount: 0,
                            toggles,
                            userName: ReadString(turn, UserNameMetadataKey),
                            userVerified: false,
                            lastServiceError: string.Empty));
                }

                return BuildNoMatchDecision(
                    turn,
                    state,
                    "Please say yes to start your personal report, or no to skip it.",
                    toggles,
                    userName: ReadString(turn, UserNameMetadataKey),
                    userVerified: false);

            case AwaitingIdentityConfirmationState:
            {
                var currentName = ReadString(turn, UserNameMetadataKey);
                if (string.IsNullOrWhiteSpace(currentName))
                {
                    return new JiboInteractionDecision(
                        "personal_report_request_name",
                        "Who is this?",
                        ContextUpdates: BuildContextUpdates(
                            AwaitingIdentityNameState,
                            noMatchCount: 0,
                            noInputCount: 0,
                            toggles,
                            userName: null,
                            userVerified: false,
                            lastServiceError: string.Empty));
                }

                if (IsAffirmativeReply(loweredTranscript))
                {
                    return await BuildDeliveredReportDecisionAsync(
                        turn,
                        catalog,
                        randomizer,
                        toggles,
                        currentName,
                        buildWeatherDecisionAsync,
                        cancellationToken);
                }

                if (IsNegativeReply(loweredTranscript))
                {
                    return new JiboInteractionDecision(
                        "personal_report_request_name",
                        "Okay, who is this?",
                        ContextUpdates: BuildContextUpdates(
                            AwaitingIdentityNameState,
                            noMatchCount: 0,
                            noInputCount: 0,
                            toggles,
                            userName: null,
                            userVerified: false,
                            lastServiceError: string.Empty));
                }

                return BuildNoMatchDecision(
                    turn,
                    state,
                    $"Please answer yes or no. Is this {currentName}?",
                    toggles,
                    userName: currentName,
                    userVerified: false);
            }

            case AwaitingIdentityNameState:
            {
                var parsedName = TryExtractName(loweredTranscript);
                if (string.IsNullOrWhiteSpace(parsedName))
                {
                    return BuildNoMatchDecision(
                        turn,
                        state,
                        "Tell me your name like this: my name is Alex.",
                        toggles,
                        userName: null,
                        userVerified: false);
                }

                personalMemoryStore.SetName(tenantScopeResolver(turn), parsedName);
                return await BuildDeliveredReportDecisionAsync(
                    turn,
                    catalog,
                    randomizer,
                    toggles,
                    parsedName,
                    buildWeatherDecisionAsync,
                    cancellationToken);
            }

            default:
                return BuildDeclinedDecision(toggles);
        }
    }

    private static async Task<JiboInteractionDecision> BuildDeliveredReportDecisionAsync(
        TurnContext turn,
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        PersonalReportServiceToggles toggles,
        string userName,
        Func<TurnContext, string, CancellationToken, Task<JiboInteractionDecision>> buildWeatherDecisionAsync,
        CancellationToken cancellationToken)
    {
        var reportSections = new List<string>
        {
            RenderPersonalReportTemplate(
                ChoosePersonalReportTemplate(
                    catalog.PersonalReportKickOffReplies,
                    "Okay. Here's your personal report."),
                userName)
        };
        var serviceError = string.Empty;

        if (toggles.WeatherEnabled)
        {
            reportSections.Add("First, your weather.");
            var weatherDecision = await buildWeatherDecisionAsync(turn, "weather", cancellationToken);
            reportSections.Add(weatherDecision.ReplyText);
            if (IsWeatherErrorReply(weatherDecision.ReplyText))
            {
                serviceError = "weather";
            }
        }

        if (toggles.CalendarEnabled)
        {
            reportSections.Add(randomizer.Choose(catalog.CalendarReplies));
        }

        if (toggles.CommuteEnabled)
        {
            reportSections.Add(randomizer.Choose(catalog.CommuteReplies));
        }

        if (toggles.NewsEnabled)
        {
            reportSections.Add(randomizer.Choose(catalog.NewsBriefings));
        }

        reportSections.Add(
            RenderPersonalReportTemplate(
                ChoosePersonalReportTemplate(
                    catalog.PersonalReportOutroReplies,
                    "And that's your report for the day. I hope you had as much fun as I did."),
                userName));

        return new JiboInteractionDecision(
            "personal_report_delivered",
            string.Join(" ", reportSections),
            ContextUpdates: BuildContextUpdates(
                IdleState,
                noMatchCount: 0,
                noInputCount: 0,
                toggles,
                userName,
                userVerified: true,
                lastServiceError: serviceError));
    }

    private static JiboInteractionDecision BuildNoInputDecision(
        TurnContext turn,
        string state,
        PersonalReportServiceToggles toggles)
    {
        var noInputCount = Math.Max(0, ReadInt(turn, NoInputCountMetadataKey)) + 1;
        if (noInputCount >= MaxNoInputCount)
        {
            return BuildDeclinedDecision(toggles);
        }

        return new JiboInteractionDecision(
            "personal_report_no_input",
            "I am still here. Do you want your personal report?",
            ContextUpdates: BuildContextUpdates(
                state,
                noMatchCount: ReadInt(turn, NoMatchCountMetadataKey),
                noInputCount,
                toggles,
                userName: ReadString(turn, UserNameMetadataKey),
                userVerified: ReadBool(turn, UserVerifiedMetadataKey) ?? false,
                lastServiceError: string.Empty));
    }

    private static JiboInteractionDecision BuildNoMatchDecision(
        TurnContext turn,
        string state,
        string repromptText,
        PersonalReportServiceToggles toggles,
        string? userName,
        bool userVerified)
    {
        var noMatchCount = Math.Max(0, ReadInt(turn, NoMatchCountMetadataKey)) + 1;
        if (noMatchCount >= MaxNoMatchCount)
        {
            return BuildDeclinedDecision(toggles);
        }

        return new JiboInteractionDecision(
            "personal_report_no_match",
            repromptText,
            ContextUpdates: BuildContextUpdates(
                state,
                noMatchCount,
                noInputCount: 0,
                toggles,
                userName,
                userVerified,
                lastServiceError: string.Empty));
    }

    private static JiboInteractionDecision BuildDeclinedDecision(PersonalReportServiceToggles toggles)
    {
        return new JiboInteractionDecision(
            "personal_report_declined",
            "No problem. We can do your personal report another time.",
            ContextUpdates: BuildContextUpdates(
                IdleState,
                noMatchCount: 0,
                noInputCount: 0,
                toggles,
                userName: null,
                userVerified: false,
                lastServiceError: string.Empty));
    }

    private static JiboInteractionDecision BuildCancelledDecision(PersonalReportServiceToggles toggles)
    {
        return new JiboInteractionDecision(
            "personal_report_cancelled",
            "Okay, canceling personal report.",
            ContextUpdates: BuildContextUpdates(
                IdleState,
                noMatchCount: 0,
                noInputCount: 0,
                toggles,
                userName: null,
                userVerified: false,
                lastServiceError: string.Empty));
    }

    private static IDictionary<string, object?> BuildContextUpdates(
        string state,
        int noMatchCount,
        int noInputCount,
        PersonalReportServiceToggles toggles,
        string? userName,
        bool userVerified,
        string lastServiceError)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [StateMetadataKey] = state,
            [NoMatchCountMetadataKey] = noMatchCount,
            [NoInputCountMetadataKey] = noInputCount,
            [UserNameMetadataKey] = userName,
            [UserVerifiedMetadataKey] = userVerified,
            [WeatherEnabledMetadataKey] = toggles.WeatherEnabled,
            [CalendarEnabledMetadataKey] = toggles.CalendarEnabled,
            [CommuteEnabledMetadataKey] = toggles.CommuteEnabled,
            [NewsEnabledMetadataKey] = toggles.NewsEnabled,
            [LastServiceErrorMetadataKey] = lastServiceError
        };
    }

    private static bool IsAffirmativeReply(string loweredTranscript)
    {
        return ContainsAnyPhrase(loweredTranscript, AffirmativePhrases);
    }

    private static bool IsNegativeReply(string loweredTranscript)
    {
        return ContainsAnyPhrase(loweredTranscript, NegativePhrases);
    }

    private static bool ContainsAnyPhrase(string loweredTranscript, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            if (string.Equals(loweredTranscript, phrase, StringComparison.Ordinal) ||
                loweredTranscript.StartsWith($"{phrase} ", StringComparison.Ordinal) ||
                loweredTranscript.Contains($" {phrase}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWeatherErrorReply(string replyText)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return false;
        }

        return replyText.Contains("couldn't fetch the weather", StringComparison.OrdinalIgnoreCase) ||
               replyText.Contains("weather service is connected", StringComparison.OrdinalIgnoreCase);
    }

    private static PersonalReportServiceToggles ReadServiceToggles(TurnContext turn)
    {
        return new PersonalReportServiceToggles(
            ReadBool(turn, WeatherEnabledMetadataKey) ?? true,
            ReadBool(turn, CalendarEnabledMetadataKey) ?? true,
            ReadBool(turn, CommuteEnabledMetadataKey) ?? true,
            ReadBool(turn, NewsEnabledMetadataKey) ?? true);
    }

    private static PersonalReportServiceToggles ApplyInlineToggleHints(
        PersonalReportServiceToggles toggles,
        string loweredTranscript,
        out string summary)
    {
        summary = string.Empty;
        var updated = toggles;

        updated = ApplyToggleHint(updated, loweredTranscript, "weather", static value => value with { WeatherEnabled = false }, static value => value with { WeatherEnabled = true });
        updated = ApplyToggleHint(updated, loweredTranscript, "calendar", static value => value with { CalendarEnabled = false }, static value => value with { CalendarEnabled = true });
        updated = ApplyToggleHint(updated, loweredTranscript, "commute", static value => value with { CommuteEnabled = false }, static value => value with { CommuteEnabled = true });
        updated = ApplyToggleHint(updated, loweredTranscript, "news", static value => value with { NewsEnabled = false }, static value => value with { NewsEnabled = true });

        var changes = new List<string>();
        if (updated.WeatherEnabled != toggles.WeatherEnabled)
        {
            changes.Add(updated.WeatherEnabled ? "including weather" : "skipping weather");
        }

        if (updated.CalendarEnabled != toggles.CalendarEnabled)
        {
            changes.Add(updated.CalendarEnabled ? "including calendar" : "skipping calendar");
        }

        if (updated.CommuteEnabled != toggles.CommuteEnabled)
        {
            changes.Add(updated.CommuteEnabled ? "including commute" : "skipping commute");
        }

        if (updated.NewsEnabled != toggles.NewsEnabled)
        {
            changes.Add(updated.NewsEnabled ? "including news" : "skipping news");
        }

        if (changes.Count > 0)
        {
            summary = $"Got it, {string.Join(", ", changes)}.";
        }

        return updated;
    }

    private static PersonalReportServiceToggles ApplyToggleHint(
        PersonalReportServiceToggles toggles,
        string loweredTranscript,
        string serviceLabel,
        Func<PersonalReportServiceToggles, PersonalReportServiceToggles> disable,
        Func<PersonalReportServiceToggles, PersonalReportServiceToggles> enable)
    {
        if (loweredTranscript.Contains($"without {serviceLabel}", StringComparison.Ordinal) ||
            loweredTranscript.Contains($"skip {serviceLabel}", StringComparison.Ordinal) ||
            loweredTranscript.Contains($"no {serviceLabel}", StringComparison.Ordinal))
        {
            return disable(toggles);
        }

        if (loweredTranscript.Contains($"with {serviceLabel}", StringComparison.Ordinal) ||
            loweredTranscript.Contains($"include {serviceLabel}", StringComparison.Ordinal))
        {
            return enable(toggles);
        }

        return toggles;
    }

    private static string ReadState(TurnContext turn)
    {
        return ReadString(turn, StateMetadataKey) ?? IdleState;
    }

    private static string? ReadString(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            _ => value.ToString()
        };
    }

    private static bool? ReadBool(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement json when json.ValueKind == JsonValueKind.String && bool.TryParse(json.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int ReadInt(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int integer => integer,
            long whole when whole <= int.MaxValue && whole >= int.MinValue => (int)whole,
            string text when int.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var parsed) => parsed,
            JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? TryExtractName(string loweredTranscript)
    {
        var normalized = NameNoiseRegex.Replace(loweredTranscript, " ")
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var prefixes = new[]
        {
            "my name is ",
            "it is ",
            "it s ",
            "it's ",
            "i am ",
            "im "
        };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = normalized[prefix.Length..].Trim();
            return NormalizeNameCandidate(candidate);
        }

        return NormalizeNameCandidate(normalized);
    }

    private static string? NormalizeNameCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var cleaned = NameNoiseRegex.Replace(candidate, " ")
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (cleaned.Length < 2 || cleaned.Length > 32)
        {
            return null;
        }

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4)
        {
            return null;
        }

        if (words.Any(static word => word.Any(char.IsDigit)))
        {
            return null;
        }

        return cleaned;
    }

    private static readonly Regex NameNoiseRegex = new("[^a-zA-Z\\-\\s']", RegexOptions.Compiled);

    private readonly record struct PersonalReportServiceToggles(
        bool WeatherEnabled,
        bool CalendarEnabled,
        bool CommuteEnabled,
        bool NewsEnabled);

    private static string ChoosePersonalReportTemplate(
        IReadOnlyList<string> templates,
        string fallback)
    {
        var usableTemplates = templates
            .Where(static template => !string.IsNullOrWhiteSpace(template) && !template.Contains("${dt.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (usableTemplates.Length == 0)
        {
            return fallback;
        }

        var speakerAwareTemplate = usableTemplates.FirstOrDefault(static template =>
            template.Contains("${speaker}", StringComparison.OrdinalIgnoreCase));
        return speakerAwareTemplate ?? usableTemplates[0];
    }

    private static string RenderPersonalReportTemplate(string template, string userName)
    {
        return template
            .Replace("${speaker}", userName, StringComparison.OrdinalIgnoreCase)
            .Replace("${speaker}'s", $"{userName}'s", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }
}
