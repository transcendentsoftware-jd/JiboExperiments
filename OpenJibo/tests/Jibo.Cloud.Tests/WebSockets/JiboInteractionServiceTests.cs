using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using System.Text.Json;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class JiboInteractionServiceTests
{
    private const string PersonalReportStateKey = "personalReportState";
    private const string PersonalReportNoMatchCountKey = "personalReportNoMatchCount";
    private const string PersonalReportUserNameKey = "personalReportUserName";
    private const string PersonalReportUserVerifiedKey = "personalReportUserVerified";
    private const string PersonalReportWeatherEnabledKey = "personalReportWeatherEnabled";
    private const string PersonalReportCalendarEnabledKey = "personalReportCalendarEnabled";
    private const string PersonalReportCommuteEnabledKey = "personalReportCommuteEnabled";
    private const string PersonalReportNewsEnabledKey = "personalReportNewsEnabled";
    private const string ChitchatStateKey = "chitchatState";
    private const string ChitchatRouteKey = "chitchatRoute";
    private const string ChitchatEmotionKey = "chitchatEmotion";

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
    public async Task BuildDecisionAsync_DoYouLikeToDance_UsesQuestionReplyStyleInsteadOfTriggeringDanceAnimation()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you like to dance",
            NormalizedTranscript = "do you like to dance"
        });

        Assert.Equal("dance_question", decision.IntentName);
        Assert.Null(decision.SkillName);
        Assert.Equal("I love to dance. Tell me to dance and I will show you a move.", decision.ReplyText);
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
    public async Task BuildDecisionAsync_HowOldAreYou_UsesPersonaBirthdayForAgeReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how old are you",
            NormalizedTranscript = "how old are you",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-05-05T19:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("robot_age", decision.IntentName);
        Assert.Equal("I count March 22, 2026 as my birthday, so I am 1 month old.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WhenIsYourBirthday_UsesPersonaBirthdayReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "when's your birthday",
            NormalizedTranscript = "when's your birthday"
        });

        Assert.Equal("robot_birthday", decision.IntentName);
        Assert.Equal("My birthday is March 22, 2026.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WhatsYourBirthday_DoesNotFallThroughToDateIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's your birthday",
            NormalizedTranscript = "what's your birthday"
        });

        Assert.Equal("robot_birthday", decision.IntentName);
        Assert.Equal("My birthday is March 22, 2026.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_DoYouHaveAPersonality_UsesCatalogBackedPersonalityReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you have a personality",
            NormalizedTranscript = "do you have a personality"
        });

        Assert.Equal("robot_personality", decision.IntentName);
        Assert.Equal("I do. I am curious, playful, and always up for a new experiment.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Hello_RoutesThroughChitchatScriptedResponse()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "hello",
            NormalizedTranscript = "hello"
        });

        Assert.Equal("hello", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("complete", decision.ContextUpdates![ChitchatStateKey]);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates[ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_AreYouHappy_RoutesThroughEmotionQuerySplit()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "are you happy",
            NormalizedTranscript = "are you happy"
        });

        Assert.Equal("emotion_query", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionQuery", decision.ContextUpdates![ChitchatRouteKey]);
        Assert.Equal(string.Empty, decision.ContextUpdates[ChitchatEmotionKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_Smile_RoutesThroughEmotionCommandSplit()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "smile",
            NormalizedTranscript = "smile"
        });

        Assert.Equal("emotion_command", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.NotNull(decision.SkillPayload);
        Assert.Contains("cat='happy'", decision.SkillPayload!["esml"]?.ToString(), StringComparison.Ordinal);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionCommand", decision.ContextUpdates![ChitchatRouteKey]);
        Assert.Equal("happy", decision.ContextUpdates[ChitchatEmotionKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_UnhandledChat_RoutesThroughErrorResponseSplit()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "blargh",
            NormalizedTranscript = "blargh"
        });

        Assert.Equal("chat", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("ErrorResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_BirthdayMemory_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my birthday is April 12",
            NormalizedTranscript = "my birthday is April 12",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_birthday", setDecision.IntentName);
        Assert.Equal("Got it. I will remember your birthday is april 12.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "when is my birthday",
            NormalizedTranscript = "when is my birthday",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_birthday", recallDecision.IntentName);
        Assert.Equal("You told me your birthday is april 12.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_BirthdaySetAttemptWithoutValue_RoutesToBirthdayPrompt()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my birthday is",
            NormalizedTranscript = "my birthday is"
        });

        Assert.Equal("memory_set_birthday", decision.IntentName);
        Assert.Equal("I can remember it if you say, my birthday is March 14.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_PreferenceMemory_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my favorite music is jazz",
            NormalizedTranscript = "my favorite music is jazz",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_preference", setDecision.IntentName);
        Assert.Equal("Got it. I will remember your favorite music is jazz.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is my favorite music",
            NormalizedTranscript = "what is my favorite music",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_preference", recallDecision.IntentName);
        Assert.Equal("You told me your favorite music is jazz.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_PreferenceSetAttemptWithoutValue_RoutesToPreferencePrompt()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my favorite music is",
            NormalizedTranscript = "my favorite music is"
        });

        Assert.Equal("memory_set_preference", decision.IntentName);
        Assert.Equal("I can remember it if you say, my favorite music is jazz.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_PreferenceSetAttemptSportWithoutValue_RoutesToPreferencePrompt()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my favorite sport.",
            NormalizedTranscript = "my favorite sport."
        });

        Assert.Equal("memory_set_preference", decision.IntentName);
        Assert.Equal("I can remember it if you say, my favorite music is jazz.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_PreferenceRecallAttemptWithoutCategory_RoutesToRecallPrompt()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's my favorite",
            NormalizedTranscript = "what's my favorite"
        });

        Assert.Equal("memory_get_preference", decision.IntentName);
        Assert.Equal("Ask me like this: what is my favorite music?", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalMemory_IsTenantScoped()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my birthday is April 12",
            NormalizedTranscript = "my birthday is April 12",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        var otherTenantRecall = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is my birthday",
            NormalizedTranscript = "what is my birthday",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-b",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-b"
        });

        Assert.Equal("memory_get_birthday", otherTenantRecall.IntentName);
        Assert.Equal("I do not know your birthday yet. You can say, my birthday is March 14.", otherTenantRecall.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_NameMemory_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my name is Alex",
            NormalizedTranscript = "my name is Alex",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_name", setDecision.IntentName);
        Assert.Equal("Nice to meet you, alex. I will remember your name.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is my name",
            NormalizedTranscript = "what is my name",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_name", recallDecision.IntentName);
        Assert.Equal("You told me your name is alex.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ImportantDateMemory_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "our anniversary is June 10",
            NormalizedTranscript = "our anniversary is June 10",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_important_date", setDecision.IntentName);
        Assert.Equal("Got it. I will remember your anniversary is june 10.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "when is our anniversary",
            NormalizedTranscript = "when is our anniversary",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_important_date", recallDecision.IntentName);
        Assert.Equal("You told me your anniversary is june 10.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AffinityMemory_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "I dislike mushrooms",
            NormalizedTranscript = "I dislike mushrooms",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_affinity", setDecision.IntentName);
        Assert.Equal("Got it. I will remember you dislike mushrooms.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do i dislike mushrooms",
            NormalizedTranscript = "do i dislike mushrooms",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_affinity", recallDecision.IntentName);
        Assert.Equal("Yes. You told me you dislike mushrooms.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_PreferenceReversePhrase_ParsesFavoriteVariant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "pizza is my favorite food",
            NormalizedTranscript = "pizza is my favorite food",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_preference", setDecision.IntentName);
        Assert.Equal("Got it. I will remember your favorite food is pizza.", setDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Surprise_WithPizzaPreference_UsesPizzaProactivity()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my favorite food is pizza",
            NormalizedTranscript = "my favorite food is pizza",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("proactive_pizza_preference", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("RA_JBO_MakePizza", decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_Surprise_OnNationalPizzaDay_UsesHolidayProactivity()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-02-09T10:45:00-06:00"}}}"""
            }
        });

        Assert.Equal("proactive_pizza_day", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Contains("National Pizza Day", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_PendingPizzaFactOffer_YesMapsToFact()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                ["pendingProactivityOffer"] = "pizza_fact"
            }
        });

        Assert.Equal("proactive_pizza_fact", decision.IntentName);
        Assert.Contains("350 slices per second", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_PendingPizzaFactOffer_NoMapsToDecline()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["pendingProactivityOffer"] = "pizza_fact"
            }
        });

        Assert.Equal("proactive_offer_declined", decision.IntentName);
        Assert.Equal("No problem. We can save the pizza fact for another time.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_MakePizza_UsesOriginalMimStylePayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "make a pizza",
            NormalizedTranscript = "make a pizza"
        });

        Assert.Equal("pizza", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("One pizza, coming right up.", decision.ReplyText);
        Assert.Equal("RA_JBO_MakePizza", decision.SkillPayload!["mim_id"]);
        Assert.Equal("RA_JBO_ShowPizzaMaking_AN_01", decision.SkillPayload["prompt_id"]);
        Assert.Contains("pizza-making", decision.SkillPayload["esml"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluRequestMakePizza_UsesOriginalMimStylePayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "requestMakePizza",
            NormalizedTranscript = "requestMakePizza",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "requestMakePizza"
            }
        });

        Assert.Equal("pizza", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("RA_JBO_MakePizza", decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouOrderPizza_UsesLegacyOrderPizzaMimPayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you order pizza",
            NormalizedTranscript = "can you order pizza"
        });

        Assert.Equal("order_pizza", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("RA_JBO_OrderPizza", decision.SkillPayload!["mim_id"]);
        Assert.Equal("RA_JBO_OrderPizza_AN_01", decision.SkillPayload["prompt_id"]);
        Assert.Contains("I can't do that yet", decision.SkillPayload["esml"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDecisionAsync_OrderAPizza_UsesLegacyOrderPizzaMimPayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "order a pizza",
            NormalizedTranscript = "order a pizza"
        });

        Assert.Equal("order_pizza", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("RA_JBO_OrderPizza", decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouOrderAPizzaWithPunctuation_UsesLegacyOrderPizzaMimPayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Can you order a pizza?",
            NormalizedTranscript = "Can you order a pizza?"
        });

        Assert.Equal("order_pizza", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("RA_JBO_OrderPizza", decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluRequestOrderPizza_UsesLegacyOrderPizzaMimPayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "requestOrderPizza",
            NormalizedTranscript = "requestOrderPizza",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "requestOrderPizza"
            }
        });

        Assert.Equal("order_pizza", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("RA_JBO_OrderPizza", decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_StartsOptInStateMachine()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "personal report",
            NormalizedTranscript = "personal report"
        });

        Assert.Equal("personal_report_opt_in", decision.IntentName);
        Assert.Equal("Would you like your personal report now?", decision.ReplyText);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("awaiting_opt_in", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportWeatherEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportCalendarEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportCommuteEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportNewsEnabledKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_OptInYesWithKnownName_AsksForIdentityConfirmation()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-a", "loop-a", "device-a"), "alex");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            DeviceId = "device-a",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a",
                [PersonalReportStateKey] = "awaiting_opt_in"
            }
        });

        Assert.Equal("personal_report_verify_user", decision.IntentName);
        Assert.Equal("I think this is alex. Is that right?", decision.ReplyText);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("awaiting_identity_confirmation", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal("alex", decision.ContextUpdates[PersonalReportUserNameKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_VerifiedIdentity_DeliversReportAndResetsState()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, US", "light rain", 61, 65, 54, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                [PersonalReportStateKey] = "awaiting_identity_confirmation",
                [PersonalReportUserNameKey] = "alex"
            }
        });

        Assert.Equal("personal_report_delivered", decision.IntentName);
        Assert.Contains("Great, alex. Here is your personal report.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Right now in Boston, US, it is light rain and 61 degrees Fahrenheit.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("That is your personal report.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("idle", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportUserVerifiedKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_NoMatchRetriesThenDeclines()
    {
        var service = CreateService();

        var firstDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "maybe",
            NormalizedTranscript = "maybe",
            Attributes = new Dictionary<string, object?>
            {
                [PersonalReportStateKey] = "awaiting_opt_in",
                [PersonalReportNoMatchCountKey] = 0
            }
        });

        Assert.Equal("personal_report_no_match", firstDecision.IntentName);
        Assert.NotNull(firstDecision.ContextUpdates);
        Assert.Equal(1, firstDecision.ContextUpdates![PersonalReportNoMatchCountKey]);

        var secondDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "maybe",
            NormalizedTranscript = "maybe",
            Attributes = new Dictionary<string, object?>
            {
                [PersonalReportStateKey] = "awaiting_opt_in",
                [PersonalReportNoMatchCountKey] = 1
            }
        });

        Assert.Equal("personal_report_declined", secondDecision.IntentName);
        Assert.NotNull(secondDecision.ContextUpdates);
        Assert.Equal("idle", secondDecision.ContextUpdates![PersonalReportStateKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_StartCanApplyToggleHints()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "personal report without weather and no news",
            NormalizedTranscript = "personal report without weather and no news"
        });

        Assert.Equal("personal_report_opt_in", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal(false, decision.ContextUpdates![PersonalReportWeatherEnabledKey]);
        Assert.Equal(false, decision.ContextUpdates[PersonalReportNewsEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportCalendarEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportCommuteEnabledKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherQuery_WithoutProvider_UsesSpokenFallback()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how is the weather",
            NormalizedTranscript = "how is the weather"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Null(decision.SkillName);
        Assert.Null(decision.SkillPayload);
        Assert.Equal("I can check weather once my weather service is connected.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherTomorrowQuery_WithoutProvider_StillReturnsFallback()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather tomorrow",
            NormalizedTranscript = "what's the weather tomorrow"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("I can check weather once my weather service is connected.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherConditionQuery_WithoutProvider_StillReturnsFallback()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "will it rain tomorrow",
            NormalizedTranscript = "will it rain tomorrow"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("I can check weather once my weather service is connected.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluRequestWeatherPR_WithoutProvider_StillReturnsFallback()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "requestWeatherPR",
            NormalizedTranscript = "requestWeatherPR",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "requestWeatherPR"
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("I can check weather once my weather service is connected.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherQuery_WithProvider_UsesProviderSummary()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, US", "light rain", 61, 65, 54, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how is the weather",
            NormalizedTranscript = "how is the weather"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Null(decision.SkillName);
        Assert.Null(decision.SkillPayload);
        Assert.Equal("Right now in Boston, US, it is light rain and 61 degrees Fahrenheit.", decision.ReplyText);
        Assert.NotNull(provider.LastRequest);
        Assert.False(provider.LastRequest!.IsTomorrow);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherLocationTomorrow_WithProvider_PassesLocationAndTomorrowRequest()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, US", "mostly cloudy", 72, 74, 60, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in chicago tomorrow",
            NormalizedTranscript = "what's the weather in chicago tomorrow"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Chicago", provider.LastRequest?.LocationQuery);
        Assert.True(provider.LastRequest?.IsTomorrow);
        Assert.Equal("Tomorrow in Chicago, US, expect mostly cloudy with a high near 74 degrees Fahrenheit and a low around 60 degrees Fahrenheit.", decision.ReplyText);
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
    public async Task BuildDecisionAsync_ClientNluAskForDate_WithBirthdayTranscript_PrefersRobotBirthdayIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's your birthday",
            NormalizedTranscript = "what's your birthday",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "askForDate"
            }
        });

        Assert.Equal("robot_birthday", decision.IntentName);
        Assert.Equal("My birthday is March 22, 2026.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClientNluAskForDate_WithPrefixBirthdayTranscript_PrefersRobotBirthdayIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "so what's your birthday",
            NormalizedTranscript = "so what's your birthday",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "askForDate"
            }
        });

        Assert.Equal("robot_birthday", decision.IntentName);
        Assert.Equal("My birthday is March 22, 2026.", decision.ReplyText);
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
                ["listenRules"] = (string[])["create/is_it_a_keeper"]
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
                ["listenRules"] = (string[])["surprises-ota/want_to_download_now"],
                ["listenAsrHints"] = (string[])["$YESNO"]
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
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
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
                ["listenRules"] = (string[])["clock/alarm_timer_change", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
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
                ["listenRules"] = (string[])["clock/alarm_timer_none_set", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"]
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
                ["listenRules"] = (string[])["settings/download_now_later", "globals/global_commands_launch"]
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
                ["listenRules"] = (string[])["surprises-date/offer_date_fact", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"]
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
                ["listenRules"] = (string[])["clock/timer_set_value"]
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
                ["listenRules"] = (string[])["clock/alarm_set_value"]
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
                ["listenRules"] = (string[])["clock/alarm_set_value"],
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
                ["clientRules"] = (string[])["clock/clock_menu"]
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
                ["clientRules"] = (string[])["clock/alarm_timer_query_menu"],
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
                ["clientRules"] = (string[])["clock/alarm_set_value"]
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
        Assert.DoesNotContain("Jibo", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
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
                ["clientRules"] = (string[])["word-of-the-day/puzzle"],
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
                ["listenRules"] = (string[])["word-of-the-day/puzzle"]
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
                ["listenRules"] = (string[])["word-of-the-day/puzzle"],
                ["listenAsrHints"] = (string[])["doodad", "pastoral", "escarpment"]
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
                ["listenRules"] = (string[])["word-of-the-day/puzzle"],
                ["listenAsrHints"] = (string[])["aglet", "hovel", "wisenheimer"]
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard aglet.", decision.ReplyText);
        Assert.Equal("aglet", decision.SkillPayload!["guess"]);
    }

    private static JiboInteractionService CreateService(
        IPersonalMemoryStore? personalMemoryStore = null,
        IWeatherReportProvider? weatherReportProvider = null)
    {
        return new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer(),
            personalMemoryStore ?? new InMemoryPersonalMemoryStore(),
            weatherReportProvider);
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[0];
        }
    }

    private sealed class CapturingWeatherReportProvider : IWeatherReportProvider
    {
        public WeatherReportRequest? LastRequest { get; private set; }

        public WeatherReportSnapshot? Snapshot { get; init; }

        public Task<WeatherReportSnapshot?> GetReportAsync(
            WeatherReportRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Snapshot);
        }
    }
}
