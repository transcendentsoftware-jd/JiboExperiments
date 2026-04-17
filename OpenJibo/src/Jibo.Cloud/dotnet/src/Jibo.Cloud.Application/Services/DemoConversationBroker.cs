using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class DemoConversationBroker(JiboInteractionService interactionService) : IConversationBroker
{
    public async Task<ResponsePlan> HandleTurnAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var decision = await interactionService.BuildDecisionAsync(turn, cancellationToken);

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
                },
                new ListenAction
                {
                    Sequence = 1,
                    Timeout = TimeSpan.FromSeconds(12),
                    Mode = "follow-up"
                }
            },
            FollowUp = new FollowUpPolicy
            {
                KeepMicOpen = true,
                Timeout = TimeSpan.FromSeconds(12),
                ExpectedTopic = "conversation"
            },
            ProtocolMetadata = new Dictionary<string, object?>
            {
                ["host"] = turn.HostName,
                ["service"] = turn.ProtocolService,
                ["operation"] = turn.ProtocolOperation
            }
        };

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
}
