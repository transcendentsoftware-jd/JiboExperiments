namespace Jibo.Cloud.Application.Services;

public static class OpenJiboCloudBuildInfo
{
    public const string Version = "1.0.17";

    public static string VersionWords => Version.Replace(".", " dot ");

    public static string SpokenVersion => $"Open Jibo Cloud version {VersionWords}.";
}
