using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LmVsProxyAgentToolRoutingTests
{
    [TestMethod]
    public void ExactFinalAnswerInstructionDoesNotSuppressRequiredFileTools()
    {
        Assert.IsTrue(PromptLikelyNeedsProxyTools(
            "Create C:\\Users\\Vin\\project\\socketjack.md with a summary. When finished, final answer exactly DONE."));
    }

    [TestMethod]
    public void ExactReplyWithoutToolNeedStillBypassesTools()
    {
        Assert.IsFalse(PromptLikelyNeedsProxyTools("Return exactly TEXT_MODE_EXACT_OK and nothing else."));
    }

    [TestMethod]
    public void SummaryPromptToNamedMarkdownFileRequiresFileWrite()
    {
        Assert.IsTrue(PromptRequestsProxyFileWrite(
            "Summarize the SocketJack C# library to a file called socketjack.md."));
        Assert.IsTrue(PromptRequestsProxyFileWrite(
            "Summarize this project to a project_summary.md file."));
    }

    [TestMethod]
    public void SummaryPromptWithExplicitVsWriteFileRequiresFileWrite()
    {
        const string prompt = """
            Summarize the SocketJack C# library to a file called socketjack.md.

            Use the available Visual Studio/file tools. Inspect the C# library under C:\Users\Vin\Documents\GitHub\SocketJack\SocketJack with vs_list_files, vs_search_files, and vs_read_file as needed. Then create or overwrite this exact file with vs_write_file:
            C:\Users\Vin\Documents\GitHub\SocketJack\SocketJack\socketjack.md

            Do not claim success until vs_write_file completes. After vs_write_file succeeds, reply exactly SOCKETJACK_SUMMARY_DONE.
            """;

        Assert.IsTrue(PromptRequestsProxyFileWrite(prompt));
        Assert.IsTrue(PromptLikelyNeedsProxyTools(prompt));
        Assert.AreEqual(
            "C:\\Users\\Vin\\Documents\\GitHub\\SocketJack\\SocketJack\\socketjack.md",
            ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void AbsolutePathExactContentPromptRequiresFileWrite()
    {
        Assert.IsTrue(PromptRequestsProxyFileWrite(
            "Use available tools. Create C:\\Users\\Vin\\project\\probe.txt containing exactly this single line: Generated-by-test. After the tool succeeds, reply exactly TOOL_WRITE_OK."));
    }

    [TestMethod]
    public void AbsolutePathExactContentExtractionStopsAtInstructionText()
    {
        const string prompt = "Use available tools. Create C:\\Users\\Vin\\project\\probe.txt containing exactly this single line: Generated-by-test. After the tool succeeds, reply exactly TOOL_WRITE_OK.";

        Assert.AreEqual("C:\\Users\\Vin\\project\\probe.txt", ExtractLikelyRequestedFileTarget(prompt));
        Assert.IsTrue(TryExtractExactRequestedFileContent(prompt, out var content));
        Assert.AreEqual("Generated-by-test.", content);
    }

    [TestMethod]
    public void FileTargetExtractionIgnoresSlashFragmentBeforeWindowsPath()
    {
        const string prompt = "Use the available Visual Studio/file tools to create the file C:\\Users\\Vin\\Documents\\GitHub\\SocketJack\\SocketJack\\public_proxy_stream_probe.txt containing exactly this single line: Generated-by-test";

        Assert.AreEqual(
            "C:\\Users\\Vin\\Documents\\GitHub\\SocketJack\\SocketJack\\public_proxy_stream_probe.txt",
            ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void FileTargetExtractionPrefersNamedOutputOverSourceMention()
    {
        const string prompt = "Summarize socketjack.md to a file called project_summary.md.";

        Assert.AreEqual("project_summary.md", ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void ChatOnlySummaryDoesNotRequireFileWrite()
    {
        Assert.IsFalse(PromptRequestsProxyFileWrite("Summarize socketjack.md in chat only."));
    }

    private static bool PromptLikelyNeedsProxyTools(string prompt)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "PromptLikelyNeedsProxyTools",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "PromptLikelyNeedsProxyTools should remain available for routing tests.");
        return (bool)method!.Invoke(null, new object[] { prompt })!;
    }

    private static bool PromptRequestsProxyFileWrite(string prompt)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "PromptRequestsProxyFileWrite",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "PromptRequestsProxyFileWrite should remain available for routing tests.");
        return (bool)method!.Invoke(null, new object[] { prompt })!;
    }

    private static string ExtractLikelyRequestedFileTarget(string prompt)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "ExtractLikelyRequestedFileTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "ExtractLikelyRequestedFileTarget should remain available for routing tests.");
        return (string)method!.Invoke(null, new object[] { prompt })!;
    }

    private static bool TryExtractExactRequestedFileContent(string prompt, out string content)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "TryExtractExactRequestedFileContent",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "TryExtractExactRequestedFileContent should remain available for routing tests.");
        object[] args = { prompt, string.Empty };
        bool result = (bool)method!.Invoke(null, args)!;
        content = (string)args[1];
        return result;
    }
}
