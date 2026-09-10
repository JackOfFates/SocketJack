using System.Net;
using System.Security.Cryptography;
using SocketJack.Net;

namespace SocketJack.MarketWatcher;

public sealed class MarketHost : IDisposable
{
    private readonly MutableTcpServer server;
    private readonly MarketSocket sockets;
    public bool HasClients => sockets.HasClients;
    public MarketHost(MarketEngine engine, int port, string assets)
    {
        server = new MutableTcpServer(port, "heirowStocks");
        server.Options.BindAddress = IPAddress.Loopback;
        server.Options.HttpDefaultCorsOrigin = "";
        string session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sockets = new MarketSocket(port, session, engine);
        server.RegisterProtocol(sockets);
        engine.Publish = sockets.Broadcast;
        foreach (string file in new[] { "index.html", "app.css", "app.js" })
        {
            string path = file == "index.html" ? "/" : "/" + file;
            server.Map("GET", path, (connection, request, ct) => {
                var response = request.Context.Response;
                if (request.Host != $"127.0.0.1:{port}" && request.Host != $"localhost:{port}")
                { request.Context.StatusCodeNumber = 403; return "Invalid host"; }
                string mime = file.EndsWith(".js") ? "text/javascript" : file.EndsWith(".css") ? "text/css" : "text/html";
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-HeirowStocks"] = "1";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'self' http://127.0.0.1:11436 http://localhost:11436; base-uri 'none'; form-action 'none'";
                if (file == "index.html") response.Headers["Set-Cookie"] = $"marketSession={session}; HttpOnly; SameSite=Strict; Path=/";
                return new FileResponse(File.ReadAllBytes(Path.Combine(assets, file)), mime);
            });
        }
        if (!server.Listen()) throw new IOException($"Could not listen on 127.0.0.1:{port}.");
    }
    public void Dispose() { server.StopListening(); server.Dispose(); }
}
