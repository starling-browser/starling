using System.Collections;

namespace Starling.Dom;

public sealed class DomTokenList : IReadOnlyList<string>
{
    private readonly Func<string> _getValue;
    private readonly Action<string> _setValue;

    // Parse cache. Selector matching calls Contains for every class selector ×
    // element × ancestor walk, and reparsing the attribute on each access was a
    // measured allocation hotspot (~40% of a github.com load's churn). Key the
    // cache on the raw attribute string: as long as the cached list is the
    // parse of _cachedRaw and is never mutated afterwards, returning it when
    // _getValue() matches is always correct — no invalidation hooks needed.
    private string? _cachedRaw;
    private List<string>? _cachedTokens;

    internal DomTokenList(Func<string> getValue, Action<string> setValue)
    {
        _getValue = getValue;
        _setValue = setValue;
    }

    public int Count => Tokens.Count;

    public string this[int index] => Tokens[index];

    public bool Contains(string token)
    {
        ValidateToken(token);
        return IndexOfOrdinal(Tokens, token) >= 0;
    }

    public void Add(string token)
    {
        ValidateToken(token);
        var tokens = Tokens;
        if (IndexOfOrdinal(tokens, token) >= 0)
        {
            return;
        }

        var updated = new List<string>(tokens.Count + 1);
        updated.AddRange(tokens);
        updated.Add(token);
        Write(updated);
    }

    public bool Remove(string token)
    {
        ValidateToken(token);
        var tokens = Tokens;
        var idx = IndexOfOrdinal(tokens, token);
        if (idx < 0)
        {
            return false;
        }

        var updated = new List<string>(tokens.Count - 1);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i != idx)
            {
                updated.Add(tokens[i]);
            }
        }

        Write(updated);
        return true;
    }

    /// <summary>DOM §7.1 replace: swap <paramref name="oldToken"/> for
    /// <paramref name="newToken"/> in a single attribute write (one mutation),
    /// returning false without writing when oldToken is absent.</summary>
    public bool Replace(string oldToken, string newToken)
    {
        ValidateToken(oldToken);
        ValidateToken(newToken);
        var tokens = Tokens; // already an ordered set (deduplicated)
        var idx = IndexOfOrdinal(tokens, oldToken);
        if (idx < 0)
        {
            return false;
        }

        // Re-dedupe in case newToken was already present elsewhere.
        var updated = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = i == idx ? newToken : tokens[i];
            if (IndexOfOrdinal(updated, t) < 0)
            {
                updated.Add(t);
            }
        }

        Write(updated);
        return true;
    }

    public IEnumerator<string> GetEnumerator() => Tokens.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // DOM §7.1 — a DOMTokenList exposes its attribute as an *ordered set*: the
    // whitespace-split tokens with duplicates removed (first occurrence wins),
    // so classList of class="a a b" has length 2. `value` still returns the raw
    // attribute; only the indexed/iterated token set is deduplicated.
    private List<string> Tokens
    {
        get
        {
            var raw = _getValue();
            if (_cachedTokens is not null && string.Equals(raw, _cachedRaw, StringComparison.Ordinal))
            {
                return _cachedTokens;
            }

            var split = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tokens = new List<string>(split.Length);
            foreach (var t in split)
            {
                if (IndexOfOrdinal(tokens, t) < 0)
                {
                    tokens.Add(t);
                }
            }

            _cachedRaw = raw;
            _cachedTokens = tokens;
            return tokens;
        }
    }

    /// <summary>Serializes <paramref name="tokens"/> into the attribute and
    /// caches the pair. <paramref name="tokens"/> must be a fresh, deduplicated
    /// list (never the cached one) because the cached list must stay immutable.
    /// If a _setValue hook rewrites the attribute, the raw-string key simply
    /// stops matching and the next access reparses.</summary>
    private void Write(List<string> tokens)
    {
        var raw = string.Join(' ', tokens);
        _setValue(raw);
        _cachedRaw = raw;
        _cachedTokens = tokens;
    }

    private static int IndexOfOrdinal(List<string> tokens, string token)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void ValidateToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        foreach (var c in token)
        {
            if (char.IsWhiteSpace(c))
            {
                throw new ArgumentException("A DOM token cannot contain whitespace.", nameof(token));
            }
        }
    }
}
