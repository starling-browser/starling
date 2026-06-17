namespace Starling.Html.InputStream;

/// <summary>
/// Implements the input-stream preprocessor from
/// <see href="https://html.spec.whatwg.org/multipage/parsing.html#preprocessing-the-input-stream">
/// WHATWG HTML §13.2.4</see>, which delegates to
/// <see href="https://infra.spec.whatwg.org/#normalize-newlines">INFRA "normalize newlines"</see>:
/// <list type="bullet">
///   <item>Replace <c>U+000D U+000A</c> with a single <c>U+000A</c>.</item>
///   <item>Replace standalone <c>U+000D</c> with <c>U+000A</c>.</item>
/// </list>
/// <para>
/// NULL (<c>U+0000</c>) is <strong>not</strong> remapped here. Each tokenizer
/// state decides what to do with NULL per §13.2.5 — Data emits it verbatim
/// with a parse error; name-buffer states (tag name, attribute name, …) map
/// it to U+FFFD with a parse error. Doing the mapping at the preprocessor
/// would conflate a literal U+FFFD in source with a normalized NULL.
/// </para>
/// BOM stripping is the caller's responsibility (it happens in the byte-level
/// encoding sniffer per §13.2.3, which lives in <c>ByteSniffer</c> and lands
/// in M2 alongside networking).
/// </summary>
/// <remarks>
/// The preprocessor is push-driven so the tokenizer can consume bytes as they
/// arrive on the network. Call <see cref="Feed(System.ReadOnlySpan{char})"/>
/// repeatedly with successive chunks; on the final chunk call
/// <see cref="EndOfInput"/> so a trailing standalone <c>U+000D</c> is emitted.
/// </remarks>
public sealed class PreprocessedStream
{
    // A pending '\r' (U+000D) may need to suppress a following '\n' or be
    // emitted as '\n'. We can't decide until we see the next char (or EOF),
    // so we buffer it across Feed() calls.
    private bool _pendingCr;

    private readonly List<int> _buffer = [];
    private int _pos;

    /// <summary>
    /// Push more code points into the stream. Each character is treated as a
    /// UTF-16 code unit; surrogate pairs are not joined here (the tokenizer
    /// uses code units, not runes, because the spec's algorithms are defined
    /// in terms of code points but every state machine inspection is a single
    /// code-unit comparison — see <see href="https://html.spec.whatwg.org/multipage/parsing.html#tokenization"/>).
    /// </summary>
    public void Feed(ReadOnlySpan<char> chars)
    {
        foreach (var ch in chars)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (ch == '\n')
                {
                    // CRLF collapsed: emit only the '\n'.
                    _buffer.Add('\n');
                    continue;
                }
                // Standalone CR: emit '\n', fall through to process ch.
                _buffer.Add('\n');
            }

            switch (ch)
            {
                case '\r':
                    _pendingCr = true;
                    break;
                default:
                    // NULL is passed through; tokenizer states handle it.
                    _buffer.Add(ch);
                    break;
            }
        }
    }

    /// <summary>
    /// Signal end-of-input. Flushes a dangling <c>\r</c> as <c>\n</c>.
    /// Idempotent.
    /// </summary>
    public void EndOfInput()
    {
        if (_pendingCr)
        {
            _pendingCr = false;
            _buffer.Add('\n');
        }
    }

    /// <summary>
    /// Returns the next code point, or <c>-1</c> if the stream is currently
    /// drained. Callers receiving <c>-1</c> may push more bytes via
    /// <see cref="Feed(System.ReadOnlySpan{char})"/> and try again, or call
    /// <see cref="EndOfInput"/> followed by one more <see cref="Read"/> to
    /// flush a pending <c>\r</c>.
    /// </summary>
    public int Read()
    {
        if (_pos >= _buffer.Count)
        {
            return -1;
        }

        return _buffer[_pos++];
    }

    /// <summary>
    /// Look at the next code point without consuming it. <c>-1</c> if drained.
    /// </summary>
    public int Peek()
    {
        if (_pos >= _buffer.Count)
        {
            return -1;
        }

        return _buffer[_pos];
    }

    /// <summary>
    /// Look at the code point <paramref name="offset"/> positions past
    /// <see cref="Peek"/>. <c>-1</c> if past the buffered region.
    /// </summary>
    public int PeekAt(int offset)
    {
        var idx = _pos + offset;
        if (idx < 0 || idx >= _buffer.Count)
        {
            return -1;
        }

        return _buffer[idx];
    }

    /// <summary>Advance the read position by <paramref name="n"/> code points.</summary>
    public void Advance(int n)
    {
        _pos += n;
        if (_pos > _buffer.Count)
        {
            _pos = _buffer.Count;
        }
    }

    /// <summary>
    /// Number of code points still buffered.
    /// </summary>
    public int Remaining => _buffer.Count - _pos;
}
