namespace Starling.Common.Encoding;

/// <summary>
/// Maps WHATWG Encoding Standard labels to their canonical encoding name.
/// Single source of truth shared by HTTP <c>Content-Type</c> charset sniffing,
/// HTML <c>&lt;meta charset&gt;</c> sniffing, and BOM sniffing.
/// </summary>
/// <remarks>
/// Source: WHATWG Encoding Living Standard, "Names and labels" table,
/// <see href="https://encoding.spec.whatwg.org/#names-and-labels"/>
/// (commit 2026-04-22 snapshot). All labels are normalized per the spec:
/// strip leading/trailing ASCII whitespace, ASCII-lowercase, then look up.
/// The "replacement" encoding (a deliberately failing decoder for known-
/// broken labels such as <c>iso-2022-cn</c>) is intentionally NOT mapped
/// here. Callers that want to honour the spec's replacement behaviour
/// should handle those labels explicitly; otherwise the engine will fall
/// back to UTF-8, Starling's default.
/// </remarks>
public static class WhatwgEncodingLabels
{
    // ASCII-whitespace per the WHATWG Infra spec § ASCII whitespace.
    private static readonly char[] AsciiWhitespace = ['\t', '\n', '\f', '\r', ' '];

    /// <summary>
    /// The canonical encoding names referenced by <see cref="Map"/>. They
    /// are exactly the WHATWG spec names, intentionally not normalised to
    /// .NET <see cref="System.Text.Encoding.WebName"/> spellings. Callers
    /// that need a .NET base class library encoding instance can use
    /// <see cref="TryGetEncoding(string, out System.Text.Encoding)"/>.
    /// </summary>
    public static class Canonical
    {
        public const string Utf8 = "UTF-8";

        // Legacy single-byte encodings.
        public const string Ibm866 = "IBM866";
        public const string Iso88592 = "ISO-8859-2";
        public const string Iso88593 = "ISO-8859-3";
        public const string Iso88594 = "ISO-8859-4";
        public const string Iso88595 = "ISO-8859-5";
        public const string Iso88596 = "ISO-8859-6";
        public const string Iso88597 = "ISO-8859-7";
        public const string Iso88598 = "ISO-8859-8";
        public const string Iso88598I = "ISO-8859-8-I";
        public const string Iso885910 = "ISO-8859-10";
        public const string Iso885913 = "ISO-8859-13";
        public const string Iso885914 = "ISO-8859-14";
        public const string Iso885915 = "ISO-8859-15";
        public const string Iso885916 = "ISO-8859-16";
        public const string Koi8R = "KOI8-R";
        public const string Koi8U = "KOI8-U";
        public const string Macintosh = "macintosh";
        public const string Windows874 = "windows-874";
        public const string Windows1250 = "windows-1250";
        public const string Windows1251 = "windows-1251";
        public const string Windows1252 = "windows-1252";
        public const string Windows1253 = "windows-1253";
        public const string Windows1254 = "windows-1254";
        public const string Windows1255 = "windows-1255";
        public const string Windows1256 = "windows-1256";
        public const string Windows1257 = "windows-1257";
        public const string Windows1258 = "windows-1258";
        public const string XMacCyrillic = "x-mac-cyrillic";

        // Legacy multi-byte (CJK) encodings.
        public const string Gbk = "GBK";
        public const string Gb18030 = "gb18030";
        public const string Big5 = "Big5";
        public const string EucJp = "EUC-JP";
        public const string Iso2022Jp = "ISO-2022-JP";
        public const string ShiftJis = "Shift_JIS";
        public const string EucKr = "EUC-KR";

        // UTF families.
        public const string Utf16Le = "UTF-16LE";
        public const string Utf16Be = "UTF-16BE";
    }

    /// <summary>
    /// Label → canonical encoding name. Keys are already normalised
    /// (ASCII-lowercase, whitespace-trimmed); lookups should normalise the
    /// caller input via <see cref="Normalize"/> first.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = BuildMap();

    /// <summary>
    /// Normalises a candidate label per WHATWG Encoding § "getting an
    /// encoding" (trim ASCII whitespace, ASCII-lowercase). Returns <c>null</c>
    /// if the input is empty after trimming.
    /// </summary>
    public static string? Normalize(string? label)
    {
        if (label is null)
        {
            return null;
        }

        var trimmed = label.Trim(AsciiWhitespace);
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Resolves <paramref name="label"/> to its canonical WHATWG name.
    /// Returns <c>null</c> for unknown labels.
    /// </summary>
    public static string? TryGetCanonicalName(string? label)
    {
        var normalized = Normalize(label);
        if (normalized is null)
        {
            return null;
        }

        return Map.TryGetValue(normalized, out var canonical) ? canonical : null;
    }

    /// <summary>
    /// Convenience: resolves a label and returns a .NET base class library
    /// <see cref="System.Text.Encoding"/> via
    /// <see cref="System.Text.Encoding.GetEncoding(string)"/>. Returns
    /// <c>false</c> for unknown labels or for canonical names not
    /// supported by the currently-registered encoding providers. The
    /// engine registers <c>CodePagesEncodingProvider.Instance</c> at
    /// startup so the legacy single-byte / CJK families resolve.
    /// </summary>
    public static bool TryGetEncoding(string? label, out System.Text.Encoding encoding)
    {
        encoding = null!;
        var canonical = TryGetCanonicalName(label);
        if (canonical is null)
        {
            return false;
        }

        try
        {
            encoding = System.Text.Encoding.GetEncoding(canonical);
            return true;
        }
        catch (System.ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> BuildMap()
    {
        // The WHATWG label table is large but stable; we embed it verbatim
        // here so there is no runtime code generation. Spec section refs
        // appear inline against each family.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        // § 4.2 "The encoding": UTF-8.
        Register(map, Canonical.Utf8,
            "unicode-1-1-utf-8", "unicode11utf8", "unicode20utf8", "utf-8",
            "utf8", "x-unicode20utf8");

        // § 4.3 "Legacy single-byte encodings".
        Register(map, Canonical.Ibm866,
            "866", "cp866", "csibm866", "ibm866");
        Register(map, Canonical.Iso88592,
            "csisolatin2", "iso-8859-2", "iso-ir-101", "iso8859-2",
            "iso88592", "iso_8859-2", "iso_8859-2:1987", "l2", "latin2");
        Register(map, Canonical.Iso88593,
            "csisolatin3", "iso-8859-3", "iso-ir-109", "iso8859-3",
            "iso88593", "iso_8859-3", "iso_8859-3:1988", "l3", "latin3");
        Register(map, Canonical.Iso88594,
            "csisolatin4", "iso-8859-4", "iso-ir-110", "iso8859-4",
            "iso88594", "iso_8859-4", "iso_8859-4:1988", "l4", "latin4");
        Register(map, Canonical.Iso88595,
            "csisolatincyrillic", "cyrillic", "iso-8859-5", "iso-ir-144",
            "iso8859-5", "iso88595", "iso_8859-5", "iso_8859-5:1988");
        Register(map, Canonical.Iso88596,
            "arabic", "asmo-708", "csiso88596e", "csiso88596i",
            "csisolatinarabic", "ecma-114", "iso-8859-6", "iso-8859-6-e",
            "iso-8859-6-i", "iso-ir-127", "iso8859-6", "iso88596",
            "iso_8859-6", "iso_8859-6:1987");
        Register(map, Canonical.Iso88597,
            "csisolatingreek", "ecma-118", "elot_928", "greek", "greek8",
            "iso-8859-7", "iso-ir-126", "iso8859-7", "iso88597",
            "iso_8859-7", "iso_8859-7:1987", "sun_eu_greek");
        Register(map, Canonical.Iso88598,
            "csiso88598e", "csisolatinhebrew", "hebrew", "iso-8859-8",
            "iso-8859-8-e", "iso-ir-138", "iso8859-8", "iso88598",
            "iso_8859-8", "iso_8859-8:1988", "visual");
        Register(map, Canonical.Iso88598I,
            "csiso88598i", "iso-8859-8-i", "logical");
        Register(map, Canonical.Iso885910,
            "csisolatin6", "iso-8859-10", "iso-ir-157", "iso8859-10",
            "iso885910", "l6", "latin6");
        Register(map, Canonical.Iso885913,
            "iso-8859-13", "iso8859-13", "iso885913");
        Register(map, Canonical.Iso885914,
            "iso-8859-14", "iso8859-14", "iso885914");
        Register(map, Canonical.Iso885915,
            "csisolatin9", "iso-8859-15", "iso8859-15", "iso885915",
            "iso_8859-15", "l9");
        Register(map, Canonical.Iso885916, "iso-8859-16");
        Register(map, Canonical.Koi8R,
            "cskoi8r", "koi", "koi8", "koi8-r", "koi8_r");
        Register(map, Canonical.Koi8U,
            "koi8-ru", "koi8-u");
        Register(map, Canonical.Macintosh,
            "csmacintosh", "mac", "macintosh", "x-mac-roman");
        Register(map, Canonical.Windows874,
            "dos-874", "iso-8859-11", "iso8859-11", "iso885911",
            "tis-620", "windows-874");
        Register(map, Canonical.Windows1250,
            "cp1250", "windows-1250", "x-cp1250");
        Register(map, Canonical.Windows1251,
            "cp1251", "windows-1251", "x-cp1251");
        Register(map, Canonical.Windows1252,
            "ansi_x3.4-1968", "ascii", "cp1252", "cp819", "csisolatin1",
            "ibm819", "iso-8859-1", "iso-ir-100", "iso8859-1", "iso88591",
            "iso_8859-1", "iso_8859-1:1987", "l1", "latin1", "us-ascii",
            "windows-1252", "x-cp1252");
        Register(map, Canonical.Windows1253,
            "cp1253", "windows-1253", "x-cp1253");
        Register(map, Canonical.Windows1254,
            "cp1254", "csisolatin5", "iso-8859-9", "iso-ir-148",
            "iso8859-9", "iso88599", "iso_8859-9", "iso_8859-9:1989", "l5",
            "latin5", "windows-1254", "x-cp1254");
        Register(map, Canonical.Windows1255,
            "cp1255", "windows-1255", "x-cp1255");
        Register(map, Canonical.Windows1256,
            "cp1256", "windows-1256", "x-cp1256");
        Register(map, Canonical.Windows1257,
            "cp1257", "windows-1257", "x-cp1257");
        Register(map, Canonical.Windows1258,
            "cp1258", "windows-1258", "x-cp1258");
        Register(map, Canonical.XMacCyrillic,
            "x-mac-cyrillic", "x-mac-ukrainian");

        // § 4.4 "Legacy multi-byte Chinese (simplified) encodings".
        Register(map, Canonical.Gbk,
            "chinese", "csgb2312", "csiso58gb231280", "gb2312", "gb_2312",
            "gb_2312-80", "gbk", "iso-ir-58", "x-gbk");
        Register(map, Canonical.Gb18030, "gb18030");

        // § 4.5 "Legacy multi-byte Chinese (traditional) encodings".
        Register(map, Canonical.Big5,
            "big5", "big5-hkscs", "cn-big5", "csbig5", "x-x-big5");

        // § 4.6 "Legacy multi-byte Japanese encodings".
        Register(map, Canonical.EucJp,
            "cseucpkdfmtjapanese", "euc-jp", "x-euc-jp");
        Register(map, Canonical.Iso2022Jp,
            "csiso2022jp", "iso-2022-jp");
        Register(map, Canonical.ShiftJis,
            "csshiftjis", "ms932", "ms_kanji", "shift-jis", "shift_jis",
            "sjis", "windows-31j", "x-sjis");

        // § 4.7 "Legacy multi-byte Korean encodings".
        Register(map, Canonical.EucKr,
            "cseuckr", "csksc56011987", "euc-kr", "iso-ir-149",
            "korean", "ks_c_5601-1987", "ks_c_5601-1989", "ksc5601",
            "ksc_5601", "windows-949");

        // § 4.8 "Legacy miscellaneous encodings".
        // The "replacement" decoder (for hz-gb-2312, iso-2022-cn, etc.) is
        // intentionally omitted — see class remarks.

        Register(map, Canonical.Utf16Be,
            "unicodefffe", "utf-16be");
        Register(map, Canonical.Utf16Le,
            "csunicode", "iso-10646-ucs-2", "ucs-2", "unicode",
            "unicodefeff", "utf-16", "utf-16le");

        // "x-user-defined" is a passthrough single-byte mapping; the .NET
        // The .NET base class library doesn't ship it, so we leave it for the caller to handle.

        return map;
    }

    private static void Register(
        Dictionary<string, string> map,
        string canonical,
        params string[] labels)
    {
        foreach (var label in labels)
        {
            map[label] = canonical;
        }
    }
}
