using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Networking;

/// <summary>
/// One parsed request from the loopback administration page.
///
/// Deliberately small: the page answers "is the room healthy" and presses three buttons, so this needs a
/// request line, a handful of headers and a short JSON body - not a general-purpose HTTP server. Everything
/// accepted is bounded, and everything refused carries a code the caller can log.
/// </summary>
public sealed class AdminHttpRequest
{
    private readonly Dictionary<string, string> headers;

    internal AdminHttpRequest(string method, string path, string query, Dictionary<string, string> headers, byte[] body,
        int contentLength = 0)
    {
        Method = method;
        Path = path;
        Query = query;
        this.headers = headers;
        Body = body;
        ContentLength = contentLength;
    }

    public string Method { get; }
    /// <summary>Path only: the query string is kept separately so a token can be refused rather than parsed.</summary>
    public string Path { get; }
    public string Query { get; }
    /// <summary>The body. For the save upload this stays empty: those bytes are streamed to disk, not buffered.</summary>
    public byte[] Body { get; private set; }
    /// <summary>Declared <c>Content-Length</c>, which for an upload is far larger than <see cref="Body"/>.</summary>
    public int ContentLength { get; }
    public IReadOnlyDictionary<string, string> Headers => headers;

    /// <summary>
    /// Attaches the body after <see cref="AdminHttp.TryParseHead"/> left it for the caller. Separate from a
    /// setter so that "the head has been parsed" and "the body has arrived" stay visibly two steps - the upload
    /// route never calls this at all.
    /// </summary>
    public void AttachBody(byte[] body) => Body = body;

    public string? Header(string name) => headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>True when the query string carries the given key. Used to reject a password placed in the URL.</summary>
    public bool QueryHas(string name)
    {
        if (Query.Length == 0) return false;
        foreach (var pair in Query.Split('&'))
        {
            var key = pair.Split('=')[0];
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public enum AdminHttpParse
{
    /// <summary>A whole request is present in the buffer.</summary>
    Complete,
    /// <summary>More bytes are needed; the caller should read again.</summary>
    Incomplete,
    /// <summary>The bytes cannot become a request this server will answer.</summary>
    Malformed,
    /// <summary>The request exceeds the accepted size; refuse it without reading the rest.</summary>
    TooLarge
}

/// <summary>
/// Minimal HTTP/1.1 parsing + the two checks that make a loopback panel safe: the Host header must name this
/// machine's loopback interface (so a DNS-rebinding page cannot drive it), and secrets are compared in
/// constant time. Kept free of Verse so it can be unit-tested without the game.
/// </summary>
public static class AdminHttp
{
    public const int MaximumHeaderBytes = 8 * 1024;
    /// <summary>16 KiB: a login or download request carries a passphrase, and truncating one would look like a wrong password.</summary>
    public const int MaximumBodyBytes = 16 * 1024;
    public const int MaximumHeaderCount = 32;

    private static readonly string[] Methods = ["GET", "POST"];

    public static AdminHttpParse TryParse(ReadOnlySpan<byte> buffer, out AdminHttpRequest request, out string error, out int consumed)
    {
        consumed = 0;
        var parse = TryParseHead(buffer, MaximumBodyBytes, out request, out error, out var headLength);
        if (parse != AdminHttpParse.Complete) return parse;
        var bodyStart = headLength;
        var bodyLength = request.ContentLength;
        if (buffer.Length - bodyStart < bodyLength) { request = null!; return AdminHttpParse.Incomplete; }
        request.AttachBody(buffer.Slice(bodyStart, bodyLength).ToArray());
        consumed = bodyStart + bodyLength;
        return AdminHttpParse.Complete;
    }

    /// <summary>
    /// Parses the request line and headers only, leaving the body to the caller.
    ///
    /// This exists for the save upload: 256 MB cannot be buffered before it is validated, so the handler reads
    /// the head here, decides (method, target, credentials, size), and streams the body straight to a temp file.
    /// <paramref name="maximumBodyBytes"/> is checked against the declared length, so an oversized request is
    /// refused before a single body byte is read.
    /// </summary>
    public static AdminHttpParse TryParseHead(ReadOnlySpan<byte> buffer, int maximumBodyBytes,
        out AdminHttpRequest request, out string error, out int headLength)
    {
        request = null!;
        error = "";
        headLength = 0;
        var end = FindHeaderEnd(buffer);
        if (end < 0)
        {
            if (buffer.Length > MaximumHeaderBytes) { error = "request headers exceed " + MaximumHeaderBytes + " bytes"; return AdminHttpParse.TooLarge; }
            return AdminHttpParse.Incomplete;
        }
        if (end + 4 > MaximumHeaderBytes) { error = "request headers exceed " + MaximumHeaderBytes + " bytes"; return AdminHttpParse.TooLarge; }

        var head = Encoding.Latin1.GetString(buffer[..end]);
        var lines = head.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0];
        var parts = requestLine.Split(' ');
        if (parts.Length != 3) { error = "request line must be: METHOD TARGET VERSION"; return AdminHttpParse.Malformed; }
        var method = parts[0];
        if (method.Length is 0 or > 16 || !method.All(c => c is >= 'A' and <= 'Z')) { error = "invalid method token"; return AdminHttpParse.Malformed; }
        var target = parts[1];
        if (target.Length == 0 || target.Length > 2048 || target[0] != '/') { error = "target must be an absolute path"; return AdminHttpParse.Malformed; }
        if (target.Any(char.IsControl)) { error = "target contains a control character"; return AdminHttpParse.Malformed; }
        var version = parts[2];
        if (version is not ("HTTP/1.1" or "HTTP/1.0")) { error = "only HTTP/1.1 and HTTP/1.0 are accepted"; return AdminHttpParse.Malformed; }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) { error = "header without a name"; return AdminHttpParse.Malformed; }
            var name = line[..colon];
            if (name.Length > 64 || name.Any(c => c is <= ' ' or ':')) { error = "invalid header name"; return AdminHttpParse.Malformed; }
            var value = line[(colon + 1)..].Trim();
            if (value.Length > 1024) { error = "header value too long"; return AdminHttpParse.TooLarge; }
            if (headers.Count >= MaximumHeaderCount && !headers.ContainsKey(name)) { error = "too many headers"; return AdminHttpParse.TooLarge; }
            headers[name] = value;
        }

        // A chunked body has no length up front. Refusing it keeps the read bounded and the parser total;
        // this client is a page we ship, and it always sends Content-Length.
        if (headers.TryGetValue("Transfer-Encoding", out var transfer) && !transfer.Equals("identity", StringComparison.OrdinalIgnoreCase))
        { error = "chunked bodies are not accepted"; return AdminHttpParse.Malformed; }

        var bodyLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!int.TryParse(lengthText, out bodyLength) || bodyLength < 0) { error = "invalid Content-Length"; return AdminHttpParse.Malformed; }
            if (bodyLength > maximumBodyBytes) { error = "request body exceeds " + maximumBodyBytes + " bytes"; return AdminHttpParse.TooLarge; }
        }

        var questionMark = target.IndexOf('?');
        var path = questionMark < 0 ? target : target[..questionMark];
        var query = questionMark < 0 ? "" : target[(questionMark + 1)..];
        headLength = end + 4;
        request = new AdminHttpRequest(method, path, query, headers, [], bodyLength);
        return AdminHttpParse.Complete;
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (var index = 0; index + 3 < buffer.Length; index++)
            if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                return index;
        return -1;
    }

    public static bool MethodAllowed(string method) => Methods.Contains(method, StringComparer.Ordinal);

    /// <summary>
    /// Whether the Host header names this loopback listener. Without this a page on any origin can point a
    /// hostname at 127.0.0.1 (DNS rebinding) and then read the status endpoint through the browser, because
    /// the request would look same-origin to the browser.
    /// </summary>
    public static bool IsLoopbackHost(string? hostHeader, int port)
    {
        if (!TrySplitHost(hostHeader, port, out var host)) return false;
        if (!host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("::1", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Whether the Host header names the interface this page was told to bind.
    ///
    /// A loopback bind only accepts loopback names - that is the DNS-rebinding defence and it must not be
    /// weakened. A network bind has to accept what operators actually type (this machine's LAN address, a DNS
    /// name that resolves to it), so the defence there is the login the bind policy demands instead. A bind to
    /// one specific address accepts that address, loopback names, and nothing else.
    /// </summary>
    public static bool HostAccepted(string? hostHeader, int port, string? bindAddress)
    {
        // A bind to "everything" must be decided before the port is checked. Behind a port mapping the
        // container listens on one port while the browser types another, so comparing the Host port against
        // the listening port rejects every legitimate request: measured, a mapped 30583 -> 30573 answered
        // 403 host-not-allowed to a plain browser request and only worked with a forged "Host: 0.0.0.0".
        // The DNS-rebinding defence is pointless on a wildcard bind anyway - anyone can put any Host in a
        // request - so the real defence there is the login the bind policy demands.
        var isAny = bindAddress is "0.0.0.0" or "::" or "[::]" or "*";
        if (isAny) return true;
        if (!TrySplitHost(hostHeader, port, out var host)) return false;
        if (string.IsNullOrEmpty(bindAddress) || bindAddress is "127.0.0.1" or "localhost") return IsLoopbackHost(hostHeader, port);
        if (host.Equals(bindAddress.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) return true;
        return IsLoopbackHost(hostHeader, port);
    }

    /// <summary>Splits <c>host[:port]</c>, including the bracketed IPv6 form, and checks the port when one is given.</summary>
    private static bool TrySplitHost(string? hostHeader, int port, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        var text = hostHeader.Trim();
        string portText;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            portText = rest.StartsWith(':') ? rest[1..] : "";
            if (rest.Length > 0 && !rest.StartsWith(':')) return false;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            // A bare IPv6 literal has several colons and no port; only treat the last group as a port when the
            // remainder is numeric.
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                host = text[..colon];
                portText = text[(colon + 1)..];
            }
            else
            {
                host = text;
                portText = "";
            }
        }
        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, out var value)) return false;
            if (port != 0 && value != port) return false;
        }
        return host.Length > 0;
    }

    /// <summary>Constant-time secret comparison: hashing first also hides the length of the configured password.</summary>
    public static bool SecretEquals(string expected, string provided)
    {
        if (expected.Length == 0 || provided.Length == 0) return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>Cookie value by name, from one or more Cookie headers.</summary>
    public static string? CookieValue(string? cookieHeader, string name)
    {
        if (string.IsNullOrEmpty(cookieHeader)) return null;
        foreach (var part in cookieHeader.Split(';'))
        {
            var pair = part.Trim();
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            if (!pair[..equals].Trim().Equals(name, StringComparison.Ordinal)) continue;
            return pair[(equals + 1)..].Trim();
        }
        return null;
    }

    public static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        303 => "See Other",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status"
    };

    /// <summary>Builds the whole response, so every reply carries the same defensive headers.</summary>
    public static byte[] Response(int status, string contentType, ReadOnlySpan<byte> body, params (string Name, string Value)[] extra)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");
        head.Append("X-Frame-Options: DENY\r\n");
        head.Append("Connection: close\r\n");
        foreach (var (name, value) in extra) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        var header = Encoding.Latin1.GetBytes(head.ToString());
        var response = new byte[header.Length + body.Length];
        header.CopyTo(response, 0);
        body.CopyTo(response.AsSpan(header.Length));
        return response;
    }
}
