using System.Collections;

namespace Starling.Net.Http;

/// <summary>
/// Order-preserving, case-insensitive HTTP header collection. A name may appear
/// multiple times (Set-Cookie, Via, etc.) — <see cref="GetFirst"/> returns the
/// first match, <see cref="GetAll"/> returns every match.
/// </summary>
/// <remarks>
/// Field-name validation follows RFC 9110 §5.1: token = 1*tchar. Field-values
/// are stored verbatim minus surrounding OWS, since the H1 parser strips that
/// before adding.
/// </remarks>
public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    public int Count => _items.Count;

    public void Add(string name, string value)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);
        _items.Add(new(name, value));
    }

    public void Set(string name, string value)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);
        RemoveAll(name);
        _items.Add(new(name, value));
    }

    public bool Contains(string name) => IndexOf(name) >= 0;

    public string? GetFirst(string name)
    {
        var i = IndexOf(name);
        return i < 0 ? null : _items[i].Value;
    }

    public IReadOnlyList<string> GetAll(string name)
    {
        List<string>? matches = null;
        foreach (var kv in _items)
        {
            if (NameEquals(kv.Key, name))
            {
                (matches ??= []).Add(kv.Value);
            }
        }
        return matches ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public int RemoveAll(string name)
    {
        return _items.RemoveAll(kv => NameEquals(kv.Key, name));
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int IndexOf(string name)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (NameEquals(_items[i].Key, name))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Header name must not be empty.", nameof(name));
        }

        foreach (var c in name)
        {
            if (!IsTokenChar(c))
            {
                throw new ArgumentException(
                    $"Header name '{name}' contains an invalid character.", nameof(name));
            }
        }
    }

    // RFC 9110 §5.6.2: tchar = "!" / "#" / "$" / "%" / "&" / "'" / "*" /
    //                          "+" / "-" / "." / "^" / "_" / "`" / "|" / "~"
    //                          / DIGIT / ALPHA
    private static bool IsTokenChar(char c) =>
        c is (>= 'a' and <= 'z')
            or (>= 'A' and <= 'Z')
            or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*'
            or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
