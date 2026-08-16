using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class MobileNavigationDrawerTests
{
    [TestMethod]
    public void ChatHost_UsesOneTouchFriendlySlidingDrawerInsteadOfToolbarIcons()
    {
        string root = FindRepositoryRoot();
        string chat = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Pages", "ChatHostPage.cs"));
        string drawer = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Controls", "MobileNavigationDrawer.cs"));

        StringAssert.Contains(chat, "NavigationPage.SetHasNavigationBar(this, false)");
        StringAssert.Contains(chat, "AutomationId = \"OpenMobileMenu\"");
        StringAssert.Contains(chat, "Projects & Sessions");
        StringAssert.Contains(chat, "Edit Session Title");
        StringAssert.Contains(chat, "JackDirector");
        StringAssert.Contains(chat, "Agent Builder");
        StringAssert.Contains(chat, "Errors / Diagnosis");
        StringAssert.Contains(chat, "Content = new Grid { Children = { _contentRoot, _alignmentLockScreen, _mobileDrawer } }");
        StringAssert.Contains(chat, "ChecksAndBalancesRetryCount");
        string dream = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Pages", "DreamManagementPage.cs"));
        StringAssert.Contains(dream, "Eligible {status.EligibleSessions}");
        StringAssert.Contains(dream, "entry.Status is not (\"running\" or \"alignment-retry\")");
        Assert.IsFalse(chat.Contains("ToolbarItems.Add", StringComparison.Ordinal), "The chat page should not restore the crowded toolbar.");

        StringAssert.Contains(drawer, "Width * 0.88");
        StringAssert.Contains(drawer, "HeightRequest = 54");
        StringAssert.Contains(drawer, "SwipeDirection.Left");
        StringAssert.Contains(drawer, "MobileNavigationDrawer");
    }

    [TestMethod]
    public void MobileMenu_KeepsAdministrativeAndDiagnosticFeaturesPermissionScoped()
    {
        string root = FindRepositoryRoot();
        string chat = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Pages", "ChatHostPage.cs"));
        string diagnostics = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Pages", "MobileDiagnosticsPage.cs"));

        StringAssert.Contains(chat, "administrator && _sqlAdminAllowed");
        StringAssert.Contains(chat, "administrator && _pcAccessAllowed");
        StringAssert.Contains(chat, "GetMobileMenuPermissionsAsync");
        StringAssert.Contains(diagnostics, "_snapshot.IsAdministrator");
        StringAssert.Contains(diagnostics, "InboundBytesPerSecond");
        StringAssert.Contains(diagnostics, "RgbMeter");
    }

    [TestMethod]
    public void MobileChat_ExposesDraftSteeringQueuedFollowUpsAndStructuredSteps()
    {
        string root = FindRepositoryRoot();
        string chat = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Pages", "ChatHostPage.cs"));
        string coordinator = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Services", "MobileGenerationCoordinator.cs"));
        string client = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Services", "JackLlmClient.cs"));

        StringAssert.Contains(chat, "Text = \"\\u21aa\"");
        StringAssert.Contains(chat, "SendOrQueueAsync");
        StringAssert.Contains(chat, "QueueFollowUpAsync");
        StringAssert.Contains(chat, "StartQueuedFollowUpAsync");
        StringAssert.Contains(chat, "ItemsUpdatingScrollMode.KeepItemsInView");
        StringAssert.Contains(chat, "if (_stickToLatest");
        StringAssert.Contains(chat, "Continue the previous answer from exactly where it stopped");
        StringAssert.Contains(chat, "nameof(ChatMessage.NeedsContinuation)");
        StringAssert.Contains(chat, "### JackHammer steps");
        StringAssert.Contains(coordinator, "public bool CanSteer");
        StringAssert.Contains(coordinator, "_steeringEnabled");
        Assert.IsFalse(coordinator.Contains("_snapshot.IsGenerating && _snapshot.JackhammerEnabled", StringComparison.Ordinal), "Ordinary connected text streams must remain steerable.");
        StringAssert.Contains(client, "steeringId = \"steer_mobile_\"");
        StringAssert.Contains(client, "ParseJackhammerSteps");
    }

    [TestMethod]
    public void ServerList_UsesResponsiveSymbolActionsIncludingTailscale()
    {
        string root = FindRepositoryRoot();
        string serverList = File.ReadAllText(Path.Combine(root, "JackLLM.Android", "Pages", "ServerListPage.cs"));

        StringAssert.Contains(serverList, "var connectionActions = new Grid");
        StringAssert.Contains(serverList, "connectionActions.Add(_openTailscale, 3)");
        StringAssert.Contains(serverList, "Text = \"\\u21c4\"");
        StringAssert.Contains(serverList, "AutomationProperties.SetName(_openTailscale, \"Open Tailscale\")");
        StringAssert.Contains(serverList, "new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)");
        Assert.IsFalse(serverList.Contains("new HorizontalStackLayout { Spacing = 10, Children = { signIn, add, refresh, _openTailscale } }", StringComparison.Ordinal), "Tailscale must not live in an unbounded horizontal action row.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SocketJack.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("SocketJack.sln was not found above the test output directory.");
    }
}
