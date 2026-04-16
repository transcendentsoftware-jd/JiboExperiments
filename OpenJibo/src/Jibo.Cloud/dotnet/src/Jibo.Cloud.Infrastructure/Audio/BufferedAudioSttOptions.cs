namespace Jibo.Cloud.Infrastructure.Audio;

public sealed class BufferedAudioSttOptions
{
    public bool EnableLocalWhisperCpp { get; set; }
    public string? FfmpegPath { get; set; }
    public string? WhisperCliPath { get; set; }
    public string? WhisperModelPath { get; set; }
    public string WhisperLanguage { get; set; } = "en";
    public string? TempDirectory { get; set; }
}
