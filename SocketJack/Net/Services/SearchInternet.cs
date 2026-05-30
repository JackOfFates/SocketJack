using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SocketJack.Net.Services;

/// <summary>
/// Runs internet search queries and returns raw JSON so prompt commands can surface
/// structured results without the UI formatter rewriting them.
/// </summary>
public sealed class SearchInternet
{
    private readonly SearchService _searchService;
    private readonly ConcurrentDictionary<string, CookieContainer> _sessionCookies = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SearchInternet(SearchService searchService)
    {
        _searchService = searchService;
    }

    public async Task<string> SearchAsync(string query, int page = 0)
    {
        query = NormalizeSearchQuery(query);
        try
        {
            var results = await _searchService.SearchAsync(query, page);
            var citations = results.Select((r, index) => new SearchInternetCitation
            {
                Index = index + 1,
                Title = r.Title,
                Url = r.Url
            }).ToList();
            var payload = new SearchInternetResponse
            {
                Query = query,
                Page = page,
                Count = results.Count,
                CitationInstructions = "Cite web-backed claims with bracket citations like [1], [2], then include these sources at the bottom of the answer.",
                Citations = citations,
                Results = results.Select((r, index) => new SearchInternetResult
                {
                    Citation = "[" + (index + 1).ToString() + "]",
                    Title = r.Title,
                    Url = r.Url,
                    Snippet = r.Snippet
                }).ToList()
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch (Exception ex)
        {
            var payload = new SearchInternetResponse
            {
                Query = query,
                Page = page,
                Error = ex.Message
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
        }
    }

    public Task<string> SearchOrReadAsync(string? action = null, string? query = null, string? url = null, int page = 0, int take = 8, string? selector = null, string? sessionId = null, int maxChars = 20000)
    {
        if (string.Equals(action, "read", StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(query)))
            return ReadPageAsync(url ?? "", selector, sessionId, maxChars, take);

        return SearchAsync(query ?? "", page);
    }

    public async Task<string> ReadPageAsync(string url, string? selector = null, string? sessionId = null, int maxChars = 20000, int take = 40)
    {
        var payload = new SearchInternetPageReadResponse
        {
            Url = url ?? "",
            FetchedUtc = DateTimeOffset.UtcNow
        };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            payload.Error = "Only absolute HTTP and HTTPS URLs can be read.";
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        var cookieSandbox = string.IsNullOrWhiteSpace(sessionId) ? "transient" : sessionId.Trim();
        var cookieJar = string.IsNullOrWhiteSpace(sessionId)
            ? new CookieContainer()
            : _sessionCookies.GetOrAdd(cookieSandbox, _ => new CookieContainer());
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = cookieJar
        };
        using var client = new System.Net.Http.HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddBrowserLikeHeaders(request);
        using var response = await client.SendAsync(request);
        string html = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
        string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();

        payload.FinalUrl = finalUrl;
        payload.StatusCode = (int)response.StatusCode;
        payload.ReasonPhrase = response.ReasonPhrase ?? "";
        payload.ContentType = response.Content?.Headers?.ContentType?.ToString() ?? "";
        payload.Title = ExtractTitle(html);
        payload.Text = Truncate(NormalizeText(HtmlToText(html)), Math.Max(1000, Math.Min(100000, maxChars)));
        payload.Links = ExtractLinks(html, finalUrl, take);
        payload.Controls = ExtractControls(html, take);
        payload.Tables = ExtractTables(html, Math.Min(8, take));
        payload.Lists = ExtractLists(html, Math.Min(8, take));
        payload.SelectorMatches = ExtractSelectorMatches(html, finalUrl, selector, Math.Min(40, take));
        payload.CookieSandbox = cookieSandbox;
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static void AddBrowserLikeHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html ?? "", "<title[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeText(WebUtility.HtmlDecode(match.Groups["title"].Value)) : "";
    }

    private static string HtmlToText(string html)
    {
        string text = Regex.Replace(html ?? "", "<script\\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<style\\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(text);
    }

    private static string NormalizeText(string value)
        => Regex.Replace(value ?? "", "\\s+", " ").Trim();

    private static string NormalizeSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        string cleaned = NormalizeText(query);
        for (int i = 0; i < 3; i++)
        {
            string before = cleaned;
            cleaned = Regex.Replace(cleaned, "(?is)^\\b(?:search|look\\s+up|find|google)\\s+(?:(?:the\\s+)?(?:internet|web)|online)\\s+(?:for|about|on|related\\s+to|of)?\\s*", "");
            cleaned = Regex.Replace(cleaned, "(?is)^\\b(?:search|look\\s+up|find|google)\\s+(?:for|about|on|related\\s+to|of)?\\s+", "");
            cleaned = Regex.Replace(cleaned, "(?is)^\\b(?:(?:the\\s+)?(?:internet|web)|online)\\s+(?:for|about|on|related\\s+to|of)\\s+", "");
            cleaned = Regex.Replace(cleaned, "(?is)^\\b(?:for|about|on|related\\s+to|of)\\s+", "");
            cleaned = cleaned.Trim().Trim('"', '\'', '`', ':', '-', ' ', '\t');
            if (string.Equals(before, cleaned, StringComparison.Ordinal))
                break;
        }
        return cleaned.Trim().Trim('"', '\'', '`', ':', '-', ' ', '\t');
    }

    private static string Truncate(string value, int maxChars)
        => string.IsNullOrEmpty(value) || value.Length <= maxChars ? value ?? "" : value.Substring(0, maxChars).TrimEnd() + "\n\n... trimmed";

    private static List<SearchInternetPageLink> ExtractLinks(string html, string baseUrl, int take)
    {
        var links = new List<SearchInternetPageLink>();
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri);
        foreach (Match match in Regex.Matches(html ?? "", "<a\\b(?<attrs>[^>]*)>(?<text>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase))
        {
            string href = ExtractAttribute(match.Groups["attrs"].Value, "href");
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (baseUri != null && Uri.TryCreate(baseUri, href, out var resolved))
                href = resolved.ToString();
            links.Add(new SearchInternetPageLink { Index = links.Count + 1, Text = Truncate(NormalizeText(HtmlToText(match.Groups["text"].Value)), 180), Url = href });
            if (links.Count >= take) break;
        }
        return links;
    }

    private static List<SearchInternetPageControl> ExtractControls(string html, int take)
    {
        var controls = new List<SearchInternetPageControl>();
        foreach (Match match in Regex.Matches(html ?? "", "<(?<tag>input)\\b(?<attrs>[^>]*)/?>|<(?<tag>textarea|select|button)\\b(?<attrs>[^>]*)>(?<inner>[\\s\\S]*?)</\\k<tag>>", RegexOptions.IgnoreCase))
        {
            string tag = match.Groups["tag"].Value.ToLowerInvariant();
            string attrs = match.Groups["attrs"].Value;
            string type = FirstNonEmpty(ExtractAttribute(attrs, "type"), tag);
            string name = FirstNonEmpty(ExtractAttribute(attrs, "name"), ExtractAttribute(attrs, "id"));
            string label = FirstNonEmpty(ExtractAttribute(attrs, "aria-label"), ExtractAttribute(attrs, "placeholder"), ExtractAttribute(attrs, "title"), NormalizeText(HtmlToText(match.Groups["inner"].Value)));
            controls.Add(new SearchInternetPageControl { Index = controls.Count + 1, Tag = tag, Type = type, Name = name, Label = Truncate(label, 180) });
            if (controls.Count >= take) break;
        }
        return controls;
    }

    private static List<SearchInternetPageTable> ExtractTables(string html, int take)
    {
        var tables = new List<SearchInternetPageTable>();
        foreach (Match table in Regex.Matches(html ?? "", "<table\\b[^>]*>(?<body>[\\s\\S]*?)</table>", RegexOptions.IgnoreCase))
        {
            var rows = new List<List<string>>();
            foreach (Match row in Regex.Matches(table.Groups["body"].Value, "<tr\\b[^>]*>(?<row>[\\s\\S]*?)</tr>", RegexOptions.IgnoreCase))
            {
                var cells = Regex.Matches(row.Groups["row"].Value, "<t[hd]\\b[^>]*>(?<cell>[\\s\\S]*?)</t[hd]>", RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(cell => Truncate(NormalizeText(HtmlToText(cell.Groups["cell"].Value)), 140))
                    .Where(cell => !string.IsNullOrWhiteSpace(cell))
                    .Take(8)
                    .ToList();
                if (cells.Count > 0) rows.Add(cells);
                if (rows.Count >= 20) break;
            }
            if (rows.Count > 0) tables.Add(new SearchInternetPageTable { Index = tables.Count + 1, Rows = rows });
            if (tables.Count >= take) break;
        }
        return tables;
    }

    private static List<SearchInternetPageList> ExtractLists(string html, int take)
    {
        var lists = new List<SearchInternetPageList>();
        foreach (Match list in Regex.Matches(html ?? "", "<(?<tag>ul|ol)\\b[^>]*>(?<body>[\\s\\S]*?)</\\k<tag>>", RegexOptions.IgnoreCase))
        {
            var items = Regex.Matches(list.Groups["body"].Value, "<li\\b[^>]*>(?<item>[\\s\\S]*?)</li>", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(item => Truncate(NormalizeText(HtmlToText(item.Groups["item"].Value)), 240))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(20)
                .ToList();
            if (items.Count > 0) lists.Add(new SearchInternetPageList { Index = lists.Count + 1, Ordered = list.Groups["tag"].Value.Equals("ol", StringComparison.OrdinalIgnoreCase), Items = items });
            if (lists.Count >= take) break;
        }
        return lists;
    }

    private static List<string> ExtractSelectorMatches(string html, string baseUrl, string? selector, int take)
    {
        var results = new List<string>();
        selector = (selector ?? "").Trim();
        if (string.IsNullOrWhiteSpace(selector)) return results;
        string tag = selector.StartsWith("#") || selector.StartsWith(".") ? "[a-zA-Z][\\w:-]*" : Regex.Escape(selector.Split('.', '#')[0]);
        foreach (Match match in Regex.Matches(html ?? "", "<(?<tag>" + tag + ")\\b(?<attrs>[^>]*)>(?<inner>[\\s\\S]*?)</\\k<tag>>|<(?<tag>" + tag + ")\\b(?<attrs>[^>]*)/?>", RegexOptions.IgnoreCase))
        {
            string attrs = match.Groups["attrs"].Value;
            if (selector.StartsWith("#") && !ExtractAttribute(attrs, "id").Equals(selector.Substring(1), StringComparison.OrdinalIgnoreCase)) continue;
            if (selector.StartsWith(".") && !Regex.IsMatch(ExtractAttribute(attrs, "class"), "(^|\\s)" + Regex.Escape(selector.Substring(1)) + "(\\s|$)", RegexOptions.IgnoreCase)) continue;
            if (selector.Contains('.') && !Regex.IsMatch(ExtractAttribute(attrs, "class"), "(^|\\s)" + Regex.Escape(selector.Split('.')[1].Split('#')[0]) + "(\\s|$)", RegexOptions.IgnoreCase)) continue;
            results.Add("<" + match.Groups["tag"].Value.ToLowerInvariant() + "> " + Truncate(NormalizeText(HtmlToText(match.Groups["inner"].Value)), 300));
            if (results.Count >= take) break;
        }
        return results;
    }

    private static string ExtractAttribute(string attrs, string name)
    {
        var match = Regex.Match(attrs ?? "", "(^|\\s)" + Regex.Escape(name) + "\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)'|(?<v>[^\\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value ?? "") : "";
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}

public sealed class SearchInternetResponse
{
    public string Query { get; set; } = "";
    public int Page { get; set; }
    public int Count { get; set; }
    public string? Error { get; set; }
    public string CitationInstructions { get; set; } = "";
    public List<SearchInternetCitation> Citations { get; set; } = [];
    public List<SearchInternetResult> Results { get; set; } = [];
}

public sealed class SearchInternetCitation
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class SearchInternetResult
{
    public string Citation { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
}

public sealed class SearchInternetPageReadResponse
{
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string CookieSandbox { get; set; } = "";
    public string? Error { get; set; }
    public DateTimeOffset FetchedUtc { get; set; }
    public List<SearchInternetPageLink> Links { get; set; } = [];
    public List<SearchInternetPageControl> Controls { get; set; } = [];
    public List<SearchInternetPageTable> Tables { get; set; } = [];
    public List<SearchInternetPageList> Lists { get; set; } = [];
    public List<string> SelectorMatches { get; set; } = [];
}

public sealed class SearchInternetPageLink
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class SearchInternetPageControl
{
    public int Index { get; set; }
    public string Tag { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class SearchInternetPageTable
{
    public int Index { get; set; }
    public List<List<string>> Rows { get; set; } = [];
}

public sealed class SearchInternetPageList
{
    public int Index { get; set; }
    public bool Ordered { get; set; }
    public List<string> Items { get; set; } = [];
}
