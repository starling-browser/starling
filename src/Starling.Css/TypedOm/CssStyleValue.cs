using System.Globalization;
using Starling.Css.Tokenizer;

namespace Starling.Css.TypedOm;

/// <summary>
/// Abstract base for all CSS Typed OM value objects.
/// CSS Typed OM Level 1 §3 — <see href="https://www.w3.org/TR/css-typed-om-1/#stylevalue-objects"/>.
/// </summary>
public abstract class CssStyleValue
{
    /// <summary>
    /// Serializes the value back to CSS text.
    /// CSS Typed OM Level 1 §3 — every subtype must serialize to the
    /// canonical CSS representation of its value.
    /// </summary>
    public abstract override string ToString();

    /// <summary>
    /// Parses <paramref name="cssText"/> for the given CSS
    /// <paramref name="property"/> and returns the most specific
    /// <see cref="CssStyleValue"/> subtype that can represent it.
    /// CSS Typed OM Level 1 §3.2 —
    /// <see href="https://www.w3.org/TR/css-typed-om-1/#dom-cssstylevalue-parse"/>.
    /// </summary>
    /// <param name="property">The CSS property name (e.g. <c>width</c>).</param>
    /// <param name="cssText">The CSS value text to parse.</param>
    /// <returns>
    /// A <see cref="CssUnitValue"/> for numeric/dimension/percentage tokens;
    /// a <see cref="CssKeywordValue"/> for identifier tokens; or a
    /// <see cref="CssUnparsedValue"/> for anything else.
    /// </returns>
    /// <remarks>
    /// Mapping rules (single token after whitespace stripping):
    /// <list type="bullet">
    ///   <item><c>&lt;dimension&gt;</c> → <see cref="CssUnitValue"/> with the canonical unit string.</item>
    ///   <item><c>&lt;percentage&gt;</c> → <see cref="CssUnitValue"/> with unit <c>%</c>.</item>
    ///   <item><c>&lt;number&gt;</c>    → <see cref="CssUnitValue"/> with unit <c>number</c>.</item>
    ///   <item><c>&lt;ident&gt;</c>     → <see cref="CssKeywordValue"/>.</item>
    ///   <item>anything else → <see cref="CssUnparsedValue"/> (raw text fallback).</item>
    /// </list>
    /// </remarks>
    public static CssStyleValue Parse(string property, string cssText)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(cssText);

        // Tokenize so we get proper number/dimension parsing (handles sign,
        // scientific notation, comments, etc.).
        var tokens = CssTokenizer.Tokenize(cssText);

        // Filter whitespace and EOF to get the meaningful tokens.
        var meaningful = tokens
            .Where(t => t.Type is not (CssTokenType.Whitespace or CssTokenType.Eof))
            .ToList();

        if (meaningful.Count != 1)
        {
            return new CssUnparsedValue(cssText);
        }

        var token = meaningful[0];
        return token.Type switch
        {
            CssTokenType.Dimension => new CssUnitValue(token.Number, token.Unit.ToLowerInvariant()),
            CssTokenType.Percentage => new CssUnitValue(token.Number, "%"),
            CssTokenType.Number => new CssUnitValue(token.Number, "number"),
            CssTokenType.Ident => new CssKeywordValue(token.Value.ToLowerInvariant()),
            _ => new CssUnparsedValue(cssText),
        };
    }
}

/// <summary>
/// A CSS keyword (identifier) value.
/// CSS Typed OM Level 1 §5 —
/// <see href="https://www.w3.org/TR/css-typed-om-1/#keywordvalue-objects"/>.
/// </summary>
public sealed class CssKeywordValue : CssStyleValue
{
    /// <summary>The keyword string, lower-cased (e.g. <c>auto</c>, <c>none</c>).</summary>
    public string Value { get; }

    /// <summary>Constructs a keyword value wrapping <paramref name="value"/>.</summary>
    /// <param name="value">The CSS identifier, e.g. <c>auto</c>.</param>
    public CssKeywordValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>Returns the keyword string, e.g. <c>auto</c>.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// Abstract base for numeric CSS Typed OM values.
/// CSS Typed OM Level 1 §4 —
/// <see href="https://www.w3.org/TR/css-typed-om-1/#numeric-value"/>.
/// </summary>
public abstract class CssNumericValue : CssStyleValue
{
}

/// <summary>
/// A CSS numeric value with a unit — covers lengths, percentages, angles,
/// times, and plain numbers.
/// CSS Typed OM Level 1 §4.2 —
/// <see href="https://www.w3.org/TR/css-typed-om-1/#cssunitvalue"/>.
/// </summary>
/// <remarks>
/// The special unit string <c>"number"</c> represents a bare
/// <c>&lt;number&gt;</c> (no suffix). All other unit strings are the
/// canonical lower-case CSS unit keyword (e.g. <c>px</c>, <c>%</c>,
/// <c>em</c>, <c>deg</c>).
/// </remarks>
public sealed class CssUnitValue : CssNumericValue
{
    /// <summary>The numeric quantity.</summary>
    public double Value { get; }

    /// <summary>
    /// The CSS unit string in lower-case (e.g. <c>px</c>, <c>%</c>, <c>em</c>)
    /// or <c>"number"</c> for a dimensionless quantity.
    /// </summary>
    public string Unit { get; }

    /// <summary>
    /// Constructs a unit value with the given <paramref name="value"/> and
    /// <paramref name="unit"/>.
    /// </summary>
    /// <param name="value">The numeric quantity.</param>
    /// <param name="unit">
    /// Lower-case CSS unit keyword, or <c>"number"</c> for a bare number.
    /// </param>
    public CssUnitValue(double value, string unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Value = value;
        Unit = unit;
    }

    /// <summary>
    /// Serializes as <c>10px</c>, <c>50%</c>, or (for unit <c>number</c>)
    /// just the number with no suffix.
    /// CSS Typed OM Level 1 §4.2.
    /// </summary>
    public override string ToString()
    {
        var numStr = Value.ToString(CultureInfo.InvariantCulture);
        return Unit == "number" ? numStr : numStr + Unit;
    }
}

/// <summary>
/// Fallback for CSS text that cannot be represented by a more specific
/// Typed OM type. Analogous to <c>CSSUnparsedValue</c> in the spec.
/// CSS Typed OM Level 1 §3.2 (parse fallback).
/// </summary>
public sealed class CssUnparsedValue : CssStyleValue
{
    /// <summary>The raw CSS text that could not be parsed into a typed value.</summary>
    public string RawText { get; }

    /// <summary>
    /// Constructs an unparsed value wrapping <paramref name="rawText"/>.
    /// </summary>
    /// <param name="rawText">The original CSS text.</param>
    public CssUnparsedValue(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        RawText = rawText;
    }

    /// <summary>Returns the original raw CSS text.</summary>
    public override string ToString() => RawText;
}
