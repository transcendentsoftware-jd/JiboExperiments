namespace Jibo.Cloud.Application.Services;

public static class OpenJiboCloudBuildInfo
{
    public const string Version = "1.0.18";

    public static string VersionWords => Version.Replace(".", "<break time='10ms'/>dot<break time='10ms'/>");

    public static string SpokenVersion => $"Cloud version<break time='10ms'/>{VersionWords}.";
}
