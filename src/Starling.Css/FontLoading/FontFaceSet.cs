namespace Starling.Css.FontLoading;

/// <summary>
/// A mutable, ordered set of <see cref="FontFace"/> objects used to track the
/// fonts available to a document. CSS Font Loading Level 3 §4
/// (<see href="https://www.w3.org/TR/css-font-loading-3/#fontfaceset-interface"/>).
/// </summary>
/// <remarks>
/// <para>
/// This is the pure-managed model slice. Out of scope for this slice:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The <c>ready</c> promise and <c>loading</c> / <c>loadingdone</c> events
///     (§4.3). Event firing and promise integration require the Starling JS engine
///     and document lifecycle hooks — deferred.
///   </description></item>
///   <item><description>
///     Automatic population from a document's style sheets (<c>document.fonts</c>
///     binding) — deferred to the Starling DOM / bindings layer.
///   </description></item>
///   <item><description>
///     The <c>load(font, text)</c> method which returns a promise — deferred.
///   </description></item>
/// </list>
/// </remarks>
public sealed class FontFaceSet
{
    private readonly List<FontFace> _faces = [];

    /// <summary>
    /// The number of <see cref="FontFace"/> objects in the set.
    /// CSS Font Loading 3 §4.
    /// </summary>
    public int Count => _faces.Count;

    /// <summary>
    /// The aggregate load status of the set. Returns
    /// <see cref="FontFaceSetLoadStatus.Loading"/> when any member is in the
    /// <see cref="FontFaceLoadStatus.Loading"/> state; otherwise
    /// <see cref="FontFaceSetLoadStatus.Loaded"/>.
    /// CSS Font Loading 3 §4.2
    /// (<see href="https://www.w3.org/TR/css-font-loading-3/#dom-fontfaceset-status"/>).
    /// </summary>
    public FontFaceSetLoadStatus Status =>
        _faces.Any(f => f.Status == FontFaceLoadStatus.Loading)
            ? FontFaceSetLoadStatus.Loading
            : FontFaceSetLoadStatus.Loaded;

    /// <summary>
    /// Adds <paramref name="face"/> to the set. If the face is already a
    /// member this is a no-op, matching set semantics.
    /// CSS Font Loading 3 §4 — <c>add(font)</c>.
    /// </summary>
    /// <param name="face">The font face to add. Must not be <see langword="null"/>.</param>
    public void Add(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (!_faces.Contains(face))
        {
            _faces.Add(face);
        }
    }

    /// <summary>
    /// Removes <paramref name="face"/> from the set. Returns <see langword="true"/>
    /// if the face was present and was removed.
    /// CSS Font Loading 3 §4 — <c>delete(font)</c>.
    /// </summary>
    /// <param name="face">The font face to remove. Must not be <see langword="null"/>.</param>
    public bool Delete(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        return _faces.Remove(face);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="face"/> is a member
    /// of the set.
    /// CSS Font Loading 3 §4 — <c>has(font)</c>.
    /// </summary>
    /// <param name="face">The font face to test. Must not be <see langword="null"/>.</param>
    public bool Has(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        return _faces.Contains(face);
    }

    /// <summary>
    /// Removes all <see cref="FontFace"/> objects from the set.
    /// CSS Font Loading 3 §4 — <c>clear()</c>.
    /// </summary>
    public void Clear() => _faces.Clear();

    /// <summary>
    /// Returns an enumerator over the <see cref="FontFace"/> objects in
    /// insertion order.
    /// CSS Font Loading 3 §4 — iterable interface.
    /// </summary>
    public IEnumerable<FontFace> Faces => _faces;

    /// <summary>
    /// Returns <see langword="true"/> when the set contains a
    /// <see cref="FontFace"/> with a family name matching the <c>font</c>
    /// shorthand string <em>and</em> that face is in the
    /// <see cref="FontFaceLoadStatus.Loaded"/> state.
    /// CSS Font Loading 3 §4.4
    /// (<see href="https://www.w3.org/TR/css-font-loading-3/#font-face-set-check"/>).
    /// </summary>
    /// <param name="font">
    /// A font shorthand string whose family name component is matched against
    /// <see cref="FontFace.Family"/>. <b>Simplification:</b> this implementation
    /// performs a case-insensitive substring search for the family name rather
    /// than running the full CSS font-shorthand parser. Quoted and unquoted
    /// names are both accepted as long as a loaded face's family appears
    /// verbatim in the <paramref name="font"/> string (after quote stripping).
    /// </param>
    /// <param name="text">
    /// Optional text to check codepoint coverage for. Currently ignored in this
    /// model slice — full unicode-range intersection is a later step.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a matching loaded face was found;
    /// <see langword="false"/> otherwise.
    /// </returns>
    public bool Check(string font, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(font);

        // Strip surrounding quotes from each candidate family token so that
        // both `check("italic 16px 'Open Sans'")` and
        // `check("16px Open Sans")` work for family "Open Sans".
        var stripped = font.Replace("\"", string.Empty, StringComparison.Ordinal)
                           .Replace("'", string.Empty, StringComparison.Ordinal);

        foreach (var face in _faces)
        {
            if (face.Status != FontFaceLoadStatus.Loaded)
            {
                continue;
            }

            if (stripped.Contains(face.Family, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
