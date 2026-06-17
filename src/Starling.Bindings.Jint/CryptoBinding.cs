using System.Globalization;
using System.Security.Cryptography;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Starling.Bindings.Jint;

/// <summary>
/// Web Crypto API §10.1 on the Jint backend.
/// Mirrors <c>Starling.Bindings/CryptoBinding.cs</c>.
/// </summary>
/// <remarks>
/// Surface: <c>crypto.getRandomValues(typedArray)</c> (integer typed arrays
/// only, ≤ 65536 bytes) and <c>crypto.randomUUID()</c> (RFC 4122 v4).
/// SubtleCrypto / Web Crypto §11 is not implemented — its async primitives
/// don't have a designed seam yet on either backend.
/// </remarks>
internal static class CryptoBinding
{
    private const uint MaxBytes = 65536;

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        var crypto = new JsObject(engine);
        JintInterop.DefineMethod(engine, crypto, "getRandomValues",
            (_, args) => GetRandomValues(engine, args), length: 1);
        JintInterop.DefineMethod(engine, crypto, "randomUUID",
            (_, _) => JintInterop.Str(GenerateRandomUuid()), length: 0);

        JintInterop.DefineDataProp(engine.Global, "crypto", crypto,
            writable: true, enumerable: true, configurable: true);
    }

    private static JsTypedArray GetRandomValues(Engine engine, JsValue[] args)
    {
        if (args.Length == 0 || args[0] is not JsTypedArray ta)
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError,
                "getRandomValues requires a TypedArray argument");
        }

        var ctorName = TypedArrayConstructorName(ta);
        if (ctorName.StartsWith("Float", StringComparison.Ordinal))
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError,
                $"getRandomValues: {ctorName} is not an integer typed array");
        }

        if (ctorName.StartsWith("BigInt", StringComparison.Ordinal) ||
            ctorName.StartsWith("BigUint", StringComparison.Ordinal))
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError,
                $"getRandomValues: {ctorName} is not supported");
        }

        var byteLengthVal = ta.Get("byteLength");
        var byteLength = byteLengthVal.IsNumber()
            ? (uint)TypeConverter.ToNumber(byteLengthVal)
            : 0u;
        if (byteLength > MaxBytes)
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError,
                $"getRandomValues: byte length {byteLength} exceeds the {MaxBytes}-byte quota");
        }

        // Fill via the typed array's own set semantics: generate a 32-bit
        // random value per element; Jint coerces to the array's element width.
        Span<byte> scratch = stackalloc byte[4];
        var len = ta.Length;
        for (uint i = 0; i < len; i++)
        {
            RandomNumberGenerator.Fill(scratch);
            var u = (uint)(scratch[0] | (scratch[1] << 8) | (scratch[2] << 16) | (scratch[3] << 24));
            ta[i] = JsNumber.Create(u);
        }
        return ta;
    }

    private static string TypedArrayConstructorName(JsTypedArray ta)
    {
        var ctor = ta.Get("constructor");
        if (ctor is ObjectInstance oi)
        {
            var name = oi.Get("name");
            if (name.IsString())
            {
                return name.ToString();
            }
        }
        return "TypedArray";
    }

    private static string GenerateRandomUuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return $"{Hex(bytes[0])}{Hex(bytes[1])}{Hex(bytes[2])}{Hex(bytes[3])}-" +
               $"{Hex(bytes[4])}{Hex(bytes[5])}-" +
               $"{Hex(bytes[6])}{Hex(bytes[7])}-" +
               $"{Hex(bytes[8])}{Hex(bytes[9])}-" +
               $"{Hex(bytes[10])}{Hex(bytes[11])}{Hex(bytes[12])}{Hex(bytes[13])}{Hex(bytes[14])}{Hex(bytes[15])}";
    }

    private static string Hex(byte b) => b.ToString("x2", CultureInfo.InvariantCulture);
}
