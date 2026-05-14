using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using System.Linq;

namespace Jibo.Cloud.Application.Services;

internal static class HouseholdListOrchestrator
{
    internal const string StateMetadataKey = "householdListState";
    internal const string TypeMetadataKey = "householdListType";
    internal const string NoMatchCountMetadataKey = "householdListNoMatchCount";
    internal const string NoInputCountMetadataKey = "householdListNoInputCount";

    private const string IdleState = "idle";
    private const string AwaitingItemState = "awaiting_item";

    public static Task<JiboInteractionDecision?> TryBuildDecisionAsync(
        TurnContext turn,
        string semanticIntent,
        string transcript,
        string loweredTranscript,
        IJiboRandomizer randomizer,
        IPersonalMemoryStore personalMemoryStore,
        Func<TurnContext, PersonalMemoryTenantScope> tenantScopeResolver)
    {
        var state = ReadString(turn, StateMetadataKey);
        var listType = ReadString(turn, TypeMetadataKey);
        var isActiveState = !string.IsNullOrWhiteSpace(state) &&
                            !string.Equals(state, IdleState, StringComparison.OrdinalIgnoreCase);
        var isShoppingIntent = string.Equals(semanticIntent, "shopping_list", StringComparison.OrdinalIgnoreCase);
        var isTodoIntent = string.Equals(semanticIntent, "todo_list", StringComparison.OrdinalIgnoreCase);

        if (!isActiveState && !isShoppingIntent && !isTodoIntent)
        {
            return Task.FromResult<JiboInteractionDecision?>(null);
        }

        var resolvedListType = isShoppingIntent ? "shopping" : isTodoIntent ? "todo" : NormalizeListType(listType);
        if (string.IsNullOrWhiteSpace(resolvedListType))
        {
            resolvedListType = "shopping";
        }

        var tenantScope = tenantScopeResolver(turn);

        if (ContainsAny(loweredTranscript, "cancel", "stop", "never mind", "nevermind", "forget it"))
        {
            return Task.FromResult<JiboInteractionDecision?>(BuildCancelledDecision(resolvedListType));
        }

        if (IsRecallRequest(loweredTranscript))
        {
            return Task.FromResult<JiboInteractionDecision?>(BuildRecallDecision(
                resolvedListType,
                personalMemoryStore.GetListItems(tenantScope, resolvedListType)));
        }

        var directItem = TryExtractListItem(loweredTranscript);
        if (string.IsNullOrWhiteSpace(directItem) && isActiveState)
        {
            if (IsConversationComplete(loweredTranscript))
            {
                return Task.FromResult<JiboInteractionDecision?>(new JiboInteractionDecision(
                    resolvedListType == "shopping" ? "shopping_list_done" : "todo_list_done",
                    BuildDoneReply(resolvedListType, personalMemoryStore.GetListItems(tenantScope, resolvedListType)),
                    ContextUpdates: BuildContextUpdates(resolvedListType, IdleState)));
            }

            directItem = NormalizeItem(transcript);
        }

        if (!string.IsNullOrWhiteSpace(directItem))
        {
            personalMemoryStore.AddListItem(tenantScope, resolvedListType, directItem);
            return Task.FromResult<JiboInteractionDecision?>(new JiboInteractionDecision(
                resolvedListType == "shopping" ? "shopping_list_add" : "todo_list_add",
                BuildAddedReply(resolvedListType, directItem, personalMemoryStore.GetListItems(tenantScope, resolvedListType)),
                ContextUpdates: BuildContextUpdates(resolvedListType, AwaitingItemState)));
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return Task.FromResult<JiboInteractionDecision?>(new JiboInteractionDecision(
                resolvedListType == "shopping" ? "shopping_list_prompt" : "todo_list_prompt",
                BuildPromptReply(resolvedListType),
                ContextUpdates: BuildContextUpdates(resolvedListType, AwaitingItemState)));
        }

        return Task.FromResult<JiboInteractionDecision?>(new JiboInteractionDecision(
            resolvedListType == "shopping" ? "shopping_list_prompt" : "todo_list_prompt",
            BuildPromptReply(resolvedListType),
            ContextUpdates: BuildContextUpdates(resolvedListType, AwaitingItemState)));
    }

    private static IDictionary<string, object?> BuildContextUpdates(string listType, string state)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [StateMetadataKey] = state,
            [TypeMetadataKey] = listType,
            [NoMatchCountMetadataKey] = 0,
            [NoInputCountMetadataKey] = 0
        };
    }

    private static JiboInteractionDecision BuildCancelledDecision(string listType)
    {
        return new JiboInteractionDecision(
            listType == "shopping" ? "shopping_list_cancel" : "todo_list_cancel",
            listType == "shopping" ? "Okay. I stopped the shopping list." : "Okay. I stopped the to-do list.",
            ContextUpdates: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [StateMetadataKey] = IdleState,
                [TypeMetadataKey] = listType,
                [NoMatchCountMetadataKey] = 0,
                [NoInputCountMetadataKey] = 0
            });
    }

    private static JiboInteractionDecision BuildRecallDecision(string listType, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return new JiboInteractionDecision(
                listType == "shopping" ? "shopping_list_recall" : "todo_list_recall",
                listType == "shopping"
                    ? "Your shopping list is empty."
                    : "Your to-do list is empty.",
                ContextUpdates: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [StateMetadataKey] = IdleState,
                    [TypeMetadataKey] = listType,
                    [NoMatchCountMetadataKey] = 0,
                    [NoInputCountMetadataKey] = 0
                });
        }

        return new JiboInteractionDecision(
            listType == "shopping" ? "shopping_list_recall" : "todo_list_recall",
            listType == "shopping"
                ? $"Your shopping list has {JoinList(items)}."
                : $"Your to-do list has {JoinList(items)}.",
            ContextUpdates: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [StateMetadataKey] = IdleState,
                [TypeMetadataKey] = listType,
                [NoMatchCountMetadataKey] = 0,
                [NoInputCountMetadataKey] = 0
            });
    }

    private static string BuildAddedReply(string listType, string addedItem, IReadOnlyList<string> items)
    {
        var itemLabel = listType == "shopping" ? "shopping list" : "to-do list";
        return items.Count == 1
            ? $"Added {addedItem} to your {itemLabel}. What else should I add?"
            : $"Added {addedItem} to your {itemLabel}. You now have {JoinList(items)}.";
    }

    private static string BuildPromptReply(string listType)
    {
        return listType == "shopping"
            ? "What should I add to your shopping list?"
            : "What should I add to your to-do list?";
    }

    private static string BuildDoneReply(string listType, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return listType == "shopping"
                ? "Okay. Your shopping list is empty."
                : "Okay. Your to-do list is empty.";
        }

        return listType == "shopping"
            ? $"Okay. Your shopping list has {JoinList(items)}."
            : $"Okay. Your to-do list has {JoinList(items)}.";
    }

    private static string JoinList(IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}"
        };
    }

    private static string? TryExtractListItem(string loweredTranscript)
    {
        foreach (var prefix in ItemPrefixes)
        {
            if (!loweredTranscript.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = loweredTranscript[prefix.Length..].Trim();
            remainder = TrimTrailingListPhrases(remainder);
            return NormalizeItem(remainder);
        }

        return null;
    }

    private static bool IsRecallRequest(string loweredTranscript)
    {
        return ContainsAny(loweredTranscript,
            "what is on my shopping list",
            "what's on my shopping list",
            "show my shopping list",
            "what is on my to do list",
            "what's on my to do list",
            "show my to do list",
            "what are my tasks",
            "what do i need to buy",
            "what do i need to do");
    }

    private static string TrimTrailingListPhrases(string value)
    {
        var result = value;
        foreach (var suffix in ItemSuffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^suffix.Length].Trim();
            }
        }

        return result;
    }

    private static string NormalizeItem(string value)
    {
        return value.Trim().TrimEnd('.', ',', '!', '?');
    }

    private static string NormalizeListType(string? listType)
    {
        var normalized = NormalizeItem(listType ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("todo", StringComparison.OrdinalIgnoreCase) || normalized.Contains("to do", StringComparison.OrdinalIgnoreCase)
            ? "todo"
            : normalized.Contains("shopping", StringComparison.OrdinalIgnoreCase) || normalized.Contains("grocery", StringComparison.OrdinalIgnoreCase)
                ? "shopping"
                : string.Empty;
    }

    private static bool ContainsAny(string loweredTranscript, params string[] phrases)
    {
        return phrases.Any(phrase => loweredTranscript.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConversationComplete(string loweredTranscript)
    {
        return ContainsAny(loweredTranscript,
            "done",
            "that's it",
            "that s it",
            "all set",
            "finished",
            "no more",
            "nothing else");
    }

    private static string? ReadString(TurnContext turn, string key)
    {
        return turn.Attributes.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static readonly string[] ItemPrefixes =
    [
        "add ",
        "put ",
        "buy ",
        "get ",
        "remind me to ",
        "i need to ",
        "i need ",
        "please add ",
        "please put "
    ];

    private static readonly string[] ItemSuffixes =
    [
        " to my shopping list",
        " to the shopping list",
        " on my shopping list",
        " to my to do list",
        " to the to do list",
        " on my to do list",
        " to my todo list",
        " to the todo list",
        " on my todo list"
    ];
}
