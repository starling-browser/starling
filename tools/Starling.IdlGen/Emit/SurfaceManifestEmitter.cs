// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Model;
using Starling.IdlGen.Overrides;

namespace Starling.IdlGen.Emit;

public sealed class SurfaceManifestEmitter(WebIdlModel model, OverrideSet overrides)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Emit(IReadOnlyList<string> interfaceNames)
    {
        var members = new List<SurfaceMember>();
        foreach (var interfaceName in interfaceNames)
        {
            if (!model.Interfaces.TryGetValue(interfaceName, out var iface))
            {
                continue;
            }

            foreach (var member in iface.Members)
            {
                switch (member)
                {
                    case IdlAttribute attr:
                        members.Add(Attribute(interfaceName, attr));
                        break;
                    case IdlOperation op when op.Name is { } name:
                        members.Add(Operation(interfaceName, name, op));
                        break;
                    case IdlConstant constant:
                        members.Add(Constant(interfaceName, constant));
                        break;
                }
            }
        }

        var emittedNames = members
            .Select(m => $"{m.Interface}.{m.Name}")
            .ToHashSet(StringComparer.Ordinal);
        var missingRequired = overrides.RequiredSurface
            .Where(name => !emittedNames.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (missingRequired.Count > 0)
        {
            throw new InvalidOperationException(
                "Required IDL surface entries are not present in the merged model: " +
                string.Join(", ", missingRequired));
        }

        members = [.. members
            .OrderBy(m => m.Interface, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Kind, StringComparer.Ordinal)
            .ThenBy(m => m.ArgumentCount)];

        var manifest = new SurfaceManifest
        {
            Schema = 1,
            Source = "testdata/webref/idl",
            Interfaces = [.. interfaceNames],
            Members = members,
        };
        return JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine;
    }

    private SurfaceMember Attribute(string interfaceName, IdlAttribute attr)
    {
        var type = model.ResolveTypedef(attr.Type);
        return new SurfaceMember
        {
            Interface = interfaceName,
            Name = attr.Name,
            Kind = "attribute",
            Static = attr.Static,
            Type = TypeName(type),
            Nullable = type.Nullable,
            Readonly = attr.Readonly,
            Required = overrides.IsSurfaceRequired(interfaceName, attr.Name),
            Descriptor = new SurfaceDescriptor
            {
                Kind = "accessor",
                Enumerable = true,
                Configurable = true,
            },
        };
    }

    private SurfaceMember Operation(string interfaceName, string name, IdlOperation op)
    {
        var returnType = model.ResolveTypedef(op.ReturnType);
        int requiredArgs = RequiredArgumentCount(op.Arguments);
        return new SurfaceMember
        {
            Interface = interfaceName,
            Name = name,
            Kind = "operation",
            Static = op.Static,
            Type = TypeName(returnType),
            Nullable = returnType.Nullable,
            ArgumentCount = op.Arguments.Count,
            RequiredArguments = requiredArgs,
            Required = overrides.IsSurfaceRequired(interfaceName, name),
            Descriptor = new SurfaceDescriptor
            {
                Kind = "method",
                Writable = true,
                Enumerable = false,
                Configurable = true,
            },
        };
    }

    private SurfaceMember Constant(string interfaceName, IdlConstant constant)
    {
        var type = model.ResolveTypedef(constant.Type);
        return new SurfaceMember
        {
            Interface = interfaceName,
            Name = constant.Name,
            Kind = "constant",
            Static = false,
            Type = TypeName(type),
            Nullable = type.Nullable,
            Required = overrides.IsSurfaceRequired(interfaceName, constant.Name),
            Descriptor = new SurfaceDescriptor
            {
                Kind = "data",
                Writable = false,
                Enumerable = true,
                Configurable = false,
            },
        };
    }

    private static int RequiredArgumentCount(IReadOnlyList<IdlArgument> args)
    {
        int count = 0;
        foreach (var arg in args)
        {
            if (arg.Optional || arg.Variadic)
            {
                break;
            }

            count++;
        }
        return count;
    }

    private string TypeName(IdlType type)
    {
        if (type.IsUnion)
        {
            return "(" + string.Join(" or ", type.Union.Select(TypeName)) + ")" + (type.Nullable ? "?" : "");
        }

        string core = type.Name switch
        {
            "sequence" => $"sequence<{TypeName(type.TypeArgs[0])}>",
            "FrozenArray" => $"FrozenArray<{TypeName(type.TypeArgs[0])}>",
            "ObservableArray" => $"ObservableArray<{TypeName(type.TypeArgs[0])}>",
            "Promise" => $"Promise<{TypeName(type.TypeArgs[0])}>",
            "record" => $"record<{TypeName(type.TypeArgs[0])}, {TypeName(type.TypeArgs[1])}>",
            _ => type.Name,
        };
        return type.Nullable ? core + "?" : core;
    }
}

public sealed record SurfaceManifest
{
    [JsonPropertyName("schema")]
    public required int Schema { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("interfaces")]
    public required IReadOnlyList<string> Interfaces { get; init; }

    [JsonPropertyName("members")]
    public required IReadOnlyList<SurfaceMember> Members { get; init; }
}

public sealed record SurfaceMember
{
    [JsonPropertyName("interface")]
    public required string Interface { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("static")]
    public required bool Static { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("nullable")]
    public required bool Nullable { get; init; }

    [JsonPropertyName("readonly")]
    public bool? Readonly { get; init; }

    [JsonPropertyName("argumentCount")]
    public int? ArgumentCount { get; init; }

    [JsonPropertyName("requiredArguments")]
    public int? RequiredArguments { get; init; }

    [JsonPropertyName("required")]
    public required bool Required { get; init; }

    [JsonPropertyName("descriptor")]
    public required SurfaceDescriptor Descriptor { get; init; }
}

public sealed record SurfaceDescriptor
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("writable")]
    public bool? Writable { get; init; }

    [JsonPropertyName("enumerable")]
    public required bool Enumerable { get; init; }

    [JsonPropertyName("configurable")]
    public required bool Configurable { get; init; }
}
