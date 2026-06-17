namespace Starling.Gui.Core.Text;

/// <summary>
/// Models text editing with an input method editor (IME) composition — the
/// committed text plus an optional "preedit" (marked) string the user is still
/// composing, shown inline at the caret. Mirrors the `NSTextInputClient` shape
/// (set-marked-text, insert-text, delete-backward) so a native macOS text-input
/// driver can feed it, and is platform-neutral and unit-tested.
/// </summary>
/// <remarks>
/// Standard GLFW delivers committed composed characters through its character
/// callback. On macOS, the native bridge forwards marked-text updates from
/// <c>NSTextInputClient</c> into <see cref="SetMarkedText"/> and calls
/// <see cref="Insert"/> on commit, so the shell can draw inline preedit text.
/// </remarks>
public sealed class ImeComposition
{
    private string _committed = "";
    private int _caret;
    private string _preedit = "";

    /// <summary>The committed text, without the active preedit.</summary>
    public string CommittedText => _committed;

    /// <summary>Caret index within <see cref="CommittedText"/>.</summary>
    public int Caret => _caret;

    /// <summary>The active preedit (marked) string, empty when not composing.</summary>
    public string Preedit => _preedit;

    /// <summary>True while a composition is in progress.</summary>
    public bool IsComposing => _preedit.Length > 0;

    /// <summary>The text to display: committed text with the preedit spliced in at the caret.</summary>
    public string DisplayText => _preedit.Length == 0
        ? _committed
        : string.Concat(_committed.AsSpan(0, _caret), _preedit, _committed.AsSpan(_caret));

    /// <summary>Caret index within <see cref="DisplayText"/> (the end of the preedit).</summary>
    public int DisplayCaret => _caret + _preedit.Length;

    /// <summary>Resets to a field's current value and caret (e.g. on focus).</summary>
    public void Reset(string text, int caret)
    {
        _committed = text ?? "";
        _caret = Math.Clamp(caret, 0, _committed.Length);
        _preedit = "";
    }

    /// <summary>Sets or replaces the active preedit shown at the caret (no commit).</summary>
    public void SetMarkedText(string preedit) => _preedit = preedit ?? "";

    /// <summary>Drops the active preedit without committing it.</summary>
    public void CancelPreedit() => _preedit = "";

    /// <summary>
    /// Commits <paramref name="text"/> at the caret, replacing any active preedit.
    /// This is the path the GLFW character callback drives today.
    /// </summary>
    public void Insert(string text)
    {
        _preedit = "";
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _committed = string.Concat(_committed.AsSpan(0, _caret), text, _committed.AsSpan(_caret));
        _caret += text.Length;
    }

    /// <summary>Commits the active preedit into the text.</summary>
    public void CommitPreedit()
    {
        if (_preedit.Length == 0)
        {
            return;
        }

        var p = _preedit;
        Insert(p); // Insert clears the preedit first, then inserts the final text
    }

    /// <summary>
    /// Backspace: shortens the preedit while composing, else deletes the character
    /// before the caret in the committed text.
    /// </summary>
    public void Backspace()
    {
        if (_preedit.Length > 0)
        {
            _preedit = _preedit[..^1];
            return;
        }
        if (_caret > 0)
        {
            _committed = string.Concat(_committed.AsSpan(0, _caret - 1), _committed.AsSpan(_caret));
            _caret--;
        }
    }

    /// <summary>Moves the caret (only meaningful when not composing).</summary>
    public void MoveCaret(int caret)
    {
        if (_preedit.Length > 0)
        {
            return;
        }

        _caret = Math.Clamp(caret, 0, _committed.Length);
    }
}
