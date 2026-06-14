using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public class DirectMlGgufBackendTests
{
    [TestMethod]
    public void TryParseRunnerOutputLine_ExtractsSocketJackRunnerToken()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("""{"token":"hello "}""", out string text, out bool done, out string finishReason);

        Assert.IsTrue(parsed);
        Assert.IsFalse(done);
        Assert.AreEqual("hello ", text);
        Assert.AreEqual("", finishReason);
    }

    [TestMethod]
    public void TryParseRunnerOutputLine_ExtractsOpenAiSseDelta()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("""data: {"choices":[{"delta":{"content":"world"}}]}""", out string text, out bool done, out string finishReason);

        Assert.IsTrue(parsed);
        Assert.IsFalse(done);
        Assert.AreEqual("world", text);
        Assert.AreEqual("", finishReason);
    }

    [TestMethod]
    public void TryParseRunnerOutputLine_ExtractsRootFinishReason()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("""{"content":"","finish_reason":"length"}""", out string text, out bool done, out string finishReason);

        Assert.IsTrue(parsed);
        Assert.IsFalse(done);
        Assert.AreEqual("", text);
        Assert.AreEqual("length", finishReason);
    }

    [TestMethod]
    public void TryParseRunnerOutputLine_ExtractsOpenAiChoiceFinishReason()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("""data: {"choices":[{"delta":{},"finish_reason":"length"}]}""", out string text, out bool done, out string finishReason);

        Assert.IsTrue(parsed);
        Assert.IsFalse(done);
        Assert.AreEqual("", text);
        Assert.AreEqual("length", finishReason);
    }

    [TestMethod]
    public void TryParseRunnerOutputLine_RecognizesDoneSentinel()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("data: [DONE]", out string text, out bool done, out string finishReason);

        Assert.IsTrue(parsed);
        Assert.IsTrue(done);
        Assert.AreEqual("", text);
        Assert.AreEqual("", finishReason);
    }

    [TestMethod]
    public async Task ReadRunnerTokenStreamAsync_YieldsTokensAndFinishReasonBeforeDone()
    {
        using var reader = new StringReader("""
{"token":"hello"}
{"token":" "}
data: {"choices":[{"delta":{"content":"world"}}]}
{"content":"","finish_reason":"length"}
[DONE]
{"token":"ignored"}
""");
        var parts = new List<string>();
        var finishReasons = new List<string>();

        await foreach (LlmChatToken token in DirectMlGgufBackend.ReadRunnerTokenStreamAsync(reader))
        {
            if (!string.IsNullOrEmpty(token.Text))
                parts.Add(token.Text);
            if (!string.IsNullOrWhiteSpace(token.FinishReason))
                finishReasons.Add(token.FinishReason);
        }

        CollectionAssert.AreEqual(new[] { "hello", " ", "world" }, parts);
        CollectionAssert.AreEqual(new[] { "length" }, finishReasons);
    }

    [TestMethod]
    public async Task ReadRunnerTokenStreamAsync_HidesReasoningTagsFromVisibleContent()
    {
        using var reader = new StringReader("""
{"token":"<think>"}
{"token":"hidden reasoning"}
{"token":"</think>"}
{"token":"visible answer"}
[DONE]
""");
        var parts = new List<string>();

        await foreach (LlmChatToken token in DirectMlGgufBackend.ReadRunnerTokenStreamAsync(reader))
        {
            if (!string.IsNullOrEmpty(token.Text))
                parts.Add(token.Text);
        }

        CollectionAssert.AreEqual(new[] { "visible answer" }, parts);
    }

    [TestMethod]
    public async Task ReadRunnerTokenStreamAsync_DropsUnclosedReasoningWhenOutputLimitStopsInsideThinkBlock()
    {
        using var reader = new StringReader("""
{"token":"<think>"}
{"token":"still reasoning"}
{"content":"","finish_reason":"length"}
[DONE]
""");
        var parts = new List<string>();
        var finishReasons = new List<string>();

        await foreach (LlmChatToken token in DirectMlGgufBackend.ReadRunnerTokenStreamAsync(reader))
        {
            if (!string.IsNullOrEmpty(token.Text))
                parts.Add(token.Text);
            if (!string.IsNullOrWhiteSpace(token.FinishReason))
                finishReasons.Add(token.FinishReason);
        }

        Assert.AreEqual(0, parts.Count);
        CollectionAssert.AreEqual(new[] { "length" }, finishReasons);
    }
}
