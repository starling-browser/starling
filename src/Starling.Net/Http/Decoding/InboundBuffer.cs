namespace Starling.Net.Http.Decoding;

/// <summary>
/// Pull-buffered reader over a <see cref="Stream"/>. Used by the response parser
/// to scan for CRLF-terminated lines and then drain a known number of body
/// bytes (or read to EOF) without re-issuing tiny reads against the network.
/// </summary>
/// <remarks>
/// The buffer grows on demand up to the caller-bounded line/header/body caps.
/// All public read methods are async; cancellation propagates from the
/// underlying stream.
/// </remarks>
internal sealed class InboundBuffer
{
    private const int InitialCapacity = 8 * 1024;
    private readonly Stream _stream;
    private byte[] _buf;
    private int _start;
    private int _end;

    public InboundBuffer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _buf = new byte[InitialCapacity];
    }

    public bool Eof { get; private set; }
    public int BufferedCount => _end - _start;

    public ReadOnlySpan<byte> Peek() => _buf.AsSpan(_start, _end - _start);

    public void Consume(int count)
    {
        if (count < 0 || count > BufferedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _start += count;
        if (_start == _end) { _start = 0; _end = 0; }
    }

    /// <summary>
    /// Read more bytes from the underlying stream into the buffer. Returns
    /// false when the stream has signalled EOF.
    /// </summary>
    public async ValueTask<bool> ReadMoreAsync(CancellationToken ct)
    {
        if (Eof)
        {
            return false;
        }

        EnsureCapacityForMore();
        var n = await _stream.ReadAsync(_buf.AsMemory(_end, _buf.Length - _end), ct)
            .ConfigureAwait(false);
        if (n == 0) { Eof = true; return false; }
        _end += n;
        return true;
    }

    /// <summary>
    /// Locate a "\r\n" sequence inside the currently buffered region. Returns
    /// the relative offset of the '\r' or -1 when no CRLF is present yet.
    /// </summary>
    public int IndexOfCrLf()
    {
        var span = Peek();
        for (var i = 0; i + 1 < span.Length; i++)
        {
            if (span[i] == 0x0D && span[i + 1] == 0x0A)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Locate a "\r\n\r\n" sequence inside the currently buffered region.
    /// Returns the relative offset of the first '\r' or -1.
    /// </summary>
    public int IndexOfDoubleCrLf()
    {
        var span = Peek();
        for (var i = 0; i + 3 < span.Length; i++)
        {
            if (span[i] == 0x0D && span[i + 1] == 0x0A
                && span[i + 2] == 0x0D && span[i + 3] == 0x0A)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Read a CRLF-terminated line. The returned span is invalidated by the
    /// next mutating call (read/consume); copy if you need to retain it.
    /// </summary>
    public async ValueTask<bool> ReadLineAsync(int maxLineLength, CancellationToken ct)
    {
        while (true)
        {
            var idx = IndexOfCrLf();
            if (idx >= 0)
            {
                return true;
            }

            if (BufferedCount > maxLineLength)
            {
                return false;
            }

            if (!await ReadMoreAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Take a CRLF-terminated line as a copied byte array (without the CRLF).
    /// Returns null on EOF before a full line was assembled.
    /// </summary>
    public async ValueTask<byte[]?> TakeLineAsync(int maxLineLength, CancellationToken ct)
    {
        if (!await ReadLineAsync(maxLineLength, ct).ConfigureAwait(false))
        {
            // Either EOF or oversized line. Distinguish via Eof flag — caller may want to retry.
            return null;
        }
        var idx = IndexOfCrLf();
        var line = Peek().Slice(0, idx).ToArray();
        Consume(idx + 2);
        return line;
    }

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes (consuming buffered bytes
    /// first, then reading from the stream). Returns the result as a new array.
    /// </summary>
    public async ValueTask<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var result = new byte[count];
        var written = 0;

        if (BufferedCount > 0)
        {
            var take = Math.Min(BufferedCount, count);
            _buf.AsSpan(_start, take).CopyTo(result.AsSpan(0, take));
            Consume(take);
            written = take;
        }

        while (written < count)
        {
            var n = await _stream
                .ReadAsync(result.AsMemory(written, count - written), ct)
                .ConfigureAwait(false);
            if (n == 0)
            {
                Eof = true;
                throw new EndOfStreamException(
                    $"Stream ended after {written} of {count} expected bytes.");
            }
            written += n;
        }
        return result;
    }

    /// <summary>
    /// Read everything remaining (buffered + stream) until EOF, capped at
    /// <paramref name="maxBytes"/>. Throws if the cap is exceeded.
    /// </summary>
    public async ValueTask<byte[]> ReadToEndAsync(int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        if (BufferedCount > 0)
        {
            if (BufferedCount > maxBytes)
            {
                throw new InvalidDataException("Body exceeded cap.");
            }

            ms.Write(_buf, _start, BufferedCount);
            Consume(BufferedCount);
        }

        var temp = new byte[8 * 1024];
        while (true)
        {
            if (ms.Length > maxBytes)
            {
                throw new InvalidDataException("Body exceeded cap.");
            }

            var n = await _stream.ReadAsync(temp, ct).ConfigureAwait(false);
            if (n == 0) { Eof = true; break; }
            if (ms.Length + n > maxBytes)
            {
                throw new InvalidDataException("Body exceeded cap.");
            }

            ms.Write(temp, 0, n);
        }
        return ms.ToArray();
    }

    private void EnsureCapacityForMore()
    {
        if (_start > 0 && _end - _start < _buf.Length / 2)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buf.Length)
        {
            var grown = new byte[_buf.Length * 2];
            Buffer.BlockCopy(_buf, _start, grown, 0, _end - _start);
            _end -= _start;
            _start = 0;
            _buf = grown;
        }
    }
}
