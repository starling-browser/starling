using System.Security.Cryptography;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// M3-31 — Web Crypto minimal surface: installs <c>crypto</c> (and aliases
/// <c>globalThis.crypto</c> / <c>self.crypto</c>) on the realm global.
/// </summary>
/// <remarks>
/// <para><b>Implemented:</b></para>
/// <list type="bullet">
///   <item><c>crypto.getRandomValues(typedArray)</c> — fills an integer
///   TypedArray with cryptographically-random bytes in place and returns the
///   same object. Rejects Float32/Float64 (TypeError) and arrays whose
///   byteLength exceeds 65536 (QuotaExceededError). Backed by
///   <see cref="RandomNumberGenerator.Fill"/>.</item>
///   <item><c>crypto.randomUUID()</c> — returns an RFC 4122 v4 UUID string
///   of the form <c>xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx</c>.</item>
/// </list>
/// <para><b>Not implemented (out of scope):</b></para>
/// <list type="bullet">
///   <item><c>crypto.subtle</c> — the SubtleCrypto subsystem (digest, sign,
///   encrypt, key derivation) is a large independent surface. The property
///   is left undefined; code that feature-detects it before use degrades
///   gracefully.</item>
/// </list>
/// </remarks>
public static class CryptoBinding
{
    /// <summary>Install <c>crypto</c> on the realm's global object.
    /// Idempotent — a second call is a no-op if the property already
    /// exists.</summary>
    public static void Install(JsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var realm = runtime.Realm;
        if (realm.GlobalObject.HasOwn("crypto"))
        {
            return;
        }

        var crypto = new JsObject(realm.ObjectPrototype);

        // getRandomValues(typedArray) — Web Crypto §10.1.2
        EventTargetBinding.DefineMethod(realm, crypto, "getRandomValues",
            (_, args) => GetRandomValues(realm, args), length: 1);

        // randomUUID() — Web Crypto §10.1.4
        EventTargetBinding.DefineMethod(realm, crypto, "randomUUID",
            (_, _) => JsValue.String(GenerateRandomUuid()), length: 0);

        // crypto.subtle is intentionally left undefined (out of scope).

        var cryptoValue = JsValue.Object(crypto);

        // Install on global as a configurable/writable data property, matching
        // the window.crypto spec profile.
        realm.GlobalObject.DefineOwnProperty("crypto",
            PropertyDescriptor.Data(cryptoValue, writable: true, enumerable: true, configurable: true));
    }

    // Web Crypto §10.1.2 — getRandomValues
    private static JsValue GetRandomValues(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsObject || args[0].AsObject is not JsTypedArray ta)
        {
            throw new JsThrow(realm.NewTypeError("getRandomValues requires a TypedArray argument"));
        }

        // Float32Array / Float64Array are not integer typed arrays — reject per spec.
        if (ta.Kind is JsTypedArrayKind.Float32 or JsTypedArrayKind.Float64)
        {
            throw new JsThrow(realm.NewTypeError(
                $"getRandomValues: {ta.ConstructorName} is not an integer typed array"));
        }

        // Spec §10.1.2 step 2: if byteLength > 65536 throw QuotaExceededError.
        // We model this as a RangeError (closest JS built-in) with the right
        // message; real DOMException is a larger surface not yet implemented.
        if (ta.ByteLength > 65536)
        {
            throw new JsThrow(realm.NewRangeError(
                $"getRandomValues: byte length {ta.ByteLength} exceeds the 65536-byte quota"));
        }

        // Fill the underlying ArrayBuffer bytes that back this view.
        var span = ta.Buffer.GetSpan(ta.ByteOffset, ta.ByteLength);
        RandomNumberGenerator.Fill(span);

        // Return the same TypedArray object (identity).
        return args[0];
    }

    // Web Crypto §10.1.4 — randomUUID
    private static string GenerateRandomUuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        // Set version 4: top nibble of byte[6] = 0100
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        // Set variant bits: top two bits of byte[8] = 10
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return $"{Hex(bytes[0])}{Hex(bytes[1])}{Hex(bytes[2])}{Hex(bytes[3])}-" +
               $"{Hex(bytes[4])}{Hex(bytes[5])}-" +
               $"{Hex(bytes[6])}{Hex(bytes[7])}-" +
               $"{Hex(bytes[8])}{Hex(bytes[9])}-" +
               $"{Hex(bytes[10])}{Hex(bytes[11])}{Hex(bytes[12])}{Hex(bytes[13])}{Hex(bytes[14])}{Hex(bytes[15])}";
    }

    private static string Hex(byte b) => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
}
