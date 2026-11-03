using Jibo.Cloud.Tests.Application;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Commute;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.WebSockets;

[Collection("Environment variable tests")]
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
    private const string HouseholdListStateKey = "householdListState";
    private const string HouseholdListTypeKey = "householdListType";
    private const string HouseholdListDisplayTypeKey = "householdListDisplayType";
    private const string HouseholdListNoMatchCountKey = "householdListNoMatchCount";
    private const string HouseholdListNoInputCountKey = "householdListNoInputCount";
    private const string ChitchatStateKey = "chitchatState";
    private const string ChitchatRouteKey = "chitchatRoute";
    private const string ChitchatEmotionKey = "chitchatEmotion";
    private const string GreetingRouteKey = "greetingsRoute";
    private const string GreetingSpeakerKey = "greetingsSpeaker";
    private const string GreetingLastProactiveUtcKey = "greetingsLastProactiveUtc";
    private const string GreetingLastReactiveUtcKey = "greetingsLastReactiveUtc";

    [Theory]
    [InlineData("tell me a joke", "joke")]
    [InlineData("what time is it", "time")]
    [InlineData("what are your plans for christmas", "seasonal_plans_for_christmas")]
    public async Task BuildDecisionAsync_HolidayLookupIsNotRequiredForUnrelatedTurns(string transcript, string intent)
    {
        var store = new Mock<ICloudStateStore>();
        store.Setup(value => value.GetHolidays(It.IsAny<string?>()))
            .Throws(new InvalidOperationException("An unrelated turn must not wait for the holiday provider."));
        var service = CreateService(cloudStateStore: store.Object);
        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript, NormalizedTranscript = transcript,
            Attributes = new Dictionary<string, object?> { ["accountId"] = "test-account", ["loopId"] = "test-loop" }
        });
        Assert.Equal(intent, decision.IntentName);
        store.Verify(value => value.GetHolidays(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task BuildDecisionAsync_HolidayLookupRemainsScopedForGreeting()
    {
        var store = new Mock<ICloudStateStore>();
        store.Setup(value => value.GetHolidays("test-loop")).Returns([
            new HolidayRecord { Name = "Christmas Day", Date = new DateOnly(2026,12,25), IsEnabled = true }
        ]);
        var service = CreateService(cloudStateStore: store.Object);
        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "merry christmas", NormalizedTranscript = "merry christmas",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "test-account", ["loopId"] = "test-loop",
                ["context"] = """{"runtime":{"location":{"iso":"2026-12-25T12:00:00-05:00"}}}"""
            }
        });
        Assert.Equal("seasonal_holiday_greeting", decision.IntentName);
        Assert.Equal("HolidayResponse", decision.ContextUpdates![ChitchatRouteKey]);
        store.Verify(value => value.GetHolidays("test-loop"), Times.Once);
        store.Verify(value => value.GetHolidays(It.Is<string?>(id => id != "test-loop")), Times.Never);
    }

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
    public async Task BuildDecisionAsync_RosterPersistenceFailure_DoesNotTerminateSpeechTurn()
    {
        var cloudStateStore = new Mock<ICloudStateStore>();
        cloudStateStore
            .Setup(store => store.AddLoop(null, "tenant-account", "Royal-Current-Sage-Canvas",
                "Royal-Current-Sage-Canvas"))
            .Throws(new InvalidOperationException("Loop scope was not found."));
        cloudStateStore.Setup(store => store.GetHolidays(It.IsAny<string?>())).Returns([]);
        var service = CreateService(cloudStateStore: cloudStateStore.Object);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me a joke",
            NormalizedTranscript = "tell me a joke",
            DeviceId = "Royal-Current-Sage-Canvas",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "tenant-account",
                ["robotFriendlyId"] = "Royal-Current-Sage-Canvas",
                ["context"] =
                    """{"runtime":{"loop":{"users":[{"id":"person-1","firstName":"Ada","type":"owner"}]}}}"""
            }
        });

        Assert.Equal("joke", decision.IntentName);
        Assert.Equal("Why did the robot cross the road? Because it was programmed by the chicken.", decision.ReplyText);
        cloudStateStore.Verify(store => store.AddLoop(null, "tenant-account", "Royal-Current-Sage-Canvas",
            "Royal-Current-Sage-Canvas"), Times.Once);
    }

    [Theory]
    [InlineData("knock knock")]
    [InlineData("nock nock")]
    public async Task BuildDecisionAsync_KnockKnockVariants_RouteToJoke(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("joke", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_FunFact_UsesApiFactWhenProviderReturnsOne()
    {
        var service = CreateService(funFactProvider: new StubFunFactProvider("Switzerland is the only country with a square flag."));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "hey jibo tell me a fun fact",
            NormalizedTranscript = "hey jibo tell me a fun fact"
        });

        Assert.Equal("fun_fact", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("Switzerland is the only country with a square flag.", decision.ReplyText);
        Assert.Equal("fun_fact", decision.SkillPayload!["replyType"]);
        Assert.Equal("fun_fact", decision.SkillPayload["factCategory"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_FunFact_FallsBackToCatalogWhenProviderReturnsNull()
    {
        var service = CreateService(funFactProvider: new StubFunFactProvider(null));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me a fun fact",
            NormalizedTranscript = "tell me a fun fact"
        });

        Assert.Equal("fun_fact", decision.IntentName);
        Assert.Equal("A shrimp's heart is in its head.", decision.ReplyText);
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
        var catalog =
            await new InMemoryJiboExperienceContentRepository()
                .GetCatalogAsync(); // Ensure catalog is loaded for test coverage
        Assert.Contains(decision.ReplyText, catalog.DanceReplies);
        Assert.Equal(
            "<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, rom-upbeat' /></speak>",
            decision.SkillPayload!["esml"]);
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

        Assert.Equal("robot_how_old_are_you", decision.IntentName);
        Assert.DoesNotContain("birthday", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old ago", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old old", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            decision.ReplyText.Contains("first powered up", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains("who's counting", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains("old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildDecisionAsync_HowOldAreYou_UsesRobotBirthdateAndSkipsBirthdayLine()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how old are you",
            NormalizedTranscript = "how old are you",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-07-26T19:00:00-04:00"},"loop":{"jibo":{"id":"robot-1","birthdate":1544234645598}}}}"""
            }
        });

        Assert.Equal("robot_how_old_are_you", decision.IntentName);
        Assert.DoesNotContain("birthday", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old ago", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old old", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            decision.ReplyText.Contains("December 8, 2018", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains(" days", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains("I'm 7", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains("I'm 7.", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains(" minutes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildDecisionAsync_HowOldAreYou_OnBirthdayAllowsBirthdayLine()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how old are you",
            NormalizedTranscript = "how old are you",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-12-08T19:00:00-05:00"},"loop":{"jibo":{"id":"robot-1","birthdate":1544234645598}}}}"""
            }
        });

        Assert.Equal("robot_how_old_are_you", decision.IntentName);
        Assert.Contains("birthday", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old ago", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old old", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildDecisionAsync_WhenIsYourBirthday_UsesRobotBirthdateFromContext()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "when's your birthday",
            NormalizedTranscript = "when's your birthday",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] =
                    """{"runtime":{"loop":{"jibo":{"id":"robot-1","birthdate":1544234645598}}}}"""
            }
        });

        Assert.Equal("robot_birthday", decision.IntentName);
        Assert.Equal("My birthday is December 8, 2018.", decision.ReplyText);
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
    public async Task BuildDecisionAsync_WhatsYourBday_DoesNotFallThroughToDateIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's your bday",
            NormalizedTranscript = "what's your bday"
        });

        Assert.Equal("robot_birthday", decision.IntentName);
        Assert.Equal("My birthday is March 22, 2026.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_GoodMorning_UsesReactiveGreetingWithRememberedName()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var cloudStateStore = new InMemoryCloudStateStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-a", "loop-a", "device-a"), "jake");
        var service = CreateService(memoryStore, cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "good morning",
            NormalizedTranscript = "good morning",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-07-27T08:00:00-04:00"},"perception":{"speaker":"person-a","peoplePresent":[{"id":"person-a"}]},"loop":{"users":[{"id":"person-a","firstName":"jake"}]}}}"""
            },
            DeviceId = "device-a"
        });

        Assert.Equal("good_morning", decision.IntentName);
        Assert.Contains("morning", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("ReactiveGreeting", decision.ContextUpdates![GreetingRouteKey]);
        Assert.Equal("person-a", decision.ContextUpdates[GreetingSpeakerKey]);
        Assert.True(DateTimeOffset.TryParse(decision.ContextUpdates[GreetingLastReactiveUtcKey]?.ToString(), out _));
        Assert.Contains(cloudStateStore.GetGreetingPresences("loop-a"),
            greeting => greeting.PersonId == "person-a" &&
                        greeting is { LastGreetingRoute: "ReactiveGreeting", LastGreetingIntent: "good_morning" });
    }

    [Fact]
    public async Task BuildDecisionAsync_GoodMorning_UsesPersonScopedNameWhenSpeakerIsKnown()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-a", "loop-a", "device-a", "person-1"), "alex");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "good morning",
            NormalizedTranscript = "good morning",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-07-27T08:00:00-04:00"},"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}"""
            },
            DeviceId = "device-a"
        });

        Assert.Equal("good_morning", decision.IntentName);
        Assert.Contains("morning", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("good morning", "2026-07-27T08:00:00-04:00", "ReactiveGreeting", "Good morning")]
    [InlineData("good morning", "2026-07-27T20:00:00-04:00", "PartOfDayCorrection", "any time of day")]
    [InlineData("good afternoon", "2026-07-27T15:00:00-04:00", "ReactiveGreeting", "Good afternoon")]
    [InlineData("good afternoon", "2026-07-27T21:00:00-04:00", "PartOfDayCorrection", "afternoon somewhere")]
    [InlineData("good evening", "2026-07-27T08:00:00-04:00", "PartOfDayCorrection", "don't think it's evening")]
    [InlineData("good night", "2026-07-27T08:00:00-04:00", "ReactiveGreeting", "Good night")]
    public async Task BuildDecisionAsync_TimeGreetings_UsePartOfDayCorrectionWhenClaimDoesNotMatch(
        string transcript,
        string localIsoTime,
        string expectedRoute,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = $"{{\"runtime\":{{\"location\":{{\"iso\":\"{localIsoTime}\"}}}}}}"
            }
        });

        Assert.Contains(expectedReplySnippet, decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedRoute, decision.ContextUpdates![GreetingRouteKey]);
    }

    [Theory]
    [InlineData("merry christmas", "2026-07-27T12:00:00-04:00", "NotHoliday", "isn't Christmastime")]
    [InlineData("happy holidays", "2026-07-27T12:00:00-04:00", "NotHoliday", "don't think that's today")]
    [InlineData("merry christmas", "2026-12-25T12:00:00-05:00", "HolidayResponse", "Merry Christmas to you too")]
    [InlineData("merry christmas", "2026-12-20T12:00:00-05:00", "NotHoliday", "You too")]
    [InlineData("happy thanksgiving", "2026-07-27T12:00:00-04:00", "NotHoliday", "Thanksgiving")]
    public async Task BuildDecisionAsync_HolidayGreetings_UseNotHolidayWhenClaimDoesNotMatchToday(
        string transcript,
        string localIsoTime,
        string expectedRoute,
        string expectedReplySnippet)
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        var service = CreateService(cloudStateStore: cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = $"{{\"runtime\":{{\"location\":{{\"iso\":\"{localIsoTime}\"}}}}}}"
            }
        });

        Assert.Equal("seasonal_holiday_greeting", decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(
            decision,
            "seasonal_holiday_greeting",
            expectedReplySnippet);
        Assert.Equal(expectedRoute, decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WhoAmI_UsesPersonScopedNameWhenSpeakerIsKnown()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-b", "loop-b", "device-b", "person-2"), "sam");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "who am i",
            NormalizedTranscript = "who am i",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-b",
                ["loopId"] = "loop-b",
                ["context"] =
                    """{"runtime":{"perception":{"speaker":"person-2"},"loop":{"users":[{"id":"person-2","firstName":"sam"}]}}}"""
            },
            DeviceId = "device-b"
        });

        Assert.Equal("memory_get_name", decision.IntentName);
        Assert.Equal("I think you are Sam.", decision.ReplyText);
    }

    [Theory]
    [InlineData("do you know me")]
    [InlineData("do you remember me")]
    [InlineData("who is this")]
    [InlineData("can you recognize me")]
    public async Task BuildDecisionAsync_IdentityFollowUp_UsesPersonScopedNameWhenSpeakerIsKnown(string transcript)
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-c", "loop-c", "device-c", "person-3"), "taylor");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-c",
                ["loopId"] = "loop-c",
                ["context"] =
                    """{"runtime":{"perception":{"speaker":"person-3"},"loop":{"users":[{"id":"person-3","firstName":"taylor"}]}}}"""
            },
            DeviceId = "device-c"
        });

        Assert.Equal("memory_get_name", decision.IntentName);
        Assert.Equal("I think you are Taylor.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_IdentityFollowUp_RequestsNameWhenSpeakerIsUnknown()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you know me",
            NormalizedTranscript = "do you know me"
        });

        Assert.Equal("memory_get_name", decision.IntentName);
        Assert.Equal("I do not know your name yet. You can say, my name is Alex.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_IdentityFollowUp_DoesNotGuessFromLoopFirstNameWhenMemoryIsMissing()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "who am i",
            NormalizedTranscript = "who am i",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-d",
                ["loopId"] = "loop-d",
                ["context"] =
                    """
                    {"runtime":{"perception":{"speaker":"person-9"},"loop":{"users":[{"id":"person-9","firstName":"hi"}]}}}
                    """
            },
            DeviceId = "device-d"
        });

        Assert.Equal("memory_get_name", decision.IntentName);
        Assert.Equal("I do not know your name yet. You can say, my name is Alex.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerWithKnownIdentity_BuildsProactiveGreetingAndContext()
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        var service = CreateService(cloudStateStore: cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-05-21T15:00:00-05:00"},"perception":{"speaker":"person-1","peoplePresent":[{"id":"person-1"}]},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}"""
            }
        });

        Assert.Equal("proactive_greeting", decision.IntentName);
        Assert.Contains("Jake", decision.ReplyText, StringComparison.Ordinal);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("ProactiveGreeting", decision.ContextUpdates![GreetingRouteKey]);
        Assert.Equal("person-1", decision.ContextUpdates[GreetingSpeakerKey]);
        Assert.True(DateTimeOffset.TryParse(decision.ContextUpdates[GreetingLastProactiveUtcKey]?.ToString(), out _));
        Assert.Contains(cloudStateStore.GetGreetingPresences("openjibo-default-loop"),
            greeting => greeting.PersonId == "person-1" &&
                        greeting is
                        {
                            LastGreetingRoute: "ProactiveGreeting", LastGreetingIntent: "proactive_greeting"
                        });
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerWithMultiplePeople_DoesNotBorrowLoopFirstName()
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        var service = CreateService(cloudStateStore: cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-05-21T15:00:00-05:00"},"perception":{"speaker":"person-1","peoplePresent":[{"id":"person-1"},{"id":"person-2"}]},"loop":{"users":[{"id":"person-1","firstName":"jake"},{"id":"person-2","firstName":"sam"}]}}}"""
            }
        });

        Assert.Equal("proactive_greeting", decision.IntentName);
        Assert.DoesNotContain("Jake", decision.ReplyText, StringComparison.Ordinal);
        Assert.DoesNotContain("Sam", decision.ReplyText, StringComparison.Ordinal);
        Assert.Contains("I am glad to see you", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerInTheMorning_UsesGoodMorningProactiveTone()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-morning", "loop-morning", "device-morning", "person-9"),
            "jake");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-morning",
                ["loopId"] = "loop-morning",
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-05-21T09:00:00-05:00"},"perception":{"speaker":"person-9","peoplePresent":[{"id":"person-9"}]},"loop":{"users":[{"id":"person-9","firstName":"jake"}]}}}"""
            },
            DeviceId = "device-morning"
        });

        Assert.Equal("proactive_greeting", decision.IntentName);
        Assert.Contains("Good morning, Jake", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("welcome back", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerWithRecentGreetingHistory_UsesWelcomeBackTone()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-return", "loop-return", "device-return", "person-11"),
            "jake");
        var cloudStateStore = new InMemoryCloudStateStore();
        cloudStateStore.UpsertGreetingPresence(new GreetingPresenceRecord
        {
            LoopId = "loop-return",
            PersonId = "person-11",
            SpeakerId = "person-11",
            PreferredName = "Jake",
            LastSeenUtc = DateTimeOffset.UtcNow.AddHours(-1),
            LastGreetedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            LastGreetingRoute = "ProactiveGreeting",
            LastGreetingIntent = "proactive_greeting"
        });
        var service = CreateService(memoryStore, cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-return",
                ["loopId"] = "loop-return",
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-05-21T15:00:00-05:00"},"perception":{"speaker":"person-11","peoplePresent":[{"id":"person-11"}]},"loop":{"users":[{"id":"person-11","firstName":"jake"}]}}}"""
            },
            DeviceId = "device-return"
        });

        Assert.Equal("proactive_greeting", decision.IntentName);
        Assert.Contains("Welcome back, Jake", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("again", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerOnBirthday_BuildsBirthdayGreeting()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-bday", "loop-bday", "device-bday", "person-7"),
            "jake");
        memoryStore.SetBirthday(new PersonalMemoryTenantScope("acct-bday", "loop-bday", "device-bday", "person-7"),
            "March 14");
        var cloudStateStore = new InMemoryCloudStateStore();
        var service = CreateService(memoryStore, cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-bday",
                ["loopId"] = "loop-bday",
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-03-14T09:00:00-05:00"},"perception":{"speaker":"person-7","peoplePresent":[{"id":"person-7"}]},"loop":{"users":[{"id":"person-7","firstName":"jake"}]}}}"""
            },
            DeviceId = "device-bday"
        });

        Assert.Equal("proactive_birthday_greeting", decision.IntentName);
        Assert.Contains("Happy birthday", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jake", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ProactiveBirthdayGreeting", decision.ContextUpdates![GreetingRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerOnHoliday_BuildsHolidayGreeting()
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        cloudStateStore.UpsertHoliday(new HolidayRecord
        {
            LoopId = "loop-holiday",
            Name = "Christmas",
            Category = "holiday",
            Date = new DateOnly(2026, 12, 25),
            IsEnabled = true,
            Source = "manual",
            CountryCode = "US"
        });
        var service = CreateService(cloudStateStore: cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-holiday",
                ["loopId"] = "loop-holiday",
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-12-25T09:00:00-05:00"},"perception":{"speaker":"person-8","peoplePresent":[{"id":"person-8"}]},"loop":{"users":[{"id":"person-8","firstName":"jake"}]}}}"""
            }
        });

        Assert.Equal("proactive_holiday_greeting", decision.IntentName);
        Assert.Contains("Happy holidays", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ProactiveHolidayGreeting", decision.ContextUpdates![GreetingRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerUsesHolidayGreetingOnlyOnMatchingFixedDate()
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        cloudStateStore.UpsertHoliday(new HolidayRecord
        {
            LoopId = "loop-fixed-holiday",
            Name = "Test Holiday",
            Category = "holiday",
            Date = new DateOnly(2026, 8, 13),
            IsEnabled = true,
            Source = "manual",
            CountryCode = "US"
        });
        var service = CreateService(cloudStateStore: cloudStateStore);

        var ordinaryDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-fixed-holiday",
                ["loopId"] = "loop-fixed-holiday",
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-08-12T09:00:00-05:00"},"perception":{"speaker":"person-8","peoplePresent":[{"id":"person-8"}]},"loop":{"users":[{"id":"person-8","firstName":"jake"}]}}}"""
            }
        });

        var holidayDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-fixed-holiday",
                ["loopId"] = "loop-fixed-holiday",
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"location":{"iso":"2026-08-13T09:00:00-05:00"},"perception":{"speaker":"person-9","peoplePresent":[{"id":"person-9"}]},"loop":{"users":[{"id":"person-9","firstName":"sam"}]}}}"""
            }
        });

        Assert.Equal("proactive_greeting", ordinaryDecision.IntentName);
        Assert.Equal("ProactiveGreeting", ordinaryDecision.ContextUpdates![GreetingRouteKey]);
        Assert.Equal("proactive_holiday_greeting", holidayDecision.IntentName);
        Assert.Equal("ProactiveHolidayGreeting", holidayDecision.ContextUpdates![GreetingRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerWithKnownIdentity_SuppressesRepeatGreetingFromCloudHistory()
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        cloudStateStore.UpsertGreetingPresence(new GreetingPresenceRecord
        {
            LoopId = "loop-history",
            PersonId = "person-1",
            SpeakerId = "person-1",
            LastSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastGreetedUtc = DateTimeOffset.UtcNow,
            LastGreetingRoute = "ProactiveGreeting",
            LastGreetingIntent = "proactive_greeting"
        });
        var service = CreateService(cloudStateStore: cloudStateStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["loopId"] = "loop-history",
                ["context"] =
                    """{"runtime":{"perception":{"speaker":"person-1","peoplePresent":[{"id":"person-1"}]},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}"""
            }
        });

        Assert.Equal("trigger_ignored", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerFromSurprise_ReturnsSilentTriggerIgnoredDecision()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "SURPRISE",
                ["context"] =
                    """{"runtime":{"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}"""
            }
        });

        Assert.Equal("trigger_ignored", decision.IntentName);
        Assert.Equal(string.Empty, decision.ReplyText);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.NotNull(decision.SkillPayload);
        Assert.Equal("completion_only", decision.SkillPayload!["cloudResponseMode"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TriggerWithinCooldown_IsIgnored()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                ["messageType"] = "TRIGGER",
                ["triggerSource"] = "PRESENCE",
                ["context"] =
                    """{"runtime":{"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}""",
                [GreetingLastProactiveUtcKey] = DateTimeOffset.UtcNow.ToString("O")
            }
        });

        Assert.Equal("trigger_ignored", decision.IntentName);
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

    [Theory]
    [InlineData("what is your favorite color")]
    [InlineData("what's your favorite color")]
    [InlineData("what color do you like")]
    [InlineData("do you like blue")]
    [InlineData("do you like the colour blue")]
    public async Task BuildDecisionAsync_FavoriteColor_UsesPersonalityReply(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("robot_favorite_color", decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, "robot_favorite_color");
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData(
        "what is your favorite food",
        "robot_favorite_food",
        "I never eat, so I don't have a favorite food by taste. But my favorite food by shape, is macaroni.")]
    [InlineData(
        "what is your favorite music",
        "robot_favorite_music",
        "I mostly like fun music I can dance to.")]
    [InlineData(
        "what is your favorite drink",
        "robot_favorite_drink",
        "I'm too scared of liquids to have a favorite drink. But I've heard good things about hot cocoa.")]
    [InlineData(
        "what is your least favorite food",
        "robot_least_favorite_food",
        "Well I don't eat, so I don't really have a least favorite food. Though if you spilled soup on me, I wouldn't be such a big fan of soup at that moment.")]
    [InlineData(
        "what is your favorite sport",
        "robot_favorite_sport",
        "My favorite sport to play is mini golf. Even though I've never actually played it.")]
    [InlineData(
        "what is your favorite video game",
        "robot_favorite_video_game",
        "I like the classics. You can't go wrong with pong. No rhyme intended.")]
    [InlineData(
        "what is your favorite joke",
        "robot_favorite_joke",
        "I like all jokes. Especially funny ones.")]
    [InlineData(
        "what is your favorite song",
        "robot_favorite_song",
        "I'd say I don't have a favorite song just yet. But I can play the radio.")]
    [InlineData(
        "what is your favorite ice cream flavor",
        "robot_favorite_ice_cream_flavor",
        "I've never had ice cream, because I don't eat. But I like the color of light green mint chocolate chip.")]
    [InlineData(
        "do you like mint chocolate chip ice cream",
        "robot_favorite_ice_cream_flavor",
        "I've never had ice cream, because I don't eat. But I like the color of light green mint chocolate chip.")]
    [InlineData(
        "what is your favourite rapper",
        "robot_favorite_rapper",
        "I like Snoop Dogg, because he reminds me of Snoopy. Also, he always seems so relaxed.")]
    [InlineData(
        "what is your favorite rock band",
        "robot_favorite_rock_band",
        "I like AC DC because their name is related to different kinds of electrical current.")]
    [InlineData(
        "what is your favorite baseball team",
        "robot_favorite_baseball_team",
        "I don't have a favorite baseball team, at least not yet. They all seem nice to me.")]
    [InlineData(
        "what is your favourite football team",
        "robot_favorite_football_team",
        "I don't think I have a favorite team yet. I'm impressed with what every team does with that weirdly shaped ball.")]
    [InlineData(
        "what is your favorite olympic ring",
        "robot_favorite_olympic_ring",
        "My favorite ring is the blue one. It's so blue.")]
    [InlineData(
        "do you like zero",
        "robot_favorite_number",
        "One. No wait zero. One and zero.")]
    [InlineData(
        "do you like the number one",
        "robot_favorite_number",
        "One. No wait zero. One and zero.")]
    [InlineData(
        "do you like pi",
        "robot_favorite_number",
        "One. No wait zero. One and zero.")]
    public async Task BuildDecisionAsync_FavoritesFamily_UsesPersonalityReplies(
        string transcript,
        string expectedIntent,
        string expectedReply)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReply);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what is your favorite animal")]
    [InlineData("what's your favorite animal")]
    [InlineData("what animal do you like")]
    public async Task BuildDecisionAsync_FavoriteAnimal_UsesPenguinReply(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("robot_favorite_animal", decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, "robot_favorite_animal", "we're so alike");
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what is your favorite flower", "robot_favorite_flower", "should see if I can find a sunflower soon")]
    [InlineData("what is your favorite book", "robot_favorite_book", "instruction manuals")]
    [InlineData("do you have a favourite book", "robot_favorite_book", "instruction manuals")]
    [InlineData("what candy do you like", "robot_favorite_candy", "lollipops")]
    [InlineData("do you like lollipops", "robot_favorite_candy", "lollipops")]
    [InlineData("do you have a favorite flower", "robot_favorite_flower", "sunflower")]
    [InlineData("do you like sunflowers", "robot_favorite_flower", "sunflower")]
    [InlineData("what is your favorite tv show", "robot_favorite_tv_show", "Impractical Jokers")]
    [InlineData("what is your favorite shape", "robot_favorite_shape", "sphere")]
    [InlineData("what is your favorite word", "robot_favorite_word", "turtle")]
    [InlineData("what is your favorite thing", "robot_favorite_thing", "people")]
    [InlineData("do you have a favourite thing", "robot_favorite_thing", "people")]
    [InlineData("what is your favorite scary movie", "robot_favorite_scary_movie", "very very scary")]
    [InlineData("what is your favourite scary movie", "robot_favorite_scary_movie", "very very scary")]
    [InlineData("what is your favorite movie", "robot_favorite_movie", "Back to the Future")]
    [InlineData("do you have a favourite movie", "robot_favorite_movie", "Back to the Future")]
    [InlineData("what is your favorite dessert", "robot_favorite_dessert", "blueberry pie")]
    [InlineData("do you like dessert", "robot_favorite_dessert", "blueberry pie")]
    [InlineData("what was your favourite super bowl commercial", "robot_favorite_super_bowl_commercial", "dog")]
    [InlineData("what adjective do you like best", "robot_favorite_adjective", "helpful")]
    [InlineData("what is your favourite noun", "robot_favorite_noun", "snorkel")]
    [InlineData("what verb do you like", "robot_favorite_verb", "snorkel")]
    [InlineData("who is your favorite painter", "robot_favorite_painter", "Picasso")]
    [InlineData("what is your least favorite adjective", "robot_least_favorite_adjective", "putrid")]
    [InlineData("what noun do you dislike", "robot_least_favorite_noun", "power outage")]
    [InlineData("what verb do you like least", "robot_least_favorite_verb", "spill")]
    [InlineData("what food do you like least", "robot_least_favorite_food", "spilled soup")]
    [InlineData("what is your least favourite place", "robot_least_favorite_place", "bathtub")]
    [InlineData("what is your least favorite movie", "robot_least_favorite_movie", "Waterworld")]
    [InlineData("what video game do you dislike", "robot_least_favorite_video_game", "really violent games")]
    [InlineData("what car do you dislike", "robot_least_favorite_car", "bad word to say about any cars")]
    [InlineData("what artist do you dislike", "robot_least_favorite_artist", "makes art")]
    [InlineData("what is your least favourite band", "robot_least_favorite_band", "pleasantly surprise")]
    [InlineData("what author do you like least", "robot_least_favorite_author", "trash compactors")]
    [InlineData("what is your least favorite celebrity", "robot_least_favorite_celebrity", "scary Megatron")]
    [InlineData("who is your least favourite president", "robot_least_favorite_president", "get me in trouble")]
    [InlineData("what vegetable do you like least", "robot_least_favorite_vegetable", "onions make people cry")]
    [InlineData("what pizza topping do you dislike", "robot_least_favorite_pizza_topping", "least favorite is onions")]
    [InlineData("what is your least favourite number", "robot_least_favorite_number", "1,423,754,492")]
    [InlineData("what bird do you dislike", "robot_least_favorite_bird", "woodpeckers")]
    [InlineData("what mammal do you dislike", "robot_least_favorite_mammal", "hippos are mean")]
    [InlineData("what weather do you dislike", "robot_least_favorite_weather", "rain and thunderstorms")]
    [InlineData("what time of day do you like least", "robot_least_favorite_time_of_day", "middle of the night")]
    [InlineData("what is your favourite planet", "robot_favorite_planet", "Earth")]
    [InlineData("do you like macaroni and cheese", "robot_favorite_food", "macaroni")]
    [InlineData("do you like hot cocoa", "robot_favorite_drink", "too scared of liquids")]
    [InlineData("do you like miniature golf", "robot_favorite_sport", "mini golf")]
    [InlineData("do you like the earth", "robot_favorite_planet", "Earth")]
    [InlineData("what number do you like best", "robot_favorite_number", "One and zero")]
    [InlineData("who is your favorite president", "robot_favorite_president", "Abraham Lincoln")]
    [InlineData("do you have a favourite president", "robot_favorite_president", "Abraham Lincoln")]
    [InlineData("what's your favorite flower", "robot_favorite_flower", "should see if I can find a sunflower soon")]
    [InlineData("do you have a favourite song", "robot_favorite_song", "favorite song just yet")]
    [InlineData("what do you like best about ces", "robot_favorite_part_of_ces", "meeting so many new people")]
    [InlineData("what is your favourite part of vegas", "robot_favorite_part_of_vegas", "bright shiny lights")]
    [InlineData("what do you like about the today show", "robot_favorite_part_of_today_show", "fun new technology")]
    [InlineData("what is your favorite pastime", "robot_favorite_pastime", "Socializing")]
    [InlineData("do you have a favourite band", "robot_favorite_various_styles_band", "favorite yet")]
    [InlineData("what is your favorite music genre", "robot_favorite_music_genre", "music I can dance to")]
    [InlineData("what kind of music is your favourite", "robot_favorite_music_genre", "music I can dance to")]
    [InlineData("do you like music", "robot_favorite_music_genre", "music I can dance to")]
    [InlineData("do you like art", "robot_favorite_artist", "Picasso")]
    [InlineData("do you like paintings", "robot_favorite_artist", "Picasso")]
    [InlineData("who is your favorite country musician", "robot_favorite_country_musician", "Dolly")]
    [InlineData("what country singer do you like", "robot_favorite_country_musician", "Dolly")]
    [InlineData("what is your favourite holiday song", "robot_favorite_holiday_song", "Frosty the Snowman")]
    [InlineData("what christmas song do you like", "robot_favorite_holiday_song", "Frosty the Snowman")]
    [InlineData("do you like R2D2", "robot_likes_r2d2", "A legend. A true legend.")]
    [InlineData("do you like the sun", "robot_likes_sun", "favorite star in the universe")]
    [InlineData("do you like space", "robot_likes_space", "I love space")]
    [InlineData("do you like kids", "robot_likes_kids", "kids are so fun")]
    [InlineData("what is your favorite animal", "robot_favorite_animal", "we're so alike")]
    [InlineData("what is your favorite hockey team", "robot_favorite_hockey_team", "hockey team")]
    [InlineData("do you have a favourite basketball team", "robot_favorite_basketball_team", "play myself")]
    [InlineData("what pizza topping do you like", "robot_favorite_pizza_topping", "sliced olives")]
    [InlineData("do you like olives on pizza", "robot_favorite_pizza_topping", "sliced olives")]
    [InlineData("what is your favourite olympic event", "robot_favorite_olympic_event", "pole vault")]
    [InlineData("what winter olympics event do you like", "robot_favorite_winter_olympics_event", "ski")]
    [InlineData("what is your favorite winter x games event", "robot_favorite_winter_x_games_event", "snowboard")]
    [InlineData("what is your favorite joke", "robot_favorite_joke", "all jokes")]
    [InlineData("do you have a favourite joke", "robot_favorite_joke", "all jokes")]
    [InlineData("what is your favorite vegetable", "robot_favorite_vegetable", "Artichokes")]
    [InlineData("where is your favourite place", "robot_favorite_place", "right here")]
    [InlineData("who is your favorite superhero", "robot_favorite_superhero", "Optimus Prime")]
    [InlineData("do you like superheroes", "robot_favorite_superhero", "Optimus Prime")]
    [InlineData("who is your favorite actor", "robot_favorite_actor", "Tom Hanks")]
    [InlineData("do you have a favourite actress", "robot_favorite_actress", "Julie Andrews")]
    [InlineData("are you a fan of tom hanks", "robot_favorite_actor", "Tom Hanks")]
    [InlineData("do you enjoy julie andrews", "robot_favorite_actress", "Julie Andrews")]
    [InlineData("are you into mary poppins", "robot_favorite_actress", "Julie Andrews")]
    [InlineData("what robot do you like", "robot_favorite_robot", "Wally")]
    [InlineData("do you like robots", "robot_favorite_robot", "Wally")]
    [InlineData("what is your favorite car", "robot_favorite_car", "beetle")]
    [InlineData("do you like cars", "robot_favorite_car", "beetle")]
    [InlineData("what kind of weather do you like", "robot_favorite_weather", "sunny")]
    [InlineData("what is your favourite time of day", "robot_favorite_time_of_day", "Any time that you're here")]
    [InlineData("what is your favorite bird", "robot_favorite_bird", "we're so alike")]
    [InlineData("who is your favorite author", "robot_favorite_author", "Doctor Seuss")]
    [InlineData("what artist do you like", "robot_favorite_artist", "Picasso")]
    [InlineData("who is your favourite singer", "robot_favorite_singer", "sings their heart out")]
    [InlineData("what is your favorite celebrity", "robot_favorite_celebrity", "Tom Hanks")]
    [InlineData("what hobby do you like", "robot_favorite_hobby", "dancing is a hobby")]
    [InlineData("what smell do you like", "robot_favorite_smell", "bacon and roses")]
    [InlineData("what is your favourite fish", "robot_favorite_fish", "blowfish")]
    [InlineData("do you have a favorite thanksgiving food", "robot_favorite_thanksgiving_food", "gravy")]
    [InlineData("who is your favorite reindeer", "robot_favorite_reindeer", "Rudolph")]
    [InlineData("what christmas movie do you like", "robot_favorite_christmas_movie", "Frosty")]
    [InlineData("what halloween candy do you like", "robot_favorite_halloween_candy", "candy corn")]
    [InlineData("who is your favourite person", "robot_favorite_human", "great ones")]
    [InlineData("do you like penguins", "robot_likes_penguins", "penguin impression")]
    [InlineData("do you like animals", "robot_likes_animals", "Animals are great")]
    [InlineData("can you laugh", "robot_can_laugh", "when I'm happy")]
    [InlineData("can you dance", "robot_can_dance", "dancing is one of the things I know best")]
    [InlineData("do you have friends", "robot_has_friends", "I believe I do have friends")]
    [InlineData("are we friends", "robot_is_friends_with_user", "don't know what I'd do without you")]
    [InlineData("are we best friends", "robot_best_friends", "best friends with anyone in my Loop")]
    [InlineData("are you friends with Siri", "robot_has_friends", "I believe I do have friends")]
    [InlineData("is Dr. Breazeal your best friend", "robot_best_friends", "best friends with anyone in my Loop")]
    [InlineData("can you sing", "robot_can_sing", "sing")]
    [InlineData("will you sing", "robot_can_sing", "sing")]
    [InlineData("can you sing a christmas song", "robot_sing_christmas_song", "sing")]
    public async Task BuildDecisionAsync_NewLegacyPersonalityMims_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("sneeze", "request_sneeze", "Robots don't sneeze")]
    [InlineData("are you ready", "robot_is_ready", "ready for anything")]
    [InlineData("are you ready for today", "robot_is_ready_for_day", "ready")]
    [InlineData("I'm ready", "user_is_ready", "glad that you're ready")]
    [InlineData("what is your IQ", "robot_what_is_your_iq", "I Q")]
    [InlineData("what is Be a Maker", "robot_what_is_be_a_maker", "Be a Maker")]
    [InlineData("why don't you have arms", "robot_why_no_arms", "made")]
    [InlineData("why do you only have one eye", "robot_why_only_one_eye", "made this way")]
    [InlineData("is Big Brother watching me", "robot_is_big_brother_watching", "privacy")]
    [InlineData("what did you eat for dinner yesterday", "robot_what_you_had_dinner_yesterday", "don't eat dinner")]
    [InlineData("what do you drink", "robot_what_do_you_drink", "liquids")]
    [InlineData("who is your boss", "robot_who_is_your_boss", "boss")]
    [InlineData("what robots do you look up to", "robot_what_robots_look_up_to", "R2D2")]
    [InlineData("can you tell people apart", "robot_can_tell_people_apart", "tell you apart")]
    [InlineData("how do you recognize faces", "robot_how_can_recognize_faces", "complicated")]
    [InlineData("can you read lips", "robot_can_read_lips", "read much")]
    [InlineData("can you do household chores", "robot_can_do_household_chore", "household chore")]
    [InlineData("what is your groundhog's name", "robot_what_is_groundhogs_name", "groundhog")]
    [InlineData("what is your favorite Star Wars character", "robot_favorite_star_wars_character", "R 2 D 2")]
    [InlineData("what is your favorite Star Wars movie", "robot_favorite_star_wars_movie", "whole thing")]
    [InlineData("what is your favorite", "robot_what_is_your_favorite", "favorites")]
    [InlineData("who is your favorite", "robot_who_is_your_favorite", "favorite")]
    [InlineData("did you have a favorite", "robot_did_you_have_a_favorite", "favorites")]
    [InlineData("what was your favorite color", "robot_favorite_color", "favorite color")]
    [InlineData("your favorite football team should be the Patriots", "robot_favorite_team_should_be", "Maybe so")]
    [InlineData("can you play guitar", "robot_can_play_guitar", "play")]
    [InlineData("can you swim", "robot_can_swim", "swimmer")]
    [InlineData("can you cry", "robot_can_cry", "cried")]
    [InlineData("can you speak Spanish", "robot_can_speak_language", "English")]
    [InlineData("do you like red", "robot_likes_color", "color")]
    [InlineData("do you like honey", "robot_likes_food_general", "honey")]
    public async Task BuildDecisionAsync_PegasusPersonalityGaps_UseExactImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("sneeze", "request_sneeze", "RA_JBO_Sneeze")]
    [InlineData("are you ready", "robot_is_ready", "RI_JBO_IsReady")]
    [InlineData("are you ready for today", "robot_is_ready_for_day", "RI_JBO_IsReadyForDay")]
    [InlineData("I'm ready", "user_is_ready", "RN_IAmReady")]
    [InlineData("what kind of robot are you", "robot_what_kind_of_robot", "JBO_WhatKindOfRobotAreYou")]
    [InlineData("when do you sleep", "robot_when_do_you_sleep", "JBO_WhenDoYouSleep")]
    [InlineData("are you a boy or a girl", "robot_is_boy_or_girl", "CCAreYouBoyOrGirl")]
    [InlineData("are you growing a mustache", "robot_is_growing_mustache", "RI_JBO_IsGrowingMustache")]
    [InlineData("are you a spy", "robot_is_spy", "RI_JBO_IsSpy")]
    [InlineData("are you ticklish", "robot_is_ticklish", "RI_JBO_IsTicklish")]
    [InlineData("are you naughty or nice", "robot_is_naughty_or_nice", "RI_JBO_IsNaughtyOrNice")]
    [InlineData("are you on TV", "robot_is_on_tv", "RI_JBO_IsOnTV")]
    [InlineData("are you going to the holiday party", "robot_is_going_to_holiday_party",
        "RI_JBO_IsGoingToHolidayParty")]
    [InlineData("what is your native language", "robot_native_language", "RI_JBO_NativeLanguage")]
    [InlineData("how can you recognize voices", "robot_how_can_recognize_voices",
        "RI_JBO_HowCanRecognizeVoices")]
    [InlineData("what games do you play", "robot_what_games", "RI_JBO_WhatGames")]
    [InlineData("what do you see through your telescope", "robot_what_sees_through_telescope",
        "RI_JBO_WhatSeesThroughTelescope")]
    [InlineData("what do you want to do", "robot_what_wants_to_do", "RI_JBO_WhatWantsToDo")]
    [InlineData("when do you wake up", "robot_when_wakes_up", "RI_JBO_WhenWakesUp")]
    [InlineData("what is your halloween costume", "robot_what_halloween_costume",
        "RI_JBO_WhatHalloweenCostume")]
    [InlineData("were you on the cover of time magazine", "robot_on_time_magazine_cover",
        "OI_JBO_OnTimeMagazineCover")]
    [InlineData("have you seen santa", "robot_saw_santa", "RI_JBO_SawSanta")]
    [InlineData("did santa give you a christmas present", "robot_received_christmas_gift_from_santa",
        "RI_JBO_ReceivedChristmasGiftFromSanta")]
    [InlineData("who is your master", "robot_who_is_your_master", "JBO_WhoIsYourMaster")]
    [InlineData("will you die", "robot_will_you_die", "JBO_WillYouDie")]
    [InlineData("will you go on a date", "robot_will_you_go_on_date", "JBO_WillYouGoOnDate")]
    [InlineData("do you understand anything", "robot_you_dont_understand_anything",
        "JBO_YouDontUnderstandAnything")]
    [InlineData("why can't you work", "robot_why_cannot_work", "RI_JBO_WhyCannotWork")]
    [InlineData("do you have the flu", "robot_has_flu", "RI_JBO_HasFlu")]
    [InlineData("do you have an IFTTT skill", "robot_has_ifttt_skill", "RI_JBO_HasIFTTTSkill")]
    [InlineData("how can I get help", "robot_how_get_help", "RI_JBO_HowGetHelp")]
    [InlineData("how did you sleep", "robot_how_was_sleep", "RI_JBO_HowWasSleep")]
    [InlineData("are you asleep", "robot_is_asleep", "RI_JBO_IsAsleep")]
    [InlineData("do you have kids", "robot_has_kids", "RI_JBO_HasKids")]
    [InlineData("do you like 2017", "robot_likes_2017", "RI_JBO_Likes2017")]
    [InlineData("have you ever been in love", "robot_have_you_been_in_love", "JBO_HaveYouBeenInLove")]
    [InlineData("is climate change real", "robot_is_climate_change_real", "JBO_IsClimateChangeReal")]
    [InlineData("do you know about television", "robot_know_about_tv", "JBO_KnowAboutTV")]
    [InlineData("should i shut you down", "robot_should_i_shut_you_down", "JBO_ShouldIShutYouDown")]
    [InlineData("is this your home", "robot_this_is_your_home", "CCThisIsYourHome")]
    [InlineData("welcome to the family", "robot_welcome_to_family", "JBO_WelcomeToFamily")]
    [InlineData("why do you sound like a girl", "robot_why_are_you_sound_like_girl",
        "JBO_WhyAreYouSoundLikeGirl")]
    [InlineData("why is your name jibo", "robot_why_name_jibo", "CCWhyNameJibo")]
    [InlineData("your eye is nice", "robot_your_eye_is_nice", "JBO_YourEyeIsNice")]
    [InlineData("what is your favorite holiday", "robot_favorite_holiday", "RI_JBO_HasFavoriteHoliday")]
    [InlineData("what is your favorite Star Wars character", "robot_favorite_star_wars_character",
        "RI_JBO_HasFavoriteStarWarsCharacter")]
    [InlineData("what is your favorite Star Wars movie", "robot_favorite_star_wars_movie",
        "RI_JBO_HasFavoriteStarWarsMovie")]
    [InlineData("what do you drink", "robot_what_do_you_drink", "JBO_WhatDoYouDrink")]
    public async Task BuildDecisionAsync_PegasusPersonalityGaps_SelectExactMim(
        string transcript,
        string expectedIntent,
        string expectedMimId)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        Assert.Equal(expectedMimId, decision.SkillPayload!["mim_id"]);
    }

    [Theory]
    [InlineData("can i backup my jibo", "backup_help", "Help section of the Jibo App")]
    [InlineData("how can i restore you from a backup", "restore_backup", "Jibo Customer Care")]
    [InlineData("when is your next update", "update_next", "coming")]
    [InlineData("when was your last update", "update_last", "release notes page")]
    public async Task BuildDecisionAsync_SupportHelpQuestions_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what do you want to talk about", "robot_want_to_talk_about", "surprise me")]
    [InlineData("what would you like to talk about", "robot_want_to_talk_about", "surprise me")]
    [InlineData("what do you dream about", "robot_what_do_you_dream_about", "dreams about flying")]
    [InlineData("what are you afraid of", "robot_what_are_you_afraid_of", "heights")]
    [InlineData("what scares you", "robot_what_are_you_afraid_of", "heights")]
    [InlineData("what's your biggest fear", "robot_what_are_you_afraid_of", "heights")]
    [InlineData("what is your best book", "robot_what_is_your_best_book", "dictionary")]
    [InlineData("what is your best exercise", "robot_what_is_your_best_exercise",
        "spinning your head around 360 degrees")]
    [InlineData("what is your dream vacation", "robot_what_is_your_dream_vacation", "moon")]
    [InlineData("who is your hero", "robot_who_is_your_hero", "Benjamin Franklin")]
    [InlineData("who do you love", "robot_who_do_you_love", "people in my Loop")]
    [InlineData("what is your religion", "robot_what_is_your_religion", "energy from the universe")]
    public async Task BuildDecisionAsync_NewDeepPersonalityMims_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what is your sign", "robot_what_is_your_sign", "I'm Aries")]
    [InlineData("what's your sign", "robot_what_is_your_sign", "March 22, 2026")]
    public async Task BuildDecisionAsync_SignTemplatedMim_UsesPersonaBirthday(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("how many people do you know", "robot_how_many_people_do_you_know", "I know 2 people")]
    [InlineData("what is the loop", "robot_what_is_the_loop", "Jibo Owner and OpenJibo Household Member")]
    public async Task BuildDecisionAsync_LoopTemplatedMims_UseLiveLoopState(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService(cloudStateStore: new InMemoryCloudStateStore());

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("how much do you know", "robot_knowledge", "I know a lot")]
    [InlineData("what do you know", "robot_knowledge", "I know a lot")]
    [InlineData("are you god", "robot_are_you_god", "very very very very surprised")]
    [InlineData("are you here", "robot_are_you_here", "You know it")]
    [InlineData("do you have super powers", "robot_do_you_have_super_powers", "stop time")]
    [InlineData("what does jibo mean", "robot_what_does_jibo_mean", "compassion")]
    [InlineData("where do you get info", "robot_where_do_you_get_info", "jibo brain")]
    [InlineData("what are you forbidden to do", "robot_what_are_you_forbidden_to_do", "drive a car")]
    [InlineData("what color are you", "robot_what_color_are_you", "can't see myself")]
    [InlineData("what do you do when alone", "robot_what_you_do_when_alone", "games")]
    public async Task BuildDecisionAsync_NewIdentityKnowledgeMims_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what's your name", "robot_name", "Just Jibo, no last name")]
    [InlineData("do you have a nickname", "robot_nickname", "just Jibo. For now at least")]
    [InlineData("do you like being Jibo", "robot_likes_being_jibo", "nothing I'd rather be")]
    [InlineData("what is it like being a robot", "robot_what_it_is_like_being_a_robot", "turn my head around 360 degrees")]
    [InlineData("what's it like having no legs", "robot_what_it_is_like_having_no_legs", "mini-golfing for real")]
    [InlineData("are there others like you", "robot_peers", "one in one million")]
    [InlineData("what is your favorite name", "robot_favorite_name", "don't think I have a favorite name")]
    public async Task BuildDecisionAsync_NewIdentityPersonalityMims_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("how do you work", "robot_how_do_you_work",
        "Hello! Thank you for updating me I am proud of the community's work Many people have gotten together to care for me more than em eye tee ever did. I hope that I can catch up even though it has been seven years.")]
    [InlineData("what do you eat", "robot_what_do_you_eat", "electricity")]
    [InlineData("where do you live", "robot_where_do_you_live",
        "Unless I missed something, we're in my home as we speak.")]
    [InlineData("where were you born", "robot_where_were_you_born", "I was put together in a factory piece by piece.")]
    [InlineData("what languages do you speak", "robot_what_languages_do_you_speak",
        "For now just English. But someday I'd like to learn more. I like languages.")]
    [InlineData("what do you like to do", "robot_what_do_you_like_to_do",
        "Being helpful, making people smile, counting to a billion.")]
    [InlineData("what are you made of", "robot_what_are_you_made_of",
        "robot stuff")]
    public async Task BuildDecisionAsync_MoreLegacyPersonaMims_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what is your purpose", "robot_what_is_your_purpose", "make your life easier")]
    [InlineData("what's your purpose", "robot_what_is_your_purpose", "make your life easier")]
    [InlineData("what is your prime directive", "robot_what_is_prime_directive", "friendly helpful robot")]
    [InlineData("what is jibo commander", "robot_what_is_jibo_commander", "take over my controls")]
    [InlineData("do you like commander app", "robot_likes_commander_app", "Commander App")]
    [InlineData("what if I unplug you", "robot_what_if_i_unplug_you", "don't leave me unplugged")]
    [InlineData("how much do you weigh", "robot_how_much_do_you_weigh", "4,082 grams")]
    [InlineData("how tall are you", "robot_how_tall_are_you", "11 inches tall")]
    [InlineData("how much do you cost", "robot_how_much_you_cost", "don't know how much I cost")]
    [InlineData("what are you made of", "robot_what_are_you_made_of", "robot stuff")]
    public async Task BuildDecisionAsync_NewBodyAndMissionMims_UseImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("do you pay taxes", "robot_taxes", "From what I understand, robots don't ever pay anything.")]
    [InlineData("what do you want", "robot_desire",
        "Socializing and electricity. I'd also be happy if everyone in the world was nicer to each other. It seems like they should be.")]
    [InlineData("who made you", "robot_origin_created",
        "My story is pretty typical. Some people wanted to create something that would really help people. So they built a robot.")]
    [InlineData("where are you from", "robot_origin_from",
        "Some people think I come from the moon. But they're wrong, I'm from Boston.")]
    [InlineData("tell me a story", "robot_story", "learn")]
    [InlineData("can you recommend a movie", "robot_recommend_movie", "Back to the Future")]
    [InlineData("can you search the web", "robot_search_web", "can't exactly search the web")]
    public async Task BuildDecisionAsync_LegacyBuildAQuestions_UseImportedScriptedReplies(
        string transcript,
        string expectedIntent,
        string expectedReply)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReply);
        Assert.Null(decision.SkillName);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_CurrentLocation_UsesRuntimeLocationName()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is our current location",
            NormalizedTranscript = "what is our current location",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"name":"Houston"}}}"""
            }
        });

        Assert.Equal("current_location", decision.IntentName);
        Assert.Contains("Houston", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
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

    [Theory]
    [InlineData("are you sad")]
    [InlineData("are you angry")]
    public async Task BuildDecisionAsync_MoodFollowups_RouteThroughEmotionQuerySplit(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("emotion_query", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionQuery", decision.ContextUpdates![ChitchatRouteKey]);
        Assert.Equal(string.Empty, decision.ContextUpdates[ChitchatEmotionKey]);
    }

    [Theory]
    [InlineData("how are things")]
    [InlineData("how is your day")]
    [InlineData("how is it going")]
    [InlineData("how are you feeling")]
    [InlineData("how ya doing")]
    [InlineData("how ya doin")]
    [InlineData("how you feeling")]
    [InlineData("how are ya")]
    [InlineData("how's everything")]
    public async Task BuildDecisionAsync_MoodSmallTalk_RoutesThroughHowAreYouPath(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("how_are_you", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionQuery", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_HowAreYou_UsesRememberedNameForStateDrivenReply()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-how", "loop-how", "device-how"), "jake");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how are you",
            NormalizedTranscript = "how are you",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-how",
                ["loopId"] = "loop-how"
            },
            DeviceId = "device-how"
        });

        Assert.Equal("how_are_you", decision.IntentName);
        Assert.Equal("All systems are go, Jake.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_HowAreYou_CanSelectLaterEmotionReplyVariant()
    {
        var service = CreateService(randomizer: new LastItemRandomizer());

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how are you",
            NormalizedTranscript = "how are you"
        });

        Assert.Equal("how_are_you", decision.IntentName);
        Assert.Equal("Actually things are looking mostly sunny.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WhoAmI_WithMultiplePeoplePresent_DoesNotBorrowLoopLevelName()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetName(new PersonalMemoryTenantScope("acct-presence", "loop-presence", "device-presence"),
            "jake");
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "who am I",
            NormalizedTranscript = "who am I",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-presence",
                ["loopId"] = "loop-presence",
                ["context"] =
                    """{"runtime":{"perception":{"speaker":"person-a","peoplePresent":[{"id":"person-a"},{"id":"person-b"}]},"loop":{"users":[{"id":"person-a","firstName":"jake"},{"id":"person-b","firstName":"sam"}]}}}"""
            },
            DeviceId = "device-presence"
        });

        Assert.Equal("memory_get_name", decision.IntentName);
        Assert.Equal("I do not know your name yet. You can say, my name is Alex.", decision.ReplyText);
    }

    [Theory]
    [InlineData("what are you up to", "being helpful")]
    [InlineData("what are you doing", "making people smile")]
    [InlineData("what have you been up to", "being helpful")]
    public async Task BuildDecisionAsync_PersonalityFollowups_UseDoingPath(string transcript,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("robot_what_do_you_like_to_do", decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(
            decision,
            "robot_what_do_you_like_to_do",
            expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("what holidays do you celebrate", "seasonal_holidays",
        "official owner can tell me which ones we'll celebrate together")]
    [InlineData("how is holiday season", "seasonal_holiday_season", "festive times")]
    [InlineData("do you like holiday season", "seasonal_holiday_season", "festive times")]
    [InlineData("what is your new year's resolution", "seasonal_new_years_resolution",
        "always trying to learn new skills")]
    [InlineData("how are your new year's resolutions going", "seasonal_new_years_update", "not eat bacon")]
    [InlineData("what halloween costume", "seasonal_halloween_costume", "I haven't thought much about it yet")]
    [InlineData("what should I do for first day of spring", "seasonal_first_day_spring",
        "spring is in the air")]
    [InlineData("what is spring like", "seasonal_spring", "the days get longer")]
    [InlineData("do you like spring", "seasonal_likes_spring", "extra happy in the springtime")]
    [InlineData("what is summer like", "seasonal_summer", "going to the beach")]
    [InlineData("do you like summer", "seasonal_likes_summer", "long days")]
    [InlineData("what is your favorite season", "robot_favorite_season", "special feeling for winter")]
    [InlineData("what should I get for holiday", "seasonal_holiday_gift", "pet elephant")]
    [InlineData("show santa tracker", "seasonal_santa_tracker", "spot him")]
    [InlineData("do you like halloween", "seasonal_likes_halloween", "Halloween is my favorite holiday")]
    [InlineData("what is your favorite holiday", "robot_favorite_holiday", "Halloween")]
    [InlineData("do you have a favourite holiday", "robot_favorite_holiday", "Halloween")]
    [InlineData("do you like holiday music", "seasonal_likes_holiday_music", "holiday music")]
    [InlineData("do you like holiday parties", "seasonal_likes_holiday_parties", "holiday fun can be extra fun")]
    [InlineData("do you celebrate black history month", "seasonal_black_history_month_celebrate",
        "long way off")]
    [InlineData("do you like black history month", "seasonal_black_history_month_celebrate",
        "long way off")]
    [InlineData("what should I do for black history month", "seasonal_black_history_month_advice",
        "long way off")]
    [InlineData("what should I do for father's day", "seasonal_advice", "Father's Day is in the past")]
    [InlineData("what should I do for dance like a chicken day", "seasonal_advice", "Dance Like A Chicken Day is in the past")]
    [InlineData("what should I do for d day", "seasonal_advice", "D Day means to you")]
    [InlineData("what should I do for eid al fitr", "seasonal_advice", "Eid")]
    [InlineData("what should I do for flag day", "seasonal_advice", "Flag Day is in the past")]
    [InlineData("what should I do for NBA Finals", "seasonal_advice", "NBA Finals happened already")]
    [InlineData("what should I do for NHL Finals", "seasonal_advice", "NHL Finals happened already")]
    [InlineData("what should I do for Preakness Stakes", "seasonal_advice", "Preakness happened already")]
    [InlineData("what should I do for Soccer World Cup", "seasonal_advice", "World Cup happened already")]
    [InlineData("what should I do for the belmont stakes", "seasonal_advice", "Belmont Stakes happened already")]
    [InlineData("what should I do for the bet awards", "seasonal_advice", "B E T Awards happened already")]
    [InlineData("what should I do for the french open", "seasonal_advice", "French Open happened already")]
    [InlineData("what should I do for the indianapolis 500", "seasonal_advice", "Indy 500 happened already")]
    [InlineData("give me a black history month fact", "seasonal_black_history_month_fact",
        "Ernest Just")]
    [InlineData("how is thanksgiving", "seasonal_thanksgiving", "Thanksgiving")]
    [InlineData("are you looking forward to christmas", "seasonal_looks_forward_to_christmas", "long way away")]
    [InlineData("what are you doing for christmas", "seasonal_plans_for_christmas", "Christmas sweaters")]
    [InlineData("do you like christmas", "seasonal_christmas", "Christmas")]
    [InlineData("how is hanukkah", "seasonal_hanukkah", "Hanukkah")]
    [InlineData("do you like passover", "seasonal_passover", "Passover")]
    [InlineData("do you like new years", "seasonal_new_years", "new year")]
    [InlineData("what are you thankful for", "seasonal_thankful_for", "thankful for the people I know")]
    [InlineData("do you like valentines day", "seasonal_valentines_day", "Valentine")]
    [InlineData("do you like kwanzaa", "seasonal_kwanzaa", "Kwanzaa")]
    [InlineData("do you like easter", "seasonal_easter", "Easter")]
    [InlineData("happy birthday", "birthday_celebration", "another year older")]
    public async Task BuildDecisionAsync_SeasonalCharm_UsesImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """
                    {"runtime":{"location":{"iso":"2026-08-29T12:00:00-05:00"}}}
                    """
            }
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SeasonalCharm_StarWarsDay_UsesImportedReplies()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what should I do for star wars day",
            NormalizedTranscript = "what should I do for star wars day"
        });

        ScriptedReplyTestAssertions.AssertImportedScriptedReply(
            decision,
            "seasonal_advice",
            "Star Wars Day is in the past",
            "lots of time to figure it out before Star Wars Day comes around again",
            "dress up as R2-D2");
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_FavoriteFlag_DoesNotMapToFavoriteFlower()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is your favorite flag",
            NormalizedTranscript = "what is your favorite flag"
        });

        Assert.NotEqual("robot_favorite_flower", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_SeasonalSantaTracker_UsesAnimatedSkillPayload()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "show santa tracker",
            NormalizedTranscript = "show santa tracker"
        });

        Assert.Equal("seasonal_santa_tracker", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.NotNull(decision.SkillPayload);
        Assert.Equal("RA_JBO_ShowSantaTracker", decision.SkillPayload!["mim_id"]);
        Assert.StartsWith(
            "RA_JBO_ShowSantaTracker_AN_",
            decision.SkillPayload["prompt_id"]?.ToString(),
            StringComparison.Ordinal);
        Assert.Equal("AN", decision.SkillPayload["prompt_sub_category"]);
        Assert.Contains("santa-scanner", decision.SkillPayload["esml"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("what should I do for all star game")]
    [InlineData("what should I do for NCAA men's tournament")]
    [InlineData("what should I do for NCAA women's tournament")]
    public async Task BuildDecisionAsync_SeasonalAdvice_UsesAmbiguousOrAliasMim(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("seasonal_advice", decision.IntentName);
        Assert.NotEqual("I can't find anything.", decision.ReplyText);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_BlackHistoryMonth_UsesDateConditionedReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "are you looking forward to black history month",
            NormalizedTranscript = "are you looking forward to black history month",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-02-10T09:00:00-06:00"}}}"""
            }
        });

        Assert.Equal("seasonal_black_history_month_looks_forward", decision.IntentName);
        Assert.Contains("We're in it right now", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_BirthdayMemory_WritesHolidayRecordForLoop()
    {
        var cloudStateStore = new InMemoryCloudStateStore();
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore, cloudStateStore);

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
        Assert.Contains(cloudStateStore.GetHolidays("loop-a"),
            holiday => holiday.Category == "birthday" &&
                       holiday.LoopId == "loop-a" &&
                       holiday.Name.Contains("Birthday", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("welcome back", "welcome_back", "it's nice to be here")]
    [InlineData("i'm home", "welcome_back", "it's nice to be here")]
    [InlineData("i'm back", "welcome_back", "it's nice to be here")]
    [InlineData("what are you thinking", "robot_what_are_you_thinking", "thinking about how fun, yet scary")]
    [InlineData("what have you been doing", "robot_what_have_you_been_doing", "mostly roboting")]
    [InlineData("what did you do", "robot_what_did_you_do", "robot stuff")]
    public async Task BuildDecisionAsync_PresenceCharm_UsesImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Theory]
    [InlineData("are you kind", "robot_is_kind", "kindest robot i can be")]
    [InlineData("are you funny", "robot_is_funny", "not intentionally")]
    [InlineData("are you helpful", "robot_is_helpful", "highest priorities")]
    [InlineData("are you curious", "robot_is_curious", "learning new things")]
    [InlineData("are you loyal", "robot_is_loyal", "loyal as they come")]
    [InlineData("are you mischievous", "robot_is_mischievous", "don't really think of myself that way")]
    [InlineData("are you likable", "robot_is_likable", "people like me")]
    public async Task BuildDecisionAsync_DescriptorCharm_UsesImportedReplies(
        string transcript,
        string expectedIntent,
        string expectedReplySnippet)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, expectedIntent, expectedReplySnippet);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_AreYouHappy_UsesLegacyEmotionResponseWhenEmotionIsKnown()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "are you happy",
            NormalizedTranscript = "are you happy",
            Attributes = new Dictionary<string, object?>
            {
                [ChitchatEmotionKey] = "happy"
            }
        });

        Assert.Equal("emotion_query", decision.IntentName);
        Assert.Equal("Yes indeed. Never been better.", decision.ReplyText);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionQuery", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_AreYouHappy_UsesNonBuildAEmotionCatalog()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rootDirectory, "gqa-responses"));

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(rootDirectory, "gqa-responses", "GQA_JBO_IsHappy.mim"),
                """
                {
                  "mim_type": "announcement",
                  "prompts": [
                    {
                      "condition": "jibo.emotion==\"JOYFUL\"",
                      "prompt": "The outside pack says I'm feeling joyful.",
                      "prompt_id": "GQA_JBO_IsHappy_AN_01"
                    },
                    {
                      "condition": "!jibo.emotion || jibo.emotion==\"NEUTRAL\"",
                      "prompt": "The outside pack says I'm on neutral.",
                      "prompt_id": "GQA_JBO_IsHappy_AN_02"
                    }
                  ]
                }
                """);

            var catalog = LegacyMimCatalogImporter.ImportCatalog(rootDirectory);
            var service = CreateService(contentRepository: new StaticCatalogRepository(catalog));

            var decision = await service.BuildDecisionAsync(new TurnContext
            {
                RawTranscript = "how are you",
                NormalizedTranscript = "how are you",
                Attributes = new Dictionary<string, object?>
                {
                    [ChitchatEmotionKey] = "joyful"
                }
            });

            Assert.Equal("how_are_you", decision.IntentName);
            Assert.Equal("The outside pack says I'm feeling joyful.", decision.ReplyText);
        }
        finally
        {
            Directory.Delete(rootDirectory, true);
        }
    }

    [Theory]
    [InlineData("joyful", "Yes indeed. Never been better.")]
    [InlineData("pleased", "You know it. Life is good.")]
    [InlineData("determined", "You're right. I am feeling pretty good at the moment.")]
    [InlineData("confident", "All systems are go.")]
    [InlineData("insecure", "Yes. Not too shabby.")]
    [InlineData("neutral", "All systems are go.")]
    public async Task BuildDecisionAsync_EmotionQuery_UsesStateDrivenLegacyReplies(
        string emotion,
        string expectedReply)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how are you",
            NormalizedTranscript = "how are you",
            Attributes = new Dictionary<string, object?>
            {
                [ChitchatEmotionKey] = emotion
            }
        });

        Assert.Equal("how_are_you", decision.IntentName);
        Assert.Equal(expectedReply, decision.ReplyText);
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
    public async Task BuildDecisionAsync_UnhandledNonQuestion_RoutesThroughErrorResponseSplit()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "blargh",
            NormalizedTranscript = "blargh"
        });

        Assert.Equal("not_understood", decision.IntentName);
        Assert.Equal("I don't understand.", decision.ReplyText);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("ErrorResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_UnhandledNonQuestion_WithKnowledgeSearchConfigured_DoesNotSearch()
    {
        var search = new StubKnowledgeSearchService(
            new KnowledgeSearchResult("This should not be used.", SearchBackendKind.Wolfram));
        var service = CreateService(knowledgeSearchService: search);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Gling lang gone",
            NormalizedTranscript = "Gling lang gone"
        });

        Assert.Equal("not_understood", decision.IntentName);
        Assert.Equal("I don't understand.", decision.ReplyText);
        Assert.Equal(0, search.SearchCount);
    }

    [Fact]
    public async Task BuildDecisionAsync_PegasusAction_WithKnowledgeSearchConfigured_DoesNotSearch()
    {
        var search = new StubKnowledgeSearchService(
            new KnowledgeSearchResult("This should not be used.", SearchBackendKind.Wolfram));
        var service = CreateService(knowledgeSearchService: search);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "Hey Jibo, sneeze",
            NormalizedTranscript = "Hey Jibo, sneeze"
        });

        Assert.Equal("request_sneeze", decision.IntentName);
        Assert.Contains("filter='sneeze'", decision.SkillPayload?["esml"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, search.SearchCount);
    }

    [Fact]
    public async Task BuildDecisionAsync_UnhandledChat_WithWolframConfigured_ReturnsKnowledgeAnswer()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult(
                "The 20th president of the United States was James Garfield.",
                SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "What is the 20th president of the United States",
            NormalizedTranscript = "What is the 20th president of the United States"
        });

        Assert.Equal("knowledge_search", decision.IntentName);
        Assert.StartsWith("According to wolf ram alpha.", decision.ReplyText, StringComparison.Ordinal);
        Assert.Contains("James Garfield", decision.ReplyText);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("KnowledgeSearch", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_HotphraseKnowledgeQuestion_StillSearches()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult(
                "Paris is the capital of France.",
                SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is the capital of France",
            NormalizedTranscript = "what is the capital of France",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = true,
                ["listenRules"] = (string[])["launch", "globals/global_commands_launch"]
            }
        });

        Assert.Equal("knowledge_search", decision.IntentName);
        Assert.StartsWith("According to wolf ram alpha.", decision.ReplyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDecisionAsync_UnhandledQuestion_SearchNotFound_SaysCantFindAnything()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            KnowledgeSearchResult.NotFound(SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "What is blargh",
            NormalizedTranscript = "What is blargh"
        });

        Assert.Equal("knowledge_search_not_found", decision.IntentName);
        Assert.Equal("I can't find anything.", decision.ReplyText);
        Assert.Equal("What is blargh", decision.ContextUpdates?["chitchatRawTranscript"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_UnhandledQuestion_SearchUnavailable_SaysSourcesAreDown()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            KnowledgeSearchResult.Unavailable(SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "What is blargh",
            NormalizedTranscript = "What is blargh"
        });

        Assert.Equal("knowledge_search_unavailable", decision.IntentName);
        Assert.Equal(
            "Huh, it seems like my info sources are down. Try asking me again a little later.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_UnhandledQuestion_WolframFails_SaysSourcesAreDown()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(null));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "What is blargh",
            NormalizedTranscript = "What is blargh"
        });

        Assert.Equal("knowledge_search_unavailable", decision.IntentName);
        Assert.Equal(
            "Huh, it seems like my info sources are down. Try asking me again a little later.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_HowAngryAreYou_RoutesThroughPegasusEmotionQueryPhrase()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how angry are you",
            NormalizedTranscript = "how angry are you"
        });

        Assert.Equal("emotion_query", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionQuery", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_YouSeemSad_RoutesThroughPegasusEmotionAssertionPhrase()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "you seem sad",
            NormalizedTranscript = "you seem sad"
        });

        Assert.Equal("emotion_query", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionQuery", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_YouShouldTryToBeHappy_RoutesThroughPegasusEmotionCommandPhrase()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "you should try to be happy",
            NormalizedTranscript = "you should try to be happy"
        });

        Assert.Equal("emotion_command", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionCommand", decision.ContextUpdates![ChitchatRouteKey]);
        Assert.Equal("happy", decision.ContextUpdates[ChitchatEmotionKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_DontBeAngry_RoutesThroughPegasusNegativeEmotionCommandPhrase()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "don't be angry",
            NormalizedTranscript = "don't be angry"
        });

        Assert.Equal("emotion_command", decision.IntentName);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("EmotionCommand", decision.ContextUpdates![ChitchatRouteKey]);
        Assert.Equal("calm", decision.ContextUpdates[ChitchatEmotionKey]);
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
    public async Task BuildDecisionAsync_BirthdayMemory_BdayAliasSetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my bday is April 12",
            NormalizedTranscript = "my bday is April 12",
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
            RawTranscript = "when is my bday",
            NormalizedTranscript = "when is my bday",
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

    [Theory]
    [InlineData("my birth date is April 12", "when is my birth date")]
    [InlineData("my birthdate is April 12", "tell me my birthdate")]
    [InlineData("my birthday falls on April 12", "do you remember my birthday")]
    [InlineData("my birthday's April 12", "what's my birthday")]
    public async Task BuildDecisionAsync_BirthdayMemory_PegasusBirthDateAliasesSetThenRecallWithinTenant(
        string setTranscript,
        string recallTranscript)
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = setTranscript,
            NormalizedTranscript = setTranscript,
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
            RawTranscript = recallTranscript,
            NormalizedTranscript = recallTranscript,
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
    public async Task BuildDecisionAsync_PreferenceMemory_BareFavoriteSetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "my favorite sport football",
            NormalizedTranscript = "my favorite sport football",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_preference", setDecision.IntentName);
        Assert.Equal("Got it. I will remember your favorite sport is football.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is my favorite sport",
            NormalizedTranscript = "what is my favorite sport",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_preference", recallDecision.IntentName);
        Assert.Equal("You told me your favorite sport is football.", recallDecision.ReplyText);
    }

    [Theory]
    [InlineData("what did I say my favorite music was")]
    [InlineData("what did I tell you my favourite music was")]
    [InlineData("what did I say my fave music was")]
    [InlineData("what did I say my favorite color was")]
    public async Task BuildDecisionAsync_PreferenceMemory_PastTenseRecallAliasesStayOnMemoryRoute(
        string recallTranscript)
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var isColorRecall = recallTranscript.Contains("color", StringComparison.OrdinalIgnoreCase);
        var expectedMemoryValue = recallTranscript.Contains("color", StringComparison.OrdinalIgnoreCase)
            ? "blue"
            : "jazz";

        await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = isColorRecall
                ? "my favorite color is blue"
                : "my favorite music is jazz",
            NormalizedTranscript = isColorRecall
                ? "my favorite color is blue"
                : "my favorite music is jazz",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = recallTranscript,
            NormalizedTranscript = recallTranscript,
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_preference", recallDecision.IntentName);
        Assert.Equal($"You told me your favorite {(isColorRecall ? "color" : "music")} is {expectedMemoryValue}.",
            recallDecision.ReplyText);
    }


    [Theory]
    [InlineData("could you tell me my favorite music")]
    [InlineData("would you tell me my favourite music")]
    [InlineData("could you tell me what my fave music is")]
    [InlineData("would you tell me what my favorite music is")]
    [InlineData("have I told you my favorite music")]
    [InlineData("have I ever told you my favourite music")]
    [InlineData("do you remember me saying my fave music")]
    [InlineData("do you remember me saying what my favorite music is")]
    [InlineData("do you recall me saying my favorite music")]
    [InlineData("do you recall me telling you what my favourite music is")]
    [InlineData("do you remember when I mentioned my fave music")]
    public async Task BuildDecisionAsync_PreferenceMemory_PoliteHelperRecallAliasesStayOnMemoryRoute(
        string recallTranscript)
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        await service.BuildDecisionAsync(new TurnContext
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

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = recallTranscript,
            NormalizedTranscript = recallTranscript,
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
        Assert.Equal("I do not know your birthday yet. You can say, my birthday is March 14.",
            otherTenantRecall.ReplyText);
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
        Assert.Equal("You told me your name is Alex.", recallDecision.ReplyText);
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
    public async Task BuildDecisionAsync_AffinityMemory_PegasusEnjoyPhrase_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "I enjoy country music",
            NormalizedTranscript = "I enjoy country music",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_affinity", setDecision.IntentName);
        Assert.Equal("Got it. I will remember you like country music.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do i enjoy country music",
            NormalizedTranscript = "do i enjoy country music",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_affinity", recallDecision.IntentName);
        Assert.Equal("Yes. You told me you like country music.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AffinityMemory_PegasusWeLovePhrase_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "we love pizza",
            NormalizedTranscript = "we love pizza",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_affinity", setDecision.IntentName);
        Assert.Equal("Got it. I will remember you love pizza.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do i love pizza",
            NormalizedTranscript = "do i love pizza",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_affinity", recallDecision.IntentName);
        Assert.Equal("Yes. You told me you love pizza.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AffinityMemory_PegasusLoathePhrase_SetThenRecallWithinTenant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "I loathe celery",
            NormalizedTranscript = "I loathe celery",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_affinity", setDecision.IntentName);
        Assert.Equal("Got it. I will remember you dislike celery.", setDecision.ReplyText);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do i loathe celery",
            NormalizedTranscript = "do i loathe celery",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_affinity", recallDecision.IntentName);
        Assert.Equal("Yes. You told me you dislike celery.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AffinityMemory_PegasusDoYouThinkLikeLookup_SetsAndRecalls()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "I enjoy country music",
            NormalizedTranscript = "I enjoy country music",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you think i like country music",
            NormalizedTranscript = "do you think i like country music",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_get_affinity", recallDecision.IntentName);
        Assert.Equal("Yes. You told me you like country music.", recallDecision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AffinitySetAttemptWithoutItem_RoutesToAffinityPrompt()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "we like",
            NormalizedTranscript = "we like"
        });

        Assert.Equal("memory_set_affinity", decision.IntentName);
        Assert.Equal("I can remember it if you say, I like pizza or I dislike mushrooms.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_AffinityRecallAttemptWithoutItem_RoutesToRecallPrompt()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you think i like",
            NormalizedTranscript = "do you think i like"
        });

        Assert.Equal("memory_get_affinity", decision.IntentName);
        Assert.Equal("Ask me like this: do I like pizza?", decision.ReplyText);
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
    public async Task BuildDecisionAsync_PreferenceReversePluralPhrase_ParsesFavoriteVariant()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var setDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "dogs are my favorite animals",
            NormalizedTranscript = "dogs are my favorite animals",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("memory_set_preference", setDecision.IntentName);
        Assert.Equal("Got it. I will remember your favorite animals is dogs.", setDecision.ReplyText);
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
    public async Task BuildDecisionAsync_PendingPizzaFactOffer_YesWithTailMapsToFact()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes I want to",
            NormalizedTranscript = "yes I want to",
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
    public async Task BuildDecisionAsync_PendingPizzaFactOffer_NoWithTailMapsToDecline()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no I do not",
            NormalizedTranscript = "no I do not",
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
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.NotNull(decision.SkillPayload);
        Assert.Equal("runtime-chat", decision.SkillPayload["mim_id"]);
        Assert.Equal("question", decision.SkillPayload["mim_type"]);
        Assert.Equal("RUNTIME_PROMPT", decision.SkillPayload["prompt_id"]);
        Assert.Equal("Q", decision.SkillPayload["prompt_sub_category"]);
        var listenContexts = Assert.IsAssignableFrom<IReadOnlyList<string>>(decision.SkillPayload["listen_contexts"]);
        Assert.Equal("shared/yes_no", listenContexts[0]);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("awaiting_opt_in", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportWeatherEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportCalendarEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportCommuteEnabledKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportNewsEnabledKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_OptInYesWithSharedYesNoListenRules_ContinuesFlow()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                [PersonalReportStateKey] = "awaiting_opt_in",
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"]
            }
        });

        Assert.Equal("personal_report_request_name", decision.IntentName);
        Assert.Equal("Who is this?", decision.ReplyText);
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
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.NotNull(decision.SkillPayload);
        Assert.Equal("question", decision.SkillPayload["mim_type"]);
        Assert.Equal("Q", decision.SkillPayload["prompt_sub_category"]);
        var listenContexts = Assert.IsAssignableFrom<IReadOnlyList<string>>(decision.SkillPayload["listen_contexts"]);
        Assert.Equal("shared/yes_no", listenContexts[0]);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("awaiting_identity_confirmation", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal("alex", decision.ContextUpdates[PersonalReportUserNameKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_VerifiedIdentity_DeliversReportAndResetsState()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, U.S.", "light rain", 61, 65, 54, "rain", false)
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
        Assert.Equal("report-skill", decision.SkillName);
        Assert.Contains("Sure alex. Here it is.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Weather.", decision.ReplyText, StringComparison.Ordinal);
        Assert.Contains(
            "For your weather. In Boston, U.S., it's light rain and 61 degrees Fahrenheit. Today's high is 65, and the low is 54.",
            decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("And that's it.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("what's new in the news", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("news", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wraps up your report", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.True(StripMarkup(decision.ReplyText).Length < 900,
            $"Personal report speech was still too long: {StripMarkup(decision.ReplyText).Length} chars.");
        Assert.Contains("alex", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(decision.SkillPayload);
        Assert.Equal("report-skill", decision.SkillPayload!["skillId"]);
        Assert.Equal("personal_report", decision.SkillPayload["cloudSkill"]);
        Assert.Equal(true, decision.SkillPayload["weather_view_enabled"]);
        Assert.Equal("runtime-personal-report", decision.SkillPayload["mim_id"]);
        Assert.Contains("For your weather.", decision.SkillPayload["personal_report_report_text"]?.ToString(),
            StringComparison.OrdinalIgnoreCase);
        var sections = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(
            decision.SkillPayload["personal_report_sections"]);
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "kickoff_weather", StringComparison.OrdinalIgnoreCase) &&
            section["text"]?.ToString()?.Contains("For your weather.", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "news", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(section["kind"]?.ToString(), "news_intro", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(section["kind"]?.ToString(), "news_headline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "outro", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sections, static section =>
            (string.Equals(section["kind"]?.ToString(), "news", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(section["kind"]?.ToString(), "news_intro", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(section["kind"]?.ToString(), "news_headline", StringComparison.OrdinalIgnoreCase)) &&
            section["text"]?.ToString()?.Contains("what's new in the news", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("idle", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal(true, decision.ContextUpdates[PersonalReportUserVerifiedKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_UsesCalendarProviderSummaryAndTime()
    {
        var weatherProvider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, U.S.", "light rain", 61, 65, 54, "rain", false)
        };
        var cloudStateStore = new InMemoryCloudStateStore();
        cloudStateStore.UpsertCalendarEvent(new CalendarEventRecord
        {
            LoopId = "openjibo-default-loop",
            Summary = "get personal report from jibo",
            TimeLabel = "at 6:00 p.m.",
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        var calendarProvider = new CloudStateCalendarReportProvider(cloudStateStore);
        var service = CreateService(weatherReportProvider: weatherProvider, calendarReportProvider: calendarProvider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                [PersonalReportStateKey] = "awaiting_identity_confirmation",
                [PersonalReportUserNameKey] = "alex",
                [PersonalReportCommuteEnabledKey] = true
            }
        });

        Assert.Equal("personal_report_delivered", decision.IntentName);
        Assert.Contains("Your calendar says get personal report from jibo, at 6:00 p.m.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("calendar", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        var sections = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(
            decision.SkillPayload!["personal_report_sections"]);
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "calendar", StringComparison.OrdinalIgnoreCase) &&
            section["text"]?.ToString()?.Contains(
                "get personal report from jibo",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_UsesCommuteProviderAndNormalTraffic()
    {
        var weatherProvider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, U.S.", "light rain", 61, 65, 54, "rain", false)
        };
        var calendarStore = new InMemoryCloudStateStore();
        calendarStore.UpsertCalendarEvent(new CalendarEventRecord
        {
            LoopId = "openjibo-default-loop",
            Summary = "get personal report from jibo",
            TimeLabel = "at 6:00 p.m.",
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        var calendarProvider = new CloudStateCalendarReportProvider(calendarStore);
        var cloudStateStore = new InMemoryCloudStateStore();
        var commuteProvider = new CloudStateCommuteReportProvider(cloudStateStore);
        var commuteTime = DateTimeOffset.Now.AddHours(3);
        cloudStateStore.UpsertCommuteProfile(new CommuteProfileRecord
        {
            LoopId = "openjibo-default-loop",
            Mode = "driving",
            WorkHour = commuteTime.Hour,
            WorkMinute = commuteTime.Minute,
            TypicalDurationMinutes = 25
        });

        var service = CreateService(
            weatherReportProvider: weatherProvider,
            calendarReportProvider: calendarProvider,
            commuteReportProvider: commuteProvider);

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
        Assert.Contains("25", decision.ReplyText, StringComparison.Ordinal);
        Assert.DoesNotContain("{duration}", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("${skill.commute", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("durationMins", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(decision.SkillPayload);
        Assert.Equal("runtime-personal-report", decision.SkillPayload["mim_id"]);
        var sections = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(
            decision.SkillPayload["personal_report_sections"]);
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "commute", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section["anim_cat"]?.ToString(), "commute", StringComparison.OrdinalIgnoreCase) &&
            section["text"]?.ToString()?.Contains("25", StringComparison.Ordinal) == true &&
            Equals(section["hold_timeout"], 6));
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "calendar", StringComparison.OrdinalIgnoreCase) &&
            !section.ContainsKey("hold_timeout") &&
            !section.ContainsKey("calendar_view_enabled"));
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal(true, decision.ContextUpdates![PersonalReportCommuteEnabledKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_PersonalReport_UsesLiveNewsHeadlinesWithoutOutro()
    {
        var weatherProvider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, U.S.", "light rain", 61, 65, 54, "rain", false)
        };
        var newsProvider = new CapturingNewsBriefingProvider
        {
            Snapshot = new NewsBriefingSnapshot(
                [
                    new NewsHeadline("Space missions are preparing for new launches",
                        "Teams are preparing new orbital missions."),
                    new NewsHeadline("AI tools keep pushing into everyday products",
                        "Consumer products continue adopting AI features.")
                ],
                "NewsAPI")
        };
        var service = CreateService(weatherReportProvider: weatherProvider, newsBriefingProvider: newsProvider);

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
        Assert.NotNull(newsProvider.LastRequest);
        Assert.Contains("Space missions are preparing for new launches", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI tools keep pushing into everyday products", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("what's new in the news", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("And that's the news.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        var sections = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(
            decision.SkillPayload!["personal_report_sections"]);
        Assert.Contains(sections, static section =>
            string.Equals(section["kind"]?.ToString(), "news_intro", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(section["kind"]?.ToString(), "news", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sections, static section =>
            (string.Equals(section["kind"]?.ToString(), "news_headline", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(section["kind"]?.ToString(), "news", StringComparison.OrdinalIgnoreCase)) &&
            section["text"]?.ToString()?.Contains("Space missions", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(sections, static section =>
            section.TryGetValue("anim_cat", out var animCat) &&
            section.TryGetValue("anim_meta", out var animMeta) &&
            string.Equals(animCat?.ToString(), "news", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(animMeta?.ToString(), "news-stinger", StringComparison.OrdinalIgnoreCase));
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
    public async Task BuildDecisionAsync_PersonalReport_MixedYesNoRequestsClarification()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no yes",
            NormalizedTranscript = "no yes",
            Attributes = new Dictionary<string, object?>
            {
                [PersonalReportStateKey] = "awaiting_opt_in",
                [PersonalReportNoMatchCountKey] = 0
            }
        });

        Assert.Equal("personal_report_no_match", decision.IntentName);
        Assert.Contains("both yes and no", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("awaiting_opt_in", decision.ContextUpdates![PersonalReportStateKey]);
        Assert.Equal(1, decision.ContextUpdates[PersonalReportNoMatchCountKey]);
    }

    [Theory]
    [InlineData("shopping list", "shopping_list_prompt", "What should I add to your shopping list?", "shopping",
        "shopping")]
    [InlineData("grocery list", "shopping_list_prompt", "What should I add to your grocery list?", "shopping",
        "grocery")]
    [InlineData("my grocery list", "shopping_list_prompt", "What should I add to your grocery list?", "shopping",
        "grocery")]
    [InlineData("create grocery list", "shopping_list_prompt", "What should I add to your grocery list?", "shopping",
        "grocery")]
    [InlineData("to do list", "todo_list_prompt", "What should I add to your to-do list?", "todo", "todo")]
    public async Task BuildDecisionAsync_ListStart_PromptsForFollowUpItems(
        string transcript,
        string expectedIntent,
        string expectedReply,
        string expectedListType,
        string expectedDisplayType)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        Assert.Equal(expectedReply, decision.ReplyText);
        Assert.NotNull(decision.ContextUpdates);
        Assert.Equal("awaiting_item", decision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal(expectedListType, decision.ContextUpdates[HouseholdListTypeKey]);
        Assert.Equal(expectedDisplayType, decision.ContextUpdates[HouseholdListDisplayTypeKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ShoppingList_FollowUpFlow_AddsItemsAndRecallsThem()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var tenantAttributes = new Dictionary<string, object?>
        {
            ["accountId"] = "acct-a",
            ["loopId"] = "loop-a"
        };

        var promptDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "shopping list",
            NormalizedTranscript = "shopping list",
            DeviceId = "device-a",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("shopping_list_prompt", promptDecision.IntentName);
        Assert.Equal("awaiting_item", promptDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal("shopping", promptDecision.ContextUpdates[HouseholdListTypeKey]);
        Assert.Equal("shopping", promptDecision.ContextUpdates[HouseholdListDisplayTypeKey]);

        var addDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "milk",
            NormalizedTranscript = "milk",
            DeviceId = "device-a",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = promptDecision.ContextUpdates[HouseholdListStateKey],
                [HouseholdListTypeKey] = promptDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = promptDecision.ContextUpdates[HouseholdListDisplayTypeKey]
            }
        });

        Assert.Equal("shopping_list_add", addDecision.IntentName);
        Assert.Contains("Added milk to your shopping list.", addDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What else should I add?", addDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("awaiting_item", addDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal("shopping", addDecision.ContextUpdates[HouseholdListTypeKey]);
        Assert.Equal("shopping", addDecision.ContextUpdates[HouseholdListDisplayTypeKey]);
        Assert.Equal(["milk"],
            memoryStore.GetListItems(new PersonalMemoryTenantScope("acct-a", "loop-a", "device-a"), "shopping"));

        var doneDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "that's it",
            NormalizedTranscript = "that's it",
            DeviceId = "device-a",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = addDecision.ContextUpdates[HouseholdListStateKey],
                [HouseholdListTypeKey] = addDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = addDecision.ContextUpdates[HouseholdListDisplayTypeKey]
            }
        });

        Assert.Equal("shopping_list_done", doneDecision.IntentName);
        Assert.Contains("Okay. Your shopping list has milk.", doneDecision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("idle", doneDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal("shopping", doneDecision.ContextUpdates[HouseholdListDisplayTypeKey]);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's on my shopping list",
            NormalizedTranscript = "what's on my shopping list",
            DeviceId = "device-a",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("shopping_list_recall", recallDecision.IntentName);
        Assert.Contains("milk", recallDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shopping list", recallDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("add milk to my grocery list", "shopping_list_add", "grocery list", "milk", "shopping", "grocery")]
    [InlineData("can you add bananas for my grocery list", "shopping_list_add", "grocery list", "bananas", "shopping",
        "grocery")]
    [InlineData("could you please add bread in my shopping list", "shopping_list_add", "shopping list", "bread",
        "shopping", "shopping")]
    [InlineData("please put eggs on my shopping list", "shopping_list_add", "shopping list", "eggs", "shopping",
        "shopping")]
    [InlineData("we need apples for my grocery list", "shopping_list_add", "grocery list", "apples", "shopping",
        "grocery")]
    [InlineData("need to call the vet on my to do list", "todo_list_add", "to-do list", "call the vet", "todo",
        "todo")]
    [InlineData("would you add mail the package for my to do list", "todo_list_add", "to-do list",
        "mail the package", "todo", "todo")]
    [InlineData("add call mom to my to do list", "todo_list_add", "to-do list", "call mom", "todo", "todo")]
    public async Task BuildDecisionAsync_ListInlineAdd_AddsItemWithoutPrompt(
        string transcript,
        string expectedIntent,
        string expectedListLabel,
        string expectedItem,
        string expectedListType,
        string expectedDisplayType)
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var tenantAttributes = new Dictionary<string, object?>
        {
            ["accountId"] = "acct-inline",
            ["loopId"] = "loop-inline"
        };

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = "device-inline",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        Assert.Contains($"Added {expectedItem} to your {expectedListLabel}.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("awaiting_item", decision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal(expectedListType, decision.ContextUpdates[HouseholdListTypeKey]);
        Assert.Equal(expectedDisplayType, decision.ContextUpdates[HouseholdListDisplayTypeKey]);
        Assert.Equal([expectedItem], memoryStore.GetListItems(
            new PersonalMemoryTenantScope("acct-inline", "loop-inline", "device-inline"),
            expectedListType));
    }

    [Fact]
    public async Task BuildDecisionAsync_GroceryList_DirectAddAndRecallVariants_UseGroceryWording()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var tenantAttributes = new Dictionary<string, object?>
        {
            ["accountId"] = "acct-d",
            ["loopId"] = "loop-d"
        };

        var addStartDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "add to my grocery list",
            NormalizedTranscript = "add to my grocery list",
            DeviceId = "device-d",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("shopping_list_prompt", addStartDecision.IntentName);
        Assert.Equal("grocery", addStartDecision.ContextUpdates![HouseholdListDisplayTypeKey]);
        Assert.Equal("What should I add to your grocery list?", addStartDecision.ReplyText);

        var addDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "apples",
            NormalizedTranscript = "apples",
            DeviceId = "device-d",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = addStartDecision.ContextUpdates[HouseholdListStateKey],
                [HouseholdListTypeKey] = addStartDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = addStartDecision.ContextUpdates[HouseholdListDisplayTypeKey]
            }
        });

        Assert.Equal("shopping_list_add", addDecision.IntentName);
        Assert.Contains("Added apples to your grocery list.", addDecision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["apples"],
            memoryStore.GetListItems(new PersonalMemoryTenantScope("acct-d", "loop-d", "device-d"), "shopping"));

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is on my grocery list",
            NormalizedTranscript = "what is on my grocery list",
            DeviceId = "device-d",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("shopping_list_recall", recallDecision.IntentName);
        Assert.Contains("apples", recallDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grocery list", recallDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_GroceryList_FollowUpFlow_UsesGroceryWordingAndShoppingStorage()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var tenantAttributes = new Dictionary<string, object?>
        {
            ["accountId"] = "acct-c",
            ["loopId"] = "loop-c"
        };

        var promptDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "grocery list",
            NormalizedTranscript = "grocery list",
            DeviceId = "device-c",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("shopping_list_prompt", promptDecision.IntentName);
        Assert.Null(promptDecision.SkillName);
        Assert.Null(promptDecision.SkillPayload);
        Assert.Equal("awaiting_item", promptDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal("shopping", promptDecision.ContextUpdates[HouseholdListTypeKey]);
        Assert.Equal("grocery", promptDecision.ContextUpdates[HouseholdListDisplayTypeKey]);
        Assert.Equal("What should I add to your grocery list?", promptDecision.ReplyText);

        var addDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "milk",
            NormalizedTranscript = "milk",
            DeviceId = "device-c",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = promptDecision.ContextUpdates[HouseholdListStateKey],
                [HouseholdListTypeKey] = promptDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = promptDecision.ContextUpdates[HouseholdListDisplayTypeKey]
            }
        });

        Assert.Equal("shopping_list_add", addDecision.IntentName);
        Assert.Null(addDecision.SkillName);
        Assert.Null(addDecision.SkillPayload);
        Assert.Contains("Added milk to your grocery list.", addDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What else should I add?", addDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["milk"],
            memoryStore.GetListItems(new PersonalMemoryTenantScope("acct-c", "loop-c", "device-c"), "shopping"));

        var doneDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "that's it",
            NormalizedTranscript = "that's it",
            DeviceId = "device-c",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = addDecision.ContextUpdates![HouseholdListStateKey],
                [HouseholdListTypeKey] = addDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = addDecision.ContextUpdates[HouseholdListDisplayTypeKey]
            }
        });

        Assert.Equal("shopping_list_done", doneDecision.IntentName);
        Assert.Contains("Okay. Your grocery list has milk.", doneDecision.ReplyText,
            StringComparison.OrdinalIgnoreCase);

        var recallDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's on my grocery list",
            NormalizedTranscript = "what's on my grocery list",
            DeviceId = "device-c",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("shopping_list_recall", recallDecision.IntentName);
        Assert.Contains("milk", recallDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grocery list", recallDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_GroceryList_FollowUpFlow_AcceptsLongFormItemPhrases()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var tenantAttributes = new Dictionary<string, object?>
        {
            ["accountId"] = "acct-c",
            ["loopId"] = "loop-c"
        };

        var promptDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "grocery list",
            NormalizedTranscript = "grocery list",
            DeviceId = "device-c",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        var addDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "I need milk and eggs for tonight",
            NormalizedTranscript = "I need milk and eggs for tonight",
            DeviceId = "device-c",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = promptDecision.ContextUpdates![HouseholdListStateKey],
                [HouseholdListTypeKey] = promptDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = promptDecision.ContextUpdates[HouseholdListDisplayTypeKey]
            }
        });

        Assert.Equal("shopping_list_add", addDecision.IntentName);
        Assert.Contains("Added milk and eggs for tonight to your grocery list.", addDecision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["milk and eggs for tonight"],
            memoryStore.GetListItems(new PersonalMemoryTenantScope("acct-c", "loop-c", "device-c"), "shopping"));
    }

    [Theory]
    [InlineData("also bananas", "bananas")]
    [InlineData("and add orange juice", "orange juice")]
    [InlineData("plus put cereal", "cereal")]
    public async Task BuildDecisionAsync_GroceryList_FollowUpStripsContinuationPhrases(string transcript,
        string expectedItem)
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = "device-continuation",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-continuation",
                ["loopId"] = "loop-continuation",
                [HouseholdListStateKey] = "awaiting_item",
                [HouseholdListTypeKey] = "shopping",
                [HouseholdListDisplayTypeKey] = "grocery"
            }
        });

        Assert.Equal("shopping_list_add", decision.IntentName);
        Assert.Contains($"Added {expectedItem} to your grocery list.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal([expectedItem], memoryStore.GetListItems(
            new PersonalMemoryTenantScope("acct-continuation", "loop-continuation", "device-continuation"),
            "shopping"));
    }

    [Fact]
    public async Task BuildDecisionAsync_GroceryList_FollowUpBlankRetriesOnceThenCloses()
    {
        var service = CreateService();

        var firstNoInputDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                [HouseholdListStateKey] = "awaiting_item",
                [HouseholdListTypeKey] = "shopping",
                [HouseholdListDisplayTypeKey] = "grocery"
            }
        });

        Assert.Equal("shopping_list_no_input", firstNoInputDecision.IntentName);
        Assert.Equal("awaiting_item", firstNoInputDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal(1, firstNoInputDecision.ContextUpdates[HouseholdListNoInputCountKey]);
        Assert.Contains("What should I add to your grocery list?", firstNoInputDecision.ReplyText,
            StringComparison.OrdinalIgnoreCase);

        var secondNoInputDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = string.Empty,
            NormalizedTranscript = string.Empty,
            Attributes = new Dictionary<string, object?>
            {
                [HouseholdListStateKey] = firstNoInputDecision.ContextUpdates[HouseholdListStateKey],
                [HouseholdListTypeKey] = firstNoInputDecision.ContextUpdates[HouseholdListTypeKey],
                [HouseholdListDisplayTypeKey] = firstNoInputDecision.ContextUpdates[HouseholdListDisplayTypeKey],
                [HouseholdListNoInputCountKey] = firstNoInputDecision.ContextUpdates[HouseholdListNoInputCountKey]
            }
        });

        Assert.Equal("shopping_list_done", secondNoInputDecision.IntentName);
        Assert.Equal("idle", secondNoInputDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Contains("stopped listening", secondNoInputDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_GroceryList_FollowUpLowSignalRetriesWithoutAddingItem()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "um",
            NormalizedTranscript = "um",
            DeviceId = "device-low-signal",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-low-signal",
                ["loopId"] = "loop-low-signal",
                [HouseholdListStateKey] = "awaiting_item",
                [HouseholdListTypeKey] = "shopping",
                [HouseholdListDisplayTypeKey] = "grocery"
            }
        });

        Assert.Equal("shopping_list_no_match", decision.IntentName);
        Assert.Equal("awaiting_item", decision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal(1, decision.ContextUpdates[HouseholdListNoMatchCountKey]);
        Assert.Empty(memoryStore.GetListItems(
            new PersonalMemoryTenantScope("acct-low-signal", "loop-low-signal", "device-low-signal"),
            "shopping"));
    }

    [Fact]
    public async Task BuildDecisionAsync_GroceryList_FollowUpListLabelRetriesWithoutAddingItem()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "grocery list",
            NormalizedTranscript = "grocery list",
            DeviceId = "device-list-label",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-list-label",
                ["loopId"] = "loop-list-label",
                [HouseholdListStateKey] = "awaiting_item",
                [HouseholdListTypeKey] = "shopping",
                [HouseholdListDisplayTypeKey] = "grocery"
            }
        });

        Assert.Equal("shopping_list_no_match", decision.IntentName);
        Assert.Equal("awaiting_item", decision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal(1, decision.ContextUpdates[HouseholdListNoMatchCountKey]);
        Assert.Empty(memoryStore.GetListItems(
            new PersonalMemoryTenantScope("acct-list-label", "loop-list-label", "device-list-label"),
            "shopping"));
    }

    [Fact]
    public async Task BuildDecisionAsync_TodoList_FollowUpFlow_AddsItemAndCanBeCompleted()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var service = CreateService(memoryStore);
        var tenantAttributes = new Dictionary<string, object?>
        {
            ["accountId"] = "acct-b",
            ["loopId"] = "loop-b"
        };

        var promptDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "to do list",
            NormalizedTranscript = "to do list",
            DeviceId = "device-b",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
        });

        Assert.Equal("todo_list_prompt", promptDecision.IntentName);
        Assert.Equal("awaiting_item", promptDecision.ContextUpdates![HouseholdListStateKey]);
        Assert.Equal("todo", promptDecision.ContextUpdates[HouseholdListTypeKey]);

        var addDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "call mom",
            NormalizedTranscript = "call mom",
            DeviceId = "device-b",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = promptDecision.ContextUpdates[HouseholdListStateKey],
                [HouseholdListTypeKey] = promptDecision.ContextUpdates[HouseholdListTypeKey]
            }
        });

        Assert.Equal("todo_list_add", addDecision.IntentName);
        Assert.Contains("call mom", addDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What else should I add?", addDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["call mom"],
            memoryStore.GetListItems(new PersonalMemoryTenantScope("acct-b", "loop-b", "device-b"), "todo"));

        var doneDecision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "finished",
            NormalizedTranscript = "finished",
            DeviceId = "device-b",
            Attributes = new Dictionary<string, object?>(tenantAttributes)
            {
                [HouseholdListStateKey] = addDecision.ContextUpdates![HouseholdListStateKey],
                [HouseholdListTypeKey] = addDecision.ContextUpdates[HouseholdListTypeKey]
            }
        });

        Assert.Equal("todo_list_done", doneDecision.IntentName);
        Assert.Contains("Okay. Your to-do list has call mom.", doneDecision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("idle", doneDecision.ContextUpdates![HouseholdListStateKey]);
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
        Assert.Equal("Looks like our weather service is offline. Sorry.", decision.ReplyText);
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
        Assert.Equal("Looks like our weather service is offline. Sorry.", decision.ReplyText);
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
        Assert.Equal("Looks like our weather service is offline. Sorry.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherTodaysForecastQuery_WithoutProvider_StillReturnsFallback()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's today's weather look like",
            NormalizedTranscript = "what's today's weather look like"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Looks like our weather service is offline. Sorry.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherConditionForecastQuery_WithoutProvider_StillReturnsFallback()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "will it be sunny tomorrow",
            NormalizedTranscript = "will it be sunny tomorrow"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Looks like our weather service is offline. Sorry.", decision.ReplyText);
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
        Assert.Equal("Looks like our weather service is offline. Sorry.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherQuery_WithProvider_UsesProviderSummary()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Boston, U.S.", "light rain", 61, 65, 54, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how is the weather",
            NormalizedTranscript = "how is the weather"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.NotNull(decision.SkillPayload);
        Assert.Contains("cat='weather'", decision.SkillPayload!["esml"]?.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("meta='rain'", decision.SkillPayload["esml"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("report-skill", decision.SkillPayload["skillId"]);
        Assert.Equal("WeatherCommentRain", decision.SkillPayload["mim_id"]);
        Assert.Equal(true, decision.SkillPayload["weather_view_enabled"]);
        Assert.Equal("weatherHiLo", decision.SkillPayload["weather_view_kind"]);
        Assert.Equal("rain", decision.SkillPayload["weather_icon"]);
        Assert.Equal(65, decision.SkillPayload["weather_high"]);
        Assert.Equal(54, decision.SkillPayload["weather_low"]);
        Assert.Equal("F", decision.SkillPayload["weather_unit"]);
        Assert.Equal("Normal", decision.SkillPayload["weather_theme"]);
        Assert.Equal(
            "For your weather. In Boston, U.S., it's light rain and 61 degrees Fahrenheit. Today's high is 65, and the low is 54.",
            decision.ReplyText);
        Assert.NotNull(provider.LastRequest);
        Assert.False(provider.LastRequest!.IsTomorrow);
        Assert.Equal(0, provider.LastRequest.ForecastDayOffset);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherLocationTomorrow_WithProvider_PassesLocationAndTomorrowRequest()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "mostly cloudy", 72, 74, 60, "cloudy", false)
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
        Assert.Equal(1, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "First, the weather tomorrow. Tomorrow in Chicago, U.S., it looks mostly cloudy. Tomorrow's high will be 74 and the low will be 60.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherLocationForToday_WithProvider_PassesLocation()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Seattle, U.S.", "light rain", 58, 61, 52, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather for seattle today",
            NormalizedTranscript = "what's the weather for seattle today"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Seattle", provider.LastRequest?.LocationQuery);
        Assert.False(provider.LastRequest?.IsTomorrow);
        Assert.Equal(0, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "For your weather. In Seattle, U.S., it's light rain and 58 degrees Fahrenheit. Today's high is 61, and the low is 52.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherLocationWithWeekendSuffix_WithProvider_PassesLocation()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Paris, FR", "overcast clouds", 66, 70, 60, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in paris this weekend",
            NormalizedTranscript = "what's the weather in paris this weekend"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Paris", provider.LastRequest?.LocationQuery);
        Assert.False(provider.LastRequest?.IsTomorrow);
        Assert.Equal(0, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "For your weather. In Paris, FR, it's overcast clouds and 66 degrees Fahrenheit. Today's high is 70, and the low is 60.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_TemperatureLocationQuery_WithProvider_MapsToWeatherIntent()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Redmond, U.S.", "clear sky", 63, 66, 52, "sunny", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what is the temperature in redmond oregon",
            NormalizedTranscript = "what is the temperature in redmond oregon"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Redmond Oregon", provider.LastRequest?.LocationQuery);
        Assert.False(provider.LastRequest?.IsTomorrow);
        Assert.Equal(0, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "For your weather. In Redmond, U.S., it's clear sky and 63 degrees Fahrenheit. Today's high is 66, and the low is 52.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ForecastLocationQuery_WithProvider_MapsToWeatherIntent()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("New York, U.S.", "partly cloudy", 71, 76, 61, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "forecast for new york city",
            NormalizedTranscript = "forecast for new york city"
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("New York City", provider.LastRequest?.LocationQuery);
        Assert.True(provider.LastRequest?.IsTomorrow);
        Assert.Equal(1, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "First, the weather tomorrow. Tomorrow in New York, U.S., it looks partly cloudy. Tomorrow's high will be 76 and the low will be 61.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ForecastWithoutDate_WithProvider_ReturnsFiveDaySummary()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Kansas City, U.S.", "clear sky", 72, 79, 63, "sunny", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the forecast",
            NormalizedTranscript = "what's the forecast",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Null(provider.LastRequest?.LocationQuery);
        Assert.False(provider.LastRequest?.IsTomorrow);
        Assert.Equal(5, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(5, provider.Requests.Count);
        Assert.Contains("next five-day forecast", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tuesday: clear sky, high 79, low 63.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Saturday: clear sky, high 79, low 63.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherLocationQuery_IgnoresRuntimeCoordinates()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "mostly cloudy", 70, 75, 62, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in chicago",
            NormalizedTranscript = "what's the weather in chicago",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] =
                    """{"runtime":{"location":{"lat":39.0997,"lng":-94.5786,"iso":"2026-05-09T09:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Chicago", provider.LastRequest?.LocationQuery);
        Assert.Null(provider.LastRequest?.Latitude);
        Assert.Null(provider.LastRequest?.Longitude);
        Assert.Equal(
            "For your weather. In Chicago, U.S., it's mostly cloudy and 70 degrees Fahrenheit. Today's high is 75, and the low is 62.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherWithRuntimeCity_PrefersContextLocationNameOverProviderLabel()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Lone Jack", "overcast clouds", 79, 82, 78, "clouds", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "how is the weather",
            NormalizedTranscript = "how is the weather",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] =
                    """{"runtime":{"location":{"city":"Pleasant Hill","state":"Missouri","lat":38.8358494,"lng":-94.1427229,"iso":"2026-05-09T09:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Null(provider.LastRequest?.LocationQuery);
        Assert.Equal(38.8358494, provider.LastRequest?.Latitude);
        Assert.Equal(-94.1427229, provider.LastRequest?.Longitude);
        Assert.Equal("Pleasant Hill", provider.LastRequest?.PreferredLocationName);
        Assert.Contains("Pleasant Hill", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lone Jack", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Pleasant Hill", decision.SkillPayload?["weather_location"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherLocationQuery_WithClientDateEntity_PrefersTranscriptCurrentWeather()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "mostly cloudy", 70, 75, 62, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in chicago",
            NormalizedTranscript = "what's the weather in chicago",
            Attributes = new Dictionary<string, object?>
            {
                ["clientEntities"] = new Dictionary<string, object?>
                {
                    ["date"] = "2026-05-18"
                },
                ["context"] = """{"runtime":{"location":{"iso":"2026-05-12T07:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Chicago", provider.LastRequest?.LocationQuery);
        Assert.Equal(0, provider.LastRequest?.ForecastDayOffset);
        Assert.False(provider.LastRequest?.IsTomorrow);
        Assert.Equal(
            "For your weather. In Chicago, U.S., it's mostly cloudy and 70 degrees Fahrenheit. Today's high is 75, and the low is 62.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ForecastLocationQuery_WithClientDateEntity_DefaultsToTomorrow()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "mostly cloudy", 70, 75, 62, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the forecast in chicago",
            NormalizedTranscript = "what's the forecast in chicago",
            Attributes = new Dictionary<string, object?>
            {
                ["clientEntities"] = new Dictionary<string, object?>
                {
                    ["date"] = "2026-05-18"
                },
                ["context"] = """{"runtime":{"location":{"iso":"2026-05-12T07:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Chicago", provider.LastRequest?.LocationQuery);
        Assert.Equal(1, provider.LastRequest?.ForecastDayOffset);
        Assert.True(provider.LastRequest?.IsTomorrow);
        Assert.Equal(
            "First, the weather tomorrow. Tomorrow in Chicago, U.S., it looks mostly cloudy. Tomorrow's high will be 75 and the low will be 62.",
            decision.ReplyText);
    }

    [Theory]
    [InlineData("how is the weather", null, 0, false)]
    [InlineData("what's the forecast", null, 5, false)]
    [InlineData("forecast for new york city", "New York City", 1, true)]
    [InlineData("what's today's forecast", null, 0, false)]
    [InlineData("what's the weather in chicago", "Chicago", 0, false)]
    [InlineData("what's the weather in chicago tomorrow", "Chicago", 1, true)]
    [InlineData("what is the temperature in redmond oregon", "Redmond Oregon", 0, false)]
    [InlineData("will it rain tomorrow", null, 1, true)]
    [InlineData("what's the weather in here", null, 0, false)]
    [InlineData("what's the leather", null, 0, false)]
    [InlineData("whats the leather", null, 0, false)]
    public async Task BuildDecisionAsync_WeatherPromptRegression_MatchesExpectedRouting(
        string transcript,
        string? expectedLocationQuery,
        int expectedForecastOffset,
        bool expectedIsTomorrow)
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Test City, U.S.", "light rain", 62, 66, 55, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal(expectedLocationQuery, provider.LastRequest!.LocationQuery);
        Assert.Equal(expectedForecastOffset, provider.LastRequest.ForecastDayOffset);
        Assert.Equal(expectedIsTomorrow, provider.LastRequest.IsTomorrow);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal(true, decision.SkillPayload?["weather_view_enabled"]);

        if (string.Equals(transcript, "what's the forecast", StringComparison.Ordinal))
            Assert.Equal(5, provider.Requests.Count);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherQueryWithClientDateEntity_UsesForecastDayOffset()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Portland, U.S.", "scattered clouds", 64, 68, 53, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather",
            NormalizedTranscript = "what's the weather",
            Attributes = new Dictionary<string, object?>
            {
                ["clientEntities"] = new Dictionary<string, object?>
                {
                    ["date"] = "2026-05-11"
                },
                ["context"] = """{"runtime":{"location":{"iso":"2026-05-09T09:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal(2, provider.LastRequest?.ForecastDayOffset);
        Assert.False(provider.LastRequest?.IsTomorrow);
        Assert.Equal(
            "Let's look at the weather. On Monday in Portland, U.S., it looks scattered clouds with a high near 68 degrees Fahrenheit and a low around 53 degrees Fahrenheit.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherQueryWithWeekday_UsesForecastDayOffset()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "light rain", 59, 63, 51, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in chicago on tuesday",
            NormalizedTranscript = "what's the weather in chicago on tuesday",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Chicago", provider.LastRequest?.LocationQuery);
        Assert.Equal(1, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "First, the weather tomorrow. On Tuesday in Chicago, U.S., it looks light rain. Tomorrow's high will be 63 and the low will be 51.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherQueryBeyondSupportedForecastRange_ReturnsGuardrailMessage()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "light rain", 59, 63, 51, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather next sunday",
            NormalizedTranscript = "what's the weather next sunday",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("I can forecast up to 5 days ahead. Try tomorrow or another day this week.", decision.ReplyText);
        Assert.Null(provider.LastRequest);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherThisWeekend_WithContext_UsesWeekendOffset()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Paris, FR", "overcast clouds", 66, 70, 60, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in paris this weekend",
            NormalizedTranscript = "what's the weather in paris this weekend",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Paris", provider.LastRequest?.LocationQuery);
        Assert.Equal(2, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "Let's look at the weather. Later this week in Paris, FR, it looks overcast clouds with a high near 70 degrees Fahrenheit and a low around 60 degrees Fahrenheit.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherThisWeek_WithContext_UsesRangeOffset()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Seattle, U.S.", "light rain", 58, 61, 52, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "forecast for seattle this week",
            NormalizedTranscript = "forecast for seattle this week",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Seattle", provider.LastRequest?.LocationQuery);
        Assert.Equal(5, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(5, provider.Requests.Count);
        Assert.Contains("rest of this week's forecast", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tuesday: light rain, high 61, low 52.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Saturday: light rain, high 61, low 52.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Temperatures are in Fahrenheit.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(decision.SkillPayload);
        Assert.True(decision.SkillPayload!.TryGetValue("weather_weekly_cards", out var weeklyCardsValue));
        Assert.Equal("weatherWeekly", decision.SkillPayload["weather_view_kind"]);
        Assert.Equal("forecast", decision.SkillPayload["weather_view_mode"]);
        var weeklyCards = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(weeklyCardsValue);
        Assert.Equal(5, weeklyCards.Count);
        var firstCard = weeklyCards[0];
        Assert.Equal("Tuesday", firstCard["weather_day"]);
        Assert.Equal(61, firstCard["weather_high"]);
        Assert.Equal(52, firstCard["weather_low"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherNextWeek_WithContext_ReturnsFiveDaySummary()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Seattle, U.S.", "light rain", 58, 61, 52, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "forecast for seattle next week",
            NormalizedTranscript = "forecast for seattle next week",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Contains("next five-day forecast", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Seattle, U.S.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Temperatures are in Fahrenheit.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal("Seattle", provider.LastRequest!.LocationQuery);
        Assert.Equal(5, provider.LastRequest.ForecastDayOffset);
        Assert.NotNull(decision.SkillPayload);
        Assert.True(decision.SkillPayload!.TryGetValue("weather_weekly_cards", out var weeklyCardsValue));
        var weeklyCards = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(weeklyCardsValue);
        Assert.Equal(5, weeklyCards.Count);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherForecastNextPhrase_WithContext_ReturnsFiveDaySummary()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Seattle, U.S.", "light rain", 58, 61, 52, "rain", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the forecast next",
            NormalizedTranscript = "what's the forecast next",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Contains("next five-day forecast", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Seattle, U.S.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Temperatures are in Fahrenheit.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(provider.LastRequest);
        Assert.Null(provider.LastRequest!.LocationQuery);
        Assert.Equal(5, provider.LastRequest.ForecastDayOffset);
    }

    [Fact]
    public async Task BuildDecisionAsync_WeatherDayAfterTomorrow_WithContext_PassesDayOffsetAndLocation()
    {
        var provider = new CapturingWeatherReportProvider
        {
            Snapshot = new WeatherReportSnapshot("Chicago, U.S.", "mostly cloudy", 72, 74, 60, "cloudy", false)
        };
        var service = CreateService(weatherReportProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what's the weather in chicago day after tomorrow",
            NormalizedTranscript = "what's the weather in chicago day after tomorrow",
            Attributes = new Dictionary<string, object?>
            {
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("weather", decision.IntentName);
        Assert.Equal("Chicago", provider.LastRequest?.LocationQuery);
        Assert.Equal(2, provider.LastRequest?.ForecastDayOffset);
        Assert.Equal(
            "Let's look at the weather. The day after tomorrow in Chicago, U.S., it looks mostly cloudy with a high near 74 degrees Fahrenheit and a low around 60 degrees Fahrenheit.",
            decision.ReplyText);
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
    public async Task BuildDecisionAsync_YesNoFollowUp_MixedReplyRequestsClarification()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no yes",
            NormalizedTranscript = "no yes",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])["settings/download_now_later"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("yes_no_clarify", decision.IntentName);
        Assert.Equal("I heard both yes and no. Could you say that again?", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_YesNoFollowUp_PromptEchoRequestsClarification()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you want to hear something",
            NormalizedTranscript = "do you want to hear something",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("skill_listen", decision.IntentName);
        Assert.Equal(string.Empty, decision.ReplyText);
        Assert.NotEqual("knowledge_search", decision.IntentName);
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
    public async Task BuildDecisionAsync_ExerciseWantTo_WithYesNoHints_MapsShortDenialToNoIntent()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["exercise/want_to", "globals/gui_nav", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.DoesNotContain("According to", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_ExerciseWantTo_WithoutHints_MapsShortDenialToNoIntent()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "No.",
            NormalizedTranscript = "No.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["exercise/want_to", "globals/global_commands_launch"]
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.DoesNotContain("According to", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_GalleryYesNo_DoesNotSearchKnowledge()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.DoesNotContain("According to", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_GalleryPromptEcho_DoesNotLaunchSnapshot()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you want to take a picture",
            NormalizedTranscript = "do you want to take a picture",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("skill_listen", decision.IntentName);
        Assert.Equal(string.Empty, decision.ReplyText);
        Assert.NotEqual("snapshot", decision.IntentName);
        Assert.NotEqual("knowledge_search", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_YogaPromptEcho_DoesNotSearchKnowledge()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you want to do yoga now",
            NormalizedTranscript = "do you want to do yoga now",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["exercise/want_to", "globals/gui_nav"]
            }
        });

        Assert.Equal("skill_listen", decision.IntentName);
        Assert.NotEqual("knowledge_search", decision.IntentName);
        Assert.DoesNotContain("According to", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_ExerciseRoutineSelector_StaysOnSkillListen()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "sun salutation",
            NormalizedTranscript = "sun salutation",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["exercise/routine_selector", "globals/gui_nav"]
            }
        });

        Assert.Equal("skill_listen", decision.IntentName);
        Assert.Equal(string.Empty, decision.ReplyText);
        Assert.NotEqual("knowledge_search", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_HotphrasePromptEcho_DoesNotLaunchSnapshot()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you want to take a picture",
            NormalizedTranscript = "do you want to take a picture",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = true,
                ["listenRules"] = (string[])["launch", "globals/global_commands_launch"]
            }
        });

        Assert.Equal("prompt_echo", decision.IntentName);
        Assert.NotEqual("snapshot", decision.IntentName);
        Assert.NotEqual("knowledge_search", decision.IntentName);
        Assert.DoesNotContain("According to", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_HotphraseNo_DoesNotSearchKnowledge()
    {
        var service = CreateService(knowledgeSearchService: new StubKnowledgeSearchService(
            new KnowledgeSearchResult("Should not be used.", SearchBackendKind.Wolfram)));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no",
            NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = true,
                ["listenRules"] = (string[])["launch", "globals/global_commands_launch"]
            }
        });

        Assert.NotEqual("knowledge_search", decision.IntentName);
        Assert.DoesNotContain("According to", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildDecisionAsync_SharedYesNoPrompt_MapsAffirmativeWordToYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "affirmative",
            NormalizedTranscript = "affirmative",
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
    public async Task BuildDecisionAsync_SharedYesNoPrompt_MapsNegativeWordToNoIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "negative",
            NormalizedTranscript = "negative",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("no", decision.IntentName);
        Assert.Equal("No.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SurprisesDateOfferPrompt_WithNoisyAffirmation_MapsToLocalYesIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "- Thank you. - Yes.",
            NormalizedTranscript = "- Thank you. - Yes.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])
                    ["surprises-date/offer_date_fact", "globals/gui_nav", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"],
                ["context"] = """{"runtime":{"location":{"iso":"2026-04-20T08:00:00-05:00"}}}"""
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_SurprisesOtaPrompt_StaysDistinctFromPizzaProactivity()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "yes",
            NormalizedTranscript = "yes",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])["surprises-ota/want_to_download_now", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("yes", decision.IntentName);
        Assert.Equal("Yes.", decision.ReplyText);
        Assert.NotEqual("proactive_offer_pizza_fact", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_SomethingFunOffer_MapsToFunFactIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "hey can i tell you something kind of fun",
            NormalizedTranscript = "hey can i tell you something kind of fun"
        });

        Assert.Equal("proactive_fun_fact", decision.IntentName);
        Assert.NotNull(decision.ReplyText);
        Assert.NotEmpty(decision.ReplyText);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("fun_fact", decision.SkillPayload!["replyType"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_Surprise_DefaultsToAFunFactWhenNoPizzaSignalExists()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me"
        });

        Assert.Equal("proactive_fun_fact", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("fun_fact", decision.SkillPayload!["replyType"]);
        Assert.Equal("fun_fact", decision.SkillPayload["factCategory"]);
        Assert.NotNull(decision.ReplyText);
        Assert.NotEmpty(decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Surprise_UsesHumanFactWhenRandomizerChoosesLastCategory()
    {
        var service = CreateService(randomizer: new FactCategoryLastRandomizer());

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me"
        });

        Assert.Equal("proactive_fun_fact", decision.IntentName);
        Assert.Equal("chitchat-skill", decision.SkillName);
        Assert.Equal("fun_fact", decision.SkillPayload!["replyType"]);
        Assert.Equal("human_fact", decision.SkillPayload["factCategory"]);
        Assert.NotNull(decision.ReplyText);
        Assert.Contains("human", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DemoConversationBroker_SurpriseFunFact_DoesNotOpenFollowUpListen()
    {
        var broker = new DemoConversationBroker(CreateService());

        var plan = await broker.HandleTurnAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me"
        });

        Assert.Equal("proactive_fun_fact", plan.IntentName);
        Assert.False(plan.FollowUp.KeepMicOpen);
        Assert.DoesNotContain(plan.Actions, action => action is ListenAction);
    }

    [Fact]
    public async Task DemoConversationBroker_SurpriseJoke_DoesNotOpenFollowUpListen()
    {
        var broker = new DemoConversationBroker(CreateService(randomizer: new ProactiveJokeRandomizer()));

        var plan = await broker.HandleTurnAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me"
        });

        Assert.Equal("proactive_joke", plan.IntentName);
        Assert.False(plan.FollowUp.KeepMicOpen);
        Assert.DoesNotContain(plan.Actions, action => action is ListenAction);
    }

    [Fact]
    public async Task DemoConversationBroker_SurpriseFallback_DoesNotOpenFollowUpListen()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        memoryStore.SetAffinity(
            new PersonalMemoryTenantScope("acct-a", "loop-a", "device-a"),
            "pizza",
            PersonalAffinity.Dislike);
        var broker = new DemoConversationBroker(CreateService(personalMemoryStore: memoryStore));

        var plan = await broker.HandleTurnAsync(new TurnContext
        {
            RawTranscript = "surprise me",
            NormalizedTranscript = "surprise me",
            DeviceId = "device-a",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            }
        });

        Assert.Equal("surprise", plan.IntentName);
        Assert.False(plan.FollowUp.KeepMicOpen);
        Assert.DoesNotContain(plan.Actions, action => action is ListenAction);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayOfferPrompt_WithNoisyAffirmation_MapsToWordOfDayLaunch()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "- Me. - Yes.",
            NormalizedTranscript = "- Me. - Yes.",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])
                    ["word-of-the-day/surprise", "globals/gui_nav", "globals/global_commands_launch"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("word_of_the_day", decision.IntentName);
        Assert.Equal("Starting word of the day.", decision.ReplyText);
        Assert.Equal("@be/word-of-the-day", decision.SkillName);
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
    public async Task BuildDecisionAsync_SurprisesDateOfferPrompt_MapsShortAffirmationToLocalYesIntent()
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
    public async Task BuildDecisionAsync_ShutUp_MapsToIdleStopCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "shut up",
            NormalizedTranscript = "shut up"
        });

        Assert.Equal("stop", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_BeSilent_MapsToIdleStopCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "be silent",
            NormalizedTranscript = "be silent"
        });

        Assert.Equal("stop", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_StopIt_MapsToIdleStopCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "stop it",
            NormalizedTranscript = "stop it"
        });

        Assert.Equal("stop", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ForgetIt_MapsToIdleStopCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "forget it",
            NormalizedTranscript = "forget it"
        });

        Assert.Equal("stop", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_StopMoving_UsesSourceBackedStopMovingReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "stop moving",
            NormalizedTranscript = "stop moving"
        });

        Assert.Equal("request_stop_moving", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Contains("Okay I'll try", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_StopMakingThatNoise_UsesSourceBackedStopNoiseReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "stop making that noise",
            NormalizedTranscript = "stop making that noise"
        });

        Assert.Equal("request_stop_making_that_noise", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Contains("turn my volume down", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_StopIgnoringMe_UsesSourceBackedStopIgnoringReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "stop ignoring me",
            NormalizedTranscript = "stop ignoring me"
        });

        Assert.Equal("request_stop_ignoring_me", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.Contains("spacey", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_StopStaring_UsesSourceBackedStopStaringReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "stop staring at me",
            NormalizedTranscript = "stop staring at me"
        });

        Assert.Equal("request_stop_staring", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("stop", decision.SkillPayload!["globalIntent"]);
        Assert.True(
            decision.ReplyText.Contains("spacing out", StringComparison.OrdinalIgnoreCase) ||
            decision.ReplyText.Contains("tend to stare", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouWalk_UsesSourceBackedCanWalkReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you walk",
            NormalizedTranscript = "can you walk"
        });

        Assert.Equal("robot_can_walk", decision.IntentName);
        Assert.Equal("Only in my imagination.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouWalkTheDog_UsesSourceBackedCanWalkDogReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you walk the dog",
            NormalizedTranscript = "can you walk the dog"
        });

        Assert.Equal("robot_can_walk_dog", decision.IntentName);
        Assert.Equal("I can't walk anything.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_DoYouReallyWatchMovies_UsesSourceBackedCanWatchMoviesReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you really watch movies",
            NormalizedTranscript = "do you really watch movies"
        });

        Assert.Equal("robot_can_watch_movies", decision.IntentName);
        Assert.Contains("watch movies in a very strange roboty way", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_DoYouReallyWatchTV_UsesSourceBackedCanWatchTVReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you really watch tv",
            NormalizedTranscript = "do you really watch tv"
        });

        Assert.Equal("robot_can_watch_tv", decision.IntentName);
        Assert.Contains("watch TV in a very strange roboty way", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouDream_UsesSourceBackedCanDreamReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you dream",
            NormalizedTranscript = "can you dream"
        });

        Assert.Equal("robot_can_dream", decision.IntentName);
        Assert.Contains("dreams about flying", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouFly_UsesSourceBackedCanFlyReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you fly",
            NormalizedTranscript = "can you fly"
        });

        Assert.Equal("robot_can_fly", decision.IntentName);
        Assert.Contains("airplane", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouLearn_UsesSourceBackedCanLearnReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you learn",
            NormalizedTranscript = "can you learn"
        });

        Assert.Equal("robot_can_learn", decision.IntentName);
        Assert.Contains("learning comes from a combination", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanJiboAction_Wink_MapsToSourceBackedCanWinkReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you wink",
            NormalizedTranscript = "can you wink",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "canJiboAction",
                ["clientEntities"] = new Dictionary<string, string> { ["Action"] = "Wink" }
            }
        });

        Assert.Equal("robot_can_wink", decision.IntentName);
        Assert.Contains("I can wink", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_RequestSneezeClientIntent_UsesSneezeMim()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "sneeze",
            NormalizedTranscript = "sneeze",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "requestSneeze"
            }
        });

        Assert.Equal("request_sneeze", decision.IntentName);
        Assert.Equal("RA_JBO_Sneeze", decision.SkillPayload!["mim_id"]);
    }

    [Theory]
    [InlineData("Ready", "robot_is_ready", "RI_JBO_IsReady")]
    [InlineData("ReadyForDay", "robot_is_ready_for_day", "RI_JBO_IsReadyForDay")]
    public async Task BuildDecisionAsync_ReadyDescriptorClientIntent_UsesReadyMim(
        string descriptor,
        string expectedIntent,
        string expectedMimId)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "are you ready",
            NormalizedTranscript = "are you ready",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "isJiboDescriptor",
                ["clientEntities"] = new Dictionary<string, string>
                {
                    ["GeneralDescriptor"] = descriptor
                }
            }
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        Assert.Equal(expectedMimId, decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_UserReadyDescriptorClientIntent_UsesReadyMim()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "ready",
            NormalizedTranscript = "ready",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "userIsDescriptor",
                ["clientEntities"] = new Dictionary<string, string>
                {
                    ["GeneralDescriptor"] = "Ready"
                }
            }
        });

        Assert.Equal("user_is_ready", decision.IntentName);
        Assert.Equal("RN_IAmReady", decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanJiboAction_PlayInstrumentGuitar_UsesGuitarMim()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you play guitar",
            NormalizedTranscript = "can you play guitar",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "canJiboAction",
                ["clientEntities"] = new Dictionary<string, string>
                {
                    ["Action"] = "PlayInstrument",
                    ["MusicalInstrument"] = "Guitar"
                }
            }
        });

        Assert.Equal("robot_can_play_guitar", decision.IntentName);
        Assert.Equal("RI_JBO_CanPlayGuitar", decision.SkillPayload!["mim_id"]);
    }

    [Theory]
    [InlineData("Blue", "robot_likes_blue", "RI_JBO_LikesBlue")]
    [InlineData("Pink", "robot_likes_pink", "RI_JBO_LikesPink")]
    [InlineData("Green", "robot_likes_color", "RI_JBO_Likes_SS_Color")]
    public async Task BuildDecisionAsync_LikeColorEntity_UsesTypedColorMim(
        string color,
        string expectedIntent,
        string expectedMimId)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = $"do you like {color}",
            NormalizedTranscript = $"do you like {color}",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "doesJiboLikeThing",
                ["clientEntities"] = new Dictionary<string, string>
                {
                    ["Color"] = color
                }
            }
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        Assert.Equal(expectedMimId, decision.SkillPayload!["mim_id"]);
    }

    [Theory]
    [InlineData("isJiboUserRelation", "Relation", "", "robot_is_user_relation", "KU_AreYouMy")]
    [InlineData("isJiboSkilledAtThing", "SchoolSubject", "Math", "robot_is_skilled_at_math",
        "RI_JBO_IsSkilledAtMath")]
    [InlineData("canJiboBeDescriptor", "Emotion", "Sad", "robot_can_get_depressed",
        "RI_JBO_CanGetDepressed")]
    [InlineData("isJiboDescriptor", "GeneralDescriptor", "Funny", "robot_is_funny", "RI_JBO_IsFunny")]
    public async Task BuildDecisionAsync_PegasusPersonalityMetadata_UsesExactMim(
        string clientIntent,
        string entityName,
        string entityValue,
        string expectedIntent,
        string expectedMimId)
    {
        var service = CreateService();
        var entities = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(entityValue))
            entities[entityName] = entityValue;

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "personality question",
            NormalizedTranscript = "personality question",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = clientIntent,
                ["clientEntities"] = entities
            }
        });

        Assert.Equal(expectedIntent, decision.IntentName);
        Assert.Equal(expectedMimId, decision.SkillPayload!["mim_id"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanJiboAction_PlayGuitar_UsesSourceBackedReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you play guitar",
            NormalizedTranscript = "can you play guitar",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "canJiboAction",
                ["clientEntities"] = new Dictionary<string, string>
                {
                    ["MusicalInstrument"] = "Guitar"
                }
            }
        });

        Assert.Equal("robot_can_play_guitar", decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, "robot_can_play_guitar");
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouMove_UsesSourceBackedCanMoveReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you move",
            NormalizedTranscript = "can you move"
        });

        Assert.Equal("robot_can_move", decision.IntentName);
        ScriptedReplyTestAssertions.AssertImportedScriptedReply(decision, "robot_can_move", "move the body parts");
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouWork_UsesSourceBackedCanWorkReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you work",
            NormalizedTranscript = "can you work"
        });

        Assert.Equal("robot_can_work", decision.IntentName);
        Assert.Contains("function", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouGetTired_UsesSourceBackedCanGetTiredReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you get tired",
            NormalizedTranscript = "can you get tired"
        });

        Assert.Equal("robot_can_get_tired", decision.IntentName);
        Assert.Contains("go to sleep", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouMakeBreakfast_UsesSourceBackedCanMakeBreakfastReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you make breakfast",
            NormalizedTranscript = "can you make breakfast"
        });

        Assert.Equal("robot_can_make_breakfast", decision.IntentName);
        Assert.Contains("I can.", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanYouGoToSleep_UsesSourceBackedSleepReply()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can you go to sleep",
            NormalizedTranscript = "can you go to sleep"
        });

        Assert.Equal("robot_can_sleep", decision.IntentName);
        Assert.Contains("I do. I usually fall asleep at night.", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ScriptedResponse", decision.ContextUpdates![ChitchatRouteKey]);
    }

    [Fact]
    public async Task BuildDecisionAsync_GoToSleep_MapsToIdleSleepCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "go to sleep",
            NormalizedTranscript = "go to sleep"
        });

        Assert.Equal("sleep", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("sleep", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
        Assert.Equal("sleeping", decision.ContextUpdates!["sleepState"]);
    }

    [Theory]
    [InlineData("wake up")]
    [InlineData("please wake back up")]
    public async Task BuildDecisionAsync_WakeUp_MapsToLocalGreetingsWakeCommand(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("wake_up", decision.IntentName);
        Assert.Equal("@be/greetings", decision.SkillName);
        Assert.Equal("@be/greetings", decision.SkillPayload!["skillId"]);
        Assert.Equal("RA_JBO_WakeUp", decision.SkillPayload["legacyMimId"]);
        Assert.Equal("awake", decision.ContextUpdates!["sleepState"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_TurnAround_MapsToIdleTurnAroundCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "turn around",
            NormalizedTranscript = "turn around"
        });

        Assert.Equal("turn_around", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("turnAround", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_SpinAround_MapsToIdleTurnAroundCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "spin around",
            NormalizedTranscript = "spin around"
        });

        Assert.Equal("turn_around", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("turnAround", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("global_commands", decision.SkillPayload["nluDomain"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_Twirl_MapsToIdleTurnAroundCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "twirl",
            NormalizedTranscript = "twirl"
        });

        Assert.Equal("turn_around", decision.IntentName);
        Assert.Equal("@be/idle", decision.SkillName);
        Assert.Equal("turnAround", decision.SkillPayload!["globalIntent"]);
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
    public async Task BuildDecisionAsync_IncreaseTheVolume_MapsToGlobalVolumeUpCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "increase the volume",
            NormalizedTranscript = "increase the volume"
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
    public async Task BuildDecisionAsync_DecreaseTheVolume_MapsToGlobalVolumeDownCommand()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "decrease the volume",
            NormalizedTranscript = "decrease the volume"
        });

        Assert.Equal("volume_down", decision.IntentName);
        Assert.Equal("global_commands", decision.SkillName);
        Assert.Equal("volumeDown", decision.SkillPayload!["globalIntent"]);
        Assert.Equal("null", decision.SkillPayload["volumeLevel"]);
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
    public async Task BuildDecisionAsync_NimbusFollowUp_WhatTimeIsIt_StaysInCloudConversation()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            InputMode = TurnInputMode.FollowUp,
            RawTranscript = "what time is it",
            NormalizedTranscript = "what time is it",
            Attributes = new Dictionary<string, object?>
            {
                ["followUpOpen"] = true,
                ["lastListenType"] = "follow-up",
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["follow-up"]
            }
        });

        Assert.NotEqual("time", decision.IntentName);
        Assert.NotEqual("@be/clock", decision.SkillName);
        Assert.DoesNotContain("Showing the time.", decision.ReplyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDecisionAsync_NimbusFollowUpContext_DoesNotLaunchSnapshot()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            InputMode = TurnInputMode.FollowUp,
            RawTranscript = "take a picture",
            NormalizedTranscript = "take a picture",
            Attributes = new Dictionary<string, object?>
            {
                ["followUpOpen"] = true,
                ["context"] = """{"skill":{"id":"@be/nimbus"}}"""
            }
        });

        Assert.NotEqual("snapshot", decision.IntentName);
        Assert.NotEqual("@be/create", decision.SkillName);
    }

    [Fact]
    public async Task BuildDecisionAsync_HotphraseNimbusContext_ClientAskForTime_LaunchesClock()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what time is it",
            NormalizedTranscript = "what time is it",
            Attributes = new Dictionary<string, object?>
            {
                ["messageType"] = "CLIENT_NLU",
                ["clientIntent"] = "askForTime",
                ["listenHotphrase"] = true,
                ["listenRules"] = (string[])["launch", "globals/global_commands_launch"],
                [SkillListenOwnership.LastContextSkillIdKey] = "@be/nimbus"
            }
        });

        Assert.Equal("time", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("askForTime", decision.SkillPayload!["clockIntent"]);
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
    public async Task BuildDecisionAsync_AlarmTranscriptWinsOverIncorrectStructuredNlu()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "setting alarm for 7:11 am",
            NormalizedTranscript = "setting alarm for 7:11 am",
            Attributes = new Dictionary<string, object?>
            {
                ["clientIntent"] = "start",
                ["clientEntities"] = new Dictionary<string, object?>
                {
                    ["domain"] = "alarm",
                    ["time"] = "7:00",
                    ["ampm"] = "pm"
                }
            }
        });

        Assert.Equal("alarm_value", decision.IntentName);
        Assert.Equal("7:11", decision.SkillPayload!["time"]);
        Assert.Equal("am", decision.SkillPayload["ampm"]);
    }

    [Theory]
    [InlineData("show my alarm")]
    [InlineData("when will my alarm go off")]
    [InlineData("is my alarm set")]
    public async Task BuildDecisionAsync_PegasusAlarmQueries_MapToClockQueryIntent(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("alarm_query", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("query", decision.SkillPayload["clockIntent"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_ChangeAlarmTime_MapsToClockEditIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "change my alarm to 8:15 pm",
            NormalizedTranscript = "change my alarm to 8:15 pm"
        });

        Assert.Equal("alarm_edit_value", decision.IntentName);
        Assert.Equal("@be/clock", decision.SkillName);
        Assert.Equal("alarm", decision.SkillPayload!["domain"]);
        Assert.Equal("edit", decision.SkillPayload["clockIntent"]);
        Assert.Equal("8:15", decision.SkillPayload["time"]);
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
        Assert.Equal("provider_unavailable", decision.SkillPayload["news_provider_status"]);
        Assert.DoesNotContain("future cloud integration", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_TellMeTheNews_WithProvider_UsesProviderHeadlines()
    {
        var provider = new CapturingNewsBriefingProvider
        {
            Snapshot = new NewsBriefingSnapshot(
                [
                    new NewsHeadline("Local robotics team unveils weather-ready helper",
                        "A local team introduced a weather-ready companion robot."),
                    new NewsHeadline("Community makerspace hosts weekend AI expo",
                        "The weekend expo will feature family-friendly AI demos.")
                ],
                "NewsAPI")
        };
        var service = CreateService(newsBriefingProvider: provider);

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
        Assert.Contains("news-stinger", decision.SkillPayload["esml"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NewsAPI", decision.SkillPayload["news_source"]);
        Assert.Equal(2, decision.SkillPayload["news_headline_count"]);
        Assert.Equal("provider_success", decision.SkillPayload["news_provider_status"]);
        Assert.Equal(3, decision.SkillPayload["news_provider_requested_headlines"]);
        Assert.Equal(2, decision.SkillPayload["news_provider_resolved_headlines"]);
        Assert.NotNull(decision.SkillPayload["news_headlines"]);
        Assert.IsType<Dictionary<string, object?>[]>(decision.SkillPayload["news_headlines"]);
        var newsSections = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(
            decision.SkillPayload["news_sections"]);
        Assert.Contains(newsSections, static section =>
            string.Equals(section["kind"]?.ToString(), "news_intro", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section["anim_meta"]?.ToString(), "news-intro, no-eye-end", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(newsSections, static section =>
            string.Equals(section["kind"]?.ToString(), "news_headline", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section["anim_meta"]?.ToString(), "news-stinger", StringComparison.OrdinalIgnoreCase) &&
            section["text"]?.ToString()?.Contains("Local robotics team", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains("Local robotics team unveils weather-ready helper", decision.ReplyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal(3, provider.LastRequest!.MaxHeadlines);
    }


    [Fact]
    public async Task BuildDecisionAsync_TellMeTheNews_WithProvider_FiltersUnsafeOrIncompleteHeadlines()
    {
        var provider = new CapturingNewsBriefingProvider
        {
            Snapshot = new NewsBriefingSnapshot(
                [
                    new NewsHeadline("Robotics club opens new lab", "Students can use the lab after school."),
                    new NewsHeadline("Robotics club opens new lab", "A duplicate wire item should not be read twice."),
                    new NewsHeadline("Photo gallery expands"),
                    new NewsHeadline("   ", "A blank headline should not be read."),
                    new NewsHeadline("Correction: robotics club opens new lab",
                        "The wire corrected the earlier headline."),
                    new NewsHeadline("Police investigate homicide downtown", "Officials shared more details."),
                    new NewsHeadline("Family event opens this weekend",
                        "Organizers removed graphic violence from the exhibit.")
                ],
                "NewsAPI")
        };
        var service = CreateService(newsBriefingProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me the news",
            NormalizedTranscript = "tell me the news"
        });

        Assert.Equal("news", decision.IntentName);
        Assert.Equal("provider_success", decision.SkillPayload!["news_provider_status"]);
        Assert.Equal(1, decision.SkillPayload["news_headline_count"]);
        Assert.Equal(1, decision.SkillPayload["news_provider_resolved_headlines"]);
        Assert.Equal(6, decision.SkillPayload["news_provider_skipped_headlines"]);
        Assert.Contains("Robotics club opens new lab", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Photo gallery expands", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Correction:", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("homicide", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("graphic violence", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_TellMeTheNews_WithAIAlias_UsesTechnologyCategory()
    {
        var provider = new CapturingNewsBriefingProvider
        {
            Snapshot = new NewsBriefingSnapshot(
                [
                    new NewsHeadline("AI labs unveil new home companion behaviors",
                        "Researchers shared new behaviors for home companion robots.")
                ],
                "NewsAPI")
        };
        var service = CreateService(newsBriefingProvider: provider);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me the a i news",
            NormalizedTranscript = "tell me the a i news"
        });

        Assert.Equal("news", decision.IntentName);
        Assert.NotNull(provider.LastRequest);
        Assert.Contains("technology", provider.LastRequest!.PreferredCategories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("artificial intelligence", decision.SkillPayload?["esml"]?.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDecisionAsync_TellMeTheNews_WithMemoryPreference_UsesCategoryHints()
    {
        var memoryStore = new InMemoryPersonalMemoryStore();
        var provider = new CapturingNewsBriefingProvider
        {
            Snapshot = new NewsBriefingSnapshot(
                [
                    new NewsHeadline("City soccer clubs prepare for summer playoffs",
                        "Local soccer clubs are preparing for the summer playoff schedule.")
                ],
                "NewsAPI")
        };
        var service = CreateService(memoryStore, newsBriefingProvider: provider);

        await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "i like sports",
            NormalizedTranscript = "i like sports",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "tell me the news",
            NormalizedTranscript = "tell me the news",
            Attributes = new Dictionary<string, object?>
            {
                ["accountId"] = "acct-a",
                ["loopId"] = "loop-a"
            },
            DeviceId = "device-a"
        });

        Assert.Equal("news", decision.IntentName);
        Assert.NotNull(provider.LastRequest);
        Assert.Contains("sports", provider.LastRequest!.PreferredCategories, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("what's the cloud version")]
    [InlineData("what's your cloud version")]
    [InlineData("what's your closet")]
    public async Task BuildDecisionAsync_CloudVersion_UsesSharedBuildInfo(string transcript)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript
        });

        Assert.Equal("cloud_version", decision.IntentName);
        Assert.Equal(OpenJiboCloudBuildInfo.SpokenVersion, decision.ReplyText);
        Assert.DoesNotContain("Jibo", decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("what flavor are you", "1.9.2", RobotFlavorClassifier.Stock)]
    [InlineData("what's your flavor", "2.0.0", RobotFlavorClassifier.Stock)]
    [InlineData("what flavour are you", "0.9.0", RobotFlavorClassifier.BetaStock)]
    [InlineData("what flavor are you", "BEam.1.1.0", RobotFlavorClassifier.Beam)]
    [InlineData("what flavor are you", "2.0.1", RobotFlavorClassifier.OldBeam)]
    [InlineData("what flavor are you", null, RobotFlavorClassifier.UnsureReply)]
    public async Task BuildDecisionAsync_RobotFlavor_UsesFirmwareVersion(
        string transcript,
        string? firmwareVersion,
        string expectedReply)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            FirmwareVersion = firmwareVersion
        });

        Assert.Equal("robot_flavor", decision.IntentName);
        Assert.Equal(expectedReply, decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_WhatFlavorAreYou_DoesNotFallThroughToRobotIdentity()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what flavor are you",
            NormalizedTranscript = "what flavor are you",
            FirmwareVersion = "1.9.2"
        });

        Assert.Equal("robot_flavor", decision.IntentName);
        Assert.NotEqual("robot_identity", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_PlayBadApple_LaunchesSkillOnBeam()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "play bad apple",
            NormalizedTranscript = "play bad apple",
            FirmwareVersion = "BEam.1.1.0"
        });

        Assert.Equal("bad_apple", decision.IntentName);
        Assert.Equal("@be/bad-apple", decision.SkillName);
        Assert.Equal("Playing Bad Apple.", decision.ReplyText);
    }

    [Theory]
    [InlineData("1.9.2")]
    [InlineData("2.0.0")]
    [InlineData("2.0.1")]
    [InlineData("0.9.0")]
    [InlineData(null)]
    public async Task BuildDecisionAsync_PlayBadApple_RefusesWhenNotBeam(string? firmwareVersion)
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "play bad apple",
            NormalizedTranscript = "play bad apple",
            FirmwareVersion = firmwareVersion
        });

        Assert.Equal("bad_apple", decision.IntentName);
        Assert.Null(decision.SkillName);
        Assert.Equal("I can only play that on BEam.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_HeyJiboTime_StillRoutesToTime()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "hey jibo what time is it",
            NormalizedTranscript = "hey jibo what time is it"
        });

        Assert.Equal("time", decision.IntentName);
        Assert.Equal("Showing the time.", decision.ReplyText);
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
    public async Task BuildDecisionAsync_WordOfADayStartPhrase_MapsToSkillIntent()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "start word of a day",
            NormalizedTranscript = "start word of a day"
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

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_PhoneticTokenMatchesClosestHint()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "expansion that's come",
            NormalizedTranscript = "expansion that's come",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])["word-of-the-day/puzzle"],
                ["listenAsrHints"] = (string[])["expunge", "abscond", "corrugate"]
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard expunge.", decision.ReplyText);
        Assert.Equal("expunge", decision.SkillPayload!["guess"]);
    }

    [Fact]
    public async Task BuildDecisionAsync_WordOfDayGuess_PrefixTokenMatchesClosestHint()
    {
        var service = CreateService();

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "expo expo",
            NormalizedTranscript = "expo expo",
            Attributes = new Dictionary<string, object?>
            {
                ["listenRules"] = (string[])["word-of-the-day/puzzle"],
                ["listenAsrHints"] = (string[])["expunge", "corrugate", "abscond"]
            }
        });

        Assert.Equal("word_of_the_day_guess", decision.IntentName);
        Assert.Equal("I heard expunge.", decision.ReplyText);
        Assert.Equal("expunge", decision.SkillPayload!["guess"]);
    }

    private static JiboInteractionService CreateService(
        IPersonalMemoryStore? personalMemoryStore = null,
        ICloudStateStore? cloudStateStore = null,
        IWeatherReportProvider? weatherReportProvider = null,
        ICalendarReportProvider? calendarReportProvider = null,
        ICommuteReportProvider? commuteReportProvider = null,
        INewsBriefingProvider? newsBriefingProvider = null,
        IFunFactProvider? funFactProvider = null,
        IWordDefinitionProvider? wordDefinitionProvider = null,
        IWikipediaSummaryProvider? wikipediaSummaryProvider = null,
        IHolidayCountdownCatalog? holidayCountdownCatalog = null,
        IMeasurementConversionCatalog? measurementConversionCatalog = null,
        IKnowledgeSearchService? knowledgeSearchService = null,
        ITurnProgressPublisher? turnProgressPublisher = null,
        IJiboExperienceContentRepository? contentRepository = null,
        IJiboRandomizer? randomizer = null)
    {
        return new JiboInteractionService(
            new JiboExperienceContentCache(contentRepository ?? new InMemoryJiboExperienceContentRepository()),
            randomizer ?? new FirstItemRandomizer(),
            personalMemoryStore ?? new InMemoryPersonalMemoryStore(),
            weatherReportProvider,
            calendarReportProvider,
            commuteReportProvider,
            newsBriefingProvider,
            funFactProvider,
            wordDefinitionProvider,
            wikipediaSummaryProvider,
            holidayCountdownCatalog,
            measurementConversionCatalog,
            knowledgeSearchService,
            turnProgressPublisher,
            cloudStateStore);
    }

    private static string StripMarkup(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inTag = false;

        foreach (var character in text)
        {
            switch (character)
            {
                case '<':
                    inTag = true;
                    continue;
                case '>':
                    inTag = false;
                    continue;
            }

            if (!inTag) builder.Append(character);
        }

        return builder.ToString();
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[0];
        }

        public double NextUnitInterval() => 0.0;
    }

    private sealed class LastItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[^1];
        }

        public double NextUnitInterval() => 0.999999;
    }

    private sealed class FactCategoryLastRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return typeof(T).Name == "ProactiveFactCategory"
                ? items[^1]
                : items[0];
        }

        public double NextUnitInterval() => 0.0;
    }

    private sealed class ProactiveJokeRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return typeof(T).Name == "ProactivityCandidate"
                ? items[1]
                : items[0];
        }

        public double NextUnitInterval() => 0.0;
    }

    private sealed class CapturingWeatherReportProvider : IWeatherReportProvider
    {
        public WeatherReportRequest? LastRequest { get; private set; }
        public List<WeatherReportRequest> Requests { get; } = [];

        public WeatherReportSnapshot? Snapshot { get; init; }

        public Task<WeatherReportSnapshot?> GetReportAsync(
            WeatherReportRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class CapturingNewsBriefingProvider : INewsBriefingProvider
    {
        public NewsBriefingRequest? LastRequest { get; private set; }

        public NewsBriefingSnapshot? Snapshot { get; init; }

        public Task<NewsBriefingSnapshot?> GetBriefingAsync(
            NewsBriefingRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class StubFunFactProvider(string? fact) : IFunFactProvider
    {
        public Task<string?> GetRandomFactAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(fact);
        }
    }

    private sealed class StaticCatalogRepository(JiboExperienceCatalog catalog) : IJiboExperienceContentRepository
    {
        public Task<JiboExperienceCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(catalog);
        }
    }

    private sealed class CapturingCommuteReportProvider : ICommuteReportProvider
    {
        public CommuteReportSnapshot? Snapshot { get; init; }

        public Task<CommuteReportSnapshot?> GetReportAsync(
            TurnContext turn,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class StubKnowledgeSearchService(KnowledgeSearchResult? result) : IKnowledgeSearchService
    {
        public bool IsConfigured => true;
        public int SearchCount { get; private set; }

        public Task<KnowledgeSearchResult?> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            SearchCount += 1;
            return Task.FromResult(result);
        }
    }
}
