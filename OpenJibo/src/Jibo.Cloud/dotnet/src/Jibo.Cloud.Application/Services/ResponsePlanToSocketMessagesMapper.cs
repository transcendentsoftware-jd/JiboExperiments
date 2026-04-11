using System.Text.Json;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class ResponsePlanToSocketMessagesMapper
{
    public IReadOnlyList<string> Map(ResponsePlan plan)
    {
        var speak = plan.Actions.OfType<SpeakAction>().FirstOrDefault();
        var messages = new List<string>();

        if (speak is not null)
        {
            messages.Add(JsonSerializer.Serialize(new
            {
                type = "OPENJIBO_RESPONSE",
                data = new
                {
                    intent = plan.IntentName,
                    text = speak.Text,
                    followUpOpen = plan.FollowUp.KeepMicOpen,
                    timeoutMs = (int)plan.FollowUp.Timeout.TotalMilliseconds
                }
            }));
        }

        messages.Add(JsonSerializer.Serialize(new
        {
            type = "EOS",
            data = new
            {
                sessionId = plan.SessionId
            }
        }));

        return messages;
    }
}
