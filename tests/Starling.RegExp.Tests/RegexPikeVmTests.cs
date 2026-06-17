// SPDX-License-Identifier: Apache-2.0

using AwesomeAssertions;

namespace Starling.RegExp.Tests;

/// <summary>
/// VM-level (no JS surface) coverage for <see cref="RegexPikeVm"/>.
/// Bypasses RegExp/RegExpCtor so failures pin to the matcher.
/// </summary>
[TestClass]
public class RegexPikeVmTests
{
    private static Starling.RegExp.RegexMatch? Run(string pattern, string flags, string input, int start = 0)
    {
        RegexFlagParser.TryParse(flags, out var f, out _);
        var re = CompiledRegex.Compile(pattern, f);
        return re.Exec(input, start);
    }

    [TestMethod]
    public void Literal_string_matches_at_first_occurrence()
    {
        var m = Run("foo", "", "abc foo bar");
        m.Should().NotBeNull();
        m!.Start.Should().Be(4);
        m.End.Should().Be(7);
        m.Group(0).Should().Be("foo");
    }

    [TestMethod]
    public void Dot_matches_any_except_newline_unless_dotAll()
    {
        Run("a.b", "", "a\nb").Should().BeNull();
        Run("a.b", "s", "a\nb").Should().NotBeNull();
        Run("a.b", "", "axb")!.Group(0).Should().Be("axb");
    }

    [TestMethod]
    public void Anchors_start_and_end()
    {
        Run("^abc", "", "abcdef")!.Group(0).Should().Be("abc");
        Run("^abc", "", "xabcdef").Should().BeNull();
        Run("def$", "", "abcdef")!.Group(0).Should().Be("def");
        Run("def$", "", "abcdefx").Should().BeNull();
    }

    [TestMethod]
    public void Multiline_anchors_match_per_line()
    {
        Run("^foo$", "m", "hello\nfoo\nbar")!.Group(0).Should().Be("foo");
    }

    [TestMethod]
    public void Predefined_classes_match()
    {
        Run("\\d+", "", "abc123def")!.Group(0).Should().Be("123");
        Run("\\w+", "", "  hello!")!.Group(0).Should().Be("hello");
        Run("\\s+", "", "ab   cd")!.Group(0).Should().Be("   ");
        Run("\\D+", "", "12abc34")!.Group(0).Should().Be("abc");
    }

    [TestMethod]
    public void Character_classes_ranges_and_negation()
    {
        Run("[a-c]+", "", "xxabbcyz")!.Group(0).Should().Be("abbc");
        Run("[^0-9]+", "", "12abc34")!.Group(0).Should().Be("abc");
    }

    [TestMethod]
    public void Alternation_matches_first_alternative()
    {
        Run("cat|dog|bird", "", "saw a dog yesterday")!.Group(0).Should().Be("dog");
    }

    [TestMethod]
    public void Greedy_and_lazy_quantifiers()
    {
        Run("a+", "", "aaaab")!.Group(0).Should().Be("aaaa");
        Run("a+?", "", "aaaab")!.Group(0).Should().Be("a");
        Run("a*", "", "bbb")!.Group(0).Should().Be("");
        Run("a{2}", "", "aaaab")!.Group(0).Should().Be("aa");
        Run("a{2,3}", "", "aaaaa")!.Group(0).Should().Be("aaa");
        Run("a{2,}", "", "aaaaa")!.Group(0).Should().Be("aaaaa");
    }

    [TestMethod]
    public void Capture_groups_record_spans()
    {
        var m = Run("(\\d+)-(\\d+)", "", "abc 12-345 xyz");
        m.Should().NotBeNull();
        m!.Group(0).Should().Be("12-345");
        m.Group(1).Should().Be("12");
        m.Group(2).Should().Be("345");
    }

    [TestMethod]
    public void Non_capturing_groups_dont_create_a_slot()
    {
        var re = CompiledRegex.Compile("(?:foo)(bar)", RegexFlags.None);
        var m = re.Exec("foobar", 0);
        m.Should().NotBeNull();
        m!.Group(1).Should().Be("bar");
    }

    [TestMethod]
    public void Named_capture_groups_round_trip()
    {
        var re = CompiledRegex.Compile("(?<y>\\d{4})-(?<m>\\d{2})", RegexFlags.None);
        re.NamedCaptures.Should().ContainKey("y");
        re.NamedCaptures.Should().ContainKey("m");
        var m = re.Exec("2024-01", 0);
        m!.Group(re.NamedCaptures["y"]).Should().Be("2024");
        m.Group(re.NamedCaptures["m"]).Should().Be("01");
    }

    [TestMethod]
    public void Backreferences_match_prior_capture()
    {
        Run("(.)\\1", "", "aab")!.Group(0).Should().Be("aa");
        Run("(.)\\1", "", "abc").Should().BeNull();
    }

    [TestMethod]
    public void Positive_and_negative_lookahead()
    {
        Run("foo(?=bar)", "", "foobar")!.Group(0).Should().Be("foo");
        Run("foo(?=bar)", "", "foobaz").Should().BeNull();
        Run("foo(?!bar)", "", "foobaz")!.Group(0).Should().Be("foo");
        Run("foo(?!bar)", "", "foobar").Should().BeNull();
    }

    [TestMethod]
    public void Positive_and_negative_lookbehind()
    {
        Run("(?<=\\$)\\d+", "", "$42")!.Group(0).Should().Be("42");
        Run("(?<=\\$)\\d+", "", "#42").Should().BeNull();
        Run("(?<!\\$)\\d+", "", "#42")!.Group(0).Should().Be("42");
    }

    [TestMethod]
    public void Sticky_flag_anchors_at_start_position()
    {
        var re = CompiledRegex.Compile("foo", RegexFlags.Sticky);
        re.Exec("foobar", 0).Should().NotBeNull();
        re.Exec("foobar", 3).Should().BeNull();
    }

    [TestMethod]
    public void Case_insensitivity()
    {
        Run("abc", "i", "ABCDE")!.Group(0).Should().Be("ABC");
        Run("[a-z]+", "i", "XYZ")!.Group(0).Should().Be("XYZ");
    }

    [TestMethod]
    public void Word_boundaries()
    {
        Run("\\bfoo\\b", "", "foo bar")!.Group(0).Should().Be("foo");
        Run("\\bfoo\\b", "", "barfoobaz").Should().BeNull();
        Run("\\Bfoo\\B", "", "barfoobaz")!.Group(0).Should().Be("foo");
    }

    [TestMethod]
    public void Escaped_specials_match_literal_text()
    {
        Run("a\\.b", "", "a.b")!.Group(0).Should().Be("a.b");
        Run("a\\.b", "", "axb").Should().BeNull();
        Run("\\(\\)", "", "()")!.Group(0).Should().Be("()");
    }

    [TestMethod]
    public void Pathological_input_runs_in_linear_time()
    {
        // Pike VM is linear; classic backtracking PCRE engines would be
        // exponential in n on this. We just want a fast null result.
        var input = new string('a', 30);
        var m = Run("a*a*a*a*a*b", "", input);
        m.Should().BeNull();
    }

    [TestMethod]
    public void Invalid_pattern_throws_syntax_exception()
    {
        var act = () => CompiledRegex.Compile("[a-", RegexFlags.None);
        act.Should().Throw<RegexSyntaxException>();
    }

    [TestMethod]
    public void Unicode_property_escape_letter_matches_in_class()
    {
        Run("\\p{Letter}+", "", "abc 123")!.Group(0).Should().Be("abc");
    }

    [TestMethod]
    public void Hex_and_unicode_escapes_decode()
    {
        Run("\\x41", "", "A")!.Group(0).Should().Be("A");
        Run("\\u0041", "", "A")!.Group(0).Should().Be("A");
    }

    [TestMethod]
    public void SlowMatcher_googleClosure_uri_regex_on_long_input_does_not_overflow_native_stack()
    {
        // Google Closure's goog.uri.utils.split URL parser. The `(?=...)` lookahead
        // forces HasBackrefOrLookaround → slow recursive matcher. On a long uniform
        // payload (e.g. Google's `am=AAAA...` xjs URLs, ~2 KB) the recursive Split
        // at RegexPikeVm.cs:311 backtracks one character at a time, recursing once
        // per char and blowing the native stack. Real crash repro: loading
        // https://www.google.com via the headless renderer.
        const string pattern =
            "^(?:([^:/?#.]+):)?(?://(?:([^\\/?#]*)@)?([^\\/?#]*?)" +
            "(?::([0-9]+))?(?=[\\/?#]|$))?([^?#]+)?(?:\\?([^#]*))?(?:#([\\s\\S]*))?$";
        var input = "/" + new string('A', 2000);

        Exception? failure = null;
        var worker = new System.Threading.Thread(() =>
        {
            try { Run(pattern, "", input); }
            catch (Exception ex) { failure = ex; }
        }, maxStackSize: 256 * 1024);
        worker.Start();
        worker.Join();

        failure.Should().BeNull();
    }

    [TestMethod]
    public void AddThread_split_chain_does_not_overflow_native_stack()
    {
        // Each (?:|a) compiles to Split(jmp-to-next, char). The Arg1 path
        // (empty branch) flows directly into the next Split, so the
        // epsilon-closure walk that AddThread performs recurses once per
        // group. Pre-fix AddThread used true recursion on the Arg1 branch,
        // overflowing the native stack on long chains. Run on a small-stack
        // worker so the regression is caught regardless of the main test
        // thread's stack.
        const int reps = 8000;
        var sb = new System.Text.StringBuilder(reps * 6);
        for (var i = 0; i < reps; i++)
        {
            sb.Append("(?:|a)");
        }

        var pattern = sb.ToString();

        RegexMatch? result = null;
        Exception? failure = null;
        var worker = new System.Threading.Thread(() =>
        {
            try { result = Run(pattern, "", ""); }
            catch (Exception ex) { failure = ex; }
        }, maxStackSize: 256 * 1024);
        worker.Start();
        worker.Join();

        failure.Should().BeNull();
        result.Should().NotBeNull();
        result!.Group(0).Should().Be("");
    }
}
