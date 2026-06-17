using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Tls;

namespace Starling.Net.Tls;

internal static partial class BcDuplexTlsStreamLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "close_notify flush failed; peer may already be gone")]
    public static partial void CloseNotifyFailed(ILogger logger, Exception ex);
}

/// <summary>
/// A full-duplex <see cref="Stream"/> over BouncyCastle's <em>non-blocking</em>
/// <see cref="TlsClientProtocol"/>. BouncyCastle's blocking stream serializes
/// reads and writes (a read blocked waiting for the peer also blocks writes),
/// which deadlocks HTTP/2 — where one reader loop and concurrent request writers
/// must share the connection. This wrapper instead feeds ciphertext in/out of
/// the protocol's in-memory buffers, holding a lock only across the (instant)
/// state transitions and performing all socket I/O outside it. That lets a
/// blocked socket read coexist with an in-flight write.
/// </summary>
internal sealed class BcDuplexTlsStream : Stream
{
    // A TLS record is at most 2^14 + overhead; this comfortably holds one read.
    private const int CipherBufferSize = 18 * 1024;

    private readonly TlsClientProtocol _protocol;
    private readonly Stream _transport;
    private readonly object _tlsGate = new();
    private readonly SemaphoreSlim _socketWrite = new(1, 1);
    private readonly byte[] _cipherReadBuffer = new byte[CipherBufferSize];
    private readonly ILogger _log;
    private bool _disposed;

    private BcDuplexTlsStream(TlsClientProtocol protocol, Stream transport, ILogger log)
    {
        _protocol = protocol;
        _transport = transport;
        _log = log;
    }

    /// <summary>
    /// Drive the (non-blocking) TLS handshake to completion over
    /// <paramref name="transport"/> and return the established duplex stream.
    /// The handshake is single-threaded, so no locking is needed here.
    /// </summary>
    public static async Task<BcDuplexTlsStream> HandshakeAsync(
        TlsClientProtocol protocol, TlsClient client, Stream transport, CancellationToken ct,
        ILogger<BcDuplexTlsStream>? log = null)
    {
        log ??= NullLogger<BcDuplexTlsStream>.Instance;
        protocol.Connect(client);
        await PumpOutputAsync(protocol, transport, ct).ConfigureAwait(false); // send ClientHello

        var cipher = new byte[CipherBufferSize];
        while (protocol.IsHandshaking)
        {
            var n = await transport.ReadAsync(cipher, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("peer closed during TLS handshake");
            }

            protocol.OfferInput(cipher, 0, n);
            await PumpOutputAsync(protocol, transport, ct).ConfigureAwait(false); // e.g. client Finished
        }

        return new BcDuplexTlsStream(protocol, transport, log);
    }

    private static async Task PumpOutputAsync(TlsClientProtocol protocol, Stream transport, CancellationToken ct)
    {
        var available = protocol.GetAvailableOutputBytes();
        if (available == 0)
        {
            return;
        }

        var buf = new byte[available];
        var read = protocol.ReadOutput(buf, 0, available);
        await transport.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        await transport.FlushAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            // 1. Hand back any already-decrypted application data.
            byte[]? appChunk = null;
            var got = 0;
            lock (_tlsGate)
            {
                var available = _protocol.GetAvailableInputBytes();
                if (available > 0)
                {
                    got = Math.Min(available, buffer.Length);
                    appChunk = ArrayPool<byte>.Shared.Rent(got);
                    _protocol.ReadInput(appChunk, 0, got);
                }
            }
            if (appChunk is not null)
            {
                appChunk.AsSpan(0, got).CopyTo(buffer.Span);
                ArrayPool<byte>.Shared.Return(appChunk);
                return got;
            }

            // 2. Otherwise pull ciphertext off the socket (outside the lock, so a
            //    concurrent write isn't blocked) and feed it to the protocol.
            var n = await _transport.ReadAsync(_cipherReadBuffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return 0; // clean EOF
            }

            byte[]? outChunk = null;
            var outLen = 0;
            lock (_tlsGate)
            {
                _protocol.OfferInput(_cipherReadBuffer, 0, n);
                // Processing input can produce output (e.g. a post-handshake
                // KeyUpdate response); flush it back to the peer.
                var pending = _protocol.GetAvailableOutputBytes();
                if (pending > 0)
                {
                    outChunk = new byte[pending];
                    outLen = _protocol.ReadOutput(outChunk, 0, pending);
                }
            }
            if (outChunk is not null)
            {
                await SocketWriteAsync(outChunk.AsMemory(0, outLen), ct).ConfigureAwait(false);
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        byte[] cipher;
        int cipherLen;
        lock (_tlsGate)
        {
            _protocol.WriteApplicationData(buffer.ToArray(), 0, buffer.Length);
            var pending = _protocol.GetAvailableOutputBytes();
            cipher = new byte[pending];
            cipherLen = _protocol.ReadOutput(cipher, 0, pending);
        }
        await SocketWriteAsync(cipher.AsMemory(0, cipherLen), ct).ConfigureAwait(false);
    }

    private async Task SocketWriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // Serialize socket writes: both the write path and read-path-generated
        // output (KeyUpdate, alerts) can reach here concurrently.
        await _socketWrite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport.WriteAsync(data, ct).ConfigureAwait(false);
            await _transport.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _socketWrite.Release(); }
    }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            // Best-effort close_notify, then tear down the socket.
            try
            {
                byte[]? outChunk = null;
                var outLen = 0;
                lock (_tlsGate)
                {
                    _protocol.Close();
                    var pending = _protocol.GetAvailableOutputBytes();
                    if (pending > 0)
                    {
                        outChunk = new byte[pending];
                        outLen = _protocol.ReadOutput(outChunk, 0, pending);
                    }
                }
                if (outChunk is not null)
                {
                    _socketWrite.Wait();
                    try { _transport.Write(outChunk, 0, outLen); }
                    finally { _socketWrite.Release(); }
                }
            }
            catch (Exception ex) { BcDuplexTlsStreamLog.CloseNotifyFailed(_log, ex); /* the peer may already be gone */ }

            _socketWrite.Dispose();
            _transport.Dispose();
        }
        base.Dispose(disposing);
    }
}
