using System.Buffers;
using System.Text;
using Starling.RegExp;
using Starling.Js.Runtime;
using Starling.Js.Runtime.Regex;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §22.2 The RegExp constructor and §22.2.6 RegExp.prototype. Wires the parser
/// → Pike-VM pipeline behind the JS-visible <c>new RegExp(…)</c>,
/// <c>RegExp.prototype.exec</c>, <c>.test</c>, and Symbol.* protocol hooks.
/// </summary>
public static class RegExpCtor
{
    public static void Install(JsRealm realm)
    {
        System.ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.RegExpPrototype;

        var ctor = new JsNativeFunction(realm, "RegExp", length: 2,
            (newTarget, args) => Construct(realm,
                IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto), args),
            isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));

        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));

        realm.RegExpBuiltinExec = IntrinsicHelpers.DefineMethod(realm, proto, "exec", 1, (thisV, args) => Exec(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "test", 1, (thisV, args) => Test(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "toString", 0,
            (thisV, _) => RegExpToString(realm, thisV));

        // Flag getters
        realm.RegExpGlobalGetter = DefineFlagGetter(realm, proto, "global", RegexFlags.Global);
        DefineFlagGetter(realm, proto, "ignoreCase", RegexFlags.IgnoreCase);
        DefineFlagGetter(realm, proto, "multiline", RegexFlags.Multiline);
        DefineFlagGetter(realm, proto, "dotAll", RegexFlags.DotAll);
        realm.RegExpUnicodeGetter = DefineFlagGetter(realm, proto, "unicode", RegexFlags.Unicode);
        DefineFlagGetter(realm, proto, "unicodeSets", RegexFlags.UnicodeSets);
        DefineFlagGetter(realm, proto, "sticky", RegexFlags.Sticky);
        DefineFlagGetter(realm, proto, "hasIndices", RegexFlags.HasIndices);

        DefineGetter(realm, proto, "source", (thisV) =>
        {
            var re = RequireRegExp(realm, thisV);
            return JsValue.String(re.Source);
        });
        DefineGetter(realm, proto, "flags", (thisV) =>
        {
            var re = RequireRegExp(realm, thisV);
            return JsValue.String(RegexFlagParser.ToFlagString(re.Flags));
        });

        // Symbol.match / Symbol.replace / Symbol.search / Symbol.split / Symbol.matchAll
        DefineSymbolMethod(realm, proto, SymbolCtor.Match, "[Symbol.match]", 1,
            (thisV, args) => SymbolMatch(realm, thisV, args));
        DefineSymbolMethod(realm, proto, SymbolCtor.MatchAll, "[Symbol.matchAll]", 1,
            (thisV, args) => SymbolMatchAll(realm, thisV, args));
        DefineSymbolMethod(realm, proto, SymbolCtor.Replace, "[Symbol.replace]", 2,
            (thisV, args) => SymbolReplace(realm, thisV, args));
        DefineSymbolMethod(realm, proto, SymbolCtor.Search, "[Symbol.search]", 1,
            (thisV, args) => SymbolSearch(realm, thisV, args));
        DefineSymbolMethod(realm, proto, SymbolCtor.Split, "[Symbol.split]", 2,
            (thisV, args) => SymbolSplit(realm, thisV, args));

        realm.RegExpBuiltinSymbolReplace = proto.Get(SymbolCtor.Replace).AsObject;

        realm.RegExpConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("RegExp",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    // ------------------------------------------------------------------
    //                       Constructor
    // ------------------------------------------------------------------
    private static JsValue Construct(JsRealm realm, JsObject instProto, JsValue[] args)
    {
        var patternArg = args.Length > 0 ? args[0] : JsValue.Undefined;
        var flagsArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        string source;
        RegexFlags flags;

        if (patternArg.IsObject && patternArg.AsObject is JsRegExp existing)
        {
            source = existing.Source;
            if (flagsArg.IsUndefined)
            {
                flags = existing.Flags;
            }
            else
            {
                if (!RegexFlagParser.TryParse(JsValue.ToStringValue(flagsArg), out flags, out var err))
                {
                    throw new JsThrow(realm.NewSyntaxError(err!));
                }
            }
        }
        else
        {
            source = patternArg.IsUndefined ? string.Empty : JsValue.ToStringValue(patternArg);
            var flagsStr = flagsArg.IsUndefined ? string.Empty : JsValue.ToStringValue(flagsArg);
            if (!RegexFlagParser.TryParse(flagsStr, out flags, out var err))
            {
                throw new JsThrow(realm.NewSyntaxError(err!));
            }
        }

        IRegexMatcher compiled;
        try
        {
            compiled = RegexBackendSelector.Compile(source, flags);
        }
        catch (RegexSyntaxException ex)
        {
            throw new JsThrow(realm.NewSyntaxError($"Invalid regular expression: /{source}/: {ex.Message}"));
        }
        var re = new JsRegExp(realm, compiled);
        if (!ReferenceEquals(instProto, realm.RegExpPrototype))
        {
            re.SetPrototypeOf(instProto);
        }

        return JsValue.Object(re);
    }

    // ------------------------------------------------------------------
    //                       prototype.exec / test
    // ------------------------------------------------------------------
    private static JsValue RegExpToString(JsRealm realm, JsValue thisV)
    {
        if (!thisV.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("RegExp.prototype.toString called on non-object"));
        }

        var obj = thisV.AsObject;
        var pattern = JsValue.ToStringValue(AbstractOperations.Get(realm.ActiveVm, obj, "source"));
        var flags = JsValue.ToStringValue(AbstractOperations.Get(realm.ActiveVm, obj, "flags"));
        return JsValue.String("/" + pattern + "/" + flags);
    }

    internal static JsValue Exec(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var re = RequireRegExp(realm, thisV);
        var input = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var start = 0;
        var advancing = (re.Flags & (RegexFlags.Global | RegexFlags.Sticky)) != 0;
        if (advancing)
        {
            start = (int)System.Math.Max(0, re.LastIndex);
            if (start > input.Length)
            {
                re.LastIndex = 0;
                return JsValue.Null;
            }
        }
        int bufLen = 2 * (re.Compiled.CaptureCount + 1);
        var spanBuffer = ArrayPool<int>.Shared.Rent(bufLen);
        try
        {
            if (!re.Compiled.ExecSpans(input, start, spanBuffer, out _, out var matchEnd))
            {
                if (advancing)
                {
                    re.LastIndex = 0;
                }

                return JsValue.Null;
            }
            if (advancing)
            {
                re.LastIndex = matchEnd;
            }

            return JsValue.Object(BuildMatchArrayFromSpans(realm, re, input, spanBuffer));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(spanBuffer);
        }
    }

    internal static JsValue Test(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var result = Exec(realm, thisV, args);
        return JsValue.Boolean(!result.IsNull);
    }

    // Public-internal helper so JsRegExpStringIterator can build per-match
    // arrays without rerouting through prototype.exec (avoids re-entering the
    // dispatcher just to construct the same object). The iterator already holds
    // a freshly-filled span buffer for the current match.
    internal static JsArray BuildMatchArrayForIterator(JsRealm realm, JsRegExp re, string input, int[] spanBuffer)
        => BuildMatchArrayFromSpans(realm, re, input, spanBuffer);

    // Build the §22.2.7.2 result array from a span buffer already filled by
    // IRegexMatcher.ExecSpans. spanBuffer holds (start,end) pairs for groups
    // 0..CaptureCount, with (-1,-1) for a non-participating group. Substrings
    // are cut from `input` only at the point a group's text is materialized,
    // and the d-flag indices block reads the same spans (no extra GroupSpan
    // calls, no re-exec).
    private static JsArray BuildMatchArrayFromSpans(JsRealm realm, JsRegExp re, string input, int[] spanBuffer)
    {
        int captureCount = re.Compiled.CaptureCount;
        // Pre-size: group 0 + each capture group.
        var arr = new JsArray(realm, captureCount + 1);
        // index 0 = full match, then group captures.
        int m0s = spanBuffer[0];
        int m0e = spanBuffer[1];
        arr.Push(JsValue.String(m0s < 0 ? string.Empty : input.Substring(m0s, m0e - m0s)));
        for (var i = 1; i <= captureCount; i++)
        {
            int gs = spanBuffer[i * 2];
            int ge = spanBuffer[i * 2 + 1];
            arr.Push(gs < 0 ? JsValue.Undefined : JsValue.String(input.Substring(gs, ge - gs)));
        }
        // index / input / groups
        arr.DefineOwnProperty("index",
            PropertyDescriptor.Data(JsValue.Number(m0s), writable: true, enumerable: true, configurable: true));
        arr.DefineOwnProperty("input",
            PropertyDescriptor.Data(JsValue.String(input), writable: true, enumerable: true, configurable: true));
        JsValue groups = JsValue.Undefined;
        if (re.Compiled.NamedCaptures.Count > 0)
        {
            var g = realm.NewOrdinaryObject();
            foreach (var (name, idx) in re.Compiled.NamedCaptures)
            {
                int gs = spanBuffer[idx * 2];
                int ge = spanBuffer[idx * 2 + 1];
                g.DefineOwnProperty(name,
                    PropertyDescriptor.Data(gs < 0 ? JsValue.Undefined : JsValue.String(input.Substring(gs, ge - gs)),
                        writable: true, enumerable: true, configurable: true));
            }
            groups = JsValue.Object(g);
        }
        arr.DefineOwnProperty("groups",
            PropertyDescriptor.Data(groups, writable: true, enumerable: true, configurable: true));

        if ((re.Flags & RegexFlags.HasIndices) != 0)
        {
            var indicesArr = new JsArray(realm, captureCount + 1);
            for (var i = 0; i <= captureCount; i++)
            {
                int gs = spanBuffer[i * 2];
                int ge = spanBuffer[i * 2 + 1];
                if (gs < 0)
                {
                    indicesArr.Push(JsValue.Undefined);
                }
                else
                {
                    var pair = new JsArray(realm, 2);
                    pair.Push(JsValue.Number(gs));
                    pair.Push(JsValue.Number(ge));
                    indicesArr.Push(JsValue.Object(pair));
                }
            }
            arr.DefineOwnProperty("indices",
                PropertyDescriptor.Data(JsValue.Object(indicesArr), writable: true, enumerable: true, configurable: true));
        }
        return arr;
    }

    // ------------------------------------------------------------------
    //                       Symbol.* protocols
    // ------------------------------------------------------------------
    // §22.2.6.8 RegExp.prototype [ @@match ] ( string ). Each match MUST be
    // obtained through RegExpExec(R, S) (§22.2.7.1), which honors the receiver's
    // (possibly overridden) `exec`; the global/unicode flags are read generically
    // via Get. The previous implementation re-matched through the internal
    // engine and ignored a user `exec` — core-js's DELEGATES_TO_EXEC
    // feature-detect saw that as a broken native and installed a recursing
    // @@match polyfill (jQuery↔core-js infinite recursion → RangeError).
    private static JsValue SymbolMatch(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        // Step 2: `this` must be an Object (need not be a genuine RegExp — a
        // user object whose `exec`/`global`/`unicode` we read is allowed).
        if (!thisV.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("RegExp.prototype[Symbol.match] called on non-object"));
        }

        var rx = thisV.AsObject;
        var vm = realm.ActiveVm;
        var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";

        // Step 5: global flag.
        bool global = JsValue.ToBoolean(AbstractOperations.Get(vm, rx, "global"));
        if (!global)
        {
            // Step 6: non-global — return RegExpExec(rx, S) directly.
            return RegExpExec(realm, rx, s);
        }

        // Step 7: global. Fast path — when rx is a genuine RegExp whose exec and
        // global/unicode getters are still the realm built-ins, no user code can
        // observe the per-match RegExpExec calls, so we may loop straight against
        // the compiled matcher pushing only each match's [0] (skipping the full
        // §22.2.7.2 result-array build). The instant any of those is overridden
        // we fall through to the generic loop, preserving the §22.2.7.1
        // DELEGATES_TO_EXEC contract (core-js's feature-detect).
        if (rx is JsRegExp fastRe && IsBuiltinExecAndFlags(realm, fastRe))
        {
            return GlobalMatchFastPath(realm, fastRe, s);
        }

        // Generic path: read the unicode flag, reset lastIndex, then loop
        // RegExpExec collecting each result's [0].
        bool fullUnicode = JsValue.ToBoolean(AbstractOperations.Get(vm, rx, "unicode"));
        AbstractOperations.Set(vm, rx, "lastIndex", JsValue.Number(0));
        var results = new JsArray(realm);
        while (true)
        {
            var result = RegExpExec(realm, rx, s);
            if (result.IsNull)
            {
                break;
            }

            var matchStr = JsValue.ToStringValue(
                AbstractOperations.Get(vm, result.AsObject, "0"));
            results.Push(JsValue.String(matchStr));
            // Step 7.g.iv: empty match → advance lastIndex so the loop terminates.
            if (matchStr.Length == 0)
            {
                var li = (int)ToLengthLocal(AbstractOperations.Get(vm, rx, "lastIndex"));
                AbstractOperations.Set(vm, rx, "lastIndex",
                    JsValue.Number(AdvanceStringIndex(s, li, fullUnicode)));
            }
        }
        return results.Length == 0 ? JsValue.Null : JsValue.Object(results);
    }

    // The @@match global fast path. Mirrors Exec's lastIndex bookkeeping exactly
    // (set 0 first, advance to match end per match, empty-match AdvanceStringIndex,
    // reset to 0 on no-match, return null when zero matches). Pushes only the
    // whole-match string per iteration; the full result array is never built.
    private static JsValue GlobalMatchFastPath(JsRealm realm, JsRegExp re, string s)
    {
        bool fullUnicode = (re.Flags & RegexFlags.Unicode) != 0;
        re.LastIndex = 0;
        var results = new JsArray(realm);
        int bufLen = 2 * (re.Compiled.CaptureCount + 1);
        var spanBuffer = ArrayPool<int>.Shared.Rent(bufLen);
        try
        {
            while (true)
            {
                var start = (int)System.Math.Max(0, re.LastIndex);
                if (start > s.Length)
                {
                    re.LastIndex = 0;
                    break;
                }
                if (!re.Compiled.ExecSpans(s, start, spanBuffer, out var matchStart, out var matchEnd))
                {
                    re.LastIndex = 0;
                    break;
                }
                re.LastIndex = matchEnd;
                int len = matchEnd - matchStart;
                results.Push(JsValue.String(len == 0 ? string.Empty : s.Substring(matchStart, len)));
                // Step 7.g.iv: empty match → advance lastIndex so the loop terminates.
                if (len == 0)
                {
                    var li = (int)re.LastIndex;
                    re.LastIndex = AdvanceStringIndex(s, li, fullUnicode);
                }
            }
            return results.Length == 0 ? JsValue.Null : JsValue.Object(results);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(spanBuffer);
        }
    }

    // True only when re.exec resolves to the realm's built-in exec and the
    // global/unicode flag getters resolve to the realm's built-in getters —
    // i.e. nothing on re or its prototype chain shadows them. Any override
    // disqualifies the fast path so the generic RegExpExec delegation runs.
    private static bool IsBuiltinExecAndFlags(JsRealm realm, JsRegExp re)
    {
        // Hot-path shortcut: a regexp that still carries the canonical
        // single-lastIndex shape owns no exec/global/unicode of its own, so
        // resolution falls straight to its prototype. If that prototype is the
        // realm's pristine RegExp.prototype with the builtin exec + flag getters
        // still in place, the guard holds without walking the chain or building a
        // descriptor per name. Anything off this path (own shadowing prop,
        // re-pointed __proto__, subclass) takes the full spec walk below.
        if (re.HasPristineShape && ReferenceEquals(re.GetPrototypeOf(), realm.RegExpPrototype)
            && ProtoBuiltinsIntact(realm))
        {
            return true;
        }

        return ResolvesToBuiltin(re, "exec", realm.RegExpBuiltinExec, accessor: false)
            && ResolvesToBuiltin(re, "global", realm.RegExpGlobalGetter, accessor: true)
            && ResolvesToBuiltin(re, "unicode", realm.RegExpUnicodeGetter, accessor: true);
    }

    // exec/global/unicode on RegExp.prototype still resolve to the builtins. The
    // prototype is dictionary-mode, so each lookup is a single hash probe with no
    // descriptor build beyond the returned struct; no prototype-chain walk.
    private static bool ProtoBuiltinsIntact(JsRealm realm)
    {
        // Steady state: the structural prototype-mutation epoch is unchanged
        // since we last confirmed the builtins intact. global/unicode are
        // accessors and exec is a method on RegExp.prototype; the only ways to
        // disturb them — defineProperty, delete, or migrating the prototype —
        // all bump ProtoEpoch. The single exception is a plain value
        // reassignment of the exec data property (`RegExp.prototype.exec = f`),
        // which rewrites a dictionary slot in place without a structural change,
        // so we re-probe exec (one hash lookup) even on an epoch hit. (We can't
        // make that write bump the epoch without adding cost to the very hot
        // generic dictionary-mode Set path, which globals in eval scope hit.)
        int epoch = JsObject.ProtoEpoch;
        if (epoch == realm.RegExpGuardCachedEpoch)
        {
            return realm.RegExpGuardCachedIntact && ExecStillBuiltin(realm);
        }

        bool fresh = ComputeProtoBuiltinsIntact(realm, realm.RegExpPrototype);
        realm.RegExpGuardCachedEpoch = epoch;
        realm.RegExpGuardCachedIntact = fresh;
        return fresh;
    }

    private static bool ExecStillBuiltin(JsRealm realm)
    {
        var ed = realm.RegExpPrototype.GetOwnPropertyDescriptor("exec");
        return ed is { IsAccessor: false } d && d.Value.IsObject
            && ReferenceEquals(d.Value.AsObject, realm.RegExpBuiltinExec);
    }

    private static bool ComputeProtoBuiltinsIntact(JsRealm realm, JsObject p)
    {
        if (!ExecStillBuiltin(realm))
        {
            return false;
        }

        var g = p.GetOwnPropertyDescriptor("global");
        if (g is not { IsAccessor: true } gd || !ReferenceEquals(gd.Getter, realm.RegExpGlobalGetter))
        {
            return false;
        }

        var u = p.GetOwnPropertyDescriptor("unicode");
        if (u is not { IsAccessor: true } ud || !ReferenceEquals(ud.Getter, realm.RegExpUnicodeGetter))
        {
            return false;
        }

        return true;
    }

    // Walk the own→prototype chain for `name`; the first object that owns it
    // wins. For an accessor property compare its getter to `expected`; for a
    // data property compare its value's object. Returns false if not found or
    // if it is the wrong kind, so a deleted/overridden slot fails the guard.
    private static bool ResolvesToBuiltin(JsObject start, string name, JsObject? expected, bool accessor)
    {
        if (expected is null)
        {
            return false;
        }

        for (var o = (JsObject?)start; o is not null; o = o.GetPrototypeOf())
        {
            var d = o.GetOwnPropertyDescriptor(name);
            if (d is null)
            {
                continue;
            }

            var desc = d.Value;
            if (accessor)
            {
                return desc.IsAccessor && ReferenceEquals(desc.Getter, expected);
            }

            return !desc.IsAccessor && desc.Value.IsObject && ReferenceEquals(desc.Value.AsObject, expected);
        }
        return false;
    }

    // §22.2.7.3 AdvanceStringIndex — step over a full code point when the
    // `unicode` flag is set and `index` sits on a leading surrogate.
    internal static int AdvanceStringIndex(string s, int index, bool unicode)
    {
        if (!unicode || index + 1 >= s.Length)
        {
            return index + 1;
        }

        var first = s[index];
        if (first < 0xD800 || first > 0xDBFF)
        {
            return index + 1;
        }

        var second = s[index + 1];
        if (second < 0xDC00 || second > 0xDFFF)
        {
            return index + 1;
        }

        return index + 2;
    }

    // §22.2.6.9 RegExp.prototype [ @@matchAll ] ( string ). Returns a
    // RegExpStringIterator (the same iterator String.prototype.matchAll
    // builds), so delegating String#matchAll through this method preserves the
    // iterator shape (Array.isArray(...) === false) the spec mandates.
    private static JsValue SymbolMatchAll(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var re = RequireRegExp(realm, thisV);
        if ((re.Flags & RegexFlags.Global) == 0)
        {
            throw new JsThrow(realm.NewTypeError("matchAll requires a global regular expression"));
        }

        var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var unicode = (re.Flags & RegexFlags.Unicode) != 0;
        return JsValue.Object(new JsRegExpStringIterator(realm, re, s, global: true, unicode: unicode));
    }

    // §22.2.7.1 RegExpExec ( R, S ): read R.exec; if callable, call it and
    // require an Object-or-null result; otherwise fall back to the built-in
    // RegExp.prototype.exec. Honoring the user-visible `exec` property is what
    // lets a subclass / monkeypatched exec drive @@replace/@@match/@@split —
    // core-js feature-detects this delegation (DELEGATES_TO_EXEC) and installs
    // a recursing polyfill when it is missing.
    internal static JsValue RegExpExec(JsRealm realm, JsObject r, string s)
    {
        var vm = realm.ActiveVm;
        var exec = AbstractOperations.Get(vm, r, "exec");
        if (AbstractOperations.IsCallable(exec))
        {
            var result = AbstractOperations.Call(vm, exec, JsValue.Object(r),
                new[] { JsValue.String(s) });
            if (!result.IsObject && !result.IsNull)
            {
                throw new JsThrow(realm.NewTypeError(
                    "RegExp exec method returned something other than an Object or null"));
            }

            return result;
        }
        // Fallback: must be a real RegExp to run the built-in.
        if (r is not JsRegExp)
        {
            throw new JsThrow(realm.NewTypeError("RegExp#exec called on incompatible receiver"));
        }

        return Exec(realm, JsValue.Object(r), new[] { JsValue.String(s) });
    }

    private static JsValue SymbolReplace(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        // §22.2.6.11 RegExp.prototype[@@replace]. `this` must be an Object but
        // need not be a genuine RegExp (it can be a user object whose `exec`
        // and `global` we read), so we do not RequireRegExp here.
        if (!thisV.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("RegExp.prototype[Symbol.replace] called on non-object"));
        }

        var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var replacement = args.Length > 1 ? args[1] : JsValue.Undefined;
        if (thisV.AsObject is JsRegExp re)
        {
            return ReplaceString(realm, re, s, replacement);
        }

        return ReplaceGeneric(realm, thisV.AsObject, s, replacement);
    }

    /// <summary>Direct entry for a genuine RegExp receiver — used by @@replace
    /// and by <c>String.prototype.replace</c>'s fast path so a
    /// <c>str.replace(/re/, "...")</c> call skips the @@replace lookup, the
    /// per-call argument array, and the native Call dispatch. Takes the
    /// span-based fast path when nothing is overridden, else the generic loop.</summary>
    internal static JsValue ReplaceString(JsRealm realm, JsRegExp re, string s, JsValue replacement)
    {
        bool functional = AbstractOperations.IsCallable(replacement);
        string replStr = functional ? null! : JsValue.ToStringValue(replacement);
        if ((re.Flags & RegexFlags.Sticky) == 0 && re.Compiled.NamedCaptures.Count == 0
            && IsBuiltinExecAndFlags(realm, re))
        {
            return ReplaceFast(realm, re, s, replacement, functional, replStr);
        }
        return ReplaceGeneric(realm, re, s, replacement);
    }

    // §22.2.6.11 generic path — honors a user-overridden exec or a non-RegExp
    // receiver: gathers matches via RegExpExec, then applies GetSubstitution.
    private static JsValue ReplaceGeneric(JsRealm realm, JsObject rx, string s, JsValue replacement)
    {
        var vm = realm.ActiveVm;
        bool functional = AbstractOperations.IsCallable(replacement);
        string replStr = functional ? null! : JsValue.ToStringValue(replacement);
        bool global = JsValue.ToBoolean(AbstractOperations.Get(vm, rx, "global"));
        if (global)
        {
            rx.Set("lastIndex", JsValue.Number(0));
        }

        // Step 14: gather every match through RegExpExec (which calls rx.exec).
        var results = new List<JsValue>();
        while (true)
        {
            var result = RegExpExec(realm, rx, s);
            if (result.IsNull)
            {
                break;
            }

            results.Add(result);
            if (!global)
            {
                break;
            }
            // Empty-match advance (§22.2.6.11 step 14.d.iii).
            var resObj = result.AsObject;
            var matchStr = JsValue.ToStringValue(AbstractOperations.Get(vm, resObj, "0"));
            if (matchStr.Length == 0)
            {
                var li = (int)ToLengthLocal(AbstractOperations.Get(vm, rx, "lastIndex"));
                bool fullUnicode = JsValue.ToBoolean(AbstractOperations.Get(vm, rx, "unicode"));
                rx.Set("lastIndex", JsValue.Number(AdvanceStringIndex(s, li, fullUnicode)));
            }
        }

        var sb = new StringBuilder();
        int nextSourcePosition = 0;
        foreach (var result in results)
        {
            var resObj = result.AsObject;
            var matched = JsValue.ToStringValue(AbstractOperations.Get(vm, resObj, "0"));
            int matchLength = matched.Length;
            // position = clamp(ToInteger(result.index), 0, s.Length)
            var rawIndex = (int)JsValue.ToNumber(AbstractOperations.Get(vm, resObj, "index"));
            int position = System.Math.Max(0, System.Math.Min(rawIndex, s.Length));
            // Captures: result[1 .. length-1].
            int nCaptures = System.Math.Max(0,
                (int)ToLengthLocal(AbstractOperations.Get(vm, resObj, "length")) - 1);
            var captures = new List<JsValue>(nCaptures);
            for (var n = 1; n <= nCaptures; n++)
            {
                var cap = AbstractOperations.Get(vm, resObj, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                captures.Add(cap.IsUndefined ? JsValue.Undefined : JsValue.String(JsValue.ToStringValue(cap)));
            }
            var namedCaptures = AbstractOperations.Get(vm, resObj, "groups");

            string replacementText;
            if (functional)
            {
                var fnArgs = new List<JsValue> { JsValue.String(matched) };
                fnArgs.AddRange(captures);
                fnArgs.Add(JsValue.Number(position));
                fnArgs.Add(JsValue.String(s));
                if (!namedCaptures.IsUndefined)
                {
                    fnArgs.Add(namedCaptures);
                }

                var r = AbstractOperations.Call(vm, replacement, JsValue.Undefined, fnArgs.ToArray());
                replacementText = JsValue.ToStringValue(r);
            }
            else
            {
                replacementText = GetSubstitution(realm, replStr, matched, s, position, captures, namedCaptures);
            }

            if (position >= nextSourcePosition)
            {
                sb.Append(s, nextSourcePosition, position - nextSourcePosition);
                sb.Append(replacementText);
                nextSourcePosition = position + matchLength;
            }
        }
        if (nextSourcePosition < s.Length)
        {
            sb.Append(s, nextSourcePosition, s.Length - nextSourcePosition);
        }

        return JsValue.String(sb.ToString());
    }

    // Fast @@replace for a built-in, non-sticky RegExp with no named captures.
    // One forward scan via ExecSpans; the result is assembled in a single
    // StringBuilder with no per-match allocation (no exec-result object, no
    // captures list, no property-bag reads). Observable behavior is identical to
    // the generic loop above for this class of receiver.
    private static JsValue ReplaceFast(JsRealm realm, JsRegExp re, string s, JsValue replacement, bool functional, string? replStr)
    {
        var vm = realm.ActiveVm;
        bool global = (re.Flags & RegexFlags.Global) != 0;
        bool fullUnicode = (re.Flags & RegexFlags.Unicode) != 0;
        int captureCount = re.Compiled.CaptureCount;
        // §22.2.6.11 step 12: a global replace sets lastIndex to 0 (and the
        // built-in scan leaves it 0 after the terminal no-match). Non-global,
        // non-sticky exec never touches lastIndex. The pristine-shape case (the
        // norm: this method is only reached when the builtin guard held) writes
        // slot 0 directly, skipping the generic setter's name lookup.
        if (global) { if (re.HasPristineShape)
            {
                re.ResetLastIndexFast();
            }
            else
            {
                re.LastIndex = 0;
            }
        }

        // Literal replacement (no '$' substitution tokens) on a backend that can
        // do a single-pass whole-string replace (the .NET engine): delegate the
        // whole operation, skipping the per-match scan and its allocations. The
        // Pike VM returns null here so its (already light) scan runs below.
        if (!functional && replStr is not null && replStr.IndexOf('$') < 0)
        {
            var whole = re.Compiled.TryReplaceLiteral(s, replStr, global);
            if (whole is not null)
            {
                return JsValue.String(whole);
            }
        }

        var spans = ArrayPool<int>.Shared.Rent(2 * (captureCount + 1));
        try
        {
            StringBuilder? sb = null;
            int nextSourcePosition = 0;
            int pos = 0;
            while (re.Compiled.ExecSpans(s, pos, spans, out int matchStart, out int matchEnd))
            {
                int matchLen = matchEnd - matchStart;
                string replacementText = functional
                    ? CallReplaceFn(vm, replacement, s, matchStart, matchEnd, spans, captureCount)
                    : GetSubstitutionFast(replStr!, s, matchStart, matchEnd, spans, captureCount);

                // Forward scan ⇒ matchStart >= nextSourcePosition always.
                sb ??= new StringBuilder(s.Length + 16);
                sb.Append(s, nextSourcePosition, matchStart - nextSourcePosition);
                sb.Append(replacementText);
                nextSourcePosition = matchStart + matchLen;

                if (!global)
                {
                    break;
                }

                pos = matchLen == 0 ? AdvanceStringIndex(s, matchEnd, fullUnicode) : matchEnd;
                if (pos > s.Length)
                {
                    break;
                }
            }
            if (sb is null)
            {
                return JsValue.String(s); // no match → original string, no allocation
            }

            if (nextSourcePosition < s.Length)
            {
                sb.Append(s, nextSourcePosition, s.Length - nextSourcePosition);
            }

            return JsValue.String(sb.ToString());
        }
        finally
        {
            ArrayPool<int>.Shared.Return(spans);
        }
    }

    // Build the (matched, cap1..capN, position, string) argument list for a
    // functional replacement directly from spans (no named-captures arg — the
    // fast path only runs when there are none).
    private static string CallReplaceFn(JsVm? vm, JsValue replacement, string s, int matchStart, int matchEnd, int[] spans, int captureCount)
    {
        var fnArgs = new JsValue[captureCount + 3];
        fnArgs[0] = JsValue.String(s.Substring(matchStart, matchEnd - matchStart));
        for (var c = 1; c <= captureCount; c++)
        {
            int cs = spans[2 * c], ce = spans[2 * c + 1];
            fnArgs[c] = cs < 0 ? JsValue.Undefined : JsValue.String(s.Substring(cs, ce - cs));
        }
        fnArgs[captureCount + 1] = JsValue.Number(matchStart);
        fnArgs[captureCount + 2] = JsValue.String(s);
        return JsValue.ToStringValue(AbstractOperations.Call(vm, replacement, JsValue.Undefined, fnArgs));
    }

    // §22.2.6.11.1 GetSubstitution working directly off match spans + the source
    // string (no captures list, no exec-result object). $<name> is treated as
    // literal — the fast path only runs when the regex has no named captures.
    private static string GetSubstitutionFast(string replacement, string str, int matchStart, int matchEnd, int[] spans, int captureCount)
    {
        // No '$' ⇒ the replacement is literal (the common case, e.g. "0"); skip
        // the StringBuilder entirely.
        if (replacement.IndexOf('$') < 0)
        {
            return replacement;
        }

        int position = matchStart;
        int tailPos = matchEnd;
        var sb = new StringBuilder(replacement.Length + 8);
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] != '$' || i + 1 >= replacement.Length) { sb.Append(replacement[i]); continue; }
            var next = replacement[i + 1];
            switch (next)
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(str, matchStart, matchEnd - matchStart); i++; break;
                case '`': sb.Append(str, 0, position); i++; break;
                case '\'': sb.Append(str, tailPos, str.Length - tailPos); i++; break;
                // No named captures here, so $<name> is literal $<name> (GetSubstitution
                // §22.2.6.11.1: refReplacement is "$<" when namedCaptures is undefined).
                // Emit "$<" and let the loop copy the rest of the token verbatim — emitting
                // only "$" would drop the "<" and corrupt the replacement (e.g. "$<x>"→"$x>").
                case '<': sb.Append('$').Append('<'); i++; break;
                default:
                    if (next >= '0' && next <= '9')
                    {
                        int n = next - '0';
                        int consumed = 1;
                        if (i + 2 < replacement.Length && replacement[i + 2] >= '0' && replacement[i + 2] <= '9')
                        {
                            var n2 = n * 10 + (replacement[i + 2] - '0');
                            if (n2 >= 1 && n2 <= captureCount) { n = n2; consumed = 2; }
                        }
                        if (n >= 1 && n <= captureCount)
                        {
                            int cs = spans[2 * n], ce = spans[2 * n + 1];
                            if (cs >= 0)
                            {
                                sb.Append(str, cs, ce - cs);
                            }

                            i += consumed;
                        }
                        else { sb.Append('$').Append(next); i++; }
                    }
                    else { sb.Append('$').Append(next); i++; }
                    break;
            }
        }
        return sb.ToString();
    }

    private static double ToLengthLocal(JsValue v)
    {
        var n = JsValue.ToNumber(v);
        if (double.IsNaN(n) || n <= 0)
        {
            return 0;
        }

        return System.Math.Min(System.Math.Floor(n), 9007199254740991d);
    }

    // §22.2.6.11.1 GetSubstitution — operates on the captures list + namedCaptures
    // object pulled from the exec result, per spec (not on the raw RegexMatch).
    private static string GetSubstitution(JsRealm realm, string replacement, string matched,
        string str, int position, List<JsValue> captures, JsValue namedCaptures)
    {
        var vm = realm.ActiveVm;
        int tailPos = position + matched.Length;
        int m = captures.Count;
        var sb = new StringBuilder();
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] != '$' || i + 1 >= replacement.Length)
            {
                sb.Append(replacement[i]);
                continue;
            }
            var next = replacement[i + 1];
            switch (next)
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(matched); i++; break;
                case '`': sb.Append(str, 0, position); i++; break;
                case '\'': sb.Append(str, tailPos, str.Length - tailPos); i++; break;
                case '<':
                    {
                        // Named captures only honored when groups is not undefined
                        // (§22.2.6.11.1 step 11). Otherwise "$<" is literal.
                        if (namedCaptures.IsUndefined) { sb.Append('$'); i++; break; }
                        var close = replacement.IndexOf('>', i + 2);
                        if (close < 0) { sb.Append('$'); i++; break; }
                        var name = replacement.Substring(i + 2, close - (i + 2));
                        var groupsObj = AbstractOperations.ToObject(realm, namedCaptures);
                        var cap = AbstractOperations.Get(vm, groupsObj, name);
                        if (!cap.IsUndefined)
                        {
                            sb.Append(JsValue.ToStringValue(cap));
                        }

                        i = close;
                        break;
                    }
                default:
                    if (next >= '0' && next <= '9')
                    {
                        int n = next - '0';
                        int consumed = 1;
                        if (i + 2 < replacement.Length && replacement[i + 2] >= '0' && replacement[i + 2] <= '9')
                        {
                            var n2 = n * 10 + (replacement[i + 2] - '0');
                            if (n2 >= 1 && n2 <= m) { n = n2; consumed = 2; }
                        }
                        if (n >= 1 && n <= m)
                        {
                            var cap = captures[n - 1];
                            if (!cap.IsUndefined)
                            {
                                sb.Append(JsValue.ToStringValue(cap));
                            }

                            i += consumed;
                        }
                        else
                        {
                            sb.Append('$').Append(next);
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append('$').Append(next);
                        i++;
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static JsValue SymbolSearch(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var re = RequireRegExp(realm, thisV);
        var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        // search ignores lastIndex; it always starts at 0 and does not advance.
        var savedLastIndex = re.LastIndex;
        re.LastIndex = 0;
        var m = re.Compiled.Exec(s, 0);
        re.LastIndex = savedLastIndex;
        return JsValue.Number(m is null ? -1 : m.Start);
    }

    private static JsValue SymbolSplit(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var re = RequireRegExp(realm, thisV);
        var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
        var limit = args.Length > 1 && !args[1].IsUndefined ? (uint)System.Math.Max(0, (int)JsValue.ToNumber(args[1])) : uint.MaxValue;
        var arr = new JsArray(realm);
        if (limit == 0)
        {
            return JsValue.Object(arr);
        }

        int captureCount = re.Compiled.CaptureCount;
        bool unicode = (re.Flags & RegexFlags.Unicode) != 0;
        int bufLen = 2 * (captureCount + 1);
        var spanBuffer = ArrayPool<int>.Shared.Rent(bufLen);
        try
        {
            if (s.Length == 0)
            {
                // Per spec, if pattern matches empty string return [], else [""].
                if (!re.Compiled.ExecSpans(s, 0, spanBuffer, out _, out _))
                {
                    arr.Push(JsValue.String(string.Empty));
                }

                return JsValue.Object(arr);
            }
            int pos = 0;
            int prev = 0;
            while (pos < s.Length)
            {
                if (!re.Compiled.ExecSpans(s, pos, spanBuffer, out var matchStart, out var matchEnd))
                {
                    break;
                }

                if (matchEnd == prev)
                {
                    pos = AdvanceStringIndex(s, pos, unicode);
                    continue;
                }
                arr.Push(JsValue.String(s[prev..matchStart]));
                if (arr.Length >= limit)
                {
                    return JsValue.Object(arr);
                }
                // Push captures — substring only for participating groups.
                for (var i = 1; i <= captureCount; i++)
                {
                    int gs = spanBuffer[i * 2];
                    int ge = spanBuffer[i * 2 + 1];
                    arr.Push(gs < 0 ? JsValue.Undefined : JsValue.String(s.Substring(gs, ge - gs)));
                    if (arr.Length >= limit)
                    {
                        return JsValue.Object(arr);
                    }
                }
                prev = matchEnd;
                pos = matchEnd;
            }
            arr.Push(JsValue.String(s[prev..]));
            return JsValue.Object(arr);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(spanBuffer);
        }
    }

    // ------------------------------------------------------------------
    //                          Helpers
    // ------------------------------------------------------------------

    /// <summary>Compile a JS pattern string with default flags. Used by
    /// String.prototype methods to coerce non-RegExp first arguments.</summary>
    public static JsRegExp Create(JsRealm realm, string source, string flags = "")
    {
        if (!RegexFlagParser.TryParse(flags, out var f, out var err))
        {
            throw new JsThrow(realm.NewSyntaxError(err!));
        }

        IRegexMatcher compiled;
        try
        {
            compiled = RegexBackendSelector.Compile(source, f);
        }
        catch (RegexSyntaxException ex)
        {
            throw new JsThrow(realm.NewSyntaxError($"Invalid regular expression: /{source}/: {ex.Message}"));
        }
        return new JsRegExp(realm, compiled);
    }

    public static bool IsRegExp(JsValue v) => v.IsObject && v.AsObject is JsRegExp;

    private static JsRegExp RequireRegExp(JsRealm realm, JsValue v)
    {
        if (v.IsObject && v.AsObject is JsRegExp r)
        {
            return r;
        }

        throw new JsThrow(realm.NewTypeError("Method called on non-RegExp object"));
    }

    private static JsNativeFunction DefineFlagGetter(JsRealm realm, JsObject target, string name, RegexFlags flag)
    {
        return DefineGetter(realm, target, name, (thisV) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsRegExp r)
            {
                return JsValue.Boolean((r.Flags & flag) != 0);
            }

            throw new JsThrow(realm.NewTypeError($"Getter '{name}' called on non-RegExp"));
        });
    }

    private static JsNativeFunction DefineGetter(JsRealm realm, JsObject target, string name, System.Func<JsValue, JsValue> body)
    {
        var fn = new JsNativeFunction(realm, "get " + name, 0, (thisV, _) => body(thisV), isConstructor: false);
        target.DefineOwnProperty(name, PropertyDescriptor.Accessor(fn, null, enumerable: false, configurable: true));
        return fn;
    }

    private static void DefineSymbolMethod(JsRealm realm, JsObject target, JsSymbol key, string name, int length, System.Func<JsValue, JsValue[], JsValue> body)
    {
        var fn = new JsNativeFunction(realm, name, length, body, isConstructor: false);
        target.DefineOwnProperty(key, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }
}
