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
            ContextUpdates = decision.ContextUpdates is not null
                ? new Dictionary<string, object?>(decision.ContextUpdates, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(),
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
            "cloud_version" => false,
            "word_of_the_day" => false,
            "word_of_the_day_guess" => false,
            "radio" => false,
            "radio_genre" => false,
            "stop" => false,
            "volume_up" => false,
            "volume_down" => false,
            "volume_to_value" => false,
            "volume_query" => false,
            "time" => false,
            "date" => false,
            "day" => false,
            "clock_open" => false,
            "clock_menu" => false,
            "timer_menu" => false,
            "alarm_menu" => false,
            "timer_delete" => false,
            "alarm_delete" => false,
            "timer_cancel" => false,
            "alarm_cancel" => false,
            "timer_value" => false,
            "alarm_value" => false,
            "photo_gallery" => false,
            "snapshot" => false,
            "photobooth" => false,
            "news" => false,
            _ => true
        };
    }
}
