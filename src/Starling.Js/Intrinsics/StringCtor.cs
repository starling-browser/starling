using System.Globalization;
using System.Text;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §22.1 The String constructor and §22.1.3 String.prototype. Strings are
/// represented as .NET UTF-16 strings, matching ECMAScript's code-unit model.
/// Methods that accept regular expressions delegate through the standard
/// <c>Symbol.match</c>, <c>Symbol.replace</c>, <c>Symbol.search</c>, and
/// <c>Symbol.split</c> hooks when present.
/// </summary>
public static class StringCtor
{
    private const int MaxRepeatLength = 1_000_000;

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var stringProto = realm.StringPrototype;

        var ctor = new JsNativeFunction(realm, "String", length: 1, (newTarget, args) =>
        {
            // §22.1.1.1 String(value): no argument returns the empty string;
            // otherwise route through §7.1.17 ToString. Use the AO variant so
            // an object's toString/valueOf (e.g. Error.prototype.toString) is
            // invoked instead of the flat "[object Object]" fallback. Note:
            // String(symbol) is the one allowed Symbol→string path, so handle
            // it before ToString (which rejects Symbols per step 2).
            // §22.1.1.1 step 4: when called as a function, return the primitive.
            var constructed = IntrinsicHelpers.IsConstructInvocation(newTarget);
            string text;
            if (args.Length == 0)
            {
                text = string.Empty;
            }
            else if (!constructed && args[0].IsSymbol)
            {
                text = args[0].AsSymbol.DescriptiveString;
            }
            else
            {
                text = AbstractOperations.ToStringJs(realm.ActiveVm, args[0]);
            }

            if (constructed)
            {
                // §22.1.1.1 step 5: StringCreate with OrdinaryCreateFromConstructor —
                // prototype comes from new.target so `class S extends String {}` works.
                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, stringProto);
                return JsValue.Object(CreateStringObject(realm, text, instProto));
            }
            return JsValue.String(text);
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(stringProto), writable: false, enumerable: false, configurable: false));

        IntrinsicHelpers.DefineMethod(realm, ctor, "fromCharCode", 1, (_, args) => FromCharCode(args));
        IntrinsicHelpers.DefineMethod(realm, ctor, "fromCodePoint", 1, (_, args) => FromCodePoint(realm, args));
        IntrinsicHelpers.DefineMethod(realm, ctor, "raw", 1, (_, args) => Raw(realm, args));

        // Bulk-install constructor + every string-keyed prototype method by
        // adopting one precomputed shape. Order is unchanged from the prior
        // sequential install, so getOwnPropertyNames order is identical and the
        // result is byte-identical. The symbol-keyed @@iterator is installed
        // separately below (symbols can never enter a shape).
        IntrinsicHelpers.BulkInstallBuiltins(realm, stringProto, new[]
        {
            new IntrinsicHelpers.BulkMember("constructor", 0, null, JsValue.Object(ctor)),
            new IntrinsicHelpers.BulkMember("at", 1, (thisV, args) => At(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("charAt", 1, (thisV, args) => CharAt(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("charCodeAt", 1, (thisV, args) => CharCodeAt(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("codePointAt", 1, (thisV, args) => CodePointAt(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("concat", 1, (thisV, args) => Concat(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("endsWith", 1, (thisV, args) => EndsWith(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("includes", 1, (thisV, args) => Includes(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("indexOf", 1, (thisV, args) => IndexOf(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("lastIndexOf", 1, (thisV, args) => LastIndexOf(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("localeCompare", 1, (thisV, args) => LocaleCompare(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("normalize", 0, (thisV, args) => Normalize(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("padEnd", 1, (thisV, args) => Pad(realm, thisV, args, atStart: false)),
            new IntrinsicHelpers.BulkMember("padStart", 1, (thisV, args) => Pad(realm, thisV, args, atStart: true)),
            new IntrinsicHelpers.BulkMember("repeat", 1, (thisV, args) => Repeat(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("match", 1, (thisV, args) => Match(realm, thisV, args, all: false)),
            new IntrinsicHelpers.BulkMember("matchAll", 1, (thisV, args) => Match(realm, thisV, args, all: true)),
            new IntrinsicHelpers.BulkMember("search", 1, (thisV, args) => Search(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("replace", 2, (thisV, args) => Replace(realm, thisV, args, replaceAll: false)),
            new IntrinsicHelpers.BulkMember("replaceAll", 2, (thisV, args) => Replace(realm, thisV, args, replaceAll: true)),
            new IntrinsicHelpers.BulkMember("slice", 2, (thisV, args) => Slice(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("split", 2, (thisV, args) => Split(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("startsWith", 1, (thisV, args) => StartsWith(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("substring", 2, (thisV, args) => Substring(realm, thisV, args)),
            // Annex B §B.2.2.1 String.prototype.substr(start[, length]) — legacy but widely used.
            new IntrinsicHelpers.BulkMember("substr", 2, (thisV, args) => Substr(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("toLowerCase", 0, (thisV, args) => JsValue.String(ThisStringValue(realm, thisV).ToLowerInvariant())),
            new IntrinsicHelpers.BulkMember("toUpperCase", 0, (thisV, args) => JsValue.String(ThisStringValue(realm, thisV).ToUpperInvariant())),
            new IntrinsicHelpers.BulkMember("toLocaleLowerCase", 0, (thisV, args) => JsValue.String(ThisStringValue(realm, thisV).ToLower(CultureInfo.InvariantCulture))),
            new IntrinsicHelpers.BulkMember("toLocaleUpperCase", 0, (thisV, args) => JsValue.String(ThisStringValue(realm, thisV).ToUpper(CultureInfo.InvariantCulture))),
            new IntrinsicHelpers.BulkMember("trim", 0, (thisV, args) => JsValue.String(TrimJs(ThisStringValue(realm, thisV), trimStart: true, trimEnd: true))),
            new IntrinsicHelpers.BulkMember("trimStart", 0, (thisV, args) => JsValue.String(TrimJs(ThisStringValue(realm, thisV), trimStart: true, trimEnd: false))),
            new IntrinsicHelpers.BulkMember("trimEnd", 0, (thisV, args) => JsValue.String(TrimJs(ThisStringValue(realm, thisV), trimStart: false, trimEnd: true))),
            new IntrinsicHelpers.BulkMember("toString", 0, (thisV, args) => JsValue.String(ThisStringValue(realm, thisV))),
            new IntrinsicHelpers.BulkMember("valueOf", 0, (thisV, args) => JsValue.String(ThisStringValue(realm, thisV))),
        });
        stringProto.DefineOwnProperty("trimLeft",
            PropertyDescriptor.BuiltinMethod(stringProto.Get("trimStart")));
        stringProto.DefineOwnProperty("trimRight",
            PropertyDescriptor.BuiltinMethod(stringProto.Get("trimEnd")));

        // §22.1.3.34 String.prototype[@@iterator] — walks the string by
        // Unicode code points (not UTF-16 code units), e.g.
        // [..."😀ab"].length === 3.
        var stringIterator = new JsNativeFunction(realm, "[Symbol.iterator]", 0, (thisV, _) =>
        {
            var s = ThisStringValue(realm, thisV);
            return IteratorIntrinsics.CreateStringIterator(realm, s);
        }, isConstructor: false);
        stringProto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(stringIterator)));

        realm.StringConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("String",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsStringObject CreateStringObject(JsRealm realm, string text, JsObject? proto = null)
        => new(proto ?? realm.StringPrototype, text);

    private static string ThisStringValue(JsRealm realm, JsValue thisV)
    {
        // §22.1.3.4 thisStringValue: accept primitive strings and String exotic
        // wrappers; otherwise RequireObjectCoercible then ToString.
        if (thisV.IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("String.prototype method called on null or undefined"));
        }

        if (thisV.IsString)
        {
            return thisV.AsString;
        }

        if (thisV.IsObject)
        {
            if (thisV.AsObject is JsStringObject jso)
            {
                return jso.Text;
            }

            return JsValue.ToStringValue(AbstractOperations.ToPrimitive(thisV, "string"));
        }
        return JsValue.ToStringValue(thisV);
    }

    private static JsValue FromCharCode(JsValue[] args)
    {
        var chars = new char[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            chars[i] = (char)ToUint16(args[i]);
        }

        return JsValue.String(new string(chars));
    }

    private static JsValue FromCodePoint(JsRealm realm, JsValue[] args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            var n = JsValue.ToNumber(arg);
            if (!IsIntegral(n) || n < 0 || n > 0x10FFFF)
            {
                throw new JsThrow(realm.NewRangeError("Invalid code point"));
            }

            sb.Append(char.ConvertFromUtf32((int)n));
        }
        return JsValue.String(sb.ToString());
    }

    private static JsValue Raw(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0)
        {
            throw new JsThrow(realm.NewTypeError("String.raw requires a template object"));
        }

        var cooked = AbstractOperations.ToObject(realm, args[0]);
        var raw = AbstractOperations.ToObject(realm, cooked.Get("raw"));
        var literalSegments = ToLength(raw.Get("length"));
        if (literalSegments == 0)
        {
            return JsValue.String(string.Empty);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < literalSegments; i++)
        {
            sb.Append(JsValue.ToStringValue(raw.Get(i.ToString(CultureInfo.InvariantCulture))));
            if (i + 1 < literalSegments)
            {
                sb.Append(i + 1 < args.Length ? JsValue.ToStringValue(args[i + 1]) : string.Empty);
            }
        }
        return JsValue.String(sb.ToString());
    }

    private static JsValue At(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var i = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        var k = i >= 0 ? i : s.Length + i;
        return k < 0 || k >= s.Length ? JsValue.Undefined : JsValue.String(s[(int)k].ToString());
    }

    private static JsValue CharAt(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var pos = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        return pos < 0 || pos >= s.Length ? JsValue.String(string.Empty) : JsValue.String(s[(int)pos].ToString());
    }

    private static JsValue CharCodeAt(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var pos = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        return pos < 0 || pos >= s.Length ? JsValue.NaN : JsValue.Number(s[(int)pos]);
    }

    private static JsValue CodePointAt(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var pos = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.Undefined;
        }

        var first = s[(int)pos];
        if (char.IsHighSurrogate(first) && pos + 1 < s.Length && char.IsLowSurrogate(s[(int)pos + 1]))
        {
            return JsValue.Number(char.ConvertToUtf32(first, s[(int)pos + 1]));
        }

        return JsValue.Number(first);
    }

    private static JsValue Concat(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var sb = new StringBuilder(ThisStringValue(realm, thisV));
        foreach (var arg in args)
        {
            sb.Append(JsValue.ToStringValue(arg));
        }

        return JsValue.String(sb.ToString());
    }

    private static JsValue EndsWith(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var search = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var end = args.Length > 1 && !args[1].IsUndefined ? Clamp(ToIntegerOrInfinity(args[1]), 0, s.Length) : s.Length;
        return JsValue.Boolean(s[..(int)end].EndsWith(search, StringComparison.Ordinal));
    }

    private static JsValue Includes(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var search = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var pos = Clamp(ToIntegerOrInfinity(args.Length > 1 ? args[1] : JsValue.Undefined), 0, s.Length);
        return JsValue.Boolean(s.IndexOf(search, (int)pos, StringComparison.Ordinal) >= 0);
    }

    private static JsValue IndexOf(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var search = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var pos = Clamp(ToIntegerOrInfinity(args.Length > 1 ? args[1] : JsValue.Undefined), 0, s.Length);
        return JsValue.Number(s.IndexOf(search, (int)pos, StringComparison.Ordinal));
    }

    private static JsValue LastIndexOf(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var search = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var raw = args.Length > 1 ? JsValue.ToNumber(args[1]) : double.NaN;
        var pos = double.IsNaN(raw) ? s.Length : Clamp(ToIntegerOrInfinity(args[1]), 0, s.Length);
        if (search.Length == 0)
        {
            return JsValue.Number(pos);
        }

        var start = Math.Min((int)pos + search.Length - 1, s.Length - 1);
        return JsValue.Number(start < 0 ? -1 : s.LastIndexOf(search, start, StringComparison.Ordinal));
    }

    private static JsValue LocaleCompare(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var that = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        return JsValue.Number(string.Compare(s, that, CultureInfo.InvariantCulture, CompareOptions.None));
    }

    private static JsValue Normalize(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var formName = args.Length == 0 || args[0].IsUndefined ? "NFC" : JsValue.ToStringValue(args[0]);
        var form = formName switch
        {
            "NFC" => NormalizationForm.FormC,
            "NFD" => NormalizationForm.FormD,
            "NFKC" => NormalizationForm.FormKC,
            "NFKD" => NormalizationForm.FormKD,
            _ => throw new JsThrow(realm.NewRangeError("Invalid normalization form")),
        };
        return JsValue.String(s.Normalize(form));
    }

    private static JsValue Pad(JsRealm realm, JsValue thisV, JsValue[] args, bool atStart)
    {
        var s = ThisStringValue(realm, thisV);
        var maxLength = ToLength(args.Length > 0 ? args[0] : JsValue.Undefined);
        if (maxLength <= s.Length)
        {
            return JsValue.String(s);
        }

        var fill = args.Length > 1 && !args[1].IsUndefined ? JsValue.ToStringValue(args[1]) : " ";
        if (fill.Length == 0)
        {
            return JsValue.String(s);
        }

        var needed = (int)Math.Min(maxLength - s.Length, MaxRepeatLength);
        var filler = RepeatToLength(fill, needed);
        return JsValue.String(atStart ? filler + s : s + filler);
    }

    private static JsValue Repeat(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var n = JsValue.ToNumber(args.Length > 0 ? args[0] : JsValue.Undefined);
        var count = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        if (double.IsInfinity(n) || count < 0)
        {
            throw new JsThrow(realm.NewRangeError("Invalid repeat count"));
        }

        if (s.Length != 0 && count > MaxRepeatLength / s.Length)
        {
            throw new JsThrow(realm.NewRangeError("Repeat count too large"));
        }

        var sb = new StringBuilder(s.Length * (int)count);
        for (var i = 0; i < count; i++)
        {
            sb.Append(s);
        }

        return JsValue.String(sb.ToString());
    }

    private static JsValue Replace(JsRealm realm, JsValue thisV, JsValue[] args, bool replaceAll)
    {
        var s = ThisStringValue(realm, thisV);
        // §22.1.3.19/§22.1.3.20: if searchValue is not undefined/null, let
        // replacer be GetMethod(searchValue, @@replace) and, if not undefined,
        // delegate to it. This is NOT RegExp-specific — any object exposing a
        // callable [Symbol.replace] participates (core-js feature-detects this).
        if (args.Length > 0 && args[0].IsObject)
        {
            // replaceAll's non-global guard is a RegExp-specific invariant
            // (§22.1.3.20 step 2): a RegExp without the global flag throws.
            if (replaceAll && RegExpCtor.IsRegExp(args[0])
                && (((JsRegExp)args[0].AsObject).Flags & Starling.RegExp.RegexFlags.Global) == 0)
            {
                throw new JsThrow(realm.NewTypeError("String.prototype.replaceAll called with a non-global RegExp"));
            }

            var replaceFn = args[0].AsObject.Get(SymbolCtor.Replace);
            if (args[0].AsObject is JsRegExp re && replaceFn.IsObject
                && ReferenceEquals(replaceFn.AsObject, realm.RegExpBuiltinSymbolReplace))
            {
                return RegExpCtor.ReplaceString(realm, re, s, args.Length > 1 ? args[1] : JsValue.Undefined);
            }

            if (AbstractOperations.IsCallable(replaceFn))
            {
                return AbstractOperations.Call(realm.ActiveVm, replaceFn, args[0],
                    new[] { JsValue.String(s), args.Length > 1 ? args[1] : JsValue.Undefined });
            }
        }
        var search = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var replacement = args.Length > 1 ? args[1] : JsValue.Undefined;
        if (!replaceAll)
        {
            var pos = s.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0)
            {
                return JsValue.String(s);
            }

            return JsValue.String(s[..pos] + ReplacementText(realm, replacement, search, pos, s) + s[(pos + search.Length)..]);
        }

        if (search.Length == 0)
        {
            var sbEmpty = new StringBuilder();
            for (var i = 0; i <= s.Length; i++)
            {
                sbEmpty.Append(ReplacementText(realm, replacement, search, i, s));
                if (i < s.Length)
                {
                    sbEmpty.Append(s[i]);
                }
            }
            return JsValue.String(sbEmpty.ToString());
        }

        var sb = new StringBuilder();
        var cursor = 0;
        while (cursor <= s.Length)
        {
            var pos = s.IndexOf(search, cursor, StringComparison.Ordinal);
            if (pos < 0)
            {
                break;
            }

            sb.Append(s, cursor, pos - cursor);
            sb.Append(ReplacementText(realm, replacement, search, pos, s));
            cursor = pos + search.Length;
        }
        sb.Append(s, cursor, s.Length - cursor);
        return JsValue.String(sb.ToString());
    }

    private static string ReplacementText(JsRealm realm, JsValue replacement, string matched, int position, string whole)
    {
        if (AbstractOperations.IsCallable(replacement))
        {
            var result = AbstractOperations.Call(realm.ActiveVm, replacement, JsValue.Undefined,
                new[] { JsValue.String(matched), JsValue.Number(position), JsValue.String(whole) });
            return JsValue.ToStringValue(result);
        }
        return GetSubstitution(JsValue.ToStringValue(replacement), matched, position, whole);
    }

    private static string GetSubstitution(string replacement, string matched, int position, string whole)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] != '$' || i + 1 >= replacement.Length)
            {
                sb.Append(replacement[i]);
                continue;
            }
            var next = replacement[++i];
            switch (next)
            {
                case '$': sb.Append('$'); break;
                case '&': sb.Append(matched); break;
                case '`': sb.Append(whole[..position]); break;
                case '\'': sb.Append(whole[(position + matched.Length)..]); break;
                default:
                    sb.Append('$').Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    private static JsValue Slice(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var len = s.Length;
        var start = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        var from = start < 0 ? Math.Max(len + start, 0) : Math.Min(start, len);
        var end = args.Length > 1 && !args[1].IsUndefined ? ToIntegerOrInfinity(args[1]) : len;
        var to = end < 0 ? Math.Max(len + end, 0) : Math.Min(end, len);
        return JsValue.String(from >= to ? string.Empty : s.Substring((int)from, (int)(to - from)));
    }

    private static JsValue Match(JsRealm realm, JsValue thisV, JsValue[] args, bool all)
    {
        // §22.1.3.13 String.prototype.match / §22.1.3.14 matchAll: if the
        // argument is neither undefined nor null, delegate to its
        // [Symbol.match] / [Symbol.matchAll] method when present — the argument
        // need not be a genuine RegExp. (Only this delegation makes core-js's
        // DELEGATES_TO_SYMBOL feature-detect pass; without it core-js wraps the
        // native and recurses.)
        var regexp = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!regexp.IsNullish)
        {
            var symbol = all ? SymbolCtor.MatchAll : SymbolCtor.Match;
            var matcher = AbstractOperations.GetMethod(realm.ActiveVm, regexp, symbol);
            if (!matcher.IsUndefined)
            {
                var sArg = ThisStringValue(realm, thisV);
                return AbstractOperations.Call(realm.ActiveVm, matcher, regexp,
                    new[] { JsValue.String(sArg) });
            }
        }

        var s = ThisStringValue(realm, thisV);
        var re = args.Length > 0 && RegExpCtor.IsRegExp(args[0])
            ? (JsRegExp)args[0].AsObject
            : RegExpCtor.Create(realm, args.Length > 0 && !args[0].IsUndefined ? JsValue.ToStringValue(args[0]) : "",
                all ? "g" : "");
        if (all)
        {
            // Spec §22.1.3.13 requires matchAll to throw TypeError if the
            // regex is not global. Mirror that here for parity with the
            // Symbol.matchAll path before returning the RegExp string iterator.
            if ((re.Flags & Starling.RegExp.RegexFlags.Global) == 0)
            {
                throw new JsThrow(realm.NewTypeError("matchAll requires a global regular expression"));
            }

            var unicode = (re.Flags & Starling.RegExp.RegexFlags.Unicode) != 0;
            return JsValue.Object(new JsRegExpStringIterator(realm, re, s, global: true, unicode: unicode));
        }
        var fn = re.Get(SymbolCtor.Match);
        if (!fn.IsObject)
        {
            return JsValue.Null;
        }

        return AbstractOperations.Call(realm.ActiveVm, fn, JsValue.Object(re), new[] { JsValue.String(s) });
    }

    private static JsValue Search(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var re = args.Length > 0 && RegExpCtor.IsRegExp(args[0])
            ? (JsRegExp)args[0].AsObject
            : RegExpCtor.Create(realm, args.Length > 0 && !args[0].IsUndefined ? JsValue.ToStringValue(args[0]) : "", "");
        var fn = re.Get(SymbolCtor.Search);
        if (!fn.IsObject)
        {
            return JsValue.Number(-1);
        }

        return AbstractOperations.Call(realm.ActiveVm, fn, JsValue.Object(re), new[] { JsValue.String(s) });
    }

    private static JsValue Split(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        // RegExp path
        if (args.Length > 0 && RegExpCtor.IsRegExp(args[0]))
        {
            var re = (JsRegExp)args[0].AsObject;
            var fn = re.Get(SymbolCtor.Split);
            if (fn.IsObject)
            {
                return AbstractOperations.Call(realm.ActiveVm, fn, args[0],
                    new[] { JsValue.String(s), args.Length > 1 ? args[1] : JsValue.Undefined });
            }
        }
        var limit = args.Length > 1 && !args[1].IsUndefined ? ToUint32(args[1]) : uint.MaxValue;
        var result = new List<JsValue>();
        if (limit == 0)
        {
            return MakeArrayLike(realm, result);
        }

        if (args.Length == 0 || args[0].IsUndefined)
        {
            result.Add(JsValue.String(s));
            return MakeArrayLike(realm, result);
        }
        var sep = JsValue.ToStringValue(args[0]);
        if (sep.Length == 0)
        {
            for (var i = 0; i < s.Length && result.Count < limit; i++)
            {
                result.Add(JsValue.String(s[i].ToString()));
            }

            return MakeArrayLike(realm, result);
        }
        var cursor = 0;
        while (result.Count < limit)
        {
            var pos = s.IndexOf(sep, cursor, StringComparison.Ordinal);
            if (pos < 0)
            {
                break;
            }

            result.Add(JsValue.String(s[cursor..pos]));
            cursor = pos + sep.Length;
        }
        if (result.Count < limit)
        {
            result.Add(JsValue.String(s[cursor..]));
        }

        return MakeArrayLike(realm, result);
    }

    private static JsValue StartsWith(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var search = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var pos = Clamp(ToIntegerOrInfinity(args.Length > 1 ? args[1] : JsValue.Undefined), 0, s.Length);
        return JsValue.Boolean(s[(int)pos..].StartsWith(search, StringComparison.Ordinal));
    }

    private static JsValue Substring(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var start = Clamp(ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined), 0, s.Length);
        var end = args.Length > 1 && !args[1].IsUndefined
            ? Clamp(ToIntegerOrInfinity(args[1]), 0, s.Length)
            : s.Length;
        if (start > end)
        {
            (start, end) = (end, start);
        }

        return JsValue.String(s.Substring((int)start, (int)(end - start)));
    }

    // Annex B §B.2.2.1 String.prototype.substr(start[, length]).
    private static JsValue Substr(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var s = ThisStringValue(realm, thisV);
        var size = (long)s.Length;
        var intStart = ToIntegerOrInfinity(args.Length > 0 ? args[0] : JsValue.Undefined);
        long lstart;
        if (double.IsNegativeInfinity(intStart))
        {
            lstart = 0;
        }
        else if (intStart < 0)
        {
            lstart = Math.Max(size + (long)intStart, 0);
        }
        else
        {
            lstart = Math.Min((long)intStart, size);
        }

        long resultLen;
        if (args.Length <= 1 || args[1].IsUndefined)
        {
            resultLen = size - lstart;
        }
        else
        {
            var lenNum = ToIntegerOrInfinity(args[1]);
            if (double.IsNegativeInfinity(lenNum) || lenNum <= 0)
            {
                return JsValue.String("");
            }

            resultLen = Math.Min(double.IsPositiveInfinity(lenNum) ? size : (long)lenNum, size - lstart);
        }
        if (resultLen <= 0)
        {
            return JsValue.String("");
        }

        return JsValue.String(s.Substring((int)lstart, (int)resultLen));
    }

    // §22.1.3.21 String.prototype.split returns a genuine Array (ArrayCreate),
    // not a bare array-like — callers do `.split(x).join(y)`, `.map(...)`, etc.,
    // which require Array.prototype. (Was returning an ordinary object, so those
    // methods were undefined.)
    private static JsValue MakeArrayLike(JsRealm realm, List<JsValue> items)
        => JsValue.Object(new JsArray(realm, items));

    private static string RepeatToLength(string fill, int length)
    {
        var sb = new StringBuilder(length);
        while (sb.Length < length)
        {
            sb.Append(fill);
        }

        return sb.ToString(0, length);
    }

    private static string TrimJs(string s, bool trimStart, bool trimEnd)
    {
        var start = 0;
        var end = s.Length - 1;
        if (trimStart)
        {
            while (start <= end && IsJsWhiteSpace(s[start]))
            {
                start++;
            }
        }

        if (trimEnd)
        {
            while (end >= start && IsJsWhiteSpace(s[end]))
            {
                end--;
            }
        }

        return start > end ? string.Empty : s[start..(end + 1)];
    }

    private static bool IsJsWhiteSpace(char c) => char.IsWhiteSpace(c) || c == '\uFEFF';

    private static long Clamp(long value, long min, long max) => Math.Min(Math.Max(value, min), max);

    private static bool IsIntegral(double d) => !double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Truncate(d);

    /// <summary>§7.1.5 ToIntegerOrInfinity.</summary>
    private static long ToIntegerOrInfinity(JsValue value)
    {
        var n = JsValue.ToNumber(value);
        if (double.IsNaN(n) || n == 0)
        {
            return 0;
        }

        if (double.IsPositiveInfinity(n))
        {
            return long.MaxValue;
        }

        if (double.IsNegativeInfinity(n))
        {
            return long.MinValue;
        }

        return (long)Math.Truncate(n);
    }

    /// <summary>§7.1.20 LengthOfArrayLike's ToLength clamp.</summary>
    private static long ToLength(JsValue value)
    {
        var len = ToIntegerOrInfinity(value);
        if (len <= 0)
        {
            return 0;
        }

        return Math.Min(len, int.MaxValue);
    }

    /// <summary>§7.1.6 ToInt32/§7.1.7 ToUint32 modulo arithmetic.</summary>
    private static uint ToUint32(JsValue value)
    {
        var n = JsValue.ToNumber(value);
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0)
        {
            return 0;
        }

        var i = Math.Truncate(n);
        var mod = i - Math.Floor(i / 4294967296.0) * 4294967296.0;
        return (uint)mod;
    }

    private static ushort ToUint16(JsValue value)
    {
        var n = JsValue.ToNumber(value);
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0)
        {
            return 0;
        }

        var i = Math.Truncate(n);
        var mod = i - Math.Floor(i / 65536.0) * 65536.0;
        return (ushort)mod;
    }
}
