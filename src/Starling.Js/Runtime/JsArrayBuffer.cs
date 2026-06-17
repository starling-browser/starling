namespace Starling.Js.Runtime;

/// <summary>ECMA-262 §25.1 ArrayBuffer object with pluggable [[ArrayBufferData]].</summary>
public sealed class JsArrayBuffer : JsObject
{
    public JsArrayBuffer(JsObject? prototype, int byteLength) : base(prototype)
    {
        if (byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        Storage = new ManagedArrayBufferStorage(new byte[byteLength]);
        RefreshByteLength();
    }

    private JsArrayBuffer(JsObject? prototype, byte[] bytes) : base(prototype)
    {
        Storage = new ManagedArrayBufferStorage(bytes);
        RefreshByteLength();
    }

    private JsArrayBuffer(JsObject? prototype, IJsArrayBufferStorage storage) : base(prototype)
    {
        Storage = storage;
        RefreshByteLength();
    }

    private IJsArrayBufferStorage Storage { get; set; }

    public int ByteLength => Storage.ByteLength;

    public Span<byte> GetSpan() => Storage.GetSpan();

    public Span<byte> GetSpan(int start) => Storage.GetSpan()[start..];

    public Span<byte> GetSpan(int start, int length) => Storage.GetSpan().Slice(start, length);

    public static JsArrayBuffer Wrap(JsObject? prototype, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new JsArrayBuffer(prototype, bytes);
    }

    public static JsArrayBuffer Wrap(JsObject? prototype, IJsArrayBufferStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return new JsArrayBuffer(prototype, storage);
    }

    public void ReplaceBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Storage = new ManagedArrayBufferStorage(bytes);
        RefreshByteLength();
    }

    public void RefreshByteLength()
    {
        DefineOwnProperty("byteLength",
            PropertyDescriptor.Data(JsValue.Number(ByteLength), writable: false, enumerable: false, configurable: true));
    }

    public JsArrayBuffer Slice(JsObject? prototype, int begin, int end)
    {
        var len = Math.Max(end - begin, 0);
        var copy = new byte[len];
        GetSpan(begin, len).CopyTo(copy);
        return new JsArrayBuffer(prototype, copy);
    }

    public override string ToString() => "[object ArrayBuffer]";
}

public interface IJsArrayBufferStorage
{
    int ByteLength { get; }

    Span<byte> GetSpan();
}

internal sealed class ManagedArrayBufferStorage : IJsArrayBufferStorage
{
    private readonly byte[] _bytes;

    public ManagedArrayBufferStorage(byte[] bytes)
        => _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));

    public int ByteLength => _bytes.Length;

    public Span<byte> GetSpan() => _bytes;
}
