namespace Jibo.Cloud.Application.Abstractions;

/// <summary>
/// Optional live camera for "what do you see?". Jibo's on-robot media service
/// (jibo-lps-service, port 8486) takes a photo on request. Set OPENJIBO_JIBO_CAMERA_URL to
/// http://&lt;jibo-ip&gt;:8486 on a self-hosted server on the same network as the robot.
/// Unset (the default) keeps today's behaviour.
/// </summary>
public static class RobotCameraSettings
{
    public const string CameraUrlVariable = "OPENJIBO_JIBO_CAMERA_URL";

    private static readonly string[] VisionPhrases =
    [
        "what do you see", "what can you see", "what did you see", "do you see", "can you see",
        "in front of you", "what am i wearing", "what am i holding", "how do i look",
        "describe me", "what color is", "look at me"
    ];

    /// <summary>The configured camera service address, or null when the feature is off.</summary>
    public static Uri? CameraBaseUri => ParseCameraUrl(Environment.GetEnvironmentVariable(CameraUrlVariable));

    public static Uri? ParseCameraUrl(string? raw)
    {
        var trimmed = raw?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(trimmed) &&
               Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    /// <summary>Questions about what Jibo sees right now (checked case-insensitively).</summary>
    public static bool IsVisionQuestion(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !text.Contains("telescope", StringComparison.OrdinalIgnoreCase) &&
        VisionPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
