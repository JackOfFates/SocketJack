using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SocketJack.MarketWatcher;

public sealed class ProviderException(string message) : Exception(message);
public sealed class PublicClient : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1);
    private readonly TimeProvider clock;
    private string? token;
    private DateTimeOffset expires, nextRequest, blockedUntil;
    public PublicClient(HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.public.com/");
        http.Timeout = TimeSpan.FromSeconds(20);
    }
    public void Forget() { token = null; expires = default; }
    public async Task Authenticate(string secret, CancellationToken ct)
    {
        token = null;
        var json = await Request(HttpMethod.Post, "userapiauthservice/personal/access-tokens", new { secret, validityInMinutes = 15 }, false, ct);
        token = json.Text("accessToken");
        if (string.IsNullOrEmpty(token)) throw new ProviderException("Public did not return an access token. Check API access in your Public settings.");
        expires = clock.GetUtcNow().AddMinutes(14);
    }
    public async Task EnsureToken(string protectedSecret, CancellationToken ct)
    {
        if (token == null || clock.GetUtcNow() >= expires) await Authenticate(MarketStore.Unprotect(protectedSecret), ct);
    }
    private async Task<JsonElement> Request(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < blockedUntil) throw new ProviderException("Public is rate limiting requests. Updates will resume automatically after a pause.");
            if (nextRequest > now) await Task.Delay(nextRequest - now, ct);
            using var request = new HttpRequestMessage(method, path);
            if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, ct);
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(350);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var pause = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
                blockedUntil = DateTimeOffset.UtcNow + (pause > TimeSpan.Zero ? pause : TimeSpan.FromSeconds(60));
                throw new ProviderException("Public is rate limiting requests. Updates will resume automatically after a pause.");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { Forget(); throw new ProviderException("Public rejected the credentials or the token expired. Retry, or replace the key in Settings."); }
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new ProviderException("Public denied access. Enable marketdata access for this account in Public settings.");
            if (!response.IsSuccessStatusCode) throw new ProviderException($"Public could not complete the request (HTTP {(int)response.StatusCode}). Try again shortly.");
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        catch (HttpRequestException) { throw new ProviderException("Cannot reach Public. Check your internet connection."); }
        catch (JsonException) { throw new ProviderException("Public returned an unexpected data format."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Public timed out. Updates will retry automatically."); }
        finally { gate.Release(); }
    }
    public async Task<List<Account>> Accounts(CancellationToken ct)
    {
        var data = await Request(HttpMethod.Get, "userapigateway/trading/account", null, true, ct);
        return data.Items("accounts").Select(x => new Account(x.Text("accountId"), x.Text("accountType"))).Where(x => x.AccountId.Length > 0).ToList();
    }
    public async Task<List<Quote>> Quotes(string account, IEnumerable<string> symbols, CancellationToken ct)
    {
        var data = await Request(HttpMethod.Post, $"userapigateway/marketdata/{Uri.EscapeDataString(account)}/quotes",
            new { instruments = symbols.Select(symbol => new { symbol, type = "EQUITY" }).ToArray() }, true, ct);
        var result = new List<Quote>();
        foreach (var x in data.Items("quotes"))
        {
            if (x.Text("outcome") != "SUCCESS" || !x.TryGetProperty("instrument", out var instrument)) continue;
            x.TryGetProperty("oneDayChange", out var change);
            DateTimeOffset? time = DateTimeOffset.TryParse(x.Text("lastTimestamp"), out var parsed) ? parsed : null;
            result.Add(new(instrument.Text("symbol"), x.Number("last"), change.Number("change"), change.Number("percentChange"), x.Number("volume"), time, DateTimeOffset.UtcNow));
        }
        return result;
    }
    public async Task<List<Instrument>> Instruments(CancellationToken ct)
    {
        var data = await Request(HttpMethod.Get, "userapigateway/trading/instruments?typeFilter=EQUITY&tradingFilter=BUY_AND_SELL", null, true, ct);
        return data.Items("instruments").Where(x => x.TryGetProperty("instrument", out _)).Select(x => {
            string symbol = x.GetProperty("instrument").Text("symbol");
            string name = symbol == "NVDA" ? "NVIDIA" : symbol == "AMD" ? "Advanced Micro Devices" : symbol;
            if (x.TryGetProperty("instrumentDetails", out var details) && details.ValueKind == JsonValueKind.Object && details.Text("name").Length > 0) name = details.Text("name");
            return new Instrument(symbol, name, x.Text("exchange"));
        }).Where(x => x.Symbol.Length > 0).DistinctBy(x => x.Symbol).OrderBy(x => x.Symbol).ToList();
    }
    public async Task<Chart> Bars(string symbol, string period, CancellationToken ct)
    {
        string range = period == "DAILY" ? "QUARTER/ONE_DAY" : period;
        var data = await Request(HttpMethod.Get, $"userapigateway/historicdata/EQUITY/{Uri.EscapeDataString(symbol)}/{range}?tradingSessionToggle=REGULAR_HOURS", null, true, ct);
        var bars = new List<Bar>();
        if (data.TryGetProperty("regularMarket", out var market))
            foreach (var b in market.Items("bars"))
            {
                DateTimeOffset time;
                string raw = b.Text("timestamp");
                if (!DateTimeOffset.TryParse(raw, out time))
                {
                    if (!long.TryParse(raw, out var unix)) continue;
                    try { time = unix > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix); } catch { continue; }
                }
                if ((b.Number("close") ?? b.Number("value")) is decimal price && price > 0) bars.Add(new(time, price, b.Number("volume") ?? 0));
            }
        return new(symbol, period, bars.DistinctBy(b => b.Timestamp).OrderBy(b => b.Timestamp).ToList(), DateTimeOffset.UtcNow);
    }
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
