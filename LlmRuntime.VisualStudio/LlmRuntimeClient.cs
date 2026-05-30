using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LlmRuntime.VisualStudio;

public sealed class LlmRuntimeClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public LlmRuntimeClient(Uri endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _httpClient = new HttpClient { BaseAddress = endpoint };
    }

    public Uri Endpoint { get; }

    public async Task<string> SendJsonAsync(string method, string endpoint, string json, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), endpoint);
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return response.IsSuccessStatusCode ? body : "HTTP " + (int)response.StatusCode + ": " + body;
    }

    public Task<HttpResponseMessage> AskAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/ide/ask", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public Task<string> GetModelsAsync(CancellationToken cancellationToken = default) =>
        _httpClient.GetStringAsync("/v1/models");

    public Task<string> GetMcpContextAsync(CancellationToken cancellationToken = default) =>
        _httpClient.GetStringAsync("/api/v1/ide/mcp/context");

    public Task<HttpResponseMessage> InlineCompletionAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/ide/completions/inline", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public Task<HttpResponseMessage> NextEditAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/ide/next-edit", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public Task<HttpResponseMessage> StartAgentAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/agent/autonomous/run", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public Task<HttpResponseMessage> CreateCheckpointAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/ide/checkpoints", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public Task<HttpResponseMessage> CodeReviewAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/github/review", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public Task<HttpResponseMessage> IndexWorkspaceAsync(string json, CancellationToken cancellationToken = default) =>
        _httpClient.PostAsync("/api/v1/ide/index", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    public void Dispose() => _httpClient.Dispose();
}
