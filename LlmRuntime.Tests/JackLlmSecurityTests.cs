using System.Net;
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
    public void VisibleWebChatBrowserNavigatesThroughTheSessionProxy()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "chatBrowserFrame.src = buildChatBrowserProxyUrl(url);");
        StringAssert.Contains(html, "function buildChatBrowserProxyUrl(value)");
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
