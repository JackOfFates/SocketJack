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
                snapshot.PcAccess = true;
                snapshot.DreamInternetSearch = true;
                snapshot.DreamFileDownloads = true;
                snapshot.DreamFtpServer = true;
                snapshot.DreamPcAccess = true;
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
                Assert.IsTrue(reloaded.PcAccess);
                Assert.IsTrue(reloaded.DreamInternetSearch);
                Assert.IsTrue(reloaded.DreamFileDownloads);
                Assert.IsTrue(reloaded.DreamFtpServer);
                Assert.IsTrue(reloaded.DreamPcAccess);
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
    public void DreamPermissionRequiresBasePermissionAndTerminalTrust()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-dream-permissions-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = CreateProxy(dataRoot);
            ChatClientPermissionSnapshot snapshot = proxy.GetChatClientPermissionsDiagnostics("webauth:dream-test");
            snapshot.InternetSearch = false;
            snapshot.DreamInternetSearch = true;
            snapshot.TerminalCommands = true;
            snapshot.TerminalForeverApproved = false;
            snapshot.DreamTerminalCommands = true;
            ChatClientPermissionSnapshot saved = proxy.SaveChatClientPermissionsDiagnostics(snapshot);
            Assert.IsFalse(saved.DreamInternetSearch);
            Assert.IsFalse(saved.DreamTerminalCommands);
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public void DreamSettingsInheritGlobalUntilOwnerOverrideIsCreated()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-dream-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var proxy = CreateProxy(dataRoot))
            {
                DreamSettingsSnapshot global = proxy.GetDreamSettingsDiagnostics("global");
                global.Enabled = true;
                global.Preset = "balanced";
                proxy.SaveDreamSettingsDiagnostics("global", global);
                Assert.IsTrue(proxy.GetDreamSettingsDiagnostics("webauth:dream-owner").Enabled);
                Assert.AreEqual("balanced", proxy.GetDreamSettingsDiagnostics("webauth:dream-owner").Preset);

                DreamSettingsSnapshot owner = proxy.GetDreamSettingsDiagnostics("webauth:dream-owner");
                owner.Enabled = false;
                owner.Preset = "custom";
                proxy.SaveDreamSettingsDiagnostics("webauth:dream-owner", owner);
                Assert.IsFalse(proxy.GetDreamSettingsDiagnostics("webauth:dream-owner").Enabled);
                proxy.ResetDreamSettingsDiagnostics("webauth:dream-owner");
                Assert.IsTrue(proxy.GetDreamSettingsDiagnostics("webauth:dream-owner").Enabled);
            }

            using (var reloaded = CreateProxy(dataRoot))
            {
                DreamSettingsSnapshot inherited = reloaded.GetDreamSettingsDiagnostics("webauth:dream-owner");
                Assert.IsTrue(inherited.Enabled, "Reset to Global must survive a Workstation restart.");
                Assert.AreEqual("balanced", inherited.Preset);
                Assert.AreEqual("auto", inherited.Model);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [TestMethod]
    public void NewDreamDefaultsUseAndPersistHardwareRecommendation()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-dream-hardware-" + Guid.NewGuid().ToString("N"));
        try
        {
            DreamSettingsSnapshot firstSettings;
            using (var first = CreateProxy(dataRoot))
            {
                firstSettings = first.GetDreamSettingsDiagnostics("global");
                DreamHardwareRecommendationSnapshot recommendation = first.GetDreamHardwareRecommendationDiagnostics();
                if (recommendation.Pending)
                {
                    recommendation = first.ResolveDreamHardwareRecommendationDiagnostics("apply");
                    firstSettings = first.GetDreamSettingsDiagnostics("global");
                }
                Assert.IsFalse(recommendation.Pending);
                Assert.AreEqual("recommended", firstSettings.Preset);
                Assert.AreEqual(recommendation.RecommendedSettings.StartCpuPercent, firstSettings.StartCpuPercent);
                Assert.AreEqual(recommendation.RecommendedSettings.PauseVramPercent, firstSettings.PauseVramPercent);
                Assert.IsTrue(firstSettings.StartCpuPercent < firstSettings.PauseCpuPercent);
                Assert.IsFalse(string.IsNullOrWhiteSpace(recommendation.CurrentHardware));
            }

            using (var reloaded = CreateProxy(dataRoot))
            {
                DreamSettingsSnapshot persisted = reloaded.GetDreamSettingsDiagnostics("global");
                Assert.AreEqual(firstSettings.Preset, persisted.Preset);
                Assert.AreEqual(firstSettings.StartRamPercent, persisted.StartRamPercent);
                Assert.IsFalse(reloaded.GetDreamHardwareRecommendationDiagnostics().Pending);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
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
            disabled.PcAccess = false;
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
            Assert.IsFalse(local.PcAccess, "Local admin elevation must not implicitly grant PC Access.");
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
