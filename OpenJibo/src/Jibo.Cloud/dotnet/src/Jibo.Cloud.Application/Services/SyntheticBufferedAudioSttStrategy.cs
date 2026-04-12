using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class SyntheticBufferedAudioSttStrategy : ISttStrategy
{
    public string Name => "synthetic-buffered-audio";

    public bool CanHandle(TurnContext turn)
    {
        return ReadBufferedAudioBytes(turn) > 0 &&
               !string.IsNullOrWhiteSpace(ReadTranscriptHint(turn));
    }

    public Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var transcriptHint = ReadTranscriptHint(turn);
        if (string.IsNullOrWhiteSpace(transcriptHint))
        {
            throw new InvalidOperationException("Synthetic buffered audio STT requires an audio transcript hint.");
        }

        return Task.FromResult(new SttResult
        {
            Text = transcriptHint.Trim(),
            Provider = Name,
            Confidence = 0.75f,
            Locale = turn.Locale,
            Metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["bufferedAudioBytes"] = ReadBufferedAudioBytes(turn),
                ["mode"] = "fixture-hint"
            }
        });
    }

    private static int ReadBufferedAudioBytes(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("bufferedAudioBytes", out var bufferedAudioBytes))
        {
            return 0;
        }

        return bufferedAudioBytes switch
        {
            int value => value,
            long value => (int)value,
            string value when int.TryParse(value, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? ReadTranscriptHint(TurnContext turn)
    {
        return turn.Attributes.TryGetValue("audioTranscriptHint", out var transcriptHint)
            ? transcriptHint?.ToString()
            : null;
    }
}
