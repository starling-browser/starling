using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// Shared helpers used by intrinsic install paths. Built-in methods created
/// here inherit from <c>realm.FunctionPrototype</c>, so they expose
/// <c>call</c>, <c>apply</c>, and <c>bind</c>.
/// </summary>
internal static class IntrinsicHelpers
{
    /// <summary>
    /// Install a writable + non-enumerable + configurable method on a
    /// prototype/namespace object (the §17 default attributes for built-in
    /// functions). The function inherits from <c>realm.FunctionPrototype</c>
    /// and has its <c>name</c> + <c>length</c> own properties stamped by the
    /// realm-aware <see cref="JsNativeFunction"/> ctor.
    /// </summary>
    public static JsNativeFunction DefineMethod(
        JsRealm realm,
        JsObject target,
        string name,
        int length,
        Func<JsValue, JsValue[], JsValue> body,
        bool isConstructor = false)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(target);
        var fn = new JsNativeFunction(realm, name, length, body, isConstructor);
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
        return fn;
    }

    /// <summary>One string-keyed data property to bulk-install on a prototype:
    /// either a builtin method created from <paramref name="Body"/>, or — when
    /// <paramref name="Body"/> is null — a pre-built value (e.g. the
    /// <c>constructor</c> back-reference, or <c>Function.prototype.length</c>)
    /// carried in <paramref name="Value"/>. <paramref name="Flags"/> are the
    /// data-property attributes (default builtin = W=true/E=false/C=true,
    /// <see cref="PropertyDescriptor.BuiltinMethod"/>); pass an explicit value for
    /// non-default data properties (e.g. a non-writable <c>length</c>/<c>name</c>).
    /// Order in the caller's array is creation order.</summary>
    internal readonly record struct BulkMember(
        string Name,
        int Length,
        Func<JsValue, JsValue[], JsValue>? Body,
        JsValue Value = default,
        byte Flags = Shape.Writable | Shape.Configurable);

    /// <summary>Bulk-install an ordered set of string-keyed builtin data
    /// properties (methods + optional <c>constructor</c>) on a freshly-created
    /// prototype by adopting ONE precomputed terminal <see cref="Shape"/> plus a
    /// filled slot array, instead of N sequential <c>DefineOwnProperty</c> calls.
    ///
    /// <para>The terminal shape is built once via <see cref="Shape.Transition"/>
    /// using each member's builtin attributes (W=true/E=false/C=true,
    /// <see cref="PropertyDescriptor.BuiltinMethod"/>), so its identity is exactly
    /// what the incremental path would have produced — inline caches and
    /// dictionary migration see no difference, and <c>getOwnPropertyNames(proto)</c>
    /// order is unchanged. The result is byte-identical to installing each member
    /// with <see cref="DefineMethod"/> / a builtin-method <c>DefineOwnProperty</c>.</para>
    ///
    /// <para>HAZARDS the caller must respect: (1) every member must be a
    /// string-keyed DATA property with builtin attributes — symbol-keyed members
    /// (e.g. <c>@@iterator</c>) can never enter a shape and must be installed via
    /// the dictionary path AFTER this call; (2) <paramref name="target"/> must
    /// still be in its initial fast state (root shape, no own properties); (3) any
    /// accessor or non-builtin-attribute property forces the incremental path.</para>
    /// </summary>
    internal static void BulkInstallBuiltins(JsRealm realm, JsObject target, System.ReadOnlySpan<BulkMember> members)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(target);

        var shape = Shape.Root;
        var slots = new JsValue[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            var m = members[i];
            JsValue value;
            if (m.Body is not null)
            {
                var fn = new JsNativeFunction(realm, m.Name, m.Length, m.Body, isConstructor: false);
                value = JsValue.Object(fn);
            }
            else
            {
                value = m.Value;
            }
            slots[i] = value;
            shape = shape.Transition(m.Name, m.Flags);
        }
        target.AdoptShape(shape, slots);
    }

    /// <summary>Install a constant data property (non-writable, non-enumerable, non-configurable).</summary>
    public static void DefineConstant(JsObject target, string name, JsValue value)
    {
        target.DefineOwnProperty(name,
            PropertyDescriptor.Data(value, writable: false, enumerable: false, configurable: false));
    }

    // ====================================================================
    //  Subclassing support — new.target threading for native constructors
    // ====================================================================
    //
    // A native constructor body is `Func<JsValue thisV, JsValue[] args>`. The
    // VM's calling convention puts new.target in the `thisV` slot when the
    // builtin is invoked through [[Construct]]: `AbstractOperations.Construct`
    // calls `nat.Body(JsValue.Object(newTarget), args)`. So:
    //   * `new X()`            → thisV is the ctor object X (its own new.target).
    //   * derived `super(...)` → thisV is the DERIVED class constructor.
    //   * plain `X()` call     → thisV is undefined / the receiver (NOT a ctor).
    // A construct invocation is therefore detected by `thisV` being a
    // constructor object; the same object is the new.target whose `.prototype`
    // OrdinaryCreateFromConstructor uses for the instance's [[Prototype]].

    /// <summary>True iff a native constructor body was reached as a
    /// [[Construct]] (`new`/derived `super()`) rather than a plain call — i.e.
    /// the first arg (new.target) is a constructor object.</summary>
    public static bool IsConstructInvocation(JsValue thisV)
        => thisV.IsObject && AbstractOperations.IsConstructor(thisV);

    /// <summary>§10.1.13 OrdinaryCreateFromConstructor prototype resolution for a
    /// native constructor. Given the new.target carried in <paramref name="thisV"/>
    /// (see notes above), returns <c>newTarget.prototype</c> when it is an object,
    /// otherwise <paramref name="defaultProto"/>. When not a construct invocation
    /// (plain call) the default is returned. This is what lets
    /// `class X extends Builtin {}` produce instances whose [[Prototype]] is
    /// <c>X.prototype</c> while still carrying the builtin's internal slots.</summary>
    public static JsObject NewTargetPrototype(JsVm? vm, JsValue thisV, JsObject defaultProto)
    {
        if (thisV.IsObject && AbstractOperations.IsConstructor(thisV))
        {
            var proto = AbstractOperations.Get(vm, thisV.AsObject, "prototype");
            if (proto.IsObject)
            {
                return proto.AsObject;
            }
        }
        return defaultProto;
    }
}
