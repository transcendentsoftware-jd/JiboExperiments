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
            Assert.Contains("All systems are go.", catalog.HowAreYouReplies);
            Assert.Contains("A Jibo is a robot. But I'm not just a machine, I have a heart. Well, not a real heart. But feelings. Well, not human feelings. You know what I mean.", catalog.PersonalityReplies);
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
        Assert.Contains("All systems are go.", catalog.HowAreYouReplies);
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
