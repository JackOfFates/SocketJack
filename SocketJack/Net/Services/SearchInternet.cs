using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace EasyYoloOcr.Example.Wpf.Services;

/// <summary>
/// Runs internet search queries and returns raw JSON so prompt commands can surface
/// structured results without the UI formatter rewriting them.
/// </summary>
public sealed class SearchInternet
{
    private readonly SearchService _searchService;

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
        try
        {
            var results = await _searchService.SearchAsync(query, page);
            var payload = new SearchInternetResponse
            {
                Query = query,
                Page = page,
                Count = results.Count,
                Results = results.Select(r => new SearchInternetResult
                {
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
}

public sealed class SearchInternetResponse
{
    public string Query { get; set; } = "";
    public int Page { get; set; }
    public int Count { get; set; }
    public string? Error { get; set; }
    public List<SearchInternetResult> Results { get; set; } = [];
}

public sealed class SearchInternetResult
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
}
