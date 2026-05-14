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
        PropertyNameCaseInsensitive = true
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
                var text = NormalizePrompt(prompt.Prompt);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                builder.Add(bucket.Value, text);
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
            fileName.StartsWith("RI_JBO_Is", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("RN_WhatAreYouFeeling", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.HowAreYou;
        }

        if (fileName.Contains("Greeting", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Welcome", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyMimBucket.Greeting;
        }

        return null;
    }

    private static string NormalizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        var text = WebUtility.HtmlDecode(prompt);
        text = PlaceholderPattern.Replace(text, " ");
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
            PersonalityReplies = Merge(baseCatalog.PersonalityReplies, importedCatalog.PersonalityReplies),
            PizzaReplies = Merge(baseCatalog.PizzaReplies, importedCatalog.PizzaReplies),
            SurpriseReplies = Merge(baseCatalog.SurpriseReplies, importedCatalog.SurpriseReplies),
            PersonalReportReplies = Merge(baseCatalog.PersonalReportReplies, importedCatalog.PersonalReportReplies),
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

    private enum LegacyMimBucket
    {
        GenericFallback,
        Greeting,
        HowAreYou,
        Personality
    }

    private sealed class LegacyMimCatalogBuilder
    {
        private readonly List<string> _greetings = [];
        private readonly List<string> _howAreYous = [];
        private readonly List<string> _personalities = [];
        private readonly List<string> _fallbacks = [];

        public void Add(LegacyMimBucket bucket, string text)
        {
            var target = bucket switch
            {
                LegacyMimBucket.GenericFallback => _fallbacks,
                LegacyMimBucket.Greeting => _greetings,
                LegacyMimBucket.HowAreYou => _howAreYous,
                LegacyMimBucket.Personality => _personalities,
                _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
            };

            if (target.Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            target.Add(text);
        }

        public JiboExperienceCatalog Build()
        {
            return new JiboExperienceCatalog
            {
                GreetingReplies = [.. _greetings],
                HowAreYouReplies = [.. _howAreYous],
                PersonalityReplies = [.. _personalities],
                GenericFallbackReplies = [.. _fallbacks]
            };
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
        public int? Weight { get; init; }
    }
}
