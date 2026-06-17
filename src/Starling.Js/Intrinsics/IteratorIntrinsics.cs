using System.Globalization;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §27.1 The %IteratorPrototype% intrinsic and the per-kind iterator
/// prototypes (%ArrayIteratorPrototype%, %StringIteratorPrototype%). Installs
/// the <c>@@iterator</c>-returns-this trick on %IteratorPrototype% so every
/// downstream iterator object is itself iterable.
/// </summary>
public static class IteratorIntrinsics
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        // %IteratorPrototype% — has [@@iterator]() { return this; }. The
        // ArrayIterator and StringIterator prototypes already inherit from
        // this via JsRealm bootstrap, so installing it once threads through.
        var iterProto = realm.IteratorPrototype;
        var iteratorReturnsThis = new JsNativeFunction(realm, "[Symbol.iterator]", 0,
            (thisV, _) => thisV, isConstructor: false);
        iterProto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(iteratorReturnsThis)));

        // %ArrayIteratorPrototype%.next()
        var arrIterProto = realm.ArrayIteratorPrototype;
        var arrayNext = new JsNativeFunction(realm, "next", 0,
            (thisV, _) => ArrayIteratorNext(realm, thisV), isConstructor: false);
        arrIterProto.DefineOwnProperty("next",
            PropertyDescriptor.BuiltinMethod(JsValue.Object(arrayNext)));
        arrIterProto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Array Iterator"), writable: false, enumerable: false, configurable: true));

        // %StringIteratorPrototype%.next()
        var strIterProto = realm.StringIteratorPrototype;
        var stringNext = new JsNativeFunction(realm, "next", 0,
            (thisV, _) => StringIteratorNext(realm, thisV), isConstructor: false);
        strIterProto.DefineOwnProperty("next",
            PropertyDescriptor.BuiltinMethod(JsValue.Object(stringNext)));
        strIterProto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("String Iterator"), writable: false, enumerable: false, configurable: true));
    }

    /// <summary>§23.1.5.1 CreateArrayIterator — public factory used by
    /// <c>Array.prototype.{keys,values,entries}</c> and the <c>@@iterator</c>
    /// installer.</summary>
    public static JsValue CreateArrayIterator(JsRealm realm, JsValue thisV, ArrayIteratorKind kind)
    {
        if (thisV.IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("Array iterator requires an object"));
        }

        var obj = thisV.IsObject ? thisV.AsObject : AbstractOperations.ToObject(realm, thisV);
        return JsValue.Object(new JsArrayIterator(realm, obj, kind));
    }

    /// <summary>§22.1.5.1 CreateStringIterator — used by
    /// <c>String.prototype[@@iterator]</c>.</summary>
    public static JsValue CreateStringIterator(JsRealm realm, string s)
        => JsValue.Object(new JsStringIterator(realm, s));

    // ------------------------------------------------------------------
    //                       Array iterator.next()
    // ------------------------------------------------------------------

    private static JsValue ArrayIteratorNext(JsRealm realm, JsValue thisV)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsArrayIterator it)
        {
            throw new JsThrow(realm.NewTypeError("Array Iterator.prototype.next called on incompatible receiver"));
        }

        return it.Next(realm);
    }

    // ------------------------------------------------------------------
    //                      String iterator.next()
    // ------------------------------------------------------------------

    private static JsValue StringIteratorNext(JsRealm realm, JsValue thisV)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsStringIterator it)
        {
            throw new JsThrow(realm.NewTypeError("String Iterator.prototype.next called on incompatible receiver"));
        }

        return it.Next(realm);
    }

    /// <summary>Build the iterator-result object <c>{value, done}</c>.</summary>
    internal static JsValue MakeResult(JsRealm realm, JsValue value, bool done)
    {
        var obj = realm.NewOrdinaryObject();
        obj.DefineOwnProperty("value", PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
        obj.DefineOwnProperty("done", PropertyDescriptor.Data(JsValue.Boolean(done), writable: true, enumerable: true, configurable: true));
        return JsValue.Object(obj);
    }
}

/// <summary>Opaque VM-side handle wrapping an <see cref="IteratorRecord"/>.
/// Lives on the operand stack as a JsValue.Object so the existing dispatcher
/// doesn't need a new value kind. Never exposed to user code.</summary>
public sealed class JsIteratorRecordHandle : JsObject
{
    public IteratorRecord Record;

    /// <summary>wp:M3-04g — true when this record was produced by
    /// <c>GetAsyncIterator</c> on an object that only has a sync
    /// <c>[Symbol.iterator]</c> (CreateAsyncFromSyncIterator, §27.1.4.1).
    /// The driver wraps each sync iterator-result in a resolved Promise and
    /// awaits its <c>value</c>.</summary>
    public bool SyncWrapped;

    public JsIteratorRecordHandle(IteratorRecord record) : base(null)
    {
        Record = record;
    }
}

/// <summary>§23.1.5.2 Array Iterator kind tag — also reused for ArrayLike.</summary>
public enum ArrayIteratorKind
{
    Key,
    Value,
    KeyAndValue,
}

/// <summary>§23.1.5.2 Array Iterator instances. Carries the
/// [[IteratedObject]] + [[NextIndex]] + [[Kind]] internal slots inline.</summary>
public sealed class JsArrayIterator : JsObject
{
    private readonly JsObject _iterated;
    private readonly ArrayIteratorKind _kind;
    private int _nextIndex;
    private bool _done;

    public JsArrayIterator(JsRealm realm, JsObject iterated, ArrayIteratorKind kind)
        : base(realm.ArrayIteratorPrototype)
    {
        _iterated = iterated;
        _kind = kind;
        _nextIndex = 0;
    }

    public JsValue Next(JsRealm realm)
    {
        if (_done)
        {
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }

        var len = GetLength(_iterated);
        if (_nextIndex >= len)
        {
            _done = true;
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }
        var index = _nextIndex;
        _nextIndex++;
        switch (_kind)
        {
            case ArrayIteratorKind.Key:
                return IteratorIntrinsics.MakeResult(realm, JsValue.Number(index), done: false);
            case ArrayIteratorKind.Value:
                return IteratorIntrinsics.MakeResult(realm, GetElement(_iterated, index), done: false);
            case ArrayIteratorKind.KeyAndValue:
                var pair = new JsArray(realm);
                pair.Push(JsValue.Number(index));
                pair.Push(GetElement(_iterated, index));
                return IteratorIntrinsics.MakeResult(realm, JsValue.Object(pair), done: false);
            default:
                throw new InvalidOperationException($"unknown array iterator kind {_kind}");
        }
    }

    private static int GetLength(JsObject obj)
    {
        if (obj is JsArray arr)
        {
            return arr.Length;
        }

        var v = obj.Get("length");
        var n = JsValue.ToNumber(v);
        if (double.IsNaN(n) || n <= 0)
        {
            return 0;
        }

        if (n > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Min(Math.Truncate(n), (double)(1L << 53) - 1);
    }

    private static JsValue GetElement(JsObject obj, int i)
    {
        if (obj is JsArray arr)
        {
            return arr[i];
        }

        return obj.Get(i.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>§22.1.5 String Iterator instances. Walks the underlying string
/// by Unicode code point (surrogate-pair-aware) so
/// <c>[..."😀ab"].length === 3</c>, matching ECMA-262.</summary>
public sealed class JsStringIterator : JsObject
{
    private readonly string _source;
    private int _nextIndex;
    private bool _done;

    public JsStringIterator(JsRealm realm, string source)
        : base(realm.StringIteratorPrototype)
    {
        _source = source ?? string.Empty;
        _nextIndex = 0;
    }

    public JsValue Next(JsRealm realm)
    {
        if (_done)
        {
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }

        if (_nextIndex >= _source.Length)
        {
            _done = true;
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }
        var first = _source[_nextIndex];
        string codePoint;
        if (char.IsHighSurrogate(first) && _nextIndex + 1 < _source.Length && char.IsLowSurrogate(_source[_nextIndex + 1]))
        {
            codePoint = _source.Substring(_nextIndex, 2);
            _nextIndex += 2;
        }
        else
        {
            codePoint = first.ToString();
            _nextIndex += 1;
        }
        return IteratorIntrinsics.MakeResult(realm, JsValue.String(codePoint), done: false);
    }
}
