using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Runtime.Abstractions;
using System.Text.Json;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class JiboInteractionServiceTests
{
    [Fact]
    public async Task BuildDecisionAsync_Joke_UsesCatalogBackedRandomContent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me a joke",
            NormalizedTranscript = "tell me a joke"
        });

        Assert.Equal("joke", decision.IntentName);
        Assert.Equal("@be/joke", decision.SkillName);
        Assert.Equal("Why did the robot cross the road? Because it was programmed by the chicken.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Dance_UsesCatalogBackedAnimation()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do a dance",
            NormalizedTranscript = "do a dance"
        });

        Assert.Equal("dance", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        var catalog = await new InMemoryJiboExperienceContentRepository().GetCatalogAsync(); // Ensure catalog is loaded for test coverage
        Assert.Contains(decision.ReplyText, catalog.DanceReplies);
        Assert.Equal("<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, rom-upbeat' /></speak>", decision.SkillPayload!["esml"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TwerkQuestion_PrefersSpecificTwerkIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you twerk",
            NormalizedTranscript = "can you twerk"
        });

        Assert.Equal("twerk", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluAskForDate_MapsToDateIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "askForDate"
            }
        });

        Assert.Equal("date", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("askForDate", decision.SkillPayload!["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_YesNoFollowUp_MapsShortAffirmationToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yeah",
            NormalizedTranscript = "yeah",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "create/is_it_a_keeper" }
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_YesNoFollowUp_FromAsrHints_MapsShortDenialToNoIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "surprises-ota/want_to_download_now" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.Equal("No.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SharedYesNoPrompt_MapsShortAffirmationToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "shared/yes_no", "globals/gui_nav" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AlarmTimerChangePrompt_MapsShortAffirmationToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "clock/alarm_timer_change", "globals/gui_nav" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AlarmTimerNoneSetPrompt_MapsShortDenialToNoIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "clock/alarm_timer_none_set", "globals/global_commands_launch" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.Equal("No.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SettingsDownloadPrompt_MapsShortDenialToNoIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "No.",
            NormalizedTranscript = "No.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "settings/download_now_later", "globals/global_commands_launch" }
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.Equal("No.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SurprisesDateOfferPrompt_MapsShortAffirmationToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Yes!",
            NormalizedTranscript = "Yes!",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "surprises-date/offer_date_fact", "globals/global_commands_launch" },
                ["listenAsrHints"] = new[] { "$YESNO" }
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SkillPhraseVariant_MapsToKnownIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "make me laugh",
            NormalizedTranscript = "make me laugh"
        });

        Assert.Equal("joke", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenTheRadio_MapsToRadioLaunchIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open the radio",
            NormalizedTranscript = "open the radio"
        });

        Assert.Equal("radio", decision.IntentName);
        Assert.Equal("@be/radio", decision.SkillName);
        Assert.Equal("@be/radio", decision.SkillPayload!["skillId"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PlayCountryMusic_MapsToRadioGenreLaunchIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "play country music",
            NormalizedTranscript = "play country music"
        });

        Assert.Equal("radio_genre", decision.IntentName);
        Assert.Equal("@be/radio", decision.SkillName);
        Assert.Equal("Country", decision.SkillPayload!["station"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_StopThat_MapsToIdleStopCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "stop that",
            NormalizedTranscript = "stop that"
        });

        Assert.Equal("stop", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_NeverMindWithPunctuation_MapsToIdleStopCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Never mind.",
            NormalizedTranscript = "Never mind."
        });

        Assert.Equal("stop", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
    }

    [Fact]
    public async Task BuildDecisionAsync_TurnItUp_MapsToGlobalVolumeUpCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "turn it up",
            NormalizedTranscript = "turn it up"
        });

        Assert.Equal("volume_up", decision.IntentName);
        Assert.Equal("global_commands", decision.SkillName);
        Assert.Equal("volumeUp", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("null", decision.SkillPayload["volumeLevel"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetVolumeToSix_MapsToGlobalVolumeToValueCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set volume to six",
            NormalizedTranscript = "set volume to six"
        });

        Assert.Equal("volume_to_value", decision.IntentName);
        Assert.Equal("global_commands", decision.SkillName);
        Assert.Equal("volumeToValue", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("6", decision.SkillPayload["volumeLevel"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetVolumeTwoSix_UsesTrailingHomophoneLevel()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Set Volume 2-6.",
            NormalizedTranscript = "Set Volume 2-6."
        });

        Assert.Equal("volume_to_value", decision.IntentName);
        Assert.Equal("volumeToValue", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("6", decision.SkillPayload["volumeLevel"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ShowVolumeControls_MapsToSettingsVolumeQuery()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "show volume controls",
            NormalizedTranscript = "show volume controls"
        });

        Assert.Equal("volume_query", decision.IntentName);
        Assert.Equal("@be/settings", decision.SkillName);
        Assert.Equal("volumeQuery", decision.SkillPayload!["localIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenPhotogal_MapsToGalleryLaunch()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open the photogal",
            NormalizedTranscript = "open the photogal"
        });

        Assert.Equal("photo_gallery", decision.IntentName);
        Assert.Equal("@be/gallery", decision.SkillName);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenTimer_MapsToLocalClockTimerMenu()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open timer",
            NormalizedTranscript = "open timer"
        });

        Assert.Equal("timer_menu", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("timer", decision.SkillPayload!["domain"]);
        Assert.Equal("menu", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenClock_MapsToDirectClockView()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open the clock",
            NormalizedTranscript = "open the clock"
        });

        Assert.Equal("clock_open", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("clock", decision.SkillPayload!["domain"]);
        Assert.Equal("askForTime", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WhatTimeIsIt_MapsToLocalClockTimeIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what time is it",
            NormalizedTranscript = "what time is it"
        });

        Assert.Equal("time", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("askForTime", decision.SkillPayload!["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TodaysDate_MapsToLocalClockDateIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's today's date",
            NormalizedTranscript = "what's today's date"
        });

        Assert.Equal("date", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("askForDate", decision.SkillPayload!["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetTimerForFiveMinutes_MapsToClockStartIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set a timer for five minutes",
            NormalizedTranscript = "set a timer for five minutes"
        });

        Assert.Equal("timer_value", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("timer", decision.SkillPayload!["domain"]);
        Assert.Equal("start", decision.SkillPayload["clockIntent"]);
        Assert.Equal("0", decision.SkillPayload["hours"]);
        Assert.Equal("5", decision.SkillPayload["minutes"]);
        Assert.Equal("null", decision.SkillPayload["seconds"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForSevenThirtyAm_MapsToClockStartIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 7:30 am",
            NormalizedTranscript = "set an alarm for 7:30 am"
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("start", decision.SkillPayload["clockIntent"]);
        Assert.Equal("7:30", decision.SkillPayload["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForEightThirty_ParsesCompactTime()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 830",
            NormalizedTranscript = "set an alarm for 830"
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("8:30", decision.SkillPayload!["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForEightThirtySpokenDigits_ParsesSplitTime()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 8 30",
            NormalizedTranscript = "set an alarm for 8 30"
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("8:30", decision.SkillPayload!["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForTenTwentyFiveWithHyphen_ParsesSplitTime()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 10-25",
            NormalizedTranscript = "set an alarm for 10-25"
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("10:25", decision.SkillPayload!["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForTenTwentyFivePm_ParsesPmSuffix()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 10:25 pm",
            NormalizedTranscript = "set an alarm for 10:25 pm"
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("10:25", decision.SkillPayload!["time"]);
        Assert.Equal("pm", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForTenTwentyFiveSpacedPm_ParsesPmSuffix()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 10 25 p m",
            NormalizedTranscript = "set an alarm for 10 25 p m"
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("10:25", decision.SkillPayload!["time"]);
        Assert.Equal("pm", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForSevenEighteen_UsesNextOccurrenceFromContext()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 7:18",
            NormalizedTranscript = "set an alarm for 7:18",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-22T07:15:00-05:00"}}}"""
            }
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("7:18", decision.SkillPayload!["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmForSevenTen_UsesNextOccurrenceFromContext()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm for 7:10",
            NormalizedTranscript = "set an alarm for 7:10",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-22T07:15:00-05:00"}}}"""
            }
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("7:10", decision.SkillPayload!["time"]);
        Assert.Equal("pm", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TimerValueFollowUp_ParsesBareDuration()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "twenty five minutes",
            NormalizedTranscript = "twenty five minutes",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "clock/timer_set_value" }
            }
        });

        Assert.Equal("timer_value", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("start", decision.SkillPayload!["clockIntent"]);
        Assert.Equal("25", decision.SkillPayload["minutes"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_AlarmValueFollowUp_ParsesBareSpokenTime()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "ten twenty five",
            NormalizedTranscript = "ten twenty five",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "clock/alarm_set_value" }
            }
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("start", decision.SkillPayload!["clockIntent"]);
        Assert.Equal("10:25", decision.SkillPayload["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_AlarmValueFollowUp_ParsesCommaSeparatedSpokenDigits()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "7, 44",
            NormalizedTranscript = "7, 44",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "clock/alarm_set_value" },
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-26T07:43:00-05:00"}}}"""
            }
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("start", decision.SkillPayload!["clockIntent"]);
        Assert.Equal("7:44", decision.SkillPayload["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetAlarmWithoutTime_AsksForClarification()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set an alarm",
            NormalizedTranscript = "set an alarm"
        });

        Assert.Equal("alarm_clarify", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("set", decision.SkillPayload["clockIntent"]);
        Assert.Equal("What time should I set the alarm for?", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_CancelAlarm_MapsToClockDeleteIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "cancel alarm",
            NormalizedTranscript = "cancel alarm"
        });

        Assert.Equal("alarm_delete", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("delete", decision.SkillPayload["clockIntent"]);
    }

    [Theory]
    [InlineData("delete the alarm")]
    [InlineData("so, delete the alarm")]
    [InlineData("delete along")]
    [InlineData("so, delete the along")]
    public async Task BuildDecisionAsync_DeleteAlarmVariants_MapsToClockDeleteIntent(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("alarm_delete", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("delete", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluSetAlarmWithoutTime_AsksForClarification()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set",
            NormalizedTranscript = "set",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "set",
                ["clientEntities"] = new Dictionary<string, object?>
                {
                    ["domain"] = "alarm"
                },
                ["clientRules"] = new[] { "clock/clock_menu" }
            }
        });

        Assert.Equal("alarm_clarify", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("set", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluCancelFromAlarmQueryMenu_UsesLastClockDomain()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "cancel",
            NormalizedTranscript = "cancel",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "cancel",
                ["clientRules"] = new[] { "clock/alarm_timer_query_menu" },
                ["lastClockDomain"] = "alarm"
            }
        });

        Assert.Equal("alarm_delete", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("delete", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluCancelFromAlarmValuePrompt_MapsToClockCancelIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "cancel",
            NormalizedTranscript = "cancel",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "cancel",
                ["clientRules"] = new[] { "clock/alarm_set_value" }
            }
        });

        Assert.Equal("alarm_cancel", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("cancel", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SetTimerWithoutDuration_AsksForClarification()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set a timer",
            NormalizedTranscript = "set a timer"
        });

        Assert.Equal("timer_clarify", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("timer", decision.SkillPayload!["domain"]);
        Assert.Equal("set", decision.SkillPayload["clockIntent"]);
        Assert.Equal("How long should I set the timer for?", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenPhotoGallery_MapsToGalleryLaunch()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open photo gallery",
            NormalizedTranscript = "open photo gallery"
        });

        Assert.Equal("photo_gallery", decision.IntentName);
        Assert.Equal("@be/gallery", decision.SkillName);
        Assert.Equal("menu", decision.SkillPayload!["localIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SnapAPicture_MapsToCreateOnePhoto()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "snap a picture",
            NormalizedTranscript = "snap a picture"
        });

        Assert.Equal("snapshot", decision.IntentName);
        Assert.Equal("@be/create", decision.SkillName);
        Assert.Equal("createOnePhoto", decision.SkillPayload!["localIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_OpenPhotobooth_MapsToCreateSomePhotos()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "open photobooth",
            NormalizedTranscript = "open photobooth"
        });

        Assert.Equal("photobooth", decision.IntentName);
        Assert.Equal("@be/create", decision.SkillName);
        Assert.Equal("createSomePhotos", decision.SkillPayload!["localIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TellMeTheNews_UsesNimbusCloudSkillPath()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me the news",
            NormalizedTranscript = "tell me the news"
        });

        Assert.Equal("news", decision.IntentName);
        Assert.Equal("news", decision.SkillName);
        Assert.Equal("news", decision.SkillPayload!["skillId"]);
        Assert.Equal("news", decision.SkillPayload["cloudSkill"]);
        Assert.Equal("runtime-news", decision.SkillPayload["mim_id"]);
        Assert.DoesNotContain("future cloud integration", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CloudVersion_UsesSharedBuildInfo()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the cloud version",
            NormalizedTranscript = "what's the cloud version"
        });

        Assert.Equal("cloud_version", decision.IntentName);
        Assert.Equal(OpenJiboCloudBuildInfo.SpokenVersion, decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_UsesStructuredClientNluGuess()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "guess",
            NormalizedTranscript = "guess",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "guess",
                ["clientRules"] = new[] { "word-of-the-day/puzzle" },
                ["clientEntities"] = JsonDocument.Parse("""{"guess":"pastoral"}""").RootElement.Clone()
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard pastoral.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_UsesSpokenTranscriptDuringPuzzleTurn()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "pastoral",
            NormalizedTranscript = "pastoral",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "word-of-the-day/puzzle" }
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard pastoral.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayStartPhrase_MapsToSkillIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "start word of the day",
            NormalizedTranscript = "start word of the day"
        });

        Assert.Equal("word_of_the_day", decision.IntentName);
        Assert.Equal("Starting word of the day.", decision.ReplyText);
        Assert.Equal("@be/word-of-the-day", decision.SkillName);
        Assert.Equal("word-of-the-day", decision.SkillPayload!["domain"]);
        Assert.Equal("@be/word-of-the-day", decision.SkillPayload["skillId"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_LineNumberUsesListenHints()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Two.",
            NormalizedTranscript = "Two.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "word-of-the-day/puzzle" },
                ["listenAsrHints"] = new[] { "doodad", "pastoral", "escarpment" }
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard pastoral.", decision.ReplyText);
        Assert.Equal("@be/word-of-the-day", decision.SkillName);
        Assert.Equal("pastoral", decision.SkillPayload!["guess"]);
        Assert.Equal("@be/word-of-the-day", decision.SkillPayload["skillId"]);
        Assert.Equal("completion_only", decision.SkillPayload["cloudResponseMode"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_FuzzyMatchesClosestHint()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Haglet.",
            NormalizedTranscript = "Haglet.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = new[] { "word-of-the-day/puzzle" },
                ["listenAsrHints"] = new[] { "aglet", "hovel", "wisenheimer" }
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard aglet.", decision.ReplyText);
        Assert.Equal("aglet", decision.SkillPayload!["guess"]);
    }

    private static JiboInteractionService CreateService()
    {
        return new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer());
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[0];
        }
    }
}
