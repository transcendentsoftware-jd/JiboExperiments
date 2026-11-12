using System.Text.Json;
using System.Text.RegularExpressions;
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
        var primarySkillRule = SkillListenOwnership.ReadPrimarySkillRule(turn);
        var isYesNoTurn = !string.IsNullOrWhiteSpace(yesNoRule);
        var isYesNoIntent = string.Equals(plan.IntentName, "yes", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(plan.IntentName, "no", StringComparison.OrdinalIgnoreCase);
        var isSkillListenIntent = string.Equals(plan.IntentName, "skill_listen", StringComparison.OrdinalIgnoreCase);
        // This internal routing label is not a grammar tag in the stock optional
        // fun-fact state. Preserve unmatched speech without inventing a native match.
        // Explicit client grammar results and global commands retain normal routing.
        if (isSkillListenIntent &&
            string.Equals(primarySkillRule, "word-of-the-day/fun_fact", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(clientIntent) &&
            !string.IsNullOrWhiteSpace(transcript))
            return MapUnmatchedWordOfDayFollowUp(transId, primarySkillRule!, transcript);

        var isPromptEchoIntent = string.Equals(plan.IntentName, "prompt_echo", StringComparison.OrdinalIgnoreCase);
        // Robot-owned skill listens speak for themselves. Personal report is cloud-owned and still
        // needs a SKILL_ACTION.
        var isSkillOwnedYesNoTurn = IsYesNoListenTurn(turn) &&
                                    SkillListenOwnership.ShouldSuppressCompetingSpeech(turn, plan.IntentName);
        var isWordOfDayLaunch = string.Equals(plan.IntentName, "word_of_the_day", StringComparison.OrdinalIgnoreCase);
        var isWordOfDayGuess =
            string.Equals(plan.IntentName, "word_of_the_day_guess", StringComparison.OrdinalIgnoreCase);
        var isRadioLaunch = string.Equals(plan.IntentName, "radio", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(plan.IntentName, "radio_genre", StringComparison.OrdinalIgnoreCase);
        var isBadAppleLaunch = string.Equals(plan.IntentName, "bad_apple", StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(skill?.SkillName, "@be/bad-apple", StringComparison.OrdinalIgnoreCase);
        var isStopCommand = string.Equals(plan.IntentName, "stop", StringComparison.OrdinalIgnoreCase);
        var isVolumeControl = string.Equals(plan.IntentName, "volume_up", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(plan.IntentName, "volume_down", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(plan.IntentName, "volume_to_value", StringComparison.OrdinalIgnoreCase);
        var isProactivePizzaFactOffer = string.Equals(plan.IntentName, "proactive_offer_pizza_fact",
            StringComparison.OrdinalIgnoreCase);
        var isProactivePizzaFactFollowUp = string.Equals(plan.IntentName, "proactive_pizza_fact",
            StringComparison.OrdinalIgnoreCase);
        var isSettingsLaunch = string.Equals(skill?.SkillName, "@be/settings", StringComparison.OrdinalIgnoreCase);
        var isSleepCommand = string.Equals(plan.IntentName, "sleep", StringComparison.OrdinalIgnoreCase);
        var isWakeUpCommand = string.Equals(plan.IntentName, "wake_up", StringComparison.OrdinalIgnoreCase);
        var isTurnAroundCommand = string.Equals(plan.IntentName, "turn_around", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(plan.IntentName, "spin_around", StringComparison.OrdinalIgnoreCase);
        var isGlobalCommand = isStopCommand || isSleepCommand || isTurnAroundCommand || isVolumeControl;
        var isPhotoGalleryLaunch = string.Equals(plan.IntentName, "photo_gallery", StringComparison.OrdinalIgnoreCase);
        var isPhotoCreateLaunch = string.Equals(plan.IntentName, "snapshot", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(plan.IntentName, "photobooth", StringComparison.OrdinalIgnoreCase);
        var isClockSkillLaunch = string.Equals(skill?.SkillName, "@be/clock", StringComparison.OrdinalIgnoreCase);
        var isReportSkillLaunch = string.Equals(skill?.SkillName, "report-skill", StringComparison.OrdinalIgnoreCase);
        var isIntroductionsLaunch = string.Equals(skill?.SkillName, "@be/introductions", StringComparison.OrdinalIgnoreCase);
        var idleRedirectDelayMs = 75;
        var idleCompletionDelayMs = isTurnAroundCommand ? 750 : 125;
        const int cloudSpeakDelayMs = 75;
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
        var isCloudOwnedFollowUp = SkillListenOwnership.IsCloudOwnedFollowUp(turn);
        var outboundIntent = isGlobalCommand && !string.IsNullOrWhiteSpace(globalIntent)
            ? globalIntent
            : isWordOfDayLaunch
                ? "menu"
                : isRadioLaunch || isBadAppleLaunch
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
                                        : string.Equals(messageType, "CLIENT_NLU",
                                              StringComparison.OrdinalIgnoreCase) &&
                                          !string.IsNullOrWhiteSpace(clientIntent) &&
                                          !isCloudOwnedFollowUp
                                            ? clientIntent
                                            : plan.IntentName ?? "unknown";
        var outboundAsrText = isWordOfDayGuess && !string.IsNullOrWhiteSpace(wordOfDayGuess)
            ? wordOfDayGuess
            : isWordOfDayLaunch
                ? string.Empty
                : isGlobalCommand
                    ? transcript
                    : isRadioLaunch || isBadAppleLaunch
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
                                            : isPromptEchoIntent
                                                ? string.Empty
                                                : string.Equals(messageType, "CLIENT_NLU",
                                                      StringComparison.OrdinalIgnoreCase) &&
                                                  !string.IsNullOrWhiteSpace(clientIntent) &&
                                                  !isCloudOwnedFollowUp
                                                    ? clientIntent
                                                    : transcript;
        var outboundRules = isProactivePizzaFactOffer || isProactivePizzaFactFollowUp
            ? ["surprises-date/offer_date_fact"]
            : isWordOfDayLaunch
                ? ["word-of-the-day/menu"]
                : isGlobalCommand
                    ? BuildGlobalCommandRules(rules)
                    : isRadioLaunch || isBadAppleLaunch
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
                                                : isSkillListenIntent && !string.IsNullOrWhiteSpace(primarySkillRule)
                                                    ? [primarySkillRule]
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
            timerHours,
            timerMinutes,
            timerSeconds,
            alarmTime,
            alarmAmPm,
            isReportSkillLaunch,
            reportDate,
            reportWeatherCondition);
        var isKnowledgeSearchIntent =
            string.Equals(plan.IntentName, "knowledge_search", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(plan.IntentName, "knowledge_search_not_found", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(plan.IntentName, "knowledge_search_unavailable", StringComparison.OrdinalIgnoreCase);
        var isAnswerCloudSkill = string.Equals(
                                     cloudSkill,
                                     SearchThinkingPreludeFactory.AnswerSkillId,
                                     StringComparison.OrdinalIgnoreCase) ||
                                 isKnowledgeSearchIntent;
        var localOnRobotSkillId = isWordOfDayLaunch ? "@be/word-of-the-day" :
            isRadioLaunch ? "@be/radio" :
            isBadAppleLaunch ? "@be/bad-apple" :
            isSettingsLaunch ? "@be/settings" :
            isPhotoGalleryLaunch ? "@be/gallery" :
            isPhotoCreateLaunch ? "@be/create" :
            isClockSkillLaunch ? "@be/clock" :
            isIntroductionsLaunch ? "@be/introductions" :
            null;
        var shouldEmitCloudSpeak = emitSkillActions &&
                                   speak is not null &&
                                   !isSkillListenIntent &&
                                   !isPromptEchoIntent &&
                                   !(isYesNoIntent && isSkillOwnedYesNoTurn);
        // Pegasus answer LISTEN: skillID="answer", onRobot=false, launch=true, no cloudSkill on wire.
        // Robot remaps skillID → match.cloudSkill and launches @be/nimbus (Thinking_Eye for answer/news).
        // Stock jetstream skill-switch uses match.skillID, not nlu.skill. Hotphrase + Nimbus
        // context suppresses delayed SKILL_REDIRECT, so the initial LISTEN match must carry
        // the on-robot skill or Nimbus will never leave idle.
        object? listenMatch;
        if (isSleepCommand)
        {
            listenMatch = new
            {
                intent = outboundIntent,
                rule = outboundRules.FirstOrDefault() ?? string.Empty,
                score = 0.95,
                skillID = "@be/idle",
                onRobot = true,
                cloudSkill,
                skipSurprises = true
            };
        }
        else if (isWakeUpCommand)
        {
            listenMatch = new
            {
                intent = outboundIntent,
                rule = outboundRules.FirstOrDefault() ?? string.Empty,
                score = 0.95,
                skillID = "@be/greetings",
                onRobot = true,
                cloudSkill,
                skipSurprises = true
            };
        }
        else if (isAnswerCloudSkill)
        {
            listenMatch = new
            {
                skillID = SearchThinkingPreludeFactory.AnswerSkillId,
                launch = true,
                onRobot = false,
                skipSurprises = true
            };
        }
        else if (!string.IsNullOrWhiteSpace(localOnRobotSkillId))
        {
            listenMatch = new
            {
                intent = outboundIntent,
                rule = outboundRules.FirstOrDefault() ?? string.Empty,
                score = 0.95,
                skillID = localOnRobotSkillId,
                onRobot = true,
                launch = true,
                cloudSkill,
                skipSurprises = true
            };
        }
        else if (shouldEmitCloudSpeak)
        {
            listenMatch = new
            {
                intent = outboundIntent,
                rule = outboundRules.FirstOrDefault() ?? string.Empty,
                score = 0.95,
                skillID = "@be/nimbus",
                onRobot = false,
                launch = true,
                cloudSkill = string.IsNullOrWhiteSpace(cloudSkill) ? "chitchat-skill" : cloudSkill,
                skipSurprises = true
            };
        }
        else
        {
            listenMatch = new
            {
                intent = outboundIntent,
                rule = outboundRules.FirstOrDefault() ?? string.Empty,
                score = 0.95,
                skillID = (string?)null,
                onRobot = (bool?)null,
                cloudSkill,
                skipSurprises = true
            };
        }

        var listenPayload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "LISTEN",
            ["transID"] = transId,
            ["data"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["asr"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["confidence"] = 0.95,
                    ["final"] = true,
                    ["text"] = outboundAsrText
                },
                ["nlu"] = BuildNluPayload(
                    outboundIntent,
                    outboundRules,
                    entities,
                    localOnRobotSkillId ??
                    (isReportSkillLaunch ? "report-skill" : null),
                    isGlobalCommand ? nluDomain ?? "global_commands" : null),
                ["match"] = listenMatch
            }
        };
        if (isAnswerCloudSkill)
            listenPayload["final"] = false;

        var skipListenAndEos = session.Metadata.TryGetValue(
                                    SearchThinkingPreludeFactory.PreludeMetadataKey,
                                    out var preludeTransId) &&
                                string.Equals(preludeTransId?.ToString(), transId, StringComparison.Ordinal);
        if (skipListenAndEos)
            session.Metadata.Remove(SearchThinkingPreludeFactory.PreludeMetadataKey);

        var messages = new List<SocketReplyPlan>();
        if (!skipListenAndEos)
        {
            // Pegasus cloud-skill order: EOS then non-final LISTEN, then SKILL_ACTION later.
            if (isAnswerCloudSkill)
            {
                messages.Add(new SocketReplyPlan(JsonSerializer.Serialize(new
                {
                    type = "EOS",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    msgID = CloudMessageIdFactory.CreateHubMessageId(),
                    transID = transId,
                    data = new { }
                })));
                messages.Add(new SocketReplyPlan(JsonSerializer.Serialize(listenPayload)));
            }
            else
            {
                messages.Add(new SocketReplyPlan(JsonSerializer.Serialize(listenPayload)));
                messages.Add(new SocketReplyPlan(JsonSerializer.Serialize(new
                {
                    type = "EOS",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    msgID = CloudMessageIdFactory.CreateHubMessageId(),
                    transID = transId,
                    data = new { }
                })));
            }
        }

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
                cloudSpeakDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/word-of-the-day")),
                125));
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
                cloudSpeakDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/radio")),
                125));
        }

        if (isBadAppleLaunch)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/bad-apple",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                cloudSpeakDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/bad-apple")),
                125));
        }

        if (isStopCommand)
            return messages;

        if (isSleepCommand)
        {
            // The initial LISTEN match routes sleep directly to @be/idle. Do not also send a
            // delayed redirect: stock BE treats it as a second local launch and briefly refreshes
            // the circadian state from ASLEEP through SELECT_INTENT.
            return messages;
        }

        if (isWakeUpCommand)
        {
            // @be/greetings is the stock robot's own wake fallback. As with
            // sleep, send it only in the initial LISTEN match: a delayed second
            // launch can interrupt the local circadian wake transition.
            return messages;
        }

        if (isTurnAroundCommand)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/idle",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                idleRedirectDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/idle")),
                idleCompletionDelayMs));
        }

        if (isVolumeControl)
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/nimbus")),
                cloudSpeakDelayMs));

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
                cloudSpeakDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/settings")),
                125));
        }

        // The initial LISTEN match already launches @be/clock (skillID + launch=true).
        // Do not also send a delayed SKILL_REDIRECT: stock JiboClock treats it as a second
        // local launch, which closes/reopens the clock screen and re-announces the time.
        // CLIENT_NLU menu turns and local alarm/timer follow-ups already rely on LISTEN/EOS only.

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
                cloudSpeakDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, skillId)),
                125));
        }

        if (isIntroductionsLaunch &&
            !string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillRedirectPayload(
                    transId,
                    "@be/introductions",
                    outboundIntent,
                    outboundAsrText,
                    outboundRules,
                    entities)),
                cloudSpeakDelayMs));
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildCompletionOnlySkillPayload(transId, "@be/introductions")),
                125));
        }

        // Don't emit a chitchat SKILL_ACTION for tutorial yes/no turns: the tutorial skill handles
        // the response locally. Sending a competing chitchat-skill action with final:true causes the
        // GLSM to double-dispatch and the tutorial never advances (dance question repeats forever).
        if (shouldEmitCloudSpeak)
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillPayload(plan, transId, speak!, skill, outboundAsrText)),
                cloudSpeakDelayMs));

        return messages;
    }

    public static IReadOnlyList<SocketReplyPlan> MapProactive(
        ResponsePlan plan,
        TurnContext turn,
        CloudSession session)
    {
        var transId = turn.Attributes.TryGetValue("transID", out var value)
            ? value?.ToString() ?? string.Empty
            : session.LastTransId ?? string.Empty;
        var speak = plan.Actions.OfType<SpeakAction>().FirstOrDefault();
        var skill = plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault();
        var noAction = string.IsNullOrWhiteSpace(plan.IntentName) ||
                       string.Equals(plan.IntentName, "trigger_ignored", StringComparison.OrdinalIgnoreCase) ||
                       speak is null && skill is null;
        if (noAction)
            return MapProactiveNoAction(transId);

        var onRobot = skill?.SkillName?.StartsWith("@be/", StringComparison.OrdinalIgnoreCase) == true;
        var skillId = onRobot ? skill!.SkillName : "chitchat-skill";
        var emitSkillAction = speak is not null && !onRobot;
        var skipSurprises = turn.Attributes.TryGetValue("triggerSource", out var triggerSource) &&
                            string.Equals(triggerSource?.ToString(), "SURPRISE",
                                StringComparison.OrdinalIgnoreCase);
        var messages = new List<SocketReplyPlan>
        {
            new(JsonSerializer.Serialize(new
            {
                type = "PROACTIVE",
                msgID = CloudMessageIdFactory.CreateHubMessageId(),
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                transID = transId,
                data = new
                {
                    match = new
                    {
                        skillID = skillId,
                        onRobot,
                        isProactive = true,
                        launch = true,
                        skipSurprises
                    }
                },
                final = !emitSkillAction
            }))
        };

        if (emitSkillAction)
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillPayload(plan, transId, speak!, skill)),
                75));
        return messages;
    }

    public static IReadOnlyList<SocketReplyPlan> MapProactiveNoAction(string transId) =>
    [
        new SocketReplyPlan(JsonSerializer.Serialize(new
        {
            type = "PROACTIVE",
            msgID = CloudMessageIdFactory.CreateHubMessageId(),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            transID = transId,
            data = new { },
            final = true
        }))
    ];

    public static IReadOnlyList<SocketReplyPlan> MapFallback(string transId,
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
                msgID = CloudMessageIdFactory.CreateHubMessageId(),
                transID = transId,
                data = new { }
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(BuildGenericFallbackSkillPayload(transId)), 75)
        ];
    }

    private static IReadOnlyList<SocketReplyPlan> MapUnmatchedWordOfDayFollowUp(
        string transId, string localRule, string transcript)
    {
        return
        [
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "LISTEN",
                transID = transId,
                data = new
                {
                    asr = new { confidence = 0.95, final = true, text = transcript },
                    nlu = new
                    {
                        confidence = 0.95,
                        intent = string.Empty,
                        rules = new[] { localRule },
                        entities = new Dictionary<string, object?>()
                    }
                }
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "EOS",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CloudMessageIdFactory.CreateHubMessageId(),
                transID = transId,
                data = new { }
            }))
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
                msgID = CloudMessageIdFactory.CreateHubMessageId(),
                transID = transId,
                data = new { }
            }))
        ];
    }

    public static IReadOnlyList<SocketReplyPlan> MapNoInputAndRedirectToSkill(
        string transId,
        IReadOnlyList<string> rules,
        string skillId,
        int redirectDelayMs = 0)
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

        if (!turn.Attributes.TryGetValue(attributeName, out var value)) return [];

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
            if (!includeCreateDomain) return new Dictionary<string, object?>();

            return new Dictionary<string, object?>
            {
                ["domain"] = "create"
            };
        }

        if (wordOfDayLaunch)
            return new Dictionary<string, object?>
            {
                ["domain"] = "word-of-the-day"
            };

        if (globalCommand)
        {
            var entities = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(volumeLevel)) entities["volumeLevel"] = volumeLevel;

            return entities;
        }

        if (radioLaunch)
        {
            var entities = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(radioStation)) entities["station"] = radioStation;

            return entities;
        }

        if (clockSkillLaunch)
        {
            var entities = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(clockDomain)) entities["domain"] = clockDomain;

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
            if (!string.IsNullOrWhiteSpace(reportDate)) entities["date"] = reportDate;

            if (!string.IsNullOrWhiteSpace(reportWeatherCondition)) entities["Weather"] = reportWeatherCondition;

            return entities;
        }

        if (wordOfDayGuess)
            return new Dictionary<string, object?>
            {
                ["guess"] = guess ?? string.Empty
            };

        if (!string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) ||
            !turn.Attributes.TryGetValue("clientEntities", out var value) || value is null)
            return new Dictionary<string, object?>();

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } jsonElement => jsonElement,
            IDictionary<string, object?> dictionary => dictionary,
            _ => new Dictionary<string, object?>()
        };
    }

    private static bool IsYesNoListenTurn(TurnContext turn)
    {
        // Robot-local yes/no and other skill-owned listens speak their own response. Personal
        // report is cloud-owned and still needs a competing SKILL_ACTION.
        if (SkillListenOwnership.IsCloudOwnedFollowUp(turn)) return false;

        return ReadRuleValues(turn).Any(IsRobotLocalYesNoRule) ||
               SkillListenOwnership.IsSkillOwnedListen(turn);
    }

    private static bool IsRobotLocalYesNoRule(string rule)
    {
        return rule.StartsWith("tutorial/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadYesNoRule(TurnContext turn)
    {
        var constrained = ReadRuleValues(turn)
            .FirstOrDefault(static rule =>
                string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase) ||
                IsRobotLocalYesNoRule(rule));
        if (!string.IsNullOrWhiteSpace(constrained)) return constrained;

        if (!SkillListenOwnership.IsSkillOwnedListen(turn)) return null;

        return SkillListenOwnership.ReadPrimarySkillRule(turn);
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
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null) return [];

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
        if (!turn.Attributes.TryGetValue("clientEntities", out var value) || value is null) return null;

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
        if (skill?.Payload is null || !skill.Payload.TryGetValue(key, out var value)) return null;

        return value?.ToString();
    }

    private static string ResolveWordOfDayGuess(TurnContext turn, string transcript, string? nluGuess)
    {
        var hints = ReadRuleValues(turn, "listenAsrHints").ToArray();
        return WordOfDayGuessResolver.Resolve(transcript, hints, nluGuess);
    }

    private static object BuildSkillPayload(ResponsePlan plan, string transId, SpeakAction speak,
        InvokeNativeSkillAction? skill, string heardTranscript = "")
    {
        var skillPayload = skill?.Payload;
        if (skillPayload is null && IsHouseholdListFollowUpIntent(plan.IntentName ?? string.Empty))
            skillPayload = BuildHouseholdListFollowUpPayload();

        if (string.Equals(ReadPayloadString(skillPayload, "cloudResponseMode"), "completion_only",
                StringComparison.OrdinalIgnoreCase))
            return BuildCompletionOnlySkillPayload(
                transId,
                ReadPayloadString(skillPayload, "skillId") ?? skill?.SkillName ?? "chitchat-skill");

        var isJoke = string.Equals(plan.IntentName, "joke", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(skill?.SkillName, "@be/joke", StringComparison.OrdinalIgnoreCase);
        var isDance = string.Equals(plan.IntentName, "dance", StringComparison.OrdinalIgnoreCase);
        var isKnowledgeSearchNotFound = string.Equals(
            plan.IntentName,
            "knowledge_search_not_found",
            StringComparison.OrdinalIgnoreCase);
        var isNotUnderstood = string.Equals(
            plan.IntentName,
            "not_understood",
            StringComparison.OrdinalIgnoreCase);
        var payloadSkill = ReadPayloadString(skillPayload, "skillId");
        var skillId = string.IsNullOrWhiteSpace(payloadSkill)
            ? isJoke ? "@be/joke" : skill?.SkillName ?? "chitchat-skill"
            : payloadSkill;
        // Knowledge search thinking is owned by Nimbus after the robot remaps skillID "answer".
        // Do not embed Thinking_Eye in the speak ESML.
        var esml = ReadPayloadString(skillPayload, "esml") ?? (isDance
            ? "<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, rom-upbeat' /></speak>"
            : isJoke
                ? $"<speak><es cat='happy' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXmlText(speak.Text)}</es></speak>"
                : $"<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXmlText(speak.Text)}</es></speak>");
        var mimId = ReadPayloadString(skillPayload, "mim_id") ?? (isJoke ? "runtime-joke" : "runtime-chat");
        var mimType = ReadPayloadString(skillPayload, "mim_type") ?? "announcement";
        var promptId = ReadPayloadString(skillPayload, "prompt_id") ?? "RUNTIME_PROMPT";
        var promptSubCategory = ReadPayloadString(skillPayload, "prompt_sub_category") ?? "AN";
        var listenContexts = ReadPayloadStringArray(skillPayload, "listen_contexts");
        var listenAsrHints = ReadPayloadStringArray(skillPayload, "listen_asr_hints");
        var playConfig = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["esml"] = esml,
            ["meta"] = new
            {
                prompt_id = promptId,
                prompt_sub_category = promptSubCategory,
                mim_id = mimId,
                mim_type = mimType
            }
        };
        var jcpConfig = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["play"] = playConfig
        };

        if (listenContexts.Count > 0)
        {
            var listenConfig = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = CloudMessageIdFactory.CreateProtocolId(),
                ["type"] = "LISTEN",
                ["contexts"] = listenContexts
            };

            if (listenAsrHints.Count > 0)
                listenConfig["asr"] = new
                {
                    hints = listenAsrHints
                };

            jcpConfig["listen"] = listenConfig;
        }

        // Answer/Nimbus clears the eye during Thinking_Eye, so LISTEN asr.text alone never
        // paints Idle's quoted "heard" Label. Attach that view on not-found or not-understood
        // speech instead.
        if ((isKnowledgeSearchNotFound || isNotUnderstood) && !string.IsNullOrWhiteSpace(heardTranscript))
            AttachHeardTranscriptDisplay(jcpConfig, heardTranscript.Trim());

        var weatherHiLoView = BuildWeatherHiLoView(skillPayload);
        var weeklyWeatherCards = BuildWeatherHiLoSequenceCards(skillPayload);
        if (weatherHiLoView is null && weeklyWeatherCards.Count > 0) weatherHiLoView = weeklyWeatherCards[0].View;

        var useSequence = false;
        var cloudSkillName = ReadPayloadString(skillPayload, "cloudSkill");
        var isPersonalReport = string.Equals(
            cloudSkillName,
            "personal_report",
            StringComparison.OrdinalIgnoreCase);
        var isNewsSkill = string.Equals(cloudSkillName, "news", StringComparison.OrdinalIgnoreCase);
        var personalReportSections = skillPayload is not null &&
                                     skillPayload.TryGetValue("personal_report_sections", out var sectionsRaw)
            ? ReadPayloadObjectArray(sectionsRaw!)
            : [];
        var newsSections = skillPayload is not null &&
                           skillPayload.TryGetValue("news_sections", out var newsSectionsRaw)
            ? ReadPayloadObjectArray(newsSectionsRaw!)
            : [];
        var usePersonalReportSequence = isPersonalReport && personalReportSections.Length > 1;
        var useNewsSequence = isNewsSkill && newsSections.Length > 1;

        if (weatherHiLoView is not null)
        {
            var resolvedGuiContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "Javascript",
                ["data"] = weatherHiLoView,
                ["pause"] = true
            };

            var legacyGuiConfig = new
            {
                type = "Javascript",
                data = "views.weatherHiLo",
                pause = true
            };

            jcpConfig["gui"] = legacyGuiConfig;
            jcpConfig["display"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["view"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    // Legacy fields used by existing tests and tooling.
                    ["type"] = "Javascript",
                    ["data"] = weatherHiLoView,
                    ["pause"] = true,
                    // Pegasus-style view context used by on-robot weather cards.
                    ["context"] = resolvedGuiContext
                }
            };

            jcpConfig["timeout"] = 6;
            jcpConfig["barge_in"] = true;
            jcpConfig["no_matches_for_gui"] = 0;
            jcpConfig["no_inputs_for_gui"] = 0;

            var weatherViews = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["weatherHiLo"] = weatherHiLoView
            };
            jcpConfig["views"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["weatherHiLo"] = weatherHiLoView
            };
            jcpConfig["local"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["views"] = weatherViews
            };

            if (usePersonalReportSequence)
            {
                useSequence = true;
                var weatherIcon = ReadPayloadString(skillPayload, "weather_icon") ?? "cloudy";
                jcpConfig["children"] = BuildPersonalReportSequenceChildren(
                    personalReportSections,
                    weatherHiLoView,
                    weatherIcon,
                    promptSubCategory,
                    mimId,
                    mimType);
            }
            else if (weeklyWeatherCards.Count > 1)
            {
                useSequence = true;
                jcpConfig["children"] = BuildWeatherHiLoSequenceChildren(
                    weeklyWeatherCards,
                    promptSubCategory,
                    mimId,
                    mimType);
            }
        }
        else if (usePersonalReportSequence)
        {
            // Personal report without a weather GUI still benefits from sectioned SLIMs.
            useSequence = true;
            jcpConfig["children"] = BuildPersonalReportSequenceChildren(
                personalReportSections,
                null,
                "cloudy",
                promptSubCategory,
                mimId,
                mimType);
        }
        else if (useNewsSequence)
        {
            // Standalone news: Intro + one Headline SLIM per story (Pegasus NewsMimLogic order).
            useSequence = true;
            jcpConfig["children"] = BuildPersonalReportSequenceChildren(
                newsSections,
                null,
                "cloudy",
                promptSubCategory,
                mimId,
                mimType);
        }

        var jcp = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "SLIM",
            ["config"] = jcpConfig
        };
        if (!useSequence ||
            !jcpConfig.TryGetValue("children", out var sequenceChildren) ||
            sequenceChildren is null)
            return new
            {
                type = "SKILL_ACTION",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CloudMessageIdFactory.CreateHubMessageId(),
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
                            jcp
                        }
                    },
                    analytics = new Dictionary<string, object?>(),
                    final = true
                }
            };

        jcp["type"] = "SEQUENCE";
        jcp.Remove("config");
        jcp["children"] = sequenceChildren;

        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CloudMessageIdFactory.CreateHubMessageId(),
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
                        jcp
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

        if (!string.IsNullOrWhiteSpace(skillId)) payload["skill"] = skillId;

        if (!string.IsNullOrWhiteSpace(domain)) payload["domain"] = domain;

        return payload;
    }

    private static IReadOnlyList<string> BuildGlobalCommandRules(IReadOnlyList<string> rules)
    {
        return rules.Any(static rule =>
            string.Equals(rule, "globals/global_commands_launch", StringComparison.OrdinalIgnoreCase))
            ? ["globals/global_commands_launch"]
            : [];
    }

    private static object BuildGenericFallbackSkillPayload(string transId)
    {
        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CloudMessageIdFactory.CreateHubMessageId(),
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
            msgID = CloudMessageIdFactory.CreateHubMessageId(),
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

    private static bool IsHouseholdListFollowUpIntent(string intentName)
    {
        return string.Equals(intentName, "shopping_list_prompt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "shopping_list_add", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "shopping_list_no_input", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "shopping_list_no_match", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "todo_list_prompt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "todo_list_add", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "todo_list_no_input", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(intentName, "todo_list_no_match", StringComparison.OrdinalIgnoreCase);
    }

    private static IDictionary<string, object?> BuildHouseholdListFollowUpPayload()
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mim_id"] = "runtime-household-list",
            ["mim_type"] = "question",
            ["prompt_id"] = "RUNTIME_PROMPT",
            ["prompt_sub_category"] = "Q",
            ["listen_contexts"] = new[] { "household-list/follow_up_item" },
            ["listen_asr_hints"] = new[] { "$ANYTHING" }
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
            msgID = CloudMessageIdFactory.CreateHubMessageId(),
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

    private static string EscapeXmlText(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string? ReadPayloadString(IDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value)) return null;

        return value?.ToString();
    }

    private static IReadOnlyList<string> ReadPayloadStringArray(IDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null) return [];

        return value switch
        {
            string text =>
            [
                .. text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(static context => !string.IsNullOrWhiteSpace(context))
            ],
            string[] contexts => [.. contexts.Where(static context => !string.IsNullOrWhiteSpace(context))],
            IEnumerable<string> contexts => [.. contexts.Where(static context => !string.IsNullOrWhiteSpace(context))],
            JsonElement { ValueKind: JsonValueKind.Array } jsonElement =>
            [
                .. jsonElement
                    .EnumerateArray()
                    .Select(static item => item.GetString())
                    .Where(static context => !string.IsNullOrWhiteSpace(context))
                    .Select(static context => context!)
            ],
            IEnumerable<object?> contexts =>
            [
                .. contexts
                    .Select(static context => context?.ToString())
                    .Where(static context => !string.IsNullOrWhiteSpace(context))
                    .Select(static context => context!)
            ],
            _ => string.IsNullOrWhiteSpace(value.ToString()) ? [] : [value.ToString()!]
        };
    }

    private static IReadOnlyList<WeatherHiLoSequenceCard> BuildWeatherHiLoSequenceCards(
        IDictionary<string, object?>? payload)
    {
        if (payload is null ||
            !payload.TryGetValue("weather_weekly_cards", out var rawCards) ||
            rawCards is null)
            return [];

        var cards = ReadPayloadObjectArray(rawCards);
        if (cards.Length == 0) return [];

        var sequenceCards = new List<WeatherHiLoSequenceCard>(cards.Length);

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var card in cards)
        {
            var weatherCardPayload = BuildWeatherCardPayload(card);

            var view = BuildWeatherHiLoView(weatherCardPayload);

            if (view is null) continue;

            var sequenceCard = BuildSequenceCard(view, weatherCardPayload);

            sequenceCards.Add(sequenceCard);
        }

        return sequenceCards;
    }

    private static WeatherHiLoSequenceCard BuildSequenceCard(object view,
        Dictionary<string, object?> weatherCardPayload)
    {
        return new WeatherHiLoSequenceCard(
            view,
            ReadPayloadString(weatherCardPayload, "weather_day"),
            ReadPayloadString(weatherCardPayload, "weather_icon"),
            ReadPayloadString(weatherCardPayload, "weather_spoken_line"));
    }

    private static Dictionary<string, object?> BuildWeatherCardPayload(IDictionary<string, object?> card)
    {
        return new Dictionary<string, object?>(card, StringComparer.OrdinalIgnoreCase)
        {
            ["weather_view_enabled"] = true,
            ["weather_view_kind"] = "weatherHiLo"
        };
    }

    private static IReadOnlyList<object> BuildPersonalReportSequenceChildren(
        IReadOnlyList<IDictionary<string, object?>> sections,
        object? weatherHiLoView,
        string weatherIcon,
        string promptSubCategory,
        string mimId,
        string mimType)
    {
        var children = new List<object>(sections.Count);
        for (var index = 0; index < sections.Count; index += 1)
        {
            var section = sections[index];
            var kind = ReadPayloadString(section, "kind") ?? $"section{index + 1}";
            var text = ReadPayloadString(section, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;

            var isWeatherSection = string.Equals(kind, "kickoff_weather", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(kind, "weather", StringComparison.OrdinalIgnoreCase);
            var animCat = ReadPayloadString(section, "anim_cat");
            var animMeta = ReadPayloadString(section, "anim_meta");
            if (isWeatherSection && string.IsNullOrWhiteSpace(animCat))
            {
                animCat = "weather";
                animMeta = weatherIcon;
            }

            var promptLabel = Regex.Replace(kind, "[^A-Za-z0-9]", string.Empty, RegexOptions.CultureInvariant);
            if (string.IsNullOrWhiteSpace(promptLabel)) promptLabel = $"Section{index + 1}";

            // Pegasus NewsHeadline / commute MIMs use a longer post-anim break than 0.35.
            var animBreak = string.Equals(animCat, "news", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(animCat, "commute", StringComparison.OrdinalIgnoreCase)
                ? "0.75"
                : "0.35";
            var esml = !string.IsNullOrWhiteSpace(animCat) && !string.IsNullOrWhiteSpace(animMeta)
                ? $"<speak><anim cat='{EscapeXml(animCat)}' meta='{EscapeXml(animMeta)}' nonBlocking='true' /><break size='{animBreak}'/><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(text)}</es></speak>"
                : $"<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(text)}</es></speak>";

            var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["play"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["esml"] = esml,
                    ["meta"] = new
                    {
                        prompt_id = $"PersonalReport{promptLabel}_AN_01",
                        prompt_sub_category = promptSubCategory,
                        mim_id = mimId,
                        mim_type = mimType
                    }
                },
                ["barge_in"] = true
            };

            var isCommuteSection = string.Equals(kind, "commute", StringComparison.OrdinalIgnoreCase);
            var isNewsSection = kind.StartsWith("news", StringComparison.OrdinalIgnoreCase);
            var shouldHoldSection = isWeatherSection ||
                                    isCommuteSection ||
                                    isNewsSection ||
                                    TryReadPayloadInt(section, "hold_timeout") is > 0;

            if (isWeatherSection && weatherHiLoView is not null)
                AttachPausedGuiView(config, "weatherHiLo", weatherHiLoView);

            if (shouldHoldSection)
            {
                config["timeout"] = TryReadPayloadInt(section, "hold_timeout") ?? 6;
                config["no_matches_for_gui"] = 0;
                config["no_inputs_for_gui"] = 0;
            }

            children.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "SLIM",
                ["config"] = config
            });
        }

        return children;
    }

    private static void AttachPausedGuiView(
        IDictionary<string, object?> config,
        string viewName,
        object view)
    {
        var resolvedGuiContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "Javascript",
            ["data"] = view,
            ["pause"] = true
        };
        config["gui"] = new
        {
            type = "Javascript",
            data = $"views.{viewName}",
            pause = true
        };
        config["display"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["view"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "Javascript",
                ["data"] = view,
                ["pause"] = true,
                ["context"] = resolvedGuiContext
            }
        };
        config["views"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [viewName] = view
        };
        config["local"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["views"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [viewName] = view
            }
        };
    }

    /// <summary>
    /// Idle HJNoMatch Label — quoted ASR text centered on the face while speaking not-found.
    /// Nimbus ProcessCloud reads display.view.context as the MIM gui.
    /// </summary>
    private static void AttachHeardTranscriptDisplay(IDictionary<string, object?> config, string transcript)
    {
        const string viewName = "heardTranscript";
        // FaceRenderer.WIDTH/HEIGHT on stock robot.
        const int faceWidth = 1280;
        const int faceHeight = 720;
        var view = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewConfig"] = new
            {
                type = "View",
                id = "global_not_matched_text"
            },
            ["componentConfigs"] = new object[]
            {
                new
                {
                    id = "bottom",
                    type = "Label",
                    text = $"\"{transcript}\"",
                    style = new
                    {
                        fontSize = "120",
                        fontFamily = "Proxima Nova Soft",
                        fontStyle = "bold",
                        fill = "#b6b6c2",
                        wordWrap = true,
                        wordWrapWidth = 1240,
                        align = "center"
                    },
                    position = new
                    {
                        x = faceWidth / 2,
                        y = faceHeight / 2
                    },
                    targetAnchor = new
                    {
                        x = 0.5,
                        y = 0.5
                    }
                }
            }
        };

        // pause:false so the Label clears with the announcement (Mim treats undefined pause as true).
        var resolvedGuiContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "Javascript",
            ["data"] = view,
            ["pause"] = false
        };
        config["gui"] = new
        {
            type = "Javascript",
            data = $"views.{viewName}",
            pause = false
        };
        config["display"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["view"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "Javascript",
                ["data"] = view,
                ["pause"] = false,
                ["context"] = resolvedGuiContext
            }
        };
        config["views"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [viewName] = view
        };
        config["local"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["views"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [viewName] = view
            }
        };
    }

    private static IReadOnlyList<object> BuildWeatherHiLoSequenceChildren(
        IReadOnlyList<WeatherHiLoSequenceCard> cards,
        string promptSubCategory,
        string mimId,
        string mimType)
    {
        var children = new List<object>(cards.Count);
        for (var index = 0; index < cards.Count; index += 1)
        {
            var card = cards[index];
            var promptLabel = string.IsNullOrWhiteSpace(card.DayName)
                ? $"Day{index + 1}"
                : Regex.Replace(card.DayName, "[^A-Za-z0-9]", string.Empty, RegexOptions.CultureInvariant);
            var promptId = $"WeatherForecast{promptLabel}_AN_13";
            var spokenLine = string.IsNullOrWhiteSpace(card.SpokenLine)
                ? "Here is another day's forecast."
                : card.SpokenLine!;
            var icon = string.IsNullOrWhiteSpace(card.Icon)
                ? "cloudy"
                : card.Icon!;
            var esml =
                $"<speak><anim cat='weather' meta='{icon}' nonBlocking='true' /><break size='0.2'/><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(spokenLine)}</es></speak>";
            var resolvedGuiContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "Javascript",
                ["data"] = card.View,
                ["pause"] = true
            };

            children.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "SLIM",
                ["config"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["play"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["esml"] = esml,
                        ["meta"] = new
                        {
                            prompt_id = promptId,
                            prompt_sub_category = promptSubCategory,
                            mim_id = mimId,
                            mim_type = mimType
                        }
                    },
                    ["gui"] = new
                    {
                        type = "Javascript",
                        data = "views.weatherHiLo",
                        pause = true
                    },
                    ["display"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["view"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["type"] = "Javascript",
                            ["data"] = card.View,
                            ["pause"] = true,
                            ["context"] = resolvedGuiContext
                        }
                    },
                    ["timeout"] = 6,
                    ["barge_in"] = true,
                    ["no_matches_for_gui"] = 0,
                    ["no_inputs_for_gui"] = 0
                }
            });
        }

        return children;
    }

    private static IDictionary<string, object?>[] ReadPayloadObjectArray(object rawValue)
    {
        return rawValue switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } jsonArray => jsonArray.EnumerateArray()
                .Select(ConvertJsonObjectToDictionary)
                .Where(static item => item is not null)
                .Cast<IDictionary<string, object?>>()
                .ToArray(),
            IEnumerable<object?> rawObjects => rawObjects.Select(ConvertObjectToDictionary)
                .Where(static item => item is not null)
                .Cast<IDictionary<string, object?>>()
                .ToArray(),
            _ => []
        };
    }

    private static IDictionary<string, object?>? ConvertObjectToDictionary(object? value)
    {
        return value switch
        {
            null => null,
            IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary,
                StringComparer.OrdinalIgnoreCase),
            _ => value is JsonElement jsonValue ? ConvertJsonObjectToDictionary(jsonValue) : null
        };
    }

    private static IDictionary<string, object?>? ConvertJsonObjectToDictionary(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            dictionary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when property.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => ConvertJsonObjectToDictionary(property.Value),
                JsonValueKind.Array => property.Value,
                _ => null
            };

        return dictionary;
    }

    private static object? BuildWeatherHiLoView(IDictionary<string, object?>? payload)
    {
        if (!TryReadPayloadBool(payload, "weather_view_enabled")) return null;

        if (!string.Equals(
                ReadPayloadString(payload, "weather_view_kind"),
                "weatherHiLo",
                StringComparison.OrdinalIgnoreCase))
            return null;

        var icon = ReadPayloadString(payload, "weather_icon");
        var unit = ReadPayloadString(payload, "weather_unit") ?? "F";
        var theme = ReadPayloadString(payload, "weather_theme") ?? "Normal";
        var high = TryReadPayloadInt(payload, "weather_high");
        var low = TryReadPayloadInt(payload, "weather_low");
        if (string.IsNullOrWhiteSpace(icon) || high is null || low is null) return null;

        var hiNumX = GetTemperatureLabelXPosition(370, high.Value);
        var hiUnitX = GetTemperatureLabelXPosition(360, high.Value);
        var loNumX = GetTemperatureLabelXPosition(1110, low.Value);
        var loUnitX = GetTemperatureLabelXPosition(1100, low.Value);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewConfig"] = new
            {
                type = "View",
                id = "weatherTempView",
                category = "gui"
            },
            ["open"] = new
            {
                transitionOpen = "trans_in",
                removeAll = true
            },
            ["defaultSelect"] = new
            {
                transitionClose = "trans_out",
                removeAll = true,
                leaveEmpty = false
            },
            ["componentConfigs"] = new object[]
            {
                new
                {
                    id = "tempBGClip",
                    type = "Clip",
                    assets = new object[]
                    {
                        new
                        {
                            id = "tempBG",
                            src = $"assets/personal-report-skill/weather/bg/temp{theme}_v01.crn",
                            type = "texture"
                        }
                    },
                    position = new { x = 36, y = 0 }
                },
                new
                {
                    id = "iconClip",
                    type = "Clip",
                    assets = new object[]
                    {
                        new
                        {
                            id = "icon",
                            src = $"assets/personal-report-skill/weather/icons/{icon}_v01.crn",
                            type = "texture"
                        }
                    },
                    position = new { x = 475, y = 195 }
                },
                new
                {
                    id = "hiNumLabel",
                    type = "Label",
                    text = $"{high.Value}°",
                    style = new
                    {
                        fontSize = "160",
                        fontFamily = "Proxima Nova Soft",
                        fontWeight = "bold",
                        fill = "#FFFFFF",
                        align = "center"
                    },
                    position = new { x = hiNumX, y = 430 },
                    targetAnchor = new { x = 1, y = 1 }
                },
                new
                {
                    id = "hiUnitLabel",
                    type = "Label",
                    text = unit,
                    style = new
                    {
                        fontSize = "90",
                        fontFamily = "Proxima Nova Soft",
                        fontWeight = "bold",
                        fill = "#FFFFFF",
                        align = "center"
                    },
                    position = new { x = hiUnitX, y = 418 },
                    targetAnchor = new { x = 0, y = 1 }
                },
                new
                {
                    id = "loNumLabel",
                    type = "Label",
                    text = $"{low.Value}°",
                    style = new
                    {
                        fontSize = "160",
                        fontFamily = "Proxima Nova Soft",
                        fontWeight = "bold",
                        fill = "#FFFFFF",
                        align = "center"
                    },
                    position = new { x = loNumX, y = 430 },
                    targetAnchor = new { x = 1, y = 1 }
                },
                new
                {
                    id = "loUnitLabel",
                    type = "Label",
                    text = unit,
                    style = new
                    {
                        fontSize = "90",
                        fontFamily = "Proxima Nova Soft",
                        fontWeight = "bold",
                        fill = "#FFFFFF",
                        align = "center"
                    },
                    position = new { x = loUnitX, y = 418 },
                    targetAnchor = new { x = 0, y = 1 }
                },
                new
                {
                    id = "hiTextLabel",
                    type = "Label",
                    text = "Hi",
                    style = new
                    {
                        fontSize = "60",
                        fontFamily = "Proxima Nova Light",
                        fill = "#FFFFFF",
                        align = "center"
                    },
                    position = new { x = 280, y = 496 },
                    targetAnchor = new { x = 0.5, y = 1 }
                },
                new
                {
                    id = "loTextLabel",
                    type = "Label",
                    text = "Lo",
                    style = new
                    {
                        fontSize = "60",
                        fontFamily = "Proxima Nova Light",
                        fill = "#FFFFFF",
                        align = "center"
                    },
                    position = new { x = 990, y = 496 },
                    targetAnchor = new { x = 0.5, y = 1 }
                }
            }
        };
    }

    private static int GetTemperatureLabelXPosition(int baseX, int temperature)
    {
        const int xOffset = 70;
        return temperature switch
        {
            < -9 or > 99 => baseX + xOffset,
            >= 0 and < 10 => baseX - xOffset,
            _ => baseX
        };
    }

    private static int? TryReadPayloadInt(IDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            int number => number,
            long number and <= int.MaxValue and >= int.MinValue => (int)number,
            double number => (int)Math.Round(number, MidpointRounding.AwayFromZero),
            float number => (int)Math.Round(number, MidpointRounding.AwayFromZero),
            string text when int.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumber when jsonNumber.TryGetInt32(out var parsed) =>
                parsed,
            JsonElement { ValueKind: JsonValueKind.String } jsonText when int.TryParse(jsonText.GetString(),
                out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryReadPayloadBool(IDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null) return false;

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } jsonText when bool.TryParse(jsonText.GetString(),
                out var parsed) => parsed,
            _ => false
        };
    }

    private sealed record WeatherHiLoSequenceCard(
        object View,
        string? DayName,
        string? Icon,
        string? SpokenLine);

    public sealed record SocketReplyPlan(string Text, int DelayMs = 0);
}
