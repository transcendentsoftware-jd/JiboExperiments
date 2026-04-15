using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class ResponsePlanToSocketMessagesMapper
{
    public IReadOnlyList<string> Map(ResponsePlan plan, TurnContext turn, CloudSession session, bool emitSkillActions)
    {
        var speak = plan.Actions.OfType<SpeakAction>().FirstOrDefault();
        var skill = plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault();
        var transId = turn.Attributes.TryGetValue("transID", out var transIdValue)
            ? transIdValue?.ToString() ?? string.Empty
            : session.LastTransId ?? string.Empty;
        var transcript = turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty;
        var rules = ReadRules(turn);
        var messages = new List<string>();

        messages.Add(JsonSerializer.Serialize(new
        {
            type = "LISTEN",
            transID = transId,
            data = new
            {
                asr = new
                {
                    confidence = 0.95,
                    final = true,
                    text = transcript
                },
                nlu = new
                {
                    confidence = 0.95,
                    intent = plan.IntentName ?? "unknown",
                    rules,
                    entities = new Dictionary<string, object?>()
                },
                match = new
                {
                    intent = plan.IntentName ?? "unknown",
                    rule = rules.FirstOrDefault() ?? string.Empty,
                    score = 0.95
                }
            }
        }));

        messages.Add(JsonSerializer.Serialize(new
        {
            type = "EOS",
            data = new
            {
                sessionId = plan.SessionId,
                transID = transId
            }
        }));

        if (emitSkillActions && speak is not null)
        {
            messages.Add(JsonSerializer.Serialize(BuildSkillPayload(plan, turn, transId, speak, skill)));
        }

        return messages;
    }

    public IReadOnlyList<string> MapFallback(CloudSession session, string transId, IReadOnlyList<string> rules)
    {
        return
        [
            JsonSerializer.Serialize(new
            {
                type = "LISTEN",
                transID = transId,
                data = new
                {
                    asr = new
                    {
                        confidence = 0.95,
                        final = true,
                        text = string.Empty
                    },
                    nlu = new
                    {
                        confidence = 0.95,
                        intent = "heyJibo",
                        rules,
                        entities = new Dictionary<string, object?>()
                    },
                    match = new
                    {
                        intent = "heyJibo",
                        rule = rules.FirstOrDefault() ?? string.Empty,
                        score = 0.95
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "EOS",
                data = new
                {
                    sessionId = session.SessionId,
                    transID = transId
                }
            }),
            JsonSerializer.Serialize(BuildGenericFallbackSkillPayload(transId))
        ];
    }

    private static IReadOnlyList<string> ReadRules(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("listenRules", out var value))
        {
            return [];
        }

        return value switch
        {
            IReadOnlyList<string> typedRules => typedRules,
            IEnumerable<string> rules => rules.Where(rule => !string.IsNullOrWhiteSpace(rule)).ToArray(),
            _ => []
        };
    }

    private static object BuildSkillPayload(ResponsePlan plan, TurnContext turn, string transId, SpeakAction speak, InvokeNativeSkillAction? skill)
    {
        var isJoke = string.Equals(plan.IntentName, "joke", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(skill?.SkillName, "@be/joke", StringComparison.OrdinalIgnoreCase);
        var skillId = isJoke ? "@be/joke" : skill?.SkillName ?? "chitchat-skill";
        var esml = isJoke
            ? $"<speak><es cat='happy' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(speak.Text)}</es></speak>"
            : $"<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(speak.Text)}</es></speak>";
        var mimId = isJoke ? "runtime-joke" : "runtime-chat";

        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = $"msg-{Guid.NewGuid():N}",
            transID = transId,
            data = new
            {
                skill = new
                {
                    id = skillId
                },
                action = new
                {
                    config = new
                    {
                        jcp = new
                        {
                            type = "SLIM",
                            config = new
                            {
                                play = new
                                {
                                    esml,
                                    meta = new
                                    {
                                        prompt_id = "RUNTIME_PROMPT",
                                        prompt_sub_category = "AN",
                                        mim_id = mimId,
                                        mim_type = "announcement",
                                        intent = plan.IntentName ?? "unknown",
                                        transcript = turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty
                                    }
                                }
                            }
                        }
                    }
                },
                analytics = new Dictionary<string, object?>(),
                final = true
            }
        };
    }

    private static object BuildGenericFallbackSkillPayload(string transId)
    {
        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = $"msg-{Guid.NewGuid():N}",
            transID = transId,
            data = new
            {
                skill = new
                {
                    id = "chitchat-skill"
                },
                action = new
                {
                    config = new
                    {
                        jcp = new
                        {
                            type = "SLIM",
                            config = new
                            {
                                play = new
                                {
                                    esml = "<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>I heard you.</es></speak>",
                                    meta = new
                                    {
                                        prompt_id = "RUNTIME_PROMPT",
                                        prompt_sub_category = "AN",
                                        mim_id = "runtime-chat",
                                        mim_type = "announcement",
                                        intent = "unknown",
                                        transcript = string.Empty
                                    }
                                }
                            }
                        }
                    }
                },
                analytics = new Dictionary<string, object?>(),
                final = true
            }
        };
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
