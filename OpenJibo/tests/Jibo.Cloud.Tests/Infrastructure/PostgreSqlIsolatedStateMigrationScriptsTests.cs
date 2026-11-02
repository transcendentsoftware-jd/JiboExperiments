using System.Text.RegularExpressions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class PostgreSqlIsolatedStateMigrationScriptsTests
{
    [Fact]
    public void GetPaths_RetainsAllStateMigrationsExceptSharedSchemaHardening()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql");
        var selected = PostgreSqlIsolatedStateMigrationScripts.GetPaths(directory).ToArray();
        var expected = Directory.GetFiles(directory, "*.sql")
            .Where(path => !path.EndsWith(".personal-memory.sql", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals(
                "014_harden_sigv4_replay_observer.state.sql", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals(
                "017_hub_token_replay_admission.state.sql", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals(
                "018_hub_channel_leases.state.sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, selected);
        Assert.Contains(selected, path => Path.GetFileName(path).Equals(
            "013_create_sigv4_replay_observations.state.sql", StringComparison.OrdinalIgnoreCase));

        // A future state migration that explicitly touches public must receive its
        // own isolation decision before it can run in parallel fixture schemas.
        foreach (var path in selected)
        {
            var sql = File.ReadAllText(path);
            Assert.DoesNotMatch(new Regex("(?:\\bpublic|\"public\")\\s*\\.", RegexOptions.IgnoreCase), sql);
        }
    }
}
