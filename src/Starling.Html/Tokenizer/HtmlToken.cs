namespace Starling.Html.Tokenizer;

/// <summary>
/// One emitted by the HTML tokenizer (WHATWG HTML §13.2.5). The discriminated
/// union mirrors the spec's token categories.
/// </summary>
public abstract record HtmlToken;

/// <summary>
/// A single character emitted into the parser. The token holds the raw code
/// point; NULL handling is per state (Data emits as-is; name-buffer states
/// map to U+FFFD with parse error).
/// </summary>
public sealed record CharacterToken(int CodePoint) : HtmlToken;

/// <summary>
/// Start tag. Attributes are added in order discovered; duplicates are
/// suppressed per spec §13.2.5.33.
/// </summary>
/// <remarks>
/// Overrides record equality so two tokens with structurally-equal attribute
/// lists compare equal. The default record-synthesized equality compares the
/// <see cref="Attributes"/> list by reference, which makes tests painful.
/// </remarks>
public sealed record StartTagToken(
    string Name,
    IReadOnlyList<HtmlAttribute> Attributes,
    bool SelfClosing) : HtmlToken
{
    public bool Equals(StartTagToken? other) =>
        other is not null &&
        Name == other.Name &&
        SelfClosing == other.SelfClosing &&
        TokenEquality.AttrsEqual(Attributes, other.Attributes);

    public override int GetHashCode() =>
        TokenEquality.TagHash(Name, SelfClosing, Attributes);

    public override string ToString() =>
        $"StartTagToken {{ Name = {Name}, " +
        $"Attributes = [{TokenEquality.FormatAttrs(Attributes)}], " +
        $"SelfClosing = {SelfClosing} }}";
}

/// <summary>End tag. Attributes are a parse error but tracked for the spec.</summary>
public sealed record EndTagToken(
    string Name,
    IReadOnlyList<HtmlAttribute> Attributes,
    bool SelfClosing) : HtmlToken
{
    public bool Equals(EndTagToken? other) =>
        other is not null &&
        Name == other.Name &&
        SelfClosing == other.SelfClosing &&
        TokenEquality.AttrsEqual(Attributes, other.Attributes);

    public override int GetHashCode() =>
        TokenEquality.TagHash(Name, SelfClosing, Attributes);

    public override string ToString() =>
        $"EndTagToken {{ Name = {Name}, " +
        $"Attributes = [{TokenEquality.FormatAttrs(Attributes)}], " +
        $"SelfClosing = {SelfClosing} }}";
}

/// <summary>HTML comment content.</summary>
public sealed record CommentToken(string Data) : HtmlToken;

/// <summary>
/// DOCTYPE. The tree builder decides quirks vs. limited-quirks vs. no-quirks
/// from these fields per §13.2.6.2; the tokenizer just reports.
/// </summary>
public sealed record DoctypeToken(
    string? Name,
    string? PublicId,
    string? SystemId,
    bool ForceQuirks) : HtmlToken;

/// <summary>Tokenizer reached end-of-input.</summary>
public sealed record EndOfFileToken : HtmlToken
{
    public static EndOfFileToken Instance { get; } = new();
}

/// <summary>An attribute as collected by the tokenizer.</summary>
public sealed record HtmlAttribute(string Name, string Value);

/// <summary>
/// Equality helpers used by tag tokens. Internal because the records' own
/// <c>Equals</c> overrides are the public seam; this just centralizes the
/// list-comparison logic.
/// </summary>
internal static class TokenEquality
{
    public static bool AttrsEqual(
        IReadOnlyList<HtmlAttribute> a, IReadOnlyList<HtmlAttribute> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static int TagHash(
        string name, bool selfClosing, IReadOnlyList<HtmlAttribute> attrs)
    {
        var hc = new HashCode();
        hc.Add(name);
        hc.Add(selfClosing);
        foreach (var a in attrs)
        {
            hc.Add(a);
        }

        return hc.ToHashCode();
    }

    public static string FormatAttrs(IReadOnlyList<HtmlAttribute> attrs)
        => string.Join(", ", attrs.Select(a => $"{a.Name}=\"{a.Value}\""));
}
