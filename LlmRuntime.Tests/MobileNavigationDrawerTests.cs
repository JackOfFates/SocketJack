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
