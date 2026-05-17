using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Infrastructure.Content;

public static class LegacyMimCatalogImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    private static readonly Regex LegacyMarkupPattern = new(
        @"<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlaceholderPattern = new(
        @"\$\{[^}]+\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SpaceBeforePunctuationPattern = new(
        @"\s+([,.;:!?])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static JiboExperienceCatalog MergeInto(
        JiboExperienceCatalog baseCatalog,
        string? rootDirectory)
    {
        if (baseCatalog is null)
        {
            throw new ArgumentNullException(nameof(baseCatalog));
        }

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return baseCatalog;
        }

        var importedCatalog = ImportCatalog(rootDirectory);
        return MergeCatalogs(baseCatalog, importedCatalog);
    }

    public static JiboExperienceCatalog ImportCatalog(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return new JiboExperienceCatalog();
        }

        var builder = new LegacyMimCatalogBuilder();
        foreach (var filePath in Directory.EnumerateFiles(rootDirectory, "*.mim", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryLoadDefinition(filePath, out var definition))
            {
                continue;
            }

            var bucket = ResolveBucket(filePath);
            if (bucket is null)
            {
                continue;
            }

            foreach (var prompt in definition.Prompts)
            {
                var text = NormalizePrompt(prompt.Prompt, preservePlaceholders: IsTemplateBucket(bucket.Value));
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                builder.Add(bucket.Value, prompt.Condition, text);
            }
        }

        return builder.Build();
    }

    private static bool TryLoadDefinition(string filePath, out LegacyMimDefinition definition)
    {
        definition = new LegacyMimDefinition();
        try
        {
            var json = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<LegacyMimDefinition>(json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            definition = parsed;
            return definition.Prompts.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static LegacyMimBucket? ResolveBucket(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        if (normalizedPath.Contains("/core-responses/", StringComparison.OrdinalIgnoreCase) &&
            fileName.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.GenericFallback;
        }

        if (normalizedPath.Contains("/core-responses/deflector/", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Deflector", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Personality;
        }

        if (normalizedPath.Contains("/emotion-responses/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/gqa-responses/", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Emotion;
        }

        if (fileName.StartsWith("WeatherIntroTomorrow", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.WeatherTomorrowIntro;
        }

        if (fileName.StartsWith("WeatherIntro", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.WeatherIntro;
        }

        if (fileName.StartsWith("WeatherTomorrowHighLow", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.WeatherTomorrowHighLow;
        }

        if (fileName.StartsWith("WeatherTodayHighLow", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.WeatherTodayHighLow;
        }

        if (fileName.StartsWith("WeatherServiceDown", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.WeatherServiceDown;
        }

        if (fileName.StartsWith("Weather", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "WetNowDryLater", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.ReportSkillTemplate;
        }

        if (fileName.StartsWith("PersonalReportKickOff", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.PersonalReportKickOff;
        }

        if (fileName.StartsWith("PersonalReportOutro", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.PersonalReportOutro;
        }

        if (fileName.StartsWith("PersonalReport", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Calendar", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Commute", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("News", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.ReportSkillTemplate;
        }

        if (fileName.StartsWith("JBO_DoYouLikeBeingJibo", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhatIsJibo", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhoAreYou", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhatAreYou", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_HowDoYouWork", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_HowMuchDoYouKnow", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_HowOldAreYou", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhenWereYouBorn", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhatsYourName", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhereDoYouGetInfo", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("JBO_WhatDoYouLikeToDo", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Personality;
        }

        if (fileName.StartsWith("OI_JBO_Is", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("OI_JBO_Seems", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("RI_JBO_IsHappy", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("RI_JBO_IsSad", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("RI_JBO_IsAngry", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("RN_WhatAreYouFeeling", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Emotion;
        }

        if (fileName.Contains("Greeting", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("RN_", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Welcome", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Greeting;
        }

        if (normalizedPath.Contains("/scripted-responses/", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Personality;
        }

        return null;
    }

    private static string NormalizePrompt(string? prompt)
    {
        return NormalizePrompt(prompt, preservePlaceholders: false);
    }

    private static string NormalizePrompt(string? prompt, bool preservePlaceholders)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        var text = WebUtility.HtmlDecode(prompt);
        if (!preservePlaceholders)
        {
            text = PlaceholderPattern.Replace(text, " ");
        }
        text = LegacyMarkupPattern.Replace(text, " ");
        text = WhitespacePattern.Replace(text, " ").Trim();
        text = SpaceBeforePunctuationPattern.Replace(text, "$1");
        text = WhitespacePattern.Replace(text, " ").Trim();
        text = text.TrimStart('.', ',', ';', ':', '!', '?', ' ');
        return text.Trim();
    }

    private static JiboExperienceCatalog MergeCatalogs(
        JiboExperienceCatalog baseCatalog,
        JiboExperienceCatalog importedCatalog)
    {
        return new JiboExperienceCatalog
        {
            Jokes = Merge(baseCatalog.Jokes, importedCatalog.Jokes),
            DanceAnimations = Merge(baseCatalog.DanceAnimations, importedCatalog.DanceAnimations),
            GreetingReplies = Merge(baseCatalog.GreetingReplies, importedCatalog.GreetingReplies),
            HowAreYouReplies = Merge(baseCatalog.HowAreYouReplies, importedCatalog.HowAreYouReplies),
            EmotionReplies = Merge(baseCatalog.EmotionReplies, importedCatalog.EmotionReplies),
            PersonalityReplies = Merge(baseCatalog.PersonalityReplies, importedCatalog.PersonalityReplies),
            PizzaReplies = Merge(baseCatalog.PizzaReplies, importedCatalog.PizzaReplies),
            SurpriseReplies = Merge(baseCatalog.SurpriseReplies, importedCatalog.SurpriseReplies),
            PersonalReportReplies = Merge(baseCatalog.PersonalReportReplies, importedCatalog.PersonalReportReplies),
            PersonalReportKickOffReplies = Merge(baseCatalog.PersonalReportKickOffReplies, importedCatalog.PersonalReportKickOffReplies),
            PersonalReportOutroReplies = Merge(baseCatalog.PersonalReportOutroReplies, importedCatalog.PersonalReportOutroReplies),
            ReportSkillTemplates = Merge(baseCatalog.ReportSkillTemplates, importedCatalog.ReportSkillTemplates),
            WeatherIntroReplies = Merge(baseCatalog.WeatherIntroReplies, importedCatalog.WeatherIntroReplies),
            WeatherTomorrowIntroReplies = Merge(baseCatalog.WeatherTomorrowIntroReplies, importedCatalog.WeatherTomorrowIntroReplies),
            WeatherTodayHighLowReplies = Merge(baseCatalog.WeatherTodayHighLowReplies, importedCatalog.WeatherTodayHighLowReplies),
            WeatherTomorrowHighLowReplies = Merge(baseCatalog.WeatherTomorrowHighLowReplies, importedCatalog.WeatherTomorrowHighLowReplies),
            WeatherServiceDownReplies = Merge(baseCatalog.WeatherServiceDownReplies, importedCatalog.WeatherServiceDownReplies),
            WeatherReplies = Merge(baseCatalog.WeatherReplies, importedCatalog.WeatherReplies),
            CalendarReplies = Merge(baseCatalog.CalendarReplies, importedCatalog.CalendarReplies),
            CommuteReplies = Merge(baseCatalog.CommuteReplies, importedCatalog.CommuteReplies),
            NewsReplies = Merge(baseCatalog.NewsReplies, importedCatalog.NewsReplies),
            NewsBriefings = Merge(baseCatalog.NewsBriefings, importedCatalog.NewsBriefings),
            GenericFallbackReplies = Merge(baseCatalog.GenericFallbackReplies, importedCatalog.GenericFallbackReplies),
            DanceReplies = Merge(baseCatalog.DanceReplies, importedCatalog.DanceReplies),
            DanceQuestionReplies = Merge(baseCatalog.DanceQuestionReplies, importedCatalog.DanceQuestionReplies)
        };
    }

    private static IReadOnlyList<string> Merge(IReadOnlyList<string> baseList, IReadOnlyList<string> importedList)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var value in baseList.Concat(importedList))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim();
            if (!seen.Add(normalized))
            {
                continue;
            }

            merged.Add(normalized);
        }

        return merged;
    }

    private static IReadOnlyList<JiboConditionedReply> Merge(
        IReadOnlyList<JiboConditionedReply> baseList,
        IReadOnlyList<JiboConditionedReply> importedList)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<JiboConditionedReply>();

        foreach (var value in baseList.Concat(importedList))
        {
            if (string.IsNullOrWhiteSpace(value.Reply))
            {
                continue;
            }

            var normalizedCondition = NormalizeCondition(value.Condition);
            var normalizedReply = value.Reply.Trim();
            var key = $"{normalizedCondition}::{normalizedReply}";
            if (!seen.Add(key))
            {
                continue;
            }

            merged.Add(new JiboConditionedReply
            {
                Condition = normalizedCondition,
                Reply = normalizedReply
            });
        }

        return merged;
    }

    private enum LegacyMimBucket
    {
        GenericFallback,
        Greeting,
        HowAreYou,
        Emotion,
        Personality,
        PersonalReportKickOff,
        PersonalReportOutro,
        WeatherIntro,
        WeatherTomorrowIntro,
        WeatherTodayHighLow,
        WeatherTomorrowHighLow,
        WeatherServiceDown,
        ReportSkillTemplate
    }

    private sealed class LegacyMimCatalogBuilder
    {
        private readonly List<string> _greetings = [];
        private readonly List<string> _howAreYous = [];
        private readonly List<JiboConditionedReply> _emotionReplies = [];
        private readonly List<string> _personalities = [];
        private readonly List<string> _fallbacks = [];
        private readonly List<string> _personalReportKickOffReplies = [];
        private readonly List<string> _personalReportOutroReplies = [];
        private readonly List<string> _reportSkillTemplates = [];
        private readonly List<string> _weatherIntroReplies = [];
        private readonly List<string> _weatherTomorrowIntroReplies = [];
        private readonly List<string> _weatherTodayHighLowReplies = [];
        private readonly List<string> _weatherTomorrowHighLowReplies = [];
        private readonly List<string> _weatherServiceDownReplies = [];

        public void Add(LegacyMimBucket bucket, string? condition, string text)
        {
            switch (bucket)
            {
                case LegacyMimBucket.GenericFallback:
                    if (_fallbacks.Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    _fallbacks.Add(text);
                    return;
                case LegacyMimBucket.Greeting:
                    if (_greetings.Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    _greetings.Add(text);
                    return;
                case LegacyMimBucket.HowAreYou:
                    if (_howAreYous.Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    _howAreYous.Add(text);
                    return;
                case LegacyMimBucket.Emotion:
                    var normalizedCondition = NormalizeCondition(condition);
                    if (_emotionReplies.Any(value =>
                            string.Equals(NormalizeCondition(value.Condition), normalizedCondition, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(value.Reply, text, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    _emotionReplies.Add(new JiboConditionedReply
                    {
                        Condition = normalizedCondition,
                        Reply = text
                    });
                    return;
                case LegacyMimBucket.Personality:
                    if (_personalities.Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    _personalities.Add(text);
                    return;
                case LegacyMimBucket.PersonalReportKickOff:
                    AddDistinct(_personalReportKickOffReplies, text);
                    return;
                case LegacyMimBucket.PersonalReportOutro:
                    AddDistinct(_personalReportOutroReplies, text);
                    return;
                case LegacyMimBucket.WeatherIntro:
                    AddDistinct(_weatherIntroReplies, text);
                    return;
                case LegacyMimBucket.WeatherTomorrowIntro:
                    AddDistinct(_weatherTomorrowIntroReplies, text);
                    return;
                case LegacyMimBucket.WeatherTodayHighLow:
                    AddDistinct(_weatherTodayHighLowReplies, text);
                    return;
                case LegacyMimBucket.WeatherTomorrowHighLow:
                    AddDistinct(_weatherTomorrowHighLowReplies, text);
                    return;
                case LegacyMimBucket.WeatherServiceDown:
                    AddDistinct(_weatherServiceDownReplies, text);
                    return;
                case LegacyMimBucket.ReportSkillTemplate:
                    AddDistinct(_reportSkillTemplates, text);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null);
            }
        }

        public JiboExperienceCatalog Build()
        {
            return new JiboExperienceCatalog
            {
                GreetingReplies = [.. _greetings],
                HowAreYouReplies = [.. _howAreYous],
                EmotionReplies = [.. _emotionReplies],
                PersonalityReplies = [.. _personalities],
                GenericFallbackReplies = [.. _fallbacks],
                PersonalReportKickOffReplies = [.. _personalReportKickOffReplies],
                PersonalReportOutroReplies = [.. _personalReportOutroReplies],
                ReportSkillTemplates = [.. _reportSkillTemplates],
                WeatherIntroReplies = [.. _weatherIntroReplies],
                WeatherTomorrowIntroReplies = [.. _weatherTomorrowIntroReplies],
                WeatherTodayHighLowReplies = [.. _weatherTodayHighLowReplies],
                WeatherTomorrowHighLowReplies = [.. _weatherTomorrowHighLowReplies],
                WeatherServiceDownReplies = [.. _weatherServiceDownReplies]
            };
        }

        private static void AddDistinct(List<string> target, string text)
        {
            if (target.Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            target.Add(text);
        }
    }

    private sealed class LegacyMimDefinition
    {
        [JsonPropertyName("skill_id")]
        public string? SkillId { get; init; }

        [JsonPropertyName("mim_id")]
        public string? MimId { get; init; }

        [JsonPropertyName("mim_type")]
        public string? MimType { get; init; }

        [JsonPropertyName("prompts")]
        public List<LegacyMimPrompt> Prompts { get; init; } = [];
    }

    private sealed class LegacyMimPrompt
    {
        [JsonPropertyName("mim_id")]
        public string? MimId { get; init; }

        [JsonPropertyName("prompt_category")]
        public string? PromptCategory { get; init; }

        [JsonPropertyName("prompt_sub_category")]
        public string? PromptSubCategory { get; init; }

        [JsonPropertyName("condition")]
        public string? Condition { get; init; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; init; }

        [JsonPropertyName("prompt_id")]
        public string? PromptId { get; init; }

        [JsonPropertyName("weight")]
        public double? Weight { get; init; }
    }

    private static string NormalizeCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return string.Empty;
        }

        return WhitespacePattern.Replace(condition.Trim(), " ");
    }

    private static bool IsTemplateBucket(LegacyMimBucket bucket)
    {
        return bucket is LegacyMimBucket.PersonalReportKickOff
            or LegacyMimBucket.PersonalReportOutro
            or LegacyMimBucket.WeatherIntro
            or LegacyMimBucket.WeatherTomorrowIntro
            or LegacyMimBucket.WeatherTodayHighLow
            or LegacyMimBucket.WeatherTomorrowHighLow
            or LegacyMimBucket.WeatherServiceDown
            or LegacyMimBucket.ReportSkillTemplate;
    }
}
