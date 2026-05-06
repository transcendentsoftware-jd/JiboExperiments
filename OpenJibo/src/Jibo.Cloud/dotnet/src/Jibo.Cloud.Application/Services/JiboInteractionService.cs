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
    IWeatherReportProvider? weatherReportProvider = null)
{
    public async Task<JiboInteractionDecision> BuildDecisionAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var catalog = await contentCache.GetCatalogAsync(cancellationToken);
        var transcript = (turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty).Trim();
        var lowered = transcript.ToLowerInvariant();
        var referenceLocalTime = TryResolveReferenceLocalTime(turn);
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
        var isYesNoTurn = IsYesNoTurn(turn);

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
            "hello" => new JiboInteractionDecision("hello", randomizer.Choose(catalog.GreetingReplies)),
            "how_are_you" => new JiboInteractionDecision("how_are_you", randomizer.Choose(catalog.HowAreYouReplies)),
            "robot_age" => BuildRobotAgeDecision(referenceLocalTime),
            "robot_birthday" => BuildRobotBirthdayDecision(),
            "robot_personality" => new JiboInteractionDecision("robot_personality", randomizer.Choose(catalog.PersonalityReplies)),
            "memory_set_name" => BuildRememberNameDecision(turn, transcript),
            "memory_get_name" => BuildRecallNameDecision(turn),
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
            "news" => BuildNewsDecision(catalog),
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

    private JiboInteractionDecision BuildRecallNameDecision(TurnContext turn)
    {
        var name = personalMemoryStore.GetName(ResolveTenantScope(turn));
        return string.IsNullOrWhiteSpace(name)
            ? new JiboInteractionDecision(
                "memory_get_name",
                "I do not know your name yet. You can say, my name is Alex.")
            : new JiboInteractionDecision(
                "memory_get_name",
                $"You told me your name is {name}.");
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
            "Do you want to hear a fun pizza fact?");
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
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["skillId"] = "report-skill",
            ["localIntent"] = "requestWeatherPR",
            ["cloudSkill"] = "weather"
        };
        var dateEntity = TryResolveWeatherDateEntity(transcript);
        if (dateEntity is not null)
        {
            payload["date"] = dateEntity;
        }

        var weatherConditionEntity = TryResolveWeatherConditionEntity(transcript);
        if (weatherConditionEntity is not null)
        {
            payload["weatherCondition"] = weatherConditionEntity;
        }

        var replyText = "Checking your weather report.";
        if (weatherReportProvider is null)
        {
            return new JiboInteractionDecision(
                "weather",
                replyText,
                "report-skill",
                payload);
        }

        var locationQuery = TryResolveWeatherLocationQuery(transcript);
        if (!string.IsNullOrWhiteSpace(locationQuery))
        {
            payload["locationQuery"] = locationQuery;
        }

        var weatherCoordinates = TryResolveWeatherCoordinates(turn);
        if (weatherCoordinates is not null)
        {
            payload["latitude"] = weatherCoordinates.Value.Latitude;
            payload["longitude"] = weatherCoordinates.Value.Longitude;
        }

        var useCelsius = ShouldUseCelsius(turn, transcript);
        var snapshot = await weatherReportProvider.GetReportAsync(
            new WeatherReportRequest(
                locationQuery,
                weatherCoordinates?.Latitude,
                weatherCoordinates?.Longitude,
                string.Equals(dateEntity, "tomorrow", StringComparison.OrdinalIgnoreCase),
                useCelsius),
            cancellationToken);

        if (snapshot is not null)
        {
            payload["provider"] = "openweather";
            payload["temperature"] = snapshot.Temperature;
            if (snapshot.HighTemperature is not null)
            {
                payload["highTemperature"] = snapshot.HighTemperature.Value;
            }

            if (snapshot.LowTemperature is not null)
            {
                payload["lowTemperature"] = snapshot.LowTemperature.Value;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Condition))
            {
                payload["weatherCondition"] = snapshot.Condition;
            }

            replyText = BuildWeatherSpokenReply(snapshot, dateEntity);
        }

        return new JiboInteractionDecision(
            "weather",
            replyText,
            "report-skill",
            payload);
    }

    private static string BuildWeatherSpokenReply(
        WeatherReportSnapshot snapshot,
        string? dateEntity)
    {
        var unit = snapshot.UseCelsius ? "Celsius" : "Fahrenheit";
        var summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? "partly cloudy"
            : snapshot.Summary.Trim().TrimEnd('.');
        var location = string.IsNullOrWhiteSpace(snapshot.LocationName)
            ? "your area"
            : snapshot.LocationName;

        if (string.Equals(dateEntity, "tomorrow", StringComparison.OrdinalIgnoreCase))
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
            return $"Tomorrow in {location}, expect {summary}{tempRange}.";
        }

        return $"Right now in {location}, it is {summary} and {snapshot.Temperature} degrees {unit}.";
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

    private JiboInteractionDecision BuildNewsDecision(JiboExperienceCatalog catalog)
    {
        var briefing = randomizer.Choose(catalog.NewsBriefings);
        return new JiboInteractionDecision(
            "news",
            briefing,
            "news",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["skillId"] = "news",
                ["cloudSkill"] = "news",
                ["mim_id"] = "runtime-news",
                ["mim_type"] = "announcement"
            });
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

        if (IsNameSetStatement(loweredTranscript))
        {
            return "memory_set_name";
        }

        if (IsNameRecallQuestion(loweredTranscript))
        {
            return "memory_get_name";
        }

        if (IsUserBirthdaySetStatement(loweredTranscript))
        {
            return "memory_set_birthday";
        }

        if (IsUserBirthdayRecallQuestion(loweredTranscript))
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

        if (IsPreferenceSetStatement(loweredTranscript))
        {
            return "memory_set_preference";
        }

        if (IsPreferenceRecallQuestion(loweredTranscript))
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

        if (IsAffinitySetStatement(loweredTranscript))
        {
            return "memory_set_affinity";
        }

        if (IsAffinityRecallQuestion(loweredTranscript))
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
                "can you cook us a pizza",
                "flip a pizza",
                "make a pizza",
                "make pizza",
                "show pizza",
                "can you make pizza",
                "let's make pizza",
                "lets make pizza"))
        {
            return "pizza";
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
                "please order pizza"))
        {
            return "order_pizza";
        }

        if (MatchesAny(loweredTranscript, "personal report", "my report", "daily report", "my update"))
        {
            return "personal_report";
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

        if (MatchesAny(loweredTranscript, "how are you", "what's up", "what s up", "what up"))
        {
            return "how_are_you";
        }

        if (MatchesAny(loweredTranscript, "hello", "hi", "hey"))
        {
            return "hello";
        }

        switch (isYesNoTurn)
        {
            case true when IsAffirmativeReply(loweredTranscript):
                return "yes";
            case true when IsNegativeReply(loweredTranscript):
                return "no";
        }

        if (MatchesAny(loweredTranscript, "what time is it", "current time", "the time", "time is it") ||
            loweredTranscript.Contains("time", StringComparison.Ordinal))
        {
            return "time";
        }

        if (MatchesAny(loweredTranscript, "what day is it", "what day is today"))
        {
            return "day";
        }

        if (MatchesAny(loweredTranscript, "what day is it", "what is the date", "today s date", "today's date") ||
            loweredTranscript.Contains("date", StringComparison.Ordinal) ||
            loweredTranscript.Contains("day", StringComparison.Ordinal))
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
            .Any(static rule =>
                string.Equals(rule, "$YESNO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase));
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
        return normalized is "yes" or "yeah" or "yep" or "yup" or "sure" or "ok" or "okay" or "absolutely" or "please do" or "why not" ||
               MatchesAny(normalized, "uh huh", "sounds good");
    }

    private static bool IsNegativeReply(string loweredTranscript)
    {
        var normalized = NormalizeCommandPhrase(loweredTranscript);
        return normalized is "no" or "nope" or "nah" or "not now" or "no thanks" or "not today" ||
               MatchesAny(normalized, "no thank you", "maybe later");
    }

    private static bool IsWeatherRequest(string loweredTranscript)
    {
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
                "what is the humidity"))
        {
            return true;
        }

        return MatchesAny(
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
            "do you think it will snow");
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

    private static string? TryResolveWeatherDateEntity(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        if (MatchesAny(normalized, "tomorrow", "tomorrow s", "tomorrow's"))
        {
            return "tomorrow";
        }

        return null;
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
        return MatchesAny(
            loweredTranscript,
            "when is your birthday",
            "when's your birthday",
            "what's your birthday",
            "what s your birthday",
            "what is your birthday",
            "when were you born",
            "what day is your birthday");
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
            "do you remember my name");
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
            "do you remember my birthday");
    }

    private static bool IsUserBirthdaySetStatement(string loweredTranscript)
    {
        return TryExtractBirthdayFact(loweredTranscript) is not null;
    }

    private static string? TryExtractBirthdayFact(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);
        var marker = "my birthday is ";
        var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var value = normalized[(markerIndex + marker.Length)..].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsPreferenceRecallQuestion(string loweredTranscript)
    {
        return TryExtractPreferenceLookupCategory(loweredTranscript) is not null;
    }

    private static bool IsPreferenceSetStatement(string loweredTranscript)
    {
        return TryExtractPreferenceSet(loweredTranscript) is not null;
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

    private static bool IsAffinityRecallQuestion(string loweredTranscript)
    {
        return TryExtractAffinityLookup(loweredTranscript) is not null;
    }

    private static (string Item, PersonalAffinity Affinity)? TryExtractAffinitySet(string transcript)
    {
        var normalized = NormalizeCommandPhrase(transcript);

        var directMappings = new (string Prefix, PersonalAffinity Affinity)[]
        {
            ("i love ", PersonalAffinity.Love),
            ("i like ", PersonalAffinity.Like),
            ("i dislike ", PersonalAffinity.Dislike),
            ("i hate ", PersonalAffinity.Dislike),
            ("i don t like ", PersonalAffinity.Dislike),
            ("i dont like ", PersonalAffinity.Dislike),
            ("i do not like ", PersonalAffinity.Dislike)
        };

        foreach (var (prefix, affinity) in directMappings)
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
        var expectationPrefixes = new (string Prefix, PersonalAffinity? ExpectedAffinity)[]
        {
            ("do i love ", PersonalAffinity.Love),
            ("do i like ", PersonalAffinity.Like),
            ("do i dislike ", PersonalAffinity.Dislike),
            ("do i hate ", PersonalAffinity.Dislike),
            ("how do i feel about ", null),
            ("what do i think about ", null)
        };

        foreach (var (prefix, expectedAffinity) in expectationPrefixes)
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

    private static PersonalMemoryTenantScope ResolveTenantScope(TurnContext turn)
    {
        var accountId = ReadTenantAttribute(turn, "accountId") ?? "usr_openjibo_owner";
        var loopId = ReadTenantAttribute(turn, "loopId") ?? "openjibo-default-loop";
        var deviceId = turn.DeviceId ?? ReadTenantAttribute(turn, "deviceId") ?? "unknown-device";
        return new PersonalMemoryTenantScope(accountId, loopId, deviceId);
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
        @"\bin\s+(?<location>[a-z][a-z\s'\-]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherLocationSuffixPattern = new(
        @"\b(?:today|tonight|tomorrow|outside|right now|please|thanks)\b",
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
        " is my favourite "
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

    private static readonly (string Phrase, string Station)[] RadioGenreAliases =
    [
        ("country music", "Country"),
        ("country radio", "Country"),
        ("country", "Country"),
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
    IDictionary<string, object?>? SkillPayload = null);
