using System.Text.Json;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Infrastructure.Audio;

public sealed class LocalWhisperCppBufferedAudioSttStrategy(
    BufferedAudioSttOptions options,
    IExternalProcessRunner processRunner) : ISttStrategy
{
    public string Name => "local-whispercpp-buffered-audio";

    public bool CanHandle(TurnContext turn)
    {
        return options.EnableLocalWhisperCpp &&
               !string.IsNullOrWhiteSpace(options.FfmpegPath) &&
               !string.IsNullOrWhiteSpace(options.WhisperCliPath) &&
               !string.IsNullOrWhiteSpace(options.WhisperModelPath) &&
               ReadBufferedAudioFrames(turn).Count > 0;
    }

    public async Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var frames = ReadBufferedAudioFrames(turn);
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("Local whisper.cpp STT requires buffered websocket audio frames.");
        }

        var tempDirectory = options.TempDirectory;
        if (string.IsNullOrWhiteSpace(tempDirectory))
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), "openjibo-stt");
        }

        Directory.CreateDirectory(tempDirectory);

        var baseName = $"turn-{turn.TurnId}";
        var oggPath = Path.Combine(tempDirectory, $"{baseName}.ogg");
        var wavPath = Path.Combine(tempDirectory, $"{baseName}.wav");

        try
        {
            await File.WriteAllBytesAsync(oggPath, OggOpusAudioNormalizer.Normalize(frames), cancellationToken);

            await processRunner.RunAsync(
                options.FfmpegPath!,
                ["-y", "-i", oggPath, "-ar", "16000", "-ac", "1", "-f", "wav", wavPath],
                cancellationToken);

            var whisperResult = await processRunner.RunAsync(
                options.WhisperCliPath!,
                ["-m", options.WhisperModelPath!, "-f", wavPath, "-l", options.WhisperLanguage],
                cancellationToken);

            var transcript = ExtractTranscript(whisperResult.StdOut);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new InvalidOperationException("whisper.cpp returned no transcript for the buffered audio turn.");
            }

            return new SttResult
            {
                Text = transcript,
                Provider = Name,
                Locale = turn.Locale,
                Metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["bufferedAudioBytes"] = ReadBufferedAudioBytes(turn),
                    ["bufferedAudioChunks"] = frames.Count,
                    ["ffmpegPath"] = options.FfmpegPath,
                    ["whisperCliPath"] = options.WhisperCliPath,
                    ["wavPath"] = wavPath
                }
            };
        }
        finally
        {
            if (options.CleanupTempFiles)
            {
                TryDelete(oggPath);
                TryDelete(wavPath);
            }
        }
    }

    private static IReadOnlyList<byte[]> ReadBufferedAudioFrames(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("bufferedAudioFrames", out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            byte[][] jagged => jagged,
            IReadOnlyList<byte[]> typed => typed,
            IEnumerable<byte[]> enumerable => enumerable.ToArray(),
            JsonElement { ValueKind: JsonValueKind.Array } jsonElement => jsonElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Array)
                .Select(static item => item.EnumerateArray().Select(static b => (byte)b.GetInt32()).ToArray())
                .ToArray(),
            _ => []
        };
    }

    private static int ReadBufferedAudioBytes(TurnContext turn)
    {
        return turn.Attributes.TryGetValue("bufferedAudioBytes", out var bufferedAudioBytes) && bufferedAudioBytes is not null
            ? bufferedAudioBytes switch
            {
                int value => value,
                long value => (int)value,
                string value when int.TryParse(value, out var parsed) => parsed,
                _ => 0
            }
            : 0;
    }

    private static string ExtractTranscript(string standardOutput)
    {
        var lines = standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var timecoded = lines
            .Where(static line => line.StartsWith("[", StringComparison.Ordinal) && line.Contains("-->", StringComparison.Ordinal))
            .Select(static line =>
            {
                var closingBracket = line.IndexOf(']');
                return closingBracket >= 0 ? line[(closingBracket + 1)..].Trim() : line.Trim();
            })
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return timecoded.Length > 0 ? string.Join(" ", timecoded).Trim() : string.Join(" ", lines).Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
