using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LmVsProxyGitHubRepoImportTests
{
    [DataTestMethod]
    [DataRow("openai/codex", "openai", "codex")]
    [DataRow("https://github.com/openai/codex", "openai", "codex")]
    [DataRow("https://github.com/openai/codex.git", "openai", "codex")]
    [DataRow("github.com/openai/codex", "openai", "codex")]
    public void NormalizesGitHubRepositoryInput(string input, string expectedOwner, string expectedRepo)
    {
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryNormalizeGitHubRepositoryInput", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("TryNormalizeGitHubRepositoryInput was not found.");
        object?[] args = [input, "", "", ""];

        bool ok = (bool)(method.Invoke(null, args) ?? false);

        Assert.IsTrue(ok, args[3]?.ToString());
        Assert.AreEqual(expectedOwner, args[1]);
        Assert.AreEqual(expectedRepo, args[2]);
    }

    [TestMethod]
    public void RejectsNonGitHubRepositoryUrl()
    {
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryNormalizeGitHubRepositoryInput", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("TryNormalizeGitHubRepositoryInput was not found.");
        object?[] args = ["https://example.com/openai/codex", "", "", ""];

        bool ok = (bool)(method.Invoke(null, args) ?? true);

        Assert.IsFalse(ok);
        StringAssert.Contains(args[3]?.ToString() ?? "", "github.com");
    }

    [TestMethod]
    public void StripsGitHubArchiveWrapperFolder()
    {
        MethodInfo method = typeof(LmVsProxy).GetMethod("StripGitHubArchiveWrapperPath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("StripGitHubArchiveWrapperPath was not found.");

        string stripped = (string)(method.Invoke(null, ["owner-repo-abc123/src/Program.cs", "owner-repo-abc123"]) ?? "");

        Assert.AreEqual("src/Program.cs", stripped);
    }

    [TestMethod]
    public void FormatsRepositoryTooLargeErrorForSessionStorage()
    {
        MethodInfo method = typeof(LmVsProxy).GetMethod("FormatGitHubRepositoryTooLargeMessage", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("FormatGitHubRepositoryTooLargeMessage was not found.");
        const long gb = 1024L * 1024L * 1024L;

        string message = (string)(method.Invoke(null, [5L * gb, gb, gb, false]) ?? "");

        Assert.AreEqual("Repository is too large (5GB), Session Storage (1.0/1.0GB)", message);
    }
}
