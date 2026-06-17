// SPDX-License-Identifier: Apache-2.0
using System.Reflection;
using AwesomeAssertions;
using Starling.Css.Values;
using Starling.Layout;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using Starling.Paint.NeutralStub;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests.Architecture;

/// <summary>
/// The capstone "unleak" proof: a paint backend living in the SixLabors-free
/// <c>Starling.Paint.NeutralStub</c> assembly implements the whole seam against
/// neutral types only. Its compilation (no SixLabors PackageReference) plus these
/// tests demonstrate a non-ImageSharp renderer (e.g. Vello) is expressible.
/// </summary>
[TestClass]
public sealed class NeutralStubProofTests
{
    [TestMethod]
    public void Stub_backend_renders_a_display_list_via_the_neutral_seam()
    {
        var factory = new NeutralStubBackendFactory();
        PaintBackendSelector.RegisterFactory(factory);
        PaintBackendSelector.FactoryFor(PaintBackendKind.NeutralStub).Should().BeSameAs(factory);

        using var backend = factory.CreateBackend(FontResolver.Default, webFonts: null);
        var list = new PaintList();
        list.Add(new FillRect(new Rect(0, 0, 4, 4), CssColor.FromSrgb(1, 0, 0), FillRectPixelAlignment.Preserve));

        var bmp = backend.Render(list, new Rect(0, 0, 4, 4), 1f);

        bmp.Width.Should().Be(4);
        bmp.Height.Should().Be(4);
        var center = ((2 * 4) + 2) * 4;
        bmp.Rgba[center].Should().Be(255, "the red FillRect was consumed from the neutral display list");
        bmp.Rgba[center + 1].Should().Be(0);
        bmp.Rgba[center + 3].Should().Be(255);
    }

    [TestMethod]
    public void Stub_assembly_uses_no_SixLabors_type()
    {
        var asm = typeof(NeutralStubBackendFactory).Assembly;
        var leaks = new SortedSet<string>(StringComparer.Ordinal);
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var t in asm.GetTypes())
        {
            if (t.Name.StartsWith('<'))
            {
                continue;
            }

            foreach (var c in t.GetConstructors(F))
            {
                foreach (var p in c.GetParameters())
                {
                    Check(leaks, t, p.ParameterType);
                }
            }

            foreach (var m in t.GetMethods(F))
            {
                Check(leaks, t, m.ReturnType);
                foreach (var p in m.GetParameters())
                {
                    Check(leaks, t, p.ParameterType);
                }
            }
            foreach (var pr in t.GetProperties(F))
            {
                Check(leaks, t, pr.PropertyType);
            }

            foreach (var fl in t.GetFields(F))
            {
                Check(leaks, t, fl.FieldType);
            }
        }

        leaks.Should().BeEmpty("the proof backend implements the seam without any SixLabors type");
    }

    private static void Check(SortedSet<string> leaks, Type owner, Type t)
    {
        foreach (var r in Flatten(t))
        {
            if ((r.Namespace ?? "").StartsWith("SixLabors", StringComparison.Ordinal))
            {
                leaks.Add($"{owner.Name} => {r.Name}");
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
