using System.Globalization;
using System.Text;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §25.5 JSON intrinsic. Implements <c>JSON.parse</c> and <c>JSON.stringify</c>
/// per RFC 8259 (parse) and the ECMAScript serialization algorithm
/// (stringify) — including reviver, replacer (function or allow-list array),
/// indent (number or string), <c>toJSON</c>, and cycle detection.
/// </summary>
/// <remarks>
/// The parser is a recursive descent parser over the input string.
/// <c>System.Text.Json</c> is not used because its grammar differs from
/// ECMAScript JSON in edge cases such as number tokens.
/// </remarks>
public static class JsonObj
{
    /// <summary>
    /// Marker subclass identifying an "array-shaped" object produced by
    /// JSON parsing. JSON.stringify uses this to decide between array and
    /// object serialization paths. <c>JSON.stringify</c> also recognizes real
    /// <see cref="JsArray"/> instances created outside JSON parsing.
    /// </summary>
    internal sealed class JsonArray : JsObject
    {
        public JsonArray(JsObject? proto) : base(proto) { }
    }

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        var json = realm.NewOrdinaryObject();

        // JSON.parse: spec length === 2 (text, reviver).
        IntrinsicHelpers.DefineMethod(realm, json, "parse", 2, (thisValue, args) =>
        {
            var text = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
            var reviver = args.Length > 1 ? args[1] : JsValue.Undefined;
            var parser = new JsonParser(text, realm);
            var value = parser.ParseRoot();
            if (AbstractOperations.IsCallable(reviver))
            {
                var root = realm.NewOrdinaryObject();
                root.DefineOwnProperty("",
                    PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
                value = InternalizeJsonProperty(root, "", reviver, realm);
            }
            return value;
        });

        // JSON.stringify: spec length === 3 (value, replacer, space).
        IntrinsicHelpers.DefineMethod(realm, json, "stringify", 3, (thisValue, args) =>
        {
            var value = args.Length > 0 ? args[0] : JsValue.Undefined;
            var replacer = args.Length > 1 ? args[1] : JsValue.Undefined;
            var space = args.Length > 2 ? args[2] : JsValue.Undefined;
            var state = StringifyState.Create(realm, replacer, space);
            var holder = realm.NewOrdinaryObject();
            holder.DefineOwnProperty("",
                PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
            var result = state.SerializeProperty("", holder);
            return result is null ? JsValue.Undefined : JsValue.String(result);
        });

        // §25.5.1 JSON[@@toStringTag] = "JSON".
        json.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("JSON"), writable: false, enumerable: false, configurable: true));

        realm.GlobalObject.DefineOwnProperty("JSON",
            PropertyDescriptor.Data(JsValue.Object(json),
                writable: true, enumerable: false, configurable: true));
    }

    // ===========================================================
    // Reviver — §25.5.1.1 InternalizeJSONProperty
    // ===========================================================

    private static JsValue InternalizeJsonProperty(JsObject holder, string key, JsValue reviver, JsRealm realm)
    {
        var val = holder.Get(key);
        if (val.IsObject)
        {
            var obj = val.AsObject;
            if (obj is JsonArray)
            {
                var len = (int)JsValue.ToNumber(obj.Get("length"));
                for (var i = 0; i < len; i++)
                {
                    var ks = i.ToString(CultureInfo.InvariantCulture);
                    var newElement = InternalizeJsonProperty(obj, ks, reviver, realm);
                    if (newElement.IsUndefined)
                    {
                        obj.Delete(ks);
                    }
                    else
                    {
                        obj.DefineOwnProperty(ks,
                            PropertyDescriptor.Data(newElement, writable: true, enumerable: true, configurable: true));
                    }
                }
            }
            else
            {
                var keys = obj.EnumerableKeys().ToArray();
                foreach (var k in keys)
                {
                    var newElement = InternalizeJsonProperty(obj, k, reviver, realm);
                    if (newElement.IsUndefined)
                    {
                        obj.Delete(k);
                    }
                    else
                    {
                        obj.DefineOwnProperty(k,
                            PropertyDescriptor.Data(newElement, writable: true, enumerable: true, configurable: true));
                    }
                }
            }
        }
        var vm = realm.ActiveVm;
        return AbstractOperations.Call(vm, reviver, JsValue.Object(holder),
            new[] { JsValue.String(key), val.IsUndefined ? JsValue.Undefined : holder.Get(key) });
    }

    // ===========================================================
    // Parser — RFC 8259 with the ECMAScript additions on numbers
    // ===========================================================

    private sealed class JsonParser
    {
        private readonly string _src;
        private readonly JsRealm _realm;
        private int _pos;

        public JsonParser(string src, JsRealm realm)
        {
            _src = src ?? "";
            _realm = realm;
            _pos = 0;
        }

        public JsValue ParseRoot()
        {
            SkipWhitespace();
            var v = ParseValue();
            SkipWhitespace();
            if (_pos != _src.Length)
            {
                throw Syntax("Unexpected token after JSON value");
            }

            return v;
        }

        private JsValue ParseValue()
        {
            SkipWhitespace();
            if (_pos >= _src.Length)
            {
                throw Syntax("Unexpected end of JSON input");
            }

            var c = _src[_pos];
            return c switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => JsValue.String(ParseString()),
                't' or 'f' => ParseBoolean(),
                'n' => ParseNull(),
                _ => ParseNumberValue(),
            };
        }

        private JsValue ParseBoolean()
        {
            if (Match("true"))
            {
                return JsValue.True;
            }

            if (Match("false"))
            {
                return JsValue.False;
            }

            throw Syntax($"Unexpected token at position {_pos}");
        }

        private JsValue ParseNull()
        {
            if (Match("null"))
            {
                return JsValue.Null;
            }

            throw Syntax($"Unexpected token at position {_pos}");
        }

        private bool Match(string lit)
        {
            if (_pos + lit.Length > _src.Length)
            {
                return false;
            }

            for (var i = 0; i < lit.Length; i++)
            {
                if (_src[_pos + i] != lit[i])
                {
                    return false;
                }
            }

            _pos += lit.Length;
            return true;
        }

        private JsValue ParseNumberValue()
        {
            var start = _pos;
            // Optional minus.
            if (_pos < _src.Length && _src[_pos] == '-')
            {
                _pos++;
            }
            // int part: 0 OR [1-9][0-9]*
            if (_pos >= _src.Length)
            {
                throw Syntax("Expected number");
            }

            if (_src[_pos] == '0')
            {
                _pos++;
            }
            else if (_src[_pos] >= '1' && _src[_pos] <= '9')
            {
                _pos++;
                while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9')
                {
                    _pos++;
                }
            }
            else
            {
                throw Syntax($"Unexpected token '{_src[_pos]}'");
            }
            // frac (optional)
            if (_pos < _src.Length && _src[_pos] == '.')
            {
                _pos++;
                if (_pos >= _src.Length || _src[_pos] < '0' || _src[_pos] > '9')
                {
                    throw Syntax("Expected digit after decimal point");
                }

                while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9')
                {
                    _pos++;
                }
            }
            // exp (optional)
            if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-'))
                {
                    _pos++;
                }

                if (_pos >= _src.Length || _src[_pos] < '0' || _src[_pos] > '9')
                {
                    throw Syntax("Expected digit in exponent");
                }

                while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9')
                {
                    _pos++;
                }
            }
            var lex = _src.Substring(start, _pos - start);
            if (!double.TryParse(lex, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                throw Syntax($"Invalid number literal '{lex}'");
            }

            return JsValue.Number(d);
        }

        private string ParseString()
        {
            if (_pos >= _src.Length || _src[_pos] != '"')
            {
                throw Syntax("Expected '\"'");
            }

            _pos++;
            var sb = new StringBuilder();
            while (_pos < _src.Length)
            {
                var c = _src[_pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c < 0x20)
                {
                    throw Syntax("Unescaped control character in string");
                }

                if (c == '\\')
                {
                    if (_pos >= _src.Length)
                    {
                        throw Syntax("Unterminated escape in string");
                    }

                    var esc = _src[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _src.Length)
                            {
                                throw Syntax("Invalid \\u escape");
                            }

                            var hex = _src.Substring(_pos, 4);
                            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                            {
                                throw Syntax("Invalid \\u escape");
                            }

                            sb.Append((char)u);
                            _pos += 4;
                            break;
                        default:
                            throw Syntax($"Invalid escape '\\{esc}'");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw Syntax("Unterminated string");
        }

        private JsValue ParseArray()
        {
            _pos++; // '['
            var arr = new JsonArray(_realm.ArrayPrototype);
            SkipWhitespace();
            var index = 0;
            if (_pos < _src.Length && _src[_pos] == ']')
            {
                _pos++;
                arr.DefineOwnProperty("length",
                    PropertyDescriptor.Data(JsValue.Number(0), writable: true, enumerable: false, configurable: false));
                return JsValue.Object(arr);
            }
            while (true)
            {
                SkipWhitespace();
                var v = ParseValue();
                arr.DefineOwnProperty(index.ToString(CultureInfo.InvariantCulture),
                    PropertyDescriptor.Data(v, writable: true, enumerable: true, configurable: true));
                index++;
                SkipWhitespace();
                if (_pos >= _src.Length)
                {
                    throw Syntax("Unterminated array");
                }

                var c = _src[_pos];
                if (c == ',') { _pos++; continue; }
                if (c == ']') { _pos++; break; }
                throw Syntax($"Expected ',' or ']' but got '{c}'");
            }
            arr.DefineOwnProperty("length",
                PropertyDescriptor.Data(JsValue.Number(index), writable: true, enumerable: false, configurable: false));
            return JsValue.Object(arr);
        }

        private JsValue ParseObject()
        {
            _pos++; // '{'
            var obj = _realm.NewOrdinaryObject();
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == '}')
            {
                _pos++;
                return JsValue.Object(obj);
            }
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _src.Length || _src[_pos] != '"')
                {
                    throw Syntax("Expected '\"' (object key must be a string)");
                }

                var key = ParseString();
                SkipWhitespace();
                if (_pos >= _src.Length || _src[_pos] != ':')
                {
                    throw Syntax("Expected ':' after object key");
                }

                _pos++;
                SkipWhitespace();
                var v = ParseValue();
                obj.DefineOwnProperty(key,
                    PropertyDescriptor.Data(v, writable: true, enumerable: true, configurable: true));
                SkipWhitespace();
                if (_pos >= _src.Length)
                {
                    throw Syntax("Unterminated object");
                }

                var c = _src[_pos];
                if (c == ',') { _pos++; continue; }
                if (c == '}') { _pos++; break; }
                throw Syntax($"Expected ',' or '}}' but got '{c}'");
            }
            return JsValue.Object(obj);
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length)
            {
                var c = _src[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    _pos++;
                }
                else
                {
                    break;
                }
            }
        }

        private JsThrow Syntax(string msg)
            => new(_realm.NewSyntaxError($"{msg} (at position {_pos})"));
    }

    // ===========================================================
    // Stringify — §25.5.2 SerializeJSONProperty / Object / Array
    // ===========================================================

    private sealed class StringifyState
    {
        private readonly JsRealm _realm;
        private readonly JsValue _replacer;             // function or Undefined
        private readonly HashSet<string>? _propertyList; // replacer-array allow list (string-converted)
        private readonly string _gap;
        private readonly StringBuilder _sb = new();
        private readonly HashSet<JsObject> _stack = new();
        private int _indentLevel;

        private StringifyState(JsRealm realm, JsValue replacer, HashSet<string>? propertyList, string gap)
        {
            _realm = realm;
            _replacer = replacer;
            _propertyList = propertyList;
            _gap = gap;
        }

        public static StringifyState Create(JsRealm realm, JsValue replacer, JsValue space)
        {
            JsValue fnReplacer = JsValue.Undefined;
            HashSet<string>? list = null;
            if (replacer.IsObject)
            {
                if (AbstractOperations.IsCallable(replacer))
                {
                    fnReplacer = replacer;
                }
                else if (replacer.AsObject is JsonArray ja)
                {
                    list = BuildPropertyList(ja);
                }
                else
                {
                    // Per spec: if replacer is an object with a "length" property
                    // and indexed entries, treat it like an array. This is a
                    // looser match used by tests where literal-array syntax
                    // (not yet wired) is unavailable.
                    var lenVal = replacer.AsObject.Get("length");
                    if (lenVal.IsNumber)
                    {
                        list = BuildPropertyList(replacer.AsObject);
                    }
                }
            }

            // ToString space if it's an object wrapper.
            var gap = "";
            if (space.IsNumber)
            {
                var n = (int)Math.Min(10, Math.Max(0, Math.Floor(space.AsNumber)));
                if (n > 0)
                {
                    gap = new string(' ', n);
                }
            }
            else if (space.IsString)
            {
                var s = space.AsString;
                gap = s.Length <= 10 ? s : s.Substring(0, 10);
            }

            return new StringifyState(realm, fnReplacer, list, gap);
        }

        private static HashSet<string> BuildPropertyList(JsObject arrLike)
        {
            var len = (int)JsValue.ToNumber(arrLike.Get("length"));
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < len; i++)
            {
                var v = arrLike.Get(i.ToString(CultureInfo.InvariantCulture));
                if (v.IsString)
                {
                    set.Add(v.AsString);
                }
                else if (v.IsNumber)
                {
                    set.Add(JsValue.ToStringValue(v));
                }
            }
            return set;
        }

        /// <summary>SerializeJSONProperty(key, holder). Returns null if the
        /// value is omitted entirely (undefined / function / symbol when in
        /// non-array context).</summary>
        public string? SerializeProperty(string key, JsObject holder)
        {
            var value = holder.Get(key);

            // toJSON override.
            if (value.IsObject)
            {
                var toJson = value.AsObject.Get("toJSON");
                if (AbstractOperations.IsCallable(toJson))
                {
                    value = AbstractOperations.Call(_realm.ActiveVm, toJson, value,
                        new[] { JsValue.String(key) });
                }
            }

            // Replacer function.
            if (AbstractOperations.IsCallable(_replacer))
            {
                value = AbstractOperations.Call(_realm.ActiveVm, _replacer,
                    JsValue.Object(holder),
                    new[] { JsValue.String(key), value });
            }

            return SerializeValue(value);
        }

        private string? SerializeValue(JsValue value)
        {
            switch (value.Kind)
            {
                case JsValueKind.Null:
                    return "null";
                case JsValueKind.Boolean:
                    return value.AsBool ? "true" : "false";
                case JsValueKind.String:
                    return QuoteJsonString(value.AsString);
                case JsValueKind.Number:
                    {
                        var d = value.AsNumber;
                        if (double.IsNaN(d) || double.IsInfinity(d))
                        {
                            return "null";
                        }

                        return NumberToJsonString(d);
                    }
                case JsValueKind.BigInt:
                    throw new JsThrow(_realm.NewTypeError("Do not know how to serialize a BigInt"));
                case JsValueKind.Object:
                    {
                        if (AbstractOperations.IsCallable(value))
                        {
                            return null;
                        }

                        var obj = value.AsObject;
                        // §25.5.2: serialize as a JSON array when IsArray(value) — that
                        // is any real Array exotic object, not only the JsonArray that
                        // JSON.parse builds. Without the JsArray arm, `[1,2,3]` and
                        // `.map(...)` results stringified as objects ({"0":1,…}).
                        if (obj is JsArray or JsonArray)
                        {
                            return SerializeArray(obj);
                        }

                        return SerializeObject(obj);
                    }
                case JsValueKind.Undefined:
                default:
                    return null;
            }
        }

        /// <summary>Variant used during array element serialization where
        /// undefined / function / symbol map to "null" rather than being
        /// omitted.</summary>
        private string SerializeArrayElement(JsValue value)
            => SerializeValue(value) ?? "null";

        private string SerializeObject(JsObject obj)
        {
            if (!_stack.Add(obj))
            {
                throw new JsThrow(_realm.NewTypeError("Converting circular structure to JSON"));
            }

            try
            {
                _indentLevel++;
                var keys = _propertyList ?? (IEnumerable<string>)obj.EnumerableKeys().ToArray();
                var entries = new List<string>();
                foreach (var k in keys)
                {
                    if (!obj.HasOwn(k))
                    {
                        continue;
                    }

                    var desc = obj.GetOwnPropertyDescriptor(k);
                    if (desc is null || !desc.Value.Enumerable)
                    {
                        continue;
                    }

                    var serialized = SerializeProperty(k, obj);
                    if (serialized is null)
                    {
                        continue;
                    }

                    var sep = _gap.Length == 0 ? ":" : ": ";
                    entries.Add(QuoteJsonString(k) + sep + serialized);
                }
                string result;
                if (entries.Count == 0)
                {
                    result = "{}";
                }
                else if (_gap.Length == 0)
                {
                    result = "{" + string.Join(",", entries) + "}";
                }
                else
                {
                    var indentInner = string.Concat(Enumerable.Repeat(_gap, _indentLevel));
                    var indentOuter = string.Concat(Enumerable.Repeat(_gap, _indentLevel - 1));
                    result = "{\n" + indentInner + string.Join(",\n" + indentInner, entries) + "\n" + indentOuter + "}";
                }
                return result;
            }
            finally
            {
                _indentLevel--;
                _stack.Remove(obj);
            }
        }

        private string SerializeArray(JsObject arr)
        {
            if (!_stack.Add(arr))
            {
                throw new JsThrow(_realm.NewTypeError("Converting circular structure to JSON"));
            }

            try
            {
                _indentLevel++;
                var len = (int)JsValue.ToNumber(arr.Get("length"));
                var parts = new List<string>(len);
                for (var i = 0; i < len; i++)
                {
                    var k = i.ToString(CultureInfo.InvariantCulture);
                    // For array indices we must NOT apply replacer-list filter;
                    // ECMAScript only applies the PropertyList to object steps.
                    var elem = SerializePropertyForArray(k, arr);
                    parts.Add(elem ?? "null");
                }
                string result;
                if (parts.Count == 0)
                {
                    result = "[]";
                }
                else if (_gap.Length == 0)
                {
                    result = "[" + string.Join(",", parts) + "]";
                }
                else
                {
                    var indentInner = string.Concat(Enumerable.Repeat(_gap, _indentLevel));
                    var indentOuter = string.Concat(Enumerable.Repeat(_gap, _indentLevel - 1));
                    result = "[\n" + indentInner + string.Join(",\n" + indentInner, parts) + "\n" + indentOuter + "]";
                }
                return result;
            }
            finally
            {
                _indentLevel--;
                _stack.Remove(arr);
            }
        }

        /// <summary>Like SerializeProperty but treats omission as "null"
        /// per §25.5.2 SerializeJSONArray step 8.b.</summary>
        private string SerializePropertyForArray(string key, JsObject holder)
        {
            var value = holder.Get(key);
            if (value.IsObject)
            {
                var toJson = value.AsObject.Get("toJSON");
                if (AbstractOperations.IsCallable(toJson))
                {
                    value = AbstractOperations.Call(_realm.ActiveVm, toJson, value,
                        new[] { JsValue.String(key) });
                }
            }
            if (AbstractOperations.IsCallable(_replacer))
            {
                value = AbstractOperations.Call(_realm.ActiveVm, _replacer,
                    JsValue.Object(holder),
                    new[] { JsValue.String(key), value });
            }
            return SerializeArrayElement(value);
        }
    }

    private static string NumberToJsonString(double d)
    {
        if (d == 0)
        {
            return "0";
        }

        if (d == Math.Truncate(d) && Math.Abs(d) < 1e21)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string QuoteJsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || (c >= 0x7f && c <= 0x9f))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
