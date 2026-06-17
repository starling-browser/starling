using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Starling.Bindings.Jint;

/// <summary>
/// Core Web-API globals for the Jint backend that the canonical
/// <c>Starling.Bindings/CoreWebApiBinding.cs</c> exposes but Jint lacked:
/// <c>btoa</c>/<c>atob</c> (HTML §forgiving-base64) and <c>structuredClone</c>
/// (HTML structured-clone for the common JS value graph).
/// </summary>
internal static class CoreWebApiBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        if (!engine.Global.HasOwnProperty("btoa"))
        {
            JintInterop.DefineMethod(engine, engine.Global, "btoa", (_, a) =>
            {
                var s = a.Length > 0 ? TypeConverter.ToString(a[0]) : "";
                var bytes = new byte[s.Length];
                for (var i = 0; i < s.Length; i++)
                {
                    if (s[i] > 0xFF)
                    {
                        throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "String contains an invalid character");
                    }

                    bytes[i] = (byte)s[i];
                }
                return JintInterop.Str(Convert.ToBase64String(bytes));
            }, 1);
        }

        if (!engine.Global.HasOwnProperty("atob"))
        {
            JintInterop.DefineMethod(engine, engine.Global, "atob", (_, a) =>
            {
                var s = RemoveAsciiWhitespace(a.Length > 0 ? TypeConverter.ToString(a[0]) : "");
                if (s.Length % 4 == 1)
                {
                    throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "The string to be decoded is not correctly encoded");
                }

                if (s.Length % 4 != 0)
                {
                    s = s.PadRight(s.Length + (4 - s.Length % 4), '=');
                }

                try
                {
                    var bytes = Convert.FromBase64String(s);
                    return JintInterop.Str(string.Create(bytes.Length, bytes, static (span, st) =>
                    {
                        for (var i = 0; i < st.Length; i++)
                        {
                            span[i] = (char)st[i];
                        }
                    }));
                }
                catch (FormatException)
                {
                    throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "The string to be decoded is not correctly encoded");
                }
            }, 1);
        }

        if (!engine.Global.HasOwnProperty("structuredClone"))
        {
            JintInterop.DefineMethod(engine, engine.Global, "structuredClone", (_, a) =>
            {
                var seen = new Dictionary<ObjectInstance, JsValue>(ReferenceEqualityComparer.Instance);
                return CloneValue(ctx, a.Length > 0 ? a[0] : JsValue.Undefined, seen);
            }, 1);
        }
    }

    private static JsValue CloneValue(JintBackendContext ctx, JsValue value, Dictionary<ObjectInstance, JsValue> seen)
    {
        var engine = ctx.Engine;
        if (value is not ObjectInstance obj)
        {
            if (value is JsSymbol)
            {
                throw DomExceptionBinding.Throw(ctx, "DataCloneError", "Symbol values cannot be cloned");
            }

            return value;
        }
        if (seen.TryGetValue(obj, out var existing))
        {
            return existing;
        }

        if (value.IsArrayBuffer() && value.AsArrayBuffer() is { } ab)
        {
            return engine.Intrinsics.ArrayBuffer.Construct((byte[])ab.Clone());
        }

        if (value is JsTypedArray ta)
        {
            return CloneTypedArray(ctx, ta);
        }

        if (value.IsCallable())
        {
            throw DomExceptionBinding.Throw(ctx, "DataCloneError", "Function objects cannot be cloned");
        }

        if (value is JsArray arr)
        {
            var c = new JsArray(engine, (uint)arr.Length);
            seen[obj] = c;
            for (uint i = 0; i < arr.Length; i++)
            {
                c[(int)i] = CloneValue(ctx, arr[(int)i], seen);
            }

            return c;
        }

        var clone = new JsObject(engine);
        seen[obj] = clone;
        foreach (var key in EnumerableStringKeys(obj))
        {
            clone.FastSetProperty(key, new global::Jint.Runtime.Descriptors.PropertyDescriptor(
                CloneValue(ctx, obj.Get(key), seen), writable: true, enumerable: true, configurable: true));
        }

        return clone;
    }

    private static ObjectInstance CloneTypedArray(JintBackendContext ctx, JsTypedArray ta)
    {
        var engine = ctx.Engine;
        // Copy the underlying bytes (honoring offset/length), rebuild the same kind.
        var bytes = ExtractBytes(ta);
        var buffer = engine.Intrinsics.ArrayBuffer.Construct(bytes);
        // Rebuild the same typed-array kind over the copied buffer.
        return engine.Construct(TypedArrayName(ta), buffer);
    }

    private static byte[] ExtractBytes(JsValue v)
    {
        if (v.IsArrayBuffer() && v.AsArrayBuffer() is { } ab)
        {
            return (byte[])ab.Clone();
        }

        if (v is ObjectInstance oi)
        {
            var bufVal = oi.Get("buffer");
            if (bufVal.IsArrayBuffer() && bufVal.AsArrayBuffer() is { } backing)
            {
                var offset = oi.Get("byteOffset").IsNumber() ? (int)oi.Get("byteOffset").AsNumber() : 0;
                var length = oi.Get("byteLength").IsNumber() ? (int)oi.Get("byteLength").AsNumber() : backing.Length;
                if (offset >= 0 && length >= 0 && offset + length <= backing.Length)
                {
                    var slice = new byte[length];
                    Array.Copy(backing, offset, slice, 0, length);
                    return slice;
                }
            }
        }
        return Array.Empty<byte>();
    }

    private static string TypedArrayName(JsTypedArray ta)
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
        return "Uint8Array";
    }

    private static IEnumerable<string> EnumerableStringKeys(ObjectInstance o)
    {
        foreach (var key in o.GetOwnPropertyKeys(Types.String))
        {
            if (!key.IsString())
            {
                continue;
            }

            var d = o.GetOwnProperty(key);
            if (d != global::Jint.Runtime.Descriptors.PropertyDescriptor.Undefined && d.Enumerable)
            {
                yield return key.AsString();
            }
        }
    }

    private static string RemoveAsciiWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is not (' ' or '\t' or '\n' or '\r' or '\f'))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
