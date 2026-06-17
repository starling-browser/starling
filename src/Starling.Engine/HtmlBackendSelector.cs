// SPDX-License-Identifier: Apache-2.0
using Starling.Html;
using Starling.Html.AngleSharp;

namespace Starling.Engine;

internal enum HtmlBackendKind
{
    Starling,
    AngleSharp,
}

/// <summary>
/// Reads <c>STARLING_HTML_PARSER</c> once and installs the matching
/// <see cref="IHtmlParserBackend"/> into <see cref="HtmlParsing.Backend"/>.
/// Mirrors <see cref="JsEngineSelector"/>: lazy, default <c>"starling"</c>, and a
/// typo is rejected loudly rather than silently falling back.
/// </summary>
/// <remarks>
/// This is the only project that references the AngleSharp backend. Removing
/// AngleSharp = delete the <see cref="HtmlBackendKind.AngleSharp"/> arm and the
/// project reference; the seam (<c>Starling.Html</c>) and every call site are
/// untouched. The Starling parser stays the default.
/// </remarks>
internal static class HtmlBackendSelector
{
    private const string EnvVar = "STARLING_HTML_PARSER";

    private static readonly Lazy<HtmlBackendKind> _selected = new(ReadEnv);

    internal static HtmlBackendKind Selected => _selected.Value;

    private static HtmlBackendKind ReadEnv() => Parse(Environment.GetEnvironmentVariable(EnvVar));

    internal static HtmlBackendKind Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HtmlBackendKind.Starling;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "starling" => HtmlBackendKind.Starling,
            "anglesharp" => HtmlBackendKind.AngleSharp,
            _ => throw new InvalidOperationException(
                $"{EnvVar}='{raw}' is not a recognised HTML parser. Allowed values: 'starling', 'anglesharp'."),
        };
    }

    /// <summary>Install the chosen backend into the global holder. Idempotent and
    /// thread-safe (one-shot <see cref="Lazy{T}"/>), so every engine entry point
    /// can call it before its first parse at no cost after the first.</summary>
    internal static void EnsureInstalled() => _ = _installed.Value;

    private static readonly Lazy<bool> _installed = new(Install);

    private static bool Install()
    {
        HtmlParsing.Backend = CreateBackend();
        return true;
    }

    // CA1859: the return type is intentionally the polymorphic interface — the
    // arms select between distinct backends (Starling vs AngleSharp).
#pragma warning disable CA1859
    private static IHtmlParserBackend CreateBackend() => Selected switch
    {
        HtmlBackendKind.Starling => new StarlingHtmlBackend(),
        HtmlBackendKind.AngleSharp => new AngleSharpHtmlBackend(),
        _ => throw new InvalidOperationException($"Unhandled HTML parser: {Selected}."),
    };
#pragma warning restore CA1859
}
