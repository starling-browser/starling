using System.Text;
using AwesomeAssertions;
namespace Starling.Net.Tests.Http;

/// <summary>
/// Live-network acceptance test for M2-05. Skipped by default. To run:
/// <code>STARLING_LIVE_HTTP_TESTS=1 dotnet test</code>.
/// </summary>
[TestClass]
public class LiveHttpsTests
{
    [TestMethod]
    public async Task GET_example_com_returns_200_and_HTML_body()
    {
        if (Environment.GetEnvironmentVariable("STARLING_LIVE_HTTP_TESTS") != "1")
        {
            return;
        }

        using var client = new StarlingHttpClient(new StarlingHttpClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            RequestTimeout = TimeSpan.FromSeconds(30),
        });

        var result = await client.GetAsync("https://example.com/", CancellationToken.None);

        result.IsOk.Should().BeTrue($"GET failed: {(result.IsOk ? "" : result.Error.ToString())}");
        var resp = result.Value;
        resp.StatusCode.Should().Be(200);

        var text = Encoding.UTF8.GetString(resp.Body.Span);
        text.Should().Contain("<html", "the body should be HTML");
        text.Should().Contain("Example Domain");
    }

    [TestMethod]
    public async Task GET_over_http2_upgrades_via_alpn_and_returns_200()
    {
        if (Environment.GetEnvironmentVariable("STARLING_LIVE_HTTP_TESTS") != "1")
        {
            return;
        }

        // Default ALPN is "h2, http/1.1"; a modern CDN negotiates h2.
        using var client = new StarlingHttpClient(new StarlingHttpClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            RequestTimeout = TimeSpan.FromSeconds(30),
        });

        var result = await client.GetAsync("https://www.cloudflare.com/", CancellationToken.None);

        result.IsOk.Should().BeTrue($"GET failed: {(result.IsOk ? "" : result.Error.ToString())}");
        var resp = result.Value;
        resp.HttpVersion.Should().Be("HTTP/2", "ALPN should have negotiated h2");
        resp.StatusCode.Should().Be(200);
        Encoding.UTF8.GetString(resp.Body.Span).Should().Contain("<html");

        // Connection security is captured for the lock popover.
        resp.Security.Should().NotBeNull();
        resp.Security!.Protocol.Should().Be("HTTP/2");
        resp.Security.IsEncrypted.Should().BeTrue();
        resp.Security.IsSecure.Should().BeTrue("the chain validated against the bundled root store");
        resp.Security.Certificate.Should().NotBeNull();
        resp.Security.Certificate!.Subject.Should().NotBeNullOrWhiteSpace();
        resp.Security.Certificate.Issuer.Should().NotBeNullOrWhiteSpace();
        resp.Security.Certificate.NotAfter.Should().BeAfter(DateTimeOffset.UtcNow);
    }
}
