using System;
using System.IO;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LmVsProxyPersistenceTests
{
    [TestMethod]
    public void ChatPermissionsPersistWhenDataRootIsExplicit()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-proxy-persistence-" + Guid.NewGuid().ToString("N"));
        const string ownerKey = "webauth:linux-persistence-test";

        try
        {
            using (var first = CreateProxy(dataRoot))
            {
                ChatClientPermissionSnapshot snapshot = first.GetChatClientPermissionsDiagnostics(ownerKey);
                snapshot.InternetSearch = true;
                snapshot.FileDownloads = true;
                snapshot.FtpServer = true;
                snapshot.SqlAdmin = true;
                snapshot.TerminalCommands = false;
                first.SaveChatClientPermissionsDiagnostics(snapshot);
            }

            using (var second = CreateProxy(dataRoot))
            {
                ChatClientPermissionSnapshot reloaded = second.GetChatClientPermissionsDiagnostics(ownerKey);
                Assert.IsTrue(reloaded.InternetSearch);
                Assert.IsTrue(reloaded.FileDownloads);
                Assert.IsTrue(reloaded.FtpServer);
                Assert.IsTrue(reloaded.SqlAdmin);
                Assert.IsFalse(reloaded.TerminalCommands);
                Assert.IsTrue(File.Exists(Path.Combine(dataRoot, "SocketJack", "JackLLMChat", "SocketJackDatabase.json")));
            }
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static LmVsProxy CreateProxy(string dataRoot)
    {
        return new LmVsProxy("127.0.0.1", 11434, 11435, 0, dataRoot);
    }
}
