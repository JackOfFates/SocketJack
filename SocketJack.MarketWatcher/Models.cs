using System.Globalization;
using System.Text.Json;

namespace SocketJack.MarketWatcher;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Text(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ToString() : "";
    public static decimal? Number(this JsonElement e, string key) => decimal.TryParse(e.Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    public static IEnumerable<JsonElement> Items(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
public record ClientMessage(string Type, string? Id, string? Symbol = null, string? Period = null, string? Query = null, string? Secret = null, string? AccountId = null);
public record ServerMessage(string Type, string? Id, object? Data = null, string? Error = null);
public record Instrument(string Symbol, string Name, string Exchange);
public record Account(string AccountId, string AccountType);
public record Quote(string Symbol, decimal? Last, decimal? Change, decimal? Percent, decimal? Volume, DateTimeOffset? Timestamp, DateTimeOffset FetchedAt);
public record Bar(DateTimeOffset Timestamp, decimal Close, decimal Volume);
public record Chart(string Symbol, string Period, List<Bar> Bars, DateTimeOffset FetchedAt, bool Stale = false);
public record Candidate(string Symbol, decimal Price, decimal DailyPercent, decimal FiveDayPercent, decimal TwentyDayPercent, decimal AverageDollarVolume, DateTimeOffset AsOf, DateTimeOffset EvaluatedAt);
public sealed class SavedState
{
    public string? ProtectedSecret { get; set; }
    public string? AccountId { get; set; }
    public List<string> Watchlist { get; set; } = new List<string> { "NVDA", "AMD" };
    public Dictionary<string, Quote> Quotes { get; set; } = new Dictionary<string, Quote>();
    public Dictionary<string, Chart> Charts { get; set; } = new Dictionary<string, Chart>();
    public List<Instrument> Instruments { get; set; } = new List<Instrument>();
    public DateTimeOffset CatalogFetchedAt { get; set; }
}
public static class Momentum
{
    public static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    public static DateTime MarketDate(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Eastern).Date;
    public static decimal Slope(IReadOnlyList<Bar> bars)
    {
        if (bars.Count < 2) return 0;
        decimal meanX = (bars.Count - 1) / 2m, meanY = bars.Average(b => b.Close), numerator = 0, denominator = 0;
        for (int i = 0; i < bars.Count; i++) { numerator += (i - meanX) * (bars[i].Close - meanY); denominator += (i - meanX) * (i - meanX); }
        return numerator / denominator;
    }
    public static (Candidate? Mover, Candidate? Sustained) Evaluate(Quote q, Chart daily, Chart? intraday, DateTimeOffset now)
    {
        // Do not treat the incomplete current daily candle or pre-IPO fill as history.
        var bars = daily.Bars.Where(b => MarketDate(b.Timestamp) < MarketDate(now) && b.Close > 0 && b.Volume > 0)
            .GroupBy(b => MarketDate(b.Timestamp)).Select(g => g.Last()).OrderBy(b => b.Timestamp).TakeLast(21).ToList();
        if (q.Last is not >= 5 || q.Percent is null || bars.Count < 21 || now - q.FetchedAt > TimeSpan.FromMinutes(20)
            || now - bars[^1].Timestamp > TimeSpan.FromDays(5)) return (null, null);
        decimal dollars = bars.TakeLast(20).Average(b => b.Close * b.Volume);
        if (dollars < 5_000_000) return (null, null);
        decimal five = (bars[^1].Close / bars[^6].Close - 1) * 100, twenty = (bars[^1].Close / bars[0].Close - 1) * 100;
        var result = new Candidate(q.Symbol, q.Last.Value, q.Percent.Value, five, twenty, dollars, q.Timestamp ?? q.FetchedAt, now);
        var recent = bars.TakeLast(20).ToList();
        bool sustained = five > 0 && twenty > 0 && Slope(recent) > 0 && q.Last > recent.Average(b => b.Close);
        var day = intraday?.Bars.Where(b => MarketDate(b.Timestamp) == MarketDate(now)).ToList() ?? [];
        bool mover = q.Percent > 0 && day.Count >= 3 && Slope(day) > 0 && now - day[^1].Timestamp < TimeSpan.FromMinutes(20);
        return (mover ? result : null, sustained ? result : null);
    }
}
