using Starling.IdlGen.Merging;
using Starling.IdlGen.Model;

namespace Starling.IdlGen.Mapping;

public enum TypeKind
{
    Boolean, Integer, Float, BigInt, String, Object, Any, Undefined,
    Sequence, FrozenArray, ObservableArray, Record, Promise, Union,
    Interface, Enum, Dictionary, Callback, Buffer, Unknown,
}

// Where a type appears. Changes a few mappings (a sequence argument accepts any
// IEnumerable, but a sequence result is a concrete IReadOnlyList).
public enum TypePosition { Parameter, Return, Member }

public sealed record MappedType(string CSharp, TypeKind Kind, bool Nullable);

// Maps a Web IDL type to a C# type, resolving typedefs through the model. The
// CSharp string already carries a trailing "?" when the type is nullable. Union
// types map to a generated .NET 11 union whose name comes from UnionName; the
// caller collects those names to emit the union declarations.
public sealed class TypeMapper(WebIdlModel model)
{
    // The Starling JS engine's boxed value type, used for any / object / buffers.
    public const string JsValue = "JsValue";

    private static readonly Dictionary<string, (string Cs, TypeKind Kind)> Primitives = new(StringComparer.Ordinal)
    {
        ["boolean"] = ("bool", TypeKind.Boolean),
        ["byte"] = ("sbyte", TypeKind.Integer),
        ["octet"] = ("byte", TypeKind.Integer),
        ["short"] = ("short", TypeKind.Integer),
        ["unsigned short"] = ("ushort", TypeKind.Integer),
        ["long"] = ("int", TypeKind.Integer),
        ["unsigned long"] = ("uint", TypeKind.Integer),
        ["long long"] = ("long", TypeKind.Integer),
        ["unsigned long long"] = ("ulong", TypeKind.Integer),
        ["float"] = ("float", TypeKind.Float),
        ["unrestricted float"] = ("float", TypeKind.Float),
        ["double"] = ("double", TypeKind.Float),
        ["unrestricted double"] = ("double", TypeKind.Float),
        ["bigint"] = ("System.Numerics.BigInteger", TypeKind.BigInt),
    };

    private static readonly HashSet<string> Strings = new(StringComparer.Ordinal)
    {
        "DOMString", "USVString", "ByteString", "CSSOMString",
    };

    private static readonly HashSet<string> Buffers = new(StringComparer.Ordinal)
    {
        "ArrayBuffer", "SharedArrayBuffer", "DataView", "ArrayBufferView", "BufferSource",
        "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array",
        "Int32Array", "Uint32Array", "BigInt64Array", "BigUint64Array",
        "Float16Array", "Float32Array", "Float64Array",
    };

    public MappedType Map(IdlType type, TypePosition pos)
    {
        var r = model.ResolveTypedef(type);

        if (r.IsUnion)
        {
            return Nullable(new MappedType(UnionName(r), TypeKind.Union, false), r.Nullable);
        }

        // Parameterised types.
        switch (r.Name)
        {
            case "sequence":
                {
                    string elem = Map(r.TypeArgs[0], pos).CSharp;
                    string cs = pos == TypePosition.Parameter ? $"IEnumerable<{elem}>" : $"IReadOnlyList<{elem}>";
                    return Nullable(new MappedType(cs, TypeKind.Sequence, false), r.Nullable);
                }
            case "FrozenArray":
                {
                    string elem = Map(r.TypeArgs[0], pos).CSharp;
                    return Nullable(new MappedType($"IReadOnlyList<{elem}>", TypeKind.FrozenArray, false), r.Nullable);
                }
            case "ObservableArray":
                {
                    string elem = Map(r.TypeArgs[0], pos).CSharp;
                    return Nullable(new MappedType($"IList<{elem}>", TypeKind.ObservableArray, false), r.Nullable);
                }
            case "record":
                {
                    string val = Map(r.TypeArgs[1], pos).CSharp;
                    return Nullable(new MappedType($"IReadOnlyDictionary<string, {val}>", TypeKind.Record, false), r.Nullable);
                }
            case "Promise":
                {
                    var inner = r.TypeArgs[0];
                    string cs = inner.Name == "undefined" && !inner.IsUnion
                        ? "Task"
                        : $"Task<{Map(inner, TypePosition.Return).CSharp}>";
                    return new MappedType(cs, TypeKind.Promise, false);
                }
        }

        if (Primitives.TryGetValue(r.Name, out var prim))
        {
            return Nullable(new MappedType(prim.Cs, prim.Kind, false), r.Nullable);
        }

        if (Strings.Contains(r.Name))
        {
            return Nullable(new MappedType("string", TypeKind.String, false), r.Nullable);
        }

        if (r.Name is "object")
        {
            return new MappedType(JsValue, TypeKind.Object, false);
        }

        if (r.Name is "any")
        {
            return new MappedType(JsValue, TypeKind.Any, false);
        }

        if (r.Name is "undefined" or "void")
        {
            return new MappedType("void", TypeKind.Undefined, false);
        }

        if (r.Name is "symbol")
        {
            return new MappedType(JsValue, TypeKind.Object, false);
        }

        if (Buffers.Contains(r.Name))
        {
            return new MappedType(JsValue, TypeKind.Buffer, false);
        }

        if (model.Enums.ContainsKey(r.Name))
        {
            return Nullable(new MappedType(r.Name, TypeKind.Enum, false), r.Nullable);
        }

        if (model.Dictionaries.ContainsKey(r.Name))
        {
            return Nullable(new MappedType(r.Name, TypeKind.Dictionary, false), r.Nullable);
        }

        if (model.Callbacks.ContainsKey(r.Name))
        {
            return Nullable(new MappedType(r.Name, TypeKind.Callback, false), r.Nullable);
        }

        if (model.Interfaces.ContainsKey(r.Name) || model.Mixins.ContainsKey(r.Name))
        {
            return Nullable(new MappedType(ClrName(r.Name), TypeKind.Interface, false), r.Nullable);
        }

        // An unknown name is almost always an interface from a spec we have not
        // vendored. Treat it as one so signatures still form.
        return Nullable(new MappedType(ClrName(r.Name), TypeKind.Unknown, false), r.Nullable);
    }

    // The Starling DOM CLR class for an IDL interface. Defaults to the IDL name;
    // the IDL-to-CLR override map (Task #5) refines the mismatches.
    public string ClrName(string idlName) => idlName;

    // A deterministic C# identifier for a union type, e.g. "(Node or DOMString)"
    // becomes "NodeOrString". The union-declaration emitter reuses this name.
    public string UnionName(IdlType union)
    {
        var r = model.ResolveTypedef(union);
        return string.Join("Or", r.Union.Select(MemberLabel));
    }

    private string MemberLabel(IdlType member)
    {
        var r = model.ResolveTypedef(member);
        if (r.IsUnion)
        {
            return string.Join("Or", r.Union.Select(MemberLabel));
        }

        return r.Name switch
        {
            "sequence" or "FrozenArray" or "ObservableArray" => MemberLabel(r.TypeArgs[0]) + "Sequence",
            "record" => "Record",
            "Promise" => "Promise",
            _ when Primitives.ContainsKey(r.Name) => Pascal(Primitives[r.Name].Cs),
            _ when Strings.Contains(r.Name) => "String",
            "object" or "any" or "symbol" => "Object",
            _ when Buffers.Contains(r.Name) => "Buffer",
            _ => r.Name,
        };
    }

    private static string Pascal(string s) => s switch
    {
        "bool" => "Boolean",
        "sbyte" => "Byte",
        "byte" => "Octet",
        "short" => "Short",
        "ushort" => "UnsignedShort",
        "int" => "Long",
        "uint" => "UnsignedLong",
        "long" => "LongLong",
        "ulong" => "UnsignedLongLong",
        "float" => "Float",
        "double" => "Double",
        "System.Numerics.BigInteger" => "BigInt",
        _ => char.ToUpperInvariant(s[0]) + s[1..],
    };

    private static MappedType Nullable(MappedType t, bool nullable)
    {
        if (!nullable || t.CSharp == "void" || t.CSharp.EndsWith('?'))
        {
            return t;
        }

        return t with { CSharp = t.CSharp + "?", Nullable = true };
    }
}
