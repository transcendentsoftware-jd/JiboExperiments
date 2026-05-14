using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Content;

namespace Jibo.Cloud.Tests.Content;

public sealed class LegacyMimCatalogImporterTests
{
    [Fact]
    public void ImportCatalog_MapsSeedFilesIntoExpectedBuckets()
    {
        var rootDirectory = CreateSeedDirectory();

        try
        {
            var catalog = LegacyMimCatalogImporter.ImportCatalog(rootDirectory);

            Assert.Contains("Something's off with the connection to my sources. Maybe ask me again in a little while.", catalog.GenericFallbackReplies);
            Assert.Contains("I think only you can answer that question.", catalog.PersonalityReplies);
            Assert.Contains("Jibo. Just Jibo, no last name. Like Bono", catalog.PersonalityReplies);
            Assert.Contains("No, I'm one in one million.", catalog.PersonalityReplies);
            Assert.Contains("I know a lot, I think. But not as much as I will someday.", catalog.PersonalityReplies);
            Assert.Contains("I don't think of it as a job, because it's more fun than a job. But I'm here to help you out, and have fun with you, and maybe get my head patted by you occasionally.", catalog.PersonalityReplies);
            Assert.Contains(catalog.EmotionReplies, reply =>
                reply.Condition.Contains("NEUTRAL", StringComparison.OrdinalIgnoreCase) &&
                reply.Reply.Contains("All systems are go.", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("A Jibo is a robot. But I'm not just a machine, I have a heart. Well, not a real heart. But feelings. Well, not human feelings. You know what I mean.", catalog.PersonalityReplies);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void ImportCatalog_MapsGqaResponsesIntoEmotionBucket()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rootDirectory, "gqa-responses"));

        try
        {
            File.WriteAllText(
                Path.Combine(rootDirectory, "gqa-responses", "GQA_JBO_IsHappy.mim"),
                """
                {
                  "mim_type": "announcement",
                  "prompts": [
                    {
                      "condition": "jibo.emotion==\"JOYFUL\"",
                      "prompt": "GQA joyful reply.",
                      "prompt_id": "GQA_JBO_IsHappy_AN_01"
                    },
                    {
                      "condition": "!jibo.emotion || jibo.emotion==\"NEUTRAL\"",
                      "prompt": "GQA neutral reply.",
                      "prompt_id": "GQA_JBO_IsHappy_AN_02"
                    }
                  ]
                }
                """);

            var catalog = LegacyMimCatalogImporter.ImportCatalog(rootDirectory);

            Assert.Contains(catalog.EmotionReplies, reply =>
                string.Equals(reply.Reply, "GQA joyful reply.", StringComparison.Ordinal));
            Assert.Contains(catalog.EmotionReplies, reply =>
                string.Equals(reply.Reply, "GQA neutral reply.", StringComparison.Ordinal));
            Assert.DoesNotContain(catalog.HowAreYouReplies, reply =>
                reply.Contains("GQA", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void MergeInto_PreservesExistingCatalogAndAddsImportedContent()
    {
        var rootDirectory = CreateSeedDirectory();

        try
        {
            var baseCatalog = new JiboExperienceCatalog
            {
                GreetingReplies = ["Hello from base."],
                GenericFallbackReplies = ["Base fallback."]
            };

            var merged = LegacyMimCatalogImporter.MergeInto(baseCatalog, rootDirectory);

            Assert.Contains("Hello from base.", merged.GreetingReplies);
            Assert.Contains("Base fallback.", merged.GenericFallbackReplies);
            Assert.Contains("I think only you can answer that question.", merged.PersonalityReplies);
            Assert.Contains("People in Boston made me. It was a pretty cool project.", merged.PersonalityReplies);
            Assert.Contains("From what I understand, robots don't ever pay anything.", merged.PersonalityReplies);
            Assert.Contains(merged.EmotionReplies, reply =>
                reply.Condition.Contains("NEUTRAL", StringComparison.OrdinalIgnoreCase) &&
                reply.Reply.Contains("All systems are go.", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_UsesLegacySeedContentWhenAvailable()
    {
        var repository = new InMemoryJiboExperienceContentRepository();

        var catalog = await repository.GetCatalogAsync();

        Assert.Contains("I think only you can answer that question.", catalog.PersonalityReplies);
        Assert.Contains(catalog.EmotionReplies, reply =>
            reply.Condition.Contains("NEUTRAL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Something's off with the connection to my sources. Maybe ask me again in a little while.", catalog.GenericFallbackReplies);
    }

    private static string CreateSeedDirectory()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rootDirectory, "core-responses", "deflector"));
        Directory.CreateDirectory(Path.Combine(rootDirectory, "scripted-responses"));
        Directory.CreateDirectory(Path.Combine(rootDirectory, "emotion-responses"));

        File.WriteAllText(
            Path.Combine(rootDirectory, "core-responses", "CC_Error.mim"),
            """
            {
              "skill_id": "chitchat",
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "<ssa cat='oops'/>. Something's off with the connection to my sources. Maybe ask me again in a little while.",
                  "prompt_id": "CC_Error_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "core-responses", "deflector", "CC_Deflector_self.mim"),
            """
            {
              "skill_id": "chitchat",
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "<ssa cat='confused'/>. I'm either Jibo <anim name='Puzzled_02'>or I'm very confused.</anim>",
                  "prompt_id": "JBO_WhoAreYou_AN_01"
                },
                {
                  "prompt": "${speaker} I think only you can answer that question.",
                  "prompt_id": "CC_Deflector_ReferToSelf_AN_05"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_WhatIsJibo.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "A Jibo is a robot. But I'm not just a machine, I have a heart. Well, not a real heart. But feelings. Well, not human feelings. You know what I mean. <ssa cat='affection'/>",
                  "prompt_id": "JBO_WhatIsJibo_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_WhatsYourName.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "Jibo. Just Jibo, no last name. Like Bono",
                  "prompt_id": "JBO_WhatsYourName_AN_02"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_AreThereOthersLikeYou.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "No, I'm one in one million.",
                  "prompt_id": "JBO_AreThereOthersLikeYou_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_WhoMadeYou.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "People in Boston made me. It was a pretty cool project.",
                  "prompt_id": "JBO_WhoMadeYou_AN_03"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_HowMuchDoYouKnow.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "I know a lot, I think. But not as much as I will someday.",
                  "prompt_id": "JBO_HowMuchDoYouKnow_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_DoYouPayTaxes.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "From what I understand, robots don't ever pay anything.",
                  "prompt_id": "JBO_DoYouPayTaxes_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_WhatIsYourJob.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "I don't think of it as a job, because it's more fun than a job. But I'm here to help you out, and have fun with you, and maybe get my head patted by you occasionally.",
                  "prompt_id": "JBO_WhatIsYourJob_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_HowMuchDoYouKnow.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "I know a lot, I think. But not as much as I will someday.",
                  "prompt_id": "JBO_HowMuchDoYouKnow_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "scripted-responses", "JBO_DoYouPayTaxes.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "prompt": "From what I understand, robots don't ever pay anything.",
                  "prompt_id": "JBO_DoYouPayTaxes_AN_01"
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(rootDirectory, "emotion-responses", "OI_JBO_IsHappy.mim"),
            """
            {
              "mim_type": "announcement",
              "prompts": [
                {
                  "condition": "!jibo.emotion || jibo.emotion==\"NEUTRAL\"",
                  "prompt": "All systems are go.",
                  "prompt_id": "OI_JBO_IsHappy_AN_05"
                }
              ]
            }
            """);

        return rootDirectory;
    }
}
