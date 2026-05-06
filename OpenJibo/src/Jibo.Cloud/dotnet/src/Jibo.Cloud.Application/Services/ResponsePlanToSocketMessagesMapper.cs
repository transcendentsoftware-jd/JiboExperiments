using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class ResponsePlanToSocketMessagesMapper
{
    public static IReadOnlyList<SocketReplyPlan> Map(ResponsePlan plan, TurnContext turn, CloudSession session,
        bool emitSkillActions)
    {
        var speak = plan.Actions.OfType<SpeakAction>().FirstOrDefault();
        var skill = plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault();
        var messageType = ReadAttribute(turn, "messageType");
        var transId = turn.Attributes.TryGetValue("transID", out var transIdValue)
            ? transIdValue?.ToString() ?? string.Empty
            : session.LastTransId ?? string.Empty;
        var transcript = turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty;
        var clientIntent = ReadAttribute(turn, "clientIntent");
        var rules = ReadRules(turn, messageType);
        var yesNoRule = ReadYesNoRule(turn);
        var isYesNoTurn = !string.IsNullOrWhiteSpace(yesNoRule);
        var isYesNoIntent = string.Equals(plan.IntentName, "yes", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(plan.IntentName, "no", StringComparison.OrdinalIgnoreCase);
        var isWordOfDayLaunch = string.Equals(plan.IntentName, "word_of_the_day", StringComparison.OrdinalIgnoreCase);
        var isWordOfDayGuess =
            string.Equals(plan.IntentName, "word_of_the_day_guess", StringComparison.OrdinalIgnoreCase);
        var isRadioLaunch = string.Equals(plan.IntentName, "radio", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(plan.IntentName, "radio_genre", StringComparison.OrdinalIgnoreCase);
        var isStopCommand = string.Equals(plan.IntentName, "stop", StringComparison.OrdinalIgnoreCase);
        var isVolumeControl = string.Equals(plan.IntentName, "volume_up", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(plan.IntentName, "volume_down", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(plan.IntentName, "volume_to_value", StringComparison.OrdinalIgnoreCase);
        var isSettingsLaunch = string.Equals(skill?.SkillName, "@be/settings", StringComparison.OrdinalIgnoreCase);
        var isGlobalCommand = isStopCommand || isVolumeControl;
        var isPhotoGalleryLaunch = string.Equals(plan.IntentName, "photo_gallery", StringComparison.OrdinalIgnoreCase);
        var isPhotoCreateLaunch = string.Equals(plan.IntentName, "snapshot", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(plan.IntentName, "photobooth", StringComparison.OrdinalIgnoreCase);
        var isClockSkillLaunch = string.Equals(skill?.SkillName, "@be/clock", StringComparison.OrdinalIgnoreCase);
        var isReportSkillLaunch = string.Equals(skill?.SkillName, "report-skill", StringComparison.OrdinalIgnoreCase);
        var localIntent = ReadSkillPayloadString(skill, "localIntent");
        var clockIntent = ReadSkillPayloadString(skill, "clockIntent");
        var clockDomain = ReadSkillPayloadString(skill, "domain");
        var timerHours = ReadSkillPayloadString(skill, "hours");
        var timerMinutes = ReadSkillPayloadString(skill, "minutes");
        var timerSeconds = ReadSkillPayloadString(skill, "seconds");
        var alarmTime = ReadSkillPayloadString(skill, "time");
        var alarmAmPm = ReadSkillPayloadString(skill, "ampm");
        var radioStation = ReadSkillPayloadString(skill, "station");
        var cloudSkill = ReadSkillPayloadString(skill, "cloudSkill");
        var globalIntent = ReadSkillPayloadString(skill, "globalIntent");
        var nluDomain = ReadSkillPayloadString(skill, "nluDomain");
        var volumeLevel = ReadSkillPayloadString(skill, "volumeLevel");
        var reportDate = ReadSkillPayloadString(skill, "date");
        var reportWeatherCondition = ReadSkillPayloadString(skill, "weatherCondition");
        var nluGuess = ReadClientEntity(turn, "guess");
        var wordOfDayGuess = ResolveWordOfDayGuess(turn, transcript, nluGuess);
        var outboundIntent = isGlobalCommand && !string.IsNullOrWhiteSpace(globalIntent)
            ? globalIntent
            : isWordOfDayLaunch
                ? "menu"
                : isRadioLaunch
                    ? "menu"
                    : isSettingsLaunch && !string.IsNullOrWhiteSpace(localIntent)
                        ? localIntent
                        : (isPhotoGalleryLaunch || isPhotoCreateLaunch) && !string.IsNullOrWhiteSpace(localIntent)
                            ? localIntent
                            : isClockSkillLaunch && !string.IsNullOrWhiteSpace(clockIntent)
                                ? clockIntent
                                : isReportSkillLaunch && !string.IsNullOrWhiteSpace(localIntent)
                                    ? localIntent
                                : isWordOfDayGuess
                                    ? "guess"
                                    : string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) &&
                                      !string.IsNullOrWhiteSpace(clientIntent)
                                        ? clientIntent
                                        : plan.IntentName ?? "unknown";
        var outboundAsrText = isWordOfDayGuess && !string.IsNullOrWhiteSpace(wordOfDayGuess)
            ? wordOfDayGuess
            : isWordOfDayLaunch
                ? string.Empty
                : isGlobalCommand
                    ? transcript
                    : isRadioLaunch
                        ? transcript
                        : isSettingsLaunch
                            ? transcript
                            : isPhotoGalleryLaunch || isPhotoCreateLaunch
                                ? transcript
                                : isClockSkillLaunch
                                    ? transcript
                                    : string.Equals(clientIntent, "guess", StringComparison.OrdinalIgnoreCase) &&
                                      !string.IsNullOrWhiteSpace(nluGuess)
                                        ? nluGuess
                                        : isYesNoTurn && isYesNoIntent
                                            ? transcript
                                            : string.Equals(messageType, "CLIENT_NLU",
                                                  StringComparison.OrdinalIgnoreCase) &&
                                              !string.IsNullOrWhiteSpace(clientIntent)
                                                ? clientIntent
                                                : transcript;
        var outboundRules = isWordOfDayLaunch
            ? ["word-of-the-day/menu"]
            : isGlobalCommand
                ? BuildGlobalCommandRules(rules)
                : isRadioLaunch
                    ? []
                    : isSettingsLaunch
                        ? string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase)
                            ? rules
                            : []
                        : isPhotoGalleryLaunch || isPhotoCreateLaunch
                            ? string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase)
                                ? rules
                                : []
                            : isClockSkillLaunch
                                ? string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase)
                                    ? rules
                                    : []
                                : isReportSkillLaunch
                                    ? []
                                : isWordOfDayGuess
                                    ? ["word-of-the-day/puzzle"]
                                    : isYesNoTurn && isYesNoIntent
                                        ? [yesNoRule!]
                                        : rules;
        var entities = ReadEntities(
            turn,
            messageType,
            isYesNoTurn && isYesNoIntent,
            ShouldIncludeCreateDomain(yesNoRule),
            isWordOfDayLaunch,
            isGlobalCommand,
            volumeLevel,
            isRadioLaunch,
            isWordOfDayGuess,
            wordOfDayGuess,
            radioStation,
            isClockSkillLaunch,
            clockDomain,
            clockIntent,
            timerHours,
            timerMinutes,
            timerSeconds,
            alarmTime,
            alarmAmPm,
            isReportSkillLaunch,
            reportDate,
            reportWeatherCondition);
        var listenMessage = new
        {
            type = "LISTEN",
            transID = transId,
            data = new
            {
                asr = new
                {
                    confidence = 0.95,
                    final = true,
                    text = outboundAsrText
                },
                nlu = BuildNluPayload(
                    outboundIntent,
                    outboundRules,
                    entities,
                    isWordOfDayLaunch ? "@be/word-of-the-day" :
                    isRadioLaunch ? "@be/radio" :
                    isSettingsLaunch ? "@be/settings" :
                    isPhotoGalleryLaunch ? "@be/gallery" :
                    isPhotoCreateLaunch ? "@be/create" :
                    isClockSkillLaunch ? "@be/clock" :
                    isReportSkillLaunch ? "report-skill" :
                    null,
                    isGlobalCommand ? nluDomain ?? "global_commands" : null),
                match = new
                {
                    intent = outboundIntent,
                    rule = outboundRules.FirstOrDefault() ?? string.Empty,
                    score = 0.95,
                    cloudSkill,
                    skipSurprises = true
                }
            }
        };

        var messages = new List<SocketReplyPlan>
        {
            new(JsonSerializer.Serialize(listenMessage)),
            new(JsonSerializer.Serialize(new
            {
                type = "EOS",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CreateHubMessageId(),
                transID = transId,
                data = new { }
            }))
        };

        if (isWordOfDayLaunch)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/word-of-the-day",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/word-of-the-day")),
                DelayMs: 125));
        }

        if (isRadioLaunch)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/radio",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/radio")),
                DelayMs: 125));
        }

        if (isStopCommand)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/idle",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/idle")),
                DelayMs: 125));
        }

        if (isSettingsLaunch &&
            !string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/settings",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/settings")),
                DelayMs: 125));
        }

        if (isClockSkillLaunch &&
            !string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) &&
            !IsLocalClockFollowUpTurn(rules))
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/clock",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/clock")),
                DelayMs: 125));
        }

        if ((isPhotoGalleryLaunch || isPhotoCreateLaunch) &&
            !string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase))
        {
            var skillId = isPhotoGalleryLaunch ? "@be/gallery" : "@be/create";
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    skillId,
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, skillId)),
                DelayMs: 125));
        }

        if (isReportSkillLaunch)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "report-skill",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                DelayMs: 75));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "report-skill")),
                DelayMs: 125));
        }

        if (emitSkillActions && speak is not null)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillPayload(plan, turn, transId, speak, skill)),
                DelayMs: 75));
        }

        return messages;
    }

    public static IReadOnlyList<SocketReplyPlan> MapFallback(CloudSession session, string transId,
        IReadOnlyList<string> rules)
    {
        return
        [
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "LISTEN",
                transID = transId,
                data = new
                {
                    asr = new
                    {
                        confidence = 0.95,
                        final = true,
                        text = string.Empty
                    },
                    nlu = new
                    {
                        confidence = 0.95,
                        intent = "heyJibo",
                        rules,
                        entities = new Dictionary<string, object?>()
                    },
                    match = new
                    {
                        intent = "heyJibo",
                        rule = rules.FirstOrDefault() ?? string.Empty,
                        score = 0.95,
                        skipSurprises = true
                    }
                }
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "EOS",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CreateHubMessageId(),
                transID = transId,
                data = new { }
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(BuildGenericFallbackSkillPayload(transId)), DelayMs: 75)
        ];
    }

    public static IReadOnlyList<SocketReplyPlan> MapCompletionOnly(string transId, string skillId, int delayMs = 0)
    {
        return
        [
            new SocketReplyPlan(JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, skillId)), delayMs)
        ];
    }

    public static IReadOnlyList<SocketReplyPlan> MapNoInput(string transId, IReadOnlyList<string> rules)
    {
        return
        [
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "LISTEN",
                transID = transId,
                data = new
                {
                    asr = new
                    {
                        confidence = 0.95,
                        final = true,
                        text = string.Empty
                    },
                    nlu = new
                    {
                        confidence = 0.95,
                        intent = string.Empty,
                        rules,
                        entities = new Dictionary<string, object?>()
                    }
                }
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "EOS",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CreateHubMessageId(),
                transID = transId,
                data = new { }
            }))
        ];
    }

    public static IReadOnlyList<SocketReplyPlan> MapNoInputAndRedirectToSkill(
        string transId,
        IReadOnlyList<string> rules,
        string skillId,
        int redirectDelayMs = 75)
    {
        var messages = new List<SocketReplyPlan>(MapNoInput(transId, rules))
        {
            new(JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    skillId,
                    string.Empty,
                    string.Empty,
                    [],
                    new Dictionary<string, object?>())),
                redirectDelayMs)
        };

        return messages;
    }

    private static IReadOnlyList<string> ReadRules(TurnContext turn, string? messageType)
    {
        var attributeName = string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase)
            ? "clientRules"
            : "listenRules";

        if (!turn.Attributes.TryGetValue(attributeName, out var value))
        {
            return [];
        }

        return value switch
        {
            IReadOnlyList<string> typedRules => typedRules,
            IEnumerable<string> rules => [.. rules.Where(rule => !string.IsNullOrWhiteSpace(rule))],
            _ => []
        };
    }

    private static object ReadEntities(
        TurnContext turn,
        string? messageType,
        bool yesNoTurn,
        bool includeCreateDomain,
        bool wordOfDayLaunch,
        bool globalCommand,
        string? volumeLevel,
        bool radioLaunch,
        bool wordOfDayGuess,
        string? guess,
        string? radioStation,
        bool clockSkillLaunch,
        string? clockDomain,
        string? clockIntent,
        string? timerHours,
        string? timerMinutes,
        string? timerSeconds,
        string? alarmTime,
        string? alarmAmPm,
        bool reportSkillLaunch,
        string? reportDate,
        string? reportWeatherCondition)
    {
        if (yesNoTurn)
        {
            if (!includeCreateDomain)
            {
                return new Dictionary<string, object?>();
            }

            return new Dictionary<string, object?>
            {
                ["domain"] = "create"
            };
        }

        if (wordOfDayLaunch)
        {
            return new Dictionary<string, object?>
            {
                ["domain"] = "word-of-the-day"
            };
        }

        if (globalCommand)
        {
            var entities = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(volumeLevel))
            {
                entities["volumeLevel"] = volumeLevel;
            }

            return entities;
        }

        if (radioLaunch)
        {
            var entities = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(radioStation))
            {
                entities["station"] = radioStation;
            }

            return entities;
        }

        if (clockSkillLaunch)
        {
            var entities = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(clockDomain))
            {
                entities["domain"] = clockDomain;
            }

            if (string.Equals(clockDomain, "timer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(timerHours + timerMinutes + timerSeconds))
            {
                entities["hours"] = timerHours ?? "0";
                entities["minutes"] = timerMinutes ?? "0";
                entities["seconds"] = timerSeconds ?? "null";
            }

            if (!string.Equals(clockDomain, "alarm", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(alarmTime) && string.IsNullOrWhiteSpace(alarmAmPm))) return entities;

            entities["time"] = alarmTime ?? string.Empty;
            entities["ampm"] = alarmAmPm ?? string.Empty;

            return entities;
        }

        if (reportSkillLaunch)
        {
            var entities = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(reportDate))
            {
                entities["date"] = reportDate;
            }

            if (!string.IsNullOrWhiteSpace(reportWeatherCondition))
            {
                entities["Weather"] = reportWeatherCondition;
            }

            return entities;
        }

        if (wordOfDayGuess)
        {
            return new Dictionary<string, object?>
            {
                ["guess"] = guess ?? string.Empty
            };
        }

        if (!string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) ||
            !turn.Attributes.TryGetValue("clientEntities", out var value) || value is null)
        {
            return new Dictionary<string, object?>();
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } jsonElement => jsonElement,
            IDictionary<string, object?> dictionary => dictionary,
            _ => new Dictionary<string, object?>()
        };
    }

    private static string? ReadYesNoRule(TurnContext turn)
    {
        return ReadRuleValues(turn)
            .FirstOrDefault(static rule =>
                string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIncludeCreateDomain(string? yesNoRule)
    {
        return string.Equals(yesNoRule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(yesNoRule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadRuleValues(TurnContext turn)
    {
        return ReadRuleValues(turn, "listenRules").Concat(ReadRuleValues(turn, "clientRules"));
    }

    private static IEnumerable<string> ReadRuleValues(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IReadOnlyList<string> typedRules => typedRules,
            IEnumerable<string> rules => rules,
            JsonElement { ValueKind: JsonValueKind.Array } jsonElement => jsonElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty),
            _ => []
        };
    }

    private static string? ReadAttribute(TurnContext turn, string key)
    {
        return turn.Attributes.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static string? ReadClientEntity(TurnContext turn, string entityName)
    {
        if (!turn.Attributes.TryGetValue("clientEntities", out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } jsonElement
                when jsonElement.TryGetProperty(entityName, out var property) &&
                     property.ValueKind == JsonValueKind.String
                => property.GetString(),
            IReadOnlyDictionary<string, string> typed when typed.TryGetValue(entityName, out var entityValue)
                => entityValue,
            IDictionary<string, object?> dictionary when dictionary.TryGetValue(entityName, out var entityValue)
                => entityValue?.ToString(),
            _ => null
        };
    }

    private static string? ReadSkillPayloadString(InvokeNativeSkillAction? skill, string key)
    {
        if (skill?.Payload is null || !skill.Payload.TryGetValue(key, out var value))
        {
            return null;
        }

        return value?.ToString();
    }

    private static string ResolveWordOfDayGuess(TurnContext turn, string transcript, string? nluGuess)
    {
        if (!string.IsNullOrWhiteSpace(nluGuess))
        {
            return nluGuess;
        }

        var normalized = NormalizeGuessToken(transcript);
        var hintIndex = normalized switch
        {
            "1" or "one" or "first" => 0,
            "2" or "two" or "second" => 1,
            "3" or "three" or "third" => 2,
            _ => -1
        };

        var hints = ReadRuleValues(turn, "listenAsrHints").ToArray();

        if (hintIndex >= 0)
        {
            return hintIndex < hints.Length
                ? hints[hintIndex]
                : transcript;
        }

        var fuzzyHintMatch = FindClosestHint(normalized, hints);
        return string.IsNullOrWhiteSpace(fuzzyHintMatch)
            ? transcript
            : fuzzyHintMatch;
    }

    private static string? FindClosestHint(string normalizedTranscript, IReadOnlyList<string> hints)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return null;
        }

        string? bestHint = null;
        var bestDistance = int.MaxValue;

        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                continue;
            }

            var normalizedHint = NormalizeGuessToken(hint);
            if (string.IsNullOrWhiteSpace(normalizedHint))
            {
                continue;
            }

            if (string.Equals(normalizedTranscript, normalizedHint, StringComparison.Ordinal))
            {
                return hint;
            }

            var distance = ComputeEditDistance(normalizedTranscript, normalizedHint);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestHint = hint;
        }

        return bestDistance <= 2 ? bestHint : null;
    }

    private static string NormalizeGuessToken(string value)
    {
        return value.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant();
    }

    private static int ComputeEditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column += 1)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row += 1)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column += 1)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static object BuildSkillPayload(ResponsePlan plan, TurnContext turn, string transId, SpeakAction speak,
        InvokeNativeSkillAction? skill)
    {
        var skillPayload = skill?.Payload;
        if (string.Equals(ReadPayloadString(skillPayload, "cloudResponseMode"), "completion_only",
                StringComparison.OrdinalIgnoreCase))
        {
            return BuildCompletionOnlySkillPayload(
                transId,
                ReadPayloadString(skillPayload, "skillId") ?? skill?.SkillName ?? "chitchat-skill");
        }

        var isJoke = string.Equals(plan.IntentName, "joke", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(skill?.SkillName, "@be/joke", StringComparison.OrdinalIgnoreCase);
        var isDance = string.Equals(plan.IntentName, "dance", StringComparison.OrdinalIgnoreCase);
        var payloadSkill = ReadPayloadString(skillPayload, "skillId");
        var skillId = string.IsNullOrWhiteSpace(payloadSkill)
            ? isJoke ? "@be/joke" : skill?.SkillName ?? "chitchat-skill"
            : payloadSkill;
        var esml = ReadPayloadString(skillPayload, "esml") ?? (isDance
            ? "<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, rom-upbeat' /></speak>"
            : isJoke
                ? $"<speak><es cat='happy' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(speak.Text)}</es></speak>"
                : $"<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(speak.Text)}</es></speak>");
        var mimId = ReadPayloadString(skillPayload, "mim_id") ?? (isJoke ? "runtime-joke" : "runtime-chat");
        var mimType = ReadPayloadString(skillPayload, "mim_type") ?? "announcement";
        var promptId = ReadPayloadString(skillPayload, "prompt_id") ?? "RUNTIME_PROMPT";
        var promptSubCategory = ReadPayloadString(skillPayload, "prompt_sub_category") ?? "AN";

        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CreateHubMessageId(),
            transID = transId,
            data = new
            {
                skill = new
                {
                    id = skillId
                },
                action = new
                {
                    config = new
                    {
                        jcp = new
                        {
                            type = "SLIM",
                            config = new
                            {
                                play = new
                                {
                                    esml,
                                    meta = new
                                    {
                                        prompt_id = promptId,
                                        prompt_sub_category = promptSubCategory,
                                        mim_id = mimId,
                                        mim_type = mimType
                                    }
                                }
                            }
                        }
                    }
                },
                analytics = new Dictionary<string, object?>(),
                final = true
            }
        };
    }

    private static IReadOnlyDictionary<string, object?> BuildNluPayload(
        string outboundIntent,
        IReadOnlyList<string> outboundRules,
        object entities,
        string? skillId,
        string? domain = null)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["confidence"] = 0.95,
            ["intent"] = outboundIntent,
            ["rules"] = outboundRules,
            ["entities"] = entities
        };

        if (!string.IsNullOrWhiteSpace(skillId))
        {
            payload["skill"] = skillId;
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            payload["domain"] = domain;
        }

        return payload;
    }

    private static IReadOnlyList<string> BuildGlobalCommandRules(IReadOnlyList<string> rules)
    {
        return rules.Any(static rule =>
            string.Equals(rule, "globals/global_commands_launch", StringComparison.OrdinalIgnoreCase))
            ? ["globals/global_commands_launch"]
            : [];
    }

    private static bool IsLocalClockFollowUpTurn(IReadOnlyList<string> rules)
    {
        return rules.Any(static rule =>
            string.Equals(rule, "clock/alarm_set_value", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule, "clock/timer_set_value", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule, "clock/alarm_timer_okay", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule, "clock/alarm_timer_query_menu", StringComparison.OrdinalIgnoreCase));
    }

    private static object BuildGenericFallbackSkillPayload(string transId)
    {
        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CreateHubMessageId(),
            transID = transId,
            data = new
            {
                skill = new
                {
                    id = "chitchat-skill"
                },
                action = new
                {
                    config = new
                    {
                        jcp = new
                        {
                            type = "SLIM",
                            config = new
                            {
                                play = new
                                {
                                    esml =
                                        "<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>I heard you.</es></speak>",
                                    meta = new
                                    {
                                        prompt_id = "RUNTIME_PROMPT",
                                        prompt_sub_category = "AN",
                                        mim_id = "runtime-chat",
                                        mim_type = "announcement"
                                    }
                                }
                            }
                        }
                    }
                },
                analytics = new Dictionary<string, object?>(),
                final = true
            }
        };
    }

    private static object BuildCompletionOnlySkillPayload(string transId, string skillId)
    {
        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CreateHubMessageId(),
            transID = transId,
            data = new
            {
                skill = new
                {
                    id = skillId
                },
                action = new
                {
                    config = new
                    {
                        jcp = new
                        {
                            type = "SLIM",
                            config = new
                            {
                                play = new
                                {
                                    esml = "<speak><break time='1ms'/></speak>",
                                    meta = new
                                    {
                                        prompt_id = "RUNTIME_PROMPT",
                                        prompt_sub_category = "AN",
                                        mim_id = "runtime-silent-complete",
                                        mim_type = "announcement"
                                    }
                                }
                            }
                        }
                    }
                },
                analytics = new Dictionary<string, object?>(),
                final = true
            }
        };
    }

    private static object BuildSkillRedirectPayload(
        string transId,
        string skillId,
        string outboundIntent,
        string outboundAsrText,
        IReadOnlyList<string> outboundRules,
        object entities)
    {
        return new
        {
            type = "SKILL_REDIRECT",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CreateHubMessageId(),
            transID = transId,
            data = new
            {
                match = new
                {
                    skillID = skillId,
                    onRobot = true,
                    launch = true,
                    skipSurprises = true
                },
                asr = new
                {
                    text = outboundAsrText,
                    confidence = 0.95
                },
                nlu = new
                {
                    confidence = 0.95,
                    intent = outboundIntent,
                    rules = outboundRules,
                    entities
                }
            }
        };
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string? ReadPayloadString(IDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value))
        {
            return null;
        }

        return value?.ToString();
    }

    private static string CreateHubMessageId()
    {
        return $"mid-{Guid.NewGuid()}";
    }

    public sealed record SocketReplyPlan(string Text, int DelayMs = 0);
}
