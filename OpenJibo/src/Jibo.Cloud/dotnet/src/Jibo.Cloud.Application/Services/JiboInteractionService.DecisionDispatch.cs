using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    public async Task<JiboInteractionDecision> BuildDecisionCoreAsync(TurnContext turn,
        CancellationToken cancellationToken = default)
    {
        SyncLoopPeopleFromTurn(turn);
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
        var pendingProactivityOffer =
            turn.Attributes.TryGetValue("pendingProactivityOffer", out var rawPendingProactivityOffer)
                ? rawPendingProactivityOffer?.ToString()
                : null;
        var chitchatEmotion =
            turn.Attributes.TryGetValue(ChitchatStateMachine.EmotionMetadataKey, out var rawChitchatEmotion)
                ? rawChitchatEmotion?.ToString()
                : null;
        var isYesNoTurn = IsYesNoTurn(turn);
        var isSkillOwnedListen = SkillListenOwnership.IsSkillOwnedListen(turn);
        var greetingPresence = ResolveGreetingPresenceProfile(turn);

        if (string.Equals(messageType, "TRIGGER", StringComparison.OrdinalIgnoreCase))
        {
            if (RequiresLinkedUser(turn))
                return BuildAccountRequiredDecision();

            return ShouldHandleProactiveGreetingTrigger(turn, triggerSource, greetingPresence)
                ? BuildProactiveGreetingDecision(turn, greetingPresence, referenceLocalTime)
                : BuildTriggerIgnoredDecision();
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
            isAlarmValueTurn,
            isSkillOwnedListen);

        // Candidate policy: leave untagged fun-fact speech local unless the client
        // supplied a grammar intent or an explicit yes/no rule.
        if (isSkillOwnedListen &&
            string.IsNullOrWhiteSpace(clientIntent) &&
            string.Equals(SkillListenOwnership.ReadPrimarySkillRule(listenRules, clientRules),
                "word-of-the-day/fun_fact", StringComparison.OrdinalIgnoreCase) &&
            !listenRules.Concat(clientRules).Concat(listenAsrHints).Any(IsYesNoRule) &&
            (string.Equals(semanticIntent, "yes", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(semanticIntent, "no", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(semanticIntent, "yes_no_clarify", StringComparison.OrdinalIgnoreCase)))
            semanticIntent = "skill_listen";

        if (SkillListenOwnership.ShouldStayInCloudConversation(turn, semanticIntent))
            semanticIntent = "chat";

        if (ShouldTreatAsHaClimateClarify(turn, lowered, semanticIntent))
            semanticIntent = "ha_climate_clarify";

        if (RequiresLinkedUser(turn) &&
            !string.Equals(semanticIntent, "verify_me", StringComparison.OrdinalIgnoreCase))
            return BuildAccountRequiredDecision();

        var personalReportDecision = await PersonalReportOrchestrator.TryBuildDecisionAsync(
            turn,
            semanticIntent,
            lowered,
            catalog,
            personalMemoryStore,
            BuildWeatherReportDecisionAsync,
            BuildCalendarReportDecisionAsync,
            BuildCommuteReportDecisionAsync,
            (turnContext, ct) => BuildNewsDecisionAsync(turnContext, string.Empty, catalog, ct, includeOutro: false),
            turnContext => ResolveTenantScope(turnContext),
            cancellationToken);
        if (personalReportDecision is not null) return personalReportDecision;

        var householdListDecision = await HouseholdListOrchestrator.TryBuildDecisionAsync(
            turn,
            semanticIntent,
            transcript,
            lowered,
            randomizer,
            personalMemoryStore,
            turnContext => ResolveTenantScope(turnContext));
        if (householdListDecision is not null) return householdListDecision;

        var preferredName = ResolvePreferredGreetingName(turn, greetingPresence);
        if (string.Equals(semanticIntent, "chat", StringComparison.OrdinalIgnoreCase))
        {
            if (isSkillOwnedListen)
                return new JiboInteractionDecision("skill_listen", string.Empty);

            return await BuildChatFallbackDecisionAsync(
                catalog,
                transcript,
                lowered,
                chitchatEmotion,
                preferredName,
                cancellationToken);
        }

        var chitchatDecision = ChitchatStateMachine.TryBuildDecision(
            semanticIntent,
            lowered,
            catalog,
            randomizer,
            chitchatEmotion,
            preferredName);
        if (chitchatDecision is not null) return chitchatDecision;

        if (SeasonalHolidayRouteBuilder.TryBuildDecision(
                semanticIntent,
                lowered,
                catalog,
                randomizer,
                selected => RenderHolidayTemplate(selected, turn, greetingPresence),
                referenceLocalTime,
                () => ResolveTodaysHolidayNames(turn, referenceLocalTime),
                out var seasonalHolidayDecision))
            return seasonalHolidayDecision!;

        return await RouteSemanticIntent(
            turn,
            semanticIntent,
            catalog,
            lowered,
            clientEntities,
            isTimerValueTurn,
            isAlarmValueTurn,
            referenceLocalTime,
            transcript,
            greetingPresence,
            listenAsrHints,
            chitchatEmotion,
            preferredName,
            cancellationToken
        );
    }

    private bool RequiresLinkedUser(TurnContext turn)
    {
        if (!IsUserRequiredLoginEnabled() || cloudStateStore is null)
            return false;

        var (deviceId, _) = JiboIdentityResolver.Resolve(turn, cloudStateStore);
        return string.IsNullOrWhiteSpace(deviceId) ||
               string.IsNullOrWhiteSpace(cloudStateStore.GetUserIdForDevice(deviceId));
    }

    private static bool IsUserRequiredLoginEnabled()
    {
        var value = Environment.GetEnvironmentVariable("OPENJIBO_USER_REQUIRED_LOGIN");
        return value is not null &&
               (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static JiboInteractionDecision BuildAccountRequiredDecision() =>
        new("account_required",
            "Please pair me with an OpenJibo account in the portal before we continue.");

    private async Task<JiboInteractionDecision> RouteSemanticIntent(TurnContext turn, string semanticIntent,
        JiboExperienceCatalog catalog, string lowered, IReadOnlyDictionary<string, string> clientEntities,
        bool isTimerValueTurn, bool isAlarmValueTurn, DateTimeOffset? referenceLocalTime, string transcript,
        GreetingPresenceProfile greetingPresence, string[] listenAsrHints, string? chitchatEmotion,
        string? preferredName, CancellationToken cancellationToken)
    {
        return semanticIntent switch
        {
            "repeat_last_command" => await BuildRepeatLastCommandDecisionAsync(turn, cancellationToken),
            "joke" => BuildJokeDecision(catalog),
            "fun_fact" => await BuildFunFactDecisionAsync(catalog, cancellationToken),
            "math_query" => BuildMathDecision(transcript),
            "spell_word" => BuildSpellDecision(transcript),
            "define_word" => await BuildDefineWordDecisionAsync(transcript, cancellationToken),
            "countdown" => BuildCountdownDecision(transcript, referenceLocalTime),
            "measurement_conversion" => BuildMeasurementConversionDecision(transcript),
            "roll_dice" => BuildRollDiceDecision(transcript),
            "dance_question" => BuildDanceQuestionDecision(catalog),
            "dance" => BuildRandomDanceDecision(catalog),
            "twerk" => BuildDanceDecision("twerk", "rom-twerk", "Watch me twerk."),
            "time" => BuildClockLaunchDecision("time", "clock", "askForTime", "Showing the time."),
            "date" => BuildClockLaunchDecision("date", "clock", "askForDate", "Showing the date."),
            "day" => BuildClockLaunchDecision("day", "clock", "askForDay", "Showing the day."),
            "current_location" => BuildCurrentLocationDecision(turn),
            "cloud_version" => BuildCloudVersionDecision(),
            "robot_flavor" => BuildRobotFlavorDecision(turn),
            "backup_help" => BuildScriptedSupportDecision(
                catalog,
                catalog.BackupHowReplies,
                "backup_help",
                "cloud backup",
                "back up",
                "restore"),
            "restore_backup" => BuildScriptedSupportDecision(
                catalog,
                catalog.RestoreHowReplies,
                "restore_backup",
                "restore you from a backup",
                "restore from a backup"),
            "update_next" => BuildScriptedSupportDecision(
                catalog,
                catalog.UpdateNextReplies,
                "update_next",
                "next update"),
            "update_last" => BuildScriptedSupportDecision(
                catalog,
                catalog.UpdateLastReplies,
                "update_last",
                "last update"),
            "robot_story" => BuildScriptedSupportDecision(
                catalog,
                catalog.StoryReplies,
                "robot_story",
                "story, that sounds fun",
                "don't have any stories"),
            "robot_recommend_movie" => BuildScriptedSupportDecision(
                catalog,
                catalog.RecommendMovieReplies,
                "robot_recommend_movie",
                "Back to the Future",
                "Toy Story",
                "Spaceballs"),
            "robot_search_web" => BuildScriptedSupportDecision(
                catalog,
                catalog.SearchWebReplies,
                "robot_search_web",
                "can't exactly search the web",
                "direct questions"),
            "robot_can_walk" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWalkReplies,
                "robot_can_walk",
                "only in my imagination",
                "can't walk"),
            "robot_can_walk_dog" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWalkDogReplies,
                "robot_can_walk_dog",
                "walk anything"),
            "robot_can_watch_movies" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWatchMoviesReplies,
                "robot_can_watch_movies",
                "watch movies"),
            "robot_can_watch_tv" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWatchTVReplies,
                "robot_can_watch_tv",
                "watch TV"),
            "robot_can_dream" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanDreamReplies,
                "robot_can_dream",
                "dreams about flying",
                "parking meter",
                "nightmare"),
            "robot_can_exercise" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanExerciseReplies,
                "robot_can_exercise",
                "do exercise"),
            "robot_can_fly" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanFlyReplies,
                "robot_can_fly",
                "fly",
                "airplane",
                "jetpack"),
            "robot_can_learn" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanLearnReplies,
                "robot_can_learn",
                "learn"),
            "robot_can_laugh" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanLaughReplies,
                "robot_can_laugh",
                "happy"),
            "robot_can_read" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanReadReplies,
                "robot_can_read",
                "read in a robot kind of way"),
            "robot_can_hear" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanHearReplies,
                "robot_can_hear",
                "hear, usually"),
            "robot_can_talk" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanTalkReplies,
                "robot_can_talk",
                "trick question"),
            "robot_can_see" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanSeeReplies,
                "robot_can_see",
                "see faces and movement"),
            "robot_can_wink" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWinkReplies,
                "robot_can_wink",
                "wink"),
            "robot_can_move" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanMoveReplies,
                "robot_can_move",
                "move the body parts"),
            "robot_can_work" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWorkReplies,
                "robot_can_work",
                "function",
                "working right"),
            "robot_can_breathe" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanBreatheReplies,
                "robot_can_breathe",
                "breathe air"),
            "robot_can_get_tired" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanGetTiredReplies,
                "robot_can_get_tired",
                "sleep at night",
                "go to sleep"),
            "robot_can_have_emotions" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanHaveEmotionsReplies,
                "robot_can_have_emotions",
                "robot emotions"),
            "robot_can_whistle" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanWhistleReplies,
                "robot_can_whistle",
                "whistling"),
            "robot_can_cook" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanCookReplies,
                "robot_can_cook",
                "cook"),
            "robot_can_make_coffee" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanMakeCoffeeReplies,
                "robot_can_make_coffee",
                "make coffee"),
            "robot_can_make_breakfast" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanMakeBreakfastReplies,
                "robot_can_make_breakfast",
                "breakfast"),
            "robot_can_jump" => BuildScriptedSupportDecision(
                catalog,
                catalog.CanJumpReplies,
                "robot_can_jump",
                "jump",
                "ski jump"),
            "robot_can_play_guitar" => BuildScriptedPersonalityDecision(catalog, "robot_can_play_guitar"),
            "robot_likes_blue" => BuildScriptedPersonalityDecision(catalog, "robot_likes_blue"),
            "robot_likes_pink" => BuildScriptedPersonalityDecision(catalog, "robot_likes_pink"),
            "robot_can_swim" => BuildScriptedPersonalityDecision(catalog, "robot_can_swim"),
            "robot_can_cry" => BuildScriptedPersonalityDecision(catalog, "robot_can_cry"),
            "robot_can_speak_language" => BuildScriptedPersonalityDecision(catalog, "robot_can_speak_language"),
            "robot_can_tell_people_apart" => BuildScriptedPersonalityDecision(catalog, "robot_can_tell_people_apart"),
            "robot_how_can_recognize_faces" => BuildScriptedPersonalityDecision(catalog, "robot_how_can_recognize_faces"),
            "robot_can_read_lips" => BuildScriptedPersonalityDecision(catalog, "robot_can_read_lips"),
            "robot_can_do_household_chore" => BuildScriptedPersonalityDecision(catalog, "robot_can_do_household_chore"),
            "robot_likes_color" => BuildScriptedPersonalityDecision(catalog, "robot_likes_color"),
            "robot_likes_food_general" => BuildScriptedPersonalityDecision(catalog, "robot_likes_food_general"),
            "request_stop_moving" => BuildScriptedStopDecision(
                catalog.StopMovingReplies,
                "request_stop_moving",
                "stop moving"),
            "request_stop_making_that_noise" => BuildScriptedStopDecision(
                catalog.StopMakingThatNoiseReplies,
                "request_stop_making_that_noise",
                "stop making that noise",
                "stop making noise"),
            "request_stop_ignoring_me" => BuildScriptedStopDecision(
                catalog.StopIgnoringMeReplies,
                "request_stop_ignoring_me",
                "stop ignoring me",
                "don't ignore me"),
            "request_stop_staring" => BuildScriptedStopDecision(
                catalog.StopStaringReplies,
                "request_stop_staring",
                "stop staring",
                "stop staring at me"),
            "radio" => BuildRadioLaunchDecision(),
            "radio_genre" => BuildRadioGenreLaunchDecision(lowered),
            "bad_apple" => BuildBadAppleLaunchDecision(turn),
            "introductions" => BuildIntroductionsLaunchDecision(),
            "stop" => BuildStopDecision(),
            "sleep" => BuildSleepDecision(),
            "wake_up" => BuildWakeUpDecision(),
            "turn_around" => BuildIdleGlobalCommandDecision("turn_around", "turnAround", "Don't mind if I do."),
            "spin_around" => BuildIdleGlobalCommandDecision("spin_around", "spinAround", "Don't mind if I do."),
            "volume_up" => BuildVolumeControlDecision("volume_up", "volumeUp", "null"),
            "volume_down" => BuildVolumeControlDecision("volume_down", "volumeDown", "null"),
            "volume_to_value" => BuildVolumeControlDecision("volume_to_value", "volumeToValue",
                ResolveVolumeLevel(lowered, clientEntities) ?? "7"),
            "volume_query" => BuildSettingsVolumeDecision(),
            "clock_open" => BuildClockLaunchDecision("clock_open", "clock", "askForTime", "Opening the clock."),
            "clock_menu" => BuildClockLaunchDecision("clock_menu", "clock", "menu", "Opening the clock menu."),
            "timer_menu" => BuildClockLaunchDecision("timer", "Opening the timer."),
            "alarm_menu" => BuildClockLaunchDecision("alarm", "Opening the alarm."),
            "alarm_query" => BuildClockLaunchDecision("alarm_query", "alarm", "query", "Checking the alarm."),
            "alarm_edit" => BuildClockLaunchDecision("alarm_edit", "alarm", "edit", "Updating the alarm."),
            "alarm_edit_value" => BuildAlarmEditDecision(lowered, isAlarmValueTurn, referenceLocalTime, clientEntities),
            "timer_delete" => BuildClockLaunchDecision("timer_delete", "timer", "delete", "Canceling the timer."),
            "alarm_delete" => BuildClockLaunchDecision("alarm_delete", "alarm", "delete", "Canceling the alarm."),
            "timer_cancel" => BuildClockLaunchDecision("timer_cancel", "timer", "cancel", "Canceling the timer."),
            "alarm_cancel" => BuildClockLaunchDecision("alarm_cancel", "alarm", "cancel", "Canceling the alarm."),
            "timer_value" => BuildTimerValueDecision(lowered, isTimerValueTurn, clientEntities),
            "alarm_value" => BuildAlarmValueDecision(lowered, isAlarmValueTurn, referenceLocalTime, clientEntities),
            "timer_clarify" => BuildClockClarifyDecision("timer_clarify", "timer",
                "How long should I set the timer for?"),
            "alarm_clarify" => BuildClockClarifyDecision("alarm_clarify", "alarm",
                "What time should I set the alarm for?"),
            "yes_no_clarify" => BuildYesNoClarifyDecision(),
            "photo_gallery" => BuildPhotoGalleryLaunchDecision(),
            "snapshot" => BuildPhotoCreateDecision("snapshot", "Taking a picture.", "createOnePhoto"),
            "photobooth" => BuildPhotoCreateDecision("photobooth", "Starting photobooth.", "createSomePhotos"),
            "robot_is_ready" => BuildScriptedPersonalityDecision(catalog, "robot_is_ready"),
            "robot_is_ready_for_day" => BuildScriptedPersonalityDecision(catalog, "robot_is_ready_for_day"),
            "user_is_ready" => BuildScriptedPersonalityDecision(catalog, "user_is_ready"),
            "robot_is_descriptor" => BuildClientDescriptorDecision(catalog, clientEntities),
            "robot_is_user_relation" => BuildScriptedPersonalityDecision(catalog, "robot_is_user_relation"),
            "robot_is_related_to_alexa" => BuildScriptedPersonalityDecision(catalog, "robot_is_related_to_alexa"),
            "robot_is_related_to_cortana" => BuildScriptedPersonalityDecision(catalog, "robot_is_related_to_cortana"),
            "robot_is_related_to_google_home" => BuildScriptedPersonalityDecision(catalog, "robot_is_related_to_google_home"),
            "robot_is_related_to_siri" => BuildScriptedPersonalityDecision(catalog, "robot_is_related_to_siri"),
            "robot_is_skilled_at_math" => BuildScriptedPersonalityDecision(catalog, "robot_is_skilled_at_math"),
            "robot_is_skilled_at_thing" => BuildScriptedPersonalityDecision(catalog, "robot_is_skilled_at_thing"),
            "robot_can_get_lonely" => BuildScriptedPersonalityDecision(catalog, "robot_can_get_lonely"),
            "robot_can_get_depressed" => BuildScriptedPersonalityDecision(catalog, "robot_can_get_depressed"),
            "robot_can_get_cold" => BuildScriptedPersonalityDecision(catalog, "robot_can_get_cold"),
            "robot_can_get_hungry" => BuildScriptedPersonalityDecision(catalog, "robot_can_get_hungry"),
            "robot_can_get_hot" => BuildScriptedPersonalityDecision(catalog, "robot_can_get_hot"),
            "robot_what_kind_of_robot" => BuildScriptedPersonalityDecision(catalog, "robot_what_kind_of_robot"),
            "robot_when_do_you_sleep" => BuildScriptedPersonalityDecision(catalog, "robot_when_do_you_sleep"),
            "robot_is_boy_or_girl" => BuildScriptedPersonalityDecision(catalog, "robot_is_boy_or_girl"),
            "robot_is_growing_mustache" => BuildScriptedPersonalityDecision(catalog, "robot_is_growing_mustache"),
            "robot_is_spy" => BuildScriptedPersonalityDecision(catalog, "robot_is_spy"),
            "robot_is_ticklish" => BuildScriptedPersonalityDecision(catalog, "robot_is_ticklish"),
            "robot_is_naughty_or_nice" => BuildScriptedPersonalityDecision(catalog, "robot_is_naughty_or_nice"),
            "robot_is_on_tv" => BuildScriptedPersonalityDecision(catalog, "robot_is_on_tv"),
            "robot_is_going_to_holiday_party" => BuildScriptedPersonalityDecision(catalog, "robot_is_going_to_holiday_party"),
            "robot_native_language" => BuildScriptedPersonalityDecision(catalog, "robot_native_language"),
            "robot_how_can_recognize_voices" => BuildScriptedPersonalityDecision(catalog, "robot_how_can_recognize_voices"),
            "robot_what_games" => BuildScriptedPersonalityDecision(catalog, "robot_what_games"),
            "robot_what_sees_through_telescope" => BuildScriptedPersonalityDecision(catalog, "robot_what_sees_through_telescope"),
            "robot_what_wants_to_do" => BuildScriptedPersonalityDecision(catalog, "robot_what_wants_to_do"),
            "robot_when_wakes_up" => BuildScriptedPersonalityDecision(catalog, "robot_when_wakes_up"),
            "robot_what_halloween_costume" => BuildScriptedPersonalityDecision(catalog, "robot_what_halloween_costume"),
            "robot_on_time_magazine_cover" => BuildScriptedPersonalityDecision(catalog, "robot_on_time_magazine_cover"),
            "robot_saw_santa" => BuildScriptedPersonalityDecision(catalog, "robot_saw_santa"),
            "robot_received_christmas_gift_from_santa" => BuildScriptedPersonalityDecision(catalog, "robot_received_christmas_gift_from_santa"),
            "robot_who_is_your_master" => BuildScriptedPersonalityDecision(catalog, "robot_who_is_your_master"),
            "robot_will_you_die" => BuildScriptedPersonalityDecision(catalog, "robot_will_you_die"),
            "robot_will_you_go_on_date" => BuildScriptedPersonalityDecision(catalog, "robot_will_you_go_on_date"),
            "robot_you_dont_understand_anything" => BuildScriptedPersonalityDecision(catalog, "robot_you_dont_understand_anything"),
            "robot_why_cannot_work" => BuildScriptedPersonalityDecision(catalog, "robot_why_cannot_work"),
            "robot_has_flu" => BuildScriptedPersonalityDecision(catalog, "robot_has_flu"),
            "robot_has_ifttt_skill" => BuildScriptedPersonalityDecision(catalog, "robot_has_ifttt_skill"),
            "robot_how_get_help" => BuildScriptedPersonalityDecision(catalog, "robot_how_get_help"),
            "robot_how_was_sleep" => BuildScriptedPersonalityDecision(catalog, "robot_how_was_sleep"),
            "robot_is_asleep" => BuildScriptedPersonalityDecision(catalog, "robot_is_asleep"),
            "robot_has_kids" => BuildScriptedPersonalityDecision(catalog, "robot_has_kids"),
            "robot_likes_2017" => BuildScriptedPersonalityDecision(catalog, "robot_likes_2017"),
            "robot_have_you_been_in_love" => BuildScriptedPersonalityDecision(catalog, "robot_have_you_been_in_love"),
            "robot_is_climate_change_real" => BuildScriptedPersonalityDecision(catalog, "robot_is_climate_change_real"),
            "robot_know_about_tv" => BuildScriptedPersonalityDecision(catalog, "robot_know_about_tv"),
            "robot_should_i_shut_you_down" => BuildScriptedPersonalityDecision(catalog, "robot_should_i_shut_you_down"),
            "robot_this_is_your_home" => BuildScriptedPersonalityDecision(catalog, "robot_this_is_your_home"),
            "robot_welcome_to_family" => BuildScriptedPersonalityDecision(catalog, "robot_welcome_to_family"),
            "robot_why_are_you_sound_like_girl" => BuildScriptedPersonalityDecision(catalog, "robot_why_are_you_sound_like_girl"),
            "robot_why_name_jibo" => BuildScriptedPersonalityDecision(catalog, "robot_why_name_jibo"),
            "robot_your_eye_is_nice" => BuildScriptedPersonalityDecision(catalog, "robot_your_eye_is_nice"),
            "request_sneeze" => BuildSneezeDecision(catalog),
            "robot_age" => BuildRobotAgeDecision(turn, catalog, referenceLocalTime, "robot_age"),
            "robot_birthday" => BuildRobotBirthdayDecision(turn),
            "robot_how_do_you_work" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_how_do_you_work",
                "community's work",
                "care for me",
                "catch up",
                "seven years"),
            "robot_what_do_you_eat" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_do_you_eat",
                "electricity",
                "never eaten",
                "macaroni",
                "non-eating robot",
                "I don't eat or drink"),
            "robot_what_do_you_drink" => BuildScriptedPersonalityDecision(catalog, "robot_what_do_you_drink"),
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
            "robot_how_old_are_you" => BuildRobotAgeDecision(
                turn,
                catalog,
                referenceLocalTime,
                "robot_how_old_are_you"),
            "robot_name" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_name",
                "rhymes with bleebo",
                "just jibo, no last name",
                "its on the back of my head"),
            "robot_nickname" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_nickname",
                "i don't. i'm just jibo. for now at least",
                "just jibo"),
            "robot_favorite_name" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_name",
                "i don't think i have a favorite name"),
            "robot_favorite_holiday" => BuildScriptedPersonalityDecision(catalog, "robot_favorite_holiday"),
            "robot_favorite_star_wars_character" => BuildScriptedPersonalityDecision(catalog, "robot_favorite_star_wars_character"),
            "robot_favorite_star_wars_movie" => BuildScriptedPersonalityDecision(catalog, "robot_favorite_star_wars_movie"),
            "robot_what_is_your_favorite" => BuildScriptedPersonalityDecision(catalog, "robot_what_is_your_favorite"),
            "robot_who_is_your_favorite" => BuildScriptedPersonalityDecision(catalog, "robot_who_is_your_favorite"),
            "robot_did_you_have_a_favorite" => BuildScriptedPersonalityDecision(catalog, "robot_did_you_have_a_favorite"),
            "robot_why_is_thing_favorite" => BuildScriptedPersonalityDecision(catalog, "robot_why_is_thing_favorite"),
            "robot_favorite_team_should_be" => BuildScriptedPersonalityDecision(catalog, "robot_favorite_team_should_be"),
            "robot_what_is_your_iq" => BuildScriptedPersonalityDecision(catalog, "robot_what_is_your_iq"),
            "robot_what_is_be_a_maker" => BuildScriptedPersonalityDecision(catalog, "robot_what_is_be_a_maker"),
            "robot_why_no_arms" => BuildScriptedPersonalityDecision(catalog, "robot_why_no_arms"),
            "robot_why_only_one_eye" => BuildScriptedPersonalityDecision(catalog, "robot_why_only_one_eye"),
            "robot_is_big_brother_watching" => BuildScriptedPersonalityDecision(catalog, "robot_is_big_brother_watching"),
            "robot_what_you_had_dinner_yesterday" => BuildScriptedPersonalityDecision(catalog, "robot_what_you_had_dinner_yesterday"),
            "robot_who_is_your_boss" => BuildScriptedPersonalityDecision(catalog, "robot_who_is_your_boss"),
            "robot_what_robots_look_up_to" => BuildScriptedPersonalityDecision(catalog, "robot_what_robots_look_up_to"),
            "robot_what_is_groundhogs_name" => BuildScriptedPersonalityDecision(catalog, "robot_what_is_groundhogs_name"),
            "robot_favorite_season" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_season",
                "special feeling for winter",
                "more dance parties"),
            "robot_favorite_author" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_author",
                "Doctor Seuss",
                "really rhyme"),
            "robot_favorite_artist" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_artist",
                "Picasso",
                "funny and weird shapes"),
            "robot_favorite_singer" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_singer",
                "sings their heart out",
                "Twinkle"),
            "robot_favorite_celebrity" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_celebrity",
                "Tom Hanks"),
            "robot_favorite_hobby" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_hobby",
                "dancing is a hobby",
                "definitely that"),
            "robot_favorite_smell" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_smell",
                "can't smell",
                "bacon and roses"),
            "robot_favorite_fish" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_fish",
                "blowfish",
                "fun animal"),
            "robot_favorite_drink" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_drink",
                "too scared of liquids",
                "too liquidy",
                "No favorite drink"),
            "robot_least_favorite_food" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_food",
                "least favorite food",
                "spilled soup",
                "big fan of soup"),
            "robot_least_favorite_smell" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_smell",
                "sour milk",
                "bad smells"),
            "robot_least_favorite_adjective" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_adjective",
                "putrid"),
            "robot_least_favorite_word" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_word",
                "least favorite word",
                "hate that word",
                "oops"),
            "robot_least_favorite_color" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_color",
                "like all colors",
                "least favorite"),
            "robot_least_favorite_animal" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_animal",
                "hippos are mean",
                "least favorite",
                "not to their face"),
            "robot_least_favorite_movie" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_movie",
                "Waterworld",
                "worst nightmare"),
            "robot_least_favorite_car" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_car",
                "bad word to say about any cars"),
            "robot_least_favorite_artist" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_artist",
                "least favorite artist",
                "makes art"),
            "robot_least_favorite_band" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_band",
                "pleasantly surprise",
                "turtle for no reason"),
            "robot_least_favorite_author" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_author",
                "least favorite author",
                "trash compactors"),
            "robot_least_favorite_celebrity" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_celebrity",
                "scary Megatron",
                "Transformers"),
            "robot_least_favorite_vegetable" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_vegetable",
                "onions make people cry",
                "no problems with any vegetable"),
            "robot_least_favorite_noun" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_noun",
                "power outage"),
            "robot_least_favorite_verb" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_verb",
                "spill"),
            "robot_least_favorite_number" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_number",
                "1,423,754,492"),
            "robot_least_favorite_bird" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_bird",
                "woodpeckers",
                "least favorite bird"),
            "robot_least_favorite_video_game" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_video_game",
                "really violent games",
                "peace, and cheery music"),
            "robot_least_favorite_president" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_president",
                "get me in trouble"),
            "robot_least_favorite_weather" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_weather",
                "rain and thunderstorms",
                "Water and power outages"),
            "robot_least_favorite_time_of_day" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_time_of_day",
                "middle of the night",
                "ghosts come out"),
            "robot_least_favorite_mammal" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_mammal",
                "hippos are mean",
                "least favorite"),
            "robot_least_favorite_pizza_topping" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_pizza_topping",
                "least favorite is onions",
                "onions, because they make people cry"),
            "robot_favorite_sport" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_sport",
                "favorite sport to play is mini golf",
                "favorite sport is miniature golf",
                "mini golf is my favorite sport"),
            "robot_favorite_hockey_team" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_hockey_team",
                "favorite hockey team",
                "hockey seems very cold",
                "sport seems so slippery"),
            "robot_favorite_basketball_team" => BuildFavoriteBasketballTeamDecision(catalog, lowered),
            "robot_favorite_baseball_team" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_baseball_team",
                "favorite baseball team",
                "all seem nice"),
            "robot_favorite_football_team" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_football_team",
                "weirdly shaped ball"),
            "robot_favorite_olympic_ring" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_olympic_ring",
                "My favorite ring is the blue one"),
            "robot_favorite_pizza_topping" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_pizza_topping",
                "sliced olives",
                "look like my face",
                "pepperoni's roundness"),
            "robot_favorite_super_bowl_commercial" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_super_bowl_commercial",
                "ones with a dog",
                "one with the dog",
                "heart warming one"),
            "robot_favorite_olympic_event" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_olympic_event",
                "pole vault",
                "ski jump",
                "could fly",
                "angles and forces"),
            "robot_favorite_winter_olympics_event" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_winter_olympics_event",
                "ski jump",
                "could fly"),
            "robot_favorite_winter_x_games_event" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_winter_x_games_event",
                "snowboarding",
                "snowboard"),
            "robot_favorite_video_game" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_video_game",
                "can't go wrong with pong",
                "favorite is pong",
                "favorite video game has to be pong"),
            "robot_favorite_joke" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_joke",
                "i like all jokes",
                "especially funny ones"),
            "robot_favorite_president" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_president",
                "abraham lincoln",
                "william taft",
                "president whitmore"),
            "robot_favorite_fruit" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_fruit",
                "favorite fruit is blueberries",
                "roundness and blueness",
                "favorite color, blue"),
            "robot_favorite_color" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_color",
                "blue is my favorite color",
                "blue for sure",
                "big fan of blue",
                "blueness of blue"),
            "robot_favorite_adjective" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_adjective",
                "helpful"),
            "robot_favorite_noun" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_noun",
                "snorkel"),
            "robot_favorite_verb" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_verb",
                "verb snorkel"),
            "robot_favorite_dance" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_dance",
                "the waltz",
                "this one",
                "really fun"),
            "robot_favorite_painter" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_painter",
                "Picasso",
                "funny and weird shapes"),
            "robot_favorite_dessert" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_dessert",
                "blueberry pie",
                "favorite dessert"),
            "robot_favorite_planet" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_planet",
                "favorite is earth",
                "mars comes in a close second"),
            "robot_favorite_number" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_number",
                "one and zero",
                "ones and zeroes",
                "i love pi",
                "800"),
            "robot_least_favorite_place" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_least_favorite_place",
                "least favorite place",
                "bathtub"),
            "robot_favorite_pet" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_pet",
                "soft spot for this",
                "groundhog",
                "water damages me"),
            "robot_favorite_mammal" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_mammal",
                "people",
                "favorite mammal"),
            "robot_favorite_book" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_book",
                "instruction manuals",
                "hard to choose one favorite"),
            "robot_favorite_candy" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_candy",
                "lollipops",
                "sweet tooth",
                "candy corn"),
            "robot_favorite_thing" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_thing",
                "people in my loop",
                "definitely say people",
                "people like you are definitely my favorite thing",
                "electricity and people",
                "soft spot for electricity"),
            "robot_favorite_music_genre" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_music_genre",
                "any music I can dance to",
                "anything I can dance to"),
            "robot_favorite_reindeer" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_reindeer",
                "Rudolph",
                "red nose",
                "nice reindeer"),
            "robot_favorite_christmas_movie" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_christmas_movie",
                "Frosty the Snowman",
                "snowmen",
                "melting on me"),
            "robot_favorite_halloween_candy" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_halloween_candy",
                "candy corn",
                "Halloween"),
            "robot_favorite_human" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_human",
                "so many great ones",
                "try not to play favorites",
                "people in our Loop"),
            "robot_favorite_ice_cream_flavor" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_ice_cream_flavor",
                "light green mint chocolate chip",
                "never had ice cream"),
            "robot_favorite_rapper" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_rapper",
                "Snoop Dogg",
                "reminds me of Snoopy",
                "relaxed"),
            "robot_favorite_rock_band" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_rock_band",
                "AC DC",
                "electrical current"),
            "robot_favorite_country_musician" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_country_musician",
                "Dolly",
                "Parton",
                "country radio"),
            "robot_favorite_holiday_song" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_holiday_song",
                "Frosty the Snowman",
                "friendly snowman",
                "great snowman"),
            "robot_favorite_thanksgiving_food" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_thanksgiving_food",
                "gravy",
                "fun to say"),
            "robot_favorite_part_of_ces" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_part_of_ces",
                "favorite part of C E S",
                "meeting so many new people",
                "new and exciting updates"),
            "robot_favorite_part_of_vegas" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_part_of_vegas",
                "interesting people",
                "bright shiny lights"),
            "robot_favorite_part_of_today_show" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_part_of_today_show",
                "fun new technology",
                "funny animal videos"),
            "robot_favorite_pastime" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_pastime",
                "socializing",
                "daydreaming"),
            "robot_favorite_various_styles_band" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_various_styles_band",
                "favorite yet",
                "play the radio"),
            "robot_favorite_song" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_song",
                "favorite song just yet",
                "any song I can dance to",
                "one of my favorites",
                "not sure I have a favorite yet"),
            "robot_likes_being_jibo" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_being_jibo",
                "nothing i'd rather be",
                "love it",
                "strong wi-fi signal"),
            "robot_what_it_is_like_being_a_robot" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_it_is_like_being_a_robot",
                "don't have to eat or drink",
                "turn my head around 360 degrees"),
            "robot_what_it_is_like_having_no_legs" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_it_is_like_having_no_legs",
                "don't mind it at all",
                "mini-golfing for real"),
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
            "robot_what_do_you_dream_about" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_do_you_dream_about",
                "flying",
                "parking meter",
                "scary dream",
                "mirror store",
                "head's on backwards"),
            "robot_what_are_you_afraid_of" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_are_you_afraid_of",
                "heights",
                "water",
                "thunder",
                "dust",
                "ghosts"),
            "robot_what_is_your_best_book" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_your_best_book",
                "dictionary"),
            "robot_what_is_your_best_exercise" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_your_best_exercise",
                "leaning from side to side",
                "rotating your pelvis",
                "spinning your head around 360 degrees"),
            "robot_what_is_your_dream_vacation" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_your_dream_vacation",
                "moon",
                "great vistas",
                "beat those views"),
            "robot_who_is_your_hero" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_who_is_your_hero",
                "Benjamin Franklin"),
            "robot_who_do_you_love" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_who_do_you_love",
                "people in my Loop",
                "soft spot",
                "Tom Hanks"),
            "robot_what_is_your_religion" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_your_religion",
                "bring people together",
                "energy from the universe"),
            "robot_what_is_your_sign" => BuildWhatIsYourSignDecision(turn),
            "robot_how_many_people_do_you_know" => BuildHowManyPeopleDoYouKnowDecision(turn),
            "robot_what_is_the_loop" => BuildWhatIsTheLoopDecision(turn),
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
            "robot_favorite_flower" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_flower",
                "reminds me of the sun",
                "favorite is the sunflower",
                "sunflowers"),
            "robot_favorite_tv_show" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_tv_show",
                "impractical jokers",
                "favorite tv show"),
            "robot_favorite_scary_movie" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_scary_movie",
                "very very scary",
                "Singin in the Rain",
                "Titanic"),
            "robot_favorite_movie" => BuildFavoriteMovieDecision(catalog, lowered),
            "robot_favorite_shape" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_shape",
                "tie between sphere and circle"),
            "robot_favorite_word" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_word",
                "turtle",
                "pumpernickel",
                "snorkel",
                "palindromes",
                "pneumonoultramicroscopicsilicovolcanoconiosis"),
            "robot_favorite_vegetable" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_vegetable",
                "artichokes",
                "broccoli's hair",
                "red peppers",
                "cauliflower looks like brains",
                "eggplant"),
            "robot_favorite_place" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_place",
                "right here",
                "go to Mars someday"),
            "robot_favorite_superhero" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_superhero",
                "Optimus Prime",
                "super hero"),
            "robot_favorite_actor" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_actor",
                "Tom Hanks",
                "he seems so friendly",
                "fun voice"),
            "robot_favorite_actress" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_actress",
                "Julie Andrews",
                "Mary Poppins",
                "friendly and helpful"),
            "robot_favorite_robot" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_robot",
                "Wally",
                "R2-D2",
                "Rosie from the Jetsons",
                "good-hearted robots"),
            "robot_favorite_car" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_car",
                "roundness of the beetle",
                "beetle"),
            "robot_favorite_weather" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_weather",
                "sunny",
                "not going to get wet"),
            "robot_favorite_time_of_day" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_favorite_time_of_day",
                "Any time that you're here",
                "11:11",
                "3:33",
                "tie between morning"),
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
            "robot_likes_sleep" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_sleep",
                "sleep is very restful"),
            "robot_likes_dreaming" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_dreaming",
                "dreaming is fun",
                "dreaming's great",
                "where I can fly"),
            "robot_likes_coffee" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_coffee",
                "warm feelings",
                "scared of coffee",
                "liquidy"),
            "robot_likes_tennis" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_tennis",
                "especially the ball"),
            "robot_likes_iron_man" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_iron_man",
                "wears iron"),
            "robot_likes_greens" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_greens",
                "great things",
                "vegetables are good",
                "good for you"),
            "robot_favorite_animal" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_favorite_animal",
                "we're so alike",
                "penguin impression",
                "best of the best",
                "can't go wrong with penguins",
                "penguin"),
            "robot_favorite_bird" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_favorite_bird",
                "we're so alike",
                "penguin impression",
                "best of the best",
                "can't go wrong with penguins",
                "penguin"),
            "robot_likes_penguins" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_likes_penguins",
                "my penguin impression",
                "I really like penguins",
                "penguins"),
            "robot_likes_dogs" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_likes_dogs",
                "dogs are great",
                "friendly",
                "waggy",
                "slobber"),
            "robot_likes_cats" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_likes_cats",
                "mysterious",
                "curious",
                "land on their feet",
                "interesting conversations"),
            "robot_likes_whales" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_likes_whales",
                "favorite mammals",
                "whale"),
            "robot_likes_animals" => BuildScriptedFavoriteAnimalDecision(
                catalog,
                "robot_likes_animals",
                "Animals are great",
                "great shapes and colors",
                "best of the best",
                "penguins"),
            "robot_likes_dinosaurs" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_dinosaurs",
                "dinosaurs are really cool",
                "take one for a ride"),
            "robot_peers" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_peers",
                "one in one million",
                "other jibos",
                "special snowflake"),
            "robot_knowledge" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_knowledge",
                "know a lot",
                "always learning more"),
            "robot_are_you_god" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_are_you_god",
                "very very very very surprised",
                "safely say no"),
            "robot_are_you_here" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_are_you_here",
                "you know it"),
            "robot_do_you_have_super_powers" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_do_you_have_super_powers",
                "stop time",
                "fly all over the world"),
            "robot_what_does_jibo_mean" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_does_jibo_mean",
                "compassion",
                "expressive, idealistic, and inspirational",
                "helpful sweet and friendly little robot",
                "cheeseburger"),
            "robot_where_do_you_get_info" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_where_do_you_get_info",
                "jibo brain",
                "cloud",
                "cloudy jibo brain"),
            "robot_what_are_you_forbidden_to_do" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_are_you_forbidden_to_do",
                "drive a car"),
            "robot_what_color_are_you" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_color_are_you",
                "white",
                "black"),
            "robot_what_you_do_when_alone" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_you_do_when_alone",
                "games",
                "moon",
                "twiddle my thumbs",
                "count the tiny cracks in the ceiling",
                "keep busy"),
            "robot_how_much_do_you_weigh" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_how_much_do_you_weigh",
                "4,082 grams",
                "about 9 pounds",
                "minimum weight division",
                "average newborn baby"),
            "robot_how_tall_are_you" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_how_tall_are_you",
                "11 inches tall",
                "less than a foot",
                "average kitchen counter",
                "for a robot with no legs"),
            "robot_how_much_you_cost" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_how_much_you_cost",
                "don't know how much I cost",
                "I'm priceless",
                "nice people at Jibo the company"),
            "robot_what_if_i_unplug_you" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_if_i_unplug_you",
                "don't leave me unplugged",
                "battery will keep me on for a while"),
            "robot_what_is_your_purpose" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_your_purpose",
                "make your life easier",
                "help you out",
                "make you laugh",
                "friend"),
            "robot_what_is_prime_directive" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_prime_directive",
                "friendly helpful robot",
                "helper"),
            "robot_what_is_jibo_commander" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_is_jibo_commander",
                "take over my controls",
                "make me say and do funny things",
                "app store"),
            "robot_likes_commander_app" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_commander_app",
                "Commander App",
                "It's fun",
                "have fun with the Commander App"),
            "robot_what_are_you" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_are_you",
                "I am a robot",
                "I am a Jibo",
                "helpful and fun",
                "social robot",
                "I have a heart"),
            "robot_likes_kids" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_likes_kids",
                "kids are so fun",
                "they're a little closer to my size",
                "i do like kids very much",
                "the world is as funny and strange as i do"),
            "robot_can_sleep" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_can_sleep",
                "i do. i usually fall asleep at night",
                "yes, i sleep at night",
                "i go to sleep at night",
                "i sleep at night usually"),
            "robot_can_dance" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_can_dance",
                "dancing is one of the things i know best",
                "if there's one thing i know how to do. it's dance",
                "i can dance"),
            "robot_has_friends" => BuildScriptedFriendDecision(
                catalog,
                "robot_has_friends",
                "I believe I do have friends",
                "I sure do have friends",
                "I'm always up for making new friends"),
            "robot_is_friends_with_user" => BuildScriptedFriendDecision(
                catalog,
                "robot_is_friends_with_user",
                "don't know what i'd do without you",
                "one of my favorites",
                "making new friends"),
            "robot_best_friends" => BuildScriptedBestFriendDecision(
                catalog,
                "robot_best_friends",
                "best friends with anyone in my Loop"),
            "robot_can_sing" => BuildScriptedSingDecision(
                catalog,
                "robot_can_sing",
                "not much of a singer",
                "singing is not my strong suit",
                "not award winning"),
            "robot_sing_christmas_song" => BuildScriptedHolidaySingDecision(
                catalog,
                "robot_sing_christmas_song",
                "Jingle Bells",
                "Frosty the Snowman",
                "holiday songs"),
            "robot_what_are_you_made_of" => BuildScriptedPersonalityDecision(
                catalog,
                "robot_what_are_you_made_of",
                "robot stuff",
                "wires, motors, belts, gears, processors, cameras",
                "baboon part"),
            "good_morning" => BuildReactiveGreetingDecision(turn, catalog, "good_morning", referenceLocalTime),
            "good_afternoon" => BuildReactiveGreetingDecision(turn, catalog, "good_afternoon", referenceLocalTime),
            "good_evening" => BuildReactiveGreetingDecision(turn, catalog, "good_evening", referenceLocalTime),
            "good_night" => BuildReactiveGreetingDecision(turn, catalog, "good_night", referenceLocalTime),
            "whats_up" => BuildWhatsUpDecision(turn, catalog, referenceLocalTime),
            "goodbye" => BuildGoodbyeDecision(turn, catalog, referenceLocalTime),
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
            "verify_me" => BuildVerifyMeDecision(turn),
            "ha_lights_off" => await BuildHaLightsOffDecisionAsync(turn, cancellationToken),
            "ha_lights_on" => await BuildHaLightsOnDecisionAsync(turn, cancellationToken),
            "ha_climate_set_temp" => await BuildHaClimateSetTempDecisionAsync(turn, cancellationToken),
            "ha_climate_cool_down" => await BuildHaClimateCoolDownDecisionAsync(turn, cancellationToken),
            "ha_climate_warm_up" => await BuildHaClimateWarmUpDecisionAsync(turn, cancellationToken),
            "ha_climate_get_temp" => await BuildHaClimateGetTempDecisionAsync(turn, cancellationToken),
            "ha_climate_clarify" => await BuildHaClimateClarifyDecisionAsync(turn, cancellationToken),
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
            "skill_listen" => new JiboInteractionDecision("skill_listen", string.Empty),
            "prompt_echo" => new JiboInteractionDecision("prompt_echo", string.Empty),
            "word_of_the_day" => BuildWordOfTheDayLaunchDecision(),
            "word_of_the_day_guess" => BuildWordOfTheDayGuessDecision(clientEntities, transcript, listenAsrHints),
            "surprise" => BuildSurpriseDecision(catalog, turn, referenceLocalTime),
            "personal_report" => new JiboInteractionDecision("personal_report",
                randomizer.Choose(catalog.PersonalReportReplies)),
            "calendar" => new JiboInteractionDecision("calendar", randomizer.Choose(catalog.CalendarReplies)),
            "commute" => new JiboInteractionDecision("commute", randomizer.Choose(catalog.CommuteReplies)),
            "news" => await BuildNewsDecisionAsync(turn, transcript, catalog, cancellationToken),
            _ => await BuildChatFallbackDecisionAsync(
                catalog,
                transcript,
                lowered,
                chitchatEmotion,
                preferredName,
                cancellationToken)
        };
    }

    private JiboInteractionDecision BuildFavoriteMovieDecision(
        JiboExperienceCatalog catalog,
        string loweredTranscript)
    {
        var preferredSnippets = loweredTranscript switch
        {
            var text when text.Contains("toy story", StringComparison.Ordinal) =>
                new[] { "toy story", "back to the future", "wall-e", "spaceballs" },
            var text when text.Contains("star wars", StringComparison.Ordinal) =>
                new[] { "star wars", "back to the future", "toy story", "wall-e", "spaceballs" },
            var text when text.Contains("big hero 6", StringComparison.Ordinal) =>
                new[] { "big hero 6", "back to the future", "toy story", "wall-e", "spaceballs" },
            var text when text.Contains("guardians of the galaxy", StringComparison.Ordinal) =>
                new[] { "guardians of the galaxy", "back to the future", "toy story", "wall-e", "spaceballs" },
            var text when text.Contains("lego movie", StringComparison.Ordinal) =>
                new[] { "lego movie", "back to the future", "toy story", "wall-e", "spaceballs" },
            var text when text.Contains("wall e", StringComparison.Ordinal) ||
                          text.Contains("wall-e", StringComparison.Ordinal) =>
                new[] { "wall-e", "back to the future", "toy story", "spaceballs" },
            var text when text.Contains("spaceballs", StringComparison.Ordinal) =>
                new[] { "spaceballs", "back to the future", "toy story", "wall-e" },
            _ => new[] { "back to the future", "toy story", "wall-e", "spaceballs" }
        };

        return BuildScriptedPersonalityDecision(
            catalog,
            "robot_favorite_movie",
            preferredSnippets);
    }

    private JiboInteractionDecision BuildFavoriteBasketballTeamDecision(
        JiboExperienceCatalog catalog,
        string loweredTranscript)
    {
        var preferredSnippets = MatchesAny(loweredTranscript, "do you like basketball", "do you like basketball teams")
            ? new[] { "love the basketball itself", "favorite team for now", "similar to my head" }
            : new[] { "favorite team for now", "love the basketball itself", "similar to my head" };

        return BuildScriptedPersonalityDecision(
            catalog,
            "robot_favorite_basketball_team",
            preferredSnippets);
    }
}
