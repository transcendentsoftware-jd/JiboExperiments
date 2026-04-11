using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class DemoConversationBroker : IConversationBroker
{
    public Task<ResponsePlan> HandleTurnAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var transcript = (turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty).Trim();
        var lowered = transcript.ToLowerInvariant();

        var reply = transcript.Length == 0
            ? "I am listening."
            : lowered.Contains("time")
                ? $"It is {DateTime.Now:hh:mm tt}."
                : lowered.Contains("hello") || lowered.Contains("hi")
                    ? "Hello from the OpenJibo cloud."
                    : lowered.Contains("joke")
                        ? "Why did the robot bring a ladder? Because it wanted to reach the cloud."
                        : $"I heard: {transcript}";

        var plan = new ResponsePlan
        {
            SessionId = turn.SessionId,
            Status = ResponseStatus.Succeeded,
            IntentName = lowered.Contains("joke") ? "joke" : lowered.Contains("time") ? "time" : "chat",
            Topic = "conversation",
            DeviceId = turn.DeviceId,
            TargetHost = turn.HostName,
            DebugRoute = "demo-broker",
            Actions =
            {
                new SpeakAction
                {
                    Sequence = 0,
                    Text = reply,
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

        return Task.FromResult(plan);
    }
}
