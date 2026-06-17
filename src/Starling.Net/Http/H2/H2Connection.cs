using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common;
using Starling.Common.Diagnostics;
using Starling.Net.Http.Decoding;
using Starling.Net.Http.H2.Hpack;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Net.Http.H2;

internal static partial class H2ConnectionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "h2 connection error {ErrorCode}: {ErrorMessage}")]
    public static partial void ConnectionError(ILogger logger, string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "transport dispose threw during reader-loop teardown")]
    public static partial void TransportDisposeFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "best-effort control frame failed on dying connection")]
    public static partial void SafeWriteFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "reader task threw during DisposeAsync cleanup")]
    public static partial void ReaderTaskCleanupFailed(ILogger logger, Exception ex);
}

/// <summary>
/// A single HTTP/2 connection multiplexing many request/response streams over
/// one TLS transport (RFC 9113). One reader loop demultiplexes inbound frames
/// to per-stream state; outbound frames are serialized by the
/// <see cref="H2FrameWriter"/>. Owns the underlying <see cref="IHttpTransport"/>
/// and tears it down when the connection closes.
/// </summary>
internal sealed class H2Connection : IAsyncDisposable
{
    // Our advertised receive settings. A generous stream/connection receive
    // window lets servers send a sizeable first burst before our first
    // WINDOW_UPDATE; we then replenish per DATA frame to keep windows topped up.
    private const int OurInitialWindowSize = 8 * 1024 * 1024;
    private const int OurMaxFrameSize = H2Protocol.DefaultMaxFrameSize;

    // Request header defaults, matching H1RequestWriter so both paths look
    // identical to a server.
    private const string DefaultUserAgent = "Starling/0.1 (https://github.com/anthropic-starling)";
    private const string DefaultAccept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
    private const string DefaultAcceptEncoding = "gzip, br, deflate";

    private readonly IHttpTransport _transport;
    private readonly H2FrameReader _reader;
    private readonly H2FrameWriter _writer;
    private readonly HpackEncoder _encoder = new();
    private readonly HpackDecoder _decoder = new(H2Protocol.DefaultHeaderTableSize);
    private readonly ILogger _log;
    private readonly Action<H2Connection>? _onClosed;

    private readonly object _lock = new();
    private readonly Dictionary<int, H2Stream> _streams = [];
    private readonly AsyncSignal _windowSignal = new();
    private readonly SemaphoreSlim _openLock = new(1, 1);

    // Cancelled when the connection is torn down. Drives the reader loop and the
    // best-effort control-frame writes that aren't bound to a caller's request,
    // so they stop promptly instead of running against a dying transport. The
    // token is captured up front so it stays usable after the source is disposed.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    // Peer settings governing what we send.
    private int _peerInitialWindowSize = H2Protocol.DefaultInitialWindowSize;
    private int _peerMaxFrameSize = H2Protocol.DefaultMaxFrameSize;
    private long _peerMaxConcurrentStreams = long.MaxValue;
    private int _connSendWindow = H2Protocol.DefaultInitialWindowSize;

    private int _nextStreamId = 1;
    private int _activeStreams;
    private bool _goAwayReceived;
    private bool _closed;
    private int _transportDisposed;

    // Header-block assembly across HEADERS + CONTINUATION frames.
    private readonly MemoryStream _headerFragments = new();
    private int _headerStreamId;       // 0 == not currently assembling
    private bool _headerEndStream;

    private Task _readerTask = Task.CompletedTask;

    public OriginKey Origin { get; }

    /// <summary>The verified leaf certificate of the underlying TLS transport, if any.</summary>
    public Tls.CertificateSummary? PeerCertificate => _transport.PeerCertificate;

    private H2Connection(IHttpTransport transport, OriginKey origin, ILogger<H2Connection>? log, Action<H2Connection>? onClosed)
    {
        _transport = transport;
        Origin = origin;
        _log = log ?? NullLogger<H2Connection>.Instance;
        _onClosed = onClosed;
        _lifetimeToken = _lifetime.Token;
        _reader = new H2FrameReader(transport.Stream, OurMaxFrameSize);
        _writer = new H2FrameWriter(transport.Stream);
    }

    /// <summary>
    /// Perform the connection preface, exchange SETTINGS, raise the connection
    /// receive window, and start the reader loop. The returned connection is
    /// ready to accept <see cref="SendAsync"/> calls immediately (RFC 9113 §3.4
    /// permits sending requests right after our preface, before the server's
    /// SETTINGS arrive).
    /// </summary>
    public static async Task<H2Connection> StartAsync(
        IHttpTransport transport, OriginKey origin, ILogger<H2Connection>? log = null,
        Action<H2Connection>? onClosed = null, CancellationToken ct = default)
    {
        var conn = new H2Connection(transport, origin, log, onClosed);
        await conn._writer.WritePrefaceAndSettingsAsync(
        [
            (H2SettingId.EnablePush, 0),
            (H2SettingId.InitialWindowSize, OurInitialWindowSize),
            (H2SettingId.MaxFrameSize, OurMaxFrameSize),
            (H2SettingId.HeaderTableSize, H2Protocol.DefaultHeaderTableSize),
        ], ct).ConfigureAwait(false);

        // Raise the connection-level receive window from its 65535 default.
        await conn._writer.WriteWindowUpdateAsync(0, OurInitialWindowSize - H2Protocol.DefaultInitialWindowSize, ct)
            .ConfigureAwait(false);

        // The CancellationToken.None here only governs whether Task.Run starts the
        // delegate; the loop's own cancellation rides on the connection lifetime token.
        conn._readerTask = Task.Run(() => conn.ReaderLoopAsync(conn._lifetimeToken), CancellationToken.None);
        StarlingTelemetry.Counter("net.h2.connections_opened", 1);
        return conn;
    }

    /// <summary>True while new streams can still be opened on this connection.</summary>
    public bool IsUsable
    {
        get
        {
            lock (_lock)
            {
                return !_closed && !_goAwayReceived && _nextStreamId > 0 && _nextStreamId < int.MaxValue;
            }
        }
    }

    /// <summary>
    /// Open a stream, send the request, and await the assembled response.
    /// Returns a retryable <see cref="NetworkError.TransportFailure"/> when the
    /// connection went away before this request could be processed, so the
    /// caller can re-dial.
    /// </summary>
    public async Task<Result<HttpResponse, NetworkError>> SendAsync(
        HttpRequest request, StarlingUrl url, CancellationToken ct)
    {
        H2Stream stream;
        var fields = BuildRequestFields(request, url);
        var block = _encoder.Encode(fields);
        var hasBody = !request.Body.IsEmpty;

        await _openLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Reserve a concurrency slot and allocate a monotonically increasing
            // stream id. Holding _openLock across the HEADERS write guarantees
            // ids reach the wire in increasing order (RFC 9113 §5.1.1).
            while (true)
            {
                var wait = _windowSignal.WaitAsync();
                lock (_lock)
                {
                    if (_closed || _goAwayReceived)
                    {
                        return Result<HttpResponse, NetworkError>.Err(NetworkError.TransportFailure);
                    }

                    if (_nextStreamId <= 0 || _nextStreamId >= int.MaxValue)
                    {
                        return Result<HttpResponse, NetworkError>.Err(NetworkError.TransportFailure);
                    }

                    if (_activeStreams < _peerMaxConcurrentStreams)
                    {
                        stream = new H2Stream(_nextStreamId) { SendWindow = _peerInitialWindowSize };
                        _streams[_nextStreamId] = stream;
                        _nextStreamId += 2;
                        _activeStreams++;
                        break;
                    }
                }
                await wait.ConfigureAwait(false);
            }

            StarlingTelemetry.Counter("net.h2.requests", 1);
            await _writer.WriteHeadersAsync(stream.Id, block, endStream: !hasBody, _peerMaxFrameSize, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _openLock.Release();
        }

        if (hasBody)
        {
            await SendBodyAsync(stream, request.Body, ct).ConfigureAwait(false);
        }

        try
        {
            return await stream.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's token fired, so cancel the stream at the peer and drop
            // our local state. We use the connection lifetime token (not the
            // already-cancelled caller token) so the RST_STREAM still goes out
            // while the connection is alive.
            await SafeWriteAsync(() => _writer.WriteRstStreamAsync(stream.Id, H2ErrorCode.Cancel, _lifetimeToken))
                .ConfigureAwait(false);
            RemoveStream(stream.Id);
            throw;
        }
    }

    private async Task SendBodyAsync(H2Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var remaining = body;
        while (!remaining.IsEmpty)
        {
            int budget;
            while (true)
            {
                var wait = _windowSignal.WaitAsync();
                lock (_lock)
                {
                    if (_closed)
                    {
                        return; // stream will be failed by the reader-loop teardown
                    }

                    budget = Math.Min(Math.Min(stream.SendWindow, _connSendWindow), _peerMaxFrameSize);
                    budget = Math.Min(budget, remaining.Length);
                    if (budget > 0)
                    {
                        stream.SendWindow -= budget;
                        _connSendWindow -= budget;
                        break;
                    }
                }
                await wait.ConfigureAwait(false);
            }

            var chunk = remaining[..budget];
            remaining = remaining[budget..];
            await _writer.WriteDataAsync(stream.Id, chunk, endStream: remaining.IsEmpty, ct)
                .ConfigureAwait(false);
        }
    }

    private List<(string, string)> BuildRequestFields(HttpRequest request, StarlingUrl url)
    {
        var scheme = url.IsHttps ? "https" : "http";
        var path = H1.H1RequestWriter.BuildRequestTarget(url);
        var authority = H1.H1RequestWriter.BuildHostHeader(url);

        var fields = new List<(string, string)>(request.Headers.Count + 8)
        {
            (":method", request.Method),
            (":scheme", scheme),
            (":authority", authority),
            (":path", path),
        };

        var hasUserAgent = false;
        var hasAccept = false;
        var hasAcceptEncoding = false;
        var hasContentLength = false;

        foreach (var kv in request.Headers)
        {
            var lower = kv.Key.ToLowerInvariant();
            // RFC 9113 §8.2.2: connection-specific header fields are forbidden;
            // the authority replaces Host.
            if (lower is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "host")
            {
                continue;
            }

            if (lower == "te" && !string.Equals(kv.Value, "trailers", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lower == "user-agent")
            {
                hasUserAgent = true;
            }
            else if (lower == "accept")
            {
                hasAccept = true;
            }
            else if (lower == "accept-encoding")
            {
                hasAcceptEncoding = true;
            }
            else if (lower == "content-length")
            {
                hasContentLength = true;
            }

            fields.Add((lower, kv.Value));
        }

        if (!hasUserAgent)
        {
            fields.Add(("user-agent", DefaultUserAgent));
        }

        if (!hasAccept)
        {
            fields.Add(("accept", DefaultAccept));
        }

        if (!hasAcceptEncoding)
        {
            fields.Add(("accept-encoding", DefaultAcceptEncoding));
        }
        // Send content-length for body-bearing methods even with an empty body.
        // END_STREAM already signals "no DATA", but some origins/WAFs reject an
        // empty POST that lacks content-length (411). Browsers always send "0".
        if (!hasContentLength &&
            (!request.Body.IsEmpty || H1.H1RequestWriter.MethodCarriesBody(request.Method)))
        {
            fields.Add(("content-length",
                request.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return fields;
    }

    // ---- Reader loop -------------------------------------------------------

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                RawFrame? maybe;
                try
                {
                    maybe = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break; // truncated frame == peer closed
                }

                if (maybe is not { } frame)
                {
                    break; // clean EOF
                }

                await HandleFrameAsync(frame, ct).ConfigureAwait(false);
            }

            CloseAll(NetworkError.TransportFailure);
        }
        catch (OperationCanceledException)
        {
            // The lifetime token fired — the connection is being torn down.
            CloseAll(NetworkError.TransportFailure);
        }
        catch (H2ConnectionException ex)
        {
            await SafeWriteAsync(() => _writer.WriteGoAwayAsync(0, ex.Code, ct))
                .ConfigureAwait(false);
            H2ConnectionLog.ConnectionError(_log, ex.Code.ToString(), ex.Message);
            CloseAll(NetworkError.ProtocolError);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            CloseAll(NetworkError.TransportFailure);
        }
        finally
        {
            // The loop is the sole reader; once it stops the socket is done.
            // Disposing here prevents a leak when the server closes first.
            await SafeDisposeTransportAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(RawFrame frame, CancellationToken ct)
    {
        // While assembling a header block only CONTINUATION frames for the same
        // stream are legal (RFC 9113 §6.10).
        if (_headerStreamId != 0
            && (frame.Type != H2FrameType.Continuation || frame.StreamId != _headerStreamId))
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "expected CONTINUATION");
        }

        switch (frame.Type)
        {
            case H2FrameType.Headers: HandleHeaders(frame); break;
            case H2FrameType.Continuation: HandleContinuation(frame); break;
            case H2FrameType.Data: await HandleDataAsync(frame, ct).ConfigureAwait(false); break;
            case H2FrameType.Settings: await HandleSettingsAsync(frame, ct).ConfigureAwait(false); break;
            case H2FrameType.WindowUpdate: HandleWindowUpdate(frame); break;
            case H2FrameType.Ping: await HandlePingAsync(frame, ct).ConfigureAwait(false); break;
            case H2FrameType.GoAway: HandleGoAway(frame); break;
            case H2FrameType.RstStream: HandleRstStream(frame); break;
            case H2FrameType.Priority: break; // ignored
            case H2FrameType.PushPromise:
                // We advertised ENABLE_PUSH=0, so a push is a protocol error.
                throw new H2ConnectionException(H2ErrorCode.ProtocolError, "unexpected PUSH_PROMISE");
            default: break; // unknown frame types are ignored (§4.1)
        }
    }

    private void HandleHeaders(RawFrame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "HEADERS on stream 0");
        }

        var payload = frame.Payload.AsSpan();
        var content = StripHeadersPadding(payload, frame.Flags);

        _headerFragments.SetLength(0);
        _headerFragments.Write(content);
        _headerStreamId = frame.StreamId;
        _headerEndStream = frame.HasFlag(H2Flags.EndStream);

        if (frame.HasFlag(H2Flags.EndHeaders))
        {
            CompleteHeaderBlock();
        }
    }

    private void HandleContinuation(RawFrame frame)
    {
        _headerFragments.Write(frame.Payload);
        if (frame.HasFlag(H2Flags.EndHeaders))
        {
            CompleteHeaderBlock();
        }
    }

    private void CompleteHeaderBlock()
    {
        var streamId = _headerStreamId;
        var endStream = _headerEndStream;
        var block = _headerFragments.ToArray();
        _headerStreamId = 0;
        _headerFragments.SetLength(0);

        // Always decode to keep HPACK state in sync, even if the stream is gone.
        if (!_decoder.TryDecode(block, out var fields))
        {
            throw new H2ConnectionException(H2ErrorCode.CompressionError, "HPACK decode failed");
        }

        H2Stream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(streamId, out stream);
        }

        if (stream is null)
        {
            return; // unknown/reset stream — fields discarded
        }

        if (stream.ResponseHeaders is null)
        {
            var status = ReadStatus(fields);
            if (status is null)
            {
                FailStream(stream, NetworkError.ProtocolError, H2ErrorCode.ProtocolError);
                return;
            }
            if (status is >= 100 and < 200)
            {
                return; // interim (1xx) response — keep waiting for the final one
            }

            stream.ResponseHeaders = fields;
        }
        // else: trailers — decoded for state, otherwise ignored (v1).

        if (endStream)
        {
            FinishStream(stream);
        }
    }

    private async Task HandleDataAsync(RawFrame frame, CancellationToken ct)
    {
        if (frame.StreamId == 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "DATA on stream 0");
        }

        var flowLength = frame.Payload.Length; // padding counts toward flow control
        var data = StripDataPadding(frame.Payload.AsSpan(), frame.Flags);

        H2Stream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(frame.StreamId, out stream);
        }

        if (stream is not null)
        {
            stream.Body.Write(data);
            if (frame.HasFlag(H2Flags.EndStream))
            {
                FinishStream(stream);
            }
        }

        // Replenish receive windows so the peer can keep sending.
        if (flowLength > 0)
        {
            await _writer.WriteWindowUpdateAsync(0, flowLength, ct).ConfigureAwait(false);
            if (stream is not null && !frame.HasFlag(H2Flags.EndStream))
            {
                await _writer.WriteWindowUpdateAsync(frame.StreamId, flowLength, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task HandleSettingsAsync(RawFrame frame, CancellationToken ct)
    {
        if (frame.StreamId != 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "SETTINGS on non-zero stream");
        }

        if (frame.HasFlag(H2Flags.Ack))
        {
            if (frame.Payload.Length != 0)
            {
                throw new H2ConnectionException(H2ErrorCode.FrameSizeError, "SETTINGS ACK with payload");
            }

            return;
        }
        if (frame.Payload.Length % 6 != 0)
        {
            throw new H2ConnectionException(H2ErrorCode.FrameSizeError, "SETTINGS length not a multiple of 6");
        }

        var p = frame.Payload;
        for (var i = 0; i < p.Length; i += 6)
        {
            var id = (H2SettingId)((p[i] << 8) | p[i + 1]);
            var value = ((uint)p[i + 2] << 24) | ((uint)p[i + 3] << 16) | ((uint)p[i + 4] << 8) | p[i + 5];
            ApplyPeerSetting(id, value);
        }

        _windowSignal.Pulse();
        await _writer.WriteSettingsAckAsync(ct).ConfigureAwait(false);
    }

    private void ApplyPeerSetting(H2SettingId id, uint value)
    {
        switch (id)
        {
            case H2SettingId.InitialWindowSize:
                if (value > int.MaxValue)
                {
                    throw new H2ConnectionException(H2ErrorCode.FlowControlError, "INITIAL_WINDOW_SIZE too large");
                }

                lock (_lock)
                {
                    var delta = (int)value - _peerInitialWindowSize;
                    _peerInitialWindowSize = (int)value;
                    foreach (var s in _streams.Values)
                    {
                        s.SendWindow += delta; // RFC 9113 §6.9.2
                    }
                }
                break;
            case H2SettingId.MaxFrameSize:
                if (value is < H2Protocol.DefaultMaxFrameSize or > H2Protocol.MaxAllowedFrameSize)
                {
                    throw new H2ConnectionException(H2ErrorCode.ProtocolError, "invalid MAX_FRAME_SIZE");
                }

                lock (_lock)
                {
                    _peerMaxFrameSize = (int)value;
                }

                break;
            case H2SettingId.MaxConcurrentStreams:
                lock (_lock)
                {
                    _peerMaxConcurrentStreams = value;
                }

                break;
            case H2SettingId.EnablePush:
                if (value > 1)
                {
                    throw new H2ConnectionException(H2ErrorCode.ProtocolError, "invalid ENABLE_PUSH");
                }

                break;
            case H2SettingId.HeaderTableSize:
            case H2SettingId.MaxHeaderListSize:
            default:
                break; // our encoder uses no dynamic table; no list-size cap enforced
        }
    }

    private void HandleWindowUpdate(RawFrame frame)
    {
        if (frame.Payload.Length != 4)
        {
            throw new H2ConnectionException(H2ErrorCode.FrameSizeError, "WINDOW_UPDATE length != 4");
        }

        var increment = (int)(((uint)frame.Payload[0] << 24 | (uint)frame.Payload[1] << 16
            | (uint)frame.Payload[2] << 8 | frame.Payload[3]) & 0x7fff_ffff);

        if (frame.StreamId == 0)
        {
            if (increment == 0)
            {
                throw new H2ConnectionException(H2ErrorCode.ProtocolError, "0 connection WINDOW_UPDATE");
            }

            lock (_lock)
            {
                var updated = (long)_connSendWindow + increment;
                if (updated > H2Protocol.MaxWindowSize)
                {
                    throw new H2ConnectionException(H2ErrorCode.FlowControlError, "connection window overflow");
                }

                _connSendWindow = (int)updated;
            }
            _windowSignal.Pulse();
            return;
        }

        H2Stream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(frame.StreamId, out stream);
            if (stream is not null && increment > 0)
            {
                stream.SendWindow = (int)Math.Min((long)stream.SendWindow + increment, H2Protocol.MaxWindowSize);
            }
        }
        if (increment > 0)
        {
            _windowSignal.Pulse();
        }
    }

    private async Task HandlePingAsync(RawFrame frame, CancellationToken ct)
    {
        if (frame.StreamId != 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "PING on non-zero stream");
        }

        if (frame.Payload.Length != 8)
        {
            throw new H2ConnectionException(H2ErrorCode.FrameSizeError, "PING length != 8");
        }

        if (!frame.HasFlag(H2Flags.Ack))
        {
            await _writer.WritePingAckAsync(frame.Payload, ct).ConfigureAwait(false);
        }
    }

    private void HandleGoAway(RawFrame frame)
    {
        if (frame.Payload.Length < 8)
        {
            throw new H2ConnectionException(H2ErrorCode.FrameSizeError, "GOAWAY too short");
        }

        var lastStreamId = (int)(((uint)frame.Payload[0] << 24 | (uint)frame.Payload[1] << 16
            | (uint)frame.Payload[2] << 8 | frame.Payload[3]) & 0x7fff_ffff);

        List<H2Stream> refused;
        lock (_lock)
        {
            _goAwayReceived = true;
            // Streams above lastStreamId were never processed — safe to retry.
            refused = _streams.Values.Where(s => s.Id > lastStreamId).ToList();
        }
        foreach (var s in refused)
        {
            FailStream(s, NetworkError.TransportFailure, rstCode: null);
        }

        _windowSignal.Pulse();
    }

    private void HandleRstStream(RawFrame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "RST_STREAM on stream 0");
        }

        if (frame.Payload.Length != 4)
        {
            throw new H2ConnectionException(H2ErrorCode.FrameSizeError, "RST_STREAM length != 4");
        }

        var code = (H2ErrorCode)((uint)frame.Payload[0] << 24 | (uint)frame.Payload[1] << 16
            | (uint)frame.Payload[2] << 8 | frame.Payload[3]);

        H2Stream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(frame.StreamId, out stream);
        }

        if (stream is not null)
        {
            var err = code == H2ErrorCode.RefusedStream
                ? NetworkError.TransportFailure // never processed — retryable
                : NetworkError.ProtocolError;
            FailStream(stream, err, rstCode: null);
        }
    }

    // ---- Stream completion -------------------------------------------------

    private void FinishStream(H2Stream stream)
    {
        lock (_lock)
        {
            if (stream.Finished)
            {
                return;
            }

            stream.Finished = true;
            _streams.Remove(stream.Id);
            _activeStreams--;
        }
        _windowSignal.Pulse();

        var result = BuildResponse(stream);
        stream.Completion.TrySetResult(result);
    }

    private void FailStream(H2Stream stream, NetworkError error, H2ErrorCode? rstCode)
    {
        bool first;
        lock (_lock)
        {
            first = !stream.Finished;
            if (first)
            {
                stream.Finished = true;
                _streams.Remove(stream.Id);
                _activeStreams--;
            }
        }
        if (!first)
        {
            return;
        }

        _windowSignal.Pulse();

        if (rstCode is { } rc)
        {
            _ = SafeWriteAsync(() => _writer.WriteRstStreamAsync(stream.Id, rc, _lifetimeToken));
        }

        stream.Completion.TrySetResult(Result<HttpResponse, NetworkError>.Err(error));
    }

    private static Result<HttpResponse, NetworkError> BuildResponse(H2Stream stream)
    {
        var fields = stream.ResponseHeaders!;
        var status = ReadStatus(fields)!.Value;

        var headers = new HttpHeaders();
        foreach (var f in fields)
        {
            if (f.Name.Length == 0 || f.Name[0] == ':')
            {
                continue; // skip pseudo-headers
            }

            try { headers.Add(f.Name, f.Value); }
            catch (ArgumentException) { return Result<HttpResponse, NetworkError>.Err(NetworkError.ProtocolError); }
        }

        var raw = stream.Body.ToArray();
        byte[] decoded;
        try
        {
            var encodings = BodyDecoder.ParseEncodings(headers.GetFirst("Content-Encoding"));
            decoded = encodings.Count == 0 ? raw : BodyDecoder.Decode(raw, encodings);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            return Result<HttpResponse, NetworkError>.Err(NetworkError.ProtocolError);
        }

        return Result<HttpResponse, NetworkError>.Ok(
            new HttpResponse("HTTP/2", status, string.Empty, headers, decoded));
    }

    private static int? ReadStatus(List<HpackHeaderField> fields)
    {
        foreach (var f in fields)
        {
            if (!string.Equals(f.Name, ":status", StringComparison.Ordinal))
            {
                continue;
            }

            return int.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                && code is >= 100 and <= 599
                ? code
                : null;
        }
        return null;
    }

    // ---- Teardown ----------------------------------------------------------

    private void CloseAll(NetworkError error)
    {
        List<H2Stream> pending;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = [.. _streams.Values];
            _streams.Clear();
            _activeStreams = 0;
        }
        // Stop the reader loop and any in-flight control-frame writes.
        _lifetime.Cancel();
        _windowSignal.Pulse();
        foreach (var s in pending)
        {
            if (!s.Finished)
            {
                s.Finished = true;
                s.Completion.TrySetResult(Result<HttpResponse, NetworkError>.Err(error));
            }
        }
        _onClosed?.Invoke(this);
    }

    private void RemoveStream(int streamId)
    {
        lock (_lock)
        {
            if (_streams.Remove(streamId))
            {
                _activeStreams--;
            }
        }
        _windowSignal.Pulse();
    }

    private async Task SafeDisposeTransportAsync()
    {
        if (Interlocked.Exchange(ref _transportDisposed, 1) != 0)
        {
            return;
        }

        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { H2ConnectionLog.TransportDisposeFailed(_log, ex); /* a half-broken socket may throw on shutdown */ }
    }

    private async Task SafeWriteAsync(Func<Task> write)
    {
        try { await write().ConfigureAwait(false); }
        catch (Exception ex) { H2ConnectionLog.SafeWriteFailed(_log, ex); /* best-effort control frame on a dying connection */ }
    }

    private static Span<byte> StripHeadersPadding(Span<byte> payload, H2Flags flags)
    {
        var offset = 0;
        var padLength = 0;
        if ((flags & H2Flags.Padded) != 0)
        {
            if (payload.Length < 1)
            {
                throw new H2ConnectionException(H2ErrorCode.ProtocolError, "padded HEADERS too short");
            }

            padLength = payload[0];
            offset = 1;
        }
        if ((flags & H2Flags.Priority) != 0)
        {
            if (payload.Length < offset + 5)
            {
                throw new H2ConnectionException(H2ErrorCode.ProtocolError, "HEADERS priority too short");
            }

            offset += 5;
        }
        var contentLength = payload.Length - offset - padLength;
        if (contentLength < 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "HEADERS padding exceeds frame");
        }

        return payload.Slice(offset, contentLength);
    }

    private static Span<byte> StripDataPadding(Span<byte> payload, H2Flags flags)
    {
        if ((flags & H2Flags.Padded) == 0)
        {
            return payload;
        }

        if (payload.Length < 1)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "padded DATA too short");
        }

        var padLength = payload[0];
        var contentLength = payload.Length - 1 - padLength;
        if (contentLength < 0)
        {
            throw new H2ConnectionException(H2ErrorCode.ProtocolError, "DATA padding exceeds frame");
        }

        return payload.Slice(1, contentLength);
    }

    public async ValueTask DisposeAsync()
    {
        // Graceful goodbye while the connection is still alive; if the reader loop
        // already tore down (lifetime cancelled), this write is skipped.
        await SafeWriteAsync(() => _writer.WriteGoAwayAsync(0, H2ErrorCode.NoError, _lifetimeToken))
            .ConfigureAwait(false);
        CloseAll(NetworkError.TransportFailure);
        // Dispose the transport first to unblock the reader loop's pending
        // ReadAsync, then wait for the loop to exit before tearing down the
        // writer it might still touch.
        await SafeDisposeTransportAsync().ConfigureAwait(false);
        try { await _readerTask.ConfigureAwait(false); }
        catch (Exception ex) { H2ConnectionLog.ReaderTaskCleanupFailed(_log, ex); /* loop teardown */ }
        _writer.Dispose();
        _openLock.Dispose();
        _lifetime.Dispose();
    }
}
