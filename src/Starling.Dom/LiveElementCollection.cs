using System.Collections;

namespace Starling.Dom;

internal sealed class LiveElementCollection : IReadOnlyList<Element>
{
    private readonly Document _document;
    private readonly Func<Element, bool> _predicate;
    private int _version = -1;
    private List<Element> _snapshot = [];

    public LiveElementCollection(Document document, Func<Element, bool> predicate)
    {
        _document = document;
        _predicate = predicate;
    }

    public int Count
    {
        get
        {
            RefreshIfNeeded();
            return _snapshot.Count;
        }
    }

    public Element this[int index]
    {
        get
        {
            RefreshIfNeeded();
            return _snapshot[index];
        }
    }

    public IEnumerator<Element> GetEnumerator()
    {
        RefreshIfNeeded();
        return _snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void RefreshIfNeeded()
    {
        if (_version == _document.MutationVersion)
        {
            return;
        }

        _snapshot = _document.Descendants().OfType<Element>().Where(_predicate).ToList();
        _version = _document.MutationVersion;
    }
}
