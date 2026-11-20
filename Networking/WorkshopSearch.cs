using System.Globalization;
using System.Net;
using System.Text;

namespace TurboTick.Networking;

/// <summary>One workshop item as it appears in a browse page.</summary>
/// <param name="Id">The published file id (the folder name steamcmd writes).</param>
/// <param name="Title">The item's title, HTML-decoded and whitespace-collapsed.</param>
/// <param name="Score">Relevance in 0..1. With no query it is a pure page-order decay, see <see cref="WorkshopSearch.Parse"/>.</param>
public sealed record WorkshopCandidate(long Id, string Title, double Score);

/// <summary>The result of one reachability test: whether steamcommunity answered, which route was used, and what to
/// tell the operator. `Route` is the whole point - a failure over a proxy and a failure while direct have completely
/// different fixes.</summary>
public sealed record WorkshopProbe(bool Ok, int Status, string Route, string Detail);

/// <summary>A lookup, whether or not it found anything. Never an exception: the caller is an HTTP handler.</summary>
public sealed record WorkshopLookup(
    string Query,
    IReadOnlyList<WorkshopCandidate> Candidates,
    IReadOnlyList<string> Notes,
    string? Problem)
{
    public bool Found => Candidates.Count > 0;

    public static WorkshopLookup Empty(string query, string problem) => new(query, [], [], problem);
}

/// <summary>The result of asking steamcmd for one item. Modelled here so Networking never references the Source assembly.</summary>
public sealed record WorkshopDownload(bool Ok, long Id, string? Path, string Detail);

/// <summary>One candidate that was actually downloaded and then checked against the requested packageId.</summary>
public sealed record WorkshopAttempt(long Id, string Title, double Score, string? PackageId, string Detail);

/// <summary>
/// The end of the chain: a workshop item whose <c>About/About.xml</c> declares the packageId the save asked for.
/// </summary>
public sealed record WorkshopResolution(
    bool Ok,
    long WorkshopId,
    string PackageId,
    string? Path,
    IReadOnlyList<WorkshopAttempt> Attempts,
    string? Problem)
{
    public static WorkshopResolution Failed(string packageId, IReadOnlyList<WorkshopAttempt> attempts, string problem) =>
        new(false, 0, packageId, null, attempts, problem);
}

/// <summary>
/// Finding a mod on the Steam workshop by title, when the save does not say where it came from.
///
/// <para><b>Why this exists.</b> A 1.7 save records <c>modSteamIds</c>, but measured on a real 77 MB colony every
/// third-party entry in that list is <c>0</c> - only the five official expansions carry a real app id. So a save
/// can name <c>local.moredoorvariants</c> and mean "the mod called [DG]More Door Variants", and the only way to get
/// it is to search the workshop by that name, download candidates, and read each one's own
/// <c>About/About.xml</c> until the packageId matches. That is exactly the loop <see cref="ResolveAsync"/>
/// implements, and the reason it verifies instead of trusting the first hit: the top result for a title is
/// regularly a translation, a fork, or an unrelated mod with a similar name.</para>
///
/// <para><b>No Verse, no new dependencies.</b> This file is file/HTTP only so the HTTP thread can run it and so it
/// is testable without the game. The actual download is injected as a delegate, because steamcmd lives in the
/// Source assembly (<c>SteamCmdRunner.DownloadWorkshopItemAsync</c>) and Networking must not depend on it.</para>
///
/// <para><b>The page.</b> The browse endpoint returns server-rendered HTML. Measured markup (2026-11, 1.7 app
/// 294100): every item appears twice - once as an image link whose body is an <c>&lt;img&gt;</c>, once as a title
/// link whose body is the bare text. Both carry the same <c>?id=</c>. The class names are content-hashed
/// (<c>SxMdVrGe9aY-</c>) and change without notice, so the parser keys on structure - "an anchor to filedetails
/// whose text body is non-empty after stripping tags" - not on those names.</para>
/// </summary>
public static class WorkshopSearch
{
    public const string BrowseEndpoint = "https://steamcommunity.com/workshop/browse/";

    /// <summary>RimWorld's app id. The workshop is per-app, so this is not configurable.</summary>
    public const string AppId = "294100";

    /// <summary>Tag that keeps 1.7 items ahead of the 1.4/1.5/1.6 back catalogue.</summary>
    public const string VersionTag = "1.7";

    /// <summary>Short enough to keep a page request from pinning an HTTP worker, long enough for a 500 KB page.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many candidates a resolve loop is willing to download before giving up.
    ///
    /// <para>Six, not three or four: measured on the deployment host, the real item is not always near the top. The
    /// worst case in the save that motivated this code is <c>aoba.deadmanswitch.core</c>, whose workshop page is
    /// titled <i>The Dead Man's Switch II</i> and therefore scores exactly like the four sibling add-ons
    /// (<i>- AncientCorps</i>, <i>- MobileDragoon</i>, <i>- Synthetic</i>) and one re-upload that sit above it. With
    /// a budget of four the loop gave up after four unrelated mods and the save was refused; with six it finds the
    /// item on the fifth try. The loop stops at the first item whose own <c>About.xml</c> agrees, so a normal mod
    /// still costs exactly one download.</para>
    /// </summary>
    public const int MaximumAttempts = 6;

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36 TurboTick/0.7";

    /// <summary>The proxy variable that wins over the generic ones; steamcommunity is unreachable from the lab host.</summary>
    public const string ProxyVariable = "TURBOTICK_STEAM_PROXY";

    /// <summary>
    /// The proxy in force for <b>our own</b> HTTP traffic, not for steamcmd.
    ///
    /// <para>Both paths have to agree: the portal learns a workshop item's id by fetching a browse page over
    /// <c>HttpClient</c>, then hands that id to steamcmd. If only one of the two is proxied, the machine either
    /// cannot search or cannot download, and the two symptoms read identically on the page ("工坊 flaky"). The
    /// variable is the same one, resolved the same way, so a deployment that sets only
    /// <c>TURBOTICK_STEAM_PROXY</c> is proxied end to end.</para>
    ///
    /// <para>An explicit override wins over the environment, because the admin page writes its choice into the
    /// process environment anyway - but a test seam needs to hand one in without touching global state.</para>
    /// </summary>
    public static void SetProxy(string? proxy)
    {
        proxyOverride = proxy;
    }

    private static string? proxyOverride;

    public static Uri? ResolveProxy(Func<string, string?>? read = null)
    {
        // The page's decision outranks the environment, and does so unconditionally: production calls this with no
        // reader, but a test that supplies one must still see the override win, otherwise "the operator set a proxy"
        // and "the search used it" cannot be asserted in the same test.
        if (proxyOverride is not null) return IsDirect(proxyOverride) ? null : ToUri(proxyOverride);
        read ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { ProxyVariable, "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
        {
            var value = (read(name) ?? "").Trim();
            if (value.Length == 0) continue;
            if (IsDirect(value)) return null;
            if (ToUri(value) is { } uri) return uri;
        }
        return null;
    }

    private static bool IsDirect(string value) =>
        value.Equals("none", StringComparison.OrdinalIgnoreCase)
        || value.Equals("off", StringComparison.OrdinalIgnoreCase)
        || value.Equals("direct", StringComparison.OrdinalIgnoreCase)
        || value.Equals("none://", StringComparison.OrdinalIgnoreCase);

    private static Uri? ToUri(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri : null;
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// Extracts (id, title) pairs from a browse page.
    ///
    /// <para>With a <paramref name="query"/> the score is relevance to that query, so the caller can try the best
    /// candidate first. Without one the score is a page-order decay (<c>1 - rank/1000</c>) because a score is only
    /// meaningful relative to something: "no query" means "keep Steam's own order", and pretending otherwise would
    /// invent a ranking the page does not have.</para>
    /// </summary>
    public static IReadOnlyList<WorkshopCandidate> Parse(string? html, string? query = null)
    {
        var found = new List<(long Id, string Title, int Rank)>();
        if (string.IsNullOrEmpty(html)) return [];

        var seen = new HashSet<long>();
        var index = 0;
        while (index < html.Length)
        {
            var open = html.IndexOf("<a", index, StringComparison.OrdinalIgnoreCase);
            if (open < 0) break;
            var after = open + 2;
            if (after >= html.Length) break;
            // `<article>`, `<abbr>`, ... are not anchors; only `<a` followed by whitespace or `>` is.
            if (html[after] is not (' ' or '\t' or '\r' or '\n' or '>')) { index = after; continue; }

            var tagEnd = FindTagEnd(html, after);
            if (tagEnd < 0) break;
            var close = html.IndexOf("</a", tagEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (close < 0) break;
            var tag = html.AsSpan(open, tagEnd - open + 1);

            var id = ReadWorkshopId(tag);
            var title = id > 0 ? ReadTitle(html.AsSpan(tagEnd + 1, close - tagEnd - 1)) : null;
            if (id > 0 && title is not null && seen.Add(id))
                found.Add((id, title, found.Count));
            index = close + 3;
        }

        if (found.Count == 0) return [];

        if (string.IsNullOrWhiteSpace(query))
            return found
                .Select((item, rank) => new WorkshopCandidate(item.Id, item.Title, RankScore(rank)))
                .ToArray();

        var wanted = Tokenize(Normalize(query));
        var exactQuery = Normalize(query);
        var scored = found
            .Select(item =>
            {
                var normalized = Normalize(item.Title);
                var tokens = Tokenize(normalized);
                var score = Score(wanted, tokens, normalized == exactQuery && exactQuery.Length > 0);
                return new Ranked(item.Id, item.Title, score, Math.Abs(tokens.Count - wanted.Count), item.Rank);
            })
            .ToList();
        scored.Sort(Compare);
        return scored.Select(item => new WorkshopCandidate(item.Id, item.Title, item.Score)).ToArray();
    }

    /// <summary>Page-order decay: strictly decreasing, so the caller's "first hit wins" stays Steam's order.</summary>
    private static double RankScore(int rank) => Math.Round(Math.Max(0.0, 1.0 - rank / 1000.0), 4);

    private sealed record Ranked(long Id, string Title, double Score, int ExtraTokens, int Rank);

    /// <summary>
    /// Best score first. Ties go to the title that adds fewer words ("Trenches" beats "Trenches ภาษาไทย"), then to
    /// page order - deterministic, because a resolve loop downloads in this order and an unstable sort would make
    /// "which candidate did it try" irreproducible in a bug report.
    /// </summary>
    private static int Compare(Ranked left, Ranked right)
    {
        var byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0) return byScore;
        var byExtra = left.ExtraTokens.CompareTo(right.ExtraTokens);
        return byExtra != 0 ? byExtra : left.Rank.CompareTo(right.Rank);
    }

    /// <summary>
    /// 0.75 * how much of the query the title covers + 0.25 for being the query verbatim.
    ///
    /// <para>Token matches are weighted 1.0 for a whole word and 0.6 for a word that merely contains the query word,
    /// which is what lets <c>variants</c> find <c>FemaleBodyVariants</c> without letting a substring hit outrank a
    /// real title match. The exact-title bonus is what puts <c>[DG]More Door Variants</c> ahead of its own
    /// translation and licence-note variants.</para>
    /// </summary>
    private static double Score(IReadOnlyList<string> wanted, IReadOnlyList<string> title, bool exact)
    {
        if (wanted.Count == 0) return 0;
        var sum = 0.0;
        foreach (var token in wanted)
        {
            if (title.Contains(token, StringComparer.Ordinal)) { sum += 1.0; continue; }
            if (title.Any(candidate => candidate.Contains(token, StringComparison.Ordinal))) sum += 0.6;
        }
        var coverage = sum / wanted.Count;
        return Math.Round(0.75 * coverage + (exact ? 0.25 : 0.0), 4);
    }

    private static int FindTagEnd(string html, int from)
    {
        var quote = '\0';
        for (var position = from; position < html.Length; position++)
        {
            var c = html[position];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '>') return position;
        }
        return -1;
    }

    /// <summary>The numeric id of a <c>filedetails/?id=</c> anchor, or 0 when the anchor points somewhere else.</summary>
    private static long ReadWorkshopId(ReadOnlySpan<char> tag)
    {
        var marker = tag.IndexOf("filedetails/?id=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return 0;
        var rest = tag[(marker + "filedetails/?id=".Length)..];
        long value = 0;
        var digits = 0;
        foreach (var c in rest)
        {
            if (!char.IsAsciiDigit(c)) break;
            // A 20-digit id is not a workshop id; refuse rather than overflow into a negative.
            if (++digits > 18) return 0;
            value = value * 10 + (c - '0');
        }
        return digits == 0 ? 0 : value;
    }

    /// <summary>
    /// The anchor's own text, or null when it has none.
    ///
    /// <para>This is the discriminator the page gives us for free: the image anchor's body is an <c>&lt;img&gt;</c>
    /// and strips to nothing, the title anchor's body is text. Skipping empty text also makes a truncated
    /// "link but no title" fragment harmless.</para>
    /// </summary>
    private static string? ReadTitle(ReadOnlySpan<char> body)
    {
        var text = new StringBuilder(body.Length);
        var inTag = false;
        foreach (var c in body)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) text.Append(c);
        }
        var decoded = WebUtility.HtmlDecode(text.ToString());
        var collapsed = Collapse(decoded);
        // A title is a line of text; anything long is a paragraph or a JSON blob that happened to be inside an anchor.
        return collapsed.Length is 0 or > 300 ? null : collapsed;
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) { space = builder.Length > 0; continue; }
            if (space) { builder.Append(' '); space = false; }
            builder.Append(c);
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------ normalising

    /// <summary>
    /// Lower-cased, tag-free, punctuation-free form used for both sides of a comparison.
    ///
    /// <para>Rules, in order: drop leading bracketed groups (<c>[DG]</c>, <c>【DG】</c>) because they are author
    /// tags rather than names; replace every character that is not a letter, digit or CJK ideograph with a space;
    /// collapse whitespace. Keeping this as the single normaliser is what makes "score" and "did it match"
    /// answerable with one function instead of two that can disagree.</para>
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        // Strip a run of bracketed prefixes: "[DG]More Door Variants", "【DG】【1.7】X".
        while (text.Length > 0)
        {
            var open = text[0];
            var close = open switch { '[' => ']', '【' => '】', '(' => ')', '（' => '）', _ => '\0' };
            if (close == '\0') break;
            var end = text.IndexOf(close);
            if (end < 0 || end > 32) break;
            text = text[(end + 1)..].TrimStart();
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0) builder.Append(' ');
                space = false;
                builder.Append(c);
            }
            else space = true;
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> Tokenize(string normalized) =>
        normalized.Length == 0 ? [] : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // --------------------------------------------------------------- querying

    /// <summary>
    /// What to type into the workshop search box for a save entry.
    ///
    /// <para>The display name is the better query when there is one: it is what the author called the mod, and it
    /// survives the packageId being a namespaced slug (<c>local.moredoorvariants</c>). Without it, the last segment
    /// of the packageId with underscores opened up is the only thing left - a poor query, but a query.</para>
    /// </summary>
    public static string QueryFor(string? packageId, string? displayName)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length > 0 && !name.Equals(packageId ?? "", StringComparison.OrdinalIgnoreCase)) return name;
        var id = (packageId ?? "").Trim();
        if (id.Length == 0) return "";
        var lastDot = id.LastIndexOf('.');
        return (lastDot >= 0 ? id[(lastDot + 1)..] : id).Replace('_', ' ').Trim();
    }

    /// <summary>The browse URL for a text query. <paramref name="versionTagged"/> is the 1.7-only pass.</summary>
    public static string BrowseUrl(string query, bool versionTagged)
    {
        var builder = new StringBuilder(BrowseEndpoint);
        builder.Append("?appid=").Append(AppId);
        builder.Append("&searchtext=").Append(Uri.EscapeDataString(query));
        builder.Append("&browsesort=textsearch&section=readytouseitems");
        if (versionTagged) builder.Append("&requiredtags%5B%5D=").Append(Uri.EscapeDataString(VersionTag));
        return builder.ToString();
    }

    /// <summary>
    /// The proxy for steamcommunity, or null for a direct connection.
    ///
    /// <para>Order matters and is deliberate: the dedicated variable wins so an operator can point only this one
    /// feature at a tunnel, then the generic HTTPS/HTTP variables are honoured because on the lab host that is the
    /// only route out. A value of <c>none</c>/<c>off</c>/<c>direct</c> explicitly disables an inherited proxy -
    /// otherwise "I removed the proxy" would be inexpressible while the container still inherits it.</para>
    ///
    /// <para>Resolution and the direct/off vocabulary live in <see cref="ResolveProxy"/> above, which this delegate
    /// form feeds; keeping one implementation is what makes the admin page's value and the environment value
    /// interpret "none" the same way.</para>
    /// </summary>
    public static Uri? ResolveProxyFrom(Func<string, string?> read) => ResolveProxy(read);

    /// <summary>
    /// Searches twice - once restricted to the 1.7 tag, once unrestricted - and merges by id.
    ///
    /// <para>The tagged pass is the good one (it drops the 1.4/1.5 back catalogue that shares titles) but it also
    /// returns nothing for a mod whose author never set the tag, hence the second pass. Failures are reported, not
    /// thrown: this runs on an HTTP worker, and "the proxy is down" must read as a message on the page, not as a
    /// 500.</para>
    /// </summary>
    public static async Task<WorkshopLookup> LookupAsync(string? packageId, string? displayName,
        CancellationToken cancellationToken) =>
        await LookupAsync(packageId, displayName, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Same, with a test seam: <paramref name="send"/> replaces the HTTP round trip.</summary>
    public static async Task<WorkshopLookup> LookupAsync(string? packageId, string? displayName,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send,
        CancellationToken cancellationToken)
    {
        var query = QueryFor(packageId, displayName);
        if (query.Length == 0) return WorkshopLookup.Empty(query, "没有可用于搜索的名称");

        var notes = new List<string>();
        string? problem = null;
        var merged = new List<(WorkshopCandidate Candidate, int Pass, int Rank)>();
        var seen = new Dictionary<long, int>();

        using var owned = send is null ? CreateClient(out problem) : null;
        if (send is null && owned is null) return WorkshopLookup.Empty(query, problem ?? "无法创建 HTTP 客户端");
        var transport = send ?? ((request, token) => owned!.SendAsync(request, token));

        for (var pass = 0; pass < 2; pass++)
        {
            var tagged = pass == 0;
            var url = BrowseUrl(query, tagged);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,zh-CN;q=0.8");
                using var response = await transport(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    notes.Add((tagged ? "1.7 标签检索" : "全量检索") + " HTTP " + (int)response.StatusCode);
                    if (!tagged) problem = "工坊返回 HTTP " + (int)response.StatusCode;
                    continue;
                }
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var candidates = Parse(html, query);
                notes.Add((tagged ? "1.7 标签检索" : "全量检索") + " " + candidates.Count + " 项");
                for (var rank = 0; rank < candidates.Count; rank++)
                {
                    var candidate = candidates[rank];
                    if (seen.TryGetValue(candidate.Id, out var existing))
                    {
                        // A tagged hit beats the same item found by the untagged pass.
                        if (pass == 0) merged[existing] = (candidate, pass, rank);
                        continue;
                    }
                    seen[candidate.Id] = merged.Count;
                    merged.Add((candidate, pass, rank));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return WorkshopLookup.Empty(query, "已取消");
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
            {
                // Timeouts surface as TaskCanceledException without the caller's token being cancelled.
                notes.Add((tagged ? "1.7 标签检索" : "全量检索") + " 失败：" + error.GetType().Name);
                if (!tagged || pass == 1) problem = Describe(error);
            }
        }

        if (merged.Count == 0) return new WorkshopLookup(query, [], notes, problem ?? "工坊没有返回可用结果");

        // Tagged first on a score tie, then Steam's own order. The score itself is left as the page gave it.
        var ordered = merged
            .OrderByDescending(item => item.Candidate.Score)
            .ThenBy(item => item.Pass)
            .ThenBy(item => item.Rank)
            .Select(item => item.Candidate)
            .ToArray();
        return new WorkshopLookup(query, ordered, notes, problem);
    }

    private static string Describe(Exception error) => error switch
    {
        TaskCanceledException => "工坊请求超时（" + LookupTimeout.TotalSeconds + " 秒）",
        HttpRequestException http => "工坊连接失败：" + http.Message,
        _ => error.GetType().Name + ": " + error.Message
    };

    private static HttpClient CreateClient(out string? problem)
    {
        problem = null;
        try
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AllowAutoRedirect = true
            };
            var proxy = ResolveProxy();
            if (proxy is not null) handler.Proxy = new WebProxy(proxy) { BypassProxyOnLocal = false };
            else handler.UseProxy = false;
            // Which route the search actually took. "The workshop search failed" and "the search was not proxied"
            // are the same question on a host without egress, and the answer is not visible in the request itself.
            LastRoute = proxy is null ? "直连" : "代理 " + proxy;
            var client = new HttpClient(handler, disposeHandler: true) { Timeout = LookupTimeout };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            problem = "创建 HTTP 客户端失败：" + error.Message;
            return null!;
        }
    }

    /// <summary>How the last lookup reached Steam: "代理 http://…" or "直连". Read by the page's log line so a
    /// diagnostic exists even when the search itself succeeded.</summary>
    public static string LastRoute { get; private set; } = "尚未发起";

    /// <summary>One request to steamcommunity through whatever <see cref="CreateClient"/> builds.
    ///
    /// <para>This exists because "I set the proxy" and "the proxy is what the search uses" are different claims, and
    /// on a host without egress only the second one matters. The admin page's 测试 button reports this result, so an
    /// operator can change the proxy and find out immediately instead of waiting for the next save import to fail.</para>
    /// </summary>
    public static async Task<WorkshopProbe> ProbeAsync(CancellationToken ct = default)
    {
        var problem = (string?)null;
        var client = CreateClient(out problem);
        if (client is null) return new WorkshopProbe(false, 0, LastRoute, problem ?? "无法创建 HTTP 客户端");
        using (client)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    BrowseEndpoint + "?appid=" + AppId + "&searchtext=probe&browsesort=textsearch&section=readytouseitems");
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                var ok = response.IsSuccessStatusCode;
                return new WorkshopProbe(ok, (int)response.StatusCode, LastRoute,
                    (ok ? "可达" : "HTTP " + (int)response.StatusCode + "（" + response.ReasonPhrase + "）"));
            }
            catch (Exception error)
            {
                return new WorkshopProbe(false, 0, LastRoute, Describe(error));
            }
        }
    }

    // --------------------------------------------------------------- resolving

    /// <summary>
    /// Finds the workshop item that really is <paramref name="packageId"/>, downloading candidates until one's own
    /// <c>About/About.xml</c> says so.
    ///
    /// <para>Verifying instead of trusting the top hit is not defensive coding, it is the measured behaviour: the
    /// results for <c>More Door Variants</c> are the mod, then a Chinese translation of it, then a Russian one, then
    /// an English/Russian re-upload. Only the first declares <c>local.moredoorvariants</c>. A resolve loop that
    /// installed the best-scoring hit would install a translation and then fail at load with a missing-mod error
    /// that no longer names the original.</para>
    ///
    /// <para>The download delegate is what steamcmd does; it lands content under
    /// <c>&lt;game&gt;/steamapps/workshop/content/294100/&lt;id&gt;/</c>, which is the path the game itself reads and
    /// the path the mods directory gets linked from.</para>
    /// </summary>
    public static async Task<WorkshopResolution> ResolveAsync(string packageId, string? displayName,
        Func<long, CancellationToken, Task<WorkshopDownload>> download,
        CancellationToken cancellationToken,
        Func<string?, string?, CancellationToken, Task<WorkshopLookup>>? lookup = null,
        int maximumAttempts = MaximumAttempts)
    {
        var wanted = SaveManifest.CanonicalId(packageId);
        if (wanted.Length == 0) return WorkshopResolution.Failed(packageId, [], "packageId 为空");
        lookup ??= LookupAsync;

        var search = await lookup(packageId, displayName, cancellationToken).ConfigureAwait(false);
        if (!search.Found)
            return WorkshopResolution.Failed(packageId, [], search.Problem ?? "工坊没有搜到候选（" + search.Query + "）");

        var attempts = new List<WorkshopAttempt>();
        foreach (var candidate in search.Candidates.Take(Math.Max(1, maximumAttempts)))
        {
            if (cancellationToken.IsCancellationRequested) return WorkshopResolution.Failed(packageId, attempts, "已取消");
            WorkshopDownload result;
            try
            {
                result = await download(candidate.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                attempts.Add(new WorkshopAttempt(candidate.Id, candidate.Title, candidate.Score, null,
                    "下载失败：" + error.Message));
                continue;
            }
            if (!result.Ok || string.IsNullOrWhiteSpace(result.Path))
            {
                attempts.Add(new WorkshopAttempt(candidate.Id, candidate.Title, candidate.Score, null,
                    result.Detail.Length > 0 ? result.Detail : "下载失败"));
                continue;
            }

            var installed = ModDirectory.Describe(result.Path);
            var actual = installed?.PackageId ?? "";
            if (actual.Length > 0 && SaveManifest.CanonicalId(actual) == wanted)
            {
                attempts.Add(new WorkshopAttempt(candidate.Id, candidate.Title, candidate.Score, actual,
                    "packageId 匹配"));
                return new WorkshopResolution(true, candidate.Id, actual, result.Path, attempts, null);
            }
            attempts.Add(new WorkshopAttempt(candidate.Id, candidate.Title, candidate.Score,
                actual.Length > 0 ? actual : null,
                actual.Length > 0 ? "packageId 不匹配：" + actual : "下载目录里没有 About/About.xml"));
        }

        var tried = attempts.Count == 0
            ? "没有尝试任何候选"
            : "试过 " + string.Join(", ", attempts.Select(item =>
                item.Id + (item.PackageId is { Length: > 0 } id ? "（" + id + "）" : "（无 packageId）")));
        return WorkshopResolution.Failed(packageId, attempts, "没有候选声明 " + packageId + "；" + tried);
    }
}
