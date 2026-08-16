using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class WorkstationServiceEditorStabilityTests
{
    [TestMethod]
    public void LiveMetrics_DoNotRebuildFocusedServiceEditors()
    {
        string root = FindRepositoryRoot();
        string code = File.ReadAllText(Path.Combine(root, "JackLLM", "MainWindow.xaml.cs"));

        int updateStart = code.IndexOf("private void UpdateServiceDetailsPanel", StringComparison.Ordinal);
        int updateEnd = code.IndexOf("private IReadOnlyList<ServiceMetricItem>", updateStart, StringComparison.Ordinal);
        Assert.IsTrue(updateStart >= 0 && updateEnd > updateStart);
        string update = code.Substring(updateStart, updateEnd - updateStart);

        StringAssert.Contains(update, "_selectedServiceOptionsSignature");
        StringAssert.Contains(update, "NormalizeServiceName(service.Name)");
        StringAssert.Contains(update, "RenderServiceOptions(service)");
        StringAssert.Contains(code, "CreateServiceActionButton(\"Refresh services\", RefreshServicesPanelAndOptions)");
        StringAssert.Contains(code, "private void RefreshServicesPanelAndOptions()");
        StringAssert.Contains(code, "ServiceDetailsPanel?.IsKeyboardFocusWithin == true");
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
