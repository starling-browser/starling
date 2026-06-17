using SixLabors.Fonts;
using Starling.Css.FontFace;

namespace Starling.Paint;

/// <summary>
/// Per-document registry of <c>@font-face</c>-declared typefaces. The
/// <c>FontFaceFetcher</c> in <c>Starling.Engine</c> populates this once
/// stylesheets have landed and their <c>url()</c> sources are fetched and
/// loaded.
/// <para>
/// The engine paints through ImageSharp.Drawing 3, which owns its own
/// font collection. <see cref="ImageSharpFontLookup.LoadCollection(FontFaceRegistry?)"/>
/// snapshots this registry at backend construction time, so any face
/// registered before <c>Painter.RenderDocument</c> participates in
/// font-family resolution alongside the bundled and system fonts.
/// </para>
/// </summary>
/// <remarks>
/// Lookup is case-insensitive on the family name to match CSS family matching
/// (family names compare ASCII case-insensitively per CSS Fonts 3 §5.1), and
/// quote/whitespace-insensitive via <see cref="FontFamilyKey.Normalize"/> so a
/// face registered as <c>'TwitterChirp'</c> is found by <c>TwitterChirp</c>
/// and vice versa.
/// </remarks>
public sealed class FontFaceRegistry : IDisposable
{
    private readonly Dictionary<string, List<RegisteredFace>> _byFamily =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Bumps on every successful registration. Lets consumers that
    /// snapshot the registry (the cached text measurer, the paint backend's
    /// font collection) detect a new @font-face without re-parsing fonts on
    /// every use.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Loads <paramref name="fontBytes"/> as a face and registers it for
    /// <paramref name="family"/>. <paramref name="unicodeRange"/> (optional)
    /// restricts which codepoints the face applies to; when null the face
    /// covers everything. Returns <c>false</c> if the bytes are unrecognisable;
    /// fail-soft to match the stylesheet/image fetchers.
    /// </summary>
    public bool TryAdd(
        string family,
        bool bold,
        bool italic,
        ReadOnlySpan<byte> fontBytes,
        UnicodeRangeSet? unicodeRange = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(family);
        // Key on the canonical family name: surrounding quotes/whitespace are
        // CSS syntax, not part of the name, and must not split the key space.
        family = FontFamilyKey.Normalize(family);
        if (family.Length == 0)
        {
            return false;
        }

        if (fontBytes.Length == 0)
        {
            return false;
        }

        var bytes = fontBytes.ToArray();
        try
        {
            // Registration is the boundary where bad src entries must fail so
            // CSS Fonts fallback can continue to the next source.
            using var stream = new MemoryStream(bytes, writable: false);
            _ = new FontCollection().Add(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }

        if (!_byFamily.TryGetValue(family, out var faces))
        {
            faces = [];
            _byFamily[family] = faces;
        }
        faces.Add(new RegisteredFace(bold, italic, unicodeRange, bytes));
        Version++;
        return true;
    }

    /// <summary>
    /// Adds every registered face to <paramref name="collection"/> and returns
    /// an alias map from each declared <c>@font-face</c> <c>font-family</c> to
    /// the <see cref="FontFamily"/> handles SixLabors created for it. Used by
    /// the paint backend (<see cref="ImageSharpFontLookup.LoadCollection(FontFaceRegistry?)"/>)
    /// to fold web fonts into the per-render <c>FontCollection</c> snapshot.
    /// <para>
    /// SixLabors.Fonts names each family from the font's own <c>name</c> table
    /// on <c>FontCollection.Add</c>, ignoring the declared family. Google Fonts'
    /// per-weight instances mangle that name (the 500-weight Inter Tight file
    /// reports "Inter Tight Medium", the 600 reports "Inter Tight SemiBold"), so
    /// the collection can't be looked up by the name the CSS uses. The returned
    /// alias map restores the CSS contract: the declared family resolves to the
    /// faces registered for it, whatever the font files call themselves.
    /// </para>
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<FontFamily>> AddTo(FontCollection collection)
    {
        var aliases = new Dictionary<string, IReadOnlyList<FontFamily>>(StringComparer.OrdinalIgnoreCase);
        if (_disposed)
        {
            return aliases;
        }

        foreach (var (declaredFamily, faces) in _byFamily)
        {
            List<FontFamily>? loaded = null;
            foreach (var face in SelectRepresentativeFaces(faces))
            {
                using var stream = new MemoryStream(face.FontBytes, writable: false);
                (loaded ??= []).Add(collection.Add(stream));
            }
            if (loaded is { Count: > 0 })
            {
                aliases[declaredFamily] = loaded;
            }
        }
        return aliases;
    }

    // SixLabors' FontCollection keys faces by the font's own (family, style)
    // name from its `name` table and has no unicode-range awareness: adding
    // several same-named subset faces — exactly what Google Fonts' unicode-range
    // splitting produces (one @font-face per script: latin, latin-ext, cyrillic,
    // greek, …) — leaves a later resolve picking an arbitrary subset, frequently
    // a non-Latin one that lacks ASCII glyphs, so ordinary text rasterises as
    // .notdef tofu. Fold in at most one face per (bold, italic): the one whose
    // unicode-range covers Basic Latin (or that carries no range at all), so a
    // registered family always resolves to a face that actually holds its glyphs.
    // A (bold, italic) bucket containing only non-Latin subsets is dropped, which
    // lets the cascade's font-family fallback take over (e.g. → monospace) rather
    // than rendering tofu. Non-Latin codepoints fall back through the system font
    // stack the same way.
    private static IEnumerable<RegisteredFace> SelectRepresentativeFaces(List<RegisteredFace> faces)
    {
        // 'A' (U+0041) is a stable Basic-Latin probe: Google's "latin" subset
        // (U+0000–00FF) covers it; every other script subset excludes it.
        const int LatinProbe = 'A';

        foreach (var group in faces.GroupBy(f => (f.Bold, f.Italic)))
        {
            RegisteredFace? unrestricted = null;
            RegisteredFace? latin = null;
            foreach (var f in group)
            {
                if (f.UnicodeRange is null) { unrestricted = f; break; }
                if (latin is null && f.UnicodeRange.Contains(LatinProbe))
                {
                    latin = f;
                }
            }

            if ((unrestricted ?? latin) is { } pick)
            {
                yield return pick;
            }
        }
    }

    /// <summary>
    /// Returns the registered font bytes for the given family / weight / style,
    /// honouring <c>unicode-range</c> when <paramref name="probeCodepoint"/>
    /// is provided. Exact bold/italic match wins; otherwise the first
    /// covering face wins. Used by tests that exercise the registry directly;
    /// the paint backend consumes the registry via
    /// <see cref="AddTo"/>.
    /// </summary>
    internal bool TryGet(string family, bool bold, bool italic, int? probeCodepoint, out byte[] fontBytes)
    {
        if (_disposed || !_byFamily.TryGetValue(FontFamilyKey.Normalize(family), out var faces) || faces.Count == 0)
        {
            fontBytes = [];
            return false;
        }

        RegisteredFace? firstCovering = null;
        foreach (var f in faces)
        {
            if (probeCodepoint is int cp && f.UnicodeRange is { } range && !range.Contains(cp))
            {
                continue;
            }

            firstCovering ??= f;
            if (f.Bold == bold && f.Italic == italic)
            {
                fontBytes = f.FontBytes;
                return true;
            }
        }

        if (firstCovering is { } any)
        {
            fontBytes = any.FontBytes;
            return true;
        }

        fontBytes = [];
        return false;
    }

    /// <summary>
    /// Disposes the registry; subsequent registrations throw and no registered
    /// faces are exposed to paint backends.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _byFamily.Clear();
    }

    private readonly struct RegisteredFace
    {
        public RegisteredFace(bool bold, bool italic, UnicodeRangeSet? unicodeRange, byte[] fontBytes)
        {
            Bold = bold;
            Italic = italic;
            UnicodeRange = unicodeRange;
            FontBytes = fontBytes;
        }

        public bool Bold { get; }

        public bool Italic { get; }

        public UnicodeRangeSet? UnicodeRange { get; }

        public byte[] FontBytes { get; }
    }
}
