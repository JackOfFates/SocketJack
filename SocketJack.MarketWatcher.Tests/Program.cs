using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketJack.MarketWatcher;
using SocketJack.Net;

int checks = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); checks++; Console.WriteLine("PASS: " + description); }
string root = Path.Combine(Path.GetTempPath(), "heirowStocks-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
if (args.Contains("--serve-ui-fixture"))
{
    string fixtureAssets = Path.Combine(root,"wwwroot"); Directory.CreateDirectory(fixtureAssets);
    foreach(string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"wwwroot"))) File.Copy(file,Path.Combine(fixtureAssets,Path.GetFileName(file)));
    string htmlPath=Path.Combine(fixtureAssets,"index.html");
    File.WriteAllText(htmlPath,File.ReadAllText(htmlPath).Replace("Follow the move.","TEST FIXTURE — synthetic data").Replace("heirowStocks · SocketJack","TEST FIXTURE · heirowStocks"));
    using var fixtureStore=new MarketStore(root);
    using var fixtureEngine=new MarketEngine(fixtureStore,()=>new PublicClient(new FakePublic()));
    using var fixtureHost=new MarketHost(fixtureEngine,17843,fixtureAssets);
    Console.WriteLine("Isolated synthetic UI fixture: http://127.0.0.1:17843/ — use test-key-only. No requests reach Public.");
    await fixtureEngine.Run(()=>fixtureHost.HasClients,CancellationToken.None);
    return;
}
var clock = new TestClock();
var authHandler = new FakePublic();
using (var client = new PublicClient(authHandler, clock))
{
    string encrypted = MarketStore.Protect("test-key-only");
    await client.EnsureToken(encrypted, default);
    await client.EnsureToken(encrypted, default);
    Check(authHandler.AuthCalls == 1, "access token is reused before expiry");
    clock.Now = clock.Now.AddMinutes(15);
    await client.EnsureToken(encrypted, default);
    Check(authHandler.AuthCalls == 2, "expired access token refreshes");
    authHandler.Throttle = true;
    try { await client.Quotes("a1", ["NVDA"], default); throw new Exception("expected throttle"); } catch (ProviderException) { }
    int calls = authHandler.Calls;
    try { await client.Quotes("a1", ["NVDA"], default); } catch (ProviderException) { }
    Check(calls == authHandler.Calls, "Retry-After pauses upstream requests");
}
var now = new DateTimeOffset(2026,9,4,18,0,0,TimeSpan.Zero);
var bars = Enumerable.Range(0,21).Select(i=>new Bar(now.AddDays(i-21), 100+i, 100000)).ToList();
var dayBars = Enumerable.Range(0,5).Select(i=>new Bar(now.AddMinutes(i-5),120+i,100000)).ToList();
var q = new Quote("TEST",125,5,4,100000,now,now);
var daily = new Chart("TEST","DAILY",bars,now);
var day = new Chart("TEST","DAY",dayBars,now);
Check(Momentum.Evaluate(q,daily,day,now) is {Mover:not null,Sustained:not null}, "rising liquid stock qualifies in both views");
Check(Momentum.Evaluate(q,daily with {Bars=bars.Select(b=>b with {Close=100}).ToList()},day,now).Sustained == null, "flat history is excluded from sustained momentum");
Check(Momentum.Evaluate(q,daily with {Bars=bars.Take(10).ToList()},day,now) == (null,null), "insufficient history is excluded");
Check(Momentum.Evaluate(q,daily with {Bars=bars.Select(b=>b with {Volume=1}).ToList()},day,now) == (null,null), "illiquid stocks are excluded");
Check(Momentum.Evaluate(q with {FetchedAt=now.AddHours(-1)},daily,day,now) == (null,null), "stale quotes are excluded");
Check(Momentum.Evaluate(q,daily,day with {Bars=dayBars.Select(b=>b with {Timestamp=b.Timestamp.AddDays(-1)}).ToList()},now).Mover == null, "yesterday's intraday bars cannot count as today's movers");
using (var store = new MarketStore(root))
using (var engine = new MarketEngine(store, ()=>new PublicClient(new FakePublic())))
{
    Guid id = Guid.NewGuid();
    var invalid = await engine.Handle(id,new("saveKey","1",Secret:"invalid"));
    Check(invalid.Error != null && store.Read().ProtectedSecret == null, "invalid key does not persist");
    var good = await engine.Handle(id,new("saveKey","2",Secret:"test-key-only"));
    Check(good.Type == "saved", "valid key and market-data access are saved");
    Check(MarketStore.Unprotect(store.Read().ProtectedSecret!) == "test-key-only", "DPAPI round trip succeeds");
    string disk = string.Join("",Directory.GetFiles(root).Select(File.ReadAllText));
    Check(!disk.Contains("test-key-only") && !disk.Contains("ProtectedSecret") && disk.Contains("SJEP1:"), "database payload is encrypted and contains no plaintext secret");
    Check(!JsonSerializer.Serialize(good,Json.Options).Contains("test-key-only"), "key never appears in a server response");
    await engine.Handle(id,new("saveKey","3",Secret:"invalid"));
    Check(MarketStore.Unprotect(store.Read().ProtectedSecret!) == "test-key-only", "failed replacement preserves the working key");
    var multi = await engine.Handle(id,new("saveKey","4",Secret:"multiple"));
    Check(multi.Type == "accounts", "multiple accounts require selection");
    Check((await engine.Handle(id,new("selectAccount","5",AccountId:"wrong"))).Error != null, "unlisted account is rejected");
    Check((await engine.Handle(id,new("selectAccount","6",AccountId:"a2"))).Type == "saved" && store.Read().AccountId == "a2", "selected account is verified and persisted");
    await engine.Handle(id,new("remove","7",Symbol:"AMD"));
    using(var reloaded = new MarketStore(root)) Check(reloaded.Read().Watchlist.SequenceEqual(new[]{"NVDA"}) && MarketStore.Unprotect(reloaded.Read().ProtectedSecret!) == "multiple", "watchlist and key survive database reload");
    Check((await engine.Handle(id,new("add","8",Symbol:"bad/symbol"))).Error != null, "invalid stock symbols are rejected");
    Check((await engine.Handle(id,new("chart","9",Symbol:"NVDA",Period:"DAY"))).Type == "chart", "chart response uses typed WebSocket contract");
    // Force an actual file-sharing failure, not a mocked success.
    using (var held = new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.None))
    {
        var failure = await engine.Handle(id,new("saveKey","10",Secret:"test-key-only"));
        Check(failure.Error != null, "persistence failure is reported instead of success");
        Check(MarketStore.Unprotect(store.Read().ProtectedSecret!) == "multiple", "persistence failure preserves active credential");
    }
    Check((await engine.Handle(id,new("removeKey","11"))).Type == "saved" && store.Read().ProtectedSecret == null, "removing the key clears stored credentials");

    var listener = new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
    using var server = new MutableTcpServer(port,"heirowStocksTest");server.Options.BindAddress=IPAddress.Loopback;
    var protocol = new MarketSocket(port,"test-session",engine);server.RegisterProtocol(protocol);engine.Publish=protocol.Broadcast;
    Check(server.Listen(),"SocketJack listener starts on loopback");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using(var denied = new ClientWebSocket())
    {
        denied.Options.SetRequestHeader("Origin","http://evil.example");
        try{await denied.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),timeout.Token);throw new Exception("accepted invalid origin");}catch(WebSocketException){checks++;Console.WriteLine("PASS: foreign WebSocket origin is rejected");}
    }
    using(var denied = new ClientWebSocket())
    {
        denied.Options.SetRequestHeader("Origin",$"http://127.0.0.1:{port}");
        try{await denied.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),timeout.Token);throw new Exception("accepted missing token");}catch(WebSocketException){checks++;Console.WriteLine("PASS: missing session token is rejected");}
    }
    for(int round=0;round<2;round++)
    {
        using var ws = new ClientWebSocket();ws.Options.SetRequestHeader("Origin",$"http://127.0.0.1:{port}");ws.Options.SetRequestHeader("Cookie","marketSession=test-session");
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),timeout.Token);
        var initial = await Read(ws,timeout.Token);
        Check(initial.Text("type")=="snapshot",round==0?"authorized WebSocket receives initial snapshot":"reconnection receives fresh snapshot");
        byte[] request=Encoding.UTF8.GetBytes("{\"type\":\"search\",\"id\":\"socket-test\",\"query\":\"NVDA\"}");
        await ws.SendAsync(request.AsMemory(0,15),WebSocketMessageType.Text,false,timeout.Token);
        await ws.SendAsync(request.AsMemory(15),WebSocketMessageType.Text,true,timeout.Token);
        var result=await Read(ws,timeout.Token);Check(result.Text("id")=="socket-test"&&result.Text("type")=="search","fragmented request preserves request ID");
        ws.Abort();
    }
    server.StopListening();
    using(var host=new MarketHost(engine,port,Path.Combine(AppContext.BaseDirectory,"wwwroot")))
    using(var http=new System.Net.Http.HttpClient())
    {
        foreach(var asset in new[]{("/","text/html"),("/app.js","text/javascript"),("/app.css","text/css")})
        {
            using var response=await http.GetAsync($"http://127.0.0.1:{port}"+asset.Item1);
            Check(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType==asset.Item2,"asset MIME type: "+asset.Item1);
        }
    }
}
Console.WriteLine($"All {checks} checks passed. Test data: {root}");
static async Task<JsonElement> Read(ClientWebSocket ws,CancellationToken ct)
{
    using var stream=new MemoryStream();byte[] bytes=new byte[32768];WebSocketReceiveResult r;
    do{r=await ws.ReceiveAsync(bytes,ct);stream.Write(bytes,0,r.Count);}while(!r.EndOfMessage);
    return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
}
sealed class TestClock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
sealed class FakePublic:HttpMessageHandler
{
    public int AuthCalls,Calls;public bool Throttle;private bool multiple;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Calls++;string path=request.RequestUri!.AbsolutePath;
        if(Throttle){var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));return response;}
        if(path.EndsWith("access-tokens")){
            AuthCalls++;var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));string secret=body.Text("secret");
            if(secret=="invalid")return new(HttpStatusCode.Unauthorized);
            multiple=secret=="multiple";return Ok(new{accessToken="fake-test-token"});
        }
        if(path.EndsWith("/account"))return Ok(new{accounts=multiple?new[]{new{accountId="a1",accountType="BROKERAGE"},new{accountId="a2",accountType="IRA"}}:new[]{new{accountId="a1",accountType="BROKERAGE"}}});
        if(path.EndsWith("/quotes")){
            var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            return Ok(new{quotes=body.Items("instruments").Select(i=>new{instrument=new{symbol=i.Text("symbol"),type="EQUITY"},outcome="SUCCESS",last="125.25",lastTimestamp=DateTimeOffset.UtcNow,volume=1000000,oneDayChange=new{change="2",percentChange="1.6"}}).ToArray()});
        }
        if(path.Contains("/historicdata/")){
            bool daily=path.EndsWith("ONE_DAY");
            return Ok(new{regularMarket=new{bars=Enumerable.Range(0,daily?25:40).Select(i=>new{timestamp=daily?DateTimeOffset.UtcNow.AddDays(i-25):DateTimeOffset.UtcNow.AddMinutes(i-40),close=100+i*.6,volume=100000}).ToArray()}});
        }
        return Ok(new{instruments=new[]{"AMD","NVDA","TEST"}.Select(s=>new{instrument=new{symbol=s,type="EQUITY"},trading="BUY_AND_SELL",exchange="NASDAQ",instrumentDetails=new{name=s+" test instrument"}}).ToArray()});
    }
    private static HttpResponseMessage Ok(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
}
