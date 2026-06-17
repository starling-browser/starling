using System.Runtime.CompilerServices;

namespace Starling.Js.Runtime;

/// <summary>
/// §24.4 WeakSet Objects — object-only set with weak membership. Same
/// rationale as <see cref="JsWeakMap"/>: ConditionalWeakTable handles GC
/// eligibility, and a sentinel value populates the unused value slot.
/// </summary>
public sealed class JsWeakSet : JsObject
{
    private static readonly object Sentinel = new();

    private readonly ConditionalWeakTable<JsObject, object> _table = new();

    public JsWeakSet(JsRealm realm) : base(realm.WeakSetPrototype) { }

    public bool Has(JsObject value) => _table.TryGetValue(value, out _);

    public void Add(JsObject value)
    {
        if (_table.TryGetValue(value, out _))
        {
            return;
        }

        _table.Add(value, Sentinel);
    }

    public bool Delete(JsObject value) => _table.Remove(value);
}
