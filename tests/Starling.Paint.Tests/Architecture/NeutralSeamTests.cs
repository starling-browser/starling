// SPDX-License-Identifier: Apache-2.0
using System.Reflection;
using AwesomeAssertions;
using Starling.Paint.Backend;

namespace Starling.Paint.Tests.Architecture;

/// <summary>
/// Guards the renderer-neutral seam: the contract types in
/// <c>Starling.Paint.Backend</c>, <c>.Compositor</c> and <c>.DisplayList</c> —
/// everything a non-ImageSharp backend (e.g. Vello) must implement against —
/// must not expose any SixLabors type in their member signatures. The ImageSharp
/// <i>adapter</i> implementations (types whose name starts with "ImageSharp") are
/// exempt; they are allowed to use SixLabors freely behind the seam.
/// <para>
/// <see cref="KnownGpuLeaks"/> is the shrinking allow-list of leaks that the GPU
/// texture/device-context steps still have to remove. It must reach empty.
/// </para>
/// </summary>
[TestClass]
public sealed class NeutralSeamTests
{
    // Allow-list of remaining SixLabors leaks on the seam (Type.Member => SixLaborsType).
    // Shrinks to empty as the GPU steps land. DUMP MODE: leave empty to print actuals.
    private static readonly HashSet<string> KnownGpuLeaks = new(StringComparer.Ordinal)
    {
        // Empty: the renderer-neutral seam exposes no SixLabors type.
    };

    [TestMethod]
    public void Seam_types_expose_no_SixLabors_except_known_gpu_leaks()
    {
        var asm = typeof(IPaintBackend).Assembly;
        var leaks = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var t in asm.GetTypes())
        {
            if (!IsContractType(t))
            {
                continue;
            }

            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var c in t.GetConstructors(F))
            {
                foreach (var p in c.GetParameters())
                {
                    Record(leaks, t, ".ctor", p.ParameterType);
                }
            }

            foreach (var m in t.GetMethods(F))
            {
                if (m.IsSpecialName)
                {
                    continue; // property/event accessors covered below
                }

                Record(leaks, t, m.Name, m.ReturnType);
                foreach (var p in m.GetParameters())
                {
                    Record(leaks, t, m.Name, p.ParameterType);
                }
            }

            foreach (var pr in t.GetProperties(F))
            {
                Record(leaks, t, pr.Name, pr.PropertyType);
            }

            foreach (var fl in t.GetFields(F))
            {
                Record(leaks, t, fl.Name, fl.FieldType);
            }
        }

        var unexpected = leaks.Except(KnownGpuLeaks).ToList();
        var stale = KnownGpuLeaks.Except(leaks).ToList();
        unexpected.Should().BeEmpty("the neutral seam must not gain new SixLabors leaks");
        stale.Should().BeEmpty("remove allow-list entries once the leak is gone");
    }

    /// <summary>
    /// The renderer-neutral contract a second backend implements/consumes: the
    /// scene records (DisplayList namespace), the backend/compositor interfaces,
    /// and the two GPU hand-off DTOs. Adapter-internal helper classes (caches,
    /// offscreen layers) are NOT contract — they live behind the seam.
    /// </summary>
    private static bool IsContractType(Type t)
    {
        if (t.Name.StartsWith("ImageSharp", StringComparison.Ordinal))
        {
            return false;
        }

        if (t.Name.StartsWith('<'))
        {
            return false; // compiler-generated
        }

        return t.Namespace == "Starling.Paint.DisplayList"
            || (t.Namespace == "Starling.Paint.Backend" && !t.IsNested
                && (t.IsInterface
                    || t.Name.StartsWith("GpuPaint", StringComparison.Ordinal)
                    || t.Name == "PaintTextureFormat"))
            || (t.Namespace == "Starling.Paint.Compositor" && t.IsInterface);
    }

    private static void Record(SortedSet<string> leaks, Type owner, string member, Type t)
    {
        foreach (var r in Flatten(t))
        {
            if ((r.Namespace ?? "").StartsWith("SixLabors", StringComparison.Ordinal))
            {
                leaks.Add($"{owner.Name}.{member} => {r.Name}");
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type t)
    {
        if (t.HasElementType) { foreach (var e in Flatten(t.GetElementType()!)) { yield return e; } yield break; }
        yield return t;
        if (t.IsGenericType)
        {
            foreach (var a in t.GetGenericArguments())
            {
                foreach (var e in Flatten(a))
                {
                    yield return e;
                }
            }
        }
    }
}
