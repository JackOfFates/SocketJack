using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class WorkstationComboBoxTemplateTests
{
    [TestMethod]
    public void SharedComboBoxTemplate_RendersSelectedTextForEditableControls()
    {
        string root = FindRepositoryRoot();
        string appXaml = File.ReadAllText(Path.Combine(root, "heirowLLM", "App.xaml"));

        StringAssert.Contains(appXaml, "x:Name=\"PART_EditableTextBox\"");
        StringAssert.Contains(appXaml, "Property=\"IsEditable\" Value=\"True\"");
        StringAssert.Contains(appXaml, "TargetName=\"ContentSite\" Property=\"Visibility\" Value=\"Hidden\"");
        StringAssert.Contains(appXaml, "TargetName=\"PART_EditableTextBox\" Property=\"Visibility\" Value=\"Visible\"");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SocketJack.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("SocketJack.sln was not found above the test output directory.");
    }
}
