using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class UserResourceQuotaTests
{
    [TestMethod]
    public void Proxy_PersistsAndEnforcesPerUserStorageBandwidthAndTokenPeriods()
    {
        string root = FindRepositoryRoot();
        string proxy = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "heirowLLM.cs"));

        StringAssert.Contains(proxy, "UserResourceQuotasTableName");
        StringAssert.Contains(proxy, "SaveUserResourceQuotaDiagnostics");
        StringAssert.Contains(proxy, "RollUserResourceQuotaPeriods");
        StringAssert.Contains(proxy, "GetUtcWeekKey");
        StringAssert.Contains(proxy, "RecordUserResourceBandwidthUsage");
        StringAssert.Contains(proxy, "RecordUserResourceTokenUsageLocked");
        StringAssert.Contains(proxy, "CanConsumeUserResourceQuota");
        StringAssert.Contains(proxy, "Daily SocketJack Networking transfer cap reached");
        StringAssert.Contains(proxy, "Daily token cap reached");
        StringAssert.Contains(proxy, "Token percentage limit reached");
        StringAssert.Contains(proxy, "Math.Min(ownerStorageLimit, serverStorageLimit)");
        StringAssert.Contains(proxy, "DefaultStorageLimitBytes = 10737418240L");
        StringAssert.Contains(proxy, "referencedWorkspaceBytes = 0L");
    }

    [TestMethod]
    public void StorageMeter_ExcludesReferencedWorkspaceTreesAndExplainsProtectedCopies()
    {
        string root = FindRepositoryRoot();
        string proxy = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "heirowLLM.cs"));
        string versions = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "heirowLLM.VersionControl.cs"));
        string html = File.ReadAllText(Path.Combine(root, "SocketJack", "html", "heirowLLMWebChat.html"));

        Assert.IsFalse(proxy.Contains("AddChatWorkspaceStorageUsage", StringComparison.Ordinal));
        StringAssert.Contains(versions, "includeReferencedWorkspaceRoots: false");
        StringAssert.Contains(html, "referenced workspace files and folders use 0 B");
        StringAssert.Contains(html, "const hasFreshStorageMetrics = solutionExplorerStorage");
        StringAssert.Contains(html, "loadSolutionExplorer(true)");
    }

    [TestMethod]
    public void ServerManagement_ProvidesAdminQuotaEditorAndAccessibleRgbMeters()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));

        StringAssert.Contains(xaml, "SocketJack Resource Limits");
        StringAssert.Contains(xaml, "UserQuotaStorageMbTextBox");
        StringAssert.Contains(xaml, "UserQuotaBandwidthKbpsTextBox");
        StringAssert.Contains(xaml, "UserQuotaDailyTokensTextBox");
        StringAssert.Contains(xaml, "UserQuotaWeeklyTokensTextBox");
        StringAssert.Contains(xaml, "UserQuotaTokenPercentTextBox");
        StringAssert.Contains(xaml, "Reset to Inherited / Unlimited");
        StringAssert.Contains(code, "SetRgbQuotaMeter");
        StringAssert.Contains(code, "AutomationProperties.SetHelpText");
        StringAssert.Contains(code, "Existing usage counters are preserved");
        StringAssert.Contains(code, "network ↓");
        StringAssert.Contains(code, "DefaultHostStorageLimitMegabytes = 10240");
        StringAssert.Contains(xaml, "Value=\"10240\"");
    }

    [TestMethod]
    public void WorkstationTopBar_SeparatesWindowsStyleMenusFromCompactResourceChips()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));

        StringAssert.Contains(xaml, "Header=\"File\"");
        StringAssert.Contains(xaml, "Header=\"Edit\"");
        StringAssert.Contains(xaml, "Header=\"View\"");
        StringAssert.Contains(xaml, "Header=\"Tools\"");
        StringAssert.Contains(xaml, "Header=\"Help\"");
        StringAssert.Contains(xaml, "TopResourceMetricsPanel");
        StringAssert.Contains(xaml, "TopGpuResourceChip");
        StringAssert.Contains(xaml, "TopCpuResourceChip");
        StringAssert.Contains(xaml, "TopRamResourceChip");
        StringAssert.Contains(xaml, "TopNetworkResourceChip");
        StringAssert.Contains(code, "ActualWidth >= 1040");
        StringAssert.Contains(code, "UpdateTopResourceMetrics");
    }

    [TestMethod]
    public void StartupDiagnosis_NeverDownloadsOrStartsTheLegacyRemoteRepairUpdater()
    {
        string root = FindRepositoryRoot();
        string diagnosis = File.ReadAllText(Path.Combine(root, "heirowLLM", "RepairInstallationWindow.cs"));
        string project = File.ReadAllText(Path.Combine(root, "heirowLLM", "heirowLLM.csproj"));

        StringAssert.Contains(diagnosis, "Startup Diagnosis");
        StringAssert.Contains(diagnosis, "No network repair or remote update was attempted");
        Assert.IsFalse(diagnosis.Contains("socketjack.com", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnosis.Contains("heirowLLMUpdater.exe", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(project, "'$(BundleOfficialSecurityBroker)' != 'false'");
    }

    [TestMethod]
    public void LocalReleaseSecurityBroker_FollowsItsWorkstationAndReusesAHealthyExistingBroker()
    {
        string root = FindRepositoryRoot();
        string startup = File.ReadAllText(Path.Combine(root, "heirowLLM", "StartupLoadingWindow.xaml.cs"));
        string broker = File.ReadAllText(Path.Combine(root, "heirowLLM.SecurityBroker", "Program.cs"));

        StringAssert.Contains(startup, "--parent-pid");
        StringAssert.Contains(startup, "existingResponse.BrokerCompatibility");
        StringAssert.Contains(broker, "ReadIntegerArgument(args, \"--parent-pid\")");
        StringAssert.Contains(broker, "WaitForExitAsync(parentLifetime.Token)");
        StringAssert.Contains(broker, "parentLifetime.Cancel()");
        Assert.IsFalse(broker.Contains("ReleaseMutex()", StringComparison.Ordinal), "The async broker must not release a thread-owned mutex from a continuation thread.");
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
