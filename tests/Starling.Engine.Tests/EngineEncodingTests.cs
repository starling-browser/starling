using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using SixLabors.ImageSharp;
using Starling.Common.Encoding;
namespace Starling.Engine.Tests;

/// <summary>
/// Drives a curated subset of <see href="https://github.com/web-platform-tests/wpt/tree/master/encoding"/>
/// vendored under <c>testdata/wpt/encoding/</c>. Two corpora are exercised:
/// (1) <c>encodings.json</c> — every WHATWG label resolves to its
/// canonical encoding name via <see cref="WhatwgEncodingLabels"/>; and
/// (2) <c>decode-fixtures.json</c> — for each (label, byte-sequence)
/// pair the engine's <c>ResolveEncoding</c> + decoder produces the
/// expected string. The work-package gate is ≥ 95% pass on the
/// supported label set.
/// </summary>
[TestClass]
public class EngineEncodingTests
{
    private static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "wpt", "encoding");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    static EngineEncodingTests()
    {
        // Ensure the engine static ctor has run so CodePages is
        // registered before we call WhatwgEncodingLabels.TryGetEncoding
        // (xunit may execute test classes in any order).
        _ = new StarlingEngine();
    }

    [TestMethod]
    public void WhatwgEncodingLabels_resolves_every_label_in_wpt_encodings_json()
    {
        var path = Path.Combine(TestDataRoot, "encodings.json");
        File.Exists(path).Should().BeTrue($"missing fixture: {path}");

        var groups = JsonSerializer.Deserialize<List<EncodingGroup>>(
            File.ReadAllBytes(path),
            JsonOptions);
        groups.Should().NotBeNull();

        var misses = new List<string>();
        foreach (var group in groups!)
        {
            foreach (var enc in group.Encodings)
            {
                foreach (var label in enc.Labels)
                {
                    var canonical = WhatwgEncodingLabels.TryGetCanonicalName(label);
                    if (!string.Equals(canonical, enc.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        misses.Add($"{label} → {canonical ?? "(null)"} (expected {enc.Name})");
                    }
                }
            }
        }

        misses.Should().BeEmpty(
            $"all WHATWG labels should resolve to canonical names ({misses.Count} failures)");
    }

    [TestMethod]
    public void DecodeFixtures_pass_rate_meets_ninety_five_percent_gate()
    {
        var path = Path.Combine(TestDataRoot, "decode-fixtures.json");
        File.Exists(path).Should().BeTrue($"missing fixture: {path}");

        var fixtures = JsonSerializer.Deserialize<List<DecodeFixture>>(
            File.ReadAllBytes(path),
            JsonOptions);
        fixtures.Should().NotBeNull().And.NotBeEmpty();

        var total = fixtures!.Count;
        var failures = new List<string>();
        foreach (var fixture in fixtures)
        {
            var bytes = fixture.Bytes.Select(ParseHexByte).ToArray();
            // Drive through the engine's full resolution path: synthesise a
            // Content-Type header so we exercise ExtractCharset → label
            // normalisation → TryResolveEncoding.
            var contentType = $"text/html; charset={fixture.Label}";
            var encoding = StarlingEngine.ResolveEncoding(contentType, bytes);
            var actual = encoding.GetString(bytes);
            if (!string.Equals(actual, fixture.Expected, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{fixture.Label} bytes=[{string.Join(",", fixture.Bytes)}] " +
                    $"→ {Escape(actual)} (expected {Escape(fixture.Expected)})");
            }
        }

        var passed = total - failures.Count;
        var passRate = (double)passed / total;
        passRate.Should().BeGreaterThanOrEqualTo(
            0.95,
            $"WPT encoding subset gate is ≥ 95% (got {passed}/{total} = {passRate:P1}). " +
            $"Failures:\n  - {string.Join("\n  - ", failures)}");
    }

    [TestMethod]
    public void MetaCharset_windows_1252_decodes_0x92_as_right_single_quote()
    {
        // Mirrors the wp:M2-07d acceptance criterion: a page declaring
        // <meta charset="windows-1252"> containing 0x92 must render as
        // U+2019 (right single quote), not '?' or U+FFFD.
        var html =
            "<!doctype html><html><head><meta charset=\"windows-1252\"></head>" +
            "<body><p>It" + (char)0x92 + "s working</p></body></html>";
        var bytes = System.Text.Encoding.Latin1.GetBytes(html);

        // No HTTP charset → falls through to meta sniff.
        var encoding = StarlingEngine.ResolveEncoding(contentType: null, bytes);
        var decoded = encoding.GetString(bytes);

        decoded.Should().Contain("It’s working");
        decoded.Should().NotContain("�");
        decoded.Should().NotContain("?s working");
    }

    [TestMethod]
    public async Task RenderAsync_uses_windows_1252_for_smart_quote_meta_charset()
    {
        // End-to-end: stub HTTP serves a windows-1252 page with byte 0x92;
        // the engine must extract the smart quote text from the rendered DOM.
        var html =
            "<!doctype html><html><head><meta charset=\"windows-1252\"></head>" +
            "<body><p>smart" + (char)0x92 + "quote</p></body></html>";
        var body = System.Text.Encoding.Latin1.GetBytes(html);
        using var server = await StubHttpServer.StartAsync(_ =>
        {
            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            var combined = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, combined, 0, head.Length);
            Buffer.BlockCopy(body, 0, combined, head.Length, body.Length);
            return combined;
        });

        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://localhost:{server.Port}/win1252",
                new RenderOptions(new Size(320, 180), 16f),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            result.Value.DisplayText.Should().Be("smart’quote");
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static byte ParseHexByte(string s)
    {
        // Fixture format: "0xNN".
        var trimmed = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return Convert.ToByte(trimmed, 16);
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var ch in s)
        {
            if (ch < 0x20 || ch == 0x7F || ch > 0x7E)
            {
                sb.Append($"\\u{(int)ch:X4}");
            }
            else
            {
                sb.Append(ch);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private sealed class EncodingGroup
    {
        public string Heading { get; set; } = "";
        public List<EncodingEntry> Encodings { get; set; } = [];
    }

    private sealed class EncodingEntry
    {
        public List<string> Labels { get; set; } = [];
        public string Name { get; set; } = "";
    }

    private sealed class DecodeFixture
    {
        public string Label { get; set; } = "";
        public string Source { get; set; } = "";
        public List<string> Bytes { get; set; } = [];
        public string Expected { get; set; } = "";
    }
}
