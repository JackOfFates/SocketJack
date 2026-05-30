using System.Net;
using System.Net.Http.Json;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmToolRegistryTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void Registry_RegistersWindowsDesktopAutomationBuiltInTool()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            var definition = registry.GetDefinition(WindowsDesktopAutomationTool.ToolId);

            Assert.IsNotNull(definition);
            Assert.AreEqual(LlmToolSourceType.BuiltInSocketJack, definition.SourceType);
            Assert.AreEqual(LlmToolApprovalMode.AskEveryTime, definition.ApprovalMode);
            Assert.IsTrue(definition.Permissions.HasFlag(LlmToolPermissions.DesktopAutomation));
            StringAssert.Contains(JsonSerializer.Serialize(registry.ExportOpenAiTools()), WindowsDesktopAutomationTool.ToolId);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task BuiltInDesktopAutomationTool_RequiresApprovalAndReturnsCapabilities()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            var invoker = new LlmToolInvoker(registry);

            var denied = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = WindowsDesktopAutomationTool.ToolId,
                Input = JsonDocument.Parse("{\"operation\":\"capabilities\"}").RootElement.Clone()
            });

            Assert.IsFalse(denied.Success);
            Assert.IsTrue(denied.ApprovalRequired);
            StringAssert.Contains(denied.Error, "requires approval");

            var approved = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = WindowsDesktopAutomationTool.ToolId,
                Approved = true,
                Input = JsonDocument.Parse("{\"operation\":\"capabilities\"}").RootElement.Clone()
            });

            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue(approved.Success, approved.Error);
                Assert.AreEqual(WindowsDesktopAutomationTool.ToolId, approved.OutputJson!.Value.GetProperty("tool").GetString());
                Assert.IsTrue(approved.OutputJson.Value.GetProperty("safety").GetProperty("approval_required").GetBoolean());
                Assert.IsTrue(approved.OutputJson.Value.GetProperty("safety").GetProperty("non_cursor_window_mouse_input").GetBoolean());
                Assert.IsTrue(approved.OutputJson.Value.GetProperty("operations").EnumerateArray().Any(item => item.GetString() == "window_mouse_click"));

                var cursor = await invoker.InvokeAsync(new LlmToolInvocationRequest
                {
                    ToolId = WindowsDesktopAutomationTool.ToolId,
                    Approved = true,
                    Input = JsonDocument.Parse("{\"operation\":\"get_cursor_position\"}").RootElement.Clone()
                });
                Assert.IsTrue(cursor.Success, cursor.Error);
                Assert.IsTrue(cursor.OutputJson!.Value.GetProperty("cursor").TryGetProperty("x", out _));
            }
            else
            {
                Assert.IsFalse(approved.Success);
                StringAssert.Contains(approved.Error, "Windows");
            }
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void HumanCursorDriver_GeneratesDeterministicLifeLikePath()
    {
        var from = new DesktopPoint(10, 20);
        var to = new DesktopPoint(320, 240);
        var options = new HumanCursorOptions(true, 450, 32, 2.0d, false, 123);

        var first = HumanCursorDriver.GeneratePath(from, to, options);
        var second = HumanCursorDriver.GeneratePath(from, to, options);

        Assert.AreEqual(from, first[0]);
        Assert.AreEqual(to, first[^1]);
        Assert.IsTrue(first.Count > 10);
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        Assert.IsTrue(first.Any(point => point.X != from.X && point.X != to.X && point.Y != from.Y && point.Y != to.Y));
    }

    [TestMethod]
    public void UpsertDefinition_PersistsProprietaryToolAndExportsOpenAiShape()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            var definition = registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Vendor Search",
                Description = "Searches a proprietary vendor index.",
                Visibility = LlmToolVisibility.Proprietary,
                SourceType = LlmToolSourceType.Http,
                Source = "https://example.test/search",
                Vendor = "ExampleVendor",
                LicenseNotes = "Commercial",
                Tags = [" search ", "vendor", "search"],
                InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}").RootElement.Clone()
            });

            var reloaded = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            var saved = reloaded.GetDefinition(definition.Id);

            Assert.IsNotNull(saved);
            Assert.AreEqual("vendor_search", saved.Id);
            Assert.AreEqual(LlmToolVisibility.Proprietary, saved.Visibility);
            Assert.AreEqual(LlmToolSourceType.Http, saved.SourceType);
            CollectionAssert.AreEqual(new[] { "search", "vendor" }, saved.Tags.ToArray());

            var openAiTools = reloaded.ExportOpenAiTools();
            string json = JsonSerializer.Serialize(openAiTools);
            StringAssert.Contains(json, "\"type\":\"function\"");
            StringAssert.Contains(json, "\"name\":\"vendor_search\"");
            StringAssert.Contains(json, "\"query\"");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SecretStore_DoesNotPersistPlaintextValue()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            registry.SetSecret("vendor_key", "super-secret-value");

            Assert.AreEqual("super-secret-value", registry.GetSecret("vendor_key"));
            string secretFile = Path.Combine(root, ".secrets", "tool-secrets.json");
            Assert.IsTrue(File.Exists(secretFile));
            string persisted = File.ReadAllText(secretFile);
            Assert.IsFalse(persisted.Contains("super-secret-value", StringComparison.Ordinal));
            if (OperatingSystem.IsWindows())
            {
                StringAssert.Contains(persisted, "DPAPI-CurrentUser");
                Assert.IsFalse(File.Exists(Path.Combine(root, ".secrets", "tool-secrets.key")));
            }

            Assert.IsTrue(registry.DeleteSecret("vendor_key"));
            Assert.IsNull(registry.GetSecret("vendor_key"));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ToolInvoke_RequiresApprovalBeforeHttpExecution()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Vendor Echo",
                Description = "Echoes tool input.",
                Visibility = LlmToolVisibility.Proprietary,
                SourceType = LlmToolSourceType.Http,
                Source = $"http://127.0.0.1:{port}/invoke",
                ApprovalMode = LlmToolApprovalMode.AskEveryTime,
                Permissions = LlmToolPermissions.NetworkAccess,
                InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
            });
            var invoker = new LlmToolInvoker(registry);

            var denied = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "vendor_echo",
                Input = JsonDocument.Parse("{\"value\":1}").RootElement.Clone()
            });

            Assert.IsFalse(denied.Success);
            StringAssert.Contains(denied.Error, "requires approval");

            Task server = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();
                byte[] response = Encoding.UTF8.GetBytes("{\"received\":" + body + "}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response);
                context.Response.Close();
            });

            var approved = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "vendor_echo",
                Approved = true,
                Input = JsonDocument.Parse("{\"value\":2}").RootElement.Clone()
            });
            await server;

            Assert.IsTrue(approved.Success, approved.Error);
            Assert.IsTrue(approved.OutputJson.HasValue, approved.OutputText);
            Assert.AreEqual(2, approved.OutputJson.Value.GetProperty("received").GetProperty("value").GetInt32());
            var audit = registry.ListAuditEntries();
            Assert.IsTrue(audit.Any(entry => entry.ToolId == "vendor_echo" && entry.Action == "invoke" && entry.Outcome == "success"), JsonSerializer.Serialize(audit));
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ToolEndpoints_SaveListExportAndInvoke()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int runtimePort = NextPort();
        try
        {
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = Path.Combine(root, "models"), ToolRoot = Path.Combine(root, "tools"), Port = runtimePort });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var saveResponse = await client.PostAsync(
                $"http://127.0.0.1:{runtimePort}/api/v1/tools",
                new StringContent("""
                {
                  "name": "Blocked Tool",
                  "description": "A proprietary tool that requires approval.",
                  "visibility": "proprietary",
                  "sourceType": "http",
                  "source": "http://127.0.0.1:9/unused",
                  "approvalMode": "askEveryTime",
                  "permissions": "networkAccess",
                  "inputSchema": { "type": "object" }
                }
                """, Encoding.UTF8, "application/json"));
            saveResponse.EnsureSuccessStatusCode();

            string listBody = await client.GetStringAsync($"http://127.0.0.1:{runtimePort}/api/v1/tools");
            StringAssert.Contains(listBody, "blocked_tool");

            string openAiBody = await client.GetStringAsync($"http://127.0.0.1:{runtimePort}/api/v1/tools/openai");
            StringAssert.Contains(openAiBody, "\"type\":\"function\"");

            var invokeResponse = await client.PostAsync(
                $"http://127.0.0.1:{runtimePort}/api/v1/tools/invoke",
                new StringContent("{\"tool_id\":\"blocked_tool\",\"input\":{}}", Encoding.UTF8, "application/json"));
            string invokeBody = await invokeResponse.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(invokeBody);

            Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
            StringAssert.Contains(document.RootElement.GetProperty("error").GetString() ?? "", "requires approval");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ToolCallLoop_ExecutesOpenAiStyleCallsAndExportsMcpShape()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Vendor Lookup",
                Description = "Looks up proprietary records.",
                Visibility = LlmToolVisibility.Proprietary,
                SourceType = LlmToolSourceType.Http,
                Source = $"http://127.0.0.1:{port}/lookup",
                ApprovalMode = LlmToolApprovalMode.AlwaysAllow,
                Permissions = LlmToolPermissions.NetworkAccess,
                InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}").RootElement.Clone()
            });

            var mcpTools = LlmMcpToolAdapter.ExportMcpTools(registry);
            string mcpJson = JsonSerializer.Serialize(mcpTools);
            StringAssert.Contains(mcpJson, "vendor_lookup");
            StringAssert.Contains(mcpJson, "inputSchema");

            Task server = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();
                byte[] response = Encoding.UTF8.GetBytes("{\"record\":" + body + "}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response);
                context.Response.Close();
            });

            var loop = new LlmToolCallLoop(registry, new LlmToolInvoker(registry));
            var results = await loop.ExecuteAsync([
                new LlmToolCall
                {
                    Id = "call_1",
                    Name = "vendor_lookup",
                    Arguments = JsonDocument.Parse("{\"id\":\"abc\"}").RootElement.Clone()
                }
            ], approved: false);
            await server;

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Success, results[0].Error);
            Assert.AreEqual("abc", results[0].OutputJson!.Value.GetProperty("record").GetProperty("id").GetString());
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ToolInvoke_DeniesHttpToolWithoutNetworkPermission()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Unsafe Http",
                Description = "Missing its required network permission.",
                SourceType = LlmToolSourceType.Http,
                Source = "http://127.0.0.1:9/unused",
                ApprovalMode = LlmToolApprovalMode.AlwaysAllow,
                Permissions = LlmToolPermissions.None,
                InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
            });

            var result = await new LlmToolInvoker(registry).InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "unsafe_http",
                Approved = true,
                Input = JsonDocument.Parse("{}").RootElement.Clone()
            });

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "permissions");
            Assert.AreEqual(LlmToolPermissions.NetworkAccess, result.RequiredPermissions);
            Assert.IsTrue(registry.ListAuditEntries("unsafe_http").Any(entry => entry.Outcome == "denied"));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ToolInvoke_ExpandsSecretsAndRedactsToolOutputAndAudit()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            registry.SetSecret("vendor_token", "secret-token-123");
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Secret Echo",
                Description = "Uses a proprietary token.",
                Visibility = LlmToolVisibility.Proprietary,
                SourceType = LlmToolSourceType.Http,
                Source = $"http://127.0.0.1:{port}/secret",
                ApprovalMode = LlmToolApprovalMode.AlwaysAllow,
                Permissions = LlmToolPermissions.NetworkAccess | LlmToolPermissions.SecretsAccess,
                RequiredSecrets =
                [
                    new LlmToolSecretReference { Name = "token", SecretId = "vendor_token" }
                ],
                HttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer {{secret:token}}"
                },
                InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
            });

            Task server = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                string auth = context.Request.Headers["Authorization"] ?? "";
                byte[] response = Encoding.UTF8.GetBytes("{\"auth\":\"" + auth + "\",\"leak\":\"secret-token-123\"}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response);
                context.Response.Close();
            });

            var result = await new LlmToolInvoker(registry).InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "secret_echo",
                Input = JsonDocument.Parse("{}").RootElement.Clone(),
                Approved = true
            });
            await server;

            Assert.IsTrue(result.Success, result.Error);
            Assert.IsFalse(result.OutputText.Contains("secret-token-123", StringComparison.Ordinal));
            StringAssert.Contains(result.OutputText, "[REDACTED]");
            Assert.AreEqual("prompt [REDACTED]", registry.RedactForTool("secret_echo", "prompt secret-token-123"));
            string audit = JsonSerializer.Serialize(registry.ListAuditEntries("secret_echo"));
            Assert.IsFalse(audit.Contains("secret-token-123", StringComparison.Ordinal));
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ToolInvoke_ExecutesNamedPipeDotNetAssemblyAndMcpAdapters()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        string pipeName = "llm_tool_test_" + Guid.NewGuid().ToString("N");
        using var listener = new HttpListener();
        try
        {
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = root });
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Pipe Echo",
                SourceType = LlmToolSourceType.NamedPipe,
                Source = pipeName,
                ApprovalMode = LlmToolApprovalMode.AlwaysAllow,
                Permissions = LlmToolPermissions.NetworkAccess,
                InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
            });
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Assembly Echo",
                SourceType = LlmToolSourceType.DotNetAssembly,
                Source = typeof(TestAssemblyTool).Assembly.Location + "::" + typeof(TestAssemblyTool).FullName + ".Echo",
                ApprovalMode = LlmToolApprovalMode.AlwaysAllow,
                Permissions = LlmToolPermissions.RepositoryAccess,
                InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
            });
            registry.UpsertDefinition(new LlmToolDefinition
            {
                Name = "Mcp Echo",
                SourceType = LlmToolSourceType.McpServer,
                Source = $"http://127.0.0.1:{port}/mcp",
                ApprovalMode = LlmToolApprovalMode.AlwaysAllow,
                Permissions = LlmToolPermissions.NetworkAccess,
                InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
            });

            Task pipeServer = Task.Run(async () =>
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string line = await reader.ReadLineAsync() ?? "{}";
                await writer.WriteAsync("{\"pipe\":" + line + "}");
            });

            var invoker = new LlmToolInvoker(registry);
            var pipeResult = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "pipe_echo",
                Input = JsonDocument.Parse("{\"value\":3}").RootElement.Clone()
            });
            await pipeServer;

            Assert.IsTrue(pipeResult.Success, pipeResult.Error);
            Assert.AreEqual(3, pipeResult.OutputJson!.Value.GetProperty("pipe").GetProperty("value").GetInt32());

            var assemblyResult = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "assembly_echo",
                Input = JsonDocument.Parse("{\"value\":4}").RootElement.Clone()
            });

            Assert.IsTrue(assemblyResult.Success, assemblyResult.Error);
            Assert.AreEqual(4, assemblyResult.OutputJson!.Value.GetProperty("assembly").GetProperty("value").GetInt32());

            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Task mcpServer = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();
                using var requestJson = JsonDocument.Parse(body);
                string method = requestJson.RootElement.GetProperty("method").GetString() ?? "";
                int value = requestJson.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("value").GetInt32();
                byte[] response = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"method\":\"" + method + "\",\"value\":" + value + "}}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response);
                context.Response.Close();
            });

            var mcpResult = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "mcp_echo",
                Input = JsonDocument.Parse("{\"value\":5}").RootElement.Clone()
            });
            await mcpServer;

            Assert.IsTrue(mcpResult.Success, mcpResult.Error);
            Assert.AreEqual("tools/call", mcpResult.OutputJson!.Value.GetProperty("result").GetProperty("method").GetString());
            Assert.AreEqual(5, mcpResult.OutputJson.Value.GetProperty("result").GetProperty("value").GetInt32());
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }
}

public static class TestAssemblyTool
{
    public static object Echo(JsonElement input) => new { assembly = input };
}
