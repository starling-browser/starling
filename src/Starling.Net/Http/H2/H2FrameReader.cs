namespace Starling.Net.Http.H2;

/// <summary>One frame read off the wire: header fields plus the raw payload.</summary>
internal readonly struct RawFrame(H2FrameType type, H2Flags flags, int streamId, byte[] payload)
{
    public H2FrameType Type { get; } = type;
    public H2Flags Flags { get; } = flags;
    public int StreamId { get; } = streamId;
    public byte[] Payload { get; } = payload;

    public bool HasFlag(H2Flags flag) => (Flags & flag) == flag;
}

/// <summary>
/// Reads HTTP/2 frames (RFC 9113 §4.1) from a byte stream. Each call returns
/// the next whole frame or null on a clean end-of-stream. A frame whose length
/// exceeds the negotiated maximum is a connection-level FRAME_SIZE_ERROR.
/// </summary>
internal sealed class H2FrameReader(Stream stream, int maxFrameSize)
{
    private readonly byte[] _header = new byte[H2Protocol.FrameHeaderLength];

    public async Task<RawFrame?> ReadFrameAsync(CancellationToken ct)
    {
        if (!await TryReadExactAsync(_header, ct).ConfigureAwait(false))
        {
            return null; // clean EOF between frames
        }

        var length = (_header[0] << 16) | (_header[1] << 8) | _header[2];
        var type = (H2FrameType)_header[3];
        var flags = (H2Flags)_header[4];
        var streamId =
            ((_header[5] & 0x7f) << 24) | (_header[6] << 16) | (_header[7] << 8) | _header[8];

        if (length > maxFrameSize)
        {
            throw new H2ConnectionException(
                H2ErrorCode.FrameSizeError, $"frame length {length} exceeds max {maxFrameSize}");
        }

        var payload = length == 0 ? [] : new byte[length];
        if (length > 0 && !await TryReadExactAsync(payload, ct).ConfigureAwait(false))
        {
            return null; // truncated payload — treat as connection closed
        }

        return new RawFrame(type, flags, streamId, payload);
    }

    private async Task<bool> TryReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("truncated HTTP/2 frame");
            }

            read += n;
        }
        return true;
    }
}
