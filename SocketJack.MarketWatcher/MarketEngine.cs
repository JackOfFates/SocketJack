using System.Text.Json;
using System.Text.RegularExpressions;

namespace SocketJack.MarketWatcher;

public sealed class MarketEngine : IDisposable
{
    private readonly MarketStore store;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Func<PublicClient> factory;
    private PublicClient provider;
    private SavedState state;
    private CancellationTokenSource providerCancellation = new();
    private readonly Dictionary<Guid, Pending> pending = [];
    private readonly Dictionary<string, Candidate> movers = [], sustained = [];
    private readonly HashSet<string> visited = [];
    private int cursor;
    private DateTimeOffset nextScan, lastCompleted, nextQuotes, nextPersist;
    private string status = "Connect Public to start watching the market.";
    private bool connected;
    public Action<ServerMessage>? Publish { get; set; }
    private record Pending(PublicClient Client, string ProtectedSecret, List<Account> Accounts);
    public MarketEngine(MarketStore store, Func<PublicClient>? factory = null)
    {
        this.store = store; this.factory = factory ?? (() => new PublicClient()); provider = this.factory(); state = store.Read();
        if (state.ProtectedSecret != null) status = "Saved key found. Connecting to Public…";
    }
    private object Snapshot() => new {
        configured = state.ProtectedSecret != null, connected, status, accountId = state.AccountId,
        watchlist = state.Watchlist.Select(symbol => new { symbol, name = Name(symbol), quote = state.Quotes.GetValueOrDefault(symbol), sparkline = state.Charts.GetValueOrDefault(symbol + ":DAY")?.Bars.TakeLast(80).ToList() }).ToList(),
        movers = movers.Values.Where(x => !state.Watchlist.Contains(x.Symbol) && DateTimeOffset.UtcNow - x.EvaluatedAt < TimeSpan.FromMinutes(20)).OrderByDescending(x => x.DailyPercent).Take(20).ToList(),
        sustained = sustained.Values.Where(x => !state.Watchlist.Contains(x.Symbol) && DateTimeOffset.UtcNow - x.EvaluatedAt < TimeSpan.FromMinutes(20)).OrderByDescending(x => x.TwentyDayPercent).Take(20).ToList(),
        scan = new { scanned = visited.Count, total = Universe().Count, lastCompleted = lastCompleted == default ? (DateTimeOffset?)null : lastCompleted },
        sentAt = DateTimeOffset.UtcNow
    };
    private string Name(string symbol) => symbol == "NVDA" ? "NVIDIA" : symbol == "AMD" ? "Advanced Micro Devices" : state.Instruments.Find(i => i.Symbol == symbol)?.Name ?? symbol;
    private List<Instrument> Universe() => state.Instruments.Where(x => IsUsExchange(x.Exchange)).ToList();
    public static bool IsUsExchange(string exchange) => new[] { "NASDAQ", "NYSE", "AMEX", "ARCA", "BATS", "XNAS", "XNYS", "XASE", "ARCX", "NYSEARCA", "NYSEAMERICAN" }.Contains(exchange.Replace(" ", "").ToUpperInvariant());
    private void Broadcast() => Publish?.Invoke(new("snapshot", null, Snapshot()));
    private static string Symbol(string? input)
    {
        string symbol = input?.Trim().ToUpperInvariant() ?? "";
        if (!Regex.IsMatch(symbol, "^[A-Z][A-Z0-9.\\-]{0,14}$")) throw new ProviderException("Enter a valid stock symbol.");
        return symbol;
    }
    private async Task Persist(Action<SavedState> mutate)
    {
        var copy = JsonSerializer.Deserialize<SavedState>(JsonSerializer.Serialize(state, Json.Options), Json.Options)!;
        mutate(copy);
        try { await store.Write(copy); }
        catch { throw new ProviderException("Could not save the encrypted database. Check disk space and folder access, then retry."); }
        state = copy;
    }
    public async Task<ServerMessage> Handle(Guid client, ClientMessage message)
    {
        if (message.Type == "removeKey") providerCancellation.Cancel();
        await gate.WaitAsync();
        try
        {
            switch (message.Type)
            {
                case "snapshot": return new("snapshot", message.Id, Snapshot());
                case "saveKey":
                    if (string.IsNullOrWhiteSpace(message.Secret) || message.Secret.Length > 8192) throw new ProviderException("Enter your Public secret key.");
                    var candidate = factory();
                    try
                    {
                        await candidate.Authenticate(message.Secret.Trim(), CancellationToken.None);
                        var accounts = await candidate.Accounts(CancellationToken.None);
                        if (accounts.Count == 0) throw new ProviderException("No Public accounts are available for this key.");
                        var entry = new Pending(candidate, MarketStore.Protect(message.Secret.Trim()), accounts);
                        if (accounts.Count == 1) { await Commit(entry, accounts[0].AccountId); Broadcast(); return new("saved", message.Id, new { message = "Key encrypted and saved. Public is connected." }); }
                        if (pending.Remove(client, out var old)) old.Client.Dispose();
                        pending[client] = entry;
                        return new("accounts", message.Id, accounts);
                    }
                    catch { candidate.Dispose(); throw; }
                case "selectAccount":
                    if (!pending.TryGetValue(client, out var selection)) throw new ProviderException("Enter the key again to select an account.");
                    if (!selection.Accounts.Any(a => a.AccountId == message.AccountId)) throw new ProviderException("Select an account from the list.");
                    await Commit(selection, message.AccountId!); pending.Remove(client); Broadcast();
                    return new("saved", message.Id, new { message = "Key encrypted and saved. Public is connected." });
                case "removeKey":
                    try { await Persist(s => { s.ProtectedSecret = null; s.AccountId = null; }); }
                    finally { providerCancellation.Dispose(); providerCancellation = new(); }
                    provider.Forget(); connected = false;
                    foreach (var p in pending.Values) p.Client.Dispose(); pending.Clear();
                    movers.Clear(); sustained.Clear(); visited.Clear(); cursor = 0;
                    status = "Key removed. Public requests are stopped."; Broadcast(); return new("saved", message.Id, new { message = status });
                case "search":
                    var query = (message.Query ?? "").Trim();
                    var source = state.Instruments.Count > 0 ? state.Instruments : new List<Instrument> { new("NVDA", "NVIDIA", "NASDAQ"), new("AMD", "Advanced Micro Devices", "NASDAQ") };
                    return new("search", message.Id, source.Where(i => i.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.Symbol.Equals(query, StringComparison.OrdinalIgnoreCase)).Take(12).ToList());
                case "add":
                    string add = Symbol(message.Symbol);
                    if (!state.Instruments.Any(i => i.Symbol == add) && add is not "NVDA" and not "AMD") throw new ProviderException("Connect Public and select a symbol from search results.");
                    if (state.Watchlist.Count >= 50) throw new ProviderException("The watchlist supports up to 50 stocks.");
                    await Persist(s => { if (!s.Watchlist.Contains(add)) s.Watchlist.Add(add); }); nextQuotes = default; Broadcast(); return new("ok", message.Id);
                case "remove":
                    string remove = Symbol(message.Symbol); await Persist(s => s.Watchlist.Remove(remove)); Broadcast(); return new("ok", message.Id);
                case "chart":
                    string symbol = Symbol(message.Symbol), period = message.Period ?? "DAY";
                    if (!new[] { "DAY", "WEEK", "MONTH", "YEAR" }.Contains(period)) throw new ProviderException("Choose day, week, month, or year.");
                    if (!state.Watchlist.Contains(symbol) && !state.Instruments.Any(i => i.Symbol == symbol)) throw new ProviderException("Unknown stock symbol.");
                    var chart = await Chart(symbol, period); return new("chart", message.Id, chart);
                default: throw new ProviderException("Unknown request.");
            }
        }
        catch (ProviderException ex) { return new("error", message.Id, Error: ex.Message); }
        catch (OperationCanceledException) { return new("error", message.Id, Error: "Public request cancelled."); }
        catch { return new("error", message.Id, Error: "The operation could not be completed. Check Settings and retry."); }
        finally { gate.Release(); }
    }
    private async Task Commit(Pending candidate, string account)
    {
        var quotes = await candidate.Client.Quotes(account, ["NVDA", "AMD"], CancellationToken.None);
        if (!quotes.Any(q => q.Last > 0)) throw new ProviderException("Public returned no usable quotes. Confirm marketdata access for this account.");
        await Persist(s => { s.ProtectedSecret = candidate.ProtectedSecret; s.AccountId = account; foreach (var q in quotes) s.Quotes[q.Symbol] = q; });
        provider.Dispose(); provider = candidate.Client;
        providerCancellation.Dispose(); providerCancellation = new();
        connected = true; status = "Connected to Public"; nextQuotes = default; nextScan = default;
    }
    private async Task Ready()
    {
        if (state.ProtectedSecret == null || state.AccountId == null) throw new ProviderException("Add your Public secret key in Settings to load market data.");
        await provider.EnsureToken(state.ProtectedSecret, providerCancellation.Token);
    }
    private async Task<Chart> Chart(string symbol, string period)
    {
        string key = symbol + ":" + period;
        if (state.Charts.TryGetValue(key, out var cached))
        {
            bool valid = period == "DAILY" ? Momentum.MarketDate(cached.FetchedAt) == Momentum.MarketDate(DateTimeOffset.UtcNow)
                : DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromSeconds(period == "DAY" ? 60 : 900);
            if (valid || state.ProtectedSecret == null) return cached with { Stale = !valid || state.ProtectedSecret == null };
        }
        await Ready();
        Chart result;
        try { result = await provider.Bars(symbol, period, providerCancellation.Token); }
        catch (ProviderException) when (cached != null && period != "DAILY") { return cached with { Stale = true }; }
        state.Charts[key] = result;
        return result;
    }
    public async Task Disconnect(Guid client)
    {
        await gate.WaitAsync();
        try { if (pending.Remove(client, out var p)) p.Client.Dispose(); } finally { gate.Release(); }
    }
    public async Task Run(Func<bool> hasClients, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { break; }
            if (!hasClients()) continue;
            await gate.WaitAsync(stop);
            try
            {
                if (state.ProtectedSecret == null) continue;
                await Ready();
                var now = DateTimeOffset.UtcNow;
                if (now >= nextQuotes)
                {
                    nextQuotes = now.AddSeconds(5);
                    if (state.Watchlist.Count > 0)
                    {
                        var quotes = await provider.Quotes(state.AccountId!, state.Watchlist, providerCancellation.Token);
                        if (!quotes.Any(q => q.Last > 0)) throw new ProviderException("Public returned no usable watchlist quotes. Check market-data access in Settings.");
                        foreach (var q in quotes) state.Quotes[q.Symbol] = q;
                    }
                    connected = true; status = "Connected to Public";
                    Broadcast();
                }
                if (state.Instruments.Count == 0 || now - state.CatalogFetchedAt > TimeSpan.FromDays(1))
                {
                    state.Instruments = await provider.Instruments(providerCancellation.Token); state.CatalogFetchedAt = now;
                }
                // One watchlist history refresh per iteration avoids holding the gate for a full list.
                string? missing = state.Watchlist.FirstOrDefault(s => !state.Charts.TryGetValue(s + ":DAY", out var c) || now - c.FetchedAt > TimeSpan.FromMinutes(2));
                if (missing != null) await Chart(missing, "DAY");
                if (now >= nextScan)
                {
                    var universe = Universe();
                    if (cursor >= universe.Count) { cursor = 0; visited.Clear(); movers.Clear(); sustained.Clear(); }
                    if (universe.Count > 0)
                    {
                        string sym = universe[cursor].Symbol;
                        var qs = await provider.Quotes(state.AccountId!, [sym], providerCancellation.Token);
                        movers.Remove(sym); sustained.Remove(sym);
                        if (qs.FirstOrDefault() is { Last: >= 5 } quote)
                        {
                            var daily = await Chart(sym, "DAILY");
                            var day = quote.Percent > 0 ? await Chart(sym, "DAY") : null;
                            var rank = Momentum.Evaluate(quote, daily, day, DateTimeOffset.UtcNow);
                            if (rank.Mover != null) movers[sym] = rank.Mover;
                            if (rank.Sustained != null) sustained[sym] = rank.Sustained;
                        }
                        visited.Add(sym); cursor++;
                        if (cursor >= universe.Count) { lastCompleted = DateTimeOffset.UtcNow; nextScan = lastCompleted.AddMinutes(15); }
                        Broadcast();
                    }
                }
                if (now >= nextPersist) { await store.Write(state); nextPersist = now.AddMinutes(1); }
            }
            catch (OperationCanceledException) { connected = false; }
            catch (Exception ex)
            {
                connected = false; status = ex is ProviderException ? ex.Message : "Saved data is available. Public connection or local storage needs attention.";
                Broadcast();
            }
            finally { gate.Release(); }
        }
    }
    public void Dispose() { providerCancellation.Cancel(); provider.Dispose(); foreach (var p in pending.Values) p.Client.Dispose(); providerCancellation.Dispose(); }
}
