using Starling.Net.Tcp;

namespace Starling.Net.Tls;

internal sealed class TcpConnectionStream : Stream
{
    private readonly ITcpConnection _connection;

    public TcpConnectionStream(ITcpConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public override bool CanRead => _connection.IsOpen;
    public override bool CanSeek => false;
    public override bool CanWrite => _connection.IsOpen;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _connection.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        _connection.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        await _connection.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        await _connection.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
