using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Net.Services;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class AgentToolSmokeTests
{
    [TestMethod]
    public async Task TerminalServiceExecutesAndCapturesOutput()
    {
        string root = CreateTemporaryDirectory("terminal");
        try
        {
            var service = new TerminalService();
            TerminalCommandResult result = await service.ExecuteAsync(new TerminalCommandRequest
            {
                Command = "Write-Output 'JACK_TERMINAL_OK'",
                Shell = "powershell",
                WorkingDirectory = root,
                AllowedWorkingDirectories = [root],
                TimeoutMs = 15000
            });

            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.IsFalse(result.TimedOut);
            Assert.IsFalse(result.Canceled);
            StringAssert.Contains(result.Output, "JACK_TERMINAL_OK");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void VsFileToolsCompleteAReadWriteEditCopyRenameSearchDeleteRoundTrip()
    {
        string root = CreateTemporaryDirectory("files");
        string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        string dataRoot = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        const string owner = "agent-tool-smoke-owner";
        const string session = "agent-tool-smoke-session";
        string source = Path.Combine(workspace, "source.txt");
        string copy = Path.Combine(workspace, "copy.txt");
        string moved = Path.Combine(workspace, "moved.txt");

        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 1234, 28434, 28436, dataRoot);
            proxy.SaveChatWorkspaceRootDiagnostics(owner, session, "", "attached", "workspace", workspace, "read-write");
            MethodInfo execute = typeof(LmVsProxy).GetMethod("ExecuteProxyVsTool", BindingFlags.Instance | BindingFlags.NonPublic)!;

            string Run(string tool, object arguments) => (string)execute.Invoke(proxy,
                [tool, JsonSerializer.Serialize(arguments), owner, session])!;

            StringAssert.Contains(Run("vs_write_file", new { path = source, content = "alpha", overwrite = false }), "wrote 5 chars");
            StringAssert.Contains(Run("vs_read_file", new { path = source, startLine = 1, endLine = 5 }), "alpha");
            StringAssert.Contains(Run("vs_replace_in_file", new { path = source, oldString = "alpha", newString = "beta" }), "1 replacement(s)");
            StringAssert.Contains(Run("vs_copy_file", new { path = source, newPath = copy }), "copied");
            StringAssert.Contains(Run("vs_rename_file", new { path = copy, newPath = moved }), "moved");
            StringAssert.Contains(Run("vs_list_files", new { path = workspace, recursive = false, take = 20 }), "source.txt");
            StringAssert.Contains(Run("vs_search_files", new { path = workspace, query = "beta", take = 20 }), "source.txt");
            StringAssert.Contains(Run("vs_delete_file", new { path = moved }), "deleted");
            StringAssert.Contains(Run("vs_delete_file", new { path = source }), "deleted");

            Assert.IsFalse(File.Exists(source));
            Assert.IsFalse(File.Exists(copy));
            Assert.IsFalse(File.Exists(moved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void VsFileToolsWriteEditAndDeleteInsideTheSessionSandbox()
    {
        string dataRoot = CreateTemporaryDirectory("session-sandbox");
        const string owner = "agent-sandbox-smoke-owner";
        const string session = "agent-sandbox-smoke-session";
        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 1234, 28444, 28446, dataRoot);
            MethodInfo execute = typeof(LmVsProxy).GetMethod("ExecuteProxyVsTool", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string Run(string tool, object arguments) => (string)execute.Invoke(proxy,
                [tool, JsonSerializer.Serialize(arguments), owner, session])!;

            StringAssert.Contains(Run("vs_write_file", new { path = "sandbox-probe.txt", content = "alpha", overwrite = false }), "wrote 5 chars");
            StringAssert.Contains(Run("vs_replace_in_file", new { path = "sandbox-probe.txt", oldString = "alpha", newString = "beta" }), "1 replacement(s)");
            StringAssert.Contains(Run("vs_read_file", new { path = "sandbox-probe.txt", startLine = 1, endLine = 5 }), "beta");
            StringAssert.Contains(Run("vs_delete_file", new { path = "sandbox-probe.txt" }), "deleted");
            StringAssert.Contains(Run("vs_read_file", new { path = "sandbox-probe.txt", startLine = 1, endLine = 5 }), "could not be found");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void SandboxedFileChangesProduceAuthoritativeLineStatsAndRevealMetadata()
    {
        string dataRoot = CreateTemporaryDirectory("sandbox-change-event");
        const string owner = "agent-sandbox-change-owner";
        const string session = "agent-sandbox-change-session";
        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 1234, 28454, 28456, dataRoot);
            MethodInfo execute = typeof(LmVsProxy).GetMethod("ExecuteProxyVsTool", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo begin = typeof(LmVsProxy).GetMethod("BeginChatFileUndoScope", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo build = typeof(LmVsProxy).GetMethod("BuildChatFileChangeStreamEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;

            object scope = begin.Invoke(proxy, [owner, session, "vs_write_file"])!;
            string result;
            try
            {
                result = (string)execute.Invoke(proxy,
                    ["vs_write_file", JsonSerializer.Serialize(new { path = "event-probe.txt", content = "alpha\nbeta\n", overwrite = false }), owner, session])!;
            }
            finally
            {
                ((IDisposable)scope).Dispose();
            }

            StringAssert.Contains(result, "wrote");
            object transaction = scope.GetType().GetProperty("Transaction")!.GetValue(scope)!;
            var entries = ((System.Collections.IEnumerable)build.Invoke(proxy, [transaction])!).Cast<object>().ToArray();
            Assert.AreEqual(1, entries.Length);
            object entry = entries[0];
            Assert.AreEqual("session", entry.GetType().GetProperty("Kind")!.GetValue(entry));
            Assert.AreEqual(false, entry.GetType().GetProperty("IsLocal")!.GetValue(entry));
            Assert.AreEqual(false, entry.GetType().GetProperty("CanReveal")!.GetValue(entry));
            Assert.AreEqual(2, entry.GetType().GetProperty("Additions")!.GetValue(entry));
            Assert.AreEqual(0, entry.GetType().GetProperty("Deletions")!.GetValue(entry));

            _ = execute.Invoke(proxy, ["vs_delete_file", JsonSerializer.Serialize(new { path = "event-probe.txt" }), owner, session]);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task BrowserToolsReadHtmlTextLinksAndControlsFromTheTrackedPage()
    {
        string root = CreateTemporaryDirectory("browser");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = $"http://127.0.0.1:{port}/tool-smoke";
        Task server = ServeSingleHtmlResponseAsync(listener,
            "<!doctype html><html><head><title>Tool Smoke</title></head><body><main data-live='yes'>JACK_BROWSER_HTML_OK</main><a href='/next'>Next page</a><input id='probe' value='ready'></body></html>");

        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 1234, 28434, 28436, root);
            MethodInfo execute = typeof(LmVsProxy).GetMethod("ExecuteBrowserSkillToolAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string open = await (Task<string>)execute.Invoke(proxy,
                ["browser_open", JsonSerializer.Serialize(new { url }), "browser-smoke-owner", "browser-smoke-session"])!;
            await server;
            string read = await (Task<string>)execute.Invoke(proxy,
                ["browser_read_page", "{}", "browser-smoke-owner", "browser-smoke-session"])!;

            StringAssert.Contains(open, "JACK_BROWSER_HTML_OK");
            StringAssert.Contains(open, "Tool Smoke");
            StringAssert.Contains(open, "Next page");
            StringAssert.Contains(open, "input type=input");
            StringAssert.Contains(open, "data-live='yes'");
            StringAssert.Contains(read, "JACK_BROWSER_HTML_OK");
            StringAssert.Contains(read, url);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ChangedFileLineStatsCountAdditionsAndDeletions()
    {
        MethodInfo calculate = typeof(LmVsProxy).GetMethod(
            "CalculateChatFileLineChanges",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] before = Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n");
        byte[] after = Encoding.UTF8.GetBytes("alpha\nBETA\ngamma\ndelta\n");

        int[] result = (int[])calculate.Invoke(null, [before, after, true, true])!;

        Assert.AreEqual(2, result[0], "One replacement and one appended line should count as two additions.");
        Assert.AreEqual(1, result[1], "The replaced line should count as one deletion.");
        Assert.AreEqual(1, result[2], "UTF-8 text should report available line statistics.");
    }

    private static string CreateTemporaryDirectory(string suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), "jackllm-agent-tool-smoke-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task ServeSingleHtmlResponseAsync(TcpListener listener, string html)
    {
        using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        byte[] requestBuffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(requestBuffer);
            if (read <= 0) break;
            request.Append(Encoding.ASCII.GetString(requestBuffer, 0, read));
        }

        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
