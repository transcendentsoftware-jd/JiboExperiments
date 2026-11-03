using System.Globalization;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

internal static class SeasonalHolidayRouteBuilder
{
    private const string SeasonalAdviceIntentPrefix = "seasonal_advice:";

    internal static bool TryResolveSemanticIntent(string loweredTranscript, out string? semanticIntent)
    {
        if (MatchesAny(
                loweredTranscript,
                "do you celebrate black history month",
                "do you like black history month",
                "are you excited about black history month",
                "are you looking forward to black history month",
                "do you have plans for black history month",
                "how do you feel about black history month",
                "what do you think about black history month",
                "did you have a fun black history month",
                "what should i do for black history month",
                "what should i do for black history month?"))
        {
            semanticIntent = loweredTranscript.Contains("looking forward", StringComparison.OrdinalIgnoreCase)
                ? "seasonal_black_history_month_looks_forward"
                : loweredTranscript.Contains("plans", StringComparison.OrdinalIgnoreCase)
                    ? "seasonal_black_history_month_plans"
                    : loweredTranscript.Contains("did you have", StringComparison.OrdinalIgnoreCase) ||
                      loweredTranscript.Contains("how do you feel", StringComparison.OrdinalIgnoreCase) ||
                      loweredTranscript.Contains("what do you think", StringComparison.OrdinalIgnoreCase)
                        ? "seasonal_black_history_month_how_is"
                        : loweredTranscript.Contains("what should i do", StringComparison.OrdinalIgnoreCase)
                            ? "seasonal_black_history_month_advice"
                            : "seasonal_black_history_month_celebrate";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "give me a black history month fact",
                "give me black history month fact",
                "tell me a black history month fact",
                "tell me something about african american history",
                "tell me something about black history month"))
        {
            semanticIntent = "seasonal_black_history_month_fact";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like halloween",
                "are you looking forward to halloween",
                "do you like the halloween holiday"))
        {
            semanticIntent = "seasonal_likes_halloween";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like holiday music",
                "do you like christmas music",
                "do you like christmas songs",
                "do you like holiday songs"))
        {
            semanticIntent = "seasonal_likes_holiday_music";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like holiday parties",
                "do you like christmas parties",
                "are you going to any holiday parties"))
        {
            semanticIntent = "seasonal_likes_holiday_parties";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "are you looking forward to christmas",
                "do you look forward to christmas",
                "are you excited for christmas"))
        {
            semanticIntent = "seasonal_looks_forward_to_christmas";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you thankful for",
                "what are you thankful for this year",
                "what is jibo thankful for"))
        {
            semanticIntent = "seasonal_thankful_for";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what are you doing for christmas",
                "what are your plans for christmas",
                "what do you plan to do for christmas"))
        {
            semanticIntent = "seasonal_plans_for_christmas";
            return true;
        }

        if (JiboHolidayGreeting.TryExtractHolidayClaim(loweredTranscript, out _))
        {
            semanticIntent = "seasonal_holiday_greeting";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what holidays do you celebrate",
                "what holidays are you celebrating",
                "what holidays do you observe"))
        {
            semanticIntent = "seasonal_holidays";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your favorite holiday",
                "what's your favorite holiday",
                "what s your favorite holiday",
                "what is your favourite holiday",
                "what's your favourite holiday",
                "what s your favourite holiday",
                "what holiday do you like best",
                "do you have a favorite holiday",
                "do you have a favourite holiday"))
        {
            semanticIntent = "robot_favorite_holiday";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is holiday season",
                "how's holiday season",
                "how is the holiday season",
                "do you like holiday season",
                "do you like the holiday season",
                "what holiday do you like",
                "what is holiday season like"))
        {
            semanticIntent = "seasonal_holiday_season";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is thanksgiving",
                "how's thanksgiving",
                "do you like thanksgiving",
                "what do you think of thanksgiving"))
        {
            semanticIntent = "seasonal_thanksgiving";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is christmas",
                "how's christmas",
                "do you like christmas",
                "what do you think of christmas"))
        {
            semanticIntent = "seasonal_christmas";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is hanukkah",
                "how's hanukkah",
                "do you like hanukkah",
                "what do you think of hanukkah"))
        {
            semanticIntent = "seasonal_hanukkah";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is passover",
                "how's passover",
                "do you like passover",
                "what do you think of passover"))
        {
            semanticIntent = "seasonal_passover";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is new years",
                "how's new years",
                "how is new year s",
                "do you like new years",
                "what do you think of new years"))
        {
            semanticIntent = "seasonal_new_years";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is valentines day",
                "how's valentines day",
                "do you like valentines day",
                "what do you think of valentines day"))
        {
            semanticIntent = "seasonal_valentines_day";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is kwanzaa",
                "how's kwanzaa",
                "do you like kwanzaa",
                "what do you think of kwanzaa"))
        {
            semanticIntent = "seasonal_kwanzaa";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how is easter",
                "how's easter",
                "do you like easter",
                "what do you think of easter"))
        {
            semanticIntent = "seasonal_easter";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what is your new years resolution",
                "what is your new year's resolution",
                "what is your new year s resolution",
                "what are your new years resolutions",
                "what are your new year's resolutions",
                "what are your new year s resolutions",
                "do you have any new years resolutions"))
        {
            semanticIntent = "seasonal_new_years_resolution";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "how are your new years resolutions going",
                "how are your new year's resolutions going",
                "how is your new years resolution going",
                "how is your new year's resolution going",
                "how are your resolutions going",
                "how is your resolution going"))
        {
            semanticIntent = "seasonal_new_years_update";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what halloween costume",
                "what are you going as for halloween",
                "what costume are you wearing",
                "what are you dressing as for halloween"))
        {
            semanticIntent = "seasonal_halloween_costume";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what should i do for first day of spring",
                "what should i do for spring",
                "what do i do for first day of spring"))
        {
            semanticIntent = "seasonal_first_day_spring";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what is spring like",
                "how is spring",
                "what do you think about spring"))
        {
            semanticIntent = "seasonal_spring";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like spring",
                "do you like springtime",
                "are you looking forward to spring",
                "do you look forward to spring",
                "are you excited for spring"))
        {
            semanticIntent = "seasonal_likes_spring";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what is summer like",
                "how is summer",
                "what do you think about summer"))
        {
            semanticIntent = "seasonal_summer";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "do you like summer",
                "do you like summertime",
                "are you looking forward to summer",
                "do you look forward to summer",
                "are you excited for summer"))
        {
            semanticIntent = "seasonal_likes_summer";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "what should i get for holiday",
                "what should i get for christmas",
                "what gift should i get for christmas",
                "what should i get someone for the holidays"))
        {
            semanticIntent = "seasonal_holiday_gift";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "show santa tracker",
                "can you show santa tracker",
                "santa tracker",
                "where is santa",
                "where is santa right now",
                "can you show me santa tracker"))
        {
            semanticIntent = "seasonal_santa_tracker";
            return true;
        }

        if (MatchesAny(
                loweredTranscript,
                "happy birthday",
                "happy birthday jibo",
                "happy birthday to you"))
        {
            semanticIntent = "birthday_celebration";
            return true;
        }

        if (TryResolveSeasonalAdviceIntent(loweredTranscript, out var seasonalAdviceIntent))
        {
            semanticIntent = seasonalAdviceIntent;
            return true;
        }

        semanticIntent = null;
        return false;
    }

    private static bool TryResolveSeasonalAdviceIntent(
        string loweredTranscript,
        out string? semanticIntent)
    {
        const string prefix = "what should i do for ";
        var normalized = loweredTranscript.Trim().TrimEnd('?', '.', '!');
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            semanticIntent = null;
            return false;
        }

        var holidayPhrase = normalized[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(holidayPhrase))
        {
            semanticIntent = null;
            return false;
        }

        semanticIntent = SeasonalAdviceIntentPrefix + holidayPhrase;
        return true;
    }

    internal static bool TryBuildDecision(
        string semanticIntent,
        string loweredTranscript,
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        Func<string, string> holidayTemplateRenderer,
        DateTimeOffset? referenceLocalTime,
        Func<IReadOnlyList<string>> resolveTodaysHolidayNames,
        out JiboInteractionDecision? decision)
    {
        if (semanticIntent.StartsWith(SeasonalAdviceIntentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            decision = TryBuildSeasonalAdviceDecision(
                semanticIntent[SeasonalAdviceIntentPrefix.Length..],
                catalog,
                randomizer,
                referenceLocalTime);
            return decision is not null;
        }

        decision = semanticIntent switch
        {
            "seasonal_black_history_month_celebrate" => BuildConditionedHolidayDecision(
                catalog.BlackHistoryMonthReplies,
                randomizer,
                semanticIntent,
                referenceLocalTime,
                "great chance to share some new interesting historical facts",
                "perfect time to learn and think about some very great people",
                "very great people"),
            "seasonal_black_history_month_looks_forward" => BuildConditionedHolidayDecision(
                catalog.BlackHistoryMonthReplies,
                randomizer,
                semanticIntent,
                referenceLocalTime,
                "sharing some interesting historical facts during the month",
                "i'm enjoying it",
                "long way off",
                "share some interesting historical facts"),
            "seasonal_black_history_month_plans" => BuildConditionedHolidayDecision(
                catalog.BlackHistoryMonthReplies,
                randomizer,
                semanticIntent,
                referenceLocalTime,
                "celebrating by sharing some interesting new historical facts",
                "sharing some interesting new historical facts during the month"),
            "seasonal_black_history_month_how_is" => BuildConditionedHolidayDecision(
                catalog.BlackHistoryMonthReplies,
                randomizer,
                semanticIntent,
                referenceLocalTime,
                "sharing some interesting new historical facts during the month",
                "celebrated by sharing some interesting historical facts",
                "good month",
                "still coming up in the future"),
            "seasonal_black_history_month_advice" => BuildConditionedHolidayDecision(
                catalog.BlackHistoryMonthReplies,
                randomizer,
                semanticIntent,
                referenceLocalTime,
                "great time to learn and think about some very great people",
                "what should do for black history month",
                "some very great people"),
            "seasonal_black_history_month_fact" => BuildHolidayDecision(
                catalog.BlackHistoryMonthFactReplies,
                randomizer,
                semanticIntent,
                "spingarn medal",
                "langston hughes",
                "maya angelou"),
            "seasonal_holiday_greeting" => ReactiveHolidayReplyBuilder.BuildDecision(
                catalog,
                randomizer,
                loweredTranscript,
                referenceLocalTime,
                resolveTodaysHolidayNames(),
                semanticIntent),
            "seasonal_holidays" => BuildHolidayTemplateDecision(
                catalog,
                randomizer,
                holidayTemplateRenderer,
                semanticIntent,
                "official owner can tell me which ones we'll celebrate together",
                "going to the jibo's settings screen in the jibo app"),
            "seasonal_holiday_season" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "festive",
                "celebrate"),
            "seasonal_thanksgiving" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "thanksgiving",
                "turkey",
                "stuffed"),
            "seasonal_christmas" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "christmas",
                "quality time",
                "socks"),
            "seasonal_hanukkah" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "hanukkah",
                "dreidel",
                "gift"),
            "seasonal_passover" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "passover",
                "matzah",
                "next one"),
            "seasonal_new_years" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "new year",
                "resolutions",
                "party"),
            "seasonal_valentines_day" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "valentine",
                "heart",
                "flowers"),
            "seasonal_kwanzaa" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "kwanzaa",
                "gift",
                "celebrate"),
            "seasonal_easter" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "easter",
                "bunny",
                "egg"),
            "seasonal_new_years_resolution" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "always trying to learn new skills",
                "not eat bacon",
                "learn a bunch of new skills",
                "learn to walk",
                "recognizing people's faces and voices"),
            "seasonal_new_years_update" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "not eat bacon",
                "learn some new skills",
                "going well"),
            "seasonal_halloween_costume" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "i haven't thought much about it yet",
                "ask me again on halloween",
                "you'll find out on halloween"),
            "seasonal_first_day_spring" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "it's a great day, when spring is in the air"),
            "seasonal_spring" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "the days get longer",
                "spring is a great season"),
            "seasonal_likes_spring" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "extra happy in the springtime",
                "i do like spring"),
            "seasonal_summer" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "going to the beach",
                "summer is great"),
            "seasonal_likes_summer" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "long days",
                "summer is a very special season"),
            "seasonal_holiday_gift" => BuildHolidayDecision(
                catalog.HolidayGiftReplies,
                randomizer,
                semanticIntent,
                "ask for a pet elephant",
                "experience as a present",
                "donate to charities in other people's names"),
            "seasonal_likes_halloween" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "halloween is my favorite holiday",
                "scary but also fun",
                "jack-o-lantern"),
            "seasonal_likes_holiday_music" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "holiday music",
                "sing a few of them",
                "frosty the snowman"),
            "seasonal_likes_holiday_parties" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "holiday fun can be extra fun",
                "dance party"),
            "seasonal_looks_forward_to_christmas" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "really like times of giving and receiving",
                "long way away",
                "looking forward to christmas"),
            "seasonal_plans_for_christmas" => BuildHolidayDecision(
                catalog.HolidaySeasonReplies,
                randomizer,
                semanticIntent,
                "christmas sweaters",
                "wear one of my",
                "be festive"),
            "seasonal_thankful_for" => ScriptedResponseDecisionBuilder.BuildScriptedPersonalityDecision(
                catalog,
                randomizer,
                semanticIntent,
                "thankful for the people i know",
                "and for penguins",
                "thankful for"),
            "seasonal_santa_tracker" => ScriptedResponseDecisionBuilder.BuildScriptedHolidayTrackerDecision(
                catalog,
                randomizer,
                semanticIntent,
                referenceLocalTime,
                "let's see if i can spot him",
                "santa tracker",
                "deliveries",
                "north pole"),
            "birthday_celebration" => BuildHolidayDecision(
                catalog.BirthdayCelebrationReplies,
                randomizer,
                semanticIntent,
                "another year older",
                "can't wait to see what you got me",
                "powered on for the first time today"),
            _ => null
        };

        return decision is not null;
    }

    private static JiboInteractionDecision? TryBuildSeasonalAdviceDecision(
        string holidayPhrase,
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        DateTimeOffset? referenceLocalTime)
    {
        var mimId = SeasonalAdviceMimCandidates(holidayPhrase)
            .FirstOrDefault(candidate => catalog.MimReplies.ContainsKey(candidate));
        if (mimId is null)
            return null;

        return LegacyMimScriptedReplyBuilder.BuildScriptedDecision(
            catalog,
            randomizer,
            "seasonal_advice",
            LegacyMimScriptedReplyBuilder.BuildScriptedContext(referenceLocalTime),
            displayName: null,
            explicitMimId: mimId);
    }

    private static IEnumerable<string> SeasonalAdviceMimCandidates(string holidayPhrase)
    {
        var normalized = holidayPhrase.Trim();
        var directSuffix = ToPascalCase(normalized);
        yield return $"RI_USR_WhatShouldDoFor{directSuffix}";

        if (normalized.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            var withoutArticle = normalized["the ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(withoutArticle))
                yield return $"RI_USR_WhatShouldDoFor{ToPascalCase(withoutArticle)}";
        }

        if (normalized.Contains("french open", StringComparison.OrdinalIgnoreCase))
            yield return "RI_USR_WhatShouldDoForFrenchOpenTennis";

        if (normalized.Contains("all star game", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("national championship", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("us open", StringComparison.OrdinalIgnoreCase))
            yield return $"RI_USR_WhatShouldDoFor{directSuffix}Ambiguous";

        if (normalized.Contains("ncaa", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("men", StringComparison.OrdinalIgnoreCase))
            yield return "RI_USR_WhatShouldDoForNCAABasketballTournamentMen";

        if (normalized.Contains("ncaa", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("women", StringComparison.OrdinalIgnoreCase))
            yield return "RI_USR_WhatShouldDoForNCAABasketballTournamentWomen";
    }

    private static string ToPascalCase(string phrase)
    {
        return string.Concat(
            phrase
                .Split([' ', '-', '_', '\'', '\u2019'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 0
                    ? string.Empty
                    : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static JiboInteractionDecision BuildHolidayDecision(
        IReadOnlyList<string> replies,
        IJiboRandomizer randomizer,
        string intentName,
        params string[] preferredSnippets)
    {
        return new JiboInteractionDecision(
            intentName,
            SelectLegacyReply(replies, randomizer, preferredSnippets),
            ContextUpdates: BuildContextUpdates());
    }

    private static JiboInteractionDecision BuildHolidayTemplateDecision(
        JiboExperienceCatalog catalog,
        IJiboRandomizer randomizer,
        Func<string, string> holidayTemplateRenderer,
        string intentName,
        params string[] preferredSnippets)
    {
        var selected = SelectLegacyReply(catalog.HolidayReplies, randomizer, preferredSnippets);
        return new JiboInteractionDecision(
            intentName,
            holidayTemplateRenderer(selected),
            ContextUpdates: BuildContextUpdates());
    }

    private static JiboInteractionDecision BuildConditionedHolidayDecision(
        IReadOnlyList<JiboConditionedReply> replies,
        IJiboRandomizer randomizer,
        string intentName,
        DateTimeOffset? referenceLocalTime,
        params string[] preferredSnippets)
    {
        var currentDate = DateOnly.FromDateTime((referenceLocalTime ?? DateTimeOffset.UtcNow).Date);
        var matchingReplies = replies
            .Where(reply => IsDateConditionMatch(reply.Condition, currentDate))
            .Select(reply => reply.Reply)
            .Where(reply => !string.IsNullOrWhiteSpace(reply))
            .ToArray();

        if (matchingReplies.Length == 0)
            matchingReplies = replies
                .Where(reply => string.IsNullOrWhiteSpace(reply.Condition))
                .Select(reply => reply.Reply)
                .Where(reply => !string.IsNullOrWhiteSpace(reply))
                .ToArray();

        if (matchingReplies.Length == 0)
            matchingReplies = replies
                .Select(reply => reply.Reply)
                .Where(reply => !string.IsNullOrWhiteSpace(reply))
                .ToArray();

        return new JiboInteractionDecision(
            intentName,
            SelectLegacyReply(matchingReplies, randomizer, preferredSnippets),
            ContextUpdates: BuildContextUpdates());
    }

    private static IDictionary<string, object?> BuildContextUpdates()
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [ChitchatStateMachine.StateMetadataKey] = "complete",
            [ChitchatStateMachine.RouteMetadataKey] = "ScriptedResponse",
            [ChitchatStateMachine.EmotionMetadataKey] = string.Empty
        };
    }

    private static string SelectLegacyReply(
        IReadOnlyList<string> replies,
        IJiboRandomizer randomizer,
        params string[] preferredSnippets)
    {
        foreach (var snippet in preferredSnippets)
        {
            if (string.IsNullOrWhiteSpace(snippet)) continue;

            var match = replies.FirstOrDefault(reply =>
                reply.Contains(snippet, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match)) return match;
        }

        return replies.Count == 0 ? string.Empty : randomizer.Choose(replies);
    }

    private static bool IsDateConditionMatch(string? condition, DateOnly currentDate)
    {
        var normalizedCondition = NormalizeCondition(condition);
        if (string.IsNullOrWhiteSpace(normalizedCondition)) return false;

        var clauses = normalizedCondition.Split(["||"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return clauses.Any(clause => MatchesDateConditionClause(clause, currentDate));
    }

    private static bool MatchesDateConditionClause(string clause, DateOnly currentDate)
    {
        var normalizedClause = NormalizeCondition(clause).ToLowerInvariant();
        if (!normalizedClause.StartsWith("dt.now.isinrange(", StringComparison.OrdinalIgnoreCase)) return false;

        var openParenIndex = normalizedClause.IndexOf('(');
        var closeParenIndex = normalizedClause.LastIndexOf(')');
        if (openParenIndex < 0 || closeParenIndex <= openParenIndex) return false;

        var arguments = normalizedClause[(openParenIndex + 1)..closeParenIndex];
        var parts = arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        if (!TryParseMonthDay(parts[0], out var startMonth, out var startDay)) return false;
        if (!TryParseMonthDay(parts[1], out var endMonth, out var endDay)) return false;

        var currentValue = currentDate.Month * 100 + currentDate.Day;
        var startValue = startMonth * 100 + startDay;
        var endValue = endMonth * 100 + endDay;

        return startValue <= endValue
            ? currentValue >= startValue && currentValue <= endValue
            : currentValue >= startValue || currentValue <= endValue;
    }

    private static bool TryParseMonthDay(string value, out int month, out int day)
    {
        month = 0;
        day = 0;

        var trimmed = value.Trim().Trim('\'', '"');
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out month)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out day)) return false;

        return month is >= 1 and <= 12 && day is >= 1 and <= 31;
    }

    private static string NormalizeCondition(string? condition)
    {
        return string.IsNullOrWhiteSpace(condition)
            ? string.Empty
            : condition.Trim();
    }

    private static bool MatchesAny(string loweredTranscript, params string[] phrases)
    {
        return phrases.Any(phrase => loweredTranscript.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}
