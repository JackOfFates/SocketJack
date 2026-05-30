using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public class DirectMlGgufBackendTests
{
    [TestMethod]
    public void TryParseRunnerOutputLine_ExtractsSocketJackRunnerToken()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("""{"token":"hello "}""", out string text, out bool done);

        Assert.IsTrue(parsed);
        Assert.IsFalse(done);
        Assert.AreEqual("hello ", text);
    }

    [TestMethod]
    public void TryParseRunnerOutputLine_ExtractsOpenAiSseDelta()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("""data: {"choices":[{"delta":{"content":"world"}}]}""", out string text, out bool done);

        Assert.IsTrue(parsed);
        Assert.IsFalse(done);
        Assert.AreEqual("world", text);
    }

    [TestMethod]
    public void TryParseRunnerOutputLine_RecognizesDoneSentinel()
    {
        bool parsed = DirectMlGgufBackend.TryParseRunnerOutputLine("data: [DONE]", out string text, out bool done);

        Assert.IsTrue(parsed);
        Assert.IsTrue(done);
        Assert.AreEqual("", text);
    }

    [TestMethod]
    public async Task ReadRunnerTokenStreamAsync_YieldsTokensBeforeDone()
    {
        using var reader = new StringReader("""
{"token":"hello"}
{"token":" "}
data: {"choices":[{"delta":{"content":"world"}}]}
[DONE]
{"token":"ignored"}
""");
        var parts = new List<string>();

        await foreach (LlmChatToken token in DirectMlGgufBackend.ReadRunnerTokenStreamAsync(reader))
            parts.Add(token.Text);

        CollectionAssert.AreEqual(new[] { "hello", " ", "world" }, parts);
    }
}
