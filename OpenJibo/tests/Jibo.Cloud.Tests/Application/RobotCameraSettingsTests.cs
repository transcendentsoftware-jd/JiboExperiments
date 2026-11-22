using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class RobotCameraSettingsTests
{
    [Theory]
    [InlineData("what do you see", true)]
    [InlineData("hey jibo can you see me", true)]
    [InlineData("What's in front of you?", true)]
    [InlineData("what do you see through your telescope", false)]
    [InlineData("what is the weather", false)]
    [InlineData("", false)]
    public void IsVisionQuestion_DetectsCameraQuestions(string text, bool expected)
    {
        Assert.Equal(expected, RobotCameraSettings.IsVisionQuestion(text));
    }

    [Theory]
    [InlineData("http://192.168.1.50:8486", "http://192.168.1.50:8486/")]
    [InlineData(" http://192.168.1.50:8486/ ", "http://192.168.1.50:8486/")]
    [InlineData("ftp://192.168.1.50", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void ParseCameraUrl_AcceptsHttpAddresses(string? raw, string? expected)
    {
        Assert.Equal(expected, RobotCameraSettings.ParseCameraUrl(raw)?.ToString());
    }
}
