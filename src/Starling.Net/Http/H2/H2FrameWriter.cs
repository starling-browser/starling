namespace Starling.Net.Http.H2;

/// <summary>
/// Serializes and writes HTTP/2 frames to the connection's byte stream. All
/// writes are serialized through a single semaphore so frames never interleave
/// at the octet level, and a HEADERS block plus its CONTINUATION frames are
/// emitted contiguously (RFC 9113 §6.10) by holding the lock across the whole
/// sequence.
/// </summary>
internal sealed class H2FrameWriter(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Send the client preface immediately followed by our SETTINGS frame.</summary>
    public async Task WritePrefaceAndSettingsAsync(
        IReadOnlyList<(H2SettingId Id, uint Value)> settings, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(H2Protocol.ClientPreface.ToArray(), ct).ConfigureAwait(false);
            await WriteSettingsLockedAsync(settings, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public Task WriteSettingsAckAsync(CancellationToken ct) =>
        WriteSimpleAsync(H2FrameType.Settings, H2Flags.Ack, 0, ReadOnlyMemory<byte>.Empty, ct);

    public Task WritePingAckAsync(byte[] opaqueData, CancellationToken ct) =>
        WriteSimpleAsync(H2FrameType.Ping, H2Flags.Ack, 0, opaqueData, ct);

    public Task WriteRstStreamAsync(int streamId, H2ErrorCode code, CancellationToken ct)
    {
        var payload = new byte[4];
        WriteUInt32(payload, (uint)code);
        return WriteSimpleAsync(H2FrameType.RstStream, H2Flags.None, streamId, payload, ct);
    }

    public Task WriteWindowUpdateAsync(int streamId, int increment, CancellationToken ct)
    {
        var payload = new byte[4];
        WriteUInt32(payload, (uint)increment);
        return WriteSimpleAsync(H2FrameType.WindowUpdate, H2Flags.None, streamId, payload, ct);
    }

    public Task WriteGoAwayAsync(int lastStreamId, H2ErrorCode code, CancellationToken ct)
    {
        var payload = new byte[8];
        WriteUInt32(payload.AsSpan(0), (uint)lastStreamId);
        WriteUInt32(payload.AsSpan(4), (uint)code);
        _ = WriteSimpleAsync(H2FrameType.GoAway, H2Flags.None, 0, payload, ct);

        // don't wait on the GOAWAY
        return Task.CompletedTask;
    }

    /// <summary>Write one DATA frame for <paramref name="streamId"/>.</summary>
    public Task WriteDataAsync(int streamId, ReadOnlyMemory<byte> data, bool endStream, CancellationToken ct) =>
        WriteSimpleAsync(
            H2FrameType.Data, endStream ? H2Flags.EndStream : H2Flags.None, streamId, data, ct);

    /// <summary>
    /// Write a HEADERS frame, splitting into CONTINUATION frames when the
    /// encoded block exceeds the peer's max frame size. END_STREAM (if set)
    /// rides on the HEADERS frame; END_HEADERS rides on the final fragment.
    /// </summary>
    public async Task WriteHeadersAsync(
        int streamId, ReadOnlyMemory<byte> block, bool endStream, int peerMaxFrameSize, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var first = Math.Min(block.Length, peerMaxFrameSize);
            var isOnly = first == block.Length;
            var flags = (endStream ? H2Flags.EndStream : H2Flags.None)
                | (isOnly ? H2Flags.EndHeaders : H2Flags.None);
            await WriteFrameLockedAsync(H2FrameType.Headers, flags, streamId, block[..first], ct)
                .ConfigureAwait(false);

            var offset = first;
            while (offset < block.Length)
            {
                var n = Math.Min(block.Length - offset, peerMaxFrameSize);
                var last = offset + n == block.Length;
                await WriteFrameLockedAsync(
                    H2FrameType.Continuation,
                    last ? H2Flags.EndHeaders : H2Flags.None,
                    streamId,
                    block.Slice(offset, n),
                    ct).ConfigureAwait(false);
                offset += n;
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteSimpleAsync(
        H2FrameType type, H2Flags flags, int streamId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameLockedAsync(type, flags, streamId, payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteSettingsLockedAsync(
        IReadOnlyList<(H2SettingId Id, uint Value)> settings, CancellationToken ct)
    {
        var payload = new byte[settings.Count * 6];
        for (var i = 0; i < settings.Count; i++)
        {
            var o = i * 6;
            payload[o] = (byte)((ushort)settings[i].Id >> 8);
            payload[o + 1] = (byte)(ushort)settings[i].Id;
            WriteUInt32(payload.AsSpan(o + 2), settings[i].Value);
        }
        await WriteFrameLockedAsync(H2FrameType.Settings, H2Flags.None, 0, payload, ct)
            .ConfigureAwait(false);
    }

    private async Task WriteFrameLockedAsync(
        H2FrameType type, H2Flags flags, int streamId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[H2Protocol.FrameHeaderLength];
        var len = payload.Length;
        header[0] = (byte)(len >> 16);
        header[1] = (byte)(len >> 8);
        header[2] = (byte)len;
        header[3] = (byte)type;
        header[4] = (byte)flags;
        WriteUInt32(header.AsSpan(5), (uint)streamId & 0x7fff_ffff);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (len > 0)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
    }

    private static void WriteUInt32(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value >> 24);
        dst[1] = (byte)(value >> 16);
        dst[2] = (byte)(value >> 8);
        dst[3] = (byte)value;
    }

    public void Dispose() => _writeLock.Dispose();
}
