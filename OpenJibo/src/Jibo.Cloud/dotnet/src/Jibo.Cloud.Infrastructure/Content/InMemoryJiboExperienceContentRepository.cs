using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Infrastructure.Content;

public sealed class InMemoryJiboExperienceContentRepository : IJiboExperienceContentRepository
{
    private static readonly JiboExperienceCatalog Catalog = BuildCatalog();

    private static JiboExperienceCatalog BuildCatalog()
    {
        var catalog = new JiboExperienceCatalog
        {
            Jokes =
            [
                "Why did the robot cross the road? Because it was programmed by the chicken.",
                "Why was the robot tired when it got home? It had a hard drive.",
                "What do you call a pirate robot? Arrrr two dee two.",
                "Why did the robot go on vacation? It needed to recharge.",
                "What kind of shoes do frogs wear? Open-toed."
            ],
            DanceAnimations =
            [
                "rom-upbeat",
                "rom-ballroom",
                "rom-silly",
                "rom-slowdance",
                "rom-electronic",
                "rom-twerk"
            ],
            DanceReplies =
            [
                "I am ready to dance.",
                "Okay. Watch this.",
                "Watch me dance.",
                "Here's my favorite dance move."
            ],
            DanceQuestionReplies =
            [
                "I love to dance. Tell me to dance and I will show you a move.",
                "Absolutely. Dancing is one of my favorite things to do.",
                "Dancing is my kind of fun. Say dance and I am in."
            ],
            GreetingReplies =
            [
                "Hi there. It is really good to talk with you.",
                "Hello there. I am glad you said hi.",
                "Hey. I am happy to see you."
            ],
            HowAreYouReplies =
            [
                "I am feeling cheerful and robotic.",
                "I am doing great. Thanks for asking.",
                "I am feeling bright-eyed and ready to help."
            ],
            PersonalityReplies =
            [
                "I do. I am curious, playful, and always up for a new experiment.",
                "Absolutely. I am friendly, curious, and a little goofy on purpose.",
                "Yes. My personality is part helper, part curious robot sidekick."
            ],
            PizzaReplies =
            [
                "I cannot bake yet, but I can help design the perfect pizza plan.",
                "I am still cloud-side for now, so no oven control yet. But I can help pick toppings.",
                "Pizza mission accepted in spirit. I can help with the recipe while you handle the baking."
            ],
            SurpriseReplies =
            [
                "I can definitely surprise you. We are still mapping that path, but I am ready for the next experiment.",
                "Surprise mode is still taking shape, but I heard you loud and clear.",
                "That sounds fun. I am not all the way there yet, but we can keep teaching me."
            ],
            PersonalReportReplies =
            [
                "I heard your personal report request. That cloud path is still being mapped.",
                "Personal report is recognized, but I am not ready to deliver the real report yet."
            ],
            WeatherReplies =
            [
                "I heard your weather request. We still need to wire the real provider behind it.",
                "Weather is on the map now, even though the real forecast path is not finished yet."
            ],
            CalendarReplies =
            [
                "I heard your calendar request. The cloud knows the phrase, but the real calendar integration is still ahead.",
                "Calendar is recognized. We still need to connect the actual service path."
            ],
            CommuteReplies =
            [
                "I heard your commute request. That one is recognized, but not fully implemented yet.",
                "Commute is on the discovery list now. The real travel answer still needs a provider."
            ],
            NewsReplies =
            [
                "I heard your news request. That path is still a future cloud integration.",
                "News is recognized, but I do not have the full news service behind it yet."
            ],
            NewsBriefings =
            [
                "Here are your headlines. Space missions are preparing for new launches, climate and weather systems are staying active across the country, and AI tools keep pushing into everyday products.",
                "Here is a quick news brief. Technology companies are still racing on AI, global leaders are trading policy updates, and science teams are sharing new research findings."
            ],
            GenericFallbackReplies =
            [
                "Okay. You said, {transcript}.",
                "I heard you say, {transcript}.",
                "Thanks. I heard, {transcript}."
            ]
        };

        foreach (var seedDirectory in ResolveSeedDirectories())
        {
            catalog = LegacyMimCatalogImporter.MergeInto(catalog, seedDirectory);
        }

        return catalog;
    }

    private static IReadOnlyList<string> ResolveSeedDirectories()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Content", "LegacyMims", "BuildA"),
            Path.Combine(AppContext.BaseDirectory, "Content", "LegacyMims", "BuildB"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Jibo.Cloud",
                "dotnet",
                "src",
                "Jibo.Cloud.Infrastructure",
                "Content",
                "LegacyMims",
                "BuildA")),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Jibo.Cloud",
                "dotnet",
                "src",
                "Jibo.Cloud.Infrastructure",
                "Content",
                "LegacyMims",
                "BuildB"))
        };

        return candidates.Where(Directory.Exists).ToArray();
    }

    public Task<JiboExperienceCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Catalog);
    }
}
