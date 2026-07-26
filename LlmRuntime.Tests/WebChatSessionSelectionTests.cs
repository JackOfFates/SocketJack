using Microsoft.VisualStudio.TestTools.UnitTesting;
using LmVs;
using SocketJack;
using System.Reflection;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class WebChatSessionSelectionTests
{
    [TestMethod]
    public void SessionSelectionRendersContentBeforeOptionalUiHydration()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");
        int loadSession = html.IndexOf("async function loadSession(id)", StringComparison.Ordinal);
        int messagesHydrated = html.IndexOf("data.session.messages", loadSession, StringComparison.Ordinal);
        int firstRender = html.IndexOf("renderConversation();", messagesHydrated, StringComparison.Ordinal);
        int modelHydration = html.IndexOf("if (data.session.model && modelSelect)", messagesHydrated, StringComparison.Ordinal);

        Assert.IsTrue(loadSession >= 0);
        Assert.IsTrue(messagesHydrated > loadSession);
        Assert.IsTrue(firstRender > messagesHydrated && firstRender < modelHydration,
            "Saved messages must render before optional model/service UI hydration can fail.");
        StringAssert.Contains(html, "const requestId = ++sessionLoadRequestId;");
        StringAssert.Contains(html, "if (requestId !== sessionLoadRequestId) return;");
    }

    [TestMethod]
    public void SessionFileExplorerRejectsResponsesForPreviouslySelectedSession()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");
        int explorerStart = html.IndexOf("async function loadSolutionExplorerInternal(requestContext)", StringComparison.Ordinal);
        int explorerEnd = html.IndexOf("function compactProjectWorkflowPath", explorerStart, StringComparison.Ordinal);
        string explorer = html.Substring(explorerStart, explorerEnd - explorerStart);

        StringAssert.Contains(explorer, "requestId !== solutionExplorerLoadRequestId || currentSessionId !== sessionId");
        StringAssert.Contains(explorer, "data.sessionId && data.sessionId !== sessionId");
        Assert.IsFalse(explorer.Contains("currentSessionId = data.sessionId", StringComparison.Ordinal),
            "A file-list response must never change the selected chat session.");
        StringAssert.Contains(html, "solutionExplorerPendingKey === loadKey");
    }

    [TestMethod]
    public void ChatPayloadWriteKeySurvivesLoginAndProxyRestart()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-chat-key-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            MethodInfo writeKey = typeof(LmVsProxy).GetMethod(
                "GetChatOwnerEncryptionSecretForWrite",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            string first;
            using (var proxy = new LmVsProxy("127.0.0.1", 1234, 28434, 28436, dataRoot))
            {
                proxy.RememberChatSessionEncryptionSecret("webauth:jack", "temporary-login-password");
                first = (string)writeKey.Invoke(proxy, new object[] { "webauth:jack" })!;
                Assert.AreNotEqual("temporary-login-password", first);
            }

            using (var proxy = new LmVsProxy("127.0.0.1", 1234, 28434, 28436, dataRoot))
            {
                string afterRestart = (string)writeKey.Invoke(proxy, new object[] { "webauth:jack" })!;
                Assert.AreEqual(first, afterRestart,
                    "Persisted chat payloads need a durable machine-bound key, not a process-only login secret.");
            }
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
