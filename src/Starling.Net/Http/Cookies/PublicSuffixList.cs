using System.Text;

namespace Starling.Net.Http.Cookies;

/// <summary>
/// Mozilla's Public Suffix List, used to reject cookies whose <c>Domain</c>
/// attribute is itself a registry-controlled label (e.g. <c>com</c>,
/// <c>co.uk</c>, <c>github.io</c>).
/// </summary>
/// <remarks>
/// <para>
/// Source file <c>Resources/Psl/effective_tld_names.dat</c> is bundled at
/// build time. Refresh quarterly via <c>tools/update-psl</c> (TBD).
/// </para>
/// <para>
/// Algorithm follows the official rules at
/// <see href="https://publicsuffix.org/list/"/>:
/// <list type="number">
///   <item>The matching rule is the rule with the most labels that matches.</item>
///   <item>An exception rule (prefixed <c>!</c>) wins over any non-exception rule.</item>
///   <item>If no rule matches, the default rule is <c>*</c> (matches one label).</item>
///   <item>If the matching rule is an exception, drop its leftmost label.</item>
///   <item>The domain is a public suffix iff it has the same label count as the prevailing rule.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class PublicSuffixList
{
    private readonly HashSet<string> _exact;
    private readonly HashSet<string> _wildcard;   // suffix portion (after "*.")
    private readonly HashSet<string> _exception;  // matched verbatim (without leading "!")

    private PublicSuffixList(HashSet<string> exact, HashSet<string> wildcard, HashSet<string> exception)
    {
        _exact = exact;
        _wildcard = wildcard;
        _exception = exception;
    }

    /// <summary>Total rule count; useful for sanity-checking the bundle loaded.</summary>
    public int RuleCount => _exact.Count + _wildcard.Count + _exception.Count;

    private static readonly Lazy<PublicSuffixList> _default = new(LoadEmbedded);
    public static PublicSuffixList Default => _default.Value;

    /// <summary>
    /// True if <paramref name="domain"/> is itself a public suffix (i.e. it
    /// is not registrable). Lookup is case-insensitive; trailing dots are
    /// stripped.
    /// </summary>
    public bool IsPublicSuffix(string domain)
    {
        if (string.IsNullOrEmpty(domain))
        {
            return false;
        }

        var labels = Normalize(domain).Split('.');
        if (labels.Length == 0)
        {
            return false;
        }

        var prevailingLabels = 0;
        var prevailingException = false;

        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels, i, labels.Length - i);

            if (_exception.Contains(suffix))
            {
                // Exception rules trump non-exception rules per algorithm step 3.
                if (!prevailingException || labels.Length - i > prevailingLabels)
                {
                    prevailingLabels = labels.Length - i;
                    prevailingException = true;
                }
                continue;
            }

            if (prevailingException)
            {
                continue; // already locked into an exception
            }

            if (_exact.Contains(suffix) && labels.Length - i > prevailingLabels)
            {
                prevailingLabels = labels.Length - i;
            }

            // Wildcard "*.suffix": matches when our current label has a parent in _wildcard.
            if (i + 1 < labels.Length)
            {
                var wcParent = string.Join('.', labels, i + 1, labels.Length - i - 1);
                if (_wildcard.Contains(wcParent) && labels.Length - i > prevailingLabels)
                {
                    prevailingLabels = labels.Length - i;
                }
            }
        }

        if (prevailingLabels == 0)
        {
            prevailingLabels = 1; // default rule "*"
        }

        if (prevailingException)
        {
            prevailingLabels--; // strip leftmost label of the exception rule
        }

        return prevailingLabels == labels.Length;
    }

    /// <summary>
    /// Parse a PSL data file. Lines beginning with <c>//</c> and blank lines
    /// are skipped. Both ICANN and PRIVATE sections are loaded — the cookie
    /// blocking algorithm doesn't distinguish between them.
    /// </summary>
    public static PublicSuffixList Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcard = new HashSet<string>(StringComparer.Ordinal);
        var exception = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // Many entries have inline whitespace separating extra commentary.
            // The rule is the first whitespace-separated token.
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                line = line[..space];
            }

            line = line.ToLowerInvariant();

            if (line.StartsWith('!'))
            {
                exception.Add(line[1..]);
            }
            else if (line.StartsWith("*.", StringComparison.Ordinal))
            {
                wildcard.Add(line[2..]);
            }
            else
            {
                exact.Add(line);
            }
        }

        return new PublicSuffixList(exact, wildcard, exception);
    }

    private static PublicSuffixList LoadEmbedded()
    {
        var assembly = typeof(PublicSuffixList).Assembly;
        const string resourceName = "Starling.Net.Resources.Psl.effective_tld_names.dat";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded PSL not found ({resourceName}). Searched: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    private static string Normalize(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
