using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class LocalWhisperCppBufferedAudioSttStrategyTests
{
    [Fact]
    public void CanHandle_ReturnsFalse_WhenLocalWhisperIsDisabled()
    {
        var strategy = new LocalWhisperCppBufferedAudioSttStrategy(
            new BufferedAudioSttOptions
            {
                EnableLocalWhisperCpp = false,
                FfmpegPath = "ffmpeg",
                WhisperCliPath = "whisper-cli",
                WhisperModelPath = "model.bin"
            },
            new FakeExternalProcessRunner());

        var turn = new TurnContext
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["bufferedAudioFrames"] = new[] { BuildMinimalOggPage() }
                }
            };

        Assert.False(strategy.CanHandle(turn));
    }

    [Fact]
    public void CanHandle_ReturnsFalse_WhenConfiguredAbsoluteWhisperPathIsMissing()
    {
        var strategy = new LocalWhisperCppBufferedAudioSttStrategy(
            new BufferedAudioSttOptions
            {
                EnableLocalWhisperCpp = true,
                FfmpegPath = "/usr/bin/ffmpeg",
                WhisperCliPath = "/path/that/does/not/exist/whisper-cli",
                WhisperModelPath = "/path/that/does/not/exist/model.bin"
            },
            new FakeExternalProcessRunner());

        var turn = new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["bufferedAudioFrames"] = new[] { BuildMinimalOggPage() }
            }
        };

        Assert.False(strategy.CanHandle(turn));
    }

    [Fact]
    public async Task TranscribeAsync_UsesFfmpegAndWhisperCpp_WhenConfigured()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"openjibo-stt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var runner = new FakeExternalProcessRunner();
            var strategy = new LocalWhisperCppBufferedAudioSttStrategy(
                new BufferedAudioSttOptions
                {
                    EnableLocalWhisperCpp = true,
                    FfmpegPath = "ffmpeg",
                    WhisperCliPath = "whisper-cli",
                    WhisperModelPath = "model.bin",
                    TempDirectory = tempDirectory
                },
                runner);

            var turn = new TurnContext
            {
                TurnId = "turn-local-stt",
                Locale = "en-US",
                Attributes = new Dictionary<string, object?>
                {
                    ["bufferedAudioBytes"] = 47,
                    ["bufferedAudioFrames"] = new[] { BuildMinimalOggPage() }
                }
            };

            var result = await strategy.TranscribeAsync(turn);

            Assert.Equal("tell me a joke", result.Text);
            Assert.Equal("local-whispercpp-buffered-audio", result.Provider);
            Assert.Equal(2, runner.Calls.Count);
            Assert.Equal("ffmpeg", runner.Calls[0].FileName);
            Assert.Equal("whisper-cli", runner.Calls[1].FileName);
            Assert.Equal(47, result.Metadata["bufferedAudioBytes"]);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static byte[] BuildMinimalOggPage()
    {
        return
        [
            0x4F, 0x67, 0x67, 0x53,
            0x00,
            0x02,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x01,
            0x13,
            0x4F, 0x70, 0x75, 0x73, 0x48, 0x65, 0x61, 0x64, 0x01, 0x01, 0x38, 0x01, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
    }

    private sealed class FakeExternalProcessRunner : IExternalProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments));

            if (string.Equals(fileName, "ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                var outputPath = arguments[^1];
                File.WriteAllBytes(outputPath, "RIFF"u8);
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(new ExternalProcessResult(0, "[00:00:00.000 --> 00:00:01.000] tell me a joke", string.Empty));
        }
    }
}
