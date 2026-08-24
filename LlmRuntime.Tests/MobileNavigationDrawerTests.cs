using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class MobileNavigationDrawerTests
{
    [TestMethod]
    public void ChatHost_UsesOneTouchFriendlySlidingDrawerInsteadOfToolbarIcons()
    {
        string root = FindRepositoryRoot();
        string chat = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "ChatHostPage.cs"));
        string drawer = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Controls", "MobileNavigationDrawer.cs"));

        StringAssert.Contains(chat, "NavigationPage.SetHasNavigationBar(this, false)");
        StringAssert.Contains(chat, "AutomationId = \"OpenMobileMenu\"");
        StringAssert.Contains(chat, "Projects & Sessions");
        StringAssert.Contains(chat, "Edit Session Title");
        StringAssert.Contains(chat, "JackDirector");
        StringAssert.Contains(chat, "Agent Builder");
        StringAssert.Contains(chat, "Errors / Diagnosis");
        StringAssert.Contains(chat, "Content = new Grid { Children = { _contentRoot, _alignmentLockScreen, _mobileDrawer } }");
        Assert.IsFalse(chat.Contains("Children = { _contentRoot, _alignmentLockScreen, _chickenChaserPanel", StringComparison.Ordinal), "Chicken Chaser chat must not be rendered inside the mobile app.");
        StringAssert.Contains(chat, "ChecksAndBalancesRetryCount");
        string dream = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "DreamManagementPage.cs"));
        StringAssert.Contains(dream, "Eligible {status.EligibleSessions}");
        StringAssert.Contains(dream, "entry.Status is not (\"running\" or \"alignment-retry\")");
        Assert.IsFalse(chat.Contains("ToolbarItems.Add", StringComparison.Ordinal), "The chat page should not restore the crowded toolbar.");

        StringAssert.Contains(drawer, "Width * 0.88");
        StringAssert.Contains(drawer, "HeightRequest = 54");
        StringAssert.Contains(drawer, "SwipeDirection.Left");
        StringAssert.Contains(drawer, "AutomationId = \"MobileMenuBack\"");
        StringAssert.Contains(drawer, "SwipeDirection.Right");
        StringAssert.Contains(drawer, "AnimateSectionAsync(forward: true)");
        StringAssert.Contains(chat, "AddSection(\"File\")");
        StringAssert.Contains(chat, "AddSection(\"Edit\")");
        StringAssert.Contains(chat, "_mobileDrawer.NavigateBackAsync()");
        Assert.IsTrue(chat.IndexOf("AddSection(\"File\")", StringComparison.Ordinal) < chat.IndexOf("AddSection(\"Edit\")", StringComparison.Ordinal), "File must be the first mobile menu category.");
        StringAssert.Contains(drawer, "MobileNavigationDrawer");
    }

    [TestMethod]
    public void MobileMenu_KeepsAdministrativeAndDiagnosticFeaturesPermissionScoped()
    {
        string root = FindRepositoryRoot();
        string chat = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "ChatHostPage.cs"));
        string diagnostics = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "MobileDiagnosticsPage.cs"));

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
        string chat = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "ChatHostPage.cs"));
        string coordinator = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Services", "MobileGenerationCoordinator.cs"));
        string client = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Services", "HeirowLlmClient.cs"));

        StringAssert.Contains(chat, "Text = \"\\u21aa\"");
        StringAssert.Contains(chat, "SendOrQueueAsync");
        StringAssert.Contains(chat, "QueueFollowUpAsync");
        StringAssert.Contains(chat, "StartQueuedFollowUpAsync");
        StringAssert.Contains(chat, "ItemsUpdatingScrollMode.KeepItemsInView");
        StringAssert.Contains(chat, "if (_stickToLatest");
        StringAssert.Contains(chat, "Continue the previous answer from exactly where it stopped");
        StringAssert.Contains(chat, "nameof(ChatMessage.NeedsContinuation)");
        StringAssert.Contains(chat, "Text = \"HEIROWFORGE\"");
        StringAssert.Contains(chat, "nameof(ChatMessage.JackhammerSteps)");
        StringAssert.Contains(chat, "nameof(JackhammerPlanStep.StepLabel)");
        StringAssert.Contains(chat, "$\"Step {activeIndex + 1}/{planned.Length}");
        Assert.IsFalse(chat.Contains("### heirowForge steps", StringComparison.Ordinal), "heirowForge progress should render as native step cards, not raw Markdown.");
        StringAssert.Contains(coordinator, "public bool CanSteer");
        StringAssert.Contains(coordinator, "_steeringEnabled");
        Assert.IsFalse(coordinator.Contains("_snapshot.IsGenerating && _snapshot.JackhammerEnabled", StringComparison.Ordinal), "Ordinary connected text streams must remain steerable.");
        StringAssert.Contains(client, "steeringId = \"steer_mobile_\"");
        StringAssert.Contains(client, "ParseJackhammerCheckpoint");
        StringAssert.Contains(client, "value.Split('|', 3");
    }

    [TestMethod]
    public void MobileChat_ProvidesExternalChickenChaserOverlayWithSettingsAndReadableChoices()
    {
        string root = FindRepositoryRoot();
        string chat = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "ChatHostPage.cs"));
        string client = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Services", "HeirowLlmClient.cs"));
        string overlay = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Platforms", "Android", "AndroidChickenChaserOverlayService.cs"));
        string manifest = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Platforms", "Android", "AndroidManifest.xml"));

        StringAssert.Contains(chat, "Open permission settings");
        StringAssert.Contains(chat, "Content = new Grid { Children = { _contentRoot, _alignmentLockScreen, _mobileDrawer } }");
        StringAssert.Contains(client, "\"/api/chickenchaser/chat\"");
        StringAssert.Contains(client, "GetChickenChaserSettingsAsync");
        StringAssert.Contains(client, "SaveChickenChaserSettingsAsync");
        StringAssert.Contains(overlay, "Settings.CanDrawOverlays");
        StringAssert.Contains(overlay, "Settings.ActionManageOverlayPermission");
        StringAssert.Contains(overlay, "WindowManagerTypes.ApplicationOverlay");
        StringAssert.Contains(overlay, "chicken_chaser_sprites.png");
        StringAssert.Contains(overlay, "SetNotificationDot");
        StringAssert.Contains(overlay, "Chicken Chaser floating chat window");
        StringAssert.Contains(overlay, "AskChickenChaserAsync(prompt, \"\", \"Floating Chicken Chaser\"");
        StringAssert.Contains(overlay, "Chicken Chaser settings");
        StringAssert.Contains(overlay, "Resource.Layout.chicken_spinner_item");
        StringAssert.Contains(overlay, "Resource.Layout.chicken_spinner_dropdown_item");
        StringAssert.Contains(manifest, "android.permission.SYSTEM_ALERT_WINDOW");
        StringAssert.Contains(manifest, "android:foregroundServiceType=\"specialUse\"");

        string chickenWindow = File.ReadAllText(Path.Combine(root, "heirowLLM", "ChickenChaserWindow.cs"));
        string workstation = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));
        StringAssert.Contains(chickenWindow, "_installedChatModels() ?? Array.Empty<string>()");
        StringAssert.Contains(chickenWindow, "_activeNotifications.TryGetValue(signature");
        StringAssert.Contains(chickenWindow, "active.IncrementRepetitions()");
            StringAssert.Contains(chickenWindow, "Math.Cos(Math.PI * progress)");
            StringAssert.Contains(chickenWindow, "bool canUseLeft = Left - workArea.Left >= widest + 12");
            StringAssert.Contains(chickenWindow, "double above = Top - 12");
            StringAssert.Contains(chickenWindow, "Rect workArea = GetCurrentMonitorWorkArea()");
            StringAssert.Contains(chickenWindow, "top - totalHeight < workArea.Top + 8");
            StringAssert.Contains(chickenWindow, "Math.Clamp(1800 + message.Length * 14, 2600, 6000)");
            StringAssert.Contains(workstation, "ModelsManagerControl?.GetAvailableChatModelIds()");
    }

    [TestMethod]
    public void ServerList_UsesResponsiveSymbolActionsIncludingTailscale()
    {
        string root = FindRepositoryRoot();
        string serverList = File.ReadAllText(Path.Combine(root, "heirowLLM.Android", "Pages", "ServerListPage.cs"));

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
