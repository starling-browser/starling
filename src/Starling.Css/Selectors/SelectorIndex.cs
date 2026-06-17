using Starling.Dom;

namespace Starling.Css.Selectors;

public sealed class SelectorIndex<T>
{
    private readonly Dictionary<string, List<SelectorIndexEntry<T>>> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SelectorIndexEntry<T>>> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SelectorIndexEntry<T>>> _attributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SelectorIndexEntry<T>>> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SelectorIndexEntry<T>> _universal = [];
    private int _sequence;

    public void Add(SelectorList selectorList, T value)
    {
        ArgumentNullException.ThrowIfNull(selectorList);
        foreach (var selector in selectorList.Selectors)
        {
            Add(selector, value);
        }
    }

    public void Add(ComplexSelector selector, T value)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var pseudo = selector.TargetPseudoElement;
        var entry = new SelectorIndexEntry<T>(selector, value, _sequence++, pseudo);

        foreach (var bucket in BucketsFor(selector))
        {
            AddToBucket(bucket, entry);
        }
    }

    public IReadOnlyList<SelectorIndexEntry<T>> GetCandidates(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var results = new List<SelectorIndexEntry<T>>();
        var seen = new HashSet<int>();
        GetCandidates(element, results, seen);
        return results;
    }

    public void GetCandidates(
        Element element,
        List<SelectorIndexEntry<T>> results,
        HashSet<int> seenSequences,
        bool filterPseudoElement = false,
        PseudoElement? pseudoElement = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(seenSequences);

        results.Clear();
        seenSequences.Clear();

        if (!string.IsNullOrEmpty(element.Id) && _ids.TryGetValue(element.Id, out var idEntries))
        {
            AddEntries(results, seenSequences, idEntries, filterPseudoElement, pseudoElement);
        }

        foreach (var className in element.ClassList)
        {
            if (_classes.TryGetValue(className, out var classEntries))
            {
                AddEntries(results, seenSequences, classEntries, filterPseudoElement, pseudoElement);
            }
        }

        foreach (var attribute in element.Attributes)
        {
            if (_attributes.TryGetValue(attribute.Name, out var attributeEntries))
            {
                AddEntries(results, seenSequences, attributeEntries, filterPseudoElement, pseudoElement);
            }
        }

        if (_tags.TryGetValue(element.LocalName, out var tagEntries))
        {
            AddEntries(results, seenSequences, tagEntries, filterPseudoElement, pseudoElement);
        }

        AddEntries(results, seenSequences, _universal, filterPseudoElement, pseudoElement);
        results.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
    }

    private static void AddTo(
        Dictionary<string, List<SelectorIndexEntry<T>>> dictionary,
        string key,
        SelectorIndexEntry<T> entry)
    {
        if (!dictionary.TryGetValue(key, out var entries))
        {
            entries = [];
            dictionary[key] = entries;
        }

        entries.Add(entry);
    }

    private void AddToBucket(Bucket bucket, SelectorIndexEntry<T> entry)
    {
        switch (bucket.Kind)
        {
            case BucketKind.Id:
                AddTo(_ids, bucket.Value!, entry);
                break;
            case BucketKind.Class:
                AddTo(_classes, bucket.Value!, entry);
                break;
            case BucketKind.Attribute:
                AddTo(_attributes, bucket.Value!, entry);
                break;
            case BucketKind.Tag:
                AddTo(_tags, bucket.Value!, entry);
                break;
            case BucketKind.Universal:
                _universal.Add(entry);
                break;
        }
    }

    private static List<Bucket> BucketsFor(ComplexSelector selector)
    {
        var buckets = new List<Bucket>();
        if (TryAddDirectBucket(selector.RightmostCompound, buckets))
        {
            return buckets;
        }

        if (TryAddFunctionalPseudoBuckets(selector.RightmostCompound, buckets))
        {
            return buckets;
        }

        buckets.Add(Bucket.Universal);
        return buckets;
    }

    private static bool TryAddDirectBucket(CompoundSelector compound, List<Bucket> buckets)
    {
        foreach (var simple in compound.SimpleSelectors)
        {
            if (simple is IdSelector id)
            {
                AddUnique(buckets, new Bucket(BucketKind.Id, id.Id));
                return true;
            }
        }

        foreach (var simple in compound.SimpleSelectors)
        {
            if (simple is ClassSelector @class)
            {
                AddUnique(buckets, new Bucket(BucketKind.Class, @class.ClassName));
                return true;
            }
        }

        foreach (var simple in compound.SimpleSelectors)
        {
            if (simple is AttributeSelector attribute)
            {
                AddUnique(buckets, new Bucket(BucketKind.Attribute, attribute.Name));
                return true;
            }
        }

        foreach (var simple in compound.SimpleSelectors)
        {
            if (simple is TypeSelector type)
            {
                AddUnique(buckets, new Bucket(BucketKind.Tag, type.LocalName));
                return true;
            }
        }

        return false;
    }

    private static bool TryAddFunctionalPseudoBuckets(CompoundSelector compound, List<Bucket> buckets)
    {
        var start = buckets.Count;
        foreach (var simple in compound.SimpleSelectors)
        {
            if (simple is not PseudoClassSelector { Argument: SelectorList list } pseudo)
            {
                continue;
            }

            if (pseudo.Name is not ("is" or "where" or "matches"))
            {
                continue;
            }

            TryAddSelectorListBuckets(list, buckets);
        }

        return buckets.Count > start;
    }

    private static bool TryAddSelectorListBuckets(SelectorList list, List<Bucket> buckets)
    {
        var start = buckets.Count;
        foreach (var selector in list.Selectors)
        {
            var selectorBuckets = new List<Bucket>();
            if (!TryAddDirectBucket(selector.RightmostCompound, selectorBuckets)
                && !TryAddFunctionalPseudoBuckets(selector.RightmostCompound, selectorBuckets))
            {
                while (buckets.Count > start)
                {
                    buckets.RemoveAt(buckets.Count - 1);
                }

                return false;
            }

            foreach (var bucket in selectorBuckets)
            {
                AddUnique(buckets, bucket);
            }
        }

        return buckets.Count > start;
    }

    private static void AddUnique(List<Bucket> buckets, Bucket bucket)
    {
        if (!buckets.Contains(bucket))
        {
            buckets.Add(bucket);
        }
    }

    private static void AddEntries(
        List<SelectorIndexEntry<T>> target,
        HashSet<int> seenSequences,
        IEnumerable<SelectorIndexEntry<T>> entries,
        bool filterPseudoElement,
        PseudoElement? pseudoElement)
    {
        foreach (var entry in entries)
        {
            if (filterPseudoElement && entry.PseudoElementTarget != pseudoElement)
            {
                continue;
            }

            if (seenSequences.Add(entry.Sequence))
            {
                target.Add(entry);
            }
        }
    }

    private enum BucketKind
    {
        Id,
        Class,
        Attribute,
        Tag,
        Universal,
    }

    private readonly record struct Bucket(BucketKind Kind, string? Value)
    {
        public static Bucket Universal => new(BucketKind.Universal, null);
    }
}

public sealed record SelectorIndexEntry<T>(
    ComplexSelector Selector,
    T Value,
    int Sequence,
    PseudoElement? PseudoElementTarget = null);
