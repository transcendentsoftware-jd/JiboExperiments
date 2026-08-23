using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Infrastructure.Media;
using Jibo.Cloud.Infrastructure.Persistence;
using Npgsql;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

var options = MigrationOptions.Parse(args);
var minimumLevel = ParseLogEventLevel(Environment.GetEnvironmentVariable("OPENJIBO_MIGRATIONS_LOG_LEVEL") ?? "Debug");
var logDirectory = ResolvePath("captures/logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minimumLevel)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        theme: AnsiConsoleTheme.Code,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logDirectory, "migrations-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        restrictedToMinimumLevel: minimumLevel,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    if (options.ShowHelp)
    {
        Log.Information("{HelpText}", MigrationOptions.HelpText);
        return 0;
    }

    MigrationTarget[] targets = options.Target switch
    {
        MigrationTarget.All => [MigrationTarget.State, MigrationTarget.PersonalMemory],
        _ => [options.Target]
    };

    var scriptsDirectory = ResolveScriptsDirectory(options.ScriptsDirectory);
    var scripts = Directory.EnumerateFiles(scriptsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (scripts.Length == 0)
    {
        Log.Error("No PostgreSQL migration scripts found in '{ScriptsDirectory}'.", scriptsDirectory);
        return 1;
    }

    foreach (var target in targets)
    {
        var targetScripts = scripts.Where(path => AppliesToTarget(path, target)).ToArray();
        var connectionString = options.ResolveConnectionString(target);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Log.Error("No connection string was provided for target '{Target}'.", target);
            return 1;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await EnsureTrackingTableAsync(connection);

        var applied = await LoadAppliedScriptsAsync(connection);

        foreach (var scriptPath in targetScripts)
        {
            var scriptName = Path.GetFileName(scriptPath);
            var scriptText = await File.ReadAllTextAsync(scriptPath);
            var checksum = ComputeChecksum(scriptText);

            if (applied.TryGetValue(scriptName, out var knownChecksum))
            {
                if (!string.Equals(knownChecksum, checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Migration script '{scriptName}' has changed after being applied to '{target}'.");

                if (options.Verbose) Log.Debug("[{Target}] already applied: {ScriptName}", target, scriptName);
                continue;
            }

            if (options.PreviewOnly)
            {
                Log.Information("[{Target}] would apply: {ScriptName}", target, scriptName);
                continue;
            }

            Log.Information("[{Target}] applying: {ScriptName}", target, scriptName);
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(scriptText, connection, transaction))
            {
                command.CommandTimeout = 0;
                await command.ExecuteNonQueryAsync();
            }

            await using (var insert = new NpgsqlCommand("""
                                                        INSERT INTO OpenJiboSchemaMigrations (ScriptName, Checksum, AppliedUtc)
                                                        VALUES (@scriptName, @checksum, NOW())
                                                        ON CONFLICT (ScriptName) DO UPDATE SET
                                                            Checksum = EXCLUDED.Checksum,
                                                            AppliedUtc = EXCLUDED.AppliedUtc
                                                        """, connection, transaction))
            {
                insert.Parameters.AddWithValue("@scriptName", scriptName);
                insert.Parameters.AddWithValue("@checksum", checksum);
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        if (target == MigrationTarget.State && options.ImportLegacyCloudState)
        {
            if (options.PreviewOnly)
            {
                Log.Information("[State] would explicitly import the legacy cloud-state snapshot.");
            }
            else if (await LoadSnapshotAsync(connection, "cloud-state") is null)
            {
                Log.Information("[State] no legacy cloud-state snapshot exists; explicit import was skipped.");
            }
            else
            {
                await using var dataSource = NpgsqlDataSource.Create(connectionString);
                var backupExportDirectory =
                    Environment.GetEnvironmentVariable("OPENJIBO_LEGACY_BACKUP_EXPORT_DIRECTORY");
                IBackupPayloadStore? backupPayloadStore = null;
                if (!string.IsNullOrWhiteSpace(options.MediaConnectionString))
                    backupPayloadStore = new MediaContentBackupPayloadStore(
                        new AzureBlobMediaContentStore(
                            options.MediaConnectionString, options.MediaContainerName));
                else if (!string.IsNullOrWhiteSpace(backupExportDirectory))
                    backupPayloadStore = new DirectoryBackupPayloadStore(backupExportDirectory);
                var importer = new PostgreSqlCloudStateSnapshotImporter(
                    dataSource,
                    new UserDataCloudStateSecretProtector(new UserDataEncryptionService()),
                    backupPayloadStore);
                var result = await importer.ImportAsync();
                Log.Information(
                    "[State] legacy import {ImportName}: sha256={SourceSha256}, alreadyImported={AlreadyImported}, counts={ImportedCounts}",
                    result.ImportName, result.SourceSha256, result.AlreadyImported, result.ImportedCounts);
            }
        }

        if (target == MigrationTarget.PersonalMemory && options.ImportLegacyPersonalMemory)
        {
            if (options.PreviewOnly)
            {
                Log.Information("[PersonalMemory] would explicitly import the legacy personal-memory snapshot.");
            }
            else
            {
                using var store = new PostgreSqlPersonalMemoryStore(connectionString);
                var state = store.GetPersistenceStateInfo();
                Log.Information(
                    "[PersonalMemory] legacy import complete: schemaVersion={SchemaVersion}, revision={Revision}",
                    state.SchemaVersion, state.Revision);
            }
        }

        if (options.Verify && !options.PreviewOnly)
            await VerifyTargetAsync(connection, target);
    }

    return 0;
}
finally
{
    Log.CloseAndFlush();
}

static string ResolveScriptsDirectory(string? configured)
{
    var candidate = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql")
        : configured;

    return Path.GetFullPath(candidate);
}

static string ResolvePath(string configuredPath)
{
    if (Path.IsPathRooted(configuredPath)) return Path.GetFullPath(configuredPath);

    var repoRoot = FindOpenJiboRepoRoot(Directory.GetCurrentDirectory()) ??
                   FindOpenJiboRepoRoot(AppContext.BaseDirectory) ??
                   Directory.GetCurrentDirectory();

    return Path.GetFullPath(configuredPath, repoRoot);
}

static string? FindOpenJiboRepoRoot(string? startPath)
{
    if (string.IsNullOrWhiteSpace(startPath)) return null;

    var directory = new DirectoryInfo(Path.GetFullPath(startPath));
    if (directory is { Exists: false, Parent: not null }) directory = directory.Parent;

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx"))) return directory.FullName;

        directory = directory.Parent;
    }

    return null;
}

static LogEventLevel ParseLogEventLevel(string? value)
{
    return Enum.TryParse<LogEventLevel>(value, true, out var level)
        ? level
        : LogEventLevel.Debug;
}

static string ComputeChecksum(string value)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static async Task EnsureTrackingTableAsync(NpgsqlConnection connection)
{
    await using var command = new NpgsqlCommand("""
                                                CREATE TABLE IF NOT EXISTS OpenJiboSchemaMigrations (
                                                    ScriptName TEXT NOT NULL PRIMARY KEY,
                                                    Checksum TEXT NOT NULL,
                                                    AppliedUtc TIMESTAMPTZ NOT NULL DEFAULT NOW()
                                                )
                                                """, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<Dictionary<string, string>> LoadAppliedScriptsAsync(NpgsqlConnection connection)
{
    var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    await using var command = new NpgsqlCommand("""
                                                SELECT ScriptName, Checksum
                                                FROM OpenJiboSchemaMigrations
                                                ORDER BY ScriptName
                                                """, connection);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var scriptName = reader.GetString(0);
        var checksum = reader.GetString(1);
        applied[scriptName] = checksum;
    }

    return applied;
}

static async Task VerifyTargetAsync(NpgsqlConnection connection, MigrationTarget target)
{
    if (target == MigrationTarget.State)
    {
        var snapshot = await LoadSnapshotAsync(connection, "cloud-state");
        if (snapshot is not null)
        {
            var sha256 = ComputeChecksum(snapshot);
            await using var import = new NpgsqlCommand("""
                                                       SELECT COUNT(*)
                                                       FROM CloudStateImports
                                                       WHERE SourceSnapshotName = 'cloud-state'
                                                         AND SourceSha256 = @sha256
                                                       """, connection);
            import.Parameters.AddWithValue("sha256", sha256);
            var importCount = (long)(await import.ExecuteScalarAsync() ?? 0L);
            if (importCount != 1)
                throw new InvalidOperationException(
                    "State verification failed: the current legacy cloud-state snapshot has not been imported.");

            await using var accounts = new NpgsqlCommand("SELECT COUNT(*) FROM Accounts", connection);
            if ((long)(await accounts.ExecuteScalarAsync() ?? 0L) == 0)
                throw new InvalidOperationException(
                    "State verification failed: the imported database has no normalized account.");
        }

        Log.Information("[State] normalized persistence verification passed.");
        return;
    }

    var personalMemorySnapshot = await LoadSnapshotAsync(connection, "personal-memory");
    if (personalMemorySnapshot is not null)
    {
        await using var import = new NpgsqlCommand("""
                                                   SELECT COUNT(*)
                                                   FROM PersonalMemoryImports
                                                   WHERE ImportName = 'persistence-snapshot-v1'
                                                   """, connection);
        var importCount = (long)(await import.ExecuteScalarAsync() ?? 0L);
        if (importCount != 1)
            throw new InvalidOperationException(
                "Personal-memory verification failed: the legacy snapshot has not been imported.");
    }

    Log.Information("[PersonalMemory] normalized persistence verification passed.");
}

static async Task<string?> LoadSnapshotAsync(NpgsqlConnection connection, string snapshotName)
{
    await using var command = new NpgsqlCommand("""
                                                SELECT SnapshotJson
                                                FROM PersistenceSnapshots
                                                WHERE SnapshotName = @snapshotName
                                                """, connection);
    command.Parameters.AddWithValue("snapshotName", snapshotName);
    return await command.ExecuteScalarAsync() as string;
}
static bool AppliesToTarget(string scriptPath, MigrationTarget target)
{
    var fileName = Path.GetFileName(scriptPath);
    if (fileName.EndsWith(".state.sql", StringComparison.OrdinalIgnoreCase))
        return target == MigrationTarget.State;

    if (fileName.EndsWith(".personal-memory.sql", StringComparison.OrdinalIgnoreCase))
        return target == MigrationTarget.PersonalMemory;

    return true;
}

internal enum MigrationTarget
{
    State,
    PersonalMemory,
    All
}

internal sealed record MigrationOptions(
    MigrationTarget Target,
    bool PreviewOnly,
    bool Verbose,
    string? ScriptsDirectory,
    string? StateConnectionString,
    string? PersonalMemoryConnectionString,
    string? MediaConnectionString,
    string MediaContainerName,
    bool ImportLegacyCloudState,
    bool ImportLegacyPersonalMemory,
    bool Verify,
    bool ShowHelp)
{
    public static string HelpText =>
        """
        Open Jibo migration runner

        Usage:
          dotnet Jibo.Cloud.Migrations.dll --apply
          dotnet Jibo.Cloud.Migrations.dll --preview
          dotnet Jibo.Cloud.Migrations.dll --target state|personal-memory|all

        Options:
          --apply                 Apply pending SQL migrations
          --preview, --dry-run    List pending migrations without applying them
          --target                Choose a target database
          --scripts               Override the SQL script directory
          --state-connection      Override the state database connection string
          --memory-connection     Override the personal memory connection string
          --media-connection      Azure Blob connection string for imported backup payloads
          --media-container       Azure Blob media container (default: openjibo-media)
          --import-legacy-cloud-state
                                  Explicitly import PersistenceSnapshots/cloud-state
          --import-legacy-personal-memory
                                  Explicitly import PersistenceSnapshots/personal-memory
          --verify                Fail unless legacy snapshots have matching import ledgers
          --verbose               Print already-applied scripts too
          --help                  Show this help
        """;

    public static MigrationOptions Parse(string[] args)
    {
        var target = MigrationTarget.All;
        var previewOnly = false;
        var verbose = false;
        string? scriptsDirectory = null;
        var stateConnectionString = Environment.GetEnvironmentVariable("OpenJibo__State__ConnectionString")
                                    ?? Environment.GetEnvironmentVariable("OPENJIBO_STATE_STORAGE_CONNECTION_STRING")
                                    ?? BuildPostgreSqlConnectionString("openjibo_state");
        var personalMemoryConnectionString = Environment.GetEnvironmentVariable(
                                                 "OpenJibo__PersonalMemory__ConnectionString")
                                             ?? Environment.GetEnvironmentVariable(
                                                 "OPENJIBO_PERSONAL_MEMORY_STORAGE_CONNECTION_STRING")
                                             ?? BuildPostgreSqlConnectionString("openjibo_memory");
        var mediaConnectionString = Environment.GetEnvironmentVariable("OpenJibo__Media__ConnectionString")
                                    ?? Environment.GetEnvironmentVariable(
                                        "OPENJIBO_MEDIA_STORAGE_CONNECTION_STRING");
        var mediaContainerName = Environment.GetEnvironmentVariable("OpenJibo__Media__ContainerName")
                                 ?? "openjibo-media";
        var showHelp = false;
        var importLegacyCloudState = false;
        var importLegacyPersonalMemory = false;
        var verify = false;

        for (var index = 0; index < args.Length; index += 1)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                case "--preview":
                case "--dry-run":
                    previewOnly = true;
                    break;
                case "--apply":
                    previewOnly = false;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--import-legacy-cloud-state":
                    importLegacyCloudState = true;
                    break;
                case "--import-legacy-personal-memory":
                    importLegacyPersonalMemory = true;
                    break;
                case "--verify":
                    verify = true;
                    break;
                case "--target":
                    target = ParseTarget(GetValue(args, ref index, "--target"));
                    break;
                case "--scripts":
                    scriptsDirectory = GetValue(args, ref index, "--scripts");
                    break;
                case "--state-connection":
                    stateConnectionString = GetValue(args, ref index, "--state-connection");
                    break;
                case "--memory-connection":
                    personalMemoryConnectionString = GetValue(args, ref index, "--memory-connection");
                    break;
                case "--media-connection":
                    mediaConnectionString = GetValue(args, ref index, "--media-connection");
                    break;
                case "--media-container":
                    mediaContainerName = GetValue(args, ref index, "--media-container") ?? "openjibo-media";
                    break;
            }
        }

        return new MigrationOptions(
            target,
            previewOnly,
            verbose,
            scriptsDirectory,
            stateConnectionString,
            personalMemoryConnectionString,
            mediaConnectionString,
            mediaContainerName,
            importLegacyCloudState,
            importLegacyPersonalMemory,
            verify,
            showHelp);
    }

    private static string BuildPostgreSqlConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_HOST") ?? "postgres",
            Port = int.TryParse(Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_PORT"), out var port)
                ? port
                : 5432,
            Database = databaseName,
            Username = Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_USER") ?? "openjibo"
        };

        var password = Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_PASSWORD");
        if (!string.IsNullOrWhiteSpace(password))
            builder.Password = password;

        return builder.ConnectionString;
    }

    public string? ResolveConnectionString(MigrationTarget target)
    {
        return target switch
        {
            MigrationTarget.State => StateConnectionString,
            MigrationTarget.PersonalMemory => PersonalMemoryConnectionString,
            _ => null
        };
    }

    private static MigrationTarget ParseTarget(string? value)
    {
        return string.Equals(value, "state", StringComparison.OrdinalIgnoreCase)
            ? MigrationTarget.State
            : string.Equals(value, "personal-memory", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(value, "memory", StringComparison.OrdinalIgnoreCase)
                ? MigrationTarget.PersonalMemory
                : MigrationTarget.All;
    }

    private static string GetValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {optionName}.");

        index += 1;
        return args[index];
    }
}
