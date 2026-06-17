using System.Globalization;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// Web Animations API §4 for the Jint backend, mirroring
/// <c>Starling.Bindings/WebAnimationsBinding.cs</c>:
/// <c>element.animate(keyframes, options)</c> returns an <c>Animation</c> with an
/// associated <c>KeyframeEffect</c>. Keyframes + timing translate into the neutral
/// <see cref="IAnimationHost"/> payloads and register with the host when one is
/// installed; with no host the Animation is returned with inert controls (matching
/// the canonical no-host path).
/// </summary>
internal static class WebAnimationsBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        if (ctx.Wrappers.ElementPrototype is not { } elProto)
        {
            return;
        }

        JintInterop.DefineMethod(engine, elProto, "animate", (t, args) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } el)
            {
                return JsValue.Undefined;
            }

            return Animate(ctx, el, args);
        }, 2);
    }

    private static JsObject Animate(JintBackendContext ctx, Element element, JsValue[] args)
    {
        var keyframes = ParseKeyframes(args.Length > 0 ? args[0] : JsValue.Undefined);
        var timing = ParseTiming(args.Length > 1 ? args[1] : JsValue.Undefined);
        var host = ctx.LayoutHost as IAnimationHost;
        var id = host?.Animate(element, keyframes, timing) ?? -1;
        return BuildAnimation(ctx, host, id, keyframes, timing);
    }

    // ---- keyframe parsing ---------------------------------------------------

    private static IReadOnlyList<AnimationKeyframeSpec> ParseKeyframes(JsValue value)
    {
        if (!value.IsObject())
        {
            return System.Array.Empty<AnimationKeyframeSpec>();
        }

        // Array form: [{opacity:0, offset:0}, {opacity:1}].
        if (value is JsArray arr)
        {
            var frames = new List<(double? Offset, List<KeyValuePair<string, string>> Decls)>((int)arr.Length);
            for (var i = 0; i < arr.Length; i++)
            {
                var item = arr[i];
                if (!item.IsObject())
                {
                    continue;
                }

                var obj = item.AsObject();
                double? offset = null;
                var off = obj.Get("offset");
                if (off.IsNumber())
                {
                    offset = off.AsNumber();
                }

                var decls = new List<KeyValuePair<string, string>>();
                foreach (var key in EnumKeys(obj))
                {
                    if (key is "offset" or "easing" or "composite")
                    {
                        continue;
                    }

                    decls.Add(new(CamelToKebab(key), CssText(obj.Get(key))));
                }
                frames.Add((offset, decls));
            }
            return DistributeOffsets(frames);
        }

        // Property-indexed form: {opacity:[0,1], transform:['none','scale(2)']}.
        var src = value.AsObject();
        var props = new List<(string Name, List<string> Values)>();
        var maxLen = 0;
        foreach (var key in EnumKeys(src))
        {
            if (key is "offset" or "easing" or "composite")
            {
                continue;
            }

            var v = src.Get(key);
            var values = new List<string>();
            if (v is JsArray a)
            {
                for (var i = 0; i < a.Length; i++)
                {
                    values.Add(CssText(a[i]));
                }
            }
            else
            {
                values.Add(CssText(v));
            }

            if (values.Count > 0) { props.Add((CamelToKebab(key), values)); maxLen = Math.Max(maxLen, values.Count); }
        }
        if (maxLen == 0)
        {
            return System.Array.Empty<AnimationKeyframeSpec>();
        }

        var result = new List<AnimationKeyframeSpec>(maxLen);
        for (var j = 0; j < maxLen; j++)
        {
            var offset = maxLen == 1 ? 1.0 : (double)j / (maxLen - 1);
            var decls = new List<KeyValuePair<string, string>>(props.Count);
            foreach (var (name, values) in props)
            {
                decls.Add(new(name, values[Math.Min(j, values.Count - 1)]));
            }

            result.Add(new AnimationKeyframeSpec(offset, decls));
        }
        return result;
    }

    private static List<AnimationKeyframeSpec> DistributeOffsets(
        List<(double? Offset, List<KeyValuePair<string, string>> Decls)> frames)
    {
        var n = frames.Count;
        var result = new List<AnimationKeyframeSpec>(n);
        for (var i = 0; i < n; i++)
        {
            var offset = frames[i].Offset ?? (n == 1 ? 1.0 : (double)i / (n - 1));
            result.Add(new AnimationKeyframeSpec(Math.Clamp(offset, 0, 1), frames[i].Decls));
        }
        return result;
    }

    // ---- timing parsing -----------------------------------------------------

    private static AnimationEffectTimingSpec ParseTiming(JsValue value)
    {
        double duration = 0, delay = 0, iterations = 1;
        string direction = "normal", fill = "none", easing = "linear";

        if (value.IsNumber())
        {
            duration = value.AsNumber();
        }
        else if (value.IsObject())
        {
            var o = value.AsObject();
            var dur = o.Get("duration"); if (dur.IsNumber())
            {
                duration = dur.AsNumber();
            }

            var del = o.Get("delay"); if (del.IsNumber())
            {
                delay = del.AsNumber();
            }

            var iter = o.Get("iterations"); if (iter.IsNumber())
            {
                iterations = iter.AsNumber();
            }

            var dir = o.Get("direction"); if (!dir.IsNull() && !dir.IsUndefined())
            {
                direction = TypeConverter.ToString(dir);
            }

            var fl = o.Get("fill"); if (!fl.IsNull() && !fl.IsUndefined())
            {
                fill = TypeConverter.ToString(fl);
            }

            var ea = o.Get("easing"); if (!ea.IsNull() && !ea.IsUndefined())
            {
                easing = TypeConverter.ToString(ea);
            }
        }

        return new AnimationEffectTimingSpec(duration, delay, iterations, direction, fill, easing);
    }

    // ---- Animation / KeyframeEffect objects ---------------------------------

    private static JsObject BuildAnimation(
        JintBackendContext ctx, IAnimationHost? host, int id,
        IReadOnlyList<AnimationKeyframeSpec> keyframes, AnimationEffectTimingSpec timing)
    {
        var engine = ctx.Engine;
        var anim = new JsObject(engine);

        JintInterop.DefineMethod(engine, anim, "play", (_, _) => { host?.Play(id); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, anim, "pause", (_, _) => { host?.Pause(id); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, anim, "cancel", (_, _) => { host?.Cancel(id); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, anim, "finish", (_, _) => { host?.Finish(id); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, anim, "reverse", (_, _) => { host?.Play(id); return JsValue.Undefined; }, 0);

        JintInterop.DefineAccessor(engine, anim, "currentTime",
            (_, _) => JintInterop.Num(host?.CurrentTime(id) ?? 0),
            (_, a) => { if (host is not null && a.Length > 0 && a[0].IsNumber()) { host.SetCurrentTime(id, a[0].AsNumber()); } return JsValue.Undefined; });
        JintInterop.DefineAccessor(engine, anim, "startTime", (_, _) => JintInterop.Num(host?.TimelineNow ?? 0));
        JintInterop.DefineAccessor(engine, anim, "playState", (_, _) => JintInterop.Str(host?.PlayState(id) ?? "idle"));
        JintInterop.DefineAccessor(engine, anim, "playbackRate", (_, _) => JintInterop.Num(1));
        JintInterop.DefineAccessor(engine, anim, "id", (_, _) => JintInterop.Str(id < 0 ? "" : id.ToString(CultureInfo.InvariantCulture)));
        JintInterop.DefineAccessor(engine, anim, "finished", (_, _) => ResolvedPromise(engine, anim));
        JintInterop.DefineAccessor(engine, anim, "ready", (_, _) => ResolvedPromise(engine, anim));
        anim.FastSetProperty("onfinish", new PropertyDescriptor(JsValue.Null, writable: true, enumerable: true, configurable: true));
        anim.FastSetProperty("oncancel", new PropertyDescriptor(JsValue.Null, writable: true, enumerable: true, configurable: true));
        anim.FastSetProperty("effect", new PropertyDescriptor(BuildKeyframeEffect(ctx, keyframes, timing), writable: true, enumerable: true, configurable: true));

        foreach (var m in new[] { "addEventListener", "removeEventListener" })
        {
            JintInterop.DefineMethod(engine, anim, m, (_, _) => JsValue.Undefined, 2);
        }

        return anim;
    }

    private static JsObject BuildKeyframeEffect(
        JintBackendContext ctx, IReadOnlyList<AnimationKeyframeSpec> keyframes, AnimationEffectTimingSpec timing)
    {
        var engine = ctx.Engine;
        var effect = new JsObject(engine);

        JintInterop.DefineMethod(engine, effect, "getKeyframes", (_, _) =>
        {
            var items = new JsValue[keyframes.Count];
            for (var i = 0; i < keyframes.Count; i++)
            {
                var kf = keyframes[i];
                var o = new JsObject(engine);
                o.FastSetProperty("offset", new PropertyDescriptor(JintInterop.Num(kf.Offset), writable: true, enumerable: true, configurable: true));
                foreach (var d in kf.Declarations)
                {
                    o.FastSetProperty(KebabToCamel(d.Key), new PropertyDescriptor(JintInterop.Str(d.Value), writable: true, enumerable: true, configurable: true));
                }

                items[i] = o;
            }
            return new JsArray(engine, items);
        }, 0);
        JintInterop.DefineMethod(engine, effect, "getTiming", (_, _) => BuildTiming(engine, timing, computed: false), 0);
        JintInterop.DefineMethod(engine, effect, "getComputedTiming", (_, _) => BuildTiming(engine, timing, computed: true), 0);

        return effect;
    }

    private static JsObject BuildTiming(global::Jint.Engine engine, AnimationEffectTimingSpec t, bool computed)
    {
        var o = new JsObject(engine);
        void Num(string k, double v) => o.FastSetProperty(k, new PropertyDescriptor(JintInterop.Num(v), writable: true, enumerable: true, configurable: true));
        void Str(string k, string v) => o.FastSetProperty(k, new PropertyDescriptor(JintInterop.Str(v), writable: true, enumerable: true, configurable: true));
        Num("delay", t.DelayMs);
        Num("endDelay", 0);
        Num("duration", t.DurationMs);
        Num("iterations", t.Iterations);
        Num("iterationStart", 0);
        Str("direction", t.Direction);
        Str("fill", t.Fill);
        Str("easing", t.Easing);
        if (computed)
        {
            var active = t.DurationMs * (double.IsInfinity(t.Iterations) ? 1 : Math.Max(0, t.Iterations));
            Num("activeDuration", active);
            Num("endTime", t.DelayMs + active);
        }
        return o;
    }

    // ---- helpers ------------------------------------------------------------

    private static JsValue ResolvedPromise(global::Jint.Engine engine, JsValue value)
    {
        var (promise, resolve, _) = engine.Advanced.RegisterPromise();
        resolve(value);
        return promise;
    }

    private static IEnumerable<string> EnumKeys(ObjectInstance o)
    {
        foreach (var key in o.GetOwnPropertyKeys(Types.String))
        {
            if (!key.IsString())
            {
                continue;
            }

            var d = o.GetOwnProperty(key);
            if (d != PropertyDescriptor.Undefined && d.Enumerable)
            {
                yield return key.AsString();
            }
        }
    }

    private static string CssText(JsValue v)
    {
        if (v.IsObject())
        {
            var ts = v.AsObject().Get("toString");
            if (ts.IsCallable())
            {
                return TypeConverter.ToString(ts.Call(v, System.Array.Empty<JsValue>())).Trim();
            }
        }
        return TypeConverter.ToString(v).Trim();
    }

    private static string CamelToKebab(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c)) { if (i > 0) { sb.Append('-'); } sb.Append(char.ToLowerInvariant(c)); }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string KebabToCamel(string s)
    {
        if (s.IndexOf('-') < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        var upper = false;
        foreach (var c in s)
        {
            if (c == '-') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }
}
