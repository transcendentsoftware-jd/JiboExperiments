using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class EncryptedUserDataSnapshotStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openjibo-integration-preserve-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_directory, "user-integrations.json");
    private static UserDataEncryptionService Keys(string passphrase = "original-passphrase", string salt = "original-salt") => new(passphrase, salt);
    private EncryptedUserDataSnapshotStore Store(UserDataEncryptionService? keys = null) => new(FilePath, keys ?? Keys());

    [Theory]
    [InlineData("wrong-passphrase", "original-salt")]
    [InlineData("original-passphrase", "wrong-salt")]
    public void WrongKeys_PreserveBytesAndRecoverOriginalSecrets(string passphrase, string salt)
    {
        var original = new InMemoryUserIntegrationStore(Store());
        var link = original.AddHomeAssistantLink("synthetic-device", "Synthetic Jibo", "synthetic-ha");
        original.UpsertMemberCalendarFeed("synthetic-loop", "synthetic-member", "https://calendar.invalid/private-token");
        var bytes = File.ReadAllBytes(FilePath);

        var exception = Assert.Throws<InvalidDataException>(() => new InMemoryUserIntegrationStore(Store(Keys(passphrase, salt))));

        Assert.DoesNotContain("private-token", exception.ToString());
        Assert.DoesNotContain(link.CommandSecret, exception.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        var recovered = new InMemoryUserIntegrationStore(Store());
        Assert.Equal(link.CommandSecret, recovered.FindLinkByLinkId(link.LinkId)!.CommandSecret);
        Assert.Equal("https://calendar.invalid/private-token", recovered.FindMemberCalendarFeed("synthetic-loop", "synthetic-member")!.IcalUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{broken: private-token}")]
    [InlineData("{\"Version\":1,\"Nonce\":\"not-base64\",\"Ciphertext\":\"private-token\"}")]
    public void UnreadableExistingFile_IsNeverReplaced(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);
        var bytes = File.ReadAllBytes(FilePath);

        var error = Assert.Throws<InvalidDataException>(() => Store().LoadOrReset());

        Assert.DoesNotContain("private-token", error.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":99,\"HomeAssistantLinks\":[]}")]
    [InlineData("{\"SchemaVersion\":0,\"HomeAssistantLinks\":[]}")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":2,\"HomeAssistantLinks\":null}")]
    [InlineData("{\"SchemaVersion\":2,\"MemberCalendarFeeds\":null}")]
    [InlineData("{\"SchemaVersion\":2,\"schemaVersion\":1}")]
    public void UnsupportedDecryptedSnapshot_IsPreserved(string plaintext)
    {
        WriteEncrypted(plaintext);
        var bytes = File.ReadAllBytes(FilePath);

        Assert.Throws<InvalidDataException>(() => Store().LoadOrReset());

        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void UnsupportedEncryptionEnvelopeVersion_IsPreserved()
    {
        Directory.CreateDirectory(_directory);
        var payload = Keys().Encrypt("""{"SchemaVersion":2}""");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new { Version = 2, payload.Nonce, payload.Ciphertext }));
        var bytes = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidDataException>(() => Store().Load());
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void FilePathIsDirectory_IsNotTreatedAsAnEmptyNewStore()
    {
        Directory.CreateDirectory(FilePath);
        Assert.Throws<InvalidDataException>(() => Store().Load());
        Assert.True(Directory.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void MissingFile_StartsEmptyWithoutWritingUntilAnExplicitChange()
    {
        var store = new InMemoryUserIntegrationStore(Store());
        Assert.Empty(store.GetHomeAssistantLinks());
        Assert.False(File.Exists(FilePath));
        store.AddHomeAssistantLink("synthetic-device", "Synthetic Jibo", "synthetic-ha");
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void LegacySchema_LoadsWithoutRewritingAndKeepsCommandSecret()
    {
        WriteEncrypted("""{"SchemaVersion":1,"HomeAssistantLinks":[{"LinkId":"legacy-link","CommandSecret":"retained-command-secret","HaInstanceId":"legacy-ha"}]}""");
        var bytes = File.ReadAllBytes(FilePath);

        var snapshot = Store().LoadOrReset();

        Assert.Equal(UserIntegrationSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal("retained-command-secret", Assert.Single(snapshot.HomeAssistantLinks).CommandSecret);
        Assert.Empty(snapshot.MemberCalendarFeeds);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    private void WriteEncrypted(string plaintext)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Keys().Encrypt(plaintext)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
