using System.Text;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§19.2 global functions used by browser scripts.</summary>
public static class Globals
{
    private const string UriReserved = ";/?:@&=+$,#";
    private const string UriUnescaped = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()";

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var evalFn = DefineGlobal(realm, "eval", (_, args) => PerformEval(realm, args), 1);
        // wp:M3-71 — record the genuine %eval% intrinsic so the VM's DirectEval
        // opcode can confirm the callee before applying caller-context semantics.
        realm.EvalFunction = evalFn;
        DefineGlobal(realm, "parseInt", (_, args) => NumberCtor.ParseInt(args), 2);
        DefineGlobal(realm, "parseFloat", (_, args) => NumberCtor.ParseFloat(args), 1);
        DefineGlobal(realm, "isNaN", (_, args) => JsValue.Boolean(double.IsNaN(NumberCtor.ToNumber(args.Length > 0 ? args[0] : JsValue.Undefined))), 1);
        DefineGlobal(realm, "isFinite", (_, args) => JsValue.Boolean(double.IsFinite(NumberCtor.ToNumber(args.Length > 0 ? args[0] : JsValue.Undefined))), 1);
        DefineGlobal(realm, "escape", (_, args) => JsValue.String(EscapeLegacy(args.Length > 0 ? args[0] : JsValue.Undefined)), 1);
        DefineGlobal(realm, "unescape", (_, args) => JsValue.String(UnescapeLegacy(args.Length > 0 ? args[0] : JsValue.Undefined)), 1);
        DefineGlobal(realm, "encodeURI", (_, args) => JsValue.String(Encode(args.Length > 0 ? args[0] : JsValue.Undefined, UriReserved, realm)), 1);
        DefineGlobal(realm, "encodeURIComponent", (_, args) => JsValue.String(Encode(args.Length > 0 ? args[0] : JsValue.Undefined, string.Empty, realm)), 1);
        DefineGlobal(realm, "decodeURI", (_, args) => JsValue.String(Decode(args.Length > 0 ? args[0] : JsValue.Undefined, preserveReserved: true, realm)), 1);
        DefineGlobal(realm, "decodeURIComponent", (_, args) => JsValue.String(Decode(args.Length > 0 ? args[0] : JsValue.Undefined, preserveReserved: false, realm)), 1);
    }

    /// <summary>§19.2.1 eval — global (indirect) eval. A non-String argument is
    /// returned unchanged (step 2). A String is parsed as a Script and evaluated
    /// in the global variable environment with <c>this</c> = global; its
    /// completion value is returned. A parse failure surfaces as a SyntaxError.
    /// NOTE: this implements indirect/global eval. Direct eval that reads the
    /// caller's local bindings is a follow-up (the global path still covers
    /// top-level direct eval and all indirect eval).</summary>
    internal static JsValue PerformEval(JsRealm realm, JsValue[] args)
    {
        var x = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!x.IsString)
        {
            return x;
        }

        return RunGlobalSource(realm, x.AsString, "<eval>");
    }

    /// <summary>Parse <paramref name="source"/> as a Script, compile it in
    /// global (eval) scope, and run it on the current realm's VM, returning the
    /// completion value. Shared by <c>eval</c> and the <c>Function</c>
    /// constructor. A parse error becomes a SyntaxError.</summary>
    internal static JsValue RunGlobalSource(JsRealm realm, string source, string name)
    {
        Chunk chunk;
        try
        {
            var program = new JsParser(source).ParseProgram();
            chunk = JsCompiler.CompileForEval(program, name);
        }
        catch (JsParseException ex)
        {
            throw new JsThrow(realm.NewSyntaxError(ex.Message));
        }
        // wp:M3-83 — cross-realm execution. The common single-realm path has an
        // ActiveVm published by the running JsVm. But when this realm's `eval`
        // (or a function closing over it) is invoked from ANOTHER realm's VM —
        // the $262.createRealm() case where host code holds `other.eval` and
        // calls it while the host realm's VM is the one on the native stack —
        // THIS realm has no running execution context. Recover the realm's own
        // primary VM via its owner runtime and publish it for the eval so the
        // body resolves against THIS realm's global environment (§9.6 / §19.2.1).
        if (realm.ActiveVm is { } active)
        {
            return active.RunEval(chunk);
        }

        if (realm.OwnerRuntime is { } owner)
        {
            return owner.WithActiveVm(vm => vm.RunEval(chunk));
        }

        throw new JsThrow(realm.NewTypeError("eval requires an active execution context"));
    }

    private static string EscapeLegacy(JsValue value)
    {
        var s = JsValue.ToStringValue(value);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (IsLegacyEscapeUnescaped(c))
            {
                sb.Append(c);
            }
            else if (c <= 0xFF)
            {
                sb.Append('%');
                sb.Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("%u");
                sb.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    private static string UnescapeLegacy(JsValue value)
    {
        var s = JsValue.ToStringValue(value);
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '%' && i + 5 < s.Length && s[i + 1] == 'u'
                && IsHex(s[i + 2]) && IsHex(s[i + 3])
                && IsHex(s[i + 4]) && IsHex(s[i + 5]))
            {
                var code = (HexValue(s[i + 2]) << 12)
                    | (HexValue(s[i + 3]) << 8)
                    | (HexValue(s[i + 4]) << 4)
                    | HexValue(s[i + 5]);
                sb.Append((char)code);
                i += 6;
                continue;
            }

            if (s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            {
                var code = (HexValue(s[i + 1]) << 4) | HexValue(s[i + 2]);
                sb.Append((char)code);
                i += 3;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>§19.2.6.4 Encode — percent-encode UTF-8 bytes, preserving encodeURI's reserved set.</summary>
    private static string Encode(JsValue value, string extraUnescaped, JsRealm realm)
    {
        var s = JsValue.ToStringValue(value);
        var sb = new StringBuilder();
        var utf8 = new UTF8Encoding(false, true);
        foreach (var rune in s.EnumerateRunes())
        {
            var text = rune.ToString();
            if (text.Length == 1 && (UriUnescaped.IndexOf(text[0], StringComparison.Ordinal) >= 0 || extraUnescaped.IndexOf(text[0], StringComparison.Ordinal) >= 0))
            {
                sb.Append(text[0]);
                continue;
            }
            byte[] bytes;
            try { bytes = utf8.GetBytes(text); }
            catch (EncoderFallbackException) { throw new JsThrow(realm.NewUriError("URI malformed")); }
            foreach (var b in bytes)
            {
                sb.Append('%');
                sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>§19.2.6.5 Decode — validate percent triplets and UTF-8, preserving reserved escapes for decodeURI.</summary>
    private static string Decode(JsValue value, bool preserveReserved, JsRealm realm)
    {
        var s = JsValue.ToStringValue(value);
        var sb = new StringBuilder();
        var utf8 = new UTF8Encoding(false, true);
        for (var i = 0; i < s.Length;)
        {
            if (s[i] != '%') { sb.Append(s[i++]); continue; }
            var start = i;
            var bytes = new List<byte>();
            while (i < s.Length && s[i] == '%')
            {
                if (i + 2 >= s.Length || !IsHex(s[i + 1]) || !IsHex(s[i + 2]))
                {
                    throw new JsThrow(realm.NewUriError("URI malformed"));
                }

                bytes.Add((byte)((HexValue(s[i + 1]) << 4) | HexValue(s[i + 2])));
                i += 3;
            }
            string decoded;
            try { decoded = utf8.GetString(bytes.ToArray()); }
            catch (DecoderFallbackException) { throw new JsThrow(realm.NewUriError("URI malformed")); }
            if (preserveReserved && decoded.Length == 1 && UriReserved.IndexOf(decoded[0], StringComparison.Ordinal) >= 0)
            {
                sb.Append(s, start, i - start);
            }
            else
            {
                sb.Append(decoded);
            }
        }
        return sb.ToString();
    }

    private static bool IsLegacyEscapeUnescaped(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '@' or '*' or '_' or '+' or '-' or '.' or '/';

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
    private static int HexValue(char c) => c <= '9' ? c - '0' : (c <= 'F' ? c - 'A' + 10 : c - 'a' + 10);

    private static JsNativeFunction DefineGlobal(JsRealm realm, string name, Func<JsValue, JsValue[], JsValue> body, int length)
    {
        // Realm-aware ctor wires [[Prototype]] = realm.FunctionPrototype and
        // stamps name + length so globals like parseInt/parseFloat inherit
        // call/apply/bind from Function.prototype.
        var fn = new JsNativeFunction(realm, name, length, body, isConstructor: false);
        realm.GlobalObject.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.Object(fn), true, false, true));
        return fn;
    }
}
