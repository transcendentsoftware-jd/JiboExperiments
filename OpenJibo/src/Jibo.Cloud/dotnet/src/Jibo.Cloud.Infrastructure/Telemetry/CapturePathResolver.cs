namespace Jibo.Cloud.Infrastructure.Telemetry;

internal static class CapturePathResolver
{
    public static string Resolve(string configuredDirectoryPath, string currentDirectory, string appBaseDirectory)
    {
        if (Path.IsPathRooted(configuredDirectoryPath))
        {
            return Path.GetFullPath(configuredDirectoryPath);
        }

        var repoRoot = FindOpenJiboRepoRoot(currentDirectory) ?? FindOpenJiboRepoRoot(appBaseDirectory);
        var baseDirectory = repoRoot ?? currentDirectory;
        return Path.GetFullPath(configuredDirectoryPath, baseDirectory);
    }

    private static string? FindOpenJiboRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        if (!directory.Exists && directory.Parent is not null)
        {
            directory = directory.Parent;
        }

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
