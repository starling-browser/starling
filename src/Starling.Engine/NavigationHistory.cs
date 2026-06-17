namespace Starling.Engine;

/// <summary>
/// Browser-style session history: navigate appends, Back/Forward move the
/// current index, and a new navigation after Back discards forward entries.
/// </summary>
public sealed class NavigationHistory
{
    private readonly List<string> _entries = [];
    private int _index = -1;

    public string? Current => _index >= 0 ? _entries[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index + 1 < _entries.Count;
    public int Count => _entries.Count;
    public int Index => _index;
    public IReadOnlyList<string> Entries => _entries;

    public string Navigate(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (_index + 1 < _entries.Count)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(url);
        _index = _entries.Count - 1;
        return url;
    }

    public string Back()
    {
        if (!CanGoBack)
        {
            throw new InvalidOperationException("Cannot go back because there is no previous history entry.");
        }

        _index--;
        return _entries[_index];
    }

    public string Forward()
    {
        if (!CanGoForward)
        {
            throw new InvalidOperationException("Cannot go forward because there is no next history entry.");
        }

        _index++;
        return _entries[_index];
    }

    public string Reload()
        => Current ?? throw new InvalidOperationException("Cannot reload before the first navigation.");
}
