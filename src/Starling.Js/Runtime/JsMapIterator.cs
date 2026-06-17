using Starling.Js.Intrinsics;

namespace Starling.Js.Runtime;

/// <summary>§24.1.5 Map Iterator kind tag.</summary>
public enum MapIteratorKind { Key, Value, KeyAndValue }

/// <summary>§24.1.5 Map Iterator instances. Mirrors
/// <see cref="JsArrayIterator"/>'s shape — inherits from
/// %MapIteratorPrototype% which in turn inherits from %IteratorPrototype%.
/// Walks the backing list of slots, skipping tombstones.</summary>
public sealed class JsMapIterator : JsObject
{
    private readonly JsMap _map;
    private readonly MapIteratorKind _kind;
    private int _nextIndex;
    private bool _done;

    public JsMapIterator(JsRealm realm, JsMap map, MapIteratorKind kind)
        : base(realm.MapIteratorPrototype)
    {
        _map = map;
        _kind = kind;
        _nextIndex = 0;
    }

    public JsValue Next(JsRealm realm)
    {
        if (_done)
        {
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }

        while (_nextIndex < _map.SlotCount)
        {
            var index = _nextIndex++;
            if (!_map.TryGetSlot(index, out var key, out var value))
            {
                continue;
            }

            return _kind switch
            {
                MapIteratorKind.Key => IteratorIntrinsics.MakeResult(realm, key, done: false),
                MapIteratorKind.Value => IteratorIntrinsics.MakeResult(realm, value, done: false),
                MapIteratorKind.KeyAndValue => IteratorIntrinsics.MakeResult(realm, MakePair(realm, key, value), done: false),
                _ => throw new InvalidOperationException($"unknown map iterator kind {_kind}"),
            };
        }

        _done = true;
        return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
    }

    private static JsValue MakePair(JsRealm realm, JsValue key, JsValue value)
    {
        var arr = new JsArray(realm);
        arr.Push(key);
        arr.Push(value);
        return JsValue.Object(arr);
    }
}
