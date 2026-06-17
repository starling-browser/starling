using System.Globalization;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// Compatibility-lite ECMA-402 surface for bundles that feature-detect Intl.
/// This is intentionally small and deterministic: UTC dates, a default en-US
/// locale, and .NET globalization-backed formatting for valid locale tags.
/// </summary>
public static class IntlObj
{
    private const string DefaultLocale = "en-US";
    private const string DefaultCalendar = "gregory";
    private const string DefaultNumberingSystem = "latn";
    private const string DefaultTimeZone = "UTC";

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        var intl = realm.NewOrdinaryObject();
        var dateTimeFormatProto = realm.NewOrdinaryObject();
        var numberFormatProto = realm.NewOrdinaryObject();
        var collatorProto = realm.NewOrdinaryObject();
        var localeProto = realm.NewOrdinaryObject();

        var dateTimeFormatCtor = CreateDateTimeFormatCtor(realm, dateTimeFormatProto);
        var numberFormatCtor = CreateNumberFormatCtor(realm, numberFormatProto);
        var collatorCtor = CreateCollatorCtor(realm, collatorProto);
        var localeCtor = CreateLocaleCtor(realm, localeProto);

        DefineData(intl, "DateTimeFormat", JsValue.Object(dateTimeFormatCtor));
        DefineData(intl, "NumberFormat", JsValue.Object(numberFormatCtor));
        DefineData(intl, "Collator", JsValue.Object(collatorCtor));
        DefineData(intl, "Locale", JsValue.Object(localeCtor));
        IntrinsicHelpers.DefineMethod(realm, intl, "getCanonicalLocales", 1,
            (_, args) => MakeLocaleArray(realm, ReadRequestedLocales(realm, args.Length > 0 ? args[0] : JsValue.Undefined)));
        IntrinsicHelpers.DefineMethod(realm, intl, "supportedValuesOf", 1,
            (_, args) => SupportedValuesOf(realm, args.Length > 0 ? args[0] : JsValue.Undefined));
        intl.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Intl"), writable: false, enumerable: false, configurable: true));

        realm.GlobalObject.DefineOwnProperty("Intl",
            PropertyDescriptor.Data(JsValue.Object(intl), writable: true, enumerable: false, configurable: true));
    }

    private static JsNativeFunction CreateDateTimeFormatCtor(JsRealm realm, JsObject proto)
    {
        var ctor = new JsNativeFunction(realm, "DateTimeFormat", 2, (newTarget, args) =>
        {
            var state = CreateDateTimeFormatState(realm, args);
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            return JsValue.Object(CreateDateTimeFormatInstance(realm, instProto, state));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        IntrinsicHelpers.DefineMethod(realm, ctor, "supportedLocalesOf", 1,
            (_, args) => SupportedLocalesOf(realm, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "format", 1,
            (thisV, args) => JsValue.String(RequireDateTimeFormat(realm, thisV).Format(realm, args.Length > 0 ? args[0] : JsValue.Undefined)));
        IntrinsicHelpers.DefineMethod(realm, proto, "resolvedOptions", 0,
            (thisV, _) => RequireDateTimeFormat(realm, thisV).ResolvedOptions(realm));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Intl.DateTimeFormat"), writable: false, enumerable: false, configurable: true));
        return ctor;
    }

    private static JsNativeFunction CreateNumberFormatCtor(JsRealm realm, JsObject proto)
    {
        var ctor = new JsNativeFunction(realm, "NumberFormat", 2, (newTarget, args) =>
        {
            var state = CreateNumberFormatState(realm, args);
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            return JsValue.Object(CreateNumberFormatInstance(realm, instProto, state));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        IntrinsicHelpers.DefineMethod(realm, ctor, "supportedLocalesOf", 1,
            (_, args) => SupportedLocalesOf(realm, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "format", 1,
            (thisV, args) => JsValue.String(RequireNumberFormat(realm, thisV).Format(args.Length > 0 ? args[0] : JsValue.Undefined)));
        IntrinsicHelpers.DefineMethod(realm, proto, "resolvedOptions", 0,
            (thisV, _) => RequireNumberFormat(realm, thisV).ResolvedOptions(realm));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Intl.NumberFormat"), writable: false, enumerable: false, configurable: true));
        return ctor;
    }

    private static JsNativeFunction CreateCollatorCtor(JsRealm realm, JsObject proto)
    {
        var ctor = new JsNativeFunction(realm, "Collator", 2, (newTarget, args) =>
        {
            var state = CreateCollatorState(realm, args);
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            return JsValue.Object(CreateCollatorInstance(realm, instProto, state));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        IntrinsicHelpers.DefineMethod(realm, ctor, "supportedLocalesOf", 1,
            (_, args) => SupportedLocalesOf(realm, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "compare", 2,
            (thisV, args) => JsValue.Number(RequireCollator(realm, thisV).Compare(args)));
        IntrinsicHelpers.DefineMethod(realm, proto, "resolvedOptions", 0,
            (thisV, _) => RequireCollator(realm, thisV).ResolvedOptions(realm));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Intl.Collator"), writable: false, enumerable: false, configurable: true));
        return ctor;
    }

    private static JsNativeFunction CreateLocaleCtor(JsRealm realm, JsObject proto)
    {
        var ctor = new JsNativeFunction(realm, "Locale", 1, (newTarget, args) =>
        {
            if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
            {
                throw new JsThrow(realm.NewTypeError("Intl.Locale requires 'new'"));
            }

            var state = CreateLocaleState(
                realm,
                args.Length > 0 ? args[0] : JsValue.Undefined,
                args.Length > 1 ? args[1] : JsValue.Undefined);
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            return JsValue.Object(CreateLocaleInstance(realm, instProto, state));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        IntrinsicHelpers.DefineMethod(realm, proto, "toString", 0,
            (thisV, _) => JsValue.String(RequireLocale(realm, thisV).State.Name));
        IntrinsicHelpers.DefineMethod(realm, proto, "minimize", 0,
            (thisV, _) => JsValue.Object(CreateLocaleInstance(
                realm,
                proto,
                RequireLocale(realm, thisV).State.Minimized())));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Intl.Locale"), writable: false, enumerable: false, configurable: true));
        return ctor;
    }

    private static IntlDateTimeFormatObject CreateDateTimeFormatInstance(JsRealm realm, JsObject proto, IntlDateTimeFormatState state)
    {
        var obj = new IntlDateTimeFormatObject(proto, state);
        var format = new JsNativeFunction(realm, "format", 1,
            (_, args) => JsValue.String(obj.Format(realm, args.Length > 0 ? args[0] : JsValue.Undefined)), isConstructor: false);
        obj.DefineOwnProperty("format", PropertyDescriptor.BuiltinMethod(JsValue.Object(format)));
        return obj;
    }

    private static IntlNumberFormatObject CreateNumberFormatInstance(JsRealm realm, JsObject proto, IntlNumberFormatState state)
    {
        var obj = new IntlNumberFormatObject(proto, state);
        var format = new JsNativeFunction(realm, "format", 1,
            (_, args) => JsValue.String(obj.Format(args.Length > 0 ? args[0] : JsValue.Undefined)), isConstructor: false);
        obj.DefineOwnProperty("format", PropertyDescriptor.BuiltinMethod(JsValue.Object(format)));
        return obj;
    }

    private static IntlCollatorObject CreateCollatorInstance(JsRealm realm, JsObject proto, IntlCollatorState state)
    {
        var obj = new IntlCollatorObject(proto, state);
        var compare = new JsNativeFunction(realm, "compare", 2,
            (_, args) => JsValue.Number(obj.Compare(args)), isConstructor: false);
        obj.DefineOwnProperty("compare", PropertyDescriptor.BuiltinMethod(JsValue.Object(compare)));
        return obj;
    }

    private static IntlLocaleObject CreateLocaleInstance(JsRealm realm, JsObject proto, IntlLocaleState state)
    {
        var obj = new IntlLocaleObject(proto, state);
        obj.Set("baseName", JsValue.String(state.BaseName));
        obj.Set("language", JsValue.String(state.Language));
        obj.Set("script", state.Script is null ? JsValue.Undefined : JsValue.String(state.Script));
        obj.Set("region", state.Region is null ? JsValue.Undefined : JsValue.String(state.Region));
        obj.Set("calendar", state.Calendar is null ? JsValue.Undefined : JsValue.String(state.Calendar));
        obj.Set("hourCycle", state.HourCycle is null ? JsValue.Undefined : JsValue.String(state.HourCycle));
        obj.Set("numeric", JsValue.Boolean(state.Numeric));
        return obj;
    }

    private static IntlDateTimeFormatState CreateDateTimeFormatState(JsRealm realm, JsValue[] args)
    {
        var locale = ResolveLocale(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject : null;
        var year = GetStringOption(realm, options, "year", "numeric", "2-digit");
        var month = GetStringOption(realm, options, "month", "numeric", "2-digit", "short", "long");
        var day = GetStringOption(realm, options, "day", "numeric", "2-digit");
        var hour = GetStringOption(realm, options, "hour", "numeric", "2-digit");
        var minute = GetStringOption(realm, options, "minute", "numeric", "2-digit");
        var second = GetStringOption(realm, options, "second", "numeric", "2-digit");
        var hasAnyField = year is not null || month is not null || day is not null || hour is not null || minute is not null || second is not null;
        if (!hasAnyField)
        {
            year = "numeric";
            month = "numeric";
            day = "numeric";
        }
        var hour12 = GetBooleanOption(realm, options, "hour12") ?? false;
        return new IntlDateTimeFormatState(locale, year, month, day, hour, minute, second, hour12);
    }

    private static IntlNumberFormatState CreateNumberFormatState(JsRealm realm, JsValue[] args)
    {
        var locale = ResolveLocale(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject : null;
        var style = GetStringOption(realm, options, "style", "decimal", "percent", "currency") ?? "decimal";
        var defaultDigits = style == "currency" ? 2 : style == "percent" ? 0 : 3;
        var minDefault = style == "currency" ? 2 : 0;
        var minFraction = GetNumberOption(realm, options, "minimumFractionDigits", 0, 20, minDefault);
        var maxFraction = GetNumberOption(realm, options, "maximumFractionDigits", 0, 20, defaultDigits);
        if (maxFraction < minFraction)
        {
            maxFraction = minFraction;
        }

        var currency = GetStringOption(realm, options, "currency") ?? "USD";
        var useGrouping = GetBooleanOption(realm, options, "useGrouping") ?? true;
        return new IntlNumberFormatState(locale, style, currency.ToUpperInvariant(), minFraction, maxFraction, useGrouping);
    }

    private static IntlCollatorState CreateCollatorState(JsRealm realm, JsValue[] args)
    {
        var locale = ResolveLocale(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject : null;
        var usage = GetStringOption(realm, options, "usage", "sort", "search") ?? "sort";
        var sensitivity = GetStringOption(realm, options, "sensitivity", "base", "accent", "case", "variant") ?? "variant";
        var numeric = GetBooleanOption(realm, options, "numeric") ?? false;
        return new IntlCollatorState(locale, usage, sensitivity, numeric);
    }

    private static IntlLocaleState CreateLocaleState(JsRealm realm, JsValue tagValue, JsValue optionsValue)
    {
        var tag = AbstractOperations.ToStringJs(realm.ActiveVm, tagValue);
        var state = ParseLocaleState(realm, tag);
        var options = optionsValue.IsObject ? optionsValue.AsObject : null;
        var calendar = GetStringOption(realm, options, "calendar") ?? state.Calendar;
        var hourCycle = GetStringOption(realm, options, "hourCycle", "h11", "h12", "h23", "h24") ?? state.HourCycle;
        var numeric = GetBooleanOption(realm, options, "numeric") ?? state.Numeric;
        return state with
        {
            Calendar = calendar,
            HourCycle = hourCycle,
            Numeric = numeric,
            Name = BuildLocaleName(state.BaseName, calendar, hourCycle, numeric)
        };
    }

    private static JsValue SupportedLocalesOf(JsRealm realm, JsValue[] args)
        => MakeLocaleArray(realm, ReadRequestedLocales(realm, args.Length > 0 ? args[0] : JsValue.Undefined));

    private static JsValue SupportedValuesOf(JsRealm realm, JsValue keyValue)
    {
        var key = AbstractOperations.ToStringJs(realm.ActiveVm, keyValue);
        var values = key switch
        {
            "calendar" => new[] { "gregory", "buddhist", "chinese", "iso8601" },
            "collation" => new[] { "default", "emoji", "eor" },
            "currency" => new[] { "EUR", "GBP", "JPY", "USD" },
            "numberingSystem" => new[] { "latn", "arab", "hanidec" },
            "timeZone" => new[] { "UTC", "America/New_York", "Europe/London" },
            "unit" => new[] { "meter", "second", "kilometer", "byte" },
            _ => throw new JsThrow(realm.NewRangeError("invalid key for Intl.supportedValuesOf"))
        };
        return MakeStringArray(realm, values);
    }

    private static JsValue MakeLocaleArray(JsRealm realm, List<string> locales)
        => MakeStringArray(realm, locales);

    private static JsValue MakeStringArray(JsRealm realm, IReadOnlyList<string> values)
    {
        var arr = new JsArray(realm);
        for (var i = 0; i < values.Count; i++)
        {
            arr.Push(JsValue.String(values[i]));
        }

        return JsValue.Object(arr);
    }

    private static List<string> ReadRequestedLocales(JsRealm realm, JsValue value)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (value.IsUndefined)
        {
            return result;
        }

        if (value.IsString)
        {
            AddSupportedLocale(value.AsString, result, seen);
            return result;
        }
        if (!value.IsObject)
        {
            return result;
        }

        var obj = value.AsObject;
        var length = ToLength(AbstractOperations.Get(realm.ActiveVm, obj, "length"));
        for (var i = 0; i < length; i++)
        {
            var item = AbstractOperations.Get(realm.ActiveVm, obj, i.ToString(CultureInfo.InvariantCulture));
            if (item.IsUndefined)
            {
                continue;
            }

            AddSupportedLocale(AbstractOperations.ToStringJs(realm.ActiveVm, item), result, seen);
        }
        return result;
    }

    private static void AddSupportedLocale(string requested, List<string> result, HashSet<string> seen)
    {
        if (!TryCreateLocale(requested, out var locale))
        {
            return;
        }

        if (seen.Add(locale.Name))
        {
            result.Add(locale.Name);
        }
    }

    private static IntlLocale ResolveLocale(JsRealm realm, JsValue value)
    {
        var requested = ReadRequestedLocales(realm, value);
        if (requested.Count > 0 && TryCreateLocale(requested[0], out var locale))
        {
            return locale;
        }

        return TryCreateLocale(DefaultLocale, out var defaultLocale)
            ? defaultLocale
            : new IntlLocale(DefaultLocale, CultureInfo.InvariantCulture);
    }

    private static bool TryCreateLocale(string locale, out IntlLocale result)
    {
        result = new IntlLocale(DefaultLocale, CultureInfo.InvariantCulture);
        var normalized = CanonicalLocaleName(NormalizeLocale(locale));
        if (normalized.Length == 0)
        {
            return false;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(normalized);
            var name = string.IsNullOrEmpty(culture.Name) ? normalized : culture.Name;
            result = new IntlLocale(name, culture);
            return true;
        }
        catch (CultureNotFoundException)
        {
            if (!IsKnownFallbackLocale(normalized))
            {
                return false;
            }

            result = new IntlLocale(normalized, CultureInfo.InvariantCulture);
            return true;
        }
    }

    private static string NormalizeLocale(string locale)
    {
        var trimmed = locale.Trim().Replace('_', '-');
        var extension = trimmed.IndexOf("-u-", StringComparison.OrdinalIgnoreCase);
        if (extension >= 0)
        {
            trimmed = trimmed[..extension];
        }

        return trimmed;
    }

    private static string CanonicalLocaleName(string locale)
    {
        if (locale.Length == 0)
        {
            return string.Empty;
        }

        var parts = locale.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = i == 0
                ? parts[i].ToLowerInvariant()
                : parts[i].Length == 2
                    ? parts[i].ToUpperInvariant()
                    : parts[i];
        }
        return string.Join('-', parts);
    }

    private static bool IsKnownFallbackLocale(string locale) => locale is
        "en" or "en-US" or "en-GB" or
        "de-DE" or "fr-FR" or "es-ES" or "it-IT" or
        "ja-JP" or "zh-CN" or "ko-KR" or "pt-BR" or "nl-NL";

    private static IntlLocaleState ParseLocaleState(JsRealm realm, string tag)
    {
        if (tag.Length == 0 || tag.AsSpan().IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            throw new JsThrow(realm.NewRangeError("invalid locale tag"));
        }

        var parts = tag.Replace('_', '-').Split('-', StringSplitOptions.None);
        if (parts.Length == 0 || parts[0].Length is < 2 or > 8 || !IsAsciiLetters(parts[0]))
        {
            throw new JsThrow(realm.NewRangeError("invalid locale tag"));
        }

        var language = parts[0].ToLowerInvariant();
        string? script = null;
        string? region = null;
        string? calendar = null;
        string? hourCycle = null;
        var numeric = false;
        var i = 1;

        if (i < parts.Length && parts[i].Length == 4 && IsAsciiLetters(parts[i]))
        {
            script = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
            i++;
        }

        if (i < parts.Length && IsRegionSubtag(parts[i]))
        {
            region = parts[i].ToUpperInvariant();
            i++;
        }

        var baseParts = new List<string> { language };
        if (script is not null)
        {
            baseParts.Add(script);
        }

        if (region is not null)
        {
            baseParts.Add(region);
        }

        var baseName = string.Join('-', baseParts);

        for (; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i], "u", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            i++;
            while (i < parts.Length)
            {
                var key = parts[i].ToLowerInvariant();
                if (key.Length != 2)
                {
                    break;
                }

                i++;
                var values = new List<string>();
                while (i < parts.Length && parts[i].Length != 2)
                {
                    if (parts[i].Length == 0)
                    {
                        throw new JsThrow(realm.NewRangeError("invalid locale tag"));
                    }

                    values.Add(parts[i].ToLowerInvariant());
                    i++;
                }

                var value = values.Count == 0 ? string.Empty : string.Join('-', values);
                if (key == "ca" && value.Length > 0)
                {
                    calendar = value;
                }
                else if (key == "hc" && value is "h11" or "h12" or "h23" or "h24")
                {
                    hourCycle = value;
                }
                else if (key == "kn")
                {
                    numeric = value.Length == 0 || value == "true";
                }
            }
            break;
        }

        return new IntlLocaleState(
            BuildLocaleName(baseName, calendar, hourCycle, numeric),
            baseName,
            language,
            script,
            region,
            calendar,
            hourCycle,
            numeric);
    }

    private static string BuildLocaleName(string baseName, string? calendar, string? hourCycle, bool numeric)
    {
        var extensions = new List<string>();
        if (calendar is not null)
        {
            extensions.Add("ca-" + calendar);
        }

        if (hourCycle is not null)
        {
            extensions.Add("hc-" + hourCycle);
        }

        if (numeric)
        {
            extensions.Add("kn");
        }

        return extensions.Count == 0 ? baseName : baseName + "-u-" + string.Join('-', extensions);
    }

    private static bool IsRegionSubtag(string value)
        => value.Length == 2 && IsAsciiLetters(value) || value.Length == 3 && IsAsciiDigits(value);

    private static bool IsAsciiLetters(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAsciiDigits(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static string? GetStringOption(JsRealm realm, JsObject? options, string name, params string[] allowed)
    {
        if (options is null)
        {
            return null;
        }

        var value = AbstractOperations.Get(realm.ActiveVm, options, name);
        if (value.IsUndefined)
        {
            return null;
        }

        var text = AbstractOperations.ToStringJs(realm.ActiveVm, value);
        if (allowed.Length == 0)
        {
            return text;
        }

        for (var i = 0; i < allowed.Length; i++)
        {
            if (string.Equals(text, allowed[i], StringComparison.Ordinal))
            {
                return text;
            }
        }

        return null;
    }

    private static bool? GetBooleanOption(JsRealm realm, JsObject? options, string name)
    {
        if (options is null)
        {
            return null;
        }

        var value = AbstractOperations.Get(realm.ActiveVm, options, name);
        return value.IsUndefined ? null : JsValue.ToBoolean(value);
    }

    private static int GetNumberOption(JsRealm realm, JsObject? options, string name, int min, int max, int fallback)
    {
        if (options is null)
        {
            return fallback;
        }

        var value = AbstractOperations.Get(realm.ActiveVm, options, name);
        if (value.IsUndefined)
        {
            return fallback;
        }

        var number = NumberCtor.ToNumber(value);
        if (double.IsNaN(number))
        {
            return fallback;
        }

        var integer = (int)Math.Truncate(number);
        return Math.Clamp(integer, min, max);
    }

    private static long ToLength(JsValue value)
    {
        var number = NumberCtor.ToNumber(value);
        if (double.IsNaN(number) || number <= 0)
        {
            return 0;
        }

        if (number >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (long)Math.Truncate(number);
    }

    private static IntlDateTimeFormatObject RequireDateTimeFormat(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is IntlDateTimeFormatObject obj)
        {
            return obj;
        }

        throw new JsThrow(realm.NewTypeError("Intl.DateTimeFormat method called on incompatible receiver"));
    }

    private static IntlNumberFormatObject RequireNumberFormat(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is IntlNumberFormatObject obj)
        {
            return obj;
        }

        throw new JsThrow(realm.NewTypeError("Intl.NumberFormat method called on incompatible receiver"));
    }

    private static IntlCollatorObject RequireCollator(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is IntlCollatorObject obj)
        {
            return obj;
        }

        throw new JsThrow(realm.NewTypeError("Intl.Collator method called on incompatible receiver"));
    }

    private static IntlLocaleObject RequireLocale(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is IntlLocaleObject obj)
        {
            return obj;
        }

        throw new JsThrow(realm.NewTypeError("Intl.Locale method called on incompatible receiver"));
    }

    private static void DefineData(JsObject obj, string name, JsValue value)
        => obj.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable: true, enumerable: false, configurable: true));

    private readonly record struct IntlLocale(string Name, CultureInfo Culture);

    private sealed record IntlDateTimeFormatState(
        IntlLocale Locale,
        string? Year,
        string? Month,
        string? Day,
        string? Hour,
        string? Minute,
        string? Second,
        bool Hour12);

    private sealed record IntlNumberFormatState(
        IntlLocale Locale,
        string Style,
        string Currency,
        int MinimumFractionDigits,
        int MaximumFractionDigits,
        bool UseGrouping);

    private sealed record IntlCollatorState(IntlLocale Locale, string Usage, string Sensitivity, bool Numeric);

    private sealed record IntlLocaleState(
        string Name,
        string BaseName,
        string Language,
        string? Script,
        string? Region,
        string? Calendar,
        string? HourCycle,
        bool Numeric)
    {
        public IntlLocaleState Minimized()
            => Region == "US" && Script is null
                ? this with { Name = Language, BaseName = Language, Region = null }
                : this;
    }

    private sealed class IntlLocaleObject(JsObject prototype, IntlLocaleState state) : JsObject(prototype)
    {
        public IntlLocaleState State { get; } = state;
    }

    private sealed class IntlDateTimeFormatObject(JsObject prototype, IntlDateTimeFormatState state) : JsObject(prototype)
    {
        public string Format(JsRealm realm, JsValue value)
        {
            var ms = DateMilliseconds(value);
            if (double.IsNaN(ms) || double.IsInfinity(ms))
            {
                throw new JsThrow(realm.NewRangeError("Invalid time value"));
            }

            try
            {
                var date = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
                var pattern = DateTimePattern(state);
                return date.ToString(pattern, state.Locale.Culture);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new JsThrow(realm.NewRangeError("Invalid time value"));
            }
        }

        public JsValue ResolvedOptions(JsRealm realm)
        {
            var obj = realm.NewOrdinaryObject();
            obj.Set("locale", JsValue.String(state.Locale.Name));
            obj.Set("calendar", JsValue.String(DefaultCalendar));
            obj.Set("numberingSystem", JsValue.String(DefaultNumberingSystem));
            obj.Set("timeZone", JsValue.String(DefaultTimeZone));
            if (state.Year is not null)
            {
                obj.Set("year", JsValue.String(state.Year));
            }

            if (state.Month is not null)
            {
                obj.Set("month", JsValue.String(state.Month));
            }

            if (state.Day is not null)
            {
                obj.Set("day", JsValue.String(state.Day));
            }

            if (state.Hour is not null)
            {
                obj.Set("hour", JsValue.String(state.Hour));
                obj.Set("hourCycle", JsValue.String(state.Hour12 ? "h12" : "h23"));
                obj.Set("hour12", JsValue.Boolean(state.Hour12));
            }
            if (state.Minute is not null)
            {
                obj.Set("minute", JsValue.String(state.Minute));
            }

            if (state.Second is not null)
            {
                obj.Set("second", JsValue.String(state.Second));
            }

            return JsValue.Object(obj);
        }

        private static double DateMilliseconds(JsValue value)
        {
            if (value.IsUndefined)
            {
                return 0;
            }

            if (value.IsObject && value.AsObject is JsDate date)
            {
                return date.TimeValueMs;
            }

            return NumberCtor.ToNumber(value);
        }

        private static string DateTimePattern(IntlDateTimeFormatState state)
        {
            var hasDate = state.Year is not null || state.Month is not null || state.Day is not null;
            var hasTime = state.Hour is not null || state.Minute is not null || state.Second is not null;
            if (hasDate && !hasTime)
            {
                return DatePattern(state);
            }

            if (hasTime && !hasDate)
            {
                return TimePattern(state);
            }

            if (hasDate && hasTime)
            {
                return DatePattern(state) + ", " + TimePattern(state);
            }

            return ShortDatePattern(state.Locale);
        }


        private static string ShortDatePattern(IntlLocale locale) => locale.Name switch
        {
            "en-US" => "M/d/yyyy",
            "en-GB" => "dd/MM/yyyy",
            _ => locale.Culture.DateTimeFormat.ShortDatePattern,
        };

        private static string DatePattern(IntlDateTimeFormatState state)
        {
            var parts = new List<string>(3);
            if (state.Month is not null)
            {
                parts.Add(state.Month switch
            {
                "2-digit" => "MM",
                "short" => "MMM",
                "long" => "MMMM",
                _ => "M",
            });
            }

            if (state.Day is not null)
            {
                parts.Add(state.Day == "2-digit" ? "dd" : "d");
            }

            if (state.Year is not null)
            {
                parts.Add(state.Year == "2-digit" ? "yy" : "yyyy");
            }

            return parts.Count == 0 ? ShortDatePattern(state.Locale) : string.Join("/", parts);
        }

        private static string TimePattern(IntlDateTimeFormatState state)
        {
            var parts = new List<string>(3);
            if (state.Hour is not null)
            {
                parts.Add(state.Hour12 ? (state.Hour == "2-digit" ? "hh" : "h") : (state.Hour == "2-digit" ? "HH" : "H"));
            }

            if (state.Minute is not null)
            {
                parts.Add(state.Minute == "2-digit" ? "mm" : "m");
            }

            if (state.Second is not null)
            {
                parts.Add(state.Second == "2-digit" ? "ss" : "s");
            }

            var pattern = parts.Count == 0 ? "HH:mm:ss" : string.Join(":", parts);
            return state.Hour12 ? pattern + " tt" : pattern;
        }
    }

    private sealed class IntlNumberFormatObject(JsObject prototype, IntlNumberFormatState state) : JsObject(prototype)
    {
        public string Format(JsValue value)
        {
            var number = value.IsUndefined ? double.NaN : NumberCtor.ToNumber(value);
            if (double.IsNaN(number))
            {
                return "NaN";
            }

            if (double.IsPositiveInfinity(number))
            {
                return "∞";
            }

            if (double.IsNegativeInfinity(number))
            {
                return "-∞";
            }

            var nfi = (NumberFormatInfo)state.Locale.Culture.NumberFormat.Clone();
            if (!state.UseGrouping)
            {
                nfi.NumberGroupSeparator = string.Empty;
                nfi.CurrencyGroupSeparator = string.Empty;
                nfi.PercentGroupSeparator = string.Empty;
            }

            return state.Style switch
            {
                "currency" => FormatCurrency(number, nfi),
                "percent" => FormatDecimal(number * 100, nfi) + nfi.PercentSymbol,
                _ => FormatDecimal(number, nfi),
            };
        }

        public JsValue ResolvedOptions(JsRealm realm)
        {
            var obj = realm.NewOrdinaryObject();
            obj.Set("locale", JsValue.String(state.Locale.Name));
            obj.Set("numberingSystem", JsValue.String(DefaultNumberingSystem));
            obj.Set("style", JsValue.String(state.Style));
            if (state.Style == "currency")
            {
                obj.Set("currency", JsValue.String(state.Currency));
                obj.Set("currencyDisplay", JsValue.String("symbol"));
            }
            obj.Set("minimumFractionDigits", JsValue.Number(state.MinimumFractionDigits));
            obj.Set("maximumFractionDigits", JsValue.Number(state.MaximumFractionDigits));
            obj.Set("useGrouping", JsValue.Boolean(state.UseGrouping));
            return JsValue.Object(obj);
        }

        private string FormatCurrency(double number, NumberFormatInfo nfi)
        {
            nfi.CurrencySymbol = CurrencySymbol(state.Currency);
            nfi.CurrencyDecimalDigits = state.MaximumFractionDigits;
            return number.ToString("C" + state.MaximumFractionDigits.ToString(CultureInfo.InvariantCulture), nfi);
        }

        private string FormatDecimal(double number, NumberFormatInfo nfi)
            => number.ToString(NumberPattern(), nfi);

        private string NumberPattern()
        {
            var pattern = state.UseGrouping ? "#,0" : "0";
            if (state.MaximumFractionDigits == 0)
            {
                return pattern;
            }

            pattern += "." + new string('0', state.MinimumFractionDigits);
            if (state.MaximumFractionDigits > state.MinimumFractionDigits)
            {
                pattern += new string('#', state.MaximumFractionDigits - state.MinimumFractionDigits);
            }

            return pattern;
        }

        private static string CurrencySymbol(string currency) => currency switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "JPY" => "¥",
            "CNY" => "¥",
            "KRW" => "₩",
            _ => currency + " ",
        };
    }

    private sealed class IntlCollatorObject(JsObject prototype, IntlCollatorState state) : JsObject(prototype)
    {
        public int Compare(JsValue[] args)
        {
            var left = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined";
            var right = args.Length > 1 ? JsValue.ToStringValue(args[1]) : "undefined";
            var result = state.Locale.Culture.CompareInfo.Compare(left, right, CompareOptionsForSensitivity(state.Sensitivity));
            return Math.Sign(result);
        }

        public JsValue ResolvedOptions(JsRealm realm)
        {
            var obj = realm.NewOrdinaryObject();
            obj.Set("locale", JsValue.String(state.Locale.Name));
            obj.Set("usage", JsValue.String(state.Usage));
            obj.Set("sensitivity", JsValue.String(state.Sensitivity));
            obj.Set("ignorePunctuation", JsValue.Boolean(false));
            obj.Set("collation", JsValue.String("default"));
            obj.Set("numeric", JsValue.Boolean(state.Numeric));
            obj.Set("caseFirst", JsValue.String("false"));
            return JsValue.Object(obj);
        }

        private static CompareOptions CompareOptionsForSensitivity(string sensitivity) => sensitivity switch
        {
            "base" => CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace,
            "accent" => CompareOptions.IgnoreCase,
            "case" => CompareOptions.IgnoreNonSpace,
            _ => CompareOptions.None,
        };
    }
}
