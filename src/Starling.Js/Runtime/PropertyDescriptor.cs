namespace Starling.Js.Runtime;

/// <summary>
/// Spec §6.2.5 Property Descriptor. Every own property on an object has one.
/// Data descriptors hold a <see cref="Value"/>; accessor descriptors hold
/// <see cref="Getter"/>/<see cref="Setter"/> callables. flag bits
/// stores writable/enumerable/configurable/accessor bits.
/// </summary>
public readonly struct PropertyDescriptor : IEquatable<PropertyDescriptor>
{
    [Flags]
    private enum Bits : byte
    {
        None = 0,
        Writable = 1 << 0,
        Enumerable = 1 << 1,
        Configurable = 1 << 2,
        Accessor = 1 << 3,
    }

    public readonly JsValue Value;
    public readonly JsObject? Getter;
    public readonly JsObject? Setter;
    private readonly Bits _flags;

    private PropertyDescriptor(JsValue value, JsObject? getter, JsObject? setter, Bits flags)
    {
        Value = value;
        Getter = getter;
        Setter = setter;
        _flags = flags;
    }

    public bool Writable => (_flags & Bits.Writable) != 0;
    public bool Enumerable => (_flags & Bits.Enumerable) != 0;
    public bool Configurable => (_flags & Bits.Configurable) != 0;
    public bool IsAccessor => (_flags & Bits.Accessor) != 0;
    public bool IsData => !IsAccessor;

    /// <summary>Default data descriptor: writable + enumerable + configurable.</summary>
    public static PropertyDescriptor Data(JsValue value) =>
        new(value, null, null, Bits.Writable | Bits.Enumerable | Bits.Configurable);

    /// <summary>Data descriptor with explicit attributes.</summary>
    public static PropertyDescriptor Data(JsValue value, bool writable, bool enumerable, bool configurable)
    {
        var flags = Bits.None;
        if (writable)
        {
            flags |= Bits.Writable;
        }

        if (enumerable)
        {
            flags |= Bits.Enumerable;
        }

        if (configurable)
        {
            flags |= Bits.Configurable;
        }

        return new PropertyDescriptor(value, null, null, flags);
    }

    /// <summary>Accessor descriptor (getter/setter pair).</summary>
    public static PropertyDescriptor Accessor(JsObject? getter, JsObject? setter, bool enumerable = false, bool configurable = true)
    {
        var flags = Bits.Accessor;
        if (enumerable)
        {
            flags |= Bits.Enumerable;
        }

        if (configurable)
        {
            flags |= Bits.Configurable;
        }

        return new PropertyDescriptor(JsValue.Undefined, getter, setter, flags);
    }

    /// <summary>Non-writable, non-enumerable, non-configurable data property — what
    /// the spec uses for most intrinsic prototype methods.</summary>
    public static PropertyDescriptor BuiltinMethod(JsValue value) =>
        Data(value, writable: true, enumerable: false, configurable: true);

    /// <summary>Same value, fresh attributes — for [[Set]] updating an existing slot.</summary>
    public PropertyDescriptor WithValue(JsValue newValue) =>
        new(newValue, Getter, Setter, _flags);

    public bool Equals(PropertyDescriptor other) =>
        _flags == other._flags
        && Value.Equals(other.Value)
        && ReferenceEquals(Getter, other.Getter)
        && ReferenceEquals(Setter, other.Setter);

    public override bool Equals(object? obj) => obj is PropertyDescriptor d && Equals(d);
    public override int GetHashCode() => HashCode.Combine(Value, Getter, Setter, _flags);
}
