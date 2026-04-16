using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class DemoConversationBroker : IConversationBroker
{
    public Task<ResponsePlan> HandleTurnAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var transcript = (turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty).Trim();
        var lowered = transcript.ToLowerInvariant();
        var clientIntent = turn.Attributes.TryGetValue("clientIntent", out var rawClientIntent)
            ? rawClientIntent?.ToString()
            : null;
        var semanticIntent = ResolveSemanticIntent(lowered, clientIntent);

        var reply = semanticIntent switch
        {
            "time" => $"It is {DateTime.Now:hh:mm tt}.",
            "date" => $"Today is {DateTime.Now:dddd, MMMM d}.",
            "dance" => "Okay. Watch this.",
            _ => transcript.Length == 0
            ? "I am listening."
            : lowered.Contains("hello") || lowered.Contains("hi")
                ? "Hello from the OpenJibo cloud."
                : lowered.Contains("joke")
                    ? "Why did the robot bring a ladder? Because it wanted to reach the cloud."
                    : $"I heard: {transcript}"
        };

        var plan = new ResponsePlan
        {
            SessionId = turn.SessionId,
            Status = ResponseStatus.Succeeded,
            IntentName = semanticIntent,
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

        if (string.Equals(plan.IntentName, "joke", StringComparison.OrdinalIgnoreCase))
        {
            plan.Actions.Add(new InvokeNativeSkillAction
            {
                Sequence = 2,
                SkillName = "@be/joke",
                Payload = new Dictionary<string, object?>
                {
                    ["replyType"] = "joke"
                }
            });
        }

        return Task.FromResult(plan);
    }

    private static string ResolveSemanticIntent(string loweredTranscript, string? clientIntent)
    {
        if (string.Equals(clientIntent, "askForTime", StringComparison.OrdinalIgnoreCase))
        {
            return "time";
        }

        if (string.Equals(clientIntent, "askForDate", StringComparison.OrdinalIgnoreCase))
        {
            return "date";
        }

        if (loweredTranscript.Contains("joke", StringComparison.Ordinal))
        {
            return "joke";
        }

        if (loweredTranscript.Contains("dance", StringComparison.Ordinal))
        {
            return "dance";
        }

        if (loweredTranscript.Contains("time", StringComparison.Ordinal))
        {
            return "time";
        }

        if (loweredTranscript.Contains("date", StringComparison.Ordinal) || loweredTranscript.Contains("day", StringComparison.Ordinal))
        {
            return "date";
        }

        return "chat";
    }
}
