using Microsoft.VisualStudio.TestTools.UnitTesting;
using LmVs;
using SocketJack;
using SocketJack.Net;
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
    public void ImageUploadCompletesBeforeVisionPromptCanBeSent()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "image.uploading = false;");
        StringAssert.Contains(html, "onUploadComplete: uploadedFile =>");
        StringAssert.Contains(html, "typeof file.onUploadComplete === 'function'");
        StringAssert.Contains(html, "images.some(image => image && image.uploading)");
        StringAssert.Contains(html, "!image.url.startsWith('data:image/')");
        StringAssert.Contains(html, "image && image.sessionFile ? image.sessionFile : null");
        StringAssert.Contains(html, "uploadedFiles: requestUploadedFiles");
        StringAssert.Contains(html, "bottom: calc(100% + 8px);");
        StringAssert.Contains(html, "status.textContent = image.uploadError ? 'Upload failed' : (image.referenced ? 'Referenced' : 'Uploaded');");
        int mobileControlsStart = html.IndexOf("function mobileComposerControlElements()", StringComparison.Ordinal);
        int mobileControlsEnd = html.IndexOf("function syncMobileComposerControls()", mobileControlsStart, StringComparison.Ordinal);
        string mobileControls = html.Substring(mobileControlsStart, mobileControlsEnd - mobileControlsStart);
        Assert.IsFalse(mobileControls.Contains("previewTray", StringComparison.Ordinal),
            "The attachment preview tray must remain above the composer instead of moving into the hidden Settings panel.");
    }

    [TestMethod]
    public void ProjectFilesCanBeDraggedIntoChatAndImagesRenderThumbnails()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "const solutionExplorerDragMime = 'application/x-jackllm-project-file';");
        StringAssert.Contains(html, "row.draggable = true;");
        StringAssert.Contains(html, "referenceSolutionEntryInChat(projectFileEntry)");
        StringAssert.Contains(html, "getSolutionImageThumbnailData(entry)");
        StringAssert.Contains(html, "solution-image-thumbnail");
        StringAssert.Contains(html, "project-file-drop-target");
        StringAssert.Contains(html, "file && (file.sessionFile || file.savedFile)");
        StringAssert.Contains(html, "dataUrl: data.dataUrl");
        StringAssert.Contains(html, "if (path === '/api/chat-file-preview')");
    }

    [TestMethod]
    public void SessionTokenMeterSeparatesHistoryFromRuntimeContextWindow()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "function runtimeModelContextInfo(modelId)");
        StringAssert.Contains(html, "if (runtimeContext.known) return 0;");
        StringAssert.Contains(html, "instance.config.context_length || instance.config.contextLength");
        StringAssert.Contains(html, "if (contextBudget > 0) settings.context_length = contextBudget;");
        StringAssert.Contains(html, "estimated conversation tokens before runtime compression");
        StringAssert.Contains(html, "Current per-request context window:");
        StringAssert.Contains(html, "Older history is compressed before inference");
        Assert.IsFalse(html.Contains("prompt tokens loaded of", StringComparison.Ordinal),
            "A saved conversation estimate is not the number of prompt tokens loaded into one inference request.");
    }

    [TestMethod]
    public void SessionStartupUsesBoundedPagesInsteadOfLoadingEveryChat()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "const chatSessionPageSize = 60;");
        StringAssert.Contains(html, "&skip=0");
        StringAssert.Contains(html, "async function loadMoreSessions()");
        StringAssert.Contains(html, "Load older chats");
        Assert.IsFalse(html.Contains("/api/chat-sessions?take=all", StringComparison.Ordinal),
            "Loading every encrypted chat during startup can exhaust the Workstation or WebView memory.");
    }

    [TestMethod]
    public void ExplorerListSelectionUsesCheckboxesInsteadOfToggleSwitches()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "Pill switches are reserved for toggle settings, never list selection.");
        StringAssert.Contains(html, ":not(.session-select-checkbox):not(.solution-select-checkbox)");
        StringAssert.Contains(html, ".shell :is(.session-select-checkbox, .solution-select-checkbox)");
        StringAssert.Contains(html, "List selection uses square checkboxes that inherit the active UI theme.");
        StringAssert.Contains(html, "selection.className = 'solution-select-checkbox';");
        StringAssert.Contains(html, "checkbox.className = 'session-select-checkbox';");
    }

    [TestMethod]
    public void ProjectExplorerPollingDoesNotReplaceFocusedControls()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");
        int loadActiveSessions = html.IndexOf("async function loadActiveSessions()", StringComparison.Ordinal);
        int focusGuard = html.IndexOf("function sessionListContainsFocus()", loadActiveSessions, StringComparison.Ordinal);
        string activeSessionRefresh = html.Substring(loadActiveSessions, focusGuard - loadActiveSessions);

        StringAssert.Contains(activeSessionRefresh, "renderSessionListFromPolling();");
        Assert.IsFalse(activeSessionRefresh.Contains("renderSessionList();", StringComparison.Ordinal),
            "The 2.5-second active-session poll must not rebuild and detach focused Project Explorer controls.");
        StringAssert.Contains(html, "function sessionListContainsFocus()");
        StringAssert.Contains(html, "sessionList.contains(active)");
        StringAssert.Contains(html, "sessionListPollingRenderDeferred = true;");
        StringAssert.Contains(html, "sessionList.addEventListener('focusout'");
        StringAssert.Contains(html, "if (!sessionListPollingRenderDeferred || sessionListContainsFocus()) return;");
        StringAssert.Contains(html, "const focusSnapshot = captureSessionListFocus();");
        StringAssert.Contains(html, "restoreSessionListFocus(focusSnapshot);");
    }

    [TestMethod]
    public void ProjectToolbarUsesColoredAccessibleSymbols()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "id=\"refreshSessions\" class=\"ghost panel-refresh project-toolbar-refresh\"");
        StringAssert.Contains(html, "aria-label=\"Refresh projects\">&#x21BB;</button>");
        StringAssert.Contains(html, "id=\"showArchivedProjects\" class=\"ghost project-toolbar-archive\"");
        StringAssert.Contains(html, "aria-label=\"Show archived projects\">&minus;</button>");
        StringAssert.Contains(html, "id=\"newProject\" class=\"ghost project-toolbar-new\"");
        StringAssert.Contains(html, "aria-label=\"New project\">+</button>");
        StringAssert.Contains(html, ".project-toolbar-new");
        StringAssert.Contains(html, ".project-toolbar-archive");
        StringAssert.Contains(html, ".project-toolbar-refresh");
    }

    [TestMethod]
    public void ChangedFilesOfferOpenAndLocalRevealActions()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "createFileChangeActionButton('Open', 'Open ' + file.name + ' in the Web Chat file panel')");
        StringAssert.Contains(html, "createFileChangeActionButton('Show on disk', 'Show ' + file.name + ' in File Explorer')");
        StringAssert.Contains(html, "async function revealSolutionFile(entry)");
        StringAssert.Contains(html, "fetch('/api/chat-file-reveal'");
        StringAssert.Contains(html, "className = 'assistant-file-additions'");
        StringAssert.Contains(html, "className = 'assistant-file-deletions'");
    }

    [TestMethod]
    public void LongAgentRunsShowPerPromptTokensAndSafeActivity()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "tokens this prompt</span>");
        StringAssert.Contains(html, "parts.promptTokensUsed");
        StringAssert.Contains(html, "const activityLine = 'Activity: ' + activityStatus;");
        StringAssert.Contains(html, "streamState.activityReasoning");
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
