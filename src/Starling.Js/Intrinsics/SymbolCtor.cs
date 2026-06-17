using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>ECMA-262 §20.4 Symbol Objects: Symbol factory, registry, prototype, and well-known symbols.</summary>
public static class SymbolCtor
{
    public static readonly JsSymbol AsyncIterator = new("Symbol.asyncIterator");
    public static readonly JsSymbol HasInstance = new("Symbol.hasInstance");
    public static readonly JsSymbol IsConcatSpreadable = new("Symbol.isConcatSpreadable");
    public static readonly JsSymbol Iterator = new("Symbol.iterator");
    public static readonly JsSymbol Match = new("Symbol.match");
    public static readonly JsSymbol MatchAll = new("Symbol.matchAll");
    public static readonly JsSymbol Replace = new("Symbol.replace");
    public static readonly JsSymbol Search = new("Symbol.search");
    public static readonly JsSymbol Species = new("Symbol.species");
    public static readonly JsSymbol Split = new("Symbol.split");
    public static readonly JsSymbol ToPrimitive = new("Symbol.toPrimitive");
    public static readonly JsSymbol ToStringTag = new("Symbol.toStringTag");
    public static readonly JsSymbol Unscopables = new("Symbol.unscopables");

    private static readonly (string Name, JsSymbol Symbol)[] WellKnown =
    [
        ("asyncIterator", AsyncIterator),
        ("hasInstance", HasInstance),
        ("isConcatSpreadable", IsConcatSpreadable),
        ("iterator", Iterator),
        ("match", Match),
        ("matchAll", MatchAll),
        ("replace", Replace),
        ("search", Search),
        ("species", Species),
        ("split", Split),
        ("toPrimitive", ToPrimitive),
        ("toStringTag", ToStringTag),
        ("unscopables", Unscopables),
    ];

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.SymbolPrototype;

        // §20.4.1.1 Symbol(description) is a factory, not a constructor.
        var ctor = new JsNativeFunction("Symbol", (_, args) =>
        {
            var desc = args.Length == 0 || args[0].IsUndefined ? null : JsValue.ToStringValue(args[0]);
            return JsValue.Symbol(new JsSymbol(desc));
        }, isConstructor: false);
        ctor.SetPrototypeOf(realm.FunctionPrototype);
        DefineData(ctor, "name", JsValue.String("Symbol"), writable: false, enumerable: false, configurable: true);
        DefineData(ctor, "length", JsValue.Number(0), writable: false, enumerable: false, configurable: true);
        DefineData(ctor, "prototype", JsValue.Object(proto), writable: false, enumerable: false, configurable: false);

        DefineMethod(ctor, "for", (_, args) => SymbolFor(realm, args), 1);
        DefineMethod(ctor, "keyFor", (_, args) => KeyFor(realm, args), 1);
        foreach (var (name, symbol) in WellKnown)
        {
            DefineData(ctor, name, JsValue.Symbol(symbol), writable: false, enumerable: false, configurable: false);
        }

        DefineData(proto, "constructor", JsValue.Object(ctor), writable: true, enumerable: false, configurable: true);
        DefineMethod(proto, "toString", (thisV, _) => JsValue.String(ThisSymbol(realm, thisV).DescriptiveString), 0);
        DefineMethod(proto, "valueOf", (thisV, _) => JsValue.Symbol(ThisSymbol(realm, thisV)), 0);
        var descriptionGetter = new JsNativeFunction("get description",
            (thisV, _) => ThisSymbol(realm, thisV).Description is { } d ? JsValue.String(d) : JsValue.Undefined,
            isConstructor: false);
        descriptionGetter.SetPrototypeOf(realm.FunctionPrototype);
        DefineData(descriptionGetter, "name", JsValue.String("get description"), false, false, true);
        DefineData(descriptionGetter, "length", JsValue.Number(0), false, false, true);
        proto.DefineOwnProperty("description", PropertyDescriptor.Accessor(descriptionGetter, null, enumerable: false, configurable: true));
        DefineSymbolMethod(proto, ToPrimitive, "[Symbol.toPrimitive]", (thisV, _) => JsValue.Symbol(ThisSymbol(realm, thisV)), 1);
        // §20.4.3.6 Symbol.prototype[@@toStringTag] = "Symbol" (non-writable, non-enumerable, configurable).
        proto.DefineOwnProperty(ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Symbol"), writable: false, enumerable: false, configurable: true));

        realm.SymbolConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Symbol", PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static JsValue SymbolFor(JsRealm realm, JsValue[] args)
    {
        var key = JsValue.ToStringValue(args.Length > 0 ? args[0] : JsValue.Undefined);
        if (!realm.SymbolRegistry.TryGetValue(key, out var symbol))
        {
            symbol = new JsSymbol(key);
            realm.SymbolRegistry[key] = symbol;
        }
        return JsValue.Symbol(symbol);
    }

    private static JsValue KeyFor(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsSymbol)
        {
            throw new JsThrow(realm.NewTypeError("Symbol.keyFor requires a Symbol"));
        }

        var needle = args[0].AsSymbol;
        foreach (var (key, symbol) in realm.SymbolRegistry)
        {
            if (ReferenceEquals(symbol, needle))
            {
                return JsValue.String(key);
            }
        }

        return JsValue.Undefined;
    }

    private static JsSymbol ThisSymbol(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsSymbol)
        {
            return thisV.AsSymbol;
        }

        if (thisV.IsObject)
        {
            var slot = thisV.AsObject.GetOwnPropertyDescriptor("__primitiveValue");
            if (slot is { } d && d.Value.IsSymbol)
            {
                return d.Value.AsSymbol;
            }
        }
        throw new JsThrow(realm.NewTypeError("Symbol.prototype method called on incompatible receiver"));
    }

    private static void DefineMethod(JsObject target, string name, Func<JsValue, JsValue[], JsValue> body, int length)
    {
        var fn = new JsNativeFunction(name, body, isConstructor: false);
        DefineData(fn, "name", JsValue.String(name), false, false, true);
        DefineData(fn, "length", JsValue.Number(length), false, false, true);
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }

    private static void DefineSymbolMethod(JsObject target, JsSymbol key, string name, Func<JsValue, JsValue[], JsValue> body, int length)
    {
        var fn = new JsNativeFunction(name, body, isConstructor: false);
        DefineData(fn, "name", JsValue.String(name), false, false, true);
        DefineData(fn, "length", JsValue.Number(length), false, false, true);
        target.DefineOwnProperty(key, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }

    private static void DefineData(JsObject target, string name, JsValue value, bool writable, bool enumerable, bool configurable)
        => target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable, enumerable, configurable));
}
