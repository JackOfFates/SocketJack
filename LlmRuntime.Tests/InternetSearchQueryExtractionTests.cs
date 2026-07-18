using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class InternetSearchQueryExtractionTests
{
    [DataTestMethod]
    [DataRow("search the internet for vincent christopher davis superior wisconsin 54880 age 30", "vincent christopher davis superior wisconsin 54880 age 30")]
    [DataRow("search the web for 3 links for SocketJack releases", "SocketJack releases")]
    [DataRow("find online JackLLM workstation download", "JackLLM workstation download")]
    [DataRow("find current links for SocketJack docs", "SocketJack docs")]
    [DataRow("search the internet for who owns valve", "who owns valve")]
    [DataRow("Search for who owns valve", "who owns valve")]
    public void ExtractExplicitInternetSearchQuery_RemovesCommandSurface(string prompt, string expected)
    {
        Assert.AreEqual(expected, ExtractSearchQuery(prompt));
    }

    [DataTestMethod]
    [DataRow("length", "", "", true)]
    [DataRow("max_tokens", "", "", true)]
    [DataRow("stop", "", "", false)]
    [DataRow("length", "working answer", "", false)]
    [DataRow("length", "", "internal reasoning", false)]
    public void EmptyOutputLimitForExplicitSearch_ForcesInternetSearch(string finishReason, string content, string reasoning, bool expected)
    {
        const string request = "{\"messages\":[{\"role\":\"user\",\"content\":\"search the internet for current SocketJack releases\"}]}";
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("ShouldForceInternetSearchAfterEmptyOutputLimit", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "ShouldForceInternetSearchAfterEmptyOutputLimit was not found.");
        Assert.AreEqual(expected, (bool)method!.Invoke(proxy, new object[] { request, finishReason, content, reasoning })!);
    }

    [TestMethod]
    public void SearchQueryPlannerRequest_PreservesContextAndDisablesTools()
    {
        const string request = """
        {
          "model": "context-model",
          "stream": true,
          "max_tokens": 32,
          "tool_choice": "auto",
          "tools": [{"type":"function","function":{"name":"internet_search"}}],
          "messages": [
            {"role":"system","content":"Use session memory."},
            {"role":"user","content":"We are discussing SocketJack."},
            {"role":"assistant","content":"Understood."},
            {"role":"user","content":"Search for its latest release."}
          ]
        }
        """;
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = GetPrivateMethod("BuildContextAwareInternetSearchPlannerRequest");
        string plannerRequest = (string)method.Invoke(proxy, new object[] { request, 1 })!;

        using JsonDocument document = JsonDocument.Parse(plannerRequest);
        JsonElement root = document.RootElement;
        Assert.AreEqual("context-model", root.GetProperty("model").GetString());
        Assert.IsFalse(root.GetProperty("stream").GetBoolean());
        Assert.AreEqual("none", root.GetProperty("tool_choice").GetString());
        Assert.AreEqual(512, root.GetProperty("max_tokens").GetInt32());
        Assert.IsFalse(root.TryGetProperty("tools", out _));
        JsonElement messages = root.GetProperty("messages");
        Assert.AreEqual(5, messages.GetArrayLength());
        Assert.AreEqual("We are discussing SocketJack.", messages[1].GetProperty("content").GetString());
        Assert.AreEqual("Search for its latest release.", messages[3].GetProperty("content").GetString());
        Assert.AreEqual("system", messages[4].GetProperty("role").GetString());
        StringAssert.Contains(messages[4].GetProperty("content").GetString(), "complete conversation above");
    }

    [DataTestMethod]
    [DataRow("{\"query\":\"SocketJack latest release\"}", true, "SocketJack latest release")]
    [DataRow("```json\n{\"query\":\"SocketJack release notes\"}\n```", true, "SocketJack release notes")]
    [DataRow("SocketJack current release", true, "SocketJack current release")]
    [DataRow("search the internet for SocketJack", false, "")]
    [DataRow("search", false, "")]
    [DataRow("first line\nsecond line", false, "")]
    public void SearchQueryPlannerOutput_ParsesOnlyUsableModelQueries(string output, bool expected, string expectedQuery)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = GetPrivateMethod("TryParseContextAwareInternetSearchPlannerOutput");
        object?[] arguments = { output, null };
        Assert.AreEqual(expected, (bool)method.Invoke(proxy, arguments)!);
        Assert.AreEqual(expectedQuery, arguments[1] as string);
    }

    [DataTestMethod]
    [DataRow("SocketJack latest release", false)]
    [DataRow("  SocketJack   latest release  ", false)]
    [DataRow("search the internet for SocketJack latest release", true)]
    [DataRow("internet_search", true)]
    [DataRow("", true)]
    public void SearchQueryPlanning_PreservesValidNativeQueriesAndRejectsCommandText(string rawQuery, bool expected)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        string normalized = (string)GetPrivateMethod("NormalizeContextAwareInternetSearchQuery").Invoke(proxy, new object[] { rawQuery })!;
        bool requiresPlanning = (bool)GetPrivateMethod("InternetSearchQueryRequiresPlanning").Invoke(proxy, new object[] { rawQuery, normalized })!;
        Assert.AreEqual(expected, requiresPlanning);
    }

    [TestMethod]
    public void SearchQueryPlanning_RejectsOverlongQuery()
    {
        string rawQuery = new string('x', 221);
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        string normalized = (string)GetPrivateMethod("NormalizeContextAwareInternetSearchQuery").Invoke(proxy, new object[] { rawQuery })!;
        Assert.IsTrue((bool)GetPrivateMethod("InternetSearchQueryRequiresPlanning").Invoke(proxy, new object[] { rawQuery, normalized })!);
    }

    [TestMethod]
    public async Task SearchQueryPlanner_UsesFullContextAndRetriesExactlyOnce()
    {
        const string request = "{\"model\":\"context-model\",\"messages\":[{\"role\":\"user\",\"content\":\"We are discussing SocketJack.\"},{\"role\":\"user\",\"content\":\"Search for its latest release.\"}]}";
        var handler = new PlannerResponseHandler(
            "not a usable query\nwith explanation",
            "{\"query\":\"SocketJack latest release\"}");
        using var proxy = CreateProxyWithHttpClient(handler);
        MethodInfo method = GetPrivateMethod("PlanInternetSearchQueryAsync");
        var task = (Task<string>)method.Invoke(proxy, new object[] { "http://planner.test/v1/chat/completions", request, CancellationToken.None })!;

        Assert.AreEqual("SocketJack latest release", await task);
        Assert.AreEqual(2, handler.RequestBodies.Count);
        Assert.IsTrue(handler.RequestBodies.All(body => body.Contains("We are discussing SocketJack.", StringComparison.Ordinal)));
        Assert.IsTrue(handler.RequestBodies.All(body => body.Contains("Search for its latest release.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SearchQueryPlanner_StopsAfterSecondInvalidResponse()
    {
        const string request = "{\"messages\":[{\"role\":\"user\",\"content\":\"Search for it.\"}]}";
        var handler = new PlannerResponseHandler("search", "still invalid\nwith explanation");
        using var proxy = CreateProxyWithHttpClient(handler);
        MethodInfo method = GetPrivateMethod("PlanInternetSearchQueryAsync");
        var task = (Task<string>)method.Invoke(proxy, new object[] { "http://planner.test/v1/chat/completions", request, CancellationToken.None })!;

        InvalidOperationException error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await task);
        StringAssert.Contains(error.Message, "no search was executed");
        Assert.AreEqual(2, handler.RequestBodies.Count);
    }

    private static string ExtractSearchQuery(string prompt)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("ExtractExplicitInternetSearchQuery", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "ExtractExplicitInternetSearchQuery was not found.");
        return (string)method!.Invoke(proxy, new object[] { prompt })!;
    }

    private static MethodInfo GetPrivateMethod(string name)
    {
        MethodInfo? method = typeof(LmVsProxy).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, name + " was not found.");
        return method!;
    }

    private static LmVsProxy CreateProxyWithHttpClient(HttpMessageHandler handler)
    {
        var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        FieldInfo? field = typeof(LmVsProxy).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "_httpClient was not found.");
        field!.SetValue(proxy, new System.Net.Http.HttpClient(handler));
        return proxy;
    }

    private sealed class PlannerResponseHandler : HttpMessageHandler
    {
        private readonly Queue<string> _outputs;

        public PlannerResponseHandler(params string[] outputs)
        {
            _outputs = new Queue<string>(outputs);
        }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            string output = _outputs.Count == 0 ? "" : _outputs.Dequeue();
            string response = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new { role = "assistant", content = output },
                        finish_reason = "stop"
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
