using System.Text;

namespace Starling.Url;

/// <summary>
/// URL value type modeled on the WHATWG URL spec
/// (<see href="https://url.spec.whatwg.org/#concept-url"/>). Constructed
/// either positionally or via the
/// <see cref="UrlParser.Parse(string)"/> state machine which populates the
/// optional <see cref="Username"/>/<see cref="Password"/> as well.
/// </summary>
public sealed record Url(
    string Scheme,
    string? Host,
    int? Port,
    string Path,
    string? Query,
    string? Fragment)
{
    /// <summary>Optional username component of the URL's authority.</summary>
    public string? Username { get; init; }

    /// <summary>Optional password component of the URL's authority.</summary>
    public string? Password { get; init; }

    public bool IsFile => Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
    public bool IsHttp => Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
    public bool IsHttps => Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    public bool IsData => Scheme.Equals("data", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if Scheme is one of <c>http</c>, <c>https</c>,
    /// <c>ws</c>, <c>wss</c>, <c>ftp</c>, <c>file</c> — the "special schemes"
    /// from §3.1 with built-in default port + authority rules.</summary>
    public bool IsSpecial => SpecialSchemes.IsSpecial(Scheme);

    /// <summary>Default port for the scheme, or null if none defined.</summary>
    public int? DefaultPort => SpecialSchemes.DefaultPort(Scheme);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Scheme).Append(':');
        if (Host is not null || IsFile)
        {
            sb.Append("//");
            if (Username is not null && Username.Length > 0)
            {
                sb.Append(Username);
                if (Password is not null && Password.Length > 0)
                {
                    sb.Append(':').Append(Password);
                }

                sb.Append('@');
            }
            if (Host is not null)
            {
                sb.Append(Host);
            }

            if (Port is int p)
            {
                sb.Append(':').Append(p);
            }
        }
        sb.Append(Path);
        if (Query is not null)
        {
            sb.Append('?').Append(Query);
        }

        if (Fragment is not null)
        {
            sb.Append('#').Append(Fragment);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Translate a <c>file://</c> URL to a local filesystem path. Throws if not
    /// a file URL.
    /// </summary>
    public string ToFileSystemPath()
    {
        if (!IsFile)
        {
            throw new InvalidOperationException($"URL is not a file:// URL: {this}");
        }

        // WHATWG: file URLs may have an empty host (file:///foo) or "localhost".
        // For relative file paths supplied as `file://./foo.html` we treat the
        // path verbatim. This is loose vs spec, but keeps local file inputs useful.
        var path = Path;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        return path;
    }
}
