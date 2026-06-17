using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starling.IdlGen.Overrides;

// The override layer. For now it carries the skip list: IDL members the emitter
// must NOT generate because they need custom marshalling the mechanical mapping
// would get wrong (HTML tag-name casing, legacy platform objects, and so on).
// Each entry is "Interface.member" with a reason. This grows into the add /
// override / patch layers later.
// A custom binding for an attribute: the verbatim getter lambda and an optional
// setter lambda the emitter uses instead of the mechanical mapping.
public sealed record AttributeOverride(string Getter, string? Setter);

// A thin-dispatch binding for an operation (the Chromium model): convert the
// arguments, call the Starling DOM impl method, translate a DOM exception, and
// wrap the return. Params are type specs ("Node" = required interface arg,
// "Node?" = nullable interface arg). Trailing are fixed extra impl-call
// arguments (e.g. "null"). Returns is "node", "void", or "this".
public sealed record DispatchSpec(
    string Impl,
    IReadOnlyList<string> Params,
    IReadOnlyList<string> Trailing,
    bool Raises,
    string Returns,
    bool Static,
    bool PassRealm);

public sealed class OverrideSet
{
    private readonly HashSet<string> _skip;
    private readonly HashSet<string> _requiredSurface;
    private readonly Dictionary<string, string> _reasons;
    private readonly Dictionary<string, AttributeOverride> _attrOverrides;
    private readonly Dictionary<string, List<string>> _adds;
    private readonly Dictionary<string, DispatchSpec> _dispatch;

    private OverrideSet(HashSet<string> skip, HashSet<string> requiredSurface, Dictionary<string, string> reasons,
        Dictionary<string, AttributeOverride> attrOverrides, Dictionary<string, List<string>> adds,
        Dictionary<string, DispatchSpec> dispatch)
    {
        _skip = skip;
        _requiredSurface = requiredSurface;
        _reasons = reasons;
        _attrOverrides = attrOverrides;
        _adds = adds;
        _dispatch = dispatch;
    }

    public static OverrideSet Empty { get; } =
        new([], [], new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, AttributeOverride>(StringComparer.Ordinal),
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            new Dictionary<string, DispatchSpec>(StringComparer.Ordinal));

    // A thin-dispatch binding for an operation, used instead of the mechanical
    // method mapping.
    public DispatchSpec? DispatchFor(string iface, string member) =>
        _dispatch.GetValueOrDefault($"{iface}.{member}");

    // Verbatim binding statements to inject into an interface's installer. The
    // 'add' layer provides members the IDL does not describe (host extras).
    public IReadOnlyList<string> AddsFor(string iface) =>
        _adds.GetValueOrDefault(iface) ?? [];

    public bool ShouldSkip(string iface, string member, out string? reason)
    {
        if (_skip.Contains($"{iface}.{member}"))
        {
            _reasons.TryGetValue($"{iface}.{member}", out reason);
            return true;
        }
        reason = null;
        return false;
    }

    public bool IsSurfaceRequired(string iface, string member) =>
        _requiredSurface.Contains($"{iface}.{member}");

    public IReadOnlySet<string> RequiredSurface => _requiredSurface;

    // A custom attribute binding supplied in the override file, used instead of
    // the mechanical CLR mapping.
    public AttributeOverride? AttributeOverrideFor(string iface, string member) =>
        _attrOverrides.GetValueOrDefault($"{iface}.{member}");

    public static OverrideSet Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    public static OverrideSet Parse(string json)
    {
        var file = JsonSerializer.Deserialize<OverridesFile>(json, JsonOpts) ?? new OverridesFile();
        var skip = new HashSet<string>(file.Skip ?? [], StringComparer.Ordinal);
        var requiredSurface = new HashSet<string>(file.RequiredSurface ?? [], StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file.SkipReason is not null)
        {
            foreach (var kv in file.SkipReason)
            {
                reasons[kv.Key] = kv.Value;
            }
        }

        var attrOverrides = new Dictionary<string, AttributeOverride>(StringComparer.Ordinal);
        if (file.Override is not null)
        {
            foreach (var kv in file.Override)
            {
                if (kv.Value.Getter is { } getter)
                {
                    attrOverrides[kv.Key] = new AttributeOverride(getter, kv.Value.Setter);
                }
            }
        }

        var adds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (file.Add is not null)
        {
            foreach (var kv in file.Add)
            {
                adds[kv.Key] = kv.Value;
            }
        }

        var dispatch = new Dictionary<string, DispatchSpec>(StringComparer.Ordinal);
        if (file.Dispatch is not null)
        {
            foreach (var kv in file.Dispatch)
            {
                if (kv.Value.Impl is { } impl)
                {
                    dispatch[kv.Key] = new DispatchSpec(
                        impl, kv.Value.Params ?? [], kv.Value.Trailing ?? [],
                        kv.Value.Raises ?? false, kv.Value.Returns ?? "void",
                        kv.Value.Static ?? false, kv.Value.PassRealm ?? false);
                }
            }
        }

        return new OverrideSet(skip, requiredSurface, reasons, attrOverrides, adds, dispatch);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class OverridesFile
    {
        [JsonPropertyName("skip")] public List<string>? Skip { get; set; }
        [JsonPropertyName("requiredSurface")] public List<string>? RequiredSurface { get; set; }
        [JsonPropertyName("skipReason")] public Dictionary<string, string>? SkipReason { get; set; }
        [JsonPropertyName("override")] public Dictionary<string, OverrideEntry>? Override { get; set; }
        [JsonPropertyName("add")] public Dictionary<string, List<string>>? Add { get; set; }
        [JsonPropertyName("dispatch")] public Dictionary<string, DispatchEntry>? Dispatch { get; set; }
    }

    private sealed class OverrideEntry
    {
        [JsonPropertyName("getter")] public string? Getter { get; set; }
        [JsonPropertyName("setter")] public string? Setter { get; set; }
    }

    private sealed class DispatchEntry
    {
        [JsonPropertyName("impl")] public string? Impl { get; set; }
        [JsonPropertyName("params")] public List<string>? Params { get; set; }
        [JsonPropertyName("trailing")] public List<string>? Trailing { get; set; }
        [JsonPropertyName("raises")] public bool? Raises { get; set; }
        [JsonPropertyName("returns")] public string? Returns { get; set; }
        [JsonPropertyName("static")] public bool? Static { get; set; }
        [JsonPropertyName("passRealm")] public bool? PassRealm { get; set; }
    }
}
