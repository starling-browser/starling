using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common;
using Starling.Common.Diagnostics;
using Starling.Net.Dns;
using Starling.Net.Http;
using Starling.Net.Http.Cookies;
using Starling.Net.Http.H1;
using Starling.Net.Http.H2;
using Starling.Net.Tcp;
using Starling.Net.Tls;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Net;

internal static partial class StarlingHttpClientLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP request failed: {Error}")]
    public static partial void RequestFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "transport dispose threw during cleanup")]
    public static partial void SafeDisposeFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Top-level HTTP client. Resolves a URL to a TCP endpoint, opens a transport
/// (TLS for https, plain for http), and speaks HTTP/2 or HTTP/1.1 depending on
/// the TLS application protocol negotiated during the handshake. Over HTTP/1.1,
/// sequential requests to the same origin reuse a pooled TCP+TLS transport when
/// both sides advertise keep-alive. Over HTTP/2, requests to the
/// same origin are multiplexed onto a single shared connection held by the
/// <see cref="H2ConnectionManager"/>.
/// </summary>
/// <remarks>
/// For a single GET against <c>https://example.com</c> the data flow is:
/// <list type="bullet">
///   <item><see cref="DnsResolver"/> → A/AAAA records</item>
///   <item><see cref="TcpDialer"/> → <see cref="ITcpConnection"/></item>
///   <item><see cref="BcTlsTransport"/> → negotiated TLS stream</item>
///   <item><see cref="H1RequestWriter"/> → wire bytes onto the stream</item>
///   <item><see cref="H1ResponseParser"/> → fully buffered <see cref="HttpResponse"/></item>
/// </list>
/// For HTTP/1.1 a <see cref="ConnectionPool"/> sits in front of the dialer:
/// every send asks the pool first, and clean responses with a definite body
/// length and keep-alive headers return the transport to the pool. For HTTP/2,
/// the post-handshake transport is handed to an <see cref="H2Connection"/> and
/// subsequent requests to the same origin are multiplexed onto it.
/// </remarks>
public sealed class StarlingHttpClient : IDisposable
{
    private readonly StarlingHttpClientOptions _options;
    private readonly DnsResolver _dns;
    private readonly TcpDialer _dialer;
    private readonly ConnectionPool _pool;
    private readonly ILoggerFactory _loggerFactory;
    private readonly H2ConnectionManager _h2;
    private readonly ILogger _log;
    private bool _disposed;

    public StarlingHttpClient() : this(new StarlingHttpClientOptions()) { }

    public StarlingHttpClient(StarlingHttpClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<StarlingHttpClient>();
        _h2 = new H2ConnectionManager(_loggerFactory.CreateLogger<H2ConnectionManager>());
        _dns = options.DnsResolver ?? new DnsResolver(new UdpDnsTransport());
        _dialer = new TcpDialer(_dns) { ConnectTimeout = options.ConnectTimeout };
        _pool = options.ConnectionPool ?? new ConnectionPool();
    }

    /// <summary>The User-Agent presented on the wire. Surfaced so the JS layer
    /// can keep <c>navigator.userAgent</c> in lockstep with what we actually
    /// send (sites compare the two).</summary>
    public string UserAgent => _options.UserAgent;

    /// <summary>Idle connection pool. Exposed mainly for tests asserting on
    /// reuse / capacity behaviour.</summary>
    public ConnectionPool ConnectionPool => _pool;

    public Task<Result<HttpResponse, NetworkError>> GetAsync(string url, CancellationToken ct = default)
    {
        var parsed = global::Starling.Url.UrlParser.Parse(url);
        if (parsed.IsErr)
        {
            return Task.FromResult(Result<HttpResponse, NetworkError>.Err(NetworkError.BadUrl));
        }

        return SendAsync(HttpRequest.Get(parsed.Value), ct);
    }

    public Task<Result<HttpResponse, NetworkError>> GetAsync(StarlingUrl url, CancellationToken ct = default)
        => SendAsync(HttpRequest.Get(url), ct);

    public async Task<Result<HttpResponse, NetworkError>> SendAsync(
        HttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = request.Url;

        if (!url.IsHttp && !url.IsHttps)
        {
            return Result<HttpResponse, NetworkError>.Err(NetworkError.UnsupportedScheme);
        }

        if (string.IsNullOrEmpty(url.Host))
        {
            return Result<HttpResponse, NetworkError>.Err(NetworkError.BadUrl);
        }

        var port = url.Port ?? url.DefaultPort
            ?? (url.IsHttps ? 443 : url.IsHttp ? 80 : 0);
        if (port is < 1 or > 65535)
        {
            return Result<HttpResponse, NetworkError>.Err(NetworkError.BadUrl);
        }

        var origin = OriginKey.Create(url.IsHttps ? "https" : "http", url.Host, port);

        // Present a recognised browser UA on every request (document, subresource,
        // and JS fetch/XHR) unless the caller forced one. Both the H1 writer and
        // the H2 header builder honour an existing User-Agent header, so this is
        // the single wire-side source of truth. See StarlingHttpClientOptions.
        if (!request.Headers.Contains("User-Agent"))
        {
            request.Headers.Set("User-Agent", _options.UserAgent);
        }

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(_options.RequestTimeout);

        using var httpSpan = StarlingTelemetry.Span("net", "http");
        Activity.Current?.SetTag("http.method", request.Method);
        Activity.Current?.SetTag("http.url", url.ToString());
        Activity.Current?.SetTag("server.address", origin.Host);
        Activity.Current?.SetTag("server.port", origin.Port);
        StarlingTelemetry.Counter("net.http.requests", 1);

        try
        {
            // 0. Reuse a live, multiplexed HTTP/2 connection if one is open for
            //    this origin. A GOAWAY/closed connection between requests yields
            //    a retryable failure; we then fall through to a fresh dial.
            if (_h2.TryGet(origin) is { } h2Existing)
            {
                var h2Result = await SendOverH2Async(
                    h2Existing, request, url, reused: true, requestCts.Token).ConfigureAwait(false);
                if (h2Result is { } got)
                {
                    RecordResponseTags(got);
                    return got;
                }
                // Connection went away before our request was processed — re-dial.
            }

            // 1. Try the pool first. A pooled transport may turn out to have
            //    been closed by the peer between the prior response and this
            //    request; if our send raises an IO error — or reads back zero
            //    bytes when we were expecting a status line — we fall through
            //    to a fresh dial.
            var pooled = _pool.TryAcquire(origin);
            if (pooled is not null)
            {
                StarlingTelemetry.Counter("net.http.connection_reused", 1);
                Activity.Current?.SetTag("connection.reused", true);
                var pooledOutcome = await TrySendOnTransportAsync(
                    pooled, request, url, fromPool: true, requestCts.Token).ConfigureAwait(false);
                if (pooledOutcome.UsedTransport)
                {
                    RecordResponseTags(pooledOutcome.Result);
                    return pooledOutcome.Result;
                }
                // Otherwise the connection was unusable (closed/IO) — dispose
                // and retry with a fresh dial. Re-tag the span: this request
                // ended up paying for a fresh handshake despite the pool hit.
                Activity.Current?.SetTag("connection.reused", false);
                await SafeDisposeAsync(pooled).ConfigureAwait(false);
            }
            else
            {
                Activity.Current?.SetTag("connection.reused", false);
            }

            // 2. Dial + (optionally) TLS-handshake a new transport.
            var dialed = await DialAsync(url, origin, requestCts.Token).ConfigureAwait(false);
            if (dialed.IsErr)
            {
                RecordError(dialed.Error);
                return Result<HttpResponse, NetworkError>.Err(dialed.Error);
            }

            StarlingTelemetry.Counter("net.http.connection_opened", 1);

            var fresh = dialed.Value;

            // ALPN decides the protocol. For "h2" the transport becomes a
            // multiplexed connection owned by the H2 manager; for everything
            // else we speak HTTP/1.1 over the transport's byte stream.
            if (string.Equals(fresh.Alpn, "h2", StringComparison.Ordinal))
            {
                var conn = await H2Connection.StartAsync(
                    fresh, origin, _loggerFactory.CreateLogger<H2Connection>(), c => _h2.Remove(origin, c), requestCts.Token).ConfigureAwait(false);
                var winner = _h2.Adopt(origin, conn);
                if (!ReferenceEquals(winner, conn))
                {
                    await conn.DisposeAsync().ConfigureAwait(false); // lost the race; use the incumbent
                }

                var h2Fresh = await SendOverH2Async(
                    winner, request, url, reused: false, requestCts.Token).ConfigureAwait(false);
                var h2Final = h2Fresh ?? Result<HttpResponse, NetworkError>.Err(NetworkError.TransportFailure);
                RecordResponseTags(h2Final);
                return h2Final;
            }

            var freshOutcome = await TrySendOnTransportAsync(
                fresh, request, url, fromPool: false, requestCts.Token).ConfigureAwait(false);
            if (!freshOutcome.UsedTransport)
            {
                // A brand-new transport that refused to talk: surface a
                // transport error and discard. We don't loop again to avoid
                // hammering.
                await SafeDisposeAsync(fresh).ConfigureAwait(false);
                RecordError(NetworkError.TransportFailure);
                return Result<HttpResponse, NetworkError>.Err(NetworkError.TransportFailure);
            }
            RecordResponseTags(freshOutcome.Result);
            return freshOutcome.Result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordError(NetworkError.RequestTimeout);
            return Result<HttpResponse, NetworkError>.Err(NetworkError.RequestTimeout);
        }
    }

    private void RecordResponseTags(Result<HttpResponse, NetworkError> result)
    {
        if (result.IsErr)
        {
            RecordError(result.Error);
            return;
        }
        var response = result.Value;
        Activity.Current?.SetTag("http.status_code", response.StatusCode);
        Activity.Current?.SetTag("http.response.body.size", response.Body.Length);
        StarlingTelemetry.Counter("net.http.bytes_in", response.Body.Length);
        if (response.StatusCode >= 500)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, $"HTTP {response.StatusCode}");
        }
    }

    private void RecordError(NetworkError error)
    {
        var message = error.ToString();
        StarlingTelemetry.Counter("net.http.failures", 1);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, message);
        StarlingHttpClientLog.RequestFailed(_log, message);
    }

    private async Task<Result<IHttpTransport, NetworkError>> DialAsync(
        StarlingUrl url, OriginKey origin, CancellationToken ct)
    {
        // DNS resolution span. The TcpDialer drives DNS internally; we mirror
        // the work with our own resolve call so the span/counter scope is
        // crisp and DNS-only failures are distinguishable from connect-only
        // failures. The result feeds DialDirectAsync below.
        Result<DnsResult, DnsError> dnsResult;
        using (var dnsSpan = StarlingTelemetry.Span("net", "dns"))
        {
            Activity.Current?.SetTag("dns.host", origin.Host);
            StarlingTelemetry.Counter("net.dns.resolutions", 1);
            dnsResult = await _dns.ResolveAsync(origin.Host, ct).ConfigureAwait(false);
            if (dnsResult.IsErr)
            {
                StarlingTelemetry.Counter("net.dns.failures", 1);
                Activity.Current?.SetStatus(ActivityStatusCode.Error, dnsResult.Error.ToString());
                return Result<IHttpTransport, NetworkError>.Err(NetworkError.DnsFailure);
            }
        }

        // TCP connect span. Try each resolved address; first success wins.
        ITcpConnection? tcp = null;
        TcpError? lastTcpError = null;
        using (var tcpSpan = StarlingTelemetry.Span("net", "tcp_connect"))
        {
            Activity.Current?.SetTag("server.address", origin.Host);
            Activity.Current?.SetTag("server.port", origin.Port);
            StarlingTelemetry.Counter("net.tcp.connects", 1);

            foreach (var ip in dnsResult.Value.Addresses)
            {
                var endpoint = new System.Net.IPEndPoint(ip, origin.Port);
                var dial = await _dialer.DialDirectAsync(
                    endpoint,
                    new TcpEndpoint(origin.Host, origin.Port),
                    ct).ConfigureAwait(false);
                if (dial.IsOk)
                {
                    tcp = dial.Value;
                    break;
                }
                lastTcpError = dial.Error;
            }

            if (tcp is null)
            {
                StarlingTelemetry.Counter("net.tcp.failures", 1);
                var err = lastTcpError == TcpError.Timeout
                    ? NetworkError.ConnectTimeout
                    : NetworkError.ConnectFailed;
                Activity.Current?.SetStatus(ActivityStatusCode.Error, err.ToString());
                return Result<IHttpTransport, NetworkError>.Err(err);
            }
        }

        if (url.IsHttps)
        {
            using var tlsSpan = StarlingTelemetry.Span("net", "tls_handshake");
            Activity.Current?.SetTag("server.address", origin.Host);
            StarlingTelemetry.Counter("net.tls.handshakes", 1);

            var tlsResult = await BcTlsTransport.ConnectAsync(
                tcp,
                new TlsClientOptions(origin.Host, _options.AlpnProtocols),
                ct).ConfigureAwait(false);
            if (tlsResult.IsErr)
            {
                StarlingTelemetry.Counter("net.tls.failures", 1);
                await tcp.DisposeAsync().ConfigureAwait(false);
                var err = tlsResult.Error == TlsError.CertificateRejected
                    ? NetworkError.TlsCertificateRejected
                    : NetworkError.TlsHandshakeFailed;
                Activity.Current?.SetStatus(ActivityStatusCode.Error, err.ToString());
                return Result<IHttpTransport, NetworkError>.Err(err);
            }
            // StarlingTlsClient pins TLS 1.3; tag it post-handshake.
            Activity.Current?.SetTag("tls.protocol", "TLSv1.3");
            // ALPN selects HTTP/2 vs HTTP/1.1 downstream (see SendAsync). We
            // advertise "h2, http/1.1", so either is expected; an empty/absent
            // ALPN falls back to HTTP/1.1.
            if (tlsResult.Value.NegotiatedApplicationProtocol is { Length: > 0 } alpn)
            {
                Activity.Current?.SetTag("tls.alpn", alpn);
            }

            return Result<IHttpTransport, NetworkError>.Ok(
                PooledHttpTransport.ForTls(origin, tcp, tlsResult.Value));
        }

        return Result<IHttpTransport, NetworkError>.Ok(
            PooledHttpTransport.ForPlainHttp(origin, tcp));
    }

    /// <summary>
    /// Write the request, parse the response, and decide whether to return
    /// the transport to the pool or close it. The returned
    /// <see cref="TransportSendOutcome.UsedTransport"/> flag is false when the
    /// caller should retry on a different transport (e.g. the pooled socket
    /// was closed between requests); the transport is still owned by the
    /// caller in that case and must be disposed.
    /// </summary>
    private async Task<TransportSendOutcome> TrySendOnTransportAsync(
        IHttpTransport transport,
        HttpRequest request,
        StarlingUrl url,
        bool fromPool,
        CancellationToken ct)
    {
        try
        {
            ApplyRequestCookies(request, url);

            using (var writeSpan = StarlingTelemetry.Span("net", "h1_request"))
            {
                StarlingTelemetry.Counter("net.h1.requests_written", 1);
                await _options.RequestWriter
                    .WriteAsync(request, transport.Stream, ct)
                    .ConfigureAwait(false);
                if (!request.Body.IsEmpty)
                {
                    StarlingTelemetry.Counter("net.http.bytes_out", request.Body.Length);
                }
            }

            Result<HttpResponse, HttpError> parseResult;
            using (var parseSpan = StarlingTelemetry.Span("net", "h1_response"))
            {
                StarlingTelemetry.Counter("net.h1.responses_parsed", 1);
                parseResult = await _options.ResponseParser
                    .ParseAsync(transport.Stream, ct).ConfigureAwait(false);
                if (parseResult.IsOk)
                {
                    Activity.Current?.SetTag("http.status_code", parseResult.Value.StatusCode);
                }
            }

            if (parseResult.IsErr)
            {
                // A pooled connection that the peer closed since the prior
                // response will write OK (TCP buffers locally) but read 0
                // bytes — the parser surfaces that as UnexpectedEof. Retry
                // on a fresh dial in that case; for everything else (or on
                // a fresh transport) surface the protocol failure.
                if (fromPool && parseResult.Error == HttpError.UnexpectedEof)
                {
                    return TransportSendOutcome.Unused();
                }

                await SafeDisposeAsync(transport).ConfigureAwait(false);
                return TransportSendOutcome.Used(
                    Result<HttpResponse, NetworkError>.Err(MapParseError(parseResult.Error)));
            }

            var response = parseResult.Value;
            response.Security = new ConnectionSecurity(
                response.HttpVersion, url.IsHttps, transport.PeerCertificate);

            StoreResponseCookies(response, url);

            // Decide pool fate. We can only safely reuse the transport if:
            //   - both sides agreed to keep-alive (RFC 9112 §9.3), AND
            //   - the body had a definite framing so we know we drained
            //     exactly to the end (no over-read into the next response).
            if (H1ResponseParser.IndicatesKeepAlive(response)
                && H1ResponseParser.HasDefiniteBodyFraming(response)
                && transport.IsOpen
                && !_disposed)
            {
                await _pool.ReleaseAsync(transport).ConfigureAwait(false);
            }
            else
            {
                await SafeDisposeAsync(transport).ConfigureAwait(false);
            }

            return TransportSendOutcome.Used(Result<HttpResponse, NetworkError>.Ok(response));
        }
        catch (OperationCanceledException)
        {
            await SafeDisposeAsync(transport).ConfigureAwait(false);
            // Cancellation includes our internal request-timeout CTS; let the
            // caller distinguish via its own token.
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            // If this was a pooled connection on its first byte, treat the
            // failure as "socket died while idle" and let the caller retry.
            // We can't reliably tell whether anything was actually written;
            // for idempotent GETs (the only verb the engine uses today) a
            // re-dial is safe. For non-GETs the caller surfaces this as a
            // transport failure when the retry has nothing to fall back to.
            //
            // Sockets propagate ECONNRESET as raw SocketException (not wrapped
            // in IOException), so we accept both.
            return TransportSendOutcome.Unused();
        }
    }

    /// <summary>
    /// Send a request over a multiplexed HTTP/2 connection. Returns null when a
    /// <paramref name="reused"/> connection turned out to be unusable (GOAWAY /
    /// closed) before the request was processed, signalling the caller to retry
    /// on a fresh dial. For a freshly-dialled connection the result — success or
    /// failure — is always returned.
    /// </summary>
    private async Task<Result<HttpResponse, NetworkError>?> SendOverH2Async(
        H2Connection conn, HttpRequest request, StarlingUrl url, bool reused, CancellationToken ct)
    {
        ApplyRequestCookies(request, url);
        Activity.Current?.SetTag("connection.reused", reused);
        Activity.Current?.SetTag("network.protocol.version", "2");
        if (reused)
        {
            StarlingTelemetry.Counter("net.http.connection_reused", 1);
        }

        Result<HttpResponse, NetworkError> result;
        using (var span = StarlingTelemetry.Span("net", "h2_request"))
        {
            StarlingTelemetry.Counter("net.h2.requests_sent", 1);
            result = await conn.SendAsync(request, url, ct).ConfigureAwait(false);
        }

        if (reused && result.IsErr && result.Error == NetworkError.TransportFailure)
        {
            return null;
        }

        if (result.IsOk)
        {
            result.Value.Security = new ConnectionSecurity(
                result.Value.HttpVersion, url.IsHttps, conn.PeerCertificate);
            StoreResponseCookies(result.Value, url);
        }
        return result;
    }

    /// <summary>
    /// Inject the cookie jar's view of the world into the request, but only when
    /// the caller hasn't already supplied a Cookie header.
    /// </summary>
    private void ApplyRequestCookies(HttpRequest request, StarlingUrl url)
    {
        if (_options.CookieJar is { } jar && !request.Headers.Contains("Cookie"))
        {
            var cookieHeader = jar.BuildCookieHeader(url);
            if (cookieHeader.Length > 0)
            {
                request.Headers.Set("Cookie", cookieHeader);
            }
        }
    }

    /// <summary>Persist any Set-Cookie headers from the response into the jar.</summary>
    private void StoreResponseCookies(HttpResponse response, StarlingUrl url)
    {
        if (_options.CookieJar is { } jar)
        {
            var setCookies = response.Headers.GetAll("Set-Cookie");
            if (setCookies.Count > 0)
            {
                jar.StoreFromHeaders(url, setCookies);
            }
        }
    }

    private async ValueTask SafeDisposeAsync(IHttpTransport transport)
    {
        try { await transport.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { StarlingHttpClientLog.SafeDisposeFailed(_log, ex); /* a half-broken socket may throw on shutdown; we don't care */ }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Synchronously dispose the pool and any HTTP/2 connections. Both only
        // await socket teardowns, which are non-blocking; .GetAwaiter()
        // .GetResult() is fine on the disposal path.
        _h2.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static NetworkError MapParseError(HttpError error) => error switch
    {
        HttpError.UnexpectedEof => NetworkError.TransportFailure,
        HttpError.TransportFailure => NetworkError.TransportFailure,
        HttpError.HeadersTooLarge => NetworkError.ProtocolError,
        HttpError.BodyTooLarge => NetworkError.ProtocolError,
        HttpError.BadStatusLine => NetworkError.ProtocolError,
        HttpError.BadHeader => NetworkError.ProtocolError,
        HttpError.BadChunkedFraming => NetworkError.ProtocolError,
        HttpError.UnsupportedEncoding => NetworkError.ProtocolError,
        HttpError.DecodeFailed => NetworkError.ProtocolError,
        _ => NetworkError.ProtocolError,
    };

    private readonly record struct TransportSendOutcome(bool UsedTransport, Result<HttpResponse, NetworkError> Result)
    {
        public static TransportSendOutcome Used(Result<HttpResponse, NetworkError> r) => new(true, r);
        public static TransportSendOutcome Unused() => new(false, default);
    }
}

public sealed class StarlingHttpClientOptions
{
    /// <summary>
    /// Browser-identifying User-Agent presented on every request. Many sites
    /// (notably google.com) sniff this and serve a degraded, JS-free page to any
    /// UA they don't recognise as a modern browser, so we present as a current
    /// Chrome on macOS. Injected by <see cref="StarlingHttpClient.SendAsync"/>
    /// when the request doesn't already carry a User-Agent, which is why it
    /// drives both the HTTP/1.1 and HTTP/2 paths from a single source of truth.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string UserAgent { get; init; } = DefaultUserAgent;
    public IReadOnlyList<string> AlpnProtocols { get; init; } = ["h2", "http/1.1"];
    public DnsResolver? DnsResolver { get; init; }
    public CookieJar? CookieJar { get; init; }
    public H1RequestWriter RequestWriter { get; init; } = new();
    public H1ResponseParser ResponseParser { get; init; } = new();

    /// <summary>
    /// Optional injected connection pool. When null, the client owns a
    /// freshly-constructed pool with default sizing (6 idle per origin,
    /// 60s idle timeout).
    /// </summary>
    public ConnectionPool? ConnectionPool { get; init; }

    /// <summary>
    /// Optional logger factory. When set, the client emits per-request log
    /// messages and passes the factory through to HTTP/2 connections.
    /// Tracing spans and metrics are always emitted via
    /// <see cref="Starling.Common.Diagnostics.StarlingTelemetry"/> regardless.
    /// Defaults to <see cref="NullLoggerFactory.Instance"/>.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}

public enum NetworkError
{
    BadUrl,
    UnsupportedScheme,
    DnsFailure,
    ConnectFailed,
    ConnectTimeout,
    RequestTimeout,
    TlsHandshakeFailed,
    TlsCertificateRejected,
    TransportFailure,
    ProtocolError,
}
