using System.Text;
using Starling.Common;

namespace Starling.Url;

/// <summary>
/// WHATWG URL "basic URL parser" per
/// <see href="https://url.spec.whatwg.org/#concept-basic-url-parser">§4.4.1</see>.
/// </summary>
/// <remarks>
/// <para>
/// Implemented as a literal state machine over the ~21 spec states. State
/// names match the spec slugs (e.g. <see cref="State.SchemeStart"/> =
/// "scheme start state"). Cross-references in source comments cite the
/// section number.
/// </para>
/// <para>
/// Known gaps:
/// <list type="bullet">
///   <item>IDNA Punycode (xn--) is not performed. ASCII domains pass through.</item>
///   <item>IPv6 bracketed literals return an unsupported error.</item>
///   <item>Base-URL resolution (relative URLs against a base) is implemented
///         for the common cases but not exhaustively spec-compliant.</item>
/// </list>
/// </para>
/// </remarks>
public static class UrlParser
{
    public enum ParseError
    {
        Empty,
        MissingScheme,
        InvalidScheme,
        UnsupportedScheme,
        MalformedAuthority,
        InvalidHost,
        InvalidPort,
        InvalidCharacter,
        Unsupported, // e.g. IPv6 bracket literals
    }

    /// <summary>Parse an absolute URL. Returns a fully-populated <see cref="Url"/>.</summary>
    public static Result<Url, ParseError> Parse(string input)
        => Parse(input, baseUrl: null);

    /// <summary>
    /// Parse a (possibly relative) URL against an optional base. When
    /// <paramref name="baseUrl"/> is supplied, scheme-less inputs resolve
    /// against it per §4.4.
    /// </summary>
    public static Result<Url, ParseError> Parse(string input, Url? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result<Url, ParseError>.Err(ParseError.Empty);
        }

        // Strip leading/trailing C0 controls + space per §4.4 preamble.
        var trimmed = input.AsSpan().Trim();
        if (trimmed.Length == 0)
        {
            return Result<Url, ParseError>.Err(ParseError.Empty);
        }

        // Tab and newline removal per §4.4 (inputs may contain them when
        // sources like HTML attributes are concerned).
        var clean = StripTabsAndNewlines(trimmed.ToString());

        var sm = new StateMachine(clean, baseUrl);
        return sm.Run();
    }

    private static string StripTabsAndNewlines(string s)
    {
        if (s.IndexOfAny(['\t', '\n', '\r']) < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch != '\t' && ch != '\n' && ch != '\r')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // State machine implementation.
    // -----------------------------------------------------------------------

    private enum State
    {
        SchemeStart,
        Scheme,
        NoScheme,
        SpecialRelativeOrAuthority,
        PathOrAuthority,
        Relative,
        RelativeSlash,
        SpecialAuthoritySlashes,
        SpecialAuthorityIgnoreSlashes,
        Authority,
        Host,
        Port,
        File,
        FileSlash,
        FileHost,
        PathStart,
        Path,
        OpaquePath,
        Query,
        Fragment,
    }

    private sealed class StateMachine
    {
        private readonly string _input;
        private readonly Url? _base;
        private int _i;
        private State _state = State.SchemeStart;

        // Builder fields for the URL under construction.
        private readonly StringBuilder _scheme = new();
        private readonly StringBuilder _buffer = new();
        private string? _host;
        private int? _port;
        private string? _username;
        private string? _password;
        private List<string> _pathSegments = [];
        private string? _opaquePath;
        private string? _query;
        private string? _fragment;
        private bool _atSignSeen;
        private bool _isSpecial;

        public StateMachine(string input, Url? baseUrl)
        {
            _input = input;
            _base = baseUrl;
        }

        public Result<Url, ParseError> Run()
        {
            while (_i <= _input.Length)
            {
                var c = _i < _input.Length ? _input[_i] : (char)0xFFFF; // EOF sentinel
                var r = Step(c);
                if (r is not null)
                {
                    return r.Value;
                }

                _i++;
            }
            return Finish();
        }

        private Result<Url, ParseError>? Step(char c)
        {
            return _state switch
            {
                State.SchemeStart => StepSchemeStart(c),
                State.Scheme => StepScheme(c),
                State.NoScheme => StepNoScheme(c),
                State.SpecialRelativeOrAuthority => StepSpecialRelativeOrAuthority(c),
                State.PathOrAuthority => StepPathOrAuthority(c),
                State.Relative => StepRelative(c),
                State.RelativeSlash => StepRelativeSlash(c),
                State.SpecialAuthoritySlashes => StepSpecialAuthoritySlashes(c),
                State.SpecialAuthorityIgnoreSlashes => StepSpecialAuthorityIgnoreSlashes(c),
                State.Authority => StepAuthority(c),
                State.Host => StepHost(c),
                State.Port => StepPort(c),
                State.File => StepFile(c),
                State.FileSlash => StepFileSlash(c),
                State.FileHost => StepFileHost(c),
                State.PathStart => StepPathStart(c),
                State.Path => StepPath(c),
                State.OpaquePath => StepOpaquePath(c),
                State.Query => StepQuery(c),
                State.Fragment => StepFragment(c),
                _ => null,
            };
        }

        private bool IsEof(char c) => c == 0xFFFF;

        // §4.4.1 scheme start state
        private Result<Url, ParseError>? StepSchemeStart(char c)
        {
            if (IsAsciiAlpha(c))
            {
                _scheme.Append(char.ToLowerInvariant(c));
                _state = State.Scheme;
                return null;
            }
            // Not alpha → no scheme, decrement and reprocess in NoScheme.
            _i--;
            _state = State.NoScheme;
            return null;
        }

        // §4.4.1 scheme state
        private Result<Url, ParseError>? StepScheme(char c)
        {
            if (IsAsciiAlphanumeric(c) || c == '+' || c == '-' || c == '.')
            {
                _scheme.Append(char.ToLowerInvariant(c));
                return null;
            }
            if (c == ':')
            {
                var schemeStr = _scheme.ToString();
                _isSpecial = SpecialSchemes.IsSpecial(schemeStr);
                if (schemeStr == "file")
                {
                    _state = State.File;
                    return null;
                }
                if (_isSpecial && _base?.Scheme == schemeStr)
                {
                    _state = State.SpecialRelativeOrAuthority;
                    return null;
                }
                if (_isSpecial)
                {
                    _state = State.SpecialAuthoritySlashes;
                    return null;
                }
                if (_i + 1 < _input.Length && _input[_i + 1] == '/')
                {
                    _state = State.PathOrAuthority;
                    _i++;
                    return null;
                }
                // Non-special, no //: opaque path.
                _state = State.OpaquePath;
                _opaquePath = "";
                return null;
            }
            // Not a valid scheme char → back up and try NoScheme.
            _scheme.Clear();
            _i = -1;
            _state = State.NoScheme;
            return null;
        }

        // §4.4.1 no scheme state
        private Result<Url, ParseError>? StepNoScheme(char c)
        {
            if (_base is null)
            {
                return Result<Url, ParseError>.Err(ParseError.MissingScheme);
            }
            // Inherit from base.
            _scheme.Clear();
            _scheme.Append(_base.Scheme);
            _isSpecial = _base.IsSpecial;
            if (c == '#')
            {
                _pathSegments = SplitPath(_base.Path);
                _query = _base.Query;
                _fragment = "";
                _state = State.Fragment;
                return null;
            }
            if (_base.IsSpecial)
            {
                // Relative state will rebuild _pathSegments; only the opaque
                // branch should inherit _opaquePath. The previous unconditional
                // assignment masked the rebuilt segments in Run()'s output.
                _state = State.Relative;
            }
            else
            {
                _state = State.OpaquePath;
                _opaquePath = _base.Path;
            }
            _pathSegments = SplitPath(_base.Path);
            _host = _base.Host;
            _port = _base.Port;
            _username = _base.Username;
            _password = _base.Password;
            _query = _base.Query;
            _i--; // reprocess
            return null;
        }

        private Result<Url, ParseError>? StepSpecialRelativeOrAuthority(char c)
        {
            if (c == '/' && _i + 1 < _input.Length && _input[_i + 1] == '/')
            {
                _state = State.SpecialAuthorityIgnoreSlashes;
                _i++;
                return null;
            }
            _state = State.Relative;
            _i--;
            return null;
        }

        private Result<Url, ParseError>? StepPathOrAuthority(char c)
        {
            if (c == '/')
            {
                _state = State.Authority;
                return null;
            }
            _state = State.Path;
            _i--;
            return null;
        }

        // §4.4.1 relative state
        private Result<Url, ParseError>? StepRelative(char c)
        {
            // base inherited fields are already set in NoScheme.
            if (c == '/')
            {
                _state = State.RelativeSlash;
                return null;
            }
            if (_isSpecial && c == '\\')
            {
                _state = State.RelativeSlash;
                return null;
            }
            if (c == '?')
            {
                _query = "";
                _state = State.Query;
                return null;
            }
            if (c == '#')
            {
                _fragment = "";
                _state = State.Fragment;
                return null;
            }
            if (IsEof(c))
            {
                return null; // finish with inherited fields
            }
            // Anything else: pop last path segment, reprocess in Path.
            _query = null;
            if (_pathSegments.Count > 0)
            {
                _pathSegments.RemoveAt(_pathSegments.Count - 1);
            }

            _state = State.Path;
            _i--;
            return null;
        }

        private Result<Url, ParseError>? StepRelativeSlash(char c)
        {
            if (_isSpecial && (c == '/' || c == '\\'))
            {
                // Protocol-relative ("//host/path"): a fresh authority follows,
                // so the base path inherited in NoScheme must not leak in.
                _pathSegments.Clear();
                _state = State.SpecialAuthorityIgnoreSlashes;
                return null;
            }
            if (c == '/')
            {
                _pathSegments.Clear();
                _state = State.Authority;
                return null;
            }
            // Reprocess as Path with single leading /. An absolute-path relative
            // URL (e.g. "/other" against "https://x/dir/page") replaces the
            // base's path entirely, so drop the inherited segments here.
            _pathSegments.Clear();
            _state = State.Path;
            _i--;
            return null;
        }

        private Result<Url, ParseError>? StepSpecialAuthoritySlashes(char c)
        {
            if (c == '/' && _i + 1 < _input.Length && _input[_i + 1] == '/')
            {
                _state = State.SpecialAuthorityIgnoreSlashes;
                _i++;
                return null;
            }
            _state = State.SpecialAuthorityIgnoreSlashes;
            _i--;
            return null;
        }

        private Result<Url, ParseError>? StepSpecialAuthorityIgnoreSlashes(char c)
        {
            if (c == '/' || c == '\\')
            {
                return null; // skip
            }

            _state = State.Authority;
            _i--;
            return null;
        }

        // §4.4.1 authority state — buffer chars until @, then split userinfo;
        // continue collecting until a host terminator.
        private Result<Url, ParseError>? StepAuthority(char c)
        {
            if (c == '@')
            {
                if (_atSignSeen)
                {
                    // Multiple @ is a parse error but we keep going by
                    // percent-encoding the @ into username.
                }
                _atSignSeen = true;
                var userPass = _buffer.ToString();
                _buffer.Clear();
                // Split on first ':' for user/pass.
                var colon = userPass.IndexOf(':');
                if (colon < 0)
                {
                    _username = userPass.Length > 0 ? userPass : null;
                }
                else
                {
                    _username = userPass[..colon];
                    _password = userPass[(colon + 1)..];
                }
                return null;
            }
            if (IsEof(c) || c == '/' || c == '?' || c == '#' || (_isSpecial && c == '\\'))
            {
                if (_atSignSeen && _buffer.Length == 0)
                {
                    return Result<Url, ParseError>.Err(ParseError.MalformedAuthority);
                }

                _i -= _buffer.Length + 1;
                _buffer.Clear();
                _state = State.Host;
                return null;
            }
            _buffer.Append(c);
            return null;
        }

        // §4.4.1 host state
        private Result<Url, ParseError>? StepHost(char c)
        {
            if (c == ':' && !InsideBrackets())
            {
                if (_buffer.Length == 0)
                {
                    return Result<Url, ParseError>.Err(ParseError.MalformedAuthority);
                }

                var hostResult = HostParser.Parse(_buffer.ToString(), _isSpecial);
                if (!hostResult.IsOk)
                {
                    return MapHostError(hostResult.Err!.Value);
                }

                _host = hostResult.Host;
                _buffer.Clear();
                _state = State.Port;
                return null;
            }
            if (IsEof(c) || c == '/' || c == '?' || c == '#' || (_isSpecial && c == '\\'))
            {
                if (_isSpecial && _buffer.Length == 0)
                {
                    return Result<Url, ParseError>.Err(ParseError.MalformedAuthority);
                }

                var hostResult = HostParser.Parse(_buffer.ToString(), _isSpecial);
                if (!hostResult.IsOk)
                {
                    return MapHostError(hostResult.Err!.Value);
                }

                _host = hostResult.Host;
                _buffer.Clear();
                _state = State.PathStart;
                _i--;
                return null;
            }
            _buffer.Append(c);
            return null;
        }

        private bool InsideBrackets()
        {
            // IPv6 bracket matching is not implemented yet.
            return _buffer.Length > 0 && _buffer[0] == '[' && !_buffer.ToString().Contains(']');
        }

        // §4.4.1 port state
        private Result<Url, ParseError>? StepPort(char c)
        {
            if (IsAsciiDigit(c))
            {
                _buffer.Append(c);
                return null;
            }
            if (IsEof(c) || c == '/' || c == '?' || c == '#' || (_isSpecial && c == '\\'))
            {
                if (_buffer.Length > 0)
                {
                    if (!int.TryParse(_buffer.ToString(), out var port) || port < 0 || port > 0xFFFF)
                    {
                        return Result<Url, ParseError>.Err(ParseError.InvalidPort);
                    }

                    var defaultPort = SpecialSchemes.DefaultPort(_scheme.ToString());
                    _port = port == defaultPort ? null : port;
                    _buffer.Clear();
                }
                _state = State.PathStart;
                _i--;
                return null;
            }
            return Result<Url, ParseError>.Err(ParseError.InvalidPort);
        }

        // §4.4.1 file state
        private Result<Url, ParseError>? StepFile(char c)
        {
            _isSpecial = true;
            if (c == '/' || c == '\\')
            {
                _state = State.FileSlash;
                return null;
            }
            if (_base?.Scheme == "file")
            {
                _host = _base.Host;
                _pathSegments = SplitPath(_base.Path);
                _query = _base.Query;
                if (c == '?')
                {
                    _query = "";
                    _state = State.Query;
                    return null;
                }
                if (c == '#')
                {
                    _fragment = "";
                    _state = State.Fragment;
                    return null;
                }
                if (IsEof(c))
                {
                    return null;
                }

                _query = null;
                _state = State.Path;
                _i--;
                return null;
            }
            _state = State.Path;
            _i--;
            return null;
        }

        private Result<Url, ParseError>? StepFileSlash(char c)
        {
            if (c == '/' || c == '\\')
            {
                _state = State.FileHost;
                return null;
            }
            if (_base?.Scheme == "file")
            {
                _host = _base.Host;
            }
            _state = State.Path;
            _i--;
            return null;
        }

        private Result<Url, ParseError>? StepFileHost(char c)
        {
            if (IsEof(c) || c == '/' || c == '\\' || c == '?' || c == '#')
            {
                if (_buffer.Length == 0)
                {
                    // empty file host → null
                    _host = null;
                }
                else if (_buffer.ToString().Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    _host = null;
                }
                else
                {
                    var hostResult = HostParser.Parse(_buffer.ToString(), isSpecial: true);
                    if (!hostResult.IsOk)
                    {
                        return MapHostError(hostResult.Err!.Value);
                    }

                    _host = hostResult.Host;
                }
                _buffer.Clear();
                _state = State.PathStart;
                _i--;
                return null;
            }
            _buffer.Append(c);
            return null;
        }

        private Result<Url, ParseError>? StepPathStart(char c)
        {
            if (_isSpecial)
            {
                if (c == '/' || c == '\\')
                {
                    _state = State.Path;
                    return null;
                }
                _state = State.Path;
                _i--;
                return null;
            }
            if (c == '?')
            {
                _query = "";
                _state = State.Query;
                return null;
            }
            if (c == '#')
            {
                _fragment = "";
                _state = State.Fragment;
                return null;
            }
            if (IsEof(c))
            {
                return null;
            }

            _state = State.Path;
            if (c != '/')
            {
                _i--;
            }

            return null;
        }

        // §4.4.1 path state
        private Result<Url, ParseError>? StepPath(char c)
        {
            if (IsEof(c) || c == '/' || (_isSpecial && c == '\\')
                || c == '?' || c == '#')
            {
                // Commit current segment.
                var seg = _buffer.ToString();
                _buffer.Clear();

                if (IsDoubleDotSegment(seg))
                {
                    if (_pathSegments.Count > 0)
                    {
                        _pathSegments.RemoveAt(_pathSegments.Count - 1);
                    }

                    if (c != '/' && !(_isSpecial && c == '\\'))
                    {
                        _pathSegments.Add("");
                    }
                }
                else if (IsSingleDotSegment(seg))
                {
                    if (c != '/' && !(_isSpecial && c == '\\'))
                    {
                        _pathSegments.Add("");
                    }
                }
                else
                {
                    _pathSegments.Add(seg);
                }

                if (c == '?')
                {
                    _query = "";
                    _state = State.Query;
                    return null;
                }
                if (c == '#')
                {
                    _fragment = "";
                    _state = State.Fragment;
                    return null;
                }
                return null;
            }
            // Append percent-encoded form to buffer.
            Percent.AppendEncoded(_buffer, c, Percent.Set.Path);
            return null;
        }

        private Result<Url, ParseError>? StepOpaquePath(char c)
        {
            if (c == '?')
            {
                _query = "";
                _state = State.Query;
                return null;
            }
            if (c == '#')
            {
                _fragment = "";
                _state = State.Fragment;
                return null;
            }
            if (IsEof(c))
            {
                return null;
            }

            var sb = new StringBuilder(_opaquePath ?? "");
            Percent.AppendEncoded(sb, c, Percent.Set.C0Control);
            _opaquePath = sb.ToString();
            return null;
        }

        private Result<Url, ParseError>? StepQuery(char c)
        {
            if (c == '#')
            {
                _query = _buffer.ToString();
                _buffer.Clear();
                _fragment = "";
                _state = State.Fragment;
                return null;
            }
            if (IsEof(c))
            {
                _query = _buffer.ToString();
                _buffer.Clear();
                return null;
            }
            var set = _isSpecial ? Percent.Set.SpecialQuery : Percent.Set.Query;
            Percent.AppendEncoded(_buffer, c, set);
            return null;
        }

        private Result<Url, ParseError>? StepFragment(char c)
        {
            if (IsEof(c))
            {
                _fragment = _buffer.ToString();
                _buffer.Clear();
                return null;
            }
            Percent.AppendEncoded(_buffer, c, Percent.Set.Fragment);
            return null;
        }

        private Result<Url, ParseError> Finish()
        {
            // EOF state-handlers (StepQuery/StepFragment/StepPath) already
            // drained their buffers when the sentinel was dispatched, so we
            // don't re-touch _query or _fragment here. Only Path / PathStart
            // could leave a non-empty buffer (an unterminated last segment).
            if ((_state == State.Path || _state == State.PathStart)
                && _buffer.Length > 0)
            {
                var seg = _buffer.ToString();
                _buffer.Clear();
                if (IsDoubleDotSegment(seg))
                {
                    if (_pathSegments.Count > 0)
                    {
                        _pathSegments.RemoveAt(_pathSegments.Count - 1);
                    }
                }
                else if (!IsSingleDotSegment(seg))
                {
                    _pathSegments.Add(seg);
                }
            }

            var pathStr = _opaquePath
                ?? (_pathSegments.Count == 0
                    ? (_isSpecial ? "/" : "")
                    : "/" + string.Join('/', _pathSegments));

            return Result<Url, ParseError>.Ok(new Url(
                Scheme: _scheme.ToString(),
                Host: _host,
                Port: _port,
                Path: pathStr,
                Query: _query,
                Fragment: _fragment)
            {
                Username = _username,
                Password = _password,
            });
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static bool IsAsciiAlpha(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsAsciiAlphanumeric(char c)
            => IsAsciiAlpha(c) || IsAsciiDigit(c);

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsSingleDotSegment(string s)
            => s == "." || s.Equals("%2e", StringComparison.OrdinalIgnoreCase);

        private static bool IsDoubleDotSegment(string s)
            => s == ".."
               || s.Equals(".%2e", StringComparison.OrdinalIgnoreCase)
               || s.Equals("%2e.", StringComparison.OrdinalIgnoreCase)
               || s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);

        private static List<string> SplitPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return [];
            }

            var parts = path.Split('/');
            // Leading '/' produces an empty first segment we drop.
            var list = new List<string>(parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                if (i == 0 && parts[i].Length == 0)
                {
                    continue;
                }

                list.Add(parts[i]);
            }
            return list;
        }

        private static Result<Url, ParseError> MapHostError(HostParser.Error e) => e switch
        {
            HostParser.Error.Empty => Result<Url, ParseError>.Err(ParseError.MalformedAuthority),
            HostParser.Error.IPv6NotSupported => Result<Url, ParseError>.Err(ParseError.Unsupported),
            HostParser.Error.InvalidCharacter => Result<Url, ParseError>.Err(ParseError.InvalidCharacter),
            _ => Result<Url, ParseError>.Err(ParseError.InvalidHost),
        };
    }
}
