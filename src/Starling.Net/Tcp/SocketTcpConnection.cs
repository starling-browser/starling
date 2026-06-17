using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Starling.Net.Tcp;

internal static partial class SocketTcpConnectionLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "socket shutdown threw; peer may already be gone")]
    public static partial void ShutdownFailed(ILogger logger, Exception ex);
}

/// <summary>
/// <see cref="ITcpConnection"/> implementation backed by a real
/// <see cref="System.Net.Sockets.Socket"/>. Pure managed per Rule 0.
/// </summary>
internal sealed class SocketTcpConnection(Socket socket, TcpEndpoint endpoint, ILogger<SocketTcpConnection>? log = null)
    : ITcpConnection
{
    private readonly Socket _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    private readonly ILogger _log = log ?? NullLogger<SocketTcpConnection>.Instance;
    private bool _open = true;

    public TcpEndpoint Endpoint { get; } = endpoint;

    public bool IsOpen => _open && _socket.Connected;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (!_open)
        {
            return 0;
        }

        var n = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct)
            .ConfigureAwait(false);
        // A zero-length read request always completes with 0 bytes — that is
        // the documented "poll for readability" idiom, which SslStream issues
        // to await data without pinning a buffer. Only a 0-byte result for a
        // *non-empty* request is a peer half-close. Conflating the two marks
        // the connection dead on SslStream's first zero-byte read and breaks
        // the TLS handshake with a spurious EOF.
        if (n == 0 && !buffer.IsEmpty)
        {
            _open = false; // peer closed
        }

        return n;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!_open)
        {
            throw new InvalidOperationException("connection is closed");
        }

        var sent = 0;
        while (sent < data.Length)
        {
            var n = await _socket.SendAsync(data[sent..], SocketFlags.None, ct)
                .ConfigureAwait(false);
            if (n == 0) { _open = false; break; }
            sent += n;
        }
    }

    public ValueTask ShutdownAsync(CancellationToken ct)
    {
        if (!_open)
        {
            return ValueTask.CompletedTask;
        }

        _open = false;
        try { _socket.Shutdown(SocketShutdown.Both); }
        catch (SocketException ex) { SocketTcpConnectionLog.ShutdownFailed(_log, ex); }
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None);
        _socket.Dispose();
    }
}
