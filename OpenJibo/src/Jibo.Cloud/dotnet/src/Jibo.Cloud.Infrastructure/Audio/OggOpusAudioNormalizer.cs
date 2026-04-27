using System.Buffers.Binary;
using System.Text;

namespace Jibo.Cloud.Infrastructure.Audio;

internal static class OggOpusAudioNormalizer
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Normalize(IReadOnlyList<byte[]> pages)
    {
        if (pages.Count == 0)
        {
            return [];
        }

        var parsed = pages.Select(ParsePage).ToArray();
        var baseGranule = parsed.Length > 1 ? parsed[1].GranulePosition : parsed[0].GranulePosition;
        var normalized = new List<byte[]>(pages.Count);

        for (var index = 0; index < pages.Count; index += 1)
        {
            var output = pages[index].ToArray();
            var parsedPage = parsed[index];

            var newGranule = index >= 1 && parsedPage.GranulePosition >= baseGranule
                ? parsedPage.GranulePosition - baseGranule
                : 0UL;

            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(6, 8), newGranule);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(18, 4), (uint)index);

            var headerType = output[5];
            output[5] = index == pages.Count - 1
                ? (byte)(headerType | 0x04)
                : (byte)(headerType & ~0x04);

            output[22] = 0;
            output[23] = 0;
            output[24] = 0;
            output[25] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(22, 4), ComputeCrc(output));

            normalized.Add(output);
        }

        return normalized.SelectMany(static page => page).ToArray();
    }

    private static ParsedOggPage ParsePage(byte[] buffer)
    {
        if (buffer.Length < 27)
        {
            throw new InvalidOperationException($"Buffered Ogg page is too short ({buffer.Length} bytes).");
        }

        if (!Encoding.ASCII.GetString(buffer, 0, 4).Equals("OggS", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Buffered audio frame did not begin with an OggS capture pattern.");
        }

        var pageSegments = buffer[26];
        if (buffer.Length < 27 + pageSegments)
        {
            throw new InvalidOperationException("Buffered Ogg page segment table was truncated.");
        }

        var payloadLength = 0;
        for (var index = 0; index < pageSegments; index += 1)
        {
            payloadLength += buffer[27 + index];
        }

        var expectedLength = 27 + pageSegments + payloadLength;
        return buffer.Length < expectedLength
            ? throw new InvalidOperationException("Buffered Ogg page payload was truncated.")
            : new ParsedOggPage(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(6, 8)));
    }

    private static uint ComputeCrc(byte[] buffer)
    {
        return buffer.Aggregate<byte, uint>(0, (current, value) => (current << 8) ^ CrcTable[((current >> 24) ^ value) & 0xff]);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index += 1)
        {
            var remainder = index << 24;
            for (var bit = 0; bit < 8; bit += 1)
            {
                remainder = (remainder & 0x80000000) != 0
                    ? (remainder << 1) ^ 0x04c11db7
                    : remainder << 1;
            }

            table[index] = remainder;
        }

        return table;
    }

    private sealed record ParsedOggPage(ulong GranulePosition);
}
