using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class ResponsePlanToSocketMessagesMapper
{
    public static IReadOnlyList<SocketReplyPlan> Map(ResponsePlan plan, TurnContext turn, CloudSession session, bool emitSkillActions)
    {
        var speak = plan.Actions.OfType<SpeakAction>().FirstOrDefault();
        var skill = plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault();
        var messageType = ReadAttribute(turn, "messageType");
        var transId = turn.Attributes.TryGetValue("transID", out var transIdValue)
            ? transIdValue?.ToString() ?? string.Empty
            : session.LastTransId ?? string.Empty;
        var transcript = turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty;
        var clientIntent = ReadAttribute(turn, "clientIntent");
        var rules = ReadRules(turn, messageType);
        var yesNoCreateRule = ReadYesNoCreateRule(turn);
        var isYesNoTurn = !string.IsNullOrWhiteSpace(yesNoCreateRule);
        var isYesNoIntent = string.Equals(plan.IntentName, "yes", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(plan.IntentName, "no", StringComparison.OrdinalIgnoreCase);
        var outboundIntent = string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(clientIntent)
            ? clientIntent
            : plan.IntentName ?? "unknown";
        var nluGuess = ReadClientEntity(turn, "guess");
        var outboundAsrText = string.Equals(clientIntent, "guess", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(nluGuess)
            ? nluGuess
            : isYesNoTurn && isYesNoIntent
            ? transcript
            : string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(clientIntent)
                ? clientIntent
                : transcript;
        var outboundRules = isYesNoTurn && isYesNoIntent ? [yesNoCreateRule!] : rules;
        var entities = ReadEntities(turn, messageType, isYesNoTurn && isYesNoIntent);
        var messages = new List<SocketReplyPlan>
        {
            new(JsonSerializer.Serialize(new
            {
                type = "LISTEN",
                transID = transId,
                data = new
                {
                    asr = new
                    {
                        confidence = 0.95,
                        final = true,
                        text = outboundAsrText
                    },
                    nlu = new
                    {
                        confidence = 0.95,
                        intent = outboundIntent,
                        rules = outboundRules,
                        entities
                    },
                    match = new
                    {
                        intent = outboundIntent,
                        rule = outboundRules.FirstOrDefault() ?? string.Empty,
                        score = 0.95
                    }
                }
            })),
            new(JsonSerializer.Serialize(new
            {
                type = "EOS",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CreateHubMessageId(),
                transID = transId,
                data = new { }
            }))
        };

        if (emitSkillActions && speak is not null)
        {
            messages.Add(new SocketReplyPlan(
                JsonSerializer.Serialize(BuildSkillPayload(plan, turn, transId, speak, skill)),
                DelayMs: 75));
        }

        return messages;
    }

    public static IReadOnlyList<SocketReplyPlan> MapFallback(CloudSession session, string transId, IReadOnlyList<string> rules)
    {
        return
        [
            new SocketReplyPlan(JsonSerializer.Serialize(new
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
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(new
            {
                type = "EOS",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                msgID = CreateHubMessageId(),
                transID = transId,
                data = new { }
            })),
            new SocketReplyPlan(JsonSerializer.Serialize(BuildGenericFallbackSkillPayload(transId)), DelayMs: 75)
        ];
    }

    private static IReadOnlyList<string> ReadRules(TurnContext turn, string? messageType)
    {
        var attributeName = string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase)
            ? "clientRules"
            : "listenRules";

        if (!turn.Attributes.TryGetValue(attributeName, out var value))
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

    private static object ReadEntities(TurnContext turn, string? messageType, bool yesNoCreateTurn)
    {
        if (yesNoCreateTurn)
        {
            return new Dictionary<string, object?>
            {
                ["domain"] = "create"
            };
        }

        if (!string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>();
        }

        if (!turn.Attributes.TryGetValue("clientEntities", out var value) || value is null)
        {
            return new Dictionary<string, object?>();
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } jsonElement => jsonElement,
            IDictionary<string, object?> dictionary => dictionary,
            _ => new Dictionary<string, object?>()
        };
    }

    private static string? ReadYesNoCreateRule(TurnContext turn)
    {
        return ReadRuleValues(turn)
            .FirstOrDefault(static rule => string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReadRuleValues(TurnContext turn)
    {
        return ReadRuleValues(turn, "listenRules").Concat(ReadRuleValues(turn, "clientRules"));
    }

    private static IEnumerable<string> ReadRuleValues(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IReadOnlyList<string> typedRules => typedRules,
            IEnumerable<string> rules => rules,
            JsonElement { ValueKind: JsonValueKind.Array } jsonElement => jsonElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty),
            _ => []
        };
    }

    private static string? ReadAttribute(TurnContext turn, string key)
    {
        return turn.Attributes.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static string? ReadClientEntity(TurnContext turn, string entityName)
    {
        if (!turn.Attributes.TryGetValue("clientEntities", out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } jsonElement
                when jsonElement.TryGetProperty(entityName, out var property) && property.ValueKind == JsonValueKind.String
                => property.GetString(),
            IReadOnlyDictionary<string, string> typed when typed.TryGetValue(entityName, out var entityValue)
                => entityValue,
            IDictionary<string, object?> dictionary when dictionary.TryGetValue(entityName, out var entityValue)
                => entityValue?.ToString(),
            _ => null
        };
    }

    private static object BuildSkillPayload(ResponsePlan plan, TurnContext turn, string transId, SpeakAction speak, InvokeNativeSkillAction? skill)
    {
        var skillPayload = skill?.Payload;
        var isJoke = string.Equals(plan.IntentName, "joke", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(skill?.SkillName, "@be/joke", StringComparison.OrdinalIgnoreCase);
        var isDance = string.Equals(plan.IntentName, "dance", StringComparison.OrdinalIgnoreCase);
        var skillId = ReadPayloadString(skillPayload, "skillId") ?? (isJoke ? "@be/joke" : skill?.SkillName ?? "chitchat-skill");
        var esml = ReadPayloadString(skillPayload, "esml") ?? (isDance
            ? "<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, rom-upbeat' /></speak>"
            : isJoke
            ? $"<speak><es cat='happy' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(speak.Text)}</es></speak>"
            : $"<speak><es cat='neutral' filter='!ssa-only, !sfx-only' endNeutral='true'>{EscapeXml(speak.Text)}</es></speak>");
        var mimId = ReadPayloadString(skillPayload, "mim_id") ?? (isJoke ? "runtime-joke" : "runtime-chat");
        var mimType = ReadPayloadString(skillPayload, "mim_type") ?? "announcement";

        return new
        {
            type = "SKILL_ACTION",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgID = CreateHubMessageId(),
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
                                        mim_type = mimType
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
            msgID = CreateHubMessageId(),
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
                                        mim_type = "announcement"
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

    private static string? ReadPayloadString(IDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value))
        {
            return null;
        }

        return value?.ToString();
    }

    private static string CreateHubMessageId()
    {
        return $"mid-{Guid.NewGuid()}";
    }

    public sealed record SocketReplyPlan(string Text, int DelayMs = 0);
}
