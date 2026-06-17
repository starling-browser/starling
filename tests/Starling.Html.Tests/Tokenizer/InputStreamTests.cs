using AwesomeAssertions;
using Starling.Html.InputStream;
namespace Starling.Html.Tests.Tokenizer;

[TestClass]
public class InputStreamTests
{
    [TestMethod]
    public void Empty_input_is_drained_after_end_of_input()
    {
        var s = new PreprocessedStream();
        s.EndOfInput();
        s.Read().Should().Be(-1);
        s.Peek().Should().Be(-1);
    }

    [TestMethod]
    public void Plain_ascii_passes_through()
    {
        var s = new PreprocessedStream();
        s.Feed("abc");
        s.EndOfInput();

        Drain(s).Should().Equal('a', 'b', 'c');
    }

    [TestMethod]
    public void Crlf_collapses_to_lf()
    {
        var s = new PreprocessedStream();
        s.Feed("a\r\nb");
        s.EndOfInput();

        Drain(s).Should().Equal('a', '\n', 'b');
    }

    [TestMethod]
    public void Lone_cr_becomes_lf()
    {
        var s = new PreprocessedStream();
        s.Feed("a\rb");
        s.EndOfInput();

        Drain(s).Should().Equal('a', '\n', 'b');
    }

    [TestMethod]
    public void Trailing_cr_flushes_to_lf_on_end_of_input()
    {
        var s = new PreprocessedStream();
        s.Feed("a\r");
        // Without EndOfInput we cannot decide whether '\r' is followed by '\n'.
        s.Peek().Should().Be('a');
        s.Read().Should().Be('a');
        s.Read().Should().Be(-1);

        s.EndOfInput();
        s.Read().Should().Be('\n');
        s.Read().Should().Be(-1);
    }

    [TestMethod]
    public void Null_passes_through_unchanged()
    {
        // Per WHATWG HTML §13.2.4 the preprocessor normalizes newlines but
        // leaves U+0000 alone; the tokenizer states (Data, TagName, …) decide
        // what to do with NULL.
        var s = new PreprocessedStream();
        s.Feed("a\0b");
        s.EndOfInput();

        Drain(s).Should().Equal('a', 0, 'b');
    }

    [TestMethod]
    public void Chunked_feed_preserves_normalization_across_boundary()
    {
        // '\r' arrives in one chunk; the deciding '\n' arrives in the next.
        // The preprocessor must hold the '\r' state and still produce a
        // single '\n' once it sees the '\n'.
        var s = new PreprocessedStream();
        s.Feed("a\r");
        s.Feed("\nb");
        s.EndOfInput();

        Drain(s).Should().Equal('a', '\n', 'b');
    }

    [TestMethod]
    public void Chunked_feed_lone_cr_followed_by_non_lf_emits_lf()
    {
        var s = new PreprocessedStream();
        s.Feed("a\r");
        s.Feed("x");
        s.EndOfInput();

        Drain(s).Should().Equal('a', '\n', 'x');
    }

    private static List<int> Drain(PreprocessedStream s)
    {
        var result = new List<int>();
        while (true)
        {
            var c = s.Read();
            if (c == -1)
            {
                return result;
            }

            result.Add(c);
        }
    }
}
