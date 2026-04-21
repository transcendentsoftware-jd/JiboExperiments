using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class DemoConversationBroker(JiboInteractionService interactionService) : IConversationBroker
{
    public async Task<ResponsePlan> HandleTurnAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var decision = await interactionService.BuildDecisionAsync(turn, cancellationToken);
        var keepMicOpen = ShouldKeepMicOpen(decision.IntentName);

        var plan = new ResponsePlan
        {
            SessionId = turn.SessionId,
            Status = ResponseStatus.Succeeded,
            IntentName = decision.IntentName,
            Topic = "conversation",
            DeviceId = turn.DeviceId,
            TargetHost = turn.HostName,
            DebugRoute = "demo-broker",
            Actions =
            {
                new SpeakAction
                {
                    Sequence = 0,
                    Text = decision.ReplyText,
                    Voice = "griffin"
                }
            },
            FollowUp = keepMicOpen
                ? new FollowUpPolicy
                {
                    KeepMicOpen = true,
                    Timeout = TimeSpan.FromSeconds(12),
                    ExpectedTopic = "conversation"
                }
                : FollowUpPolicy.None,
            ProtocolMetadata = new Dictionary<string, object?>
            {
                ["host"] = turn.HostName,
                ["service"] = turn.ProtocolService,
                ["operation"] = turn.ProtocolOperation
            }
        };

        if (keepMicOpen)
        {
            plan.Actions.Add(new ListenAction
            {
                Sequence = 1,
                Timeout = TimeSpan.FromSeconds(12),
                Mode = "follow-up"
            });
        }

        if (!string.IsNullOrWhiteSpace(decision.SkillName))
        {
            plan.Actions.Add(new InvokeNativeSkillAction
            {
                Sequence = 2,
                SkillName = decision.SkillName,
                Payload = decision.SkillPayload ?? new Dictionary<string, object?>()
            });
        }

        return plan;
    }

    private static bool ShouldKeepMicOpen(string? intentName)
    {
        return intentName switch
        {
            "word_of_the_day" => false,
            "word_of_the_day_guess" => false,
            "radio" => false,
            "radio_genre" => false,
            "clock_menu" => false,
            "timer_menu" => false,
            "alarm_menu" => false,
            "timer_value" => false,
            "alarm_value" => false,
            "news" => false,
            _ => true
        };
    }
}
