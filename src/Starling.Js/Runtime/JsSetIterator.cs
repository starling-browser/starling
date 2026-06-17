using Starling.Js.Intrinsics;

namespace Starling.Js.Runtime;

/// <summary>§24.2.5 Set Iterator kind tag.</summary>
public enum SetIteratorKind { Value, KeyAndValue }

/// <summary>§24.2.5 Set Iterator instances. Spec's <c>keys</c> and
/// <c>values</c> both produce a <see cref="SetIteratorKind.Value"/> iterator;
/// <c>entries</c> produces <see cref="SetIteratorKind.KeyAndValue"/> which
/// yields <c>[v, v]</c> pairs per §24.2.3.6.</summary>
public sealed class JsSetIterator : JsObject
{
    private readonly JsSet _set;
    private readonly SetIteratorKind _kind;
    private int _nextIndex;
    private bool _done;

    public JsSetIterator(JsRealm realm, JsSet set, SetIteratorKind kind)
        : base(realm.SetIteratorPrototype)
    {
        _set = set;
        _kind = kind;
        _nextIndex = 0;
    }

    public JsValue Next(JsRealm realm)
    {
        if (_done)
        {
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }

        while (_nextIndex < _set.SlotCount)
        {
            var index = _nextIndex++;
            if (!_set.TryGetSlot(index, out var value))
            {
                continue;
            }

            return _kind switch
            {
                SetIteratorKind.Value => IteratorIntrinsics.MakeResult(realm, value, done: false),
                SetIteratorKind.KeyAndValue => IteratorIntrinsics.MakeResult(realm, MakePair(realm, value), done: false),
                _ => throw new InvalidOperationException($"unknown set iterator kind {_kind}"),
            };
        }

        _done = true;
        return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
    }

    private static JsValue MakePair(JsRealm realm, JsValue value)
    {
        var arr = new JsArray(realm);
        arr.Push(value);
        arr.Push(value);
        return JsValue.Object(arr);
    }
}
