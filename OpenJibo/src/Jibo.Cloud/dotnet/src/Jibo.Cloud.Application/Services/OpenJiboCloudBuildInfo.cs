using System.Globalization;

namespace Jibo.Cloud.Application.Services;

public static class OpenJiboCloudBuildInfo
{
    public const string Version = "1.0.19";
    public static readonly DateOnly PersonaBirthday = new(2026, 3, 22);

    public static string VersionWords => Version.Replace(".", " dot ");
    public static string PersonaBirthdayWords => PersonaBirthday.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    public static string SpokenVersion => $"Cloud version {VersionWords}.";

    public static string EsmlVersion => $"Cloud version<break time='10ms'/> {VersionWords.Replace(" ", "<break time='10ms' />")}.";
}
