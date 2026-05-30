using System.Reflection;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string clientDemoHtml = LoadEmbeddedHtml("WebSocketClientDemo.html");

app.MapGet("/", context => {
    context.Response.ContentType = "text/html";
    return context.Response.WriteAsync(clientDemoHtml);
});
app.Run();

static string LoadEmbeddedHtml(string fileName)
{
    string resourceName = "WebSocketTestApplication.Html." + fileName;
    using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    if (stream == null)
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>WebSocket Test</title></head><body><h1>WebSocket test page missing</h1></body></html>";

    using var reader = new StreamReader(stream, Encoding.UTF8, true);
    return reader.ReadToEnd();
}
