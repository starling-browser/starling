namespace Starling.Net.Http.H2.Hpack;

/// <summary>
/// HPACK static table (RFC 7541 Appendix A). 61 predefined header field
/// entries, addressed by 1-based index in the combined index space (the
/// dynamic table follows immediately after, starting at index 62).
/// </summary>
internal static class HpackStaticTable
{
    /// <summary>Number of entries in the static table (RFC 7541 §2.3.1).</summary>
    public const int Count = 61;

    // Index 1 == Entries[0]. Value is the empty string when the table defines
    // no value for the entry (e.g. ":authority").
    private static readonly (string Name, string Value)[] Entries =
    [
        (":authority", ""),
        (":method", "GET"),
        (":method", "POST"),
        (":path", "/"),
        (":path", "/index.html"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "200"),
        (":status", "204"),
        (":status", "206"),
        (":status", "304"),
        (":status", "400"),
        (":status", "404"),
        (":status", "500"),
        ("accept-charset", ""),
        ("accept-encoding", "gzip, deflate"),
        ("accept-language", ""),
        ("accept-ranges", ""),
        ("accept", ""),
        ("access-control-allow-origin", ""),
        ("age", ""),
        ("allow", ""),
        ("authorization", ""),
        ("cache-control", ""),
        ("content-disposition", ""),
        ("content-encoding", ""),
        ("content-language", ""),
        ("content-length", ""),
        ("content-location", ""),
        ("content-range", ""),
        ("content-type", ""),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("expect", ""),
        ("expires", ""),
        ("from", ""),
        ("host", ""),
        ("if-match", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("if-range", ""),
        ("if-unmodified-since", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("max-forwards", ""),
        ("proxy-authenticate", ""),
        ("proxy-authorization", ""),
        ("range", ""),
        ("referer", ""),
        ("refresh", ""),
        ("retry-after", ""),
        ("server", ""),
        ("set-cookie", ""),
        ("strict-transport-security", ""),
        ("transfer-encoding", ""),
        ("user-agent", ""),
        ("vary", ""),
        ("via", ""),
        ("www-authenticate", ""),
    ];

    /// <summary>
    /// Look up an entry by its 1-based static index. Returns false when the
    /// index is out of range (1..61).
    /// </summary>
    public static bool TryGet(int index, out string name, out string value)
    {
        if (index is < 1 or > Count)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }
        (name, value) = Entries[index - 1];
        return true;
    }

    /// <summary>
    /// Find a static index for a header. Returns the index of an exact
    /// name+value match if one exists; otherwise the index of the first
    /// name-only match; otherwise 0. <paramref name="exact"/> reports whether
    /// the returned index also matched the value.
    /// </summary>
    public static int FindIndex(string name, string value, out bool exact)
    {
        var nameOnly = 0;
        for (var i = 0; i < Entries.Length; i++)
        {
            if (!string.Equals(Entries[i].Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(Entries[i].Value, value, StringComparison.Ordinal))
            {
                exact = true;
                return i + 1;
            }
            if (nameOnly == 0)
            {
                nameOnly = i + 1;
            }
        }
        exact = false;
        return nameOnly;
    }
}
