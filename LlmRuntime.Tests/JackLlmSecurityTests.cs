using System.Net;
using System.Reflection;
using System.Text.Json;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class JackLlmSecurityTests
{
    [TestMethod]
    public void CompanionPolicyDetectsFinancialAndSensitiveActions()
    {
        Assert.IsTrue(SocketJack.Net.LmVsProxy.IsCompanionFinancialAction("open checkout and purchase the subscription"));
        Assert.IsTrue(SocketJack.Net.LmVsProxy.IsCompanionFinancialAction("Invoke-RestMethod https://api.stripe.com/v1/payment_intents"));
        Assert.IsTrue(SocketJack.Net.LmVsProxy.IsCompanionFinancialAction("transfer bitcoin to this wallet"));
        Assert.IsFalse(SocketJack.Net.LmVsProxy.IsCompanionFinancialAction("open Notepad and type a draft"));
        Assert.IsTrue(SocketJack.Net.LmVsProxy.IsCompanionSensitiveText("api_key=sk-example"));
        Assert.IsTrue(SocketJack.Net.LmVsProxy.IsCompanionSensitiveText("password: hunter2"));
        Assert.IsFalse(SocketJack.Net.LmVsProxy.IsCompanionSensitiveText("the user prefers dark mode"));
        StringAssert.Contains(SocketJack.Net.LmVsProxy.RedactCompanionSensitiveText("password=hunter2"), "[redacted]");
    }

    [TestMethod]
    public void CompanionObservationIsConvertedToFreshVisionInput()
    {
        MethodInfo? extract = typeof(LmVsProxy).GetMethod(
            "TryExtractCompanionImageDataUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(extract);

        const string jpegBase64 = "/9j/4AAQSkZJRg==";
        string result = JsonSerializer.Serialize(new
        {
            ok = true,
            type = "screen",
            contentType = "image/jpeg",
            data = jpegBase64
        });

        string dataUrl = (string)extract.Invoke(null, ["companion_action", result])!;

        Assert.AreEqual("data:image/jpeg;base64," + jpegBase64, dataUrl);
        Assert.AreEqual("", (string)extract.Invoke(null, ["internet_search", result])!);
    }

    [TestMethod]
    public void CompanionDesktopControlRaisesAndClearsSafetyStateAroundInput()
    {
        const string ownerKey = "webauth:companion-control-state-test";
        using var proxy = new LmVsProxy("127.0.0.1", 1234, 24434, 24436);
        ChatClientPermissionSnapshot permissions = proxy.GetChatClientPermissionsDiagnostics(ownerKey);
        permissions.CompanionEnabled = true;
        permissions.CompanionCursorControl = true;
        proxy.SaveChatClientPermissionsDiagnostics(permissions);
        var states = new List<bool>();
        int inputCalls = 0;
        proxy.CompanionControlStateChanged += states.Add;
        proxy.CompanionInput = _ => inputCalls++;
        MethodInfo? execute = typeof(LmVsProxy).GetMethod(
            "ExecuteCompanionToolAction",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(execute);

        string result = (string)execute.Invoke(proxy, [ownerKey, "{\"type\":\"move\",\"x\":10,\"y\":20}", false])!;

        Assert.AreEqual(1, inputCalls);
        CollectionAssert.AreEqual(new[] { true, false }, states);
        StringAssert.Contains(result, "\"ok\":true");
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1:11435/")]
    [DataRow("http://10.0.0.1/")]
    [DataRow("http://172.16.0.1/")]
    [DataRow("http://192.168.1.1/")]
    [DataRow("http://169.254.169.254/latest/meta-data/")]
    [DataRow("http://[::1]/")]
    [DataRow("http://[fd00::1]/")]
    public void BrowserDestinationRejectsPrivateAndSpecialPurposeAddresses(string value)
    {
        bool allowed = LmVsProxy.TryValidateBrowserDestination(new Uri(value), allowPrivateNetwork: false, out string reason);

        Assert.IsFalse(allowed);
        StringAssert.Contains(reason, "blocked");
    }

    [TestMethod]
    public void BrowserDestinationAllowsPublicLiteralAddress()
    {
        bool allowed = LmVsProxy.TryValidateBrowserDestination(new Uri("https://8.8.8.8/"), allowPrivateNetwork: false, out string reason);

        Assert.IsTrue(allowed, reason);
    }

    [TestMethod]
    public void BrowserDestinationRejectsEmbeddedCredentials()
    {
        bool allowed = LmVsProxy.TryValidateBrowserDestination(new Uri("https://user:password@8.8.8.8/"), allowPrivateNetwork: true, out string reason);

        Assert.IsFalse(allowed);
        StringAssert.Contains(reason, "credentials");
    }

    [TestMethod]
    public void BrowserDestinationPrivateNetworkOptInIsExplicit()
    {
        bool allowed = LmVsProxy.TryValidateBrowserDestination(new Uri("http://127.0.0.1:8080/"), allowPrivateNetwork: true, out string reason);

        Assert.IsTrue(allowed, reason);
    }

    [TestMethod]
    public void ChatServerBindsLoopbackWhenPublicAccessIsDisabled()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 1234, 21434, 21436)
        {
            PublicAccessEnabled = false
        };

        var server = (MutableTcpServer)proxy.ChatServer;

        Assert.AreEqual(IPAddress.Loopback, server.Options.BindAddress);
    }

    [TestMethod]
    public void ChatServerBindsAllInterfacesOnlyAfterPublicAccessOptIn()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 1234, 22434, 22436)
        {
            PublicAccessEnabled = true
        };

        var server = (MutableTcpServer)proxy.ChatServer;

        Assert.AreEqual(IPAddress.Any, server.Options.BindAddress);
    }

    [TestMethod]
    public void WebChatNativeVisionPayloadUsesRuntimeChatMessageContract()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 1234, 23434, 23436);
        MethodInfo? buildRequest = typeof(LmVsProxy).GetMethod(
            "BuildChatUiNativeChatRequestJson",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(buildRequest);

        const string imageDataUrl = "data:image/png;base64,iVBORw0KGgo=";
        string requestBody = $$"""
        {
          "model": "vision-model",
          "messages": [
          {
            "role": "user",
            "content": "What is in the previous image?"
          },
          {
            "role": "assistant",
            "content": "The previous image is Google Search in a Chrome browser."
          },
          {
            "role": "user",
            "content": [
              { "type": "text", "text": "Describe this image." },
              { "type": "image_url", "image_url": { "url": "{{imageDataUrl}}" } }
            ]
          }]
        }
        """;

        string nativeJson = (string)buildRequest.Invoke(proxy, [requestBody, null, null, null, false])!;
        using JsonDocument document = JsonDocument.Parse(nativeJson);
        JsonElement root = document.RootElement;
        Assert.IsTrue(root.TryGetProperty("messages", out JsonElement messages));
        Assert.IsFalse(root.TryGetProperty("input", out _));
        JsonElement user = messages.EnumerateArray().Last(message =>
            message.GetProperty("role").GetString() == "user");
        StringAssert.Contains(user.GetProperty("content").GetRawText(), "\"type\":\"image_url\"");
        StringAssert.Contains(user.GetProperty("content").GetRawText(), imageDataUrl);
        JsonElement system = messages.EnumerateArray().First(message =>
            message.GetProperty("role").GetString() == "system");
        StringAssert.Contains(system.GetProperty("content").GetString(), "current user message contains actual image pixels");
        StringAssert.Contains(system.GetProperty("content").GetString(), "Do not claim that images are unavailable");
        Assert.IsFalse(nativeJson.Contains("Google Search", StringComparison.OrdinalIgnoreCase),
            "A stale assistant image description must not be promoted into the system prompt for fresh pixels.");

        LlmChatRequest parsed = LlmChatRequest.FromJson(root);
        Assert.IsTrue(parsed.Messages.Last().HasImageContent);
        Assert.IsNotNull(parsed.Messages.Last().StructuredContent);
    }

    [TestMethod]
    public void VisibleWebChatBrowserNavigatesThroughTheSessionProxy()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "chatBrowserFrame.src = buildChatBrowserProxyUrl(url);");
        StringAssert.Contains(html, "chatBrowserFrame.src = buildChatBrowserProxyUrl(chatBrowserUrl);");
        StringAssert.Contains(html, "function buildChatBrowserProxyUrl(value)");
        StringAssert.Contains(html, "function serializeBrowserSkillLiveDocument(doc)");
        StringAssert.Contains(html, "snapshot.html = truncateBrowserSkillText(serializeBrowserSkillLiveDocument(doc), 60000);");
        StringAssert.Contains(html, "copy.setAttribute('value', '[redacted]');");
    }

    [TestMethod]
    public void WebChatStreamsAllToolCallsAndShowsCodexStyleChangedFiles()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "const visibleCalls = calls;");
        StringAssert.Contains(html, "row.open = status === 'failed' || status === 'started' || status === 'progress';");
        StringAssert.Contains(html, "updateAssistantFileChanges(streamState.parts, streamState.fileChanges);");
        StringAssert.Contains(html, "title.textContent = changedFilesHeading(fileEntries);");
        StringAssert.Contains(html, "createFileChangeActionButton('Review', 'Review the changed files')");
        StringAssert.Contains(html, "appendChangedFileStats(totals, totalAdditions, totalDeletions, true);");
        StringAssert.Contains(html, "animation: throbber-spin 850ms linear infinite;");
    }

    [TestMethod]
    public void WebChatVoiceModeStaysClosedUntilUserOpensIt()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "id=\"personaPlexPanel\" class=\"personaplex-panel\" aria-live=\"polite\" hidden");
        StringAssert.Contains(html, "setPersonaPlexPanelOpen(!personaPlexPanel || personaPlexPanel.hidden)");
        Assert.IsFalse(html.Contains("setPersonaPlexPanelOpen(true)", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("autoOpenPersonaPlex", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebChatDreamManagementPersistsPermissionsAndProtectsDirtySettings()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "dreamInternetSearch: !!(data.permissions && data.permissions.dreamInternetSearch)");
        StringAssert.Contains(html, "dreamPermInternetSearch','dreamPermVsCopilotTools");
        StringAssert.Contains(html, "if(dreamDirty)return");
        StringAssert.Contains(html, "Clear Resolved");
        StringAssert.Contains(html, "/api/dream-permissions");
        StringAssert.Contains(html, "dreamPresetValues");
        StringAssert.Contains(html, "class=\"memory-panel dream-panel\"");
        StringAssert.Contains(html, "class=\"dream-scroll-body\"");
        StringAssert.Contains(html, "class=\"dream-permission-grid\"");
        StringAssert.Contains(html, "renderDreamJournalStable");
        StringAssert.Contains(html, "refreshDreamStatus()");
        StringAssert.Contains(html, "dream-settings-scroll");
        StringAssert.Contains(html, "dream-journal-list");
        StringAssert.Contains(html, "Resource Thresholds");
        StringAssert.Contains(html, "class=\"dream-slider-field\"");
        StringAssert.Contains(html, "data-dream-step=\"-1\"");
        StringAssert.Contains(html, "syncDreamSliderOutputs");
        StringAssert.Contains(html, "Recommended for this PC");
        StringAssert.Contains(html, "dream-recommendation-marker");
        StringAssert.Contains(html, "dream-quality-perfect");
        StringAssert.Contains(html, "/api/dream-hardware-recommendation");
        StringAssert.Contains(html, "maybePromptDreamHardwareRecommendation");
        StringAssert.Contains(html, "id=\"dreamResourceToggle\"");
        StringAssert.Contains(html, "body.hidden=open");
        StringAssert.Contains(html, "dreamPermissionEdits.has(dream)");
        StringAssert.Contains(html, "dreamPermissionSaveChain=dreamPermissionSaveChain.then");
        StringAssert.Contains(html, "status.status==='completed-with-source-errors'||status.status==='alignment-retry'");
        StringAssert.Contains(html, "status.status==='model-failed'");
        StringAssert.Contains(html, "eligible '+Number(status.eligibleSessions||0)");
        StringAssert.Contains(html, "entry.status!=='alignment-retry'");
        StringAssert.Contains(html, "await loadAlignment().catch(() => { });");
    }

    [TestMethod]
    public void WebChatMemoryManagementGroupsTopicsAndManagesBlacklistRules()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "id=\"memoryTopic\"");
        StringAssert.Contains(html, "Memory Blacklist");
        StringAssert.Contains(html, "id=\"memoryPolicyInput\"");
        StringAssert.Contains(html, "action: 'add-blacklist'");
        StringAssert.Contains(html, "chatMemoryBlacklist");
        StringAssert.Contains(html, "memory-topic-group");
        StringAssert.Contains(html, "groups.get(topic)");
        StringAssert.Contains(html, "Needs context · excluded from LLM recall");
    }

    [TestMethod]
    public void WebChatShowsIconAttachmentsOnlyInAgentMode()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "aria-label=\"Agent attachments\"");
        StringAssert.Contains(html, "class=\"composer-attach-icon\"");
        StringAssert.Contains(html, "const agentMode = selectedService() === 'agent' && !sharedView;");
        StringAssert.Contains(html, "const showImages = agentMode && permissionState.imageUploads !== false;");
        StringAssert.Contains(html, "const showFiles = agentMode && permissionState.fileUploads !== false;");
        StringAssert.Contains(html, "body:not(.agent-mode) .composer-attach-stack");
        StringAssert.Contains(html, "body.agent-mode .composer.mobile-compact-composer > #composerAttachStack:not([hidden])");
    }

    [TestMethod]
    public void WebChatRecoversWorkstationAuthenticationWithoutRenderingChatErrors()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "async function recoverWorkstationAuthentication()");
        StringAssert.Contains(html, "function resetWebChatApiTransport()");
        StringAssert.Contains(html, "transport.close('Workstation authentication changed.')");
        StringAssert.Contains(html, "return { request, close };");
        StringAssert.Contains(html, "if (accessToken)\n            clearSocketJackUrlTokenFromUrl();");
        StringAssert.Contains(html, "function sessionUploadHeaders(headers)");
        StringAssert.Contains(html, "result.set('Authorization', 'Bearer ' + token);");
        StringAssert.Contains(html, "if (await recoverWorkstationAuthentication())");
        StringAssert.Contains(html, "return performRequest(retryResource);");
        StringAssert.Contains(html, "async function waitForWorkstationAuthentication(sessionId)");
        StringAssert.Contains(html, "if (isWorkstationAuthError(error))");
        StringAssert.Contains(html, "Your Workstation session expired. Sign in again to reconnect; your chat will resume automatically.");
        StringAssert.Contains(html, "isNoisySessionOwnershipSaveMessage(content) || isWorkstationAuthError(content)");
    }

    [TestMethod]
    public void WebChatLoginDoesNotStealFocusFromThePasswordField()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "const wasHidden = loginGate.hidden;");
        StringAssert.Contains(html, "if (visible && wasHidden && loginUsername)");
        StringAssert.Contains(html, "loginGate.contains(document.activeElement)");
        StringAssert.Contains(html, "const initialField = loginUsername.value && loginPassword ? loginPassword : loginUsername;");
        Assert.IsFalse(html.Contains("window.setTimeout(() => loginUsername.focus()", StringComparison.Ordinal));
    }
}
