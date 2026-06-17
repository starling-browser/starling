using System.Globalization;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §21.4 The Date constructor and §21.4.4 Date.prototype. Spec-faithful surface
/// backed by .NET <see cref="System.DateTimeOffset"/>; <see cref="JsDate"/>
/// stores the millisecond timestamp directly (with NaN encoding invalid dates).
/// </summary>
/// <remarks>
/// Locale is intentionally invariant — every <c>toLocale*</c> method delegates
/// to its non-locale sibling, and <c>getTimezoneOffset</c> returns 0 since
/// Starling treats the local timezone as UTC for reproducibility. The browser
/// would otherwise pull the host's TZ + culture, which makes snapshot rendering
/// non-deterministic across machines.
/// </remarks>
public static class DateCtor
{
    private static readonly string[] WeekdayShort = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] MonthShort = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.DatePrototype;

        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction(realm, "Date", length: 7, (newTarget, args) =>
        {
            // §21.4.1.1 — Called without new, return the current date as a
            // string regardless of args (spec oddity that real bundles use to
            // sniff host locale formatting; we return a stable UTC string).
            var calledAsConstructor = IntrinsicHelpers.IsConstructInvocation(newTarget);
            if (!calledAsConstructor)
            {
                return JsValue.String(FormatToString(NowMs()));
            }

            double ms;
            if (args.Length == 0)
            {
                ms = NowMs();
            }
            else if (args.Length == 1)
            {
                var v = args[0];
                if (v.IsString)
                {
                    ms = ParseDate(v.AsString);
                }
                else if (v.IsObject && v.AsObject is JsDate other)
                {
                    ms = other.TimeValueMs;
                }
                else
                {
                    ms = JsValue.ToNumber(v);
                }
            }
            else
            {
                ms = MakeLocalMs(args);
            }
            // §21.4.1.1 step 14: OrdinaryCreateFromConstructor — prototype from
            // new.target so `class D extends Date {}` produces a D-prototyped date.
            var date = new JsDate(realm, ms);
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            if (!ReferenceEquals(instProto, proto))
            {
                date.SetPrototypeOf(instProto);
            }

            return JsValue.Object(date);
        }, isConstructor: true);

        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));

        // Static methods.
        IntrinsicHelpers.DefineMethod(realm, ctor, "now", 0, (_, _) => JsValue.Number(NowMs()));
        IntrinsicHelpers.DefineMethod(realm, ctor, "parse", 1, (_, args) =>
            JsValue.Number(args.Length == 0 ? double.NaN : ParseDate(JsValue.ToStringValue(args[0]))));
        IntrinsicHelpers.DefineMethod(realm, ctor, "UTC", 7, (_, args) => JsValue.Number(MakeUtcMs(args)));

        // Prototype getters.
        IntrinsicHelpers.DefineMethod(realm, proto, "getTime", 0, (thisV, _) => Time(realm, thisV));
        IntrinsicHelpers.DefineMethod(realm, proto, "valueOf", 0, (thisV, _) => Time(realm, thisV));
        IntrinsicHelpers.DefineMethod(realm, proto, "getFullYear", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Year));
        IntrinsicHelpers.DefineMethod(realm, proto, "getMonth", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Month - 1));
        IntrinsicHelpers.DefineMethod(realm, proto, "getDate", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Day));
        IntrinsicHelpers.DefineMethod(realm, proto, "getDay", 0, (thisV, _) => GetField(realm, thisV, dto => (int)dto.DayOfWeek));
        IntrinsicHelpers.DefineMethod(realm, proto, "getHours", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Hour));
        IntrinsicHelpers.DefineMethod(realm, proto, "getMinutes", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Minute));
        IntrinsicHelpers.DefineMethod(realm, proto, "getSeconds", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Second));
        IntrinsicHelpers.DefineMethod(realm, proto, "getMilliseconds", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Millisecond));
        IntrinsicHelpers.DefineMethod(realm, proto, "getTimezoneOffset", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return d.IsValid ? JsValue.Number(0) : JsValue.NaN;
        });

        // UTC variants — equivalent under our invariant-UTC locale, but kept
        // distinct so dual lookups still resolve.
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCFullYear", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Year));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCMonth", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Month - 1));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCDate", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Day));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCDay", 0, (thisV, _) => GetField(realm, thisV, dto => (int)dto.DayOfWeek));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCHours", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Hour));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCMinutes", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Minute));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCSeconds", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Second));
        IntrinsicHelpers.DefineMethod(realm, proto, "getUTCMilliseconds", 0, (thisV, _) => GetField(realm, thisV, dto => dto.Millisecond));

        // Setters.
        IntrinsicHelpers.DefineMethod(realm, proto, "setTime", 1, (thisV, args) =>
        {
            var d = RequireDate(realm, thisV);
            var ms = args.Length == 0 ? double.NaN : JsValue.ToNumber(args[0]);
            d.SetTimeMs(ms);
            return JsValue.Number(d.TimeValueMs);
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "setFullYear", 3, (thisV, args) => SetParts(realm, thisV, args, DatePart.Year));
        IntrinsicHelpers.DefineMethod(realm, proto, "setMonth", 2, (thisV, args) => SetParts(realm, thisV, args, DatePart.Month));
        IntrinsicHelpers.DefineMethod(realm, proto, "setDate", 1, (thisV, args) => SetParts(realm, thisV, args, DatePart.Day));
        IntrinsicHelpers.DefineMethod(realm, proto, "setHours", 4, (thisV, args) => SetParts(realm, thisV, args, DatePart.Hours));
        IntrinsicHelpers.DefineMethod(realm, proto, "setMinutes", 3, (thisV, args) => SetParts(realm, thisV, args, DatePart.Minutes));
        IntrinsicHelpers.DefineMethod(realm, proto, "setSeconds", 2, (thisV, args) => SetParts(realm, thisV, args, DatePart.Seconds));
        IntrinsicHelpers.DefineMethod(realm, proto, "setMilliseconds", 1, (thisV, args) => SetParts(realm, thisV, args, DatePart.Milliseconds));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCFullYear", 3, (thisV, args) => SetParts(realm, thisV, args, DatePart.Year));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCMonth", 2, (thisV, args) => SetParts(realm, thisV, args, DatePart.Month));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCDate", 1, (thisV, args) => SetParts(realm, thisV, args, DatePart.Day));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCHours", 4, (thisV, args) => SetParts(realm, thisV, args, DatePart.Hours));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCMinutes", 3, (thisV, args) => SetParts(realm, thisV, args, DatePart.Minutes));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCSeconds", 2, (thisV, args) => SetParts(realm, thisV, args, DatePart.Seconds));
        IntrinsicHelpers.DefineMethod(realm, proto, "setUTCMilliseconds", 1, (thisV, args) => SetParts(realm, thisV, args, DatePart.Milliseconds));

        // String conversions.
        IntrinsicHelpers.DefineMethod(realm, proto, "toString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatToString(d.TimeValueMs) : "Invalid Date");
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toDateString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatDateString(d.TimeValueMs) : "Invalid Date");
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toTimeString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatTimeString(d.TimeValueMs) : "Invalid Date");
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toISOString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            if (!d.IsValid)
            {
                throw new JsThrow(realm.NewRangeError("Invalid time value"));
            }

            var dto = d.ToDto();
            if (dto is null)
            {
                throw new JsThrow(realm.NewRangeError("Invalid time value"));
            }

            return JsValue.String(dto.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toJSON", 1, (thisV, _) =>
        {
            // §21.4.4.37 — toJSON re-uses toISOString. Invalid dates per spec
            // return null (the TimeClip / NaN check on the time value short-
            // circuits before toISOString throws).
            var d = RequireDate(realm, thisV);
            if (!d.IsValid)
            {
                return JsValue.Null;
            }

            var dto = d.ToDto();
            if (dto is null)
            {
                return JsValue.Null;
            }

            return JsValue.String(dto.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toUTCString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatUtcString(d.TimeValueMs) : "Invalid Date");
        });
        // Locale variants — invariant, so delegate to the non-locale form.
        IntrinsicHelpers.DefineMethod(realm, proto, "toLocaleString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatToString(d.TimeValueMs) : "Invalid Date");
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toLocaleDateString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatDateString(d.TimeValueMs) : "Invalid Date");
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "toLocaleTimeString", 0, (thisV, _) =>
        {
            var d = RequireDate(realm, thisV);
            return JsValue.String(d.IsValid ? FormatTimeString(d.TimeValueMs) : "Invalid Date");
        });

        // §21.4.4.45 Date.prototype[@@toPrimitive] — coerces to string for
        // "default"/"string" hints, number otherwise. Real bundles call
        // String(date) which goes through this.
        var toPrimitive = new JsNativeFunction(realm, "[Symbol.toPrimitive]", 1, (thisV, args) =>
        {
            var d = RequireDate(realm, thisV);
            var hint = args.Length > 0 && args[0].IsString ? args[0].AsString : "default";
            if (hint == "number")
            {
                return d.IsValid ? JsValue.Number(d.TimeValueMs) : JsValue.NaN;
            }

            return JsValue.String(d.IsValid ? FormatToString(d.TimeValueMs) : "Invalid Date");
        }, isConstructor: false);
        proto.DefineOwnProperty(SymbolCtor.ToPrimitive,
            PropertyDescriptor.Data(JsValue.Object(toPrimitive), writable: false, enumerable: false, configurable: true));

        realm.DateConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Date",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    // ------------------------------------------------------------------
    //                          Time helpers
    // ------------------------------------------------------------------

    private static double NowMs() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static JsDate RequireDate(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsDate d)
        {
            return d;
        }

        throw new JsThrow(realm.NewTypeError("Date.prototype method called on non-Date receiver"));
    }

    private static JsValue Time(JsRealm realm, JsValue thisV)
    {
        var d = RequireDate(realm, thisV);
        return d.IsValid ? JsValue.Number(d.TimeValueMs) : JsValue.NaN;
    }

    private static JsValue GetField(JsRealm realm, JsValue thisV, Func<System.DateTimeOffset, int> selector)
    {
        var d = RequireDate(realm, thisV);
        var dto = d.ToDto();
        if (dto is null)
        {
            return JsValue.NaN;
        }

        return JsValue.Number(selector(dto.Value));
    }

    // ------------------------------------------------------------------
    //                          new Date(y, m, ...) construction
    // ------------------------------------------------------------------

    // For invariant-UTC locale, the local-form constructor matches Date.UTC.
    private static double MakeLocalMs(JsValue[] args) => MakeUtcMs(args);

    private static double MakeUtcMs(JsValue[] args)
    {
        if (args.Length == 0)
        {
            return double.NaN;
        }

        var year = JsValue.ToNumber(args[0]);
        if (double.IsNaN(year))
        {
            return double.NaN;
        }
        // §21.4.1.16 — 0..99 maps to 1900..1999.
        if (year >= 0 && year <= 99)
        {
            year += 1900;
        }

        var month = args.Length > 1 ? JsValue.ToNumber(args[1]) : 0;
        var day = args.Length > 2 ? JsValue.ToNumber(args[2]) : 1;
        var hours = args.Length > 3 ? JsValue.ToNumber(args[3]) : 0;
        var minutes = args.Length > 4 ? JsValue.ToNumber(args[4]) : 0;
        var seconds = args.Length > 5 ? JsValue.ToNumber(args[5]) : 0;
        var ms = args.Length > 6 ? JsValue.ToNumber(args[6]) : 0;

        if (double.IsNaN(month) || double.IsNaN(day) || double.IsNaN(hours)
            || double.IsNaN(minutes) || double.IsNaN(seconds) || double.IsNaN(ms))
        {
            return double.NaN;
        }

        return MakeMs((int)year, (int)month, (int)day, (int)hours, (int)minutes, (int)seconds, (int)ms);
    }

    private static double MakeMs(int year, int month, int day, int hours, int minutes, int seconds, int ms)
    {
        try
        {
            // Normalize month overflow/underflow per §21.4.1.13 MakeDay.
            var totalMonths = year * 12L + month;
            var yy = (int)(totalMonths / 12);
            var mm = (int)(totalMonths % 12);
            if (mm < 0) { mm += 12; yy -= 1; }
            // Build a UTC DateTime then add day/time deltas as TimeSpans so
            // out-of-range day/hour values normalize per spec.
            if (yy < 1 || yy > 9999)
            {
                return double.NaN;
            }

            var baseDate = new System.DateTime(yy, mm + 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            var delta = System.TimeSpan.FromDays(day - 1)
                + System.TimeSpan.FromHours(hours)
                + System.TimeSpan.FromMinutes(minutes)
                + System.TimeSpan.FromSeconds(seconds)
                + System.TimeSpan.FromMilliseconds(ms);
            var dto = new System.DateTimeOffset(baseDate, System.TimeSpan.Zero).Add(delta);
            return dto.ToUnixTimeMilliseconds();
        }
        catch (System.ArgumentOutOfRangeException) { return double.NaN; }
        catch (System.OverflowException) { return double.NaN; }
    }

    // ------------------------------------------------------------------
    //                          Setters
    // ------------------------------------------------------------------

    private enum DatePart { Year, Month, Day, Hours, Minutes, Seconds, Milliseconds }

    private static JsValue SetParts(JsRealm realm, JsValue thisV, JsValue[] args, DatePart kind)
    {
        var d = RequireDate(realm, thisV);
        // setFullYear is the only setter that operates even on an invalid
        // Date (§21.4.4.21). Every other setter on an invalid Date stays NaN.
        var dto = d.ToDto();
        int year, month, day, hours, minutes, seconds, ms;
        if (dto is null)
        {
            if (kind != DatePart.Year) { d.SetTimeMs(double.NaN); return JsValue.NaN; }
            year = 1970; month = 0; day = 1; hours = 0; minutes = 0; seconds = 0; ms = 0;
        }
        else
        {
            var v = dto.Value;
            year = v.Year; month = v.Month - 1; day = v.Day;
            hours = v.Hour; minutes = v.Minute; seconds = v.Second; ms = v.Millisecond;
        }

        if (args.Length == 0) { d.SetTimeMs(double.NaN); return JsValue.NaN; }

        double TakeOr(int i, double fallback)
        {
            if (i >= args.Length)
            {
                return fallback;
            }

            var n = JsValue.ToNumber(args[i]);
            return n;
        }

        double nYear = year, nMonth = month, nDay = day, nHours = hours, nMinutes = minutes, nSeconds = seconds, nMs = ms;
        switch (kind)
        {
            case DatePart.Year:
                nYear = JsValue.ToNumber(args[0]);
                nMonth = TakeOr(1, month);
                nDay = TakeOr(2, day);
                break;
            case DatePart.Month:
                nMonth = JsValue.ToNumber(args[0]);
                nDay = TakeOr(1, day);
                break;
            case DatePart.Day:
                nDay = JsValue.ToNumber(args[0]);
                break;
            case DatePart.Hours:
                nHours = JsValue.ToNumber(args[0]);
                nMinutes = TakeOr(1, minutes);
                nSeconds = TakeOr(2, seconds);
                nMs = TakeOr(3, ms);
                break;
            case DatePart.Minutes:
                nMinutes = JsValue.ToNumber(args[0]);
                nSeconds = TakeOr(1, seconds);
                nMs = TakeOr(2, ms);
                break;
            case DatePart.Seconds:
                nSeconds = JsValue.ToNumber(args[0]);
                nMs = TakeOr(1, ms);
                break;
            case DatePart.Milliseconds:
                nMs = JsValue.ToNumber(args[0]);
                break;
        }

        if (double.IsNaN(nYear) || double.IsNaN(nMonth) || double.IsNaN(nDay)
            || double.IsNaN(nHours) || double.IsNaN(nMinutes) || double.IsNaN(nSeconds) || double.IsNaN(nMs))
        {
            d.SetTimeMs(double.NaN);
            return JsValue.NaN;
        }

        var newMs = MakeMs((int)nYear, (int)nMonth, (int)nDay, (int)nHours, (int)nMinutes, (int)nSeconds, (int)nMs);
        d.SetTimeMs(newMs);
        return JsValue.Number(newMs);
    }

    // ------------------------------------------------------------------
    //                       Parsing (ISO-leaning lenient)
    // ------------------------------------------------------------------

    private static readonly string[] AcceptedFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.fff",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd",
        "yyyy-MM",
        "yyyy",
    };

    internal static double ParseDate(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return double.NaN;
        }

        var s = input.Trim();

        // Fast path: ISO 8601 with optional fractional seconds + offset.
        if (System.DateTimeOffset.TryParseExact(s, AcceptedFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            return exact.ToUnixTimeMilliseconds();
        }

        // Fall back to lenient round-trip parser for edge formats.
        if (System.DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var any))
        {
            return any.ToUnixTimeMilliseconds();
        }

        // Legacy "loose" formats every browser accepts (§21.4.3.2 leaves
        // non-ISO strings implementation-defined, but the web depends on
        // these): "Tue Jun 09 20:00:00 +0000 2026" (Twitter's created_at),
        // "Tue Jun 09 2026 20:00:00 GMT+0000 (Coordinated Universal Time)"
        // (Date.prototype.toString round-trip), "Jun 9, 2026" and friends.
        if (TryParseLegacy(s, out var legacy))
        {
            return legacy;
        }

        return double.NaN;
    }

    /// <summary>Token-based parser for the loose date forms: any order of
    /// weekday (ignored), month name, day, 4-digit year, hh:mm[:ss[.fff]],
    /// and a UTC offset ("+0000", "GMT+01:00", "Z", bare "GMT"/"UTC").
    /// Parenthesized zone names are stripped. Without an offset the time is
    /// taken as UTC, matching this engine's ISO fallback behaviour.</summary>
    private static bool TryParseLegacy(string input, out double ms)
    {
        ms = double.NaN;
        var noParen = System.Text.RegularExpressions.Regex.Replace(input, @"\([^)]*\)", " ");
        var tokens = noParen.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        int month = -1, day = -1, year = int.MinValue;
        int hour = 0, minute = 0, second = 0, milli = 0;
        var offsetMinutes = 0;
        var sawTime = false;

        foreach (var raw in tokens)
        {
            var tok = raw;
            if (tok.StartsWith("GMT", System.StringComparison.OrdinalIgnoreCase)
                || tok.StartsWith("UTC", System.StringComparison.OrdinalIgnoreCase))
            {
                tok = tok[3..];
                if (tok.Length == 0)
                {
                    continue; // bare GMT/UTC → offset 0
                }
            }
            if (tok is "Z" or "z")
            {
                continue;
            }

            if (tok[0] is '+' or '-')
            {
                var sign = tok[0] == '-' ? -1 : 1;
                var digits = tok[1..].Replace(":", "");
                if (digits.Length != 4 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
                {
                    return false;
                }

                offsetMinutes = sign * (hhmm / 100 * 60 + hhmm % 100);
                continue;
            }

            if (tok.Contains(':'))
            {
                var fracSplit = tok.Split('.');
                var parts = fracSplit[0].Split(':');
                if (parts.Length is < 2 or > 3)
                {
                    return false;
                }

                if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour))
                {
                    return false;
                }

                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute))
                {
                    return false;
                }

                if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out second))
                {
                    return false;
                }

                if (fracSplit.Length == 2)
                {
                    var frac = fracSplit[1].PadRight(3, '0')[..3];
                    if (!int.TryParse(frac, NumberStyles.None, CultureInfo.InvariantCulture, out milli))
                    {
                        return false;
                    }
                }
                sawTime = true;
                continue;
            }

            if (char.IsAsciiDigit(tok[0]))
            {
                if (!int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
                {
                    return false;
                }

                if (tok.Length >= 3 || num > 31)
                {
                    if (year != int.MinValue)
                    {
                        return false;
                    }

                    year = num;
                }
                else if (day < 0)
                {
                    day = num;
                }
                else if (year == int.MinValue)
                {
                    // "MMM d yy"-style trailing 2-digit year (1900-window not
                    // applied — browsers map 0-99 via the full-year rule only
                    // for Date(y, m) construction, strings keep the literal).
                    year = num;
                }
                else
                {
                    return false;
                }

                continue;
            }

            // Month or weekday name (weekday ignored). Anything else → fail.
            var name = tok.Length > 3 ? tok[..3] : tok;
            var monthIdx = System.Array.FindIndex(MonthShort,
                m => string.Equals(m, name, System.StringComparison.OrdinalIgnoreCase));
            if (monthIdx >= 0)
            {
                if (month >= 0)
                {
                    return false;
                }

                month = monthIdx + 1;
                continue;
            }
            var isWeekday = System.Array.Exists(WeekdayShort,
                w => string.Equals(w, name, System.StringComparison.OrdinalIgnoreCase));
            if (!isWeekday)
            {
                return false;
            }
        }

        if (month < 0 || day < 1 || day > 31 || year == int.MinValue)
        {
            return false;
        }

        if (hour > 24 || minute > 59 || second > 59)
        {
            return false;
        }

        if (!sawTime && (hour != 0 || minute != 0))
        {
            return false;
        }

        try
        {
            var dto = new System.DateTimeOffset(year, month, day, 0, 0, 0, System.TimeSpan.Zero);
            ms = dto.ToUnixTimeMilliseconds()
                + hour * 3_600_000L + minute * 60_000L + second * 1_000L + milli
                - offsetMinutes * 60_000L;
            return true;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    //                       Formatting (invariant)
    // ------------------------------------------------------------------

    private static string FormatToString(double ms)
    {
        var dto = SafeFromMs(ms);
        if (dto is null)
        {
            return "Invalid Date";
        }

        var v = dto.Value.UtcDateTime;
        return string.Format(CultureInfo.InvariantCulture,
            "{0} {1} {2:D2} {3:D4} {4:D2}:{5:D2}:{6:D2} GMT+0000 (Coordinated Universal Time)",
            WeekdayShort[(int)v.DayOfWeek], MonthShort[v.Month - 1], v.Day, v.Year, v.Hour, v.Minute, v.Second);
    }

    private static string FormatDateString(double ms)
    {
        var dto = SafeFromMs(ms);
        if (dto is null)
        {
            return "Invalid Date";
        }

        var v = dto.Value.UtcDateTime;
        return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:D2} {3:D4}",
            WeekdayShort[(int)v.DayOfWeek], MonthShort[v.Month - 1], v.Day, v.Year);
    }

    private static string FormatTimeString(double ms)
    {
        var dto = SafeFromMs(ms);
        if (dto is null)
        {
            return "Invalid Date";
        }

        var v = dto.Value.UtcDateTime;
        return string.Format(CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2} GMT+0000 (Coordinated Universal Time)", v.Hour, v.Minute, v.Second);
    }

    private static string FormatUtcString(double ms)
    {
        var dto = SafeFromMs(ms);
        if (dto is null)
        {
            return "Invalid Date";
        }

        var v = dto.Value.UtcDateTime;
        return string.Format(CultureInfo.InvariantCulture,
            "{0}, {1:D2} {2} {3:D4} {4:D2}:{5:D2}:{6:D2} GMT",
            WeekdayShort[(int)v.DayOfWeek], v.Day, MonthShort[v.Month - 1], v.Year, v.Hour, v.Minute, v.Second);
    }

    private static System.DateTimeOffset? SafeFromMs(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms))
        {
            return null;
        }

        if (ms < -62135596800000d || ms > 253402300799999d)
        {
            return null;
        }

        try { return System.DateTimeOffset.FromUnixTimeMilliseconds((long)ms); }
        catch (System.ArgumentOutOfRangeException) { return null; }
    }
}
