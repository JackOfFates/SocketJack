var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", async context => {
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("WebSocketClientDemo.html");
});
app.Run();
