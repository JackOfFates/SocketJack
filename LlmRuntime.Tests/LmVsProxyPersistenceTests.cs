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

    [TestMethod]
    public void LocalhostChatOwnerGetsAdminPermissionsByDefault()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-proxy-local-permissions-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var proxy = CreateProxy(dataRoot);
            ChatClientPermissionSnapshot disabled = proxy.GetChatClientPermissionsDiagnostics("ip:127.0.0.1");
            disabled.InternetSearch = false;
            disabled.FileDownloads = false;
            disabled.FtpServer = false;
            disabled.SqlAdmin = false;
            disabled.TerminalCommands = false;
            disabled.TerminalForeverApproved = false;
            disabled.AgentAccess = false;
            disabled.FileUploads = false;
            disabled.ImageUploads = false;
            disabled.MuteUntilEnabled = true;
            disabled.BanUntilEnabled = true;
            disabled.MutedUntilUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            disabled.BannedUntilUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            proxy.SaveChatClientPermissionsDiagnostics(disabled);

            ChatClientPermissionSnapshot local = proxy.GetChatClientPermissionsDiagnostics("ip:127.0.0.1");
            Assert.IsTrue(local.InternetSearch);
            Assert.IsTrue(local.FileDownloads);
            Assert.IsTrue(local.FtpServer);
            Assert.IsTrue(local.SqlAdmin);
            Assert.IsTrue(local.TerminalCommands);
            Assert.IsTrue(local.TerminalForeverApproved);
            Assert.IsTrue(local.AgentAccess);
            Assert.IsTrue(local.FileUploads);
            Assert.IsTrue(local.ImageUploads);
            Assert.IsFalse(local.MuteUntilEnabled);
            Assert.IsFalse(local.BanUntilEnabled);
            Assert.AreEqual("", local.MutedUntilUtc);
            Assert.AreEqual("", local.BannedUntilUtc);
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
