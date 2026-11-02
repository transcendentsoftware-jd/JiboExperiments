namespace Jibo.Cloud.Tests.Infrastructure;

internal static class PostgreSqlIsolatedStateMigrationScripts
{
    private static readonly string[] SharedSchemaMigrations =
        ["014_harden_sigv4_replay_observer.state.sql", "017_hub_token_replay_admission.state.sql"];

    internal static IEnumerable<string> GetPaths(string directory, string? excludedFileName = null) =>
        Directory.GetFiles(directory, "*.sql")
            .Where(path => !path.EndsWith(".personal-memory.sql", StringComparison.OrdinalIgnoreCase))
            // Shared migrations intentionally create/replace functions in public. Running them from
            // parallel, schema-isolated tests mutates shared database state and races.
            // Replay observation/admission tests apply 014/017 in the nonparallel
            // privileged collection to cover their security-definer behavior.
            .Where(path => !SharedSchemaMigrations.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => excludedFileName is null ||
                           !Path.GetFileName(path).Equals(excludedFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
}
