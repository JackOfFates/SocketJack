using System.Security.Cryptography;
using System.Text;
using SocketJack.Net;

namespace SocketJack.MarketWatcher;

public sealed class IntegratedHeirowStocks : IDisposable
{
    private readonly MarketStore store;
    private readonly MarketEngine engine;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly MarketSocket socket;
    private readonly string session = CreateSession();

    public IntegratedHeirowStocks(MutableTcpServer server, int port)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "MarketWatcher");
        store = new MarketStore(directory);
        engine = new MarketEngine(store);
        socket = new MarketSocket(port, session, engine, "/Stocks/ws");
        engine.Publish = socket.Broadcast;
        server.RegisterProtocol(socket);
        Map(server, "/Stocks", "index.html", "text/html");
        Map(server, "/Stocks/", "index.html", "text/html");
        Map(server, "/Stocks/app.css", "app.css", "text/css");
        Map(server, "/Stocks/app.js", "app.js", "text/javascript");
        loop = Task.Run(() => engine.Run(() => socket.HasClients, stop.Token));
    }

    private void Map(HttpServer server, string path, string resource, string mime)
    {
        server.Map("GET", path, (connection, request, ct) => {
            var response = request.Context.Response;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-HeirowStocks"] = "integrated";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
            if (resource == "index.html") response.Headers["Set-Cookie"] = "marketSession=" + session + "; HttpOnly; SameSite=Strict; Path=/Stocks";
            return new FileResponse(ReadResource(resource), mime);
        });
    }

    private static byte[] ReadResource(string name)
    {
        using Stream stream = typeof(IntegratedHeirowStocks).Assembly.GetManifestResourceStream("heirowStocks." + name)
            ?? throw new IOException("Missing embedded heirowStocks asset: " + name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string CreateSession()
    {
        byte[] bytes = new byte[32];
        using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(bytes);
        var value = new StringBuilder(64);
        foreach (byte item in bytes) value.Append(item.ToString("X2"));
        return value.ToString();
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        engine.Dispose();
        store.Dispose();
        stop.Dispose();
    }
}
