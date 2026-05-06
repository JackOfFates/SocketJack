using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EasyYoloOcr.Example.Wpf.Services;

/// <summary>
/// Handles all web search operations: Google/DuckDuckGo, Bing RSS fallback,
/// video/YouTube/image/download searches.
/// Extracted from MainWindow to reduce monolith size.
/// </summary>
public sealed class SearchService
{
    private static readonly HttpClient _httpClient = new();

    // --- State for pagination ---
    public List<(string Title, string Url, string Snippet)> LastSearchResults { get; private set; } = new();
    public int SearchCurrentPage { get; set; }
    public string? SearchCurrentQuery { get; set; }
    public string? ImageSearchQuery { get; set; }
    public int ImageSearchPage { get; set; }

    /// <summary>
    /// Search the web and return plain-text results for LLM context injection.
    /// Uses DuckDuckGo HTML with Bing RSS fallback.
    /// </summary>
    public async Task<string> SearchAndReturnContext(string query)
    {
        try
        {
            string encoded = Uri.EscapeDataString(query);
            string url = $"https://html.duckduckgo.com/html/?q={encoded}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            var response = await _httpClient.SendAsync(request);
            string html = await response.Content.ReadAsStringAsync();

            var results = ParseHtmlSearchResults(html);

            if (results.Count == 0)
                results = await FetchBingRssResults(query);

            if (results.Count == 0)
                return $"[SEARCH RESULTS for \"{query}\"]\nNo results found.";

            var sb = new StringBuilder();
            sb.AppendLine($"[SEARCH RESULTS for \"{query}\"]");
            for (int i = 0; i < Math.Min(results.Count, 10); i++)
            {
                var (title, resultUrl, snippet) = results[i];
                sb.AppendLine($"{i + 1}. {title}");
                sb.AppendLine($"   URL: {resultUrl}");
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"   {snippet}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"[SEARCH RESULTS for \"{query}\"]\nSearch failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Execute a web search and return structured results.
    /// </summary>
    public async Task<List<(string Title, string Url, string Snippet)>> SearchAsync(string query, int page = 0)
    {
        SearchCurrentQuery = query;
        SearchCurrentPage = page;
        int offset = page * 20;

        try
        {
            string encoded = Uri.EscapeDataString(query);
            string url = $"https://html.duckduckgo.com/html/?q={encoded}&s={offset}&dc={offset + 1}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            var response = await _httpClient.SendAsync(request);
            string html = await response.Content.ReadAsStringAsync();

            var results = ParseHtmlSearchResults(html);

            if (results.Count == 0)
                results = await FetchBingRssResults(query);

            LastSearchResults = results;
            return results;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Search failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Search for videos via DuckDuckGo (appends site:youtube.com).</summary>
    public async Task<List<(string Title, string Url, string Snippet)>> SearchVideosAsync(string query, int page = 0)
    {
        string encoded = Uri.EscapeDataString($"{query} site:youtube.com");
        int offset = page * 20;
        string url = $"https://html.duckduckgo.com/html/?q={encoded}&s={offset}&dc={offset + 1}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        var response = await _httpClient.SendAsync(request);
        string html = await response.Content.ReadAsStringAsync();
        return ParseHtmlSearchResults(html);
    }

    /// <summary>Search YouTube specifically, filtering to YouTube URLs only.</summary>
    public async Task<List<(string Title, string Url, string Snippet)>> SearchYouTubeAsync(string query, int page = 0)
    {
        var results = await SearchVideosAsync(query, page);

        results = results
            .Where(r => r.Url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase)
                      || r.Url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (results.Count == 0)
        {
            var bingResults = await FetchBingRssResults($"{query} site:youtube.com");
            results = bingResults
                .Where(r => r.Url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase)
                          || r.Url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return results;
    }

    /// <summary>Search for downloads by appending "download" to the query.</summary>
    public async Task<List<(string Title, string Url, string Snippet)>> SearchDownloadsAsync(string query, int page = 0)
    {
        string searchQuery = $"{query} download";
        string encoded = Uri.EscapeDataString(searchQuery);
        int offset = page * 20;
        string url = $"https://html.duckduckgo.com/html/?q={encoded}&s={offset}&dc={offset + 1}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        var response = await _httpClient.SendAsync(request);
        string html = await response.Content.ReadAsStringAsync();
        var results = ParseHtmlSearchResults(html);

        if (results.Count == 0)
            results = await FetchBingRssResults(searchQuery);

        return results;
    }

    /// <summary>
    /// Fetch image results. Tries DuckDuckGo Images API first, falls back to
    /// Bing Image HTML scraping, then plain text search with image hints.
    /// </summary>
    public async Task<List<(string Title, string ImageUrl, string ThumbUrl, string SourceUrl, int Width, int Height)>>
        FetchImagesAsync(string query, int page)
    {
        ImageSearchQuery = query;
        ImageSearchPage = page;

        // Try DuckDuckGo Images API (vqd token required)
        var results = await FetchImagesDuckDuckGo(query, page);
        if (results.Count > 0) return results;

        // Fallback: Bing Image HTML scrape
        results = await FetchImagesBing(query, page);
        if (results.Count > 0) return results;

        // Last resort: text search with image hints
        return await FetchImagesFallback(query, page);
    }

    private async Task<List<(string Title, string ImageUrl, string ThumbUrl, string SourceUrl, int Width, int Height)>>
        FetchImagesDuckDuckGo(string query, int page)
    {
        var results = new List<(string, string, string, string, int, int)>();
        string encoded = Uri.EscapeDataString(query);

        // Step 1: Get vqd token from DuckDuckGo
        string vqd = "";
        try
        {
            // Use the newer API endpoint for token extraction
            var tokenReq = new HttpRequestMessage(HttpMethod.Get, $"https://duckduckgo.com/?q={encoded}&iar=images&iax=images&ia=images");
            tokenReq.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            tokenReq.Headers.Add("Accept", "text/html,application/xhtml+xml");
            tokenReq.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            var tokenResp = await _httpClient.SendAsync(tokenReq);
            string tokenHtml = await tokenResp.Content.ReadAsStringAsync();

            // Try multiple vqd patterns — DuckDuckGo changes these frequently
            var vqdPatterns = new[]
            {
                @"vqd=""([^""]+)""",
                @"vqd=([a-zA-Z0-9_-]+)",
                @"""vqd""\s*:\s*""([^""]+)""",
                @"vqd%3D([a-zA-Z0-9_-]+)",
                @"vqd['"":=]+\s*([a-zA-Z0-9_-]{20,})",
                @"vqd\s*=\s*'([^']+)'",
                @"nrj\('i',\s*'([^']+)'\)",
            };
            foreach (var pattern in vqdPatterns)
            {
                var m = Regex.Match(tokenHtml, pattern);
                if (m.Success) { vqd = m.Groups[1].Value; break; }
            }
        }
        catch { }

        if (string.IsNullOrEmpty(vqd))
            return results;

        // Step 2: Query the images JSON API
        int offset = page * 100;
        string apiUrl = $"https://duckduckgo.com/i.js?l=us-en&o=json&q={encoded}&vqd={vqd}&f=,,,,,&p=1&s={offset}";

        var apiReq = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        apiReq.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        apiReq.Headers.Add("Referer", "https://duckduckgo.com/");

        var apiResp = await _httpClient.SendAsync(apiReq);
        string json = await apiResp.Content.ReadAsStringAsync();

        var resultMatches = Regex.Matches(json,
            @"\{[^{}]*?""title""\s*:\s*""(?<title>(?:[^""\\]|\\.)*)""[^{}]*?""image""\s*:\s*""(?<image>(?:[^""\\]|\\.)*)""[^{}]*?""thumbnail""\s*:\s*""(?<thumb>(?:[^""\\]|\\.)*)""[^{}]*?""url""\s*:\s*""(?<url>(?:[^""\\]|\\.)*)""[^{}]*?""width""\s*:\s*(?<w>\d+)[^{}]*?""height""\s*:\s*(?<h>\d+)[^{}]*?\}",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (resultMatches.Count == 0)
        {
            resultMatches = Regex.Matches(json,
                @"\{[^{}]*?""image""\s*:\s*""(?<image>(?:[^""\\]|\\.)*)""[^{}]*?\}",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match m in resultMatches)
            {
                string image = UnescapeJsonString(m.Groups["image"].Value);
                if (!image.StartsWith("http")) continue;

                string block = m.Value;
                string title = ExtractJsonField(block, "title");
                string thumb = ExtractJsonField(block, "thumbnail");
                string srcUrl = ExtractJsonField(block, "url");
                int.TryParse(ExtractJsonField(block, "width"), out int w2);
                int.TryParse(ExtractJsonField(block, "height"), out int h2);

                if (string.IsNullOrEmpty(thumb)) thumb = image;
                results.Add((title, image, thumb, srcUrl, w2, h2));
            }
        }
        else
        {
            foreach (Match m in resultMatches)
            {
                string title = UnescapeJsonString(m.Groups["title"].Value);
                string image = UnescapeJsonString(m.Groups["image"].Value);
                string thumb = UnescapeJsonString(m.Groups["thumb"].Value);
                string srcUrl = UnescapeJsonString(m.Groups["url"].Value);
                int.TryParse(m.Groups["w"].Value, out int w);
                int.TryParse(m.Groups["h"].Value, out int h);

                if (!image.StartsWith("http")) continue;
                if (string.IsNullOrEmpty(thumb)) thumb = image;
                results.Add((title, image, thumb, srcUrl, w, h));
            }
        }

        return results.Take(30).ToList();
    }

    private async Task<List<(string Title, string ImageUrl, string ThumbUrl, string SourceUrl, int Width, int Height)>>
        FetchImagesBing(string query, int page)
    {
        var results = new List<(string, string, string, string, int, int)>();
        try
        {
            string encoded = Uri.EscapeDataString(query);
            int offset = page * 30;
            string url = $"https://www.bing.com/images/search?q={encoded}&first={offset + 1}&count=30&qft=+filterui:photo-photo";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            var response = await _httpClient.SendAsync(request);
            string html = await response.Content.ReadAsStringAsync();

            // Bing embeds image metadata in data attributes or m="" JSON blobs
            var mMatches = Regex.Matches(html,
                @"m=""\{(?<json>[^""]*?murl[^""]*?)\}""",
                RegexOptions.IgnoreCase);

            foreach (Match m in mMatches)
            {
                string raw = m.Groups["json"].Value.Replace("&quot;", "\"").Replace("&amp;", "&");
                string imageUrl = ExtractJsonField("{" + raw + "}", "murl");
                string thumbUrl = ExtractJsonField("{" + raw + "}", "turl");
                string title = ExtractJsonField("{" + raw + "}", "t");
                string srcUrl = ExtractJsonField("{" + raw + "}", "purl");

                if (string.IsNullOrEmpty(imageUrl) || !imageUrl.StartsWith("http")) continue;
                if (string.IsNullOrEmpty(thumbUrl)) thumbUrl = imageUrl;

                int.TryParse(ExtractJsonField("{" + raw + "}", "w"), out int w);
                int.TryParse(ExtractJsonField("{" + raw + "}", "h"), out int h);

                results.Add((title, imageUrl, thumbUrl, srcUrl, w, h));
                if (results.Count >= 30) break;
            }

            // Alternative pattern: data-src in img tags with class "mimg"
            if (results.Count == 0)
            {
                var imgMatches = Regex.Matches(html,
                    @"<img[^>]*class=""mimg""[^>]*(?:data-src|src)=""(?<thumb>[^""]+)""[^>]*/?>",
                    RegexOptions.IgnoreCase);

                foreach (Match im in imgMatches)
                {
                    string thumb = HtmlDecode(im.Groups["thumb"].Value);
                    if (!thumb.StartsWith("http")) continue;
                    results.Add(("", thumb, thumb, "", 0, 0));
                    if (results.Count >= 30) break;
                }
            }
        }
        catch { }

        return results;
    }

    // --- Utility methods ---

    public static string? ExtractYouTubeId(string url)
    {
        var m = Regex.Match(url, @"(?:youtube\.com/watch\?v=|youtu\.be/)([a-zA-Z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string StripHtmlTags(string input)
        => Regex.Replace(input, @"<[^>]+>", "");

    public static string HtmlDecode(string input)
        => input
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");

    // --- Private helpers ---

    private static List<(string Title, string Url, string Snippet)> ParseHtmlSearchResults(string html)
    {
        var results = new List<(string Title, string Url, string Snippet)>();

        var resultPattern = new Regex(
            @"<a[^>]*class=""result__a""[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>.*?<a[^>]*class=""result__snippet""[^>]*>(?<snippet>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in resultPattern.Matches(html))
        {
            string title = HtmlDecode(StripHtmlTags(m.Groups["title"].Value)).Trim();
            string url = HtmlDecode(m.Groups["url"].Value).Trim();
            string snippet = HtmlDecode(StripHtmlTags(m.Groups["snippet"].Value)).Trim();

            if (url.Contains("uddg="))
            {
                var uddgMatch = Regex.Match(url, @"uddg=([^&]+)");
                if (uddgMatch.Success)
                    url = Uri.UnescapeDataString(uddgMatch.Groups[1].Value);
            }

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http")) continue;

            results.Add((title, url, snippet));
        }

        return results;
    }

    private static async Task<List<(string Title, string Url, string Snippet)>> FetchBingRssResults(string query)
    {
        try
        {
            string encoded = Uri.EscapeDataString(query);
            string url = $"https://www.bing.com/search?q={encoded}&format=rss&count=20";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request);
            string rss = await response.Content.ReadAsStringAsync();

            return ParseRssResults(rss);
        }
        catch
        {
            return new List<(string, string, string)>();
        }
    }

    private static List<(string Title, string Url, string Snippet)> ParseRssResults(string rss)
    {
        var results = new List<(string Title, string Url, string Snippet)>();

        var itemPattern = new Regex(
            @"<item>\s*<title>(?<title>.*?)</title>\s*<link>(?<url>.*?)</link>\s*<description>(?<desc>.*?)</description>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in itemPattern.Matches(rss))
        {
            string title = HtmlDecode(m.Groups["title"].Value).Trim();
            string url = HtmlDecode(m.Groups["url"].Value).Trim();
            string snippet = HtmlDecode(StripHtmlTags(m.Groups["desc"].Value)).Trim();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;

            results.Add((title, url, snippet));
            if (results.Count >= 20) break;
        }

        return results;
    }

    private async Task<List<(string Title, string ImageUrl, string ThumbUrl, string SourceUrl, int Width, int Height)>>
        FetchImagesFallback(string query, int page)
    {
        var results = new List<(string, string, string, string, int, int)>();

        string encoded = Uri.EscapeDataString(query + " images");
        int offset = page * 20;
        string url = $"https://html.duckduckgo.com/html/?q={encoded}&s={offset}&dc={offset + 1}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        var response = await _httpClient.SendAsync(request);
        string html = await response.Content.ReadAsStringAsync();

        var textResults = ParseHtmlSearchResults(html);

        foreach (var (title, resultUrl, snippet) in textResults)
        {
            string domain = "";
            try { domain = new Uri(resultUrl).Host; } catch { }
            string thumbUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";

            results.Add((title, resultUrl, thumbUrl, resultUrl, 0, 0));
        }

        return results;
    }

    private static string ExtractJsonField(string json, string fieldName)
    {
        var m = Regex.Match(json, $@"""{fieldName}""\s*:\s*""((?:[^""\\]|\\.)*)""");
        return m.Success ? UnescapeJsonString(m.Groups[1].Value) : "";
    }

    private static string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\\\", "\\")
                .Replace("\\n", "\n").Replace("\\t", "\t");
    }

    /// <summary>
    /// Fetch the og:image (or twitter:image) URL from a page's HEAD section.
    /// Returns null if not found or on error.
    /// </summary>
    public async Task<string?> FetchOgImageAsync(string pageUrl, int timeoutMs = 3000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Read only first ~16KB to get the HEAD section
            using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[8192];
            int totalRead = 0;
            var sb = new StringBuilder();
            while (totalRead < 16384)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) break;
                totalRead += read;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (sb.ToString().Contains("</head>", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            string head = sb.ToString();

            // Try og:image
            var match = Regex.Match(head, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(head, @"<meta[^>]*content=[""']([^""']+)[""'][^>]*property=[""']og:image[""']", RegexOptions.IgnoreCase);

            // Try twitter:image
            if (!match.Success)
                match = Regex.Match(head, @"<meta[^>]*name=[""']twitter:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(head, @"<meta[^>]*content=[""']([^""']+)[""'][^>]*name=[""']twitter:image[""']", RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string imageUrl = HtmlDecode(match.Groups[1].Value);
                if (imageUrl.StartsWith("//"))
                    imageUrl = "https:" + imageUrl;
                if (imageUrl.StartsWith("http"))
                    return imageUrl;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
