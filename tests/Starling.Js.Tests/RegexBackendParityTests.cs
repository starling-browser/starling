using AwesomeAssertions;
using Starling.Js.Runtime.Regex;
using Starling.RegExp;

namespace Starling.Js.Tests;

/// <summary>
/// Parity between the two regex backends. Each case compiles the SAME pattern on
/// both the Pike VM (<c>starling</c>) and the .NET delegation (<c>dotnet</c>)
/// backend and asserts their exec output is identical: numbered groups, named
/// groups, the match index/span, the indices(d) spans, and sticky(y) iteration.
///
/// The tests are backend-explicit — they call
/// <see cref="RegexBackendSelector.Compile(string, RegexFlags, RegexBackendKind)"/>
/// directly rather than relying on the process env var, which the selector reads
/// only once. So they prove parity regardless of how the process is launched.
/// </summary>
[TestClass]
public class RegexBackendParityTests
{
    private static IRegexMatcher Starling(string p, RegexFlags f)
        => RegexBackendSelector.Compile(p, f, RegexBackendKind.Starling);

    private static IRegexMatcher DotNet(string p, RegexFlags f)
        => RegexBackendSelector.Compile(p, f, RegexBackendKind.DotNet);

    // ------------------------------------------------------------------
    // Cases that MUST translate to .NET (prove the .NET path is exercised).
    // ------------------------------------------------------------------
    public static IEnumerable<object[]> Translatable =>
        new List<object[]>
        {
            // pattern, input, flags
            new object[] { @"(\d+)-(\d+)", "ab 12-345 cd", RegexFlags.None },              // numbered captures
            new object[] { @"(?<y>\d{4})-(?<m>\d{2})", "x 2026-06 y", RegexFlags.None },   // named captures
            new object[] { @"(a)(b)(c)", "zabc", RegexFlags.None },                         // multiple unnamed
            new object[] { @"(a)(?<mid>b)(c)", "zabc", RegexFlags.None },                   // mixed named/unnamed ordering
            new object[] { @"foo|bar|baz", "xxbaryy", RegexFlags.None },                    // alternation
            new object[] { @"(\w+)\s+\1", "hello hello world", RegexFlags.None },           // backreference
            new object[] { @"(?<=\$)\d+", "price $42 now", RegexFlags.None },               // lookbehind
            new object[] { @"a*", "aaab", RegexFlags.None },                                // zero-width / greedy
            new object[] { @"x?", "y", RegexFlags.None },                                   // zero-width at start
            new object[] { @"\bword\b", "a word here", RegexFlags.None },                   // word boundary
            new object[] { @"^line$", "line\nline", RegexFlags.Multiline },                 // multiline
            new object[] { @"HELLO", "say hello", RegexFlags.IgnoreCase },                  // ignoreCase ASCII
            new object[] { @"(?<n>\d+)", "a1b22c", RegexFlags.HasIndices },                 // indices(d) + named
            new object[] { @"(ab)+", "ababab", RegexFlags.None },                           // quantified group
            new object[] { @"colou?r", "color colour", RegexFlags.Global },                 // global iteration
            new object[] { @"\d", "a1b", RegexFlags.Sticky },                               // sticky
        };

    // ------------------------------------------------------------------
    // Cases that MUST fall back to the Pike VM (translatability guard).
    // ------------------------------------------------------------------
    public static IEnumerable<object[]> FallsBack =>
        new List<object[]>
        {
            new object[] { @"\p{L}+", "abc", RegexFlags.None },                  // \p{ property escape
            new object[] { @"\P{L}", "a-b", RegexFlags.None },                   // \P{ property escape
            new object[] { @".", "café", RegexFlags.Unicode },                  // u flag
            // dotAll: .NET rejects RegexOptions.ECMAScript | Singleline at
            // construction, so the translator falls back to the Pike VM. The
            // pattern still works correctly there — this is a safe fallback.
            new object[] { @"a.c", "a\nc", RegexFlags.DotAll },
            new object[] { @"café", "a café", RegexFlags.IgnoreCase },          // i + non-ASCII literal
            new object[] { @"é", "é", RegexFlags.IgnoreCase },             // i + \u escape > 0x7F
        };

    [TestMethod]
    [DynamicData(nameof(Translatable))]
    public void Translatable_backends_agree(string pattern, string input, RegexFlags flags)
    {
        // The .NET backend must actually translate these (not silently fall back),
        // otherwise the parity assertion would be vacuous.
        var dn = DotNet(pattern, flags);
        dn.Should().BeOfType<DotNetRegexMatcher>(
            $"pattern /{pattern}/ ({flags}) is in the translatable set");

        AssertParity(pattern, input, flags, Starling(pattern, flags), dn);
    }

    [TestMethod]
    [DynamicData(nameof(FallsBack))]
    public void NonTranslatable_falls_back_to_pike(string pattern, string input, RegexFlags flags)
    {
        var dn = DotNet(pattern, flags);
        dn.Should().BeOfType<StarlingRegexMatcher>(
            $"pattern /{pattern}/ ({flags}) must fall back to the Pike VM");

        // Still parity-check (both are the Pike VM here, but the harness stays uniform).
        AssertParity(pattern, input, flags, Starling(pattern, flags), dn);
    }

    [TestMethod]
    public void Metadata_matches_pike_for_translatable()
    {
        // CaptureCount + NamedCaptures come from the Pike VM form on both backends.
        var s = Starling(@"(a)(?<mid>b)(c)", RegexFlags.None);
        var d = DotNet(@"(a)(?<mid>b)(c)", RegexFlags.None);
        d.CaptureCount.Should().Be(s.CaptureCount).And.Be(3);
        d.NamedCaptures.Should().BeEquivalentTo(s.NamedCaptures);
        d.NamedCaptures["mid"].Should().Be(2); // JS left-to-right index, not .NET's post-unnamed numbering
    }

    [TestMethod]
    public void Sticky_iteration_agrees()
    {
        // Drive a sticky scan across the string on both backends and assert the
        // same lastIndex progression (the y-flag reject-unless-anchored rule).
        const string pattern = @"\d";
        const string input = "12a34";
        var s = Starling(pattern, RegexFlags.Sticky);
        var d = DotNet(pattern, RegexFlags.Sticky);
        d.Should().BeOfType<DotNetRegexMatcher>();

        int sPos = 0, dPos = 0;
        var sStarts = new List<int>();
        var dStarts = new List<int>();
        while (true)
        {
            var sm = s.Exec(input, sPos);
            var dm = d.Exec(input, dPos);
            (sm is null).Should().Be(dm is null);
            if (sm is null)
            {
                break;
            }

            sm!.Start.Should().Be(dm!.Start);
            sm.End.Should().Be(dm.End);
            sStarts.Add(sm.Start);
            dStarts.Add(dm.Start);
            sPos = sm.End == sm.Start ? sm.End + 1 : sm.End;
            dPos = dm.End == dm.Start ? dm.End + 1 : dm.End;
        }
        dStarts.Should().Equal(sStarts);
        // Sticky must NOT skip the non-digit 'a': it stops at index 2.
        sStarts.Should().Equal(new List<int> { 0, 1 });
    }

    [TestMethod]
    public void Global_iteration_agrees()
    {
        // Non-sticky scan forward from a moving start, same as the intrinsic does
        // for the g flag.
        const string pattern = @"\w+";
        const string input = "  foo   bar baz ";
        var s = Starling(pattern, RegexFlags.Global);
        var d = DotNet(pattern, RegexFlags.Global);
        d.Should().BeOfType<DotNetRegexMatcher>();

        int sPos = 0, dPos = 0;
        var sSpans = new List<(int, int)>();
        var dSpans = new List<(int, int)>();
        while (sPos <= input.Length)
        {
            var sm = s.Exec(input, sPos);
            var dm = d.Exec(input, dPos);
            (sm is null).Should().Be(dm is null);
            if (sm is null)
            {
                break;
            }

            sSpans.Add((sm!.Start, sm.End));
            dSpans.Add((dm!.Start, dm.End));
            sPos = sm.End == sm.Start ? sm.End + 1 : sm.End;
            dPos = dm.End == dm.Start ? dm.End + 1 : dm.End;
        }
        dSpans.Should().Equal(sSpans);
        sSpans.Should().Equal(new List<(int, int)> { (2, 5), (8, 11), (12, 15) });
    }

    // ------------------------------------------------------------------
    // Compare full exec output of two matchers at start=0.
    // ------------------------------------------------------------------
    private static void AssertParity(string pattern, string input, RegexFlags flags,
        IRegexMatcher expected, IRegexMatcher actual)
    {
        actual.Source.Should().Be(expected.Source);
        actual.Flags.Should().Be(expected.Flags);
        actual.CaptureCount.Should().Be(expected.CaptureCount);
        actual.NamedCaptures.Should().BeEquivalentTo(expected.NamedCaptures);

        var em = expected.Exec(input, 0);
        var am = actual.Exec(input, 0);

        (am is null).Should().Be(em is null,
            $"/{pattern}/ ({flags}) on \"{input}\": match presence must agree");
        if (em is null)
        {
            return;
        }

        am!.Start.Should().Be(em!.Start, $"/{pattern}/ start");
        am.End.Should().Be(em.End, $"/{pattern}/ end");

        // Numbered groups 0..CaptureCount (text + span, incl. non-participating null).
        for (int i = 0; i <= expected.CaptureCount; i++)
        {
            am.Group(i).Should().Be(em.Group(i), $"/{pattern}/ group {i} text");
            am.GroupSpan(i).Should().Be(em.GroupSpan(i), $"/{pattern}/ group {i} span");
        }

        // Named groups resolve to the same text via their JS index.
        foreach (var (name, idx) in expected.NamedCaptures)
        {
            am.Group(idx).Should().Be(em.Group(idx), $"/{pattern}/ named group '{name}'");
        }
    }
}
