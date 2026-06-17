// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text.Json;

namespace Starling.Ipc;

public static class IpcFraming
{
    public static IpcEnvelope CreateEnvelope<TPayload>(
        long messageId,
        string? sessionId,
        string kind,
        TPayload payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, IpcJson.Options);
        return new IpcEnvelope(IpcProtocol.Version, messageId, sessionId, kind, element);
    }

    public static TPayload ReadPayload<TPayload>(this IpcEnvelope envelope)
    {
        if (envelope.ProtocolVersion != IpcProtocol.Version)
        {
            throw new InvalidOperationException(
                $"Unsupported IPC protocol version {envelope.ProtocolVersion}.");
        }

        return envelope.Payload.Deserialize<TPayload>(IpcJson.Options)
            ?? throw new InvalidOperationException($"IPC payload for {envelope.Kind} was null.");
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJsonContext.Default.IpcEnvelope);
        if (bytes.Length > IpcProtocol.DefaultMaxMessageBytes)
        {
            throw new InvalidOperationException($"IPC message is too large: {bytes.Length} bytes.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<IpcEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var read = await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read != header.Length)
        {
            throw new EndOfStreamException("IPC stream ended in the middle of a message header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcProtocol.DefaultMaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid IPC message length: {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.IpcEnvelope)
            ?? throw new InvalidDataException("IPC message did not contain an envelope.");
    }

    private static async ValueTask<int> ReadExactlyOrEndAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC stream ended in the middle of a message payload.");
            }

            total += read;
        }
    }
}
