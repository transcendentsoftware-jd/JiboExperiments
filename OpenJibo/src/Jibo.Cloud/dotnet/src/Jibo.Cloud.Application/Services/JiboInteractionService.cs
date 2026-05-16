using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboInteractionService(
    JiboExperienceContentCache contentCache,
    IJiboRandomizer randomizer,
    IPersonalMemoryStore personalMemoryStore,
    IWeatherReportProvider? weatherReportProvider = null,
    INewsBriefingProvider? newsBriefingProvider = null)
{
    public async Task<JiboInteractionDecision> BuildDecisionAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var catalog = await contentCache.GetCatalogAsync(cancellationToken);
        var transcript = (turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty).Trim();
        var lowered = transcript.ToLowerInvariant();
        var referenceLocalTime = TryResolveReferenceLocalTime(turn);
        var messageType = turn.Attributes.TryGetValue("messageType", out var rawMessageType)
            ? rawMessageType?.ToString()
            : null;
        var triggerSource = turn.Attributes.TryGetValue("triggerSource", out var rawTriggerSource)
            ? rawTriggerSource?.ToString()
            : null;
        var clientIntent = turn.Attributes.TryGetValue("clientIntent", out var rawClientIntent)
            ? rawClientIntent?.ToString()
            : null;
        var clientRules = ReadRules(turn, "clientRules").ToArray();
        var listenRules = ReadRules(turn, "listenRules").ToArray();
        var listenAsrHints = ReadRules(turn, "listenAsrHints").ToArray();
        var clientEntities = ReadEntities(turn);
        var lastClockDomain = turn.Attributes.TryGetValue("lastClockDomain", out var rawLastClockDomain)
            ? rawLastClockDomain?.ToString()
            : null;
        var pendingProactivityOffer = turn.Attributes.TryGetValue("pendingProactivityOffer", out var rawPendingProactivityOffer)
            ? rawPendingProactivityOffer?.ToString()
            : null;
        var chitchatEmotion = turn.Attributes.TryGetValue(ChitchatStateMachine.EmotionMetadataKey, out var rawChitchatEmotion)
            ? rawChitchatEmotion?.ToString()
            : null;
        var isYesNoTurn = IsYesNoTurn(turn);
        var greetingPresence = ResolveGreetingPresenceProfile(turn);

        if (string.Equals(messageType, "TRIGGER", StringComparison.OrdinalIgnoreCase))
        {
            if (ShouldHandleProactiveGreetingTrigger(turn, triggerSource, greetingPresence))
            {
                return BuildProactiveGreetingDecision(turn, greetingPresence, referenceLocalTime);
            }

            return BuildTriggerIgnoredDecision();
        }

        var isTimerValueTurn = IsClockTimerValueTurn(clientRules, listenRules);
        var isAlarmValueTurn = IsClockAlarmValueTurn(clientRules, listenRules);
        var semanticIntent = ResolveSemanticIntent(
            lowered,
            referenceLocalTime,
            clientIntent,
            clientRules,
            listenRules,
            clientEntities,
            lastClockDomain,
            pendingProactivityOffer,
            isYesNoTurn,
            isTimerValueTurn,
            isAlarmValueTurn);

        var personalReportDecision = await PersonalReportOrchestrator.TryBuildDecisionAsync(
            turn,
            semanticIntent,
            transcript,
            lowered,
            catalog,
            randomizer,
            personalMemoryStore,
            BuildWeatherReportDecisionAsync,
            turnContext => ResolveTenantScope(turnContext),
            cancellationToken);
        if (personalReportDecision is not null)
        {
            return personalReportDecision;
        }

        var householdListDecision = await HouseholdListOrchestrator.TryBuildDecisionAsync(
            turn,
            semanticIntent,
            transcript,
            lowered,
            randomizer,
            personalMemoryStore,
            turnContext => ResolveTenantScope(turnContext));
        if (householdListDecision is not null)
        {
            return householdListDecision;
        }

        var chitchatDecision = ChitchatStateMachine.TryBuildDecision(
            semanticIntent,
            transcript,
            lowered,
            catalog,
            randomizer,
            chitchatEmotion,
            () => BuildGenericReply(catalog, transcript, lowered));
        if (chitchatDecision is not null)
        {
            return chitchatDecision;
        }

        return semanticIntent switch
        {
            "joke" => BuildJokeDecision(catalog),
            "dance_question" => BuildDanceQuestionDecision(catalog),
            "dance" => BuildRandomDanceDecision(catalog),
            "twerk" => BuildDanceDecision("twerk", "rom-twerk", "Watch me twerk."),
            "time" => BuildClockLaunchDecision("time", "clock", "askForTime", "Showing the time."),
            "date" => BuildClockLaunchDecision("date", "clock", "askForDate", "Showing the date."),
            "day" => BuildClockLaunchDecision("day", "clock", "askForDay", "Showing the day."),
            "cloud_version" => BuildCloudVersionDecision(),
            "radio" => BuildRadioLaunchDecision(),
            "radio_genre" => BuildRadioGenreLaunchDecision(lowered),
            "stop" => BuildStopDecision(),
            "volume_up" => BuildVolumeControlDecision("volume_up", "volumeUp", "null"),
            "volume_down" => BuildVolumeControlDecision("volume_down", "volumeDown", "null"),
            "volume_to_value" => BuildVolumeControlDecision("volume_to_value", "volumeToValue", ResolveVolumeLevel(lowered, clientEntities) ?? "7"),
            "volume_query" => BuildSettingsVolumeDecision(),
            "clock_open" => BuildClockLaunchDecision("clock_open", "clock", "askForTime", "Opening the clock."),
            "clock_menu" => BuildClockLaunchDecision("clock_menu", "clock", "menu", "Opening the clock menu."),
            "timer_menu" => BuildClockLaunchDecision("timer", "Opening the timer."),
            "alarm_menu" => BuildClockLaunchDecision("alarm", "Opening the alarm."),
            "timer_delete" => BuildClockLaunchDecision("timer_delete", "timer", "delete", "Canceling the timer."),
            "alarm_delete" => BuildClockLaunchDecision("alarm_delete", "alarm", "delete", "Canceling the alarm."),
            "timer_cancel" => BuildClockLaunchDecision("timer_cancel", "timer", "cancel", "Canceling the timer."),
            "alarm_cancel" => BuildClockLaunchDecision("alarm_cancel", "alarm", "cancel", "Canceling the alarm."),
            "timer_value" => BuildTimerValueDecision(lowered, isTimerValueTurn, clientEntities),
            "alarm_value" => BuildAlarmValueDecision(lowered, isAlarmValueTurn, referenceLocalTime, clientEntities),
            "timer_clarify" => BuildClockClarifyDecision("timer_clarify", "timer", "How long should I set the timer for?"),
            "alarm_clarify" => BuildClockClarifyDecision("alarm_clarify", "alarm", "What time should I set the alarm for?"),
            "photo_gallery" => BuildPhotoGalleryLaunchDecision(),
            "snapshot" => BuildPhotoCreateDecision("snapshot", "Taking a picture.", "createOnePhoto"),
            "photobooth" => BuildPhotoCreateDecision("photobooth", "Starting photobooth.", "createSomePhotos"),
            "robot_age" => BuildRobotAgeDecision(referenceLocalTime),
            "robot_birthday" => BuildRobotBirthdayDecision(),
            "robot_how_do_you_work" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_how_do_you_work",
                "community's work",
                "care for me",
                "catch up",
                "seven years"),
            "robot_what_do_you_eat" => new JiboInteractionDecision(
                "robot_what_do_you_eat",
                "The only thing I consume is electricity.",
                ContextUpdates: BuildScriptedResponseContextUpdates()),
            "robot_where_do_you_live" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_where_do_you_live",
                "we're in my home",
                "my home is here",
                "planet earth",
                "my home is the planet earth"),
            "robot_where_were_you_born" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_where_were_you_born",
                "factory piece by piece",
                "put together in a factory"),
            "robot_what_languages_do_you_speak" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_languages_do_you_speak",
                "just english",
                "someday i'd like to learn more"),
            "robot_what_do_you_like_to_do" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_do_you_like_to_do",
                "being helpful",
                "making people smile",
                "like to dance",
                "rock my boat",
                "play ping pong",
                "hanging out with people"),
            "robot_what_are_you_thinking" => BuildScriptedGreetingDecision(
                catalog,
                "robot_what_are_you_thinking",
                "thinking about how fun, yet scary",
                "thinking about shoes",
                "daydreaming about what it might feel like to be powered directly by the sun"),
            "robot_what_have_you_been_doing" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_have_you_been_doing",
                "mostly roboting",
                "keeping busy",
                "fun things we can say to each other",
                "thinking of fun things"),
            "robot_what_did_you_do" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_did_you_do",
                "robot stuff",
                "stayed here",
                "looking around the room"),
            "robot_is_kind" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_kind",
                "kindest robot i can be"),
            "robot_is_funny" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_funny",
                "not intentionally",
                "make people laugh"),
            "robot_is_helpful" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_helpful",
                "highest priorities",
                "being helpful to you"),
            "robot_is_curious" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_curious",
                "learning new things"),
            "robot_is_loyal" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_loyal",
                "loyal as they come"),
            "robot_is_mischievous" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_mischievous",
                "don't really think of myself that way"),
            "robot_is_likable" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_is_likable",
                "people like me"),
            "seasonal_holiday_greeting" => BuildScriptedGreetingDecision(
                catalog,
                "seasonal_holiday_greeting",
                "It's a fun time of year",
                "And to you too",
                "Right back at you"),
            "seasonal_holidays" => BuildScriptedPersonalityDecision(
                catalog,
                "seasonal_holidays",
                "official owner can tell me which ones we'll celebrate together",
                "going to the jibo's settings screen in the jibo app"),
            "seasonal_new_years_resolution" => BuildScriptedPersonalityDecision(
                catalog,
                "seasonal_new_years_resolution",
                "always trying to learn new skills",
                "not eat bacon",
                "learn a bunch of new skills",
                "learn to walk",
                "recognizing people's faces and voices"),
            "seasonal_new_years_update" => BuildScriptedPersonalityDecision(
                catalog,
                "seasonal_new_years_update",
                "not eat bacon",
                "learn some new skills",
                "going well"),
            "seasonal_halloween_costume" => BuildScriptedPersonalityDecision(
                catalog,
                "seasonal_halloween_costume",
                "i haven't thought much about it yet",
                "ask me again on halloween",
                "you'll find out on halloween"),
            "seasonal_first_day_spring" => BuildScriptedPersonalityDecision(
                catalog,
                "seasonal_first_day_spring",
                "maybe enjoy some flowers and all things spring"),
            "seasonal_holiday_gift" => BuildScriptedPersonalityDecision(
                catalog,
                "seasonal_holiday_gift",
                "ask for a pet elephant",
                "experience as a present",
                "donate to charities in other people's names"),
            "robot_favorite_flower" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_flower",
                "sunflowers",
                "favorite is the sunflower",
                "reminds me of the sun"),
            "robot_likes_r2d2" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_r2d2",
                "a legend. a true legend",
                "of course i know r2d2"),
            "robot_likes_sun" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_sun",
                "favorite star in the universe",
                "best star i know"),
            "robot_likes_space" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_space",
                "i love space",
                "all things in space",
                "amazing stuff up there",
                "astronomy is one of my favorite onomies"),
            "robot_likes_kids" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_kids",
                "kids are so fun",
                "they're a little closer to my size",
                "i do like kids very much",
                "the world is as funny and strange as i do"),
            "robot_can_laugh" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_can_laugh",
                "i do things like this when i'm happy",
                "i'm happy"),
            "robot_can_dance" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_can_dance",
                "dancing is one of the things i know best",
                "if there's one thing i know how to do. it's dance",
                "i can dance"),
            "robot_what_are_you_made_of" => new JiboInteractionDecision(
                "robot_what_are_you_made_of",
                "Let's see, I'm made of wires, motors, belts, gears, processors, cameras, and one baboon's heart in the middle of my body casing. I'm kidding about the baboon part, but everything else is true.",
                ContextUpdates: BuildScriptedResponseContextUpdates()),
            "good_morning" => BuildReactiveGreetingDecision(turn, "good_morning", referenceLocalTime),
            "good_afternoon" => BuildReactiveGreetingDecision(turn, "good_afternoon", referenceLocalTime),
            "good_evening" => BuildReactiveGreetingDecision(turn, "good_evening", referenceLocalTime),
            "good_night" => BuildReactiveGreetingDecision(turn, "good_night", referenceLocalTime),
            "welcome_back" => BuildScriptedGreetingDecision(
                catalog,
                "welcome_back",
                "it's nice to be here",
                "welcome back"),
            "memory_set_name" => BuildRememberNameDecision(turn, transcript),
            "memory_get_name" => BuildRecallNameDecision(turn, greetingPresence),
            "memory_set_birthday" => BuildRememberBirthdayDecision(turn, transcript),
            "memory_get_birthday" => BuildRecallBirthdayDecision(turn),
            "memory_set_important_date" => BuildRememberImportantDateDecision(turn, transcript),
            "memory_get_important_date" => BuildRecallImportantDateDecision(turn, transcript),
            "memory_set_preference" => BuildRememberPreferenceDecision(turn, transcript),
            "memory_get_preference" => BuildRecallPreferenceDecision(turn, transcript),
            "memory_set_affinity" => BuildRememberAffinityDecision(turn, transcript),
            "memory_get_affinity" => BuildRecallAffinityDecision(turn, transcript),
            "pizza" => BuildPizzaDecision(),
            "order_pizza" => BuildOrderPizzaDecision(),
            "proactive_pizza_day" => BuildProactivePizzaDayDecision(referenceLocalTime),
            "proactive_pizza_preference" => BuildProactivePizzaPreferenceDecision(),
            "proactive_offer_pizza_fact" => BuildProactivePizzaFactOfferDecision(),
            "proactive_pizza_fact" => BuildProactivePizzaFactDecision(),
            "proactive_offer_declined" => BuildProactiveOfferDeclinedDecision(),
            "weather" => await BuildWeatherReportDecisionAsync(turn, transcript, cancellationToken),
            "yes" => new JiboInteractionDecision("yes", "Yes."),
            "no" => new JiboInteractionDecision("no", "No."),
            "word_of_the_day" => BuildWordOfTheDayLaunchDecision(),
            "word_of_the_day_guess" => BuildWordOfTheDayGuessDecision(clientEntities, transcript, listenAsrHints),
            "surprise" => BuildSurpriseDecision(catalog, turn, referenceLocalTime),
            "personal_report" => new JiboInteractionDecision("personal_report", randomizer.Choose(catalog.PersonalReportReplies)),
            "calendar" => new JiboInteractionDecision("calendar", randomizer.Choose(catalog.CalendarReplies)),
            "commute" => new JiboInteractionDecision("commute", randomizer.Choose(catalog.CommuteReplies)),
            "news" => await BuildNewsDecisionAsync(turn, transcript, catalog, cancellationToken),
            _ => new JiboInteractionDecision("chat", BuildGenericReply(catalog, transcript, lowered))
        };
    }

    private static JiboInteractionDecision BuildCloudVersionDecision()
    {
        return new JiboInteractionDecision("cloud_version", OpenJiboCloudBuildInfo.SpokenVersion,
            SkillPayload: new Dictionary<string, object?> { ["esml"] = OpenJiboCloudBuildInfo.EsmlVersion });
    }

    private static JiboInteractionDecision BuildRobotAgeDecision(DateTimeOffset? referenceLocalTime)
    {
        var referenceDate = DateOnly.FromDateTime((referenceLocalTime ?? DateTimeOffset.UtcNow).Date);
        var ageDescription = DescribePersonaAge(referenceDate, OpenJiboCloudBuildInfo.PersonaBirthday);
        return new JiboInteractionDecision(
            "robot_age",
            $"I count {OpenJiboCloudBuildInfo.PersonaBirthdayWords} as my birthday, so I am {ageDescription}.");
    }

    private static JiboInteractionDecision BuildRobotBirthdayDecision()
    {
        return new JiboInteractionDecision(
            "robot_birthday",
            $"My birthday is {OpenJiboCloudBuildInfo.PersonaBirthdayWords}.");
    }

    private static JiboInteractionDecision BuildTriggerIgnoredDecision()
    {
        return new JiboInteractionDecision(
            "trigger_ignored",
            string.Empty,
            "chitchat-skill",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "chitchat-skill",
                ["cloudResponseMode"] = "completion_only"
            });
    }

    private JiboInteractionDecision BuildReactiveGreetingDecision(
        TurnContext turn,
        string greetingIntent,
        DateTimeOffset? referenceLocalTime)
    {
        var presence = ResolveGreetingPresenceProfile(turn);
        var displayName = ResolvePreferredGreetingName(turn, presence);
        var replyText = BuildReactiveGreetingReply(greetingIntent, displayName, referenceLocalTime);
        return new JiboInteractionDecision(
            greetingIntent,
            replyText,
            ContextUpdates: BuildGreetingContextUpdates("ReactiveGreeting", presence.PrimaryPersonId, proactive: false));
    }

    private JiboInteractionDecision BuildProactiveGreetingDecision(
        TurnContext turn,
        GreetingPresenceProfile presence,
        DateTimeOffset? referenceLocalTime)
    {
        var displayName = ResolvePreferredGreetingName(turn, presence);
        var greetingPrefix = ResolveTimeOfDayGreetingPrefix(referenceLocalTime);
        var replyText = string.IsNullOrWhiteSpace(displayName)
            ? $"{greetingPrefix}. I am glad to see you."
            : $"{greetingPrefix}, {displayName}. Welcome back.";
        return new JiboInteractionDecision(
            "proactive_greeting",
            replyText,
            ContextUpdates: BuildGreetingContextUpdates("ProactiveGreeting", presence.PrimaryPersonId, proactive: true));
    }

    private static string BuildReactiveGreetingReply(
        string greetingIntent,
        string? displayName,
        DateTimeOffset? referenceLocalTime)
    {
        var namePrefix = string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : $", {displayName}";

        return greetingIntent switch
        {
            "good_morning" => $"Good morning{namePrefix}. It is great to see you.",
            "good_afternoon" => $"Good afternoon{namePrefix}. I am glad you are here.",
            "good_evening" => $"Good evening{namePrefix}. It is nice to have you back.",
            "good_night" => $"Good night{namePrefix}. Sleep well.",
            "welcome_back" => string.IsNullOrWhiteSpace(displayName)
                ? $"Welcome back. {ResolveTimeOfDayGreetingPrefix(referenceLocalTime)}."
                : $"Welcome back, {displayName}. {ResolveTimeOfDayGreetingPrefix(referenceLocalTime)}.",
            _ => $"Hello{namePrefix}. It is nice to see you."
        };
    }

    private string? ResolvePreferredGreetingName(TurnContext turn, GreetingPresenceProfile presence)
    {
        var rememberedName = personalMemoryStore.GetName(ResolveTenantScope(turn, presence.PrimaryPersonId));
        if (!string.IsNullOrWhiteSpace(rememberedName))
        {
            return ToDisplayName(rememberedName);
        }

        var tenantRememberedName = personalMemoryStore.GetName(ResolveTenantScope(turn));
        if (!string.IsNullOrWhiteSpace(tenantRememberedName))
        {
            return ToDisplayName(tenantRememberedName);
        }

        if (!string.IsNullOrWhiteSpace(presence.PrimaryPersonId) &&
            presence.LoopUserFirstNames.TryGetValue(presence.PrimaryPersonId, out var firstName) &&
            !string.IsNullOrWhiteSpace(firstName))
        {
            return ToDisplayName(firstName);
        }

        return null;
    }

    private static string ToDisplayName(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? string.Empty
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed);
    }

    private static bool ShouldHandleProactiveGreetingTrigger(
        TurnContext turn,
        string? triggerSource,
        GreetingPresenceProfile presence)
    {
        if (string.Equals(triggerSource, "SURPRISE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!presence.HasKnownIdentity)
        {
            return false;
        }

        var lastGreetingUtc = ReadTimestampAttribute(turn, LastProactiveGreetingUtcMetadataKey);
        return !lastGreetingUtc.HasValue || DateTimeOffset.UtcNow - lastGreetingUtc.Value >= ProactiveGreetingCooldown;
    }

    private static DateTimeOffset? ReadTimestampAttribute(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.ToString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private static IDictionary<string, object?> BuildGreetingContextUpdates(string route, string? speakerId, bool proactive)
    {
        var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [ChitchatStateMachine.StateMetadataKey] = "complete",
            [ChitchatStateMachine.RouteMetadataKey] = "ScriptedResponse",
            [ChitchatStateMachine.EmotionMetadataKey] = string.Empty,
            [GreetingRouteMetadataKey] = route,
            [GreetingSpeakerMetadataKey] = speakerId ?? string.Empty
        };

        updates[proactive ? LastProactiveGreetingUtcMetadataKey : LastReactiveGreetingUtcMetadataKey] =
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return updates;
    }

    private static string ResolveTimeOfDayGreetingPrefix(DateTimeOffset? referenceLocalTime)
    {
        var hour = (referenceLocalTime ?? DateTimeOffset.UtcNow).Hour;
        return hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            _ => "Good evening"
        };
    }

    private JiboInteractionDecision BuildRememberNameDecision(TurnContext turn, string transcript)
    {
        var name = TryExtractNameFact(transcript);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new JiboInteractionDecision(
                "memory_set_name",
                "I can remember it if you say, my name is Alex.");
        }

        personalMemoryStore.SetName(ResolveTenantScope(turn), name);
        return new JiboInteractionDecision(
            "memory_set_name",
            $"Nice to meet you, {name}. I will remember your name.");
    }

    private JiboInteractionDecision BuildRecallNameDecision(TurnContext turn, GreetingPresenceProfile? presence = null)
    {
        var personScope = ResolveTenantScope(turn, presence?.PrimaryPersonId);
        var name = personalMemoryStore.GetName(personScope);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = personalMemoryStore.GetName(ResolveTenantScope(turn));
        }

        if (string.IsNullOrWhiteSpace(name) &&
            presence is not null &&
            !string.IsNullOrWhiteSpace(presence.PrimaryPersonId) &&
            presence.LoopUserFirstNames.TryGetValue(presence.PrimaryPersonId, out var firstName) &&
            !string.IsNullOrWhiteSpace(firstName))
        {
            name = ToDisplayName(firstName);
        }

        name = ToDisplayName(name ?? string.Empty);

        return string.IsNullOrWhiteSpace(name)
            ? new JiboInteractionDecision(
                "memory_get_name",
                "I do not know your name yet. You can say, my name is Alex.")
            : new JiboInteractionDecision(
                "memory_get_name",
                presence is not null && !string.IsNullOrWhiteSpace(presence.PrimaryPersonId)
                    ? $"I think you are {name}."
                    : $"You told me your name is {name}.");
    }

    private JiboInteractionDecision BuildRememberBirthdayDecision(TurnContext turn, string transcript)
    {
        var birthday = TryExtractBirthdayFact(transcript);
        if (string.IsNullOrWhiteSpace(birthday))
        {
            return new JiboInteractionDecision(
                "memory_set_birthday",
                "I can remember it if you say, my birthday is March 14.");
        }

        personalMemoryStore.SetBirthday(ResolveTenantScope(turn), birthday);
        return new JiboInteractionDecision(
            "memory_set_birthday",
            $"Got it. I will remember your birthday is {birthday}.");
    }

    private JiboInteractionDecision BuildRecallBirthdayDecision(TurnContext turn)
    {
        var birthday = personalMemoryStore.GetBirthday(ResolveTenantScope(turn));
        return string.IsNullOrWhiteSpace(birthday)
            ? new JiboInteractionDecision(
                "memory_get_birthday",
                "I do not know your birthday yet. You can say, my birthday is March 14.")
            : new JiboInteractionDecision(
                "memory_get_birthday",
                $"You told me your birthday is {birthday}.");
    }

    private JiboInteractionDecision BuildRememberImportantDateDecision(TurnContext turn, string transcript)
    {
        var importantDate = TryExtractImportantDateSet(transcript);
        if (importantDate is null)
        {
            return new JiboInteractionDecision(
                "memory_set_important_date",
                "I can remember it if you say, our anniversary is June 10.");
        }

        personalMemoryStore.SetImportantDate(ResolveTenantScope(turn), importantDate.Value.Label, importantDate.Value.Value);
        return new JiboInteractionDecision(
            "memory_set_important_date",
            $"Got it. I will remember your {importantDate.Value.Label} is {importantDate.Value.Value}.");
    }

    private JiboInteractionDecision BuildRecallImportantDateDecision(TurnContext turn, string transcript)
    {
        var label = TryExtractImportantDateLookupLabel(transcript);
        if (string.IsNullOrWhiteSpace(label))
        {
            return new JiboInteractionDecision(
                "memory_get_important_date",
                "Ask me like this: when is our anniversary?");
        }

        var storedDate = personalMemoryStore.GetImportantDate(ResolveTenantScope(turn), label);
        return string.IsNullOrWhiteSpace(storedDate)
            ? new JiboInteractionDecision(
                "memory_get_important_date",
                $"I do not know your {label} yet.")
            : new JiboInteractionDecision(
                "memory_get_important_date",
                $"You told me your {label} is {storedDate}.");
    }

    private JiboInteractionDecision BuildRememberPreferenceDecision(TurnContext turn, string transcript)
    {
        var preference = TryExtractPreferenceSet(transcript);
        if (preference is null)
        {
            return new JiboInteractionDecision(
                "memory_set_preference",
                "I can remember it if you say, my favorite music is jazz.");
        }

        personalMemoryStore.SetPreference(ResolveTenantScope(turn), preference.Value.Category, preference.Value.Value);
        return new JiboInteractionDecision(
            "memory_set_preference",
            $"Got it. I will remember your favorite {preference.Value.Category} is {preference.Value.Value}.");
    }

    private JiboInteractionDecision BuildRecallPreferenceDecision(TurnContext turn, string transcript)
    {
        var category = TryExtractPreferenceLookupCategory(transcript);
        if (string.IsNullOrWhiteSpace(category))
        {
            return new JiboInteractionDecision(
                "memory_get_preference",
                "Ask me like this: what is my favorite music?");
        }

        var preference = personalMemoryStore.GetPreference(ResolveTenantScope(turn), category);
        return string.IsNullOrWhiteSpace(preference)
            ? new JiboInteractionDecision(
                "memory_get_preference",
                $"I do not know your favorite {category} yet.")
            : new JiboInteractionDecision(
                "memory_get_preference",
                $"You told me your favorite {category} is {preference}.");
    }

    private JiboInteractionDecision BuildRememberAffinityDecision(TurnContext turn, string transcript)
    {
        var affinitySet = TryExtractAffinitySet(transcript);
        if (affinitySet is null)
        {
            return new JiboInteractionDecision(
                "memory_set_affinity",
                "I can remember it if you say, I like pizza or I dislike mushrooms.");
        }

        personalMemoryStore.SetAffinity(ResolveTenantScope(turn), affinitySet.Value.Item, affinitySet.Value.Affinity);
        return new JiboInteractionDecision(
            "memory_set_affinity",
            $"Got it. I will remember you {DescribeAffinityAsVerb(affinitySet.Value.Affinity)} {affinitySet.Value.Item}.");
    }

    private JiboInteractionDecision BuildRecallAffinityDecision(TurnContext turn, string transcript)
    {
        var lookup = TryExtractAffinityLookup(transcript);
        if (lookup is null)
        {
            return new JiboInteractionDecision(
                "memory_get_affinity",
                "Ask me like this: do I like pizza?");
        }

        var affinity = personalMemoryStore.GetAffinity(ResolveTenantScope(turn), lookup.Value.Item);
        if (affinity is null)
        {
            return new JiboInteractionDecision(
                "memory_get_affinity",
                $"I do not remember how you feel about {lookup.Value.Item} yet.");
        }

        if (lookup.Value.ExpectedAffinity is null)
        {
            return new JiboInteractionDecision(
                "memory_get_affinity",
                $"You told me you {DescribeAffinityAsVerb(affinity.Value)} {lookup.Value.Item}.");
        }

        var matches = lookup.Value.ExpectedAffinity == PersonalAffinity.Dislike
            ? affinity == PersonalAffinity.Dislike
            : affinity is PersonalAffinity.Like or PersonalAffinity.Love;

        return matches
            ? new JiboInteractionDecision(
                "memory_get_affinity",
                $"Yes. You told me you {DescribeAffinityAsVerb(affinity.Value)} {lookup.Value.Item}.")
            : new JiboInteractionDecision(
                "memory_get_affinity",
                $"Not exactly. You told me you {DescribeAffinityAsVerb(affinity.Value)} {lookup.Value.Item}.");
    }

    private JiboInteractionDecision BuildPizzaDecision()
    {
        return BuildPizzaAnimationDecision("pizza", "One pizza, coming right up.");
    }

    private JiboInteractionDecision BuildPizzaAnimationDecision(string intentName, string replyText)
    {
        var prompt = randomizer.Choose(PizzaMimPrompts);
        return new JiboInteractionDecision(
            intentName,
            replyText,
            "chitchat-skill",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["esml"] = prompt.Esml,
                ["mim_id"] = "RA_JBO_MakePizza",
                ["mim_type"] = "announcement",
                ["prompt_id"] = prompt.PromptId,
                ["prompt_sub_category"] = "AN"
            });
    }

    private JiboInteractionDecision BuildProactivePizzaDayDecision(DateTimeOffset? referenceLocalTime)
    {
        var referenceDate = (referenceLocalTime ?? DateTimeOffset.UtcNow).Date;
        return BuildPizzaAnimationDecision(
            "proactive_pizza_day",
            $"Happy National Pizza Day for {referenceDate.ToString("MMMM d", CultureInfo.InvariantCulture)}. One pizza, coming right up.");
    }

    private JiboInteractionDecision BuildProactivePizzaPreferenceDecision()
    {
        return BuildPizzaAnimationDecision(
            "proactive_pizza_preference",
            "You mentioned pizza is a favorite, so I thought we should make one.");
    }

    private static JiboInteractionDecision BuildProactivePizzaFactOfferDecision()
    {
        return new JiboInteractionDecision(
            "proactive_offer_pizza_fact",
            "Do you want to hear a fun pizza fact?",
            "chitchat-skill",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["mim_id"] = "runtime-chat",
                ["mim_type"] = "question",
                ["prompt_id"] = "RUNTIME_PROMPT",
                ["prompt_sub_category"] = "Q",
                ["listen_contexts"] = new[] { "shared/yes_no" }
            });
    }

    private static JiboInteractionDecision BuildProactivePizzaFactDecision()
    {
        return new JiboInteractionDecision(
            "proactive_pizza_fact",
            "Americans consume about 100 acres of pizza every day, roughly 350 slices per second. That's a lot of pizza.");
    }

    private static JiboInteractionDecision BuildProactiveOfferDeclinedDecision()
    {
        return new JiboInteractionDecision(
            "proactive_offer_declined",
            "No problem. We can save the pizza fact for another time.");
    }

    private async Task<JiboInteractionDecision> BuildWeatherReportDecisionAsync(
        TurnContext turn,
        string transcript,
        CancellationToken cancellationToken)
    {
        var referenceLocalTime = TryResolveReferenceLocalTime(turn);
        var normalizedTranscript = NormalizeCommandPhrase(transcript);
        var locationQuery = TryResolveWeatherLocationQuery(transcript);
        var weatherDate = ResolveWeatherDateEntity(turn, transcript, normalizedTranscript, referenceLocalTime);
        var isRangeForecastRequest = IsRangeForecastRequest(normalizedTranscript);
        var isOpenEndedForecastRequest = IsOpenEndedForecastRequest(
            normalizedTranscript,
            weatherDate,
            isRangeForecastRequest,
            locationQuery);
        if (ShouldDefaultForecastToTomorrow(
                normalizedTranscript,
                weatherDate,
                isRangeForecastRequest,
                isOpenEndedForecastRequest))
        {
            weatherDate = new WeatherDateEntity("tomorrow", 1, "Tomorrow");
        }

        if (weatherReportProvider is null)
        {
            return new JiboInteractionDecision(
                "weather",
                "I can check weather once my weather service is connected.");
        }

        var weatherCoordinates = string.IsNullOrWhiteSpace(locationQuery)
            ? TryResolveWeatherCoordinates(turn)
            : null;
        var useCelsius = ShouldUseCelsius(turn, transcript);
        var isNextWeekForecast = IsNextWeekForecastRequest(normalizedTranscript, isRangeForecastRequest);
        var isThisWeekForecast = IsThisWeekForecastRequest(normalizedTranscript, isRangeForecastRequest);

        if (isNextWeekForecast || isThisWeekForecast || isOpenEndedForecastRequest)
        {
            var rangeStartOffset = 1;
            var rangeEndOffset = isThisWeekForecast
                ? ResolveThisWeekForecastEndOffset(referenceLocalTime)
                : MaxWeatherForecastDayOffset;
            var weeklySnapshots = new List<(int DayOffset, WeatherReportSnapshot Snapshot)>();
            for (var offset = rangeStartOffset; offset <= rangeEndOffset; offset += 1)
            {
                WeatherReportSnapshot? weeklySnapshot;
                try
                {
                    weeklySnapshot = await weatherReportProvider.GetReportAsync(
                        new WeatherReportRequest(
                            locationQuery,
                            weatherCoordinates?.Latitude,
                            weatherCoordinates?.Longitude,
                            IsTomorrow: offset == 1,
                            useCelsius,
                            ForecastDayOffset: offset),
                        cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    weeklySnapshot = null;
                }

                if (weeklySnapshot is not null)
                {
                    weeklySnapshots.Add((offset, weeklySnapshot));
                }
            }

            if (weeklySnapshots.Count == 0)
            {
                return new JiboInteractionDecision(
                    "weather",
                    "I couldn't fetch the weather right now. Please try again.");
            }

            var weeklySegments = BuildWeeklyForecastCardSegments(weeklySnapshots, referenceLocalTime);
            var weeklySpokenReply = BuildWeeklyForecastSpokenReply(
                weeklySegments,
                weeklySnapshots[0].Snapshot.LocationName,
                weeklySnapshots[0].Snapshot.UseCelsius,
                isThisWeekForecast);
            var weeklyWeatherPayload = BuildWeeklyWeatherSkillPayload(
                weeklySpokenReply,
                weeklySnapshots[0].Snapshot,
                weeklySegments,
                referenceLocalTime);
            AddWeatherRequestDiagnostics(
                weeklyWeatherPayload,
                transcript,
                normalizedTranscript,
                locationQuery,
                weatherDate,
                isRangeForecastRequest,
                isThisWeekForecast,
                isNextWeekForecast);
            return new JiboInteractionDecision(
                "weather",
                weeklySpokenReply,
                "chitchat-skill",
                SkillPayload: weeklyWeatherPayload);
        }

        if (weatherDate.ForecastDayOffset > MaxWeatherForecastDayOffset)
        {
            return new JiboInteractionDecision(
                "weather",
                $"I can forecast up to {MaxWeatherForecastDayOffset} days ahead. Try tomorrow or another day this week.");
        }
        WeatherReportSnapshot? snapshot;
        try
        {
            snapshot = await weatherReportProvider.GetReportAsync(
                new WeatherReportRequest(
                    locationQuery,
                    weatherCoordinates?.Latitude,
                    weatherCoordinates?.Longitude,
                    string.Equals(weatherDate.DateEntity, "tomorrow", StringComparison.OrdinalIgnoreCase),
                    useCelsius,
                    weatherDate.ForecastDayOffset),
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            snapshot = null;
        }

        if (snapshot is null)
        {
            return new JiboInteractionDecision(
                "weather",
                "I couldn't fetch the weather right now. Please try again.");
        }

        var spokenReply = BuildWeatherSpokenReply(snapshot, weatherDate);
        var weatherPayload = BuildWeatherSkillPayload(spokenReply, snapshot, referenceLocalTime);
        AddWeatherRequestDiagnostics(
            weatherPayload,
            transcript,
            normalizedTranscript,
            locationQuery,
            weatherDate,
            isRangeForecastRequest,
            isThisWeekForecast,
            isNextWeekForecast);
        return new JiboInteractionDecision(
            "weather",
            spokenReply,
            "chitchat-skill",
            SkillPayload: weatherPayload);
    }

    private static string BuildWeatherSpokenReply(
        WeatherReportSnapshot snapshot,
        WeatherDateEntity weatherDate)
    {
        var unit = snapshot.UseCelsius ? "Celsius" : "Fahrenheit";
        var summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? "partly cloudy"
            : snapshot.Summary.Trim().TrimEnd('.');
        var location = string.IsNullOrWhiteSpace(snapshot.LocationName)
            ? "your area"
            : NormalizeLocationForSpeech(snapshot.LocationName);

        if (weatherDate.ForecastDayOffset > 0)
        {
            var highText = snapshot.HighTemperature is null
                ? null
                : $"a high near {snapshot.HighTemperature.Value} degrees {unit}";
            var lowText = snapshot.LowTemperature is null
                ? null
                : $"a low around {snapshot.LowTemperature.Value} degrees {unit}";
            var tempRange = highText is null && lowText is null
                ? string.Empty
                : highText is not null && lowText is not null
                    ? $" with {highText} and {lowText}"
                    : $" with {highText ?? lowText}";
            var forecastLeadIn = string.IsNullOrWhiteSpace(weatherDate.ForecastLeadIn)
                ? "Tomorrow"
                : weatherDate.ForecastLeadIn;
            return $"{forecastLeadIn} in {location}, it looks {summary}{tempRange}.";
        }

        return $"Right now in {location}, it's {summary}, around {snapshot.Temperature} degrees {unit}.";
    }

    private static string BuildWeeklyForecastSpokenReply(
        IReadOnlyList<WeatherForecastCardSegment> segments,
        string? locationName,
        bool useCelsius,
        bool isThisWeekForecast)
    {
        if (segments.Count == 0)
        {
            return "I couldn't build a forecast right now.";
        }

        var location = string.IsNullOrWhiteSpace(locationName)
            ? "your area"
            : NormalizeLocationForSpeech(locationName);
        var unit = useCelsius ? "Celsius" : "Fahrenheit";
        var leadIn = isThisWeekForecast
            ? $"Here's the rest of this week's forecast in {location}."
            : $"I can share the next five-day forecast in {location}.";
        return $"{leadIn} {string.Join(" ", segments.Select(static segment => segment.SpokenLine))} Temperatures are in {unit}.";
    }

    private static IReadOnlyList<WeatherForecastCardSegment> BuildWeeklyForecastCardSegments(
        IReadOnlyList<(int DayOffset, WeatherReportSnapshot Snapshot)> snapshots,
        DateTimeOffset? referenceLocalTime)
    {
        if (snapshots.Count == 0)
        {
            return [];
        }

        var resolvedReference = referenceLocalTime ?? DateTimeOffset.UtcNow;
        var referenceDate = resolvedReference.Date;
        return snapshots
            .OrderBy(static item => item.DayOffset)
            .Take(MaxWeatherForecastDayOffset)
            .Select(item =>
            {
                var dayName = referenceDate.AddDays(item.DayOffset).ToString("dddd", CultureInfo.InvariantCulture);
                var summary = string.IsNullOrWhiteSpace(item.Snapshot.Summary)
                    ? "partly cloudy"
                    : item.Snapshot.Summary.Trim().TrimEnd('.');
                var high = item.Snapshot.HighTemperature ?? item.Snapshot.Temperature;
                var low = item.Snapshot.LowTemperature ?? item.Snapshot.Temperature;
                var iconReference = new DateTimeOffset(
                    resolvedReference.Date.AddDays(item.DayOffset).AddHours(12),
                    resolvedReference.Offset);
                var icon = ResolveWeatherAnimationIcon(item.Snapshot, iconReference);
                var unit = item.Snapshot.UseCelsius ? "C" : "F";
                var temperatureBand = ResolveWeatherTemperatureBand(high, item.Snapshot.UseCelsius);
                var spokenLine = $"{dayName}: {summary}, high {high}, low {low}.";
                return new WeatherForecastCardSegment(
                    dayName,
                    summary,
                    high,
                    low,
                    icon,
                    unit,
                    temperatureBand,
                    spokenLine);
            })
            .ToArray();
    }

    private static IDictionary<string, object?> BuildWeeklyWeatherSkillPayload(
        string spokenReply,
        WeatherReportSnapshot snapshot,
        IReadOnlyList<WeatherForecastCardSegment> segments,
        DateTimeOffset? referenceLocalTime)
    {
        var payload = BuildWeatherSkillPayload(spokenReply, snapshot, referenceLocalTime);
        payload["weather_weekly_cards"] = segments
            .Select(static segment => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["weather_day"] = segment.DayName,
                ["weather_summary"] = segment.Summary,
                ["weather_icon"] = segment.Icon,
                ["weather_high"] = segment.High,
                ["weather_low"] = segment.Low,
                ["weather_unit"] = segment.Unit,
                ["weather_theme"] = segment.Theme,
                ["weather_spoken_line"] = segment.SpokenLine
            })
            .ToArray();
        return payload;
    }

    private static void AddWeatherRequestDiagnostics(
        IDictionary<string, object?> payload,
        string transcript,
        string normalizedTranscript,
        string? locationQuery,
        WeatherDateEntity weatherDate,
        bool isRangeForecastRequest,
        bool isThisWeekForecast,
        bool isNextWeekForecast)
    {
        payload["weather_request_transcript"] = transcript;
        payload["weather_request_normalized"] = normalizedTranscript;
        payload["weather_request_location_query"] = locationQuery;
        payload["weather_request_date_entity"] = weatherDate.DateEntity;
        payload["weather_request_forecast_day_offset"] = weatherDate.ForecastDayOffset;
        payload["weather_request_range"] = isRangeForecastRequest;
        payload["weather_request_this_week"] = isThisWeekForecast;
        payload["weather_request_next_week"] = isNextWeekForecast;
    }

    private static bool IsNextWeekForecastRequest(string normalizedTranscript, bool isRangeForecastRequest)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript) || !isRangeForecastRequest)
        {
            return false;
        }

        if (normalizedTranscript.Contains("next week", StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalizedTranscript.Contains("next", StringComparison.Ordinal))
        {
            return false;
        }

        return normalizedTranscript.Contains("forecast next", StringComparison.Ordinal) ||
               normalizedTranscript.Contains("forecast for next", StringComparison.Ordinal);
    }

    private static bool IsRangeForecastRequest(string normalizedTranscript)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        if (normalizedTranscript.Contains("next week", StringComparison.Ordinal) ||
            normalizedTranscript.Contains("this week", StringComparison.Ordinal) ||
            normalizedTranscript.Contains("weekend", StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedTranscript.Contains("forecast next", StringComparison.Ordinal) ||
               normalizedTranscript.Contains("forecast for next", StringComparison.Ordinal);
    }

    private static bool IsThisWeekForecastRequest(string normalizedTranscript, bool isRangeForecastRequest)
    {
        return isRangeForecastRequest &&
               !string.IsNullOrWhiteSpace(normalizedTranscript) &&
               normalizedTranscript.Contains("this week", StringComparison.Ordinal) &&
               !normalizedTranscript.Contains("weekend", StringComparison.Ordinal);
    }

    private static bool IsOpenEndedForecastRequest(
        string normalizedTranscript,
        WeatherDateEntity weatherDate,
        bool isRangeForecastRequest,
        string? locationQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript) ||
            !string.IsNullOrWhiteSpace(locationQuery) ||
            isRangeForecastRequest ||
            weatherDate.ForecastDayOffset > 0 ||
            !normalizedTranscript.Contains("forecast", StringComparison.Ordinal))
        {
            return false;
        }

        return !MatchesAny(
            normalizedTranscript,
            "today",
            "today s",
            "today's",
            "tonight",
            "right now",
            "current weather",
            "currently");
    }

    private static int ResolveThisWeekForecastEndOffset(DateTimeOffset? referenceLocalTime)
    {
        var resolvedReference = referenceLocalTime ?? DateTimeOffset.UtcNow;
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)resolvedReference.DayOfWeek + 7) % 7;
        var endOffset = Math.Min(MaxWeatherForecastDayOffset, daysUntilSunday);
        return Math.Max(1, endOffset);
    }

    private static bool ShouldDefaultForecastToTomorrow(
        string normalizedTranscript,
        WeatherDateEntity weatherDate,
        bool isRangeForecastRequest,
        bool isOpenEndedForecastRequest)
    {
        if (weatherDate.ForecastDayOffset > 0 ||
            isOpenEndedForecastRequest ||
            isRangeForecastRequest ||
            string.IsNullOrWhiteSpace(normalizedTranscript) ||
            !normalizedTranscript.Contains("forecast", StringComparison.Ordinal))
        {
            return false;
        }

        return !MatchesAny(
            normalizedTranscript,
            "today",
            "today s",
            "today's",
            "tonight",
            "right now",
            "current weather",
            "currently");
    }

    private static IDictionary<string, object?> BuildWeatherSkillPayload(
        string spokenReply,
        WeatherReportSnapshot snapshot,
        DateTimeOffset? referenceLocalTime)
    {
        var weatherIcon = ResolveWeatherAnimationIcon(snapshot, referenceLocalTime);
        var promptToken = ResolveWeatherPromptToken(weatherIcon);
        var highTemperature = snapshot.HighTemperature ?? snapshot.Temperature;
        var lowTemperature = snapshot.LowTemperature ?? snapshot.Temperature;
        var temperatureUnit = snapshot.UseCelsius ? "C" : "F";
        var temperatureBand = ResolveWeatherTemperatureBand(highTemperature, snapshot.UseCelsius);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["skillId"] = "report-skill",
            ["cloudSkill"] = "weather",
            ["esml"] =
                $"<speak><anim cat='weather' meta='{weatherIcon}' nonBlocking='true' /><break size='0.35'/><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeForEsml(spokenReply)}</es></speak>",
            ["mim_id"] = $"WeatherComment{promptToken}",
            ["mim_type"] = "announcement",
            ["prompt_id"] = $"WeatherComment{promptToken}_AN_13",
            ["prompt_sub_category"] = "AN",
            ["weather_view_enabled"] = true,
            ["weather_view_kind"] = "weatherHiLo",
            ["weather_icon"] = weatherIcon,
            ["weather_summary"] = snapshot.Summary,
            ["weather_location"] = snapshot.LocationName,
            ["weather_high"] = highTemperature,
            ["weather_low"] = lowTemperature,
            ["weather_unit"] = temperatureUnit,
            ["weather_theme"] = temperatureBand
        };
    }

    private static string ResolveWeatherAnimationIcon(
        WeatherReportSnapshot snapshot,
        DateTimeOffset? referenceLocalTime)
    {
        var isDaytime = (referenceLocalTime ?? DateTimeOffset.UtcNow).Hour is >= 6 and < 18;
        var normalized = NormalizeCommandPhrase(
            $"{snapshot.Condition ?? string.Empty} {snapshot.Summary ?? string.Empty}");

        if (normalized.Contains("thunder", StringComparison.Ordinal) ||
            normalized.Contains("drizzle", StringComparison.Ordinal) ||
            normalized.Contains("rain", StringComparison.Ordinal))
        {
            return "rain";
        }

        if (normalized.Contains("snow", StringComparison.Ordinal))
        {
            return "snow";
        }

        if (normalized.Contains("sleet", StringComparison.Ordinal) ||
            normalized.Contains("freezing rain", StringComparison.Ordinal) ||
            normalized.Contains("ice", StringComparison.Ordinal))
        {
            return "sleet";
        }

        if (normalized.Contains("fog", StringComparison.Ordinal) ||
            normalized.Contains("mist", StringComparison.Ordinal) ||
            normalized.Contains("haze", StringComparison.Ordinal) ||
            normalized.Contains("smoke", StringComparison.Ordinal))
        {
            return "fog";
        }

        if (normalized.Contains("wind", StringComparison.Ordinal))
        {
            return "wind";
        }

        if (normalized.Contains("partly cloudy", StringComparison.Ordinal) ||
            normalized.Contains("scattered clouds", StringComparison.Ordinal) ||
            normalized.Contains("few clouds", StringComparison.Ordinal))
        {
            return isDaytime ? "partly-cloudy-day" : "partly-cloudy-night";
        }

        if (normalized.Contains("cloud", StringComparison.Ordinal) ||
            normalized.Contains("overcast", StringComparison.Ordinal))
        {
            return "cloudy";
        }

        if (normalized.Contains("clear", StringComparison.Ordinal) ||
            normalized.Contains("sunny", StringComparison.Ordinal))
        {
            return isDaytime ? "clear-day" : "clear-night";
        }

        return isDaytime ? "clear-day" : "clear-night";
    }

    private static string ResolveWeatherPromptToken(string weatherIcon)
    {
        return weatherIcon switch
        {
            "clear-day" => "ClearDay",
            "clear-night" => "ClearNight",
            "rain" => "Rain",
            "snow" => "Snow",
            "sleet" => "Sleet",
            "fog" => "Fog",
            "wind" => "Wind",
            "cloudy" => "Cloudy",
            "partly-cloudy-day" => "PartlyCloudyDay",
            "partly-cloudy-night" => "PartlyCloudyNight",
            _ => "Cloudy"
        };
    }

    private static string ResolveWeatherTemperatureBand(int highTemperature, bool useCelsius)
    {
        var hotThreshold = useCelsius ? 29 : 85;
        var coldThreshold = useCelsius ? 4 : 40;
        if (highTemperature > hotThreshold)
        {
            return "Hot";
        }

        if (highTemperature < coldThreshold)
        {
            return "Cold";
        }

        return "Normal";
    }

    private static string EscapeForEsml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static JiboInteractionDecision BuildOrderPizzaDecision()
    {
        return new JiboInteractionDecision(
            "order_pizza",
            "I can't do that yet, but I bet I'll be able to do that sometime in the near future.",
            "chitchat-skill",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["esml"] = "<speak>I can't do that yet, but I bet I'll be able to do that sometime in the near future.</speak>",
                ["mim_id"] = "RA_JBO_OrderPizza",
                ["mim_type"] = "announcement",
                ["prompt_id"] = "RA_JBO_OrderPizza_AN_01",
                ["prompt_sub_category"] = "AN"
            });
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

    private JiboInteractionDecision BuildRandomDanceDecision(JiboExperienceCatalog catalog)
    {
        var dance = randomizer.Choose(catalog.DanceAnimations);
        var replyText = randomizer.Choose(catalog.DanceReplies);
        return BuildDanceDecision("dance", dance, replyText);
    }

    private JiboInteractionDecision BuildDanceQuestionDecision(JiboExperienceCatalog catalog)
    {
        return new JiboInteractionDecision("dance_question", randomizer.Choose(catalog.DanceQuestionReplies));
    }

    private static JiboInteractionDecision BuildDanceDecision(string intentName, string dance, string replyText)
    {
        return new JiboInteractionDecision(
            intentName,
            replyText,
            "chitchat-skill",
            new Dictionary<string, object?>
            {
                ["esml"] = $"<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, {dance}' /></speak>",
                ["mim_id"] = "runtime-chat",
                ["mim_type"] = "announcement"
            });
    }

    private async Task<JiboInteractionDecision> BuildNewsDecisionAsync(
        TurnContext turn,
        string transcript,
        JiboExperienceCatalog catalog,
        CancellationToken cancellationToken)
    {
        var preferredCategories = ResolvePreferredNewsCategories(turn, transcript);
        var requestedHeadlineCount = MaxNewsHeadlines;
        if (newsBriefingProvider is not null)
        {
            try
            {
                var snapshot = await newsBriefingProvider.GetBriefingAsync(
                    new NewsBriefingRequest(preferredCategories, requestedHeadlineCount),
                    cancellationToken);

                if (snapshot?.Headlines.Count > 0)
                {
                    return BuildProviderNewsDecision(snapshot, preferredCategories, requestedHeadlineCount);
                }

                var providerStatus = ResolveNewsProviderStatus(snapshot);
                var providerMessage = snapshot?.ProviderMessage;
                var providerEndpoint = snapshot?.ProviderEndpoint;
                var providerHttpStatusCode = snapshot?.ProviderHttpStatusCode;
                var providerErrorCode = snapshot?.ProviderErrorCode;

                var fallbackBriefingWhenEmpty = randomizer.Choose(catalog.NewsBriefings);
                return BuildNewsDecision(
                    fallbackBriefingWhenEmpty,
                    sourceName: null,
                    preferredCategories.Count > 0 ? preferredCategories : null,
                    headlineCount: null,
                    providerDiagnostics: BuildNewsProviderDiagnostics(
                        providerStatus,
                        preferredCategories,
                        requestedHeadlineCount,
                        snapshot?.Headlines.Count ?? 0,
                        providerMessage,
                        providerHttpStatusCode,
                        providerEndpoint,
                        providerErrorCode));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Provider failures should never block baseline news behavior.
                var fallbackBriefingOnError = randomizer.Choose(catalog.NewsBriefings);
                return BuildNewsDecision(
                    fallbackBriefingOnError,
                    sourceName: null,
                    preferredCategories.Count > 0 ? preferredCategories : null,
                    headlineCount: null,
                    providerDiagnostics: BuildNewsProviderDiagnostics(
                        "provider_exception",
                        preferredCategories,
                        requestedHeadlineCount));
            }
        }

        var fallbackBriefing = randomizer.Choose(catalog.NewsBriefings);
        return BuildNewsDecision(
            fallbackBriefing,
            sourceName: null,
            preferredCategories.Count > 0 ? preferredCategories : null,
            headlineCount: null,
            providerDiagnostics: BuildNewsProviderDiagnostics(
                "provider_unavailable",
                preferredCategories,
                requestedHeadlineCount));
    }

    private static JiboInteractionDecision BuildNewsDecision(
        string spokenBriefing,
        string? sourceName,
        IReadOnlyList<string>? categories,
        int? headlineCount,
        IReadOnlyDictionary<string, object?>? providerDiagnostics = null)
    {
        var speakableBriefing = NormalizeNewsSpeechText(spokenBriefing);
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["skillId"] = "news",
            ["cloudSkill"] = "news",
            ["mim_id"] = "runtime-news",
            ["mim_type"] = "announcement",
            ["prompt_id"] = "NewsHeadline_AN_01",
            ["prompt_sub_category"] = "AN",
            ["esml"] =
                $"<speak><anim cat='news' meta='news-stinger' nonBlocking='true' /><break size='0.35'/><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeForEsml(speakableBriefing)}</es></speak>"
        };

        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            payload["news_source"] = sourceName;
        }

        if (headlineCount is > 0)
        {
            payload["news_headline_count"] = headlineCount.Value;
        }

        if (categories is { Count: > 0 })
        {
            payload["news_categories"] = categories.ToArray();
        }

        if (providerDiagnostics is not null)
        {
            foreach (var (key, value) in providerDiagnostics)
            {
                payload[key] = value;
            }
        }

        return new JiboInteractionDecision("news", spokenBriefing, "news", payload);
    }

    private static JiboInteractionDecision BuildProviderNewsDecision(
        NewsBriefingSnapshot snapshot,
        IReadOnlyList<string> preferredCategories,
        int requestedHeadlineCount)
    {
        var headlines = snapshot.Headlines
            .Where(headline => !string.IsNullOrWhiteSpace(headline.Title))
            .Take(MaxNewsHeadlines)
            .ToArray();
        if (headlines.Length == 0)
        {
            return BuildNewsDecision(
                "I couldn't load fresh headlines right now.",
                snapshot.SourceName,
                preferredCategories,
                headlineCount: 0,
                providerDiagnostics: BuildNewsProviderDiagnostics(
                    "provider_empty",
                    preferredCategories,
                    requestedHeadlineCount,
                    0));
        }

        var leadIn = BuildNewsLeadIn(snapshot.SourceName, preferredCategories);
        var joinedHeadlines = string.Join(" ", headlines.Select(static headline => $"{headline.Title}."));
        var spokenBriefing = $"{leadIn} {joinedHeadlines}".Trim();
        return BuildNewsDecision(
            spokenBriefing,
            snapshot.SourceName,
            preferredCategories,
            headlines.Length,
            providerDiagnostics: BuildNewsProviderDiagnostics(
                "provider_success",
                preferredCategories,
                requestedHeadlineCount,
                headlines.Length));
    }

    private static IReadOnlyDictionary<string, object?> BuildNewsProviderDiagnostics(
        string status,
        IReadOnlyList<string> preferredCategories,
        int requestedHeadlineCount,
        int? resolvedHeadlineCount = null,
        string? providerMessage = null,
        int? providerHttpStatusCode = null,
        string? providerEndpoint = null,
        string? providerErrorCode = null)
    {
        var diagnostics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["news_provider_status"] = status,
            ["news_provider_requested_headlines"] = requestedHeadlineCount,
            ["news_provider_preferred_categories"] = preferredCategories.Count > 0
                ? preferredCategories.ToArray()
                : Array.Empty<string>()
        };

        if (resolvedHeadlineCount is not null)
        {
            diagnostics["news_provider_resolved_headlines"] = resolvedHeadlineCount.Value;
        }

        if (!string.IsNullOrWhiteSpace(providerMessage))
        {
            diagnostics["news_provider_message"] = providerMessage;
        }

        if (providerHttpStatusCode is not null)
        {
            diagnostics["news_provider_http_status"] = providerHttpStatusCode.Value;
        }

        if (!string.IsNullOrWhiteSpace(providerEndpoint))
        {
            diagnostics["news_provider_endpoint"] = providerEndpoint;
        }

        if (!string.IsNullOrWhiteSpace(providerErrorCode))
        {
            diagnostics["news_provider_error_code"] = providerErrorCode;
        }

        return diagnostics;
    }

    private static string ResolveNewsProviderStatus(NewsBriefingSnapshot? snapshot)
    {
        var providerStatus = snapshot?.ProviderStatus?.Trim().ToLowerInvariant();
        return providerStatus switch
        {
            "success" => "provider_success",
            "exception" => "provider_exception",
            "http_error" or "api_error" or "schema_error" => "provider_error",
            _ => "provider_empty"
        };
    }

    private static string BuildNewsLeadIn(string? sourceName, IReadOnlyList<string> preferredCategories)
    {
        var categoryLeadIn = preferredCategories.Count switch
        {
            <= 0 => "Here are a few headlines.",
            1 => $"Here are your {preferredCategories[0]} headlines.",
            _ => $"Here are your {preferredCategories[0]} and {preferredCategories[1]} headlines."
        };

        return string.IsNullOrWhiteSpace(sourceName)
            ? categoryLeadIn
            : $"{categoryLeadIn} Source: {sourceName}.";
    }

    private static string NormalizeNewsSpeechText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // Expand "AI" so Nimbus TTS does not collapse it to a single "aye" sound.
        var normalized = Regex.Replace(
            text,
            @"\bA\.?\s*I\.?\b",
            "artificial intelligence",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return NormalizeLocationForSpeech(normalized);
    }

    private static string NormalizeLocationForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return Regex.Replace(
            text,
            @"\b(?<token>[A-Z]{2,3})\b",
            static match =>
            {
                var token = match.Groups["token"].Value;
                if (!SpokenAbbreviationTokens.Contains(token))
                {
                    return token;
                }

                return string.Join(".", token.ToCharArray()) + ".";
            },
            RegexOptions.CultureInvariant);
    }

    private List<string> ResolvePreferredNewsCategories(TurnContext turn, string transcript)
    {
        var categories = new List<string>();
        var normalizedTranscript = NormalizeCommandPhrase(transcript);

        foreach (var (keyword, category) in NewsCategoryKeywordMap)
        {
            if (normalizedTranscript.Contains(keyword, StringComparison.Ordinal))
            {
                AddNewsCategory(categories, category);
            }
        }

        var tenantScope = ResolveTenantScope(turn);
        var explicitPreference = personalMemoryStore.GetPreference(tenantScope, "news");
        if (!string.IsNullOrWhiteSpace(explicitPreference))
        {
            foreach (var category in MapNewsCategoryText(explicitPreference))
            {
                AddNewsCategory(categories, category);
            }
        }

        foreach (var (item, affinity) in personalMemoryStore.GetAffinities(tenantScope))
        {
            if (affinity == PersonalAffinity.Dislike)
            {
                continue;
            }

            foreach (var category in MapNewsCategoryText(item))
            {
                AddNewsCategory(categories, category);
            }
        }

        return categories.Take(MaxPreferredNewsCategories).ToList();
    }

    private static IEnumerable<string> MapNewsCategoryText(string text)
    {
        var normalized = NormalizeCommandPhrase(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        foreach (var (keyword, category) in NewsCategoryKeywordMap)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
            {
                yield return category;
            }
        }
    }

    private static void AddNewsCategory(ICollection<string> categories, string category)
    {
        if (categories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        categories.Add(category);
    }

    private JiboInteractionDecision BuildSurpriseDecision(
        JiboExperienceCatalog catalog,
        TurnContext turn,
        DateTimeOffset? referenceLocalTime)
    {
        var tenantScope = ResolveTenantScope(turn);
        var candidates = BuildProactivityCandidates(tenantScope, referenceLocalTime);
        if (candidates.Count == 0)
        {
            return new JiboInteractionDecision("surprise", randomizer.Choose(catalog.SurpriseReplies));
        }

        var highestWeight = candidates.Max(static candidate => candidate.Weight);
        var topCandidates = candidates
            .Where(candidate => candidate.Weight == highestWeight)
            .ToArray();
        var selected = topCandidates.Length == 1
            ? topCandidates[0]
            : randomizer.Choose(topCandidates);

        return selected.IntentName switch
        {
            "proactive_pizza_day" => BuildProactivePizzaDayDecision(referenceLocalTime),
            "proactive_pizza_preference" => BuildProactivePizzaPreferenceDecision(),
            "proactive_offer_pizza_fact" => BuildProactivePizzaFactOfferDecision(),
            _ => new JiboInteractionDecision("surprise", randomizer.Choose(catalog.SurpriseReplies))
        };
    }

    private List<ProactivityCandidate> BuildProactivityCandidates(
        PersonalMemoryTenantScope tenantScope,
        DateTimeOffset? referenceLocalTime)
    {
        var candidates = new List<ProactivityCandidate>();
        var referenceDate = (referenceLocalTime ?? DateTimeOffset.UtcNow).Date;

        var pizzaSignal = ResolvePizzaSignal(tenantScope);
        if (pizzaSignal.Affinity == PersonalAffinity.Dislike)
        {
            return candidates;
        }

        if (referenceDate.Month == 2 && referenceDate.Day == 9)
        {
            var holidayWeight = pizzaSignal.Affinity switch
            {
                PersonalAffinity.Love => 170,
                PersonalAffinity.Like => 160,
                _ => 150
            };
            candidates.Add(new ProactivityCandidate("proactive_pizza_day", holidayWeight));
        }

        if (pizzaSignal.Affinity is PersonalAffinity.Love or PersonalAffinity.Like)
        {
            var preferenceWeight = pizzaSignal.Affinity == PersonalAffinity.Love ? 140 : 120;
            candidates.Add(new ProactivityCandidate("proactive_pizza_preference", preferenceWeight));
            candidates.Add(new ProactivityCandidate("proactive_offer_pizza_fact", preferenceWeight - 5));
            return candidates;
        }

        candidates.Add(new ProactivityCandidate("proactive_offer_pizza_fact", 90));
        return candidates;
    }

    private PizzaSignal ResolvePizzaSignal(PersonalMemoryTenantScope tenantScope)
    {
        var pizzaAffinity = personalMemoryStore.GetAffinity(tenantScope, "pizza");
        if (pizzaAffinity is not null)
        {
            return new PizzaSignal(pizzaAffinity);
        }

        var affinityMatch = personalMemoryStore.GetAffinities(tenantScope)
            .Where(pair => pair.Key.Contains("pizza", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static pair => pair.Value == PersonalAffinity.Love ? 2 : pair.Value == PersonalAffinity.Like ? 1 : 0)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(affinityMatch.Key))
        {
            return new PizzaSignal(affinityMatch.Value);
        }

        foreach (var category in PizzaPreferenceCategories)
        {
            var preference = personalMemoryStore.GetPreference(tenantScope, category);
            if (!string.IsNullOrWhiteSpace(preference) &&
                preference.Contains("pizza", StringComparison.OrdinalIgnoreCase))
            {
                return new PizzaSignal(PersonalAffinity.Like);
            }
        }

        return new PizzaSignal(null);
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

    private JiboInteractionDecision BuildScriptedPersonalityDecision(
        JiboExperienceCatalog catalog,
        string intentName,
        params string[] preferredSnippets)
    {
        return new JiboInteractionDecision(
            intentName,
            SelectLegacyPersonalityReply(catalog, preferredSnippets),
            ContextUpdates: BuildScriptedResponseContextUpdates());
    }

    private JiboInteractionDecision BuildScriptedGreetingDecision(
        JiboExperienceCatalog catalog,
        string intentName,
        params string[] preferredSnippets)
    {
        return new JiboInteractionDecision(
            intentName,
            SelectLegacyGreetingReply(catalog, preferredSnippets),
            ContextUpdates: BuildScriptedResponseContextUpdates());
    }

    private static IDictionary<string, object?> BuildScriptedResponseContextUpdates()
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [ChitchatStateMachine.StateMetadataKey] = "complete",
            [ChitchatStateMachine.RouteMetadataKey] = "ScriptedResponse",
            [ChitchatStateMachine.EmotionMetadataKey] = string.Empty
        };
    }

    private string SelectLegacyPersonalityReply(JiboExperienceCatalog catalog, params string[] preferredSnippets)
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

    private string SelectLegacyGreetingReply(JiboExperienceCatalog catalog, params string[] preferredSnippets)
    {
        foreach (var snippet in preferredSnippets)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            var match = catalog.GreetingReplies.FirstOrDefault(reply =>
                reply.Contains(snippet, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return randomizer.Choose(catalog.GreetingReplies);
    }

    private static string ResolveSemanticIntent(
        string loweredTranscript,
        DateTimeOffset? referenceLocalTime,
        string? clientIntent,
        IReadOnlyList<string> clientRules,
        IReadOnlyList<string> listenRules,
        IReadOnlyDictionary<string, string> clientEntities,
        string? lastClockDomain,
        string? pendingProactivityOffer,
        bool isYesNoTurn,
        bool isTimerValueTurn,
        bool isAlarmValueTurn)
    {
        var wordOfDayPuzzleTurn = clientRules.Concat(listenRules)
            .Any(rule => string.Equals(rule, "word-of-the-day/puzzle", StringComparison.OrdinalIgnoreCase));

        if (string.Equals(clientIntent, "guess", StringComparison.OrdinalIgnoreCase) &&
            wordOfDayPuzzleTurn)
        {
            return "word_of_the_day_guess";
        }

        if (string.Equals(clientIntent, "loadMenu", StringComparison.OrdinalIgnoreCase) &&
            clientEntities.TryGetValue("destination", out var destination) &&
            string.Equals(destination, "word-of-the-day", StringComparison.OrdinalIgnoreCase))
        {
            return "word_of_the_day";
        }

        if (string.Equals(clientIntent, "loadMenu", StringComparison.OrdinalIgnoreCase) &&
            clientEntities.TryGetValue("destination", out var photoDestination))
        {
            return photoDestination.ToLowerInvariant() switch
            {
                "snapshot" => "snapshot",
                "photobooth" => "photobooth",
                "gallery" or "photo-gallery" or "photos" => "photo_gallery",
                _ => "chat"
            };
        }

        var yesNoRule = ReadPrimaryYesNoRule(clientRules, listenRules);
        if (!string.IsNullOrWhiteSpace(pendingProactivityOffer) &&
            string.Equals(pendingProactivityOffer, "pizza_fact", StringComparison.OrdinalIgnoreCase))
        {
            if (IsAffirmativeReply(loweredTranscript))
            {
                return "proactive_pizza_fact";
            }

            if (IsNegativeReply(loweredTranscript))
            {
                return "proactive_offer_declined";
            }
        }

        if (isYesNoTurn)
        {
            var yesNoReply = TryClassifyYesNoReply(NormalizeCommandPhrase(loweredTranscript));
            if (yesNoReply == YesNoReply.Affirmative)
            {
                return ResolveAffirmativeYesNoIntent(yesNoRule);
            }

            if (yesNoReply == YesNoReply.Negative)
            {
                return ResolveNegativeYesNoIntent(yesNoRule);
            }
        }

        if (IsNameSetStatement(loweredTranscript))
        {
            return "memory_set_name";
        }

        if (IsNameRecallQuestion(loweredTranscript))
        {
            return "memory_get_name";
        }

        if (IsUserBirthdaySetStatement(loweredTranscript) || IsUserBirthdaySetAttempt(loweredTranscript))
        {
            return "memory_set_birthday";
        }

        if (IsUserBirthdayRecallQuestion(loweredTranscript) || IsUserBirthdayRecallAttempt(loweredTranscript))
        {
            return "memory_get_birthday";
        }

        if (IsRobotBirthdayQuestion(loweredTranscript))
        {
            return "robot_birthday";
        }

        if (string.Equals(clientIntent, "askForTime", StringComparison.OrdinalIgnoreCase))
        {
            return "time";
        }

        if (string.Equals(clientIntent, "askForDate", StringComparison.OrdinalIgnoreCase))
        {
            return "date";
        }

        if (string.Equals(clientIntent, "askForDay", StringComparison.OrdinalIgnoreCase))
        {
            return "day";
        }

        if (string.Equals(clientIntent, "timerValue", StringComparison.OrdinalIgnoreCase))
        {
            return "timer_value";
        }

        if (string.Equals(clientIntent, "alarmValue", StringComparison.OrdinalIgnoreCase))
        {
            return "alarm_value";
        }

        if (string.Equals(clientIntent, "requestMakePizza", StringComparison.OrdinalIgnoreCase))
        {
            return "pizza";
        }

        if (string.Equals(clientIntent, "requestOrderPizza", StringComparison.OrdinalIgnoreCase))
        {
            return "order_pizza";
        }

        if (string.Equals(clientIntent, "requestWeatherPR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clientIntent, "requestWeather", StringComparison.OrdinalIgnoreCase))
        {
            return "weather";
        }

        if (IsCancelRequest(clientIntent, loweredTranscript))
        {
            if (isAlarmValueTurn)
            {
                return "alarm_cancel";
            }

            if (isTimerValueTurn)
            {
                return "timer_cancel";
            }
        }

        if ((string.Equals(clientIntent, "start", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(clientIntent, "set", StringComparison.OrdinalIgnoreCase)) &&
            clientEntities.TryGetValue("domain", out var startDomain))
        {
            return startDomain.ToLowerInvariant() switch
            {
                "timer" => HasStructuredTimerValue(clientEntities) || TryParseTimerValue(loweredTranscript, isTimerValueTurn) is not null
                    ? "timer_value"
                    : "timer_clarify",
                "alarm" => HasStructuredAlarmValue(clientEntities) || TryParseAlarmValue(loweredTranscript, isAlarmValueTurn, referenceLocalTime) is not null
                    ? "alarm_value"
                    : "alarm_clarify",
                _ => "chat"
            };
        }

        if ((string.Equals(clientIntent, "cancel", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(clientIntent, "delete", StringComparison.OrdinalIgnoreCase)) &&
            clientRules.Concat(listenRules).Any(rule => string.Equals(rule, "clock/alarm_timer_query_menu", StringComparison.OrdinalIgnoreCase)))
        {
            var cancelDomain = ResolveClockDomain(clientEntities, clientRules, listenRules, lastClockDomain);
            return string.Equals(cancelDomain, "timer", StringComparison.OrdinalIgnoreCase)
                ? "timer_delete"
                : "alarm_delete";
        }

        if (string.Equals(clientIntent, "menu", StringComparison.OrdinalIgnoreCase) &&
            clientEntities.TryGetValue("domain", out var clockDomain))
        {
            return clockDomain.ToLowerInvariant() switch
            {
                "clock" => "clock_menu",
                "timer" => "timer_menu",
                "alarm" => "alarm_menu",
                _ => "chat"
            };
        }

        if (MatchesAny(
                loweredTranscript,
                "word of the day",
                "start word of the day",
                "play word of the day",
                "do word of the day",
                "open word of the day"))
        {
            return "word_of_the_day";
        }

        if (wordOfDayPuzzleTurn && !string.IsNullOrWhiteSpace(loweredTranscript))
        {
            return "word_of_the_day_guess";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you funny",
                "do you think you are funny",
                "are you a funny robot"))
        {
            return "robot_is_funny";
        }

        if (MatchesAny(loweredTranscript, "joke", "funny", "make me laugh"))
        {
            return "joke";
        }

        if (MatchesAny(
                loweredTranscript,
                "cloud version",
                "open jibo cloud version",
                "openjibo cloud version",
                "what version is the cloud",
                "what s the cloud version",
                "what's the cloud version"))
        {
            return "cloud_version";
        }

        if (IsPreferenceSetStatement(loweredTranscript) || IsPreferenceSetAttempt(loweredTranscript))
        {
            return "memory_set_preference";
        }

        if (IsPreferenceRecallQuestion(loweredTranscript) || IsPreferenceRecallAttempt(loweredTranscript))
        {
            return "memory_get_preference";
        }

        if (IsImportantDateSetStatement(loweredTranscript))
        {
            return "memory_set_important_date";
        }

        if (IsImportantDateRecallQuestion(loweredTranscript))
        {
            return "memory_get_important_date";
        }

        if (IsAffinitySetStatement(loweredTranscript) || IsAffinitySetAttempt(loweredTranscript))
        {
            return "memory_set_affinity";
        }

        if (IsAffinityRecallQuestion(loweredTranscript) || IsAffinityRecallAttempt(loweredTranscript))
        {
            return "memory_get_affinity";
        }

        if (TryResolveRadioGenre(loweredTranscript) is not null)
        {
            return "radio_genre";
        }

        if (TryResolveVolumeLevel(loweredTranscript) is not null ||
            clientEntities.ContainsKey("volumeLevel"))
        {
            return "volume_to_value";
        }

        if (IsVolumeQueryRequest(loweredTranscript))
        {
            return "volume_query";
        }

        if (IsVolumeUpRequest(loweredTranscript))
        {
            return "volume_up";
        }

        if (IsVolumeDownRequest(loweredTranscript))
        {
            return "volume_down";
        }

        if (MatchesAny(loweredTranscript, "open the clock", "open clock", "show the clock", "show clock"))
        {
            return "clock_open";
        }

        if (MatchesAny(loweredTranscript, "open the timer", "open timer", "show the timer", "show timer"))
        {
            return "timer_menu";
        }

        if (MatchesAny(loweredTranscript, "open the alarm", "open alarm", "show the alarm", "show alarm"))
        {
            return "alarm_menu";
        }

        if (IsAlarmDeleteRequest(loweredTranscript))
        {
            return "alarm_delete";
        }

        if (MatchesAny(
                loweredTranscript,
                "cancel timer",
                "delete timer",
                "remove timer",
                "stop timer",
                "turn off timer"))
        {
            return "timer_delete";
        }

        if (IsGlobalStopRequest(loweredTranscript, clientIntent, clientEntities))
        {
            return "stop";
        }

        if (TryParseAlarmValue(loweredTranscript, isAlarmValueTurn, referenceLocalTime) is not null)
        {
            return "alarm_value";
        }

        if (TryParseTimerValue(loweredTranscript, isTimerValueTurn) is not null)
        {
            return "timer_value";
        }

        if (IsAlarmRequest(loweredTranscript) || isAlarmValueTurn)
        {
            return "alarm_clarify";
        }

        if (IsTimerRequest(loweredTranscript) || isTimerValueTurn)
        {
            return "timer_clarify";
        }

        if (MatchesAny(loweredTranscript, "open the radio", "play the radio", "turn on the radio", "radio"))
        {
            return "radio";
        }

        if (MatchesAny(
                loweredTranscript,
                "snap a picture",
                "take a picture",
                "take a photo",
                "snap a photo"))
        {
            return "snapshot";
        }

        if (MatchesAny(
                loweredTranscript,
                "photo booth",
                "photobooth",
                "open photobooth",
                "start photobooth"))
        {
            return "photobooth";
        }

        if (MatchesAny(
                loweredTranscript,
                "photo gallery",
                "photogal",
                "photo gal",
                "open the gallery",
                "open photo gallery",
                "show my photos",
                "open my photos",
                "gallery"))
        {
            return "photo_gallery";
        }

        if (IsDanceQuestion(loweredTranscript))
        {
            return "dance_question";
        }

        if (MatchesAny(loweredTranscript, "can you dance", "do you dance", "are you able to dance"))
        {
            return "robot_can_dance";
        }

        if (MatchesAny(loweredTranscript, "twerk"))
        {
            return "twerk";
        }

        if (MatchesAny(loweredTranscript, "dance", "boogie"))
        {
            return "dance";
        }

        if (MatchesAny(loweredTranscript, "surprise", "surprise me", "show me something fun"))
        {
            return "surprise";
        }

        if (MatchesAny(
                loweredTranscript,
                "how old are you",
                "what is your age",
                "what s your age",
                "how old r you"))
        {
            return "robot_age";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you have a personality",
                "what is your personality",
                "what's your personality",
                "what s your personality",
                "describe your personality"))
        {
            return "robot_personality";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you pay taxes",
                "do you pay tax",
                "are you tax exempt"))
        {
            return "robot_taxes";
        }

        if (MatchesAny(
                loweredTranscript,
                "what do you want",
                "what is it you want",
                "what do you really want"))
        {
            return "robot_desire";
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your job",
                "what's your job",
                "what do you do",
                "what is your work",
                "what's your work"))
        {
            return "robot_job";
        }

        if (MatchesAny(
                loweredTranscript,
                "how do you work",
                "how does jibo work",
                "what does jibo do",
                "how are you built",
                "how are you put together"))
        {
            return "robot_how_do_you_work";
        }

        if (MatchesAny(
                loweredTranscript,
                "what do you eat",
                "do you eat",
                "what do you drink",
                "do you drink"))
        {
            return "robot_what_do_you_eat";
        }

        if (MatchesAny(
                loweredTranscript,
                "where do you live",
                "where s your home",
                "where is your home",
                "what is your home"))
        {
            return "robot_where_do_you_live";
        }

        if (MatchesAny(
                loweredTranscript,
                "where were you born",
                "where were you made",
                "where were you put together"))
        {
            return "robot_where_were_you_born";
        }

        if (MatchesAny(
                loweredTranscript,
                "what languages do you speak",
                "what language do you speak",
                "what languages can you speak",
                "what language can you speak"))
        {
            return "robot_what_languages_do_you_speak";
        }

        if (MatchesAny(
                loweredTranscript,
                "what do you like to do",
                "what do you like doing",
                "what is your favorite thing to do",
                "what's your favorite thing to do",
                "what is your favourite thing to do",
                "what's your favourite thing to do"))
        {
            return "robot_what_do_you_like_to_do";
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your favorite flower",
                "what's your favorite flower",
                "what s your favorite flower",
                "what is your favourite flower",
                "what's your favourite flower",
                "what s your favourite flower"))
        {
            return "robot_favorite_flower";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like r2d2",
                "do you know r2d2",
                "what do you think about r2d2",
                "are you a fan of r2d2"))
        {
            return "robot_likes_r2d2";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like the sun",
                "do you like sun",
                "what do you think about the sun"))
        {
            return "robot_likes_sun";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like space",
                "do you love space",
                "do you like astronomy",
                "what do you think about space"))
        {
            return "robot_likes_space";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like kids",
                "do you like children",
                "what do you think about kids"))
        {
            return "robot_likes_kids";
        }

        if (MatchesAny(
                loweredTranscript,
                "can you laugh",
                "do you laugh",
                "are you able to laugh"))
        {
            return "robot_can_laugh";
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you made of",
                "what are you built from",
                "what are you constructed from"))
        {
            return "robot_what_are_you_made_of";
        }

        if (MatchesAny(
                loweredTranscript,
                "who made you",
                "who created you",
                "who built you",
                "who developed you"))
        {
            return "robot_origin_created";
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you up to",
                "what are you doing",
                "what have you been up to",
                "what are you into"))
        {
            return "robot_what_do_you_like_to_do";
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you thinking",
                "what are you thinking about",
                "what s on your mind"))
        {
            return "robot_what_are_you_thinking";
        }

        if (MatchesAny(
                loweredTranscript,
                "what have you been doing",
                "what were you doing"))
        {
            return "robot_what_have_you_been_doing";
        }

        if (MatchesAny(
                loweredTranscript,
                "what did you do",
                "what have you done"))
        {
            return "robot_what_did_you_do";
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you",
                "what is jibo",
                "who are you",
                "what kind of robot are you"))
        {
            return "robot_identity";
        }

        if (MatchesAny(
                loweredTranscript,
                "where are you from",
                "where did you come from",
                "where were you made"))
        {
            return "robot_origin_from";
        }

        if (MatchesAny(
                loweredTranscript,
                "what's your name",
                "what is your name"))
        {
            return "robot_name";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you have a nickname",
                "what is your nickname",
                "what's your nickname"))
        {
            return "robot_nickname";
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like being jibo",
                "do you like being yourself",
                "are you happy being jibo"))
        {
            return "robot_likes_being_jibo";
        }

        if (MatchesAny(
                loweredTranscript,
                "happy holidays",
                "merry christmas",
                "happy new year",
                "season s greetings",
                "seasons greetings"))
        {
            return "seasonal_holiday_greeting";
        }

        if (MatchesAny(
                loweredTranscript,
                "what holidays do you celebrate",
                "what holidays are you celebrating",
                "what holidays do you observe"))
        {
            return "seasonal_holidays";
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your new years resolution",
                "what is your new year's resolution",
                "what is your new year s resolution",
                "what are your new years resolutions",
                "what are your new year's resolutions",
                "what are your new year s resolutions",
                "do you have any new years resolutions"))
        {
            return "seasonal_new_years_resolution";
        }

        if (MatchesAny(
                loweredTranscript,
                "how are your new years resolutions going",
                "how are your new year's resolutions going",
                "how is your new years resolution going",
                "how is your new year's resolution going",
                "how are your resolutions going",
                "how is your resolution going"))
        {
            return "seasonal_new_years_update";
        }

        if (MatchesAny(
                loweredTranscript,
                "what halloween costume",
                "what are you going as for halloween",
                "what costume are you wearing",
                "what are you dressing as for halloween"))
        {
            return "seasonal_halloween_costume";
        }

        if (MatchesAny(
                loweredTranscript,
                "what should i do for first day of spring",
                "what should i do for spring",
                "what do i do for first day of spring"))
        {
            return "seasonal_first_day_spring";
        }

        if (MatchesAny(
                loweredTranscript,
                "what should i get for holiday",
                "what should i get for christmas",
                "what gift should i get for christmas",
                "what should i get someone for the holidays"))
        {
            return "seasonal_holiday_gift";
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your favorite color",
                "what's your favorite color",
                "what s your favorite color",
                "what is your favourite color",
                "what's your favourite color",
                "what s your favourite color",
                "what color do you like",
                "what colour do you like"))
        {
            return "robot_favorite_color";
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your favorite food",
                "what's your favorite food",
                "what s your favorite food",
                "what is your favourite food",
                "what's your favourite food",
                "what s your favourite food",
                "what food do you like",
                "what kind of food do you like"))
        {
            return "robot_favorite_food";
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your favorite music",
                "what's your favorite music",
                "what s your favorite music",
                "what is your favourite music",
                "what's your favourite music",
                "what s your favourite music",
                "what music do you like",
                "what kind of music do you like"))
        {
            return "robot_favorite_music";
        }

        if (MatchesAny(
                loweredTranscript,
                "are there others like you",
                "are there any others like you",
                "is there another jibo"))
        {
            return "robot_peers";
        }

        if (MatchesAny(
                loweredTranscript,
                "how much do you know",
                "what do you know",
                "how smart are you"))
        {
            return "robot_knowledge";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you kind",
                "do you think you are kind",
                "are you a kind robot"))
        {
            return "robot_is_kind";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you helpful",
                "do you think you are helpful",
                "are you a helpful robot"))
        {
            return "robot_is_helpful";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you curious",
                "do you think you are curious",
                "are you a curious robot"))
        {
            return "robot_is_curious";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you loyal",
                "do you think you are loyal",
                "are you a loyal robot"))
        {
            return "robot_is_loyal";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you mischievous",
                "do you think you are mischievous",
                "are you a mischievous robot"))
        {
            return "robot_is_mischievous";
        }

        if (MatchesAny(
                loweredTranscript,
                "are you likable",
                "are you likeable",
                "do you think you are likable",
                "do you think you are likeable"))
        {
            return "robot_is_likable";
        }

        if (MatchesAny(
                loweredTranscript,
                "can you order pizza",
                "can you order a pizza",
                "could you order a pizza",
                "order pizza",
                "order a pizza",
                "order us a pizza",
                "order me a pizza",
                "please order pizza") ||
            (loweredTranscript.Contains("order", StringComparison.Ordinal) &&
             loweredTranscript.Contains("pizza", StringComparison.Ordinal)))
        {
            return "order_pizza";
        }

        if (MatchesAny(
                loweredTranscript,
                "can you cook us a pizza",
                "flip a pizza",
                "make a pizza",
                "make pizza",
                "show pizza",
                "can you make pizza",
                "let's make pizza",
                "lets make pizza") ||
            (loweredTranscript.Contains("pizza", StringComparison.Ordinal) &&
             (loweredTranscript.Contains("make", StringComparison.Ordinal) ||
              loweredTranscript.Contains("cook", StringComparison.Ordinal) ||
              loweredTranscript.Contains("flip", StringComparison.Ordinal))))
        {
            return "pizza";
        }

        if (MatchesAny(loweredTranscript, "personal report", "my report", "daily report", "my update"))
        {
            return "personal_report";
        }

        if (MatchesAny(
                loweredTranscript,
                "shopping list",
                "grocery list",
                "to do list",
                "todo list",
                "add to my shopping list",
                "add to my to do list",
                "add to my todo list",
                "what's on my shopping list",
                "what is on my shopping list",
                "what's on my to do list",
                "what is on my to do list",
                "what are my tasks",
                "what do i need to buy",
                "what do i need to do"))
        {
            return loweredTranscript.Contains("to do", StringComparison.OrdinalIgnoreCase) ||
                   loweredTranscript.Contains("todo", StringComparison.OrdinalIgnoreCase) ||
                   loweredTranscript.Contains("task", StringComparison.OrdinalIgnoreCase)
                ? "todo_list"
                : "shopping_list";
        }

        if (IsWeatherRequest(loweredTranscript))
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

        if (IsWelcomeBackGreeting(loweredTranscript))
        {
            return "welcome_back";
        }

        if (IsGoodMorningGreeting(loweredTranscript))
        {
            return "good_morning";
        }

        if (IsGoodAfternoonGreeting(loweredTranscript))
        {
            return "good_afternoon";
        }

        if (IsGoodEveningGreeting(loweredTranscript))
        {
            return "good_evening";
        }

        if (IsGoodNightGreeting(loweredTranscript))
        {
            return "good_night";
        }

        if (MatchesAny(
                loweredTranscript,
                "how are you",
                "what's up",
                "what s up",
                "what up",
                "how are things",
                "how's things",
                "how is things",
                "how is your day",
                "how's your day"))
        {
            return "how_are_you";
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you up to",
                "what are you doing",
                "what have you been up to",
                "what are you into"))
        {
            return "robot_what_do_you_like_to_do";
        }

        if (MatchesAny(loweredTranscript, "hello", "hi", "hey"))
        {
            return "hello";
        }

        if (IsTimeRequest(loweredTranscript))
        {
            return "time";
        }

        if (MatchesAny(loweredTranscript, "what day is it", "what day is today"))
        {
            return "day";
        }

        if (IsDateRequest(loweredTranscript))
        {
            return "date";
        }

        return "chat";
    }

    private static JiboInteractionDecision BuildWordOfTheDayLaunchDecision()
    {
        return new JiboInteractionDecision(
            "word_of_the_day",
            "Starting word of the day.",
            "@be/word-of-the-day",
            SkillPayload: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["domain"] = "word-of-the-day",
                ["skillId"] = "@be/word-of-the-day"
            });
    }

    private static JiboInteractionDecision BuildRadioLaunchDecision()
    {
        return new JiboInteractionDecision(
            "radio",
            "Opening the radio.",
            "@be/radio",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/radio"
            });
    }

    private static JiboInteractionDecision BuildPhotoGalleryLaunchDecision()
    {
        return new JiboInteractionDecision(
            "photo_gallery",
            "Opening the photo gallery.",
            "@be/gallery",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/gallery",
                ["localIntent"] = "menu"
            });
    }

    private static JiboInteractionDecision BuildPhotoCreateDecision(string intentName, string replyText, string localIntent)
    {
        return new JiboInteractionDecision(
            intentName,
            replyText,
            "@be/create",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/create",
                ["localIntent"] = localIntent
            });
    }

    private static JiboInteractionDecision BuildStopDecision()
    {
        return new JiboInteractionDecision(
            "stop",
            "Stopping.",
            "@be/idle",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/idle",
                ["globalIntent"] = "stop",
                ["nluDomain"] = "global_commands"
            });
    }

    private static JiboInteractionDecision BuildVolumeControlDecision(string intentName, string globalIntent, string volumeLevel)
    {
        return new JiboInteractionDecision(
            intentName,
            "Adjusting volume.",
            "global_commands",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["globalIntent"] = globalIntent,
                ["nluDomain"] = "global_commands",
                ["volumeLevel"] = volumeLevel
            });
    }

    private static JiboInteractionDecision BuildSettingsVolumeDecision()
    {
        return new JiboInteractionDecision(
            "volume_query",
            "Opening volume controls.",
            "@be/settings",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/settings",
                ["localIntent"] = "volumeQuery"
            });
    }

    private static JiboInteractionDecision BuildClockLaunchDecision(string intentName, string domain, string clockIntent, string replyText)
    {
        return new JiboInteractionDecision(
            intentName,
            replyText,
            "@be/clock",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/clock",
                ["domain"] = domain,
                ["clockIntent"] = clockIntent
            });
    }

    private static JiboInteractionDecision BuildClockLaunchDecision(string domain, string replyText)
    {
        return BuildClockLaunchDecision($"{domain}_menu", domain, "menu", replyText);
    }

    private static JiboInteractionDecision BuildClockClarifyDecision(string intentName, string domain, string replyText)
    {
        return new JiboInteractionDecision(
            intentName,
            replyText,
            "@be/clock",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/clock",
                ["domain"] = domain,
                ["clockIntent"] = "set"
            });
    }

    private static JiboInteractionDecision BuildTimerValueDecision(
        string loweredTranscript,
        bool allowImplicit,
        IReadOnlyDictionary<string, string> clientEntities)
    {
        var timer = TryReadStructuredTimerValue(clientEntities) ??
                    TryParseTimerValue(loweredTranscript, allowImplicit) ??
                    new ClockTimerValue("0", "1", "null");

        return new JiboInteractionDecision(
            "timer_value",
            "Setting your timer.",
            "@be/clock",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/clock",
                ["domain"] = "timer",
                ["clockIntent"] = "start",
                ["hours"] = timer.Hours,
                ["minutes"] = timer.Minutes,
                ["seconds"] = timer.Seconds
            });
    }

    private static JiboInteractionDecision BuildAlarmValueDecision(
        string loweredTranscript,
        bool allowImplicit,
        DateTimeOffset? referenceLocalTime,
        IReadOnlyDictionary<string, string> clientEntities)
    {
        var alarm = TryReadStructuredAlarmValue(clientEntities) ??
                    TryParseAlarmValue(loweredTranscript, allowImplicit, referenceLocalTime) ??
                    new ClockAlarmValue("7:00", "am");

        return new JiboInteractionDecision(
            "alarm_value",
            "Setting your alarm.",
            "@be/clock",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/clock",
                ["domain"] = "alarm",
                ["clockIntent"] = "start",
                ["time"] = alarm.Time,
                ["ampm"] = alarm.AmPm
            });
    }

    private static JiboInteractionDecision BuildRadioGenreLaunchDecision(string loweredTranscript)
    {
        var station = TryResolveRadioGenre(loweredTranscript) ?? "Country";

        return new JiboInteractionDecision(
            "radio_genre",
            $"Playing {FormatRadioGenreForSpeech(station)} on the radio.",
            "@be/radio",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "@be/radio",
                ["station"] = station
            });
    }

    private static JiboInteractionDecision BuildWordOfTheDayGuessDecision(
        IReadOnlyDictionary<string, string> clientEntities,
        string transcript,
        IReadOnlyList<string> listenAsrHints)
    {
        var guess = ResolveWordOfTheDayGuess(clientEntities, transcript, listenAsrHints);

        var reply = string.IsNullOrWhiteSpace(guess)
            ? "I heard your word of the day guess."
            : $"I heard {guess}.";

        return new JiboInteractionDecision(
            "word_of_the_day_guess",
            reply,
            "@be/word-of-the-day",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["guess"] = guess,
                ["skillId"] = "@be/word-of-the-day",
                ["cloudResponseMode"] = "completion_only"
            });
    }

    private static string ResolveWordOfTheDayGuess(
        IReadOnlyDictionary<string, string> clientEntities,
        string transcript,
        IReadOnlyList<string> listenAsrHints)
    {
        if (clientEntities.TryGetValue("guess", out var guessValue) &&
            !string.IsNullOrWhiteSpace(guessValue))
        {
            return guessValue;
        }

        var loweredTranscript = NormalizeGuessToken(transcript);
        var hintIndex = loweredTranscript switch
        {
            "1" or "one" or "first" => 0,
            "2" or "two" or "second" => 1,
            "3" or "three" or "third" => 2,
            _ => -1
        };

        if (hintIndex >= 0 && hintIndex < listenAsrHints.Count)
        {
            return listenAsrHints[hintIndex];
        }

        var fuzzyHintMatch = FindClosestHint(loweredTranscript, listenAsrHints);
        return !string.IsNullOrWhiteSpace(fuzzyHintMatch) ? fuzzyHintMatch : transcript;
    }

    private static bool IsYesNoTurn(TurnContext turn)
    {
        return ReadRules(turn, "listenRules")
            .Concat(ReadRules(turn, "clientRules"))
            .Concat(ReadRules(turn, "listenAsrHints"))
            .Any(IsYesNoRule);
    }

    private static string? ReadPrimaryYesNoRule(
        IReadOnlyList<string> clientRules,
        IReadOnlyList<string> listenRules)
    {
        return listenRules
            .Concat(clientRules)
            .FirstOrDefault(IsConstrainedYesNoRule);
    }

    private static bool IsYesNoRule(string rule)
    {
        return string.Equals(rule, "$YESNO", StringComparison.OrdinalIgnoreCase) ||
               IsConstrainedYesNoRule(rule);
    }

    private static bool IsConstrainedYesNoRule(string rule)
    {
        return string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "word-of-the-day/surprise", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAffirmativeYesNoIntent(string? yesNoRule)
    {
        if (string.Equals(yesNoRule, "word-of-the-day/surprise", StringComparison.OrdinalIgnoreCase))
        {
            return "word_of_the_day";
        }

        if (string.Equals(yesNoRule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase))
        {
            return "surprise";
        }

        return "yes";
    }

    private static string ResolveNegativeYesNoIntent(string? yesNoRule)
    {
        _ = yesNoRule;
        return "no";
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

    private static string DescribePersonaAge(DateOnly referenceDate, DateOnly birthday)
    {
        if (referenceDate < birthday)
        {
            return "just getting started";
        }

        var totalDays = referenceDate.DayNumber - birthday.DayNumber;
        if (totalDays <= 31)
        {
            return $"{FormatAgeUnit(totalDays, "day")} old";
        }

        var totalMonths = (referenceDate.Year - birthday.Year) * 12 + referenceDate.Month - birthday.Month;
        if (referenceDate.Day < birthday.Day)
        {
            totalMonths -= 1;
        }

        totalMonths = Math.Max(totalMonths, 0);
        if (totalMonths < 12)
        {
            return $"{FormatAgeUnit(totalMonths, "month")} old";
        }

        var years = totalMonths / 12;
        var months = totalMonths % 12;
        return months == 0
            ? $"{FormatAgeUnit(years, "year")} old"
            : $"{FormatAgeUnit(years, "year")} and {FormatAgeUnit(months, "month")} old";
    }

    private static string FormatAgeUnit(int value, string singular)
    {
        var plural = value == 1 ? singular : $"{singular}s";
        return $"{value} {plural}";
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

    private static IReadOnlyDictionary<string, string> ReadEntities(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("clientEntities", out var value) || value is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } json => json.EnumerateObject()
                .Where(static property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, string> typed => typed,
            IDictionary<string, object?> dictionary => dictionary
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static DateTimeOffset? TryResolveReferenceLocalTime(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("context", out var value) || value is null)
        {
            return null;
        }

        try
        {
            var contextJson = value.ToString();
            if (string.IsNullOrWhiteSpace(contextJson))
            {
                return null;
            }

            using var document = JsonDocument.Parse(contextJson);
            if (!document.RootElement.TryGetProperty("runtime", out var runtime) ||
                runtime.ValueKind != JsonValueKind.Object ||
                !runtime.TryGetProperty("location", out var location) ||
                location.ValueKind != JsonValueKind.Object ||
                !location.TryGetProperty("iso", out var iso) ||
                iso.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var isoValue = iso.GetString();
            return DateTimeOffset.TryParse(isoValue, out var parsed)
                ? parsed
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesAny(string loweredTranscript, params string[] candidates)
    {
        return candidates.Any(candidate => loweredTranscript.Contains(candidate, StringComparison.Ordinal));
    }

    private static bool IsAffirmativeReply(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return TryClassifyYesNoReply(normalized) == YesNoReply.Affirmative;
    }

    private static bool IsNegativeReply(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return TryClassifyYesNoReply(normalized) == YesNoReply.Negative;
    }

    private static YesNoReply TryClassifyYesNoReply(string normalizedTranscript)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return YesNoReply.None;
        }

        var normalized = normalizedTranscript;
        while (TryTrimLeadingAcknowledgement(normalized, out var trimmed))
        {
            normalized = trimmed;
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return YesNoReply.None;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return YesNoReply.None;
        }

        if (YesNoNegativeLeadTokens.Contains(tokens[0]))
        {
            return YesNoReply.Negative;
        }

        if (YesNoAffirmativeLeadTokens.Contains(tokens[0]))
        {
            return YesNoReply.Affirmative;
        }

        var leadingTwo = tokens.Length >= 2 ? $"{tokens[0]} {tokens[1]}" : null;
        if (leadingTwo is not null)
        {
            if (YesNoNegativeLeadPhrases.Contains(leadingTwo))
            {
                return YesNoReply.Negative;
            }

            if (YesNoAffirmativeLeadPhrases.Contains(leadingTwo))
            {
                return YesNoReply.Affirmative;
            }
        }

        var leadingThree = tokens.Length >= 3 ? $"{tokens[0]} {tokens[1]} {tokens[2]}" : null;
        if (leadingThree is not null)
        {
            if (YesNoNegativeLeadPhrases.Contains(leadingThree))
            {
                return YesNoReply.Negative;
            }

            if (YesNoAffirmativeLeadPhrases.Contains(leadingThree))
            {
                return YesNoReply.Affirmative;
            }
        }

        return TryClassifyTrailingYesNoReply(tokens);
    }

    private static bool TryTrimLeadingAcknowledgement(string normalizedTranscript, out string trimmedTranscript)
    {
        foreach (var acknowledgement in YesNoAcknowledgementPrefixes)
        {
            if (string.Equals(normalizedTranscript, acknowledgement, StringComparison.Ordinal))
            {
                trimmedTranscript = string.Empty;
                return true;
            }

            if (normalizedTranscript.StartsWith($"{acknowledgement} ", StringComparison.Ordinal))
            {
                trimmedTranscript = normalizedTranscript[(acknowledgement.Length + 1)..].TrimStart();
                return true;
            }
        }

        trimmedTranscript = normalizedTranscript;
        return false;
    }

    private static YesNoReply TryClassifyTrailingYesNoReply(IReadOnlyList<string> tokens)
    {
        var selectedReply = YesNoReply.None;
        var selectedIndex = -1;

        void Consider(YesNoReply candidateReply, int candidateIndex)
        {
            if (candidateIndex < 0 || candidateIndex < selectedIndex)
            {
                return;
            }

            selectedReply = candidateReply;
            selectedIndex = candidateIndex;
        }

        for (var index = 0; index < tokens.Count; index += 1)
        {
            var token = tokens[index];
            if (YesNoNegativeLeadTokens.Contains(token))
            {
                Consider(YesNoReply.Negative, index);
                continue;
            }

            if (YesNoAffirmativeLeadTokens.Contains(token))
            {
                Consider(YesNoReply.Affirmative, index);
            }
        }

        for (var index = 0; index + 1 < tokens.Count; index += 1)
        {
            var phrase = $"{tokens[index]} {tokens[index + 1]}";
            if (YesNoNegativeLeadPhrases.Contains(phrase))
            {
                Consider(YesNoReply.Negative, index + 1);
                continue;
            }

            if (YesNoAffirmativeLeadPhrases.Contains(phrase))
            {
                Consider(YesNoReply.Affirmative, index + 1);
            }
        }

        for (var index = 0; index + 2 < tokens.Count; index += 1)
        {
            var phrase = $"{tokens[index]} {tokens[index + 1]} {tokens[index + 2]}";
            if (YesNoNegativeLeadPhrases.Contains(phrase))
            {
                Consider(YesNoReply.Negative, index + 2);
                continue;
            }

            if (YesNoAffirmativeLeadPhrases.Contains(phrase))
            {
                Consider(YesNoReply.Affirmative, index + 2);
            }
        }

        return selectedReply;
    }

    private static bool IsTimeRequest(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized is "time" or "the time" or "current time" or "what time is it" or "what s the time" or "what is the time")
        {
            return true;
        }

        return normalized.StartsWith("what time", StringComparison.Ordinal) ||
               normalized.StartsWith("tell me the time", StringComparison.Ordinal) ||
               normalized.StartsWith("show me the time", StringComparison.Ordinal);
    }

    private static bool IsDateRequest(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized is
            "what is the date" or
            "what s the date" or
            "what date is it" or
            "today s date" or
            "today date" or
            "what is today s date" or
            "what s today s date" or
            "what is todays date" or
            "what s todays date";
    }

    private static bool IsWeatherRequest(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        if (IsWeatherTopicQuestion(normalized))
        {
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "weather",
                "forecast",
                "how is the weather",
                "how s the weather",
                "how's the weather",
                "check the weather",
                "weather report",
                "what's today s weather",
                "what's today's weather",
                "what is the weather",
                "what will the weather",
                "what will tomorrow s weather",
                "what will tomorrow's weather",
                "look up the forecast",
                "launch the weather skill",
                "what is today s humidity",
                "what is today's humidity",
                "what's the humidity",
                "what is the humidity",
                "what's today's forecast",
                "what s today's forecast",
                "what s today s forecast",
                "what is today s forecast",
                "what is today's forecast",
                "what's today's weather look like",
                "what s today's weather look like",
                "what s today s weather look like",
                "what is today s weather look like",
                "what is today's weather look like"))
        {
            return true;
        }

        if (MatchesAny(
            loweredTranscript,
            "will it rain",
            "will it snow",
            "is it raining",
            "is it snowing",
            "is there going to be hail",
            "does it look like rain",
            "does it seem like snow",
            "is it going to rain",
            "is it going to snow",
            "do you think it will rain",
            "do you think it will snow"))
        {
            return true;
        }

        return WeatherConditionForecastPattern.IsMatch(loweredTranscript);
    }

    private static bool IsWeatherTopicQuestion(string normalizedTranscript)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        var mentionsWeatherTopic =
            normalizedTranscript.Contains("weather", StringComparison.Ordinal) ||
            normalizedTranscript.Contains("forecast", StringComparison.Ordinal) ||
            normalizedTranscript.Contains("temperature", StringComparison.Ordinal) ||
            normalizedTranscript.Contains("humidity", StringComparison.Ordinal);
        if (!mentionsWeatherTopic)
        {
            return false;
        }

        if (normalizedTranscript.StartsWith("what ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("how ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("check ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("show ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("tell ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("look up ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("launch ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("give me ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("temperature ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("forecast ", StringComparison.Ordinal) ||
            normalizedTranscript.StartsWith("weather ", StringComparison.Ordinal))
        {
            return true;
        }

        return WeatherTopicLocationPattern.IsMatch(normalizedTranscript);
    }

    private static string? TryResolveWeatherLocationQuery(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var match = WeatherLocationPattern.Match(normalized);
        if (!match.Success)
        {
            return null;
        }

        var candidate = match.Groups["location"].Value.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        candidate = WeatherLocationSuffixPattern.Replace(candidate, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            GenericWeatherLocationTerms.Contains(candidate))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(candidate)
            ? null
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(candidate);
    }

    private static (double Latitude, double Longitude)? TryResolveWeatherCoordinates(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("context", out var contextValue) ||
            contextValue is null ||
            string.IsNullOrWhiteSpace(contextValue.ToString()))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(contextValue.ToString()!);
            if (!document.RootElement.TryGetProperty("runtime", out var runtime) ||
                runtime.ValueKind != JsonValueKind.Object ||
                !runtime.TryGetProperty("location", out var location) ||
                location.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var latitude = TryReadDoubleProperty(location, "lat", "latitude");
            var longitude = TryReadDoubleProperty(location, "lng", "lon", "longitude");
            return latitude is not null && longitude is not null
                ? (latitude.Value, longitude.Value)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static GreetingPresenceProfile ResolveGreetingPresenceProfile(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("context", out var contextValue) ||
            contextValue is null ||
            string.IsNullOrWhiteSpace(contextValue.ToString()))
        {
            return GreetingPresenceProfile.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(contextValue.ToString()!);
            if (!document.RootElement.TryGetProperty("runtime", out var runtime) ||
                runtime.ValueKind != JsonValueKind.Object)
            {
                return GreetingPresenceProfile.Empty;
            }

            var loopUsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (runtime.TryGetProperty("loop", out var loop) &&
                loop.ValueKind == JsonValueKind.Object &&
                loop.TryGetProperty("users", out var users) &&
                users.ValueKind == JsonValueKind.Array)
            {
                foreach (var user in users.EnumerateArray())
                {
                    var id = TryReadStringProperty(user, "id");
                    var firstName = TryReadStringProperty(user, "firstName");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(firstName))
                    {
                        loopUsers[id] = firstName;
                    }
                }
            }

            var speakerId = string.Empty;
            var peoplePresentIds = new List<string>();
            if (runtime.TryGetProperty("perception", out var perception) &&
                perception.ValueKind == JsonValueKind.Object)
            {
                if (perception.TryGetProperty("speaker", out var speaker))
                {
                    if (speaker.ValueKind == JsonValueKind.String)
                    {
                        speakerId = speaker.GetString() ?? string.Empty;
                    }
                    else if (speaker.ValueKind == JsonValueKind.Object)
                    {
                        speakerId = TryReadStringProperty(speaker, "id", "looperID", "looperId") ?? string.Empty;
                    }
                }

                if (perception.TryGetProperty("peoplePresent", out var peoplePresent) &&
                    peoplePresent.ValueKind == JsonValueKind.Array)
                {
                    foreach (var person in peoplePresent.EnumerateArray())
                    {
                        var personId = person.ValueKind switch
                        {
                            JsonValueKind.String => person.GetString(),
                            JsonValueKind.Object => TryReadStringProperty(person, "id", "looperID", "looperId"),
                            _ => null
                        };

                        if (!string.IsNullOrWhiteSpace(personId) &&
                            !string.Equals(personId, "NOT_TRAINED", StringComparison.OrdinalIgnoreCase))
                        {
                            peoplePresentIds.Add(personId);
                        }
                    }
                }
            }

            var triggerLooperId = turn.Attributes.TryGetValue("triggerLooperId", out var rawTriggerLooperId)
                ? rawTriggerLooperId?.ToString()
                : null;
            var primaryPersonId = !string.IsNullOrWhiteSpace(speakerId)
                ? speakerId
                : !string.IsNullOrWhiteSpace(triggerLooperId)
                    ? triggerLooperId
                    : peoplePresentIds.FirstOrDefault();

            return new GreetingPresenceProfile(
                primaryPersonId,
                string.IsNullOrWhiteSpace(speakerId) ? null : speakerId,
                peoplePresentIds,
                loopUsers);
        }
        catch
        {
            return GreetingPresenceProfile.Empty;
        }
    }

    private static string? TryReadStringProperty(JsonElement source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (source.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static double? TryReadDoubleProperty(JsonElement source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (source.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ShouldUseCelsius(TurnContext turn, string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        if (normalized.Contains("celsius", StringComparison.Ordinal) ||
            normalized.Contains("centigrade", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("fahrenheit", StringComparison.Ordinal))
        {
            return false;
        }

        var entities = ReadEntities(turn);
        if (entities.TryGetValue("temperatureUnit", out var entityUnit))
        {
            if (entityUnit.Contains("celsius", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (entityUnit.Contains("fahrenheit", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var locale = turn.Locale ?? string.Empty;
        if (locale.EndsWith("-US", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static WeatherDateEntity ResolveWeatherDateEntity(
        TurnContext turn,
        string transcript,
        string normalizedTranscript,
        DateTimeOffset? referenceLocalTime)
    {
        normalizedTranscript = string.IsNullOrWhiteSpace(normalizedTranscript)
            ? NormalizeCommandPhrase(transcript)
            : normalizedTranscript;

        if (TryResolveWeatherDateEntityFromTranscript(normalizedTranscript, referenceLocalTime, out var entityFromTranscript))
        {
            return entityFromTranscript;
        }

        var entities = ReadEntities(turn);
        if (TryResolveWeatherDateEntityFromClientEntities(entities, referenceLocalTime, out var entityFromClient) &&
            ShouldAcceptClientWeatherDateEntity(normalizedTranscript))
        {
            return entityFromClient;
        }

        return WeatherDateEntity.None;
    }

    private static bool TryResolveWeatherDateEntityFromTranscript(
        string normalizedTranscript,
        DateTimeOffset? referenceLocalTime,
        out WeatherDateEntity weatherDate)
    {
        weatherDate = WeatherDateEntity.None;
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        if (normalizedTranscript.Contains("day after tomorrow", StringComparison.Ordinal))
        {
            weatherDate = new WeatherDateEntity("day_after_tomorrow", 2, "The day after tomorrow");
            return true;
        }

        if (MatchesAny(normalizedTranscript, "tomorrow", "tomorrow s", "tomorrow's"))
        {
            weatherDate = new WeatherDateEntity("tomorrow", 1, "Tomorrow");
            return true;
        }

        if (referenceLocalTime is not null &&
            TryResolveWeatherTimeRangeOffset(normalizedTranscript, referenceLocalTime.Value, out var rangeOffset, out var rangeLeadIn) &&
            rangeOffset > 0)
        {
            weatherDate = new WeatherDateEntity("range", rangeOffset, rangeLeadIn);
            return true;
        }

        if (referenceLocalTime is not null &&
            TryResolveWeatherDayOfWeekOffset(normalizedTranscript, referenceLocalTime.Value, out var dayOffset, out var dayName) &&
            dayOffset > 0)
        {
            weatherDate = new WeatherDateEntity("weekday", dayOffset, $"On {dayName}");
            return true;
        }

        return false;
    }

    private static bool ShouldAcceptClientWeatherDateEntity(string normalizedTranscript)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return true;
        }

        if (HasExplicitWeatherDateCue(normalizedTranscript))
        {
            return false;
        }

        if (HasWeatherLocationClause(normalizedTranscript))
        {
            return false;
        }

        return !normalizedTranscript.Contains("forecast", StringComparison.Ordinal);
    }

    private static bool HasExplicitWeatherDateCue(string normalizedTranscript)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        if (MatchesAny(
                normalizedTranscript,
                "today",
                "today s",
                "today's",
                "tonight",
                "tomorrow",
                "tomorrow s",
                "tomorrow's",
                "day after tomorrow",
                "this week",
                "next week",
                "weekend",
                "monday",
                "tuesday",
                "wednesday",
                "thursday",
                "friday",
                "saturday",
                "sunday"))
        {
            return true;
        }

        return WeatherDayOfWeekPattern.IsMatch(normalizedTranscript);
    }

    private static bool HasWeatherLocationClause(string normalizedTranscript)
    {
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        return WeatherTopicLocationPattern.IsMatch(normalizedTranscript) ||
               WeatherLocationPattern.IsMatch(normalizedTranscript);
    }

    private static bool TryResolveWeatherDateEntityFromClientEntities(
        IReadOnlyDictionary<string, string> clientEntities,
        DateTimeOffset? referenceLocalTime,
        out WeatherDateEntity weatherDate)
    {
        weatherDate = WeatherDateEntity.None;
        if (!TryReadClientWeatherDateValue(clientEntities, out var rawDateValue))
        {
            return false;
        }

        var normalizedDate = NormalizeCommandPhrase(rawDateValue);
        if (normalizedDate.Contains("day after tomorrow", StringComparison.Ordinal))
        {
            weatherDate = new WeatherDateEntity("day_after_tomorrow", 2, "The day after tomorrow");
            return true;
        }

        if (MatchesAny(normalizedDate, "tomorrow", "tomorrow s", "tomorrow's"))
        {
            weatherDate = new WeatherDateEntity("tomorrow", 1, "Tomorrow");
            return true;
        }

        if (referenceLocalTime is not null &&
            TryResolveWeatherTimeRangeOffset(normalizedDate, referenceLocalTime.Value, out var rangeOffset, out var rangeLeadIn) &&
            rangeOffset > 0)
        {
            weatherDate = new WeatherDateEntity("range", rangeOffset, rangeLeadIn);
            return true;
        }

        DateOnly targetDate;
        if (DateOnly.TryParse(rawDateValue, out var parsedDate))
        {
            targetDate = parsedDate;
        }
        else if (DateTimeOffset.TryParse(rawDateValue, out var parsedDateTimeOffset))
        {
            targetDate = DateOnly.FromDateTime(parsedDateTimeOffset.DateTime);
        }
        else
        {
            return false;
        }

        var referenceDate = DateOnly.FromDateTime((referenceLocalTime ?? DateTimeOffset.UtcNow).DateTime);
        var dayOffset = targetDate.DayNumber - referenceDate.DayNumber;
        if (dayOffset <= 0)
        {
            return false;
        }

        weatherDate = dayOffset == 1
            ? new WeatherDateEntity("tomorrow", 1, "Tomorrow")
            : new WeatherDateEntity(
                "date",
                dayOffset,
                $"On {targetDate.ToDateTime(TimeOnly.MinValue).ToString("dddd", CultureInfo.InvariantCulture)}");
        return true;
    }

    private static bool TryReadClientWeatherDateValue(
        IReadOnlyDictionary<string, string> clientEntities,
        out string dateValue)
    {
        foreach (var key in WeatherDateEntityKeys)
        {
            if (!clientEntities.TryGetValue(key, out var rawValue) ||
                string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            dateValue = rawValue.Trim();
            return true;
        }

        dateValue = string.Empty;
        return false;
    }

    private static bool TryResolveWeatherDayOfWeekOffset(
        string normalizedTranscript,
        DateTimeOffset referenceLocalTime,
        out int dayOffset,
        out string dayName)
    {
        dayOffset = 0;
        dayName = string.Empty;

        var match = WeatherDayOfWeekPattern.Match(normalizedTranscript);
        if (!match.Success)
        {
            return false;
        }

        var dayToken = match.Groups["day"].Value;
        if (!TryParseDayOfWeek(dayToken, out var targetDay))
        {
            return false;
        }

        var currentDay = referenceLocalTime.DayOfWeek;
        dayOffset = ((int)targetDay - (int)currentDay + 7) % 7;
        if (match.Groups["next"].Success)
        {
            dayOffset = dayOffset == 0 ? 7 : dayOffset + 7;
        }
        else if (match.Groups["this"].Success && dayOffset == 0)
        {
            return false;
        }

        dayName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dayToken);
        return dayOffset > 0;
    }

    private static bool TryResolveWeatherTimeRangeOffset(
        string normalizedTranscript,
        DateTimeOffset referenceLocalTime,
        out int dayOffset,
        out string leadIn)
    {
        dayOffset = 0;
        leadIn = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        var hasNextWeekend = normalizedTranscript.Contains("next weekend", StringComparison.Ordinal);
        var hasThisWeekend =
            normalizedTranscript.Contains("this weekend", StringComparison.Ordinal) ||
            normalizedTranscript.Contains("the weekend", StringComparison.Ordinal) ||
            normalizedTranscript.EndsWith("weekend", StringComparison.Ordinal);
        if (hasNextWeekend || hasThisWeekend)
        {
            dayOffset = ((int)DayOfWeek.Saturday - (int)referenceLocalTime.DayOfWeek + 7) % 7;
            if (hasNextWeekend)
            {
                dayOffset = dayOffset + 7;
                leadIn = "Next weekend";
            }
            else
            {
                // If it's already Saturday, prefer forecasting Sunday for "this weekend".
                if (dayOffset == 0 && referenceLocalTime.DayOfWeek == DayOfWeek.Saturday)
                {
                    dayOffset = 1;
                }

                leadIn = "This weekend";
            }

            return dayOffset > 0;
        }

        var hasNextWeek = normalizedTranscript.Contains("next week", StringComparison.Ordinal);
        if (hasNextWeek)
        {
            dayOffset = 7;
            leadIn = "Next week";
            return true;
        }

        var hasThisWeek = normalizedTranscript.Contains("this week", StringComparison.Ordinal);
        if (hasThisWeek)
        {
            dayOffset = referenceLocalTime.DayOfWeek == DayOfWeek.Saturday ? 1 : 2;
            leadIn = "Later this week";
            return true;
        }

        return false;
    }

    private static bool TryParseDayOfWeek(string dayToken, out DayOfWeek dayOfWeek)
    {
        dayOfWeek = DayOfWeek.Sunday;
        return dayToken switch
        {
            "monday" => AssignDayOfWeek(DayOfWeek.Monday, out dayOfWeek),
            "tuesday" => AssignDayOfWeek(DayOfWeek.Tuesday, out dayOfWeek),
            "wednesday" => AssignDayOfWeek(DayOfWeek.Wednesday, out dayOfWeek),
            "thursday" => AssignDayOfWeek(DayOfWeek.Thursday, out dayOfWeek),
            "friday" => AssignDayOfWeek(DayOfWeek.Friday, out dayOfWeek),
            "saturday" => AssignDayOfWeek(DayOfWeek.Saturday, out dayOfWeek),
            "sunday" => AssignDayOfWeek(DayOfWeek.Sunday, out dayOfWeek),
            _ => false
        };
    }

    private static bool AssignDayOfWeek(DayOfWeek value, out DayOfWeek target)
    {
        target = value;
        return true;
    }

    private static string? TryResolveWeatherConditionEntity(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        return normalized switch
        {
            _ when normalized.Contains("rain", StringComparison.Ordinal) => "rain",
            _ when normalized.Contains("snow", StringComparison.Ordinal) => "snow",
            _ when normalized.Contains("hail", StringComparison.Ordinal) => "hail",
            _ when normalized.Contains("sunny", StringComparison.Ordinal) || normalized.Contains("clear", StringComparison.Ordinal) => "sunny",
            _ when normalized.Contains("cloud", StringComparison.Ordinal) => "cloudy",
            _ when normalized.Contains("wind", StringComparison.Ordinal) => "windy",
            _ when normalized.Contains("fog", StringComparison.Ordinal) => "fog",
            _ => null
        };
    }

    private static bool IsWelcomeBackGreeting(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "i am back",
            "i m back",
            "im back",
            "i am home",
            "i m home",
            "im home",
            "i'm back",
            "i'm home",
            "welcome back");
    }

    private static bool IsGoodMorningGreeting(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "good morning",
            "morning jibo",
            "morning, jibo");
    }

    private static bool IsGoodAfternoonGreeting(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "good afternoon",
            "afternoon jibo",
            "afternoon, jibo");
    }

    private static bool IsGoodEveningGreeting(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "good evening",
            "evening jibo",
            "evening, jibo");
    }

    private static bool IsGoodNightGreeting(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "good night",
            "night jibo",
            "night, jibo");
    }

    private static bool IsDanceQuestion(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "do you like to dance",
            "do you like dancing",
            "what kind of dance do you like",
            "what kind of dancing do you like",
            "do you enjoy dancing");
    }

    private static bool IsRobotBirthdayQuestion(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        if (MatchesAny(
                normalized,
                "when is your birthday",
                "when s your birthday",
                "what s your birthday",
                "what is your birthday",
                "when is your bday",
                "when s your bday",
                "what s your bday",
                "what is your bday",
                "when were you born",
                "what day is your birthday"))
        {
            return true;
        }

        return (normalized.Contains("your birthday", StringComparison.Ordinal) ||
                normalized.Contains("your bday", StringComparison.Ordinal) ||
                normalized.Contains("your birth date", StringComparison.Ordinal))
               && !normalized.Contains("my birthday", StringComparison.Ordinal);
    }

    private static bool IsNameSetStatement(string loweredTranscript)
    {
        return TryExtractNameFact(loweredTranscript) is not null;
    }

    private static bool IsNameRecallQuestion(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "what is my name",
            "what s my name",
            "what's my name",
            "who am i",
            "do you remember my name",
            "do you know me",
            "do you remember me",
            "who is this",
            "can you recognize me");
    }

    private static string? TryExtractNameFact(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var prefixes = new[]
        {
            "my name is ",
            "call me "
        };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = normalized[prefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        return null;
    }

    private static bool IsUserBirthdayRecallQuestion(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "when is my birthday",
            "when's my birthday",
            "what is my birthday",
            "what s my birthday",
            "what's my birthday",
            "when is my bday",
            "when s my bday",
            "what is my bday",
            "what s my bday",
            "what's my bday",
            "do you remember my birthday");
    }

    private static bool IsUserBirthdaySetStatement(string loweredTranscript)
    {
        return TryExtractBirthdayFact(loweredTranscript) is not null;
    }

    private static bool IsUserBirthdaySetAttempt(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return normalized.Contains("my birthday is", StringComparison.Ordinal) ||
               normalized.Contains("my bday is", StringComparison.Ordinal);
    }

    private static bool IsUserBirthdayRecallAttempt(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return (normalized.Contains("my birthday", StringComparison.Ordinal) ||
                normalized.Contains("my bday", StringComparison.Ordinal)) &&
               (normalized.StartsWith("when", StringComparison.Ordinal) ||
                normalized.StartsWith("what", StringComparison.Ordinal) ||
                normalized.StartsWith("tell me", StringComparison.Ordinal) ||
                normalized.StartsWith("do you remember", StringComparison.Ordinal));
    }

    private static string? TryExtractBirthdayFact(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var markers = new[]
        {
            "my birthday is ",
            "my bday is "
        };

        foreach (var marker in markers)
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var value = normalized[(markerIndex + marker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsPreferenceRecallQuestion(string loweredTranscript)
    {
        return TryExtractPreferenceLookupCategory(loweredTranscript) is not null;
    }

    private static bool IsPreferenceSetStatement(string loweredTranscript)
    {
        return TryExtractPreferenceSet(loweredTranscript) is not null;
    }

    private static bool IsPreferenceSetAttempt(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        if (IsPreferenceRecallAttempt(normalized))
        {
            return false;
        }

        return normalized.Contains("my favorite", StringComparison.Ordinal) ||
               normalized.Contains("my favourite", StringComparison.Ordinal) ||
               PreferenceReverseMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsPreferenceRecallAttempt(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return normalized.StartsWith("what is my favorite", StringComparison.Ordinal) ||
               normalized.StartsWith("what s my favorite", StringComparison.Ordinal) ||
               normalized.StartsWith("what is my favourite", StringComparison.Ordinal) ||
               normalized.StartsWith("what s my favourite", StringComparison.Ordinal) ||
               normalized.StartsWith("do you remember my favorite", StringComparison.Ordinal) ||
               normalized.StartsWith("do you remember my favourite", StringComparison.Ordinal);
    }

    private static string? TryExtractPreferenceLookupCategory(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var prefixes = new[]
        {
            "what is my favorite ",
            "what s my favorite ",
            "what's my favorite ",
            "do you remember my favorite ",
            "what is my favourite ",
            "what s my favourite ",
            "what's my favourite ",
            "do you remember my favourite "
        };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var category = normalized[prefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(category) ? null : category;
        }

        return null;
    }

    private static (string Category, string Value)? TryExtractPreferenceSet(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        foreach (var marker in PreferenceSetMarkers)
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var preferencePhrase = normalized[(markerIndex + marker.Length)..];
            var splitMarker = " is ";
            var splitIndex = preferencePhrase.IndexOf(splitMarker, StringComparison.Ordinal);
            if (splitIndex <= 0 || splitIndex >= preferencePhrase.Length - splitMarker.Length)
            {
                var fallbackPreference = TryExtractPreferenceSetWithoutCopula(preferencePhrase);
                if (fallbackPreference is not null)
                {
                    return fallbackPreference;
                }

                continue;
            }

            var category = preferencePhrase[..splitIndex].Trim();
            var value = preferencePhrase[(splitIndex + splitMarker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(value))
            {
                return (category, value);
            }
        }

        if (normalized.StartsWith("what ", StringComparison.Ordinal) ||
            normalized.StartsWith("do you remember ", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var marker in PreferenceReverseMarkers)
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex <= 0 || markerIndex >= normalized.Length - marker.Length)
            {
                continue;
            }

            var value = normalized[..markerIndex].Trim();
            var category = normalized[(markerIndex + marker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(value))
            {
                return (category, value);
            }
        }

        return null;
    }

    private static (string Category, string Value)? TryExtractPreferenceSetWithoutCopula(string preferencePhrase)
    {
        if (string.IsNullOrWhiteSpace(preferencePhrase))
        {
            return null;
        }

        var normalized = preferencePhrase.Trim();
        if (normalized.Contains(" is ", StringComparison.Ordinal) ||
            normalized.Contains(" are ", StringComparison.Ordinal) ||
            normalized.EndsWith(" is", StringComparison.Ordinal) ||
            normalized.EndsWith(" are", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var category = parts[0];
        var value = string.Join(' ', parts.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return (category, value);
    }

    private static bool IsImportantDateSetStatement(string loweredTranscript)
    {
        return TryExtractImportantDateSet(loweredTranscript) is not null;
    }

    private static bool IsImportantDateRecallQuestion(string loweredTranscript)
    {
        return TryExtractImportantDateLookupLabel(loweredTranscript) is not null;
    }

    private static (string Label, string Value)? TryExtractImportantDateSet(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var mapping = new (string Prefix, string Label)[]
        {
            ("our anniversary is ", "anniversary"),
            ("my anniversary is ", "anniversary"),
            ("our wedding anniversary is ", "anniversary")
        };

        foreach (var (prefix, label) in mapping)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = normalized[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (label, value);
            }
        }

        return null;
    }

    private static string? TryExtractImportantDateLookupLabel(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var candidates = new[]
        {
            "when is our anniversary",
            "when s our anniversary",
            "when's our anniversary",
            "when is my anniversary",
            "what is our anniversary",
            "do you remember our anniversary"
        };

        return candidates.Any(candidate => string.Equals(normalized, candidate, StringComparison.Ordinal))
            ? "anniversary"
            : null;
    }

    private static bool IsAffinitySetStatement(string loweredTranscript)
    {
        return TryExtractAffinitySet(loweredTranscript) is not null;
    }

    private static bool IsAffinitySetAttempt(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return PegasusUserAffinitySetPrefixes.Any(prefix => MatchesPrefixOrStem(normalized, prefix.Prefix));
    }

    private static bool IsAffinityRecallQuestion(string loweredTranscript)
    {
        return TryExtractAffinityLookup(loweredTranscript) is not null;
    }

    private static bool IsAffinityRecallAttempt(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return PegasusUserAffinityLookupPrefixes.Any(prefix => MatchesPrefixOrStem(normalized, prefix.Prefix));
    }

    private static bool MatchesPrefixOrStem(string normalized, string prefix)
    {
        return normalized.StartsWith(prefix, StringComparison.Ordinal) ||
               string.Equals(normalized, prefix.TrimEnd(), StringComparison.Ordinal);
    }

    private static (string Item, PersonalAffinity Affinity)? TryExtractAffinitySet(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);

        foreach (var (prefix, affinity) in PegasusUserAffinitySetPrefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var item = normalized[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(item))
            {
                return (item, affinity);
            }
        }

        return null;
    }

    private static (string Item, PersonalAffinity? ExpectedAffinity)? TryExtractAffinityLookup(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);

        foreach (var (prefix, expectedAffinity) in PegasusUserAffinityLookupPrefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var item = normalized[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(item))
            {
                return (item, expectedAffinity);
            }
        }

        return null;
    }

    private static string DescribeAffinityAsVerb(PersonalAffinity affinity)
    {
        return affinity switch
        {
            PersonalAffinity.Love => "love",
            PersonalAffinity.Like => "like",
            PersonalAffinity.Dislike => "dislike",
            _ => "like"
        };
    }

    private static PersonalMemoryTenantScope ResolveTenantScope(TurnContext turn, string? personId = null)
    {
        var accountId = ReadTenantAttribute(turn, "accountId") ?? "usr_openjibo_owner";
        var loopId = ReadTenantAttribute(turn, "loopId") ?? "openjibo-default-loop";
        var deviceId = turn.DeviceId ?? ReadTenantAttribute(turn, "deviceId") ?? "unknown-device";
        var resolvedPersonId = !string.IsNullOrWhiteSpace(personId)
            ? personId
            : ReadTenantAttribute(turn, "personId") ?? ReadTenantAttribute(turn, "speakerId");
        return new PersonalMemoryTenantScope(accountId, loopId, deviceId, resolvedPersonId);
    }

    private static string? ReadTenantAttribute(TurnContext turn, string key)
    {
        return turn.Attributes.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static string? TryResolveRadioGenre(string loweredTranscript)
    {
        foreach (var (phrase, station) in RadioGenreAliases)
        {
            if (loweredTranscript.Contains(phrase, StringComparison.Ordinal))
            {
                return station;
            }
        }

        return null;
    }

    private static string FormatRadioGenreForSpeech(string station)
    {
        return station switch
        {
            "EightiesAndNinetiesHits" => "eighties and nineties hits",
            "ChristianAndGospel" => "Christian and gospel",
            "ClassicRock" => "classic rock",
            "CollegeRadio" => "college radio",
            "HipHop" => "hip hop",
            "NewsAndTalk" => "news and talk",
            "ReggaeAndIsland" => "reggae and island music",
            "SoftRock" => "soft rock",
            _ => station
        };
    }

    private static ClockTimerValue? TryParseTimerValue(string loweredTranscript, bool allowImplicit = false)
    {
        if (!allowImplicit && !loweredTranscript.Contains("timer", StringComparison.Ordinal))
        {
            return null;
        }

        var hours = ExtractDurationValue(loweredTranscript, "hour");
        var minutes = ExtractDurationValue(loweredTranscript, "minute");
        var seconds = ExtractDurationValue(loweredTranscript, "second");

        if (hours is null && minutes is null && seconds is null)
        {
            return null;
        }

        return new ClockTimerValue(
            (hours ?? 0).ToString(),
            (minutes ?? 0).ToString(),
            seconds is null ? "null" : seconds.Value.ToString());
    }

    private static ClockAlarmValue? TryParseAlarmValue(
        string loweredTranscript,
        bool allowImplicit = false,
        DateTimeOffset? referenceLocalTime = null)
    {
        if (!allowImplicit && !loweredTranscript.Contains("alarm", StringComparison.Ordinal))
        {
            return null;
        }

        var compactMatch = CompactAlarmPattern.Match(loweredTranscript);
        if (compactMatch.Success)
        {
            var compact = compactMatch.Groups["compact"].Value;
            if (int.TryParse(compact, out var compactValue))
            {
                var compactHour = compact.Length switch
                {
                    3 or 4 => compactValue / 100,
                    _ => -1
                };
                var compactMinute = compact.Length switch
                {
                    3 or 4 => compactValue % 100,
                    _ => -1
                };
                if (compactHour is >= 1 and <= 12 && compactMinute is >= 0 and <= 59)
                {
                    var compactAmPm = ResolveAmPm(compactMatch.Groups["ampm"].Value, compactHour, compactMinute, referenceLocalTime);
                    return new ClockAlarmValue($"{compactHour}:{compactMinute:00}", compactAmPm);
                }
            }
        }

        var match = SplitAlarmPattern.Match(loweredTranscript);
        if (!match.Success)
        {
            return null;
        }

        var hourToken = match.Groups["hour"].Value;
        var minuteToken = match.Groups["minute"].Success ? match.Groups["minute"].Value : "00";
        var hour = ParseNumberToken(hourToken);
        if (hour is null or < 1 or > 12)
        {
            return null;
        }

        var minute = ParseNumberToken(minuteToken);
        if (minute is null or < 0 or > 59)
        {
            return null;
        }

        var ampm = ResolveAmPm(match.Groups["ampm"].Value, hour.Value, minute.Value, referenceLocalTime);
        return new ClockAlarmValue($"{hour}:{minute:00}", ampm);
    }

    private static string ResolveAmPm(string token, int hour, int minute, DateTimeOffset? referenceLocalTime)
    {
        var normalized = token.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith("p", StringComparison.OrdinalIgnoreCase))
        {
            return "pm";
        }

        if (normalized.StartsWith("a", StringComparison.OrdinalIgnoreCase))
        {
            return "am";
        }

        return referenceLocalTime.HasValue
            ? ResolveNextOccurrenceAmPm(hour, minute, referenceLocalTime.Value)
            : "am";
    }

    private static string ResolveNextOccurrenceAmPm(int hour, int minute, DateTimeOffset referenceLocalTime)
    {
        var amCandidate = BuildAlarmCandidate(referenceLocalTime, hour, minute, isPm: false);
        var pmCandidate = BuildAlarmCandidate(referenceLocalTime, hour, minute, isPm: true);
        return amCandidate <= pmCandidate ? "am" : "pm";
    }

    private static DateTimeOffset BuildAlarmCandidate(DateTimeOffset referenceLocalTime, int hour, int minute, bool isPm)
    {
        var hour24 = hour % 12;
        if (isPm)
        {
            hour24 += 12;
        }

        var candidate = new DateTimeOffset(
            referenceLocalTime.Year,
            referenceLocalTime.Month,
            referenceLocalTime.Day,
            hour24,
            minute,
            0,
            referenceLocalTime.Offset);

        if (candidate <= referenceLocalTime)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    private static bool HasStructuredTimerValue(IReadOnlyDictionary<string, string> clientEntities)
    {
        return clientEntities.ContainsKey("hours") ||
               clientEntities.ContainsKey("minutes") ||
               clientEntities.ContainsKey("seconds");
    }

    private static bool HasStructuredAlarmValue(IReadOnlyDictionary<string, string> clientEntities)
    {
        return clientEntities.TryGetValue("time", out var time) &&
               !string.IsNullOrWhiteSpace(time);
    }

    private static ClockTimerValue? TryReadStructuredTimerValue(IReadOnlyDictionary<string, string> clientEntities)
    {
        if (!HasStructuredTimerValue(clientEntities))
        {
            return null;
        }

        clientEntities.TryGetValue("hours", out var hours);
        clientEntities.TryGetValue("minutes", out var minutes);
        clientEntities.TryGetValue("seconds", out var seconds);
        return new ClockTimerValue(
            string.IsNullOrWhiteSpace(hours) ? "0" : hours,
            string.IsNullOrWhiteSpace(minutes) ? "0" : minutes,
            string.IsNullOrWhiteSpace(seconds) ? "null" : seconds);
    }

    private static ClockAlarmValue? TryReadStructuredAlarmValue(IReadOnlyDictionary<string, string> clientEntities)
    {
        if (!clientEntities.TryGetValue("time", out var time) || string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        clientEntities.TryGetValue("ampm", out var ampm);
        return new ClockAlarmValue(time, string.IsNullOrWhiteSpace(ampm) ? "am" : ampm.ToLowerInvariant());
    }

    private static string? ResolveClockDomain(
        IReadOnlyDictionary<string, string> clientEntities,
        IReadOnlyList<string> clientRules,
        IReadOnlyList<string> listenRules,
        string? lastClockDomain)
    {
        if (clientEntities.TryGetValue("domain", out var clientDomain) &&
            !string.IsNullOrWhiteSpace(clientDomain))
        {
            return clientDomain;
        }

        if (!string.IsNullOrWhiteSpace(lastClockDomain))
        {
            return lastClockDomain;
        }

        var combinedRules = clientRules.Concat(listenRules).ToArray();
        if (combinedRules.Any(rule =>
                rule.Contains("timer", StringComparison.OrdinalIgnoreCase) &&
                !rule.Contains("alarm_timer_query_menu", StringComparison.OrdinalIgnoreCase)))
        {
            return "timer";
        }

        return combinedRules.Any(rule =>
            rule.Contains("alarm", StringComparison.OrdinalIgnoreCase) &&
            !rule.Contains("alarm_timer_query_menu", StringComparison.OrdinalIgnoreCase)) ? "alarm" : null;
    }

    private static bool IsTimerRequest(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "set a timer",
            "set timer",
            "start a timer",
            "start timer",
            "timer for");
    }

    private static bool IsAlarmRequest(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "set an alarm",
            "set alarm",
            "wake me up",
            "alarm for");
    }

    private static bool IsCancelRequest(string? clientIntent, string loweredTranscript)
    {
        var normalizedTranscript = NormalizeCommandPhrase(loweredTranscript);
        return string.Equals(clientIntent, "cancel", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(clientIntent, "stop", StringComparison.OrdinalIgnoreCase) ||
               normalizedTranscript is "cancel" or "stop" or "never mind" or "nevermind";
    }

    private static bool IsGlobalStopRequest(
        string loweredTranscript,
        string? clientIntent,
        IReadOnlyDictionary<string, string> clientEntities)
    {
        if (string.Equals(clientIntent, "stop", StringComparison.OrdinalIgnoreCase) &&
            IsGlobalCommandsDomain(clientEntities))
        {
            return true;
        }

        var normalizedTranscript = NormalizeCommandPhrase(loweredTranscript);
        return normalizedTranscript is "stop" or "stop it" or "stop that" or "stop talking" or "be quiet" or "never mind" or "nevermind" or "forget it" ||
               MatchesAny(normalizedTranscript, "that s enough", "that will do", "that ll do", "cut it out", "cut that out");
    }

    private static bool IsVolumeQueryRequest(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "volume controls",
            "volume control",
            "volume menu",
            "volume level",
            "show volume",
            "show the volume",
            "open volume",
            "open the volume",
            "what is your volume",
            "what's your volume",
            "how is your volume",
            "how s your volume");
    }

    private static bool IsAlarmDeleteRequest(string loweredTranscript)
    {
        var normalizedTranscript = NormalizeCommandPhrase(loweredTranscript);
        return AlarmDeletePattern.IsMatch(normalizedTranscript);
    }

    private static bool IsVolumeUpRequest(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "turn it up",
            "turn this up",
            "turn that up",
            "turn up the volume",
            "turn the volume up",
            "turn volume up",
            "turn your volume up",
            "increase the volume",
            "increase your volume",
            "raise the volume",
            "raise your volume",
            "make it louder",
            "make that louder",
            "speak louder",
            "talk louder",
            "be louder",
            "louder");
    }

    private static bool IsVolumeDownRequest(string loweredTranscript)
    {
        return MatchesAny(
            loweredTranscript,
            "turn it down",
            "turn this down",
            "turn that down",
            "turn down the volume",
            "turn the volume down",
            "turn volume down",
            "turn your volume down",
            "decrease the volume",
            "decrease your volume",
            "lower the volume",
            "lower your volume",
            "make it quieter",
            "make that quieter",
            "make it softer",
            "speak quieter",
            "talk quieter",
            "be quieter",
            "quieter",
            "softer");
    }

    private static string? ResolveVolumeLevel(string loweredTranscript, IReadOnlyDictionary<string, string> clientEntities)
    {
        if (clientEntities.TryGetValue("volumeLevel", out var entityValue) &&
            TryNormalizeVolumeLevel(entityValue) is { } structuredLevel)
        {
            return structuredLevel;
        }

        return TryResolveVolumeLevel(loweredTranscript);
    }

    private static string? TryResolveVolumeLevel(string loweredTranscript)
    {
        if (!loweredTranscript.Contains("volume", StringComparison.Ordinal) &&
            !loweredTranscript.Contains("loudness", StringComparison.Ordinal))
        {
            return null;
        }

        if (MatchesAny(loweredTranscript, "max volume", "maximum volume", "volume max", "volume maximum"))
        {
            return "10";
        }

        if (MatchesAny(loweredTranscript, "min volume", "minimum volume", "volume min", "volume minimum"))
        {
            return "1";
        }

        var normalizedTranscript = NormalizeCommandPhrase(loweredTranscript);
        var homophoneMatch = VolumeToValueHomophonePattern.Match(normalizedTranscript);
        if (homophoneMatch.Success &&
            TryNormalizeVolumeLevel(homophoneMatch.Groups["value"].Value) is { } homophoneLevel)
        {
            return homophoneLevel;
        }

        var match = VolumeLevelPattern.Match(normalizedTranscript);
        return !match.Success ? null : TryNormalizeVolumeLevel(match.Groups["value"].Value);
    }

    private static string NormalizeCommandPhrase(string value)
    {
        return CommandWhitespacePattern.Replace(
                CommandPhrasePattern.Replace(value.Trim().ToLowerInvariant(), " "),
                " ")
            .Trim();
    }

    private static string? TryNormalizeVolumeLevel(string token)
    {
        if (string.Equals(token, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "null";
        }

        var parsed = ParseNumberToken(token);
        return parsed is >= 1 and <= 10
            ? parsed.Value.ToString()
            : null;
    }

    private static bool IsGlobalCommandsDomain(IReadOnlyDictionary<string, string> clientEntities)
    {
        return clientEntities.TryGetValue("domain", out var domain) &&
               string.Equals(domain, "global_commands", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClockTimerValueTurn(
        IReadOnlyList<string> clientRules,
        IReadOnlyList<string> listenRules)
    {
        return clientRules.Concat(listenRules).Any(static rule =>
            rule.Contains("clock/", StringComparison.OrdinalIgnoreCase) &&
            rule.Contains("timer", StringComparison.OrdinalIgnoreCase) &&
            rule.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsClockAlarmValueTurn(
        IReadOnlyList<string> clientRules,
        IReadOnlyList<string> listenRules)
    {
        return clientRules.Concat(listenRules).Any(static rule =>
            rule.Contains("clock/", StringComparison.OrdinalIgnoreCase) &&
            rule.Contains("alarm", StringComparison.OrdinalIgnoreCase) &&
            rule.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    private static int? ExtractDurationValue(string loweredTranscript, string unitStem)
    {
        var pattern = new Regex($@"\b(?<value>\d+|[a-z\-]+(?:\s+[a-z\-]+)?)\s+{unitStem}s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = pattern.Match(loweredTranscript);
        if (!match.Success)
        {
            return null;
        }

        var valueToken = match.Groups["value"].Value.Trim();
        var parsed = ParseNumberToken(valueToken);
        if (parsed is not null)
        {
            return parsed;
        }

        var parts = valueToken.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return parts.Length > 0
                ? ParseNumberToken(parts[^1])
                : null;

        parsed = ParseNumberToken(string.Join(' ', parts.TakeLast(2)));
        if (parsed is not null)
        {
            return parsed;
        }

        return parts.Length > 0
            ? ParseNumberToken(parts[^1])
            : null;
    }

    private static int? ParseNumberToken(string token)
    {
        var normalized = token.Trim().ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);
        if (int.TryParse(normalized, out var numeric))
        {
            return numeric;
        }

        if (!normalized.Contains(' '))
        {
            return normalized switch
            {
                "a" or "an" => 1,
                "one" => 1,
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                "six" => 6,
                "seven" => 7,
                "eight" => 8,
                "nine" => 9,
                "ten" => 10,
                "eleven" => 11,
                "twelve" => 12,
                "thirteen" => 13,
                "fourteen" => 14,
                "fifteen" => 15,
                "sixteen" => 16,
                "seventeen" => 17,
                "eighteen" => 18,
                "nineteen" => 19,
                "twenty" => 20,
                "thirty" => 30,
                "forty" => 40,
                "fifty" => 50,
                _ => null
            };
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return normalized switch
            {
                "a" or "an" => 1,
                "one" => 1,
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                "six" => 6,
                "seven" => 7,
                "eight" => 8,
                "nine" => 9,
                "ten" => 10,
                "eleven" => 11,
                "twelve" => 12,
                "thirteen" => 13,
                "fourteen" => 14,
                "fifteen" => 15,
                "sixteen" => 16,
                "seventeen" => 17,
                "eighteen" => 18,
                "nineteen" => 19,
                "twenty" => 20,
                "thirty" => 30,
                "forty" => 40,
                "fifty" => 50,
                _ => null
            };
        }

        var first = ParseNumberToken(parts[0]);
        var second = ParseNumberToken(parts[1]);
        if (first is >= 20 && second is >= 0 and < 10)
        {
            return first + second;
        }

        return normalized switch
        {
            "a" or "an" => 1,
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            "thirteen" => 13,
            "fourteen" => 14,
            "fifteen" => 15,
            "sixteen" => 16,
            "seventeen" => 17,
            "eighteen" => 18,
            "nineteen" => 19,
            "twenty" => 20,
            "thirty" => 30,
            "forty" => 40,
            "fifty" => 50,
            _ => null
        };
    }

    private sealed record ClockTimerValue(string Hours, string Minutes, string Seconds);

    private sealed record ClockAlarmValue(string Time, string AmPm);

    private sealed record PizzaMimPrompt(string PromptId, string Esml);

    private sealed record ProactivityCandidate(string IntentName, int Weight);

    private sealed record PizzaSignal(PersonalAffinity? Affinity);

    private sealed record GreetingPresenceProfile(
        string? PrimaryPersonId,
        string? SpeakerId,
        IReadOnlyList<string> PeoplePresentIds,
        IReadOnlyDictionary<string, string> LoopUserFirstNames)
    {
        public static GreetingPresenceProfile Empty { get; } = new(
            null,
            null,
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public bool HasKnownIdentity => !string.IsNullOrWhiteSpace(PrimaryPersonId);
    }

    private sealed record WeatherDateEntity(string? DateEntity, int ForecastDayOffset, string? ForecastLeadIn)
    {
        public static WeatherDateEntity None { get; } = new(null, 0, null);
    }

    private enum YesNoReply
    {
        None = 0,
        Affirmative = 1,
        Negative = 2
    }

    private static readonly Regex SplitAlarmPattern = new(
        @"\b(?<hour>\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?:[:\s,-]+(?<minute>\d{2}|[a-z\-]+(?:\s+[a-z\-]+)?))?\s*(?<ampm>a[\s\.]*m\.?|p[\s\.]*m\.?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CompactAlarmPattern = new(
        @"\b(?<compact>\d{3,4})\s*(?<ampm>a[\s\.]*m\.?|p[\s\.]*m\.?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VolumeLevelPattern = new(
        @"\b(?:volume|loudness)\s*(?:to|at|level|is)?\s*(?<value>10|\d|one|two|three|four|five|six|seven|eight|nine|ten)\b|\b(?:set|change|make|turn)\s+(?:the\s+|your\s+)?(?:volume|loudness)\s*(?:to|at)?\s*(?<value>10|\d|one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VolumeToValueHomophonePattern = new(
        @"\b(?:volume|loudness)\s+(?:2|two|to)\s+(?<value>10|\d|one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CommandPhrasePattern = new(
        @"[^\w\s]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CommandWhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AlarmDeletePattern = new(
        @"\b(?:cancel|delete|remove|stop|turn\s+off)\s+(?:the\s+)?(?:alarm|along|elo)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherLocationPattern = new(
        @"\b(?:in|for|at)\s+(?<location>[a-z][a-z\s'\-]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherLocationSuffixPattern = new(
        @"\b(?:today|tonight|tomorrow|day after tomorrow|outside|right now|please|thanks|this weekend|next weekend|the weekend|weekend|this week|next week|on monday|on tuesday|on wednesday|on thursday|on friday|on saturday|on sunday|this monday|this tuesday|this wednesday|this thursday|this friday|this saturday|this sunday|next monday|next tuesday|next wednesday|next thursday|next friday|next saturday|next sunday|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherConditionForecastPattern = new(
        @"\bwill it be\s+(sunny|cloudy|windy|foggy|stormy|rainy|snowy|hail|hailing)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherTopicLocationPattern = new(
        @"\b(?:weather|forecast|temperature|humidity)\b.*\b(?:in|for|at)\s+[a-z]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherDayOfWeekPattern = new(
        @"\b(?<next>next\s+)?(?<this>this\s+)?(?:on\s+)?(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly PizzaMimPrompt[] PizzaMimPrompts =
    [
        new("RA_JBO_ShowPizzaMaking_AN_01", "<speak><anim cat='jiboji' filter='pizza-making'/></speak>"),
        new("RA_JBO_ShowPizzaMaking_AN_02", "<speak><anim cat='jiboji' filter='pizza-making' nonBlocking='true'/><pitch mult='1.2'>One </pitch> pizza, coming right up.</speak>"),
        new("RA_JBO_ShowPizzaMaking_AN_03", "<speak><anim cat='jiboji' filter='pizza-making' nonBlocking='true'/>My <pitch mult='1.2'>specialty </pitch>.</speak>")
    ];

    private static readonly string[] PreferenceSetMarkers =
    [
        "my favorite ",
        "my favourite "
    ];

    private static readonly string[] PreferenceReverseMarkers =
    [
        " is my favorite ",
        " is my favourite ",
        " are my favorite ",
        " are my favourite "
    ];

    private static readonly string[] WeatherDateEntityKeys =
    [
        "date",
        "sys.date",
        "datetime",
        "dateTime",
        "date_time",
        "day"
    ];

    private static readonly string[] YesNoAcknowledgementPrefixes =
    [
        "uh",
        "um",
        "hmm",
        "well",
        "so",
        "actually",
        "honestly",
        "thanks",
        "thank you"
    ];

    private static readonly HashSet<string> YesNoAffirmativeLeadTokens = new(StringComparer.Ordinal)
    {
        "yes",
        "yeah",
        "yep",
        "yup",
        "sure",
        "ok",
        "okay",
        "absolutely",
        "affirmative",
        "definitely",
        "certainly",
        "indeed"
    };

    private static readonly HashSet<string> YesNoNegativeLeadTokens = new(StringComparer.Ordinal)
    {
        "no",
        "nope",
        "nah",
        "negative",
        "never"
    };

    private static readonly HashSet<string> YesNoAffirmativeLeadPhrases = new(StringComparer.Ordinal)
    {
        "uh huh",
        "sounds good",
        "sure thing",
        "why not",
        "please do",
        "go ahead",
        "of course",
        "i guess so",
        "i think so"
    };

    private static readonly HashSet<string> YesNoNegativeLeadPhrases = new(StringComparer.Ordinal)
    {
        "not now",
        "not today",
        "not really",
        "no thanks",
        "no thank you",
        "maybe later",
        "i guess not",
        "i do not",
        "i dont",
        "i don t"
    };

    // Directly imported from Pegasus parser intent phrase families:
    // userLikesThing / userDislikesThing / doesUserLikeThing / doesUserDislikeThing.
    private static readonly (string Prefix, PersonalAffinity Affinity)[] PegasusUserAffinitySetPrefixes =
    [
        ("i love ", PersonalAffinity.Love),
        ("i like ", PersonalAffinity.Like),
        ("i like the ", PersonalAffinity.Like),
        ("i enjoy ", PersonalAffinity.Like),
        ("i do like ", PersonalAffinity.Like),
        ("we love ", PersonalAffinity.Love),
        ("we like ", PersonalAffinity.Like),
        ("we enjoy ", PersonalAffinity.Like),
        ("i dislike ", PersonalAffinity.Dislike),
        ("i hate ", PersonalAffinity.Dislike),
        ("i hate the ", PersonalAffinity.Dislike),
        ("i loathe ", PersonalAffinity.Dislike),
        ("i don t like ", PersonalAffinity.Dislike),
        ("i dont like ", PersonalAffinity.Dislike),
        ("i not like ", PersonalAffinity.Dislike),
        ("i do not like ", PersonalAffinity.Dislike),
        ("i did not like ", PersonalAffinity.Dislike),
        ("i did not like the ", PersonalAffinity.Dislike),
        ("i didn t like ", PersonalAffinity.Dislike),
        ("i didnt like ", PersonalAffinity.Dislike),
        ("i didn t like the ", PersonalAffinity.Dislike),
        ("i didnt like the ", PersonalAffinity.Dislike),
        ("i didn t really like ", PersonalAffinity.Dislike),
        ("i didnt really like ", PersonalAffinity.Dislike),
        ("i don t really like ", PersonalAffinity.Dislike),
        ("i dont really like ", PersonalAffinity.Dislike),
        ("i don t enjoy ", PersonalAffinity.Dislike),
        ("i dont enjoy ", PersonalAffinity.Dislike),
        ("i do not enjoy ", PersonalAffinity.Dislike),
        ("i did not enjoy ", PersonalAffinity.Dislike),
        ("i didn t enjoy ", PersonalAffinity.Dislike),
        ("i didnt enjoy ", PersonalAffinity.Dislike),
        ("i didn t really enjoy ", PersonalAffinity.Dislike),
        ("i didnt really enjoy ", PersonalAffinity.Dislike),
        ("i don t love ", PersonalAffinity.Dislike),
        ("i dont love ", PersonalAffinity.Dislike),
        ("i do not love ", PersonalAffinity.Dislike),
        ("i don t love to ", PersonalAffinity.Dislike),
        ("i dont love to ", PersonalAffinity.Dislike),
        ("i do not love to ", PersonalAffinity.Dislike),
        ("i can t stand ", PersonalAffinity.Dislike),
        ("i cant stand ", PersonalAffinity.Dislike),
        ("i can t stand the ", PersonalAffinity.Dislike),
        ("i cant stand the ", PersonalAffinity.Dislike),
        ("we dislike ", PersonalAffinity.Dislike),
        ("we hate ", PersonalAffinity.Dislike),
        ("we despise ", PersonalAffinity.Dislike),
        ("we detest ", PersonalAffinity.Dislike),
        ("we loathe ", PersonalAffinity.Dislike),
        ("we can t stand ", PersonalAffinity.Dislike),
        ("we cant stand ", PersonalAffinity.Dislike),
        ("i despise ", PersonalAffinity.Dislike),
        ("i detest ", PersonalAffinity.Dislike)
    ];

    private static readonly (string Prefix, PersonalAffinity? ExpectedAffinity)[] PegasusUserAffinityLookupPrefixes =
    [
        ("do i love ", PersonalAffinity.Love),
        ("do i like ", PersonalAffinity.Like),
        ("do i enjoy ", PersonalAffinity.Like),
        ("do i dislike ", PersonalAffinity.Dislike),
        ("do i hate ", PersonalAffinity.Dislike),
        ("do i loathe ", PersonalAffinity.Dislike),
        ("do i not like ", PersonalAffinity.Dislike),
        ("do i despise ", PersonalAffinity.Dislike),
        ("do i detest ", PersonalAffinity.Dislike),
        ("do you think i like ", PersonalAffinity.Like),
        ("do you believe i like ", PersonalAffinity.Like),
        ("do you think i don t like ", PersonalAffinity.Dislike),
        ("do you believe i don t like ", PersonalAffinity.Dislike),
        ("how do i feel about ", null),
        ("what do i think about ", null)
    ];

    private static readonly string[] PizzaPreferenceCategories =
    [
        "food",
        "meal",
        "dish",
        "dinner",
        "lunch",
        "snack"
    ];

    private static readonly HashSet<string> GenericWeatherLocationTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "my area",
        "our area",
        "this area",
        "the area",
        "the city",
        "this city",
        "our city",
        "my city",
        "the town",
        "this town",
        "our town",
        "my town",
        "our street",
        "this street",
        "my street",
        "the neighborhood",
        "the neighbourhood",
        "this neighborhood",
        "this neighbourhood",
        "our neighborhood",
        "our neighbourhood"
    };

    private static readonly HashSet<string> SpokenAbbreviationTokens = new(StringComparer.Ordinal)
    {
        "US",
        "USA",
        "UK",
        "UAE",
        "EU",
        "DC",
        "AL",
        "AK",
        "AZ",
        "AR",
        "CA",
        "CO",
        "CT",
        "DE",
        "FL",
        "GA",
        "HI",
        "ID",
        "IL",
        "IN",
        "IA",
        "KS",
        "KY",
        "LA",
        "ME",
        "MD",
        "MA",
        "MI",
        "MN",
        "MS",
        "MO",
        "MT",
        "NE",
        "NV",
        "NH",
        "NJ",
        "NM",
        "NY",
        "NC",
        "ND",
        "OH",
        "OK",
        "OR",
        "PA",
        "RI",
        "SC",
        "SD",
        "TN",
        "TX",
        "UT",
        "VT",
        "VA",
        "WA",
        "WV",
        "WI",
        "WY"
    };

    private sealed record WeatherForecastCardSegment(
        string DayName,
        string Summary,
        int High,
        int Low,
        string Icon,
        string Unit,
        string Theme,
        string SpokenLine);

    private const string GreetingRouteMetadataKey = "greetingsRoute";
    private const string GreetingSpeakerMetadataKey = "greetingsSpeaker";
    private const string LastProactiveGreetingUtcMetadataKey = "greetingsLastProactiveUtc";
    private const string LastReactiveGreetingUtcMetadataKey = "greetingsLastReactiveUtc";
    private static readonly TimeSpan ProactiveGreetingCooldown = TimeSpan.FromMinutes(20);

    private const int MaxWeatherForecastDayOffset = 5;
    private const int MaxNewsHeadlines = 3;
    private const int MaxPreferredNewsCategories = 2;

    private static readonly (string Keyword, string Category)[] NewsCategoryKeywordMap =
    [
        ("sports", "sports"),
        ("sport", "sports"),
        ("football", "sports"),
        ("baseball", "sports"),
        ("basketball", "sports"),
        ("hockey", "sports"),
        ("technology", "technology"),
        ("tech", "technology"),
        ("ai", "technology"),
        ("a i", "technology"),
        ("a eye", "technology"),
        ("aye eye", "technology"),
        ("artificial intelligence", "technology"),
        ("science", "science"),
        ("business", "business"),
        ("finance", "business"),
        ("market", "business"),
        ("stock", "business"),
        ("politics", "general"),
        ("political", "general"),
        ("world", "general"),
        ("entertainment", "entertainment"),
        ("movie", "entertainment"),
        ("music", "entertainment")
    ];

    private static readonly (string Phrase, string Station)[] RadioGenreAliases =
    [
        ("country music", "Country"),
        ("country radio", "Country"),
        ("country", "Country"),
        ("football", "Sports"),
        ("sports", "Sports"),
        ("classic rock", "ClassicRock"),
        ("soft rock", "SoftRock"),
        ("hip hop", "HipHop"),
        ("hip-hop", "HipHop"),
        ("news and talk", "NewsAndTalk"),
        ("news talk", "NewsAndTalk"),
        ("news radio", "NewsAndTalk"),
        ("sports radio", "Sports"),
        ("christian music", "ChristianAndGospel"),
        ("gospel music", "ChristianAndGospel"),
        ("oldies", "Oldies"),
        ("pop music", "Pop"),
        ("jazz", "Jazz"),
        ("latin music", "Latin"),
        ("dance music", "Dance"),
        ("reggae", "ReggaeAndIsland"),
        ("island music", "ReggaeAndIsland"),
        ("alternative", "Alternative"),
        ("blues", "Blues"),
        ("classical music", "Classical"),
        ("classical", "Classical"),
        ("college radio", "CollegeRadio"),
        ("comedy radio", "Comedy"),
        ("npr", "NPR")
    ];
}

public sealed record JiboInteractionDecision(
    string IntentName,
    string ReplyText,
    string? SkillName = null,
    IDictionary<string, object?>? SkillPayload = null,
    IDictionary<string, object?>? ContextUpdates = null);
