using System.Runtime.CompilerServices;
using System.Text;

namespace TokenScope.Api.Features.LocalUsage;

public static class LocalLineReader
{
    private const int BufferSize = 65_536;
    private const int MaximumLineBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record Line(long NextOffset, string? Text);

    // Only newline-terminated records advance the checkpoint. An incomplete tail is reread next poll.
    public static async IAsyncEnumerable<Line> ReadAsync(
        Stream stream,
        long offset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[BufferSize];
        using var line = new MemoryStream();
        var oversized = false;
        var position = offset;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var segmentStart = 0;
            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] != '\n')
                {
                    continue;
                }

                oversized = AppendSegment(line, buffer, segmentStart, index - segmentStart, oversized);
                var text = oversized ? null : DecodeLine(line);
                yield return new Line(position + index + 1, text);

                line.SetLength(0);
                oversized = false;
                segmentStart = index + 1;
            }

            oversized = AppendSegment(line, buffer, segmentStart, bytesRead - segmentStart, oversized);
            position += bytesRead;
        }
    }

    private static bool AppendSegment(
        MemoryStream line,
        byte[] buffer,
        int offset,
        int count,
        bool oversized)
    {
        if (oversized || line.Length + count > MaximumLineBytes)
        {
            return true;
        }

        line.Write(buffer, offset, count);
        return false;
    }

    private static string? DecodeLine(MemoryStream line)
    {
        try
        {
            return StrictUtf8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
