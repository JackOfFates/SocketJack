using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class InternetSearchQueryExtractionTests
{
    [DataTestMethod]
    [DataRow("search the internet for vincent christopher davis superior wisconsin 54880 age 30", "vincent christopher davis superior wisconsin 54880 age 30")]
    [DataRow("search the web for 3 links for SocketJack releases", "SocketJack releases")]
    [DataRow("find online JackLLM workstation download", "JackLLM workstation download")]
    [DataRow("find current links for SocketJack docs", "SocketJack docs")]
    public void ExtractExplicitInternetSearchQuery_RemovesCommandSurface(string prompt, string expected)
    {
        Assert.AreEqual(expected, ExtractSearchQuery(prompt));
    }

    private static string ExtractSearchQuery(string prompt)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("ExtractExplicitInternetSearchQuery", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "ExtractExplicitInternetSearchQuery was not found.");
        return (string)method!.Invoke(proxy, new object[] { prompt })!;
    }
}
