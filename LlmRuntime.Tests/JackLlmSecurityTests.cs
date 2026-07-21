using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class JackLlmSecurityTests
{
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
    public void WebChatDreamManagementPersistsPermissionsAndProtectsDirtySettings()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "dreamInternetSearch: !!(data.permissions && data.permissions.dreamInternetSearch)");
        StringAssert.Contains(html, "dreamPermInternetSearch','dreamPermVsCopilotTools");
        StringAssert.Contains(html, "if(dreamDirty)return");
        StringAssert.Contains(html, "Clear Resolved");
        StringAssert.Contains(html, "/api/dream-permissions");
        StringAssert.Contains(html, "dreamPresetValues");
    }
}
