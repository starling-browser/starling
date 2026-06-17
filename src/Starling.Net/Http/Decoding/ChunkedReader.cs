using System.Globalization;
using System.Text;

namespace Starling.Net.Http.Decoding;

/// <summary>
/// Decoder for HTTP/1.1 <c>Transfer-Encoding: chunked</c> framing per
/// RFC 9112 §7.1. Drains an <see cref="InboundBuffer"/> until the
/// terminating zero-sized chunk (and any trailers) is consumed.
/// </summary>
internal static class ChunkedReader
{
    private const int MaxChunkSizeLineLength = 1024;
    private const int MaxTrailerLineLength = 8 * 1024;

    public static async ValueTask<byte[]> ReadAllAsync(
        InboundBuffer source, int maxBodyBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxBodyBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        }

        using var ms = new MemoryStream();

        while (true)
        {
            var sizeLine = await source.TakeLineAsync(MaxChunkSizeLineLength, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Unexpected EOF before chunk size line.");

            var size = ParseChunkSize(sizeLine);

            if (size == 0)
            {
                // Drain trailer section: zero or more header lines followed by an empty line.
                while (true)
                {
                    var trailer = await source.TakeLineAsync(MaxTrailerLineLength, ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Unexpected EOF inside chunked trailers.");
                    if (trailer.Length == 0)
                    {
                        break;
                    }
                    // v1: trailers ignored.
                }
                break;
            }

            if (ms.Length + size > maxBodyBytes)
            {
                throw new InvalidDataException("Chunked body exceeded cap.");
            }

            var chunk = await source.ReadExactAsync(size, ct).ConfigureAwait(false);
            ms.Write(chunk);

            var crlf = await source.TakeLineAsync(2, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Unexpected EOF after chunk data.");
            if (crlf.Length != 0)
            {
                throw new InvalidDataException("Expected CRLF terminator after chunk data.");
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Parse a chunk-size line per §7.1.1. Form: <c>1*HEXDIG [chunk-ext]</c>.
    /// We accept hex digits in either case and ignore everything from the
    /// first ';' onwards (chunk extensions).
    /// </summary>
    internal static int ParseChunkSize(byte[] line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0)
        {
            throw new InvalidDataException("Chunk-size line was empty.");
        }

        var end = line.Length;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == (byte)';' || line[i] == (byte)' ' || line[i] == (byte)'\t')
            {
                end = i;
                break;
            }
        }

        if (end == 0)
        {
            throw new InvalidDataException("Chunk-size line had no hex digits.");
        }

        var asAscii = Encoding.ASCII.GetString(line, 0, end);
        if (!int.TryParse(asAscii, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
            || size < 0)
        {
            throw new InvalidDataException($"Chunk-size '{asAscii}' is not a valid hex integer.");
        }
        return size;
    }
}
