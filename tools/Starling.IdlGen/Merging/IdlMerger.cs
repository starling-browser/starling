using Starling.IdlGen.Model;

namespace Starling.IdlGen.Merging;

// Folds the parsed fragments into one WebIdlModel. The pass order follows the
// Web IDL spec: merge partials, then apply includes, then the rest is keyed by
// name. Inheritance is left as the Inherits link on each definition and walked
// later by the emitter.
public static class IdlMerger
{
    public static WebIdlModel Merge(IEnumerable<IdlDocument> docs)
    {
        var defs = docs.SelectMany(d => d.Definitions).ToList();

        var interfaces = MergeInterfaces(defs.OfType<IdlInterface>().Where(i => !i.Mixin));
        var mixins = MergeInterfaces(defs.OfType<IdlInterface>().Where(i => i.Mixin));
        var dictionaries = MergeDictionaries(defs.OfType<IdlDictionary>());
        var namespaces = MergeNamespaces(defs.OfType<IdlNamespace>());

        var enums = LastWins(defs.OfType<IdlEnum>());
        var callbacks = LastWins(defs.OfType<IdlCallback>());

        var typedefs = new Dictionary<string, IdlType>(StringComparer.Ordinal);
        foreach (var td in defs.OfType<IdlTypedef>())
        {
            typedefs[td.Name] = td.Type;
        }

        var unresolved = ApplyIncludes(interfaces, mixins, defs.OfType<IdlIncludes>());

        return new WebIdlModel
        {
            Interfaces = interfaces,
            Mixins = mixins,
            Dictionaries = dictionaries,
            Enums = enums,
            Callbacks = callbacks,
            Namespaces = namespaces,
            Typedefs = typedefs,
            UnresolvedIncludes = unresolved,
        };
    }

    private static Dictionary<string, IdlInterface> MergeInterfaces(IEnumerable<IdlInterface> group)
    {
        var result = new Dictionary<string, IdlInterface>(StringComparer.Ordinal);
        foreach (var byName in group.GroupBy(i => i.Name, StringComparer.Ordinal))
        {
            var primary = byName.FirstOrDefault(i => !i.Partial) ?? byName.First();
            var members = new List<IdlMember>(primary.Members);
            foreach (var part in byName)
            {
                if (!ReferenceEquals(part, primary))
                {
                    members.AddRange(part.Members);
                }
            }

            result[byName.Key] = primary with
            {
                Members = members,
                ExtendedAttributes = byName.SelectMany(i => i.ExtendedAttributes).ToList(),
                Partial = false,
            };
        }
        return result;
    }

    private static Dictionary<string, IdlDictionary> MergeDictionaries(IEnumerable<IdlDictionary> group)
    {
        var result = new Dictionary<string, IdlDictionary>(StringComparer.Ordinal);
        foreach (var byName in group.GroupBy(d => d.Name, StringComparer.Ordinal))
        {
            var primary = byName.FirstOrDefault(d => !d.Partial) ?? byName.First();
            var members = new List<IdlDictionaryMember>(primary.Members);
            foreach (var part in byName)
            {
                if (!ReferenceEquals(part, primary))
                {
                    members.AddRange(part.Members);
                }
            }

            result[byName.Key] = primary with
            {
                Members = members,
                ExtendedAttributes = byName.SelectMany(d => d.ExtendedAttributes).ToList(),
                Partial = false,
            };
        }
        return result;
    }

    private static Dictionary<string, IdlNamespace> MergeNamespaces(IEnumerable<IdlNamespace> group)
    {
        var result = new Dictionary<string, IdlNamespace>(StringComparer.Ordinal);
        foreach (var byName in group.GroupBy(n => n.Name, StringComparer.Ordinal))
        {
            var primary = byName.FirstOrDefault(n => !n.Partial) ?? byName.First();
            var members = new List<IdlMember>(primary.Members);
            foreach (var part in byName)
            {
                if (!ReferenceEquals(part, primary))
                {
                    members.AddRange(part.Members);
                }
            }

            result[byName.Key] = primary with
            {
                Members = members,
                ExtendedAttributes = byName.SelectMany(n => n.ExtendedAttributes).ToList(),
                Partial = false,
            };
        }
        return result;
    }

    // Copies each included mixin's members onto the interface as distinct members.
    // Returns the interface names whose mixin was not found in the parsed set.
    private static List<string> ApplyIncludes(
        Dictionary<string, IdlInterface> interfaces,
        Dictionary<string, IdlInterface> mixins,
        IEnumerable<IdlIncludes> includes)
    {
        var unresolved = new List<string>();
        foreach (var inc in includes)
        {
            if (!interfaces.TryGetValue(inc.Name, out var iface)) { unresolved.Add($"{inc.Name} includes {inc.Mixin}"); continue; }
            if (!mixins.TryGetValue(inc.Mixin, out var mixin)) { unresolved.Add($"{inc.Name} includes {inc.Mixin}"); continue; }

            interfaces[inc.Name] = iface with { Members = [.. iface.Members, .. mixin.Members] };
        }
        return unresolved;
    }

    private static Dictionary<string, T> LastWins<T>(IEnumerable<T> defs) where T : IdlDefinition
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            result[d.Name] = d;
        }

        return result;
    }
}
