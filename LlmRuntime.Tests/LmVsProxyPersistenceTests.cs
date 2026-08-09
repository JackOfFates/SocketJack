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
    public void LegacyThirtySevenColumnPermissionRowsKeepExistingIndexes()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-legacy-permission-row-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = CreateProxy(dataRoot);
            object[] row = Enumerable.Repeat<object>("false", 37).ToArray();
            row[0] = "webauth:legacy-row";
            row[2] = "true";
            row[11] = "true";
            row[36] = "true";
            var method = typeof(LmVsProxy).GetMethod("ChatPermissionStateFromRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            object state = method.Invoke(proxy, new object[] { row })!;
            Assert.IsTrue((bool)state.GetType().GetProperty("vsCopilotTools")!.GetValue(state)!);
            Assert.IsTrue((bool)state.GetType().GetProperty("agentAccess")!.GetValue(state)!);
            Assert.IsTrue((bool)state.GetType().GetProperty("agentBuilder")!.GetValue(state)!);
            Assert.IsFalse((bool)state.GetType().GetProperty("runningApplications")!.GetValue(state)!);
            Assert.IsFalse((bool)state.GetType().GetProperty("windowsServices")!.GetValue(state)!);
            Assert.IsFalse((bool)state.GetType().GetProperty("eventViewer")!.GetValue(state)!);
            Assert.IsFalse((bool)state.GetType().GetProperty("fileAccess")!.GetValue(state)!);
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
    }

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
                snapshot.AgentBuilder = true;
                snapshot.RunningApplications = true;
                snapshot.WindowsServices = true;
                snapshot.EventViewer = true;
                snapshot.FileAccess = true;
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
                Assert.IsTrue(reloaded.AgentBuilder);
                Assert.IsTrue(reloaded.RunningApplications);
                Assert.IsTrue(reloaded.WindowsServices);
                Assert.IsTrue(reloaded.EventViewer);
                Assert.IsTrue(reloaded.FileAccess);
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
    public void CompanionPermissionsPersistAndRemainDefaultOffForLocalhost()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-companion-permissions-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = CreateProxy(dataRoot))
            {
                ChatClientPermissionSnapshot snapshot = first.GetChatClientPermissionsDiagnostics("global");
                Assert.IsFalse(snapshot.CompanionEnabled);
                snapshot.CompanionEnabled = true;
                snapshot.CompanionScreenView = true;
                snapshot.CompanionCursorControl = true;
                snapshot.CompanionApplicationLaunch = true;
                snapshot.CompanionApplicationControl = true;
                snapshot.CompanionTerminalCommands = true;
                snapshot.CompanionActivityTranscriptStorage = true;
                snapshot.CompanionSensitiveMemory = true;
                snapshot.CompanionFinancialActions = true;
                first.SaveChatClientPermissionsDiagnostics(snapshot);
            }
            using (var second = CreateProxy(dataRoot))
            {
                ChatClientPermissionSnapshot saved = second.GetChatClientPermissionsDiagnostics("global");
                Assert.IsTrue(saved.CompanionEnabled);
                Assert.IsTrue(saved.CompanionScreenView);
                Assert.IsTrue(saved.CompanionCursorControl);
                Assert.IsTrue(saved.CompanionApplicationLaunch);
                Assert.IsTrue(saved.CompanionApplicationControl);
                Assert.IsTrue(saved.CompanionTerminalCommands);
                Assert.IsTrue(saved.CompanionActivityTranscriptStorage);
                Assert.IsTrue(saved.CompanionSensitiveMemory);
                Assert.IsTrue(saved.CompanionFinancialActions);

                ChatClientPermissionSnapshot owner = second.GetChatClientPermissionsDiagnostics("webauth:new-owner");
                owner.CompanionEnabled = false;
                owner.CompanionScreenView = false;
                owner.CompanionCursorControl = false;
                owner.CompanionApplicationLaunch = false;
                owner.CompanionApplicationControl = false;
                owner.CompanionTerminalCommands = false;
                owner.CompanionActivityTranscriptStorage = false;
                owner.CompanionSensitiveMemory = false;
                owner.CompanionFinancialActions = false;
                second.SaveChatClientPermissionsDiagnostics(owner);
                ChatClientPermissionSnapshot local = second.GetChatClientPermissionsDiagnostics("ip:127.0.0.1");
                Assert.IsTrue(local.CompanionEnabled, "Loopback inherits the explicit global rule but must not manufacture a grant.");
                ChatClientPermissionSnapshot explicitLocal = second.GetChatClientPermissionsDiagnostics("ip:127.0.0.1");
                explicitLocal.CompanionEnabled = false;
                explicitLocal.CompanionScreenView = false;
                second.SaveChatClientPermissionsDiagnostics(explicitLocal);
                Assert.IsFalse(second.GetChatClientPermissionsDiagnostics("ip:127.0.0.1").CompanionEnabled);
                Assert.IsFalse(second.GetChatClientPermissionsDiagnostics("ip:127.0.0.1").CompanionScreenView);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
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
            disabled.AgentBuilder = false;
            disabled.TerminalCommands = false;
            disabled.TerminalForeverApproved = false;
            disabled.AgentAccess = false;
            disabled.FileUploads = false;
            disabled.ImageUploads = false;
            disabled.PcAccess = false;
            disabled.RunningApplications = false;
            disabled.WindowsServices = false;
            disabled.EventViewer = false;
            disabled.FileAccess = false;
	            disabled.BanUntilEnabled = true;
	            disabled.BannedUntilUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            proxy.SaveChatClientPermissionsDiagnostics(disabled);

            ChatClientPermissionSnapshot local = proxy.GetChatClientPermissionsDiagnostics("ip:127.0.0.1");
            Assert.IsTrue(local.InternetSearch);
            Assert.IsTrue(local.FileDownloads);
            Assert.IsTrue(local.FtpServer);
            Assert.IsTrue(local.SqlAdmin);
            Assert.IsTrue(local.AgentBuilder);
            Assert.IsTrue(local.TerminalCommands);
            Assert.IsTrue(local.TerminalForeverApproved);
            Assert.IsTrue(local.AgentAccess);
            Assert.IsTrue(local.FileUploads);
            Assert.IsTrue(local.ImageUploads);
            Assert.IsFalse(local.PcAccess, "Local admin elevation must not implicitly grant PC Access.");
            Assert.IsFalse(local.RunningApplications, "Localhost must not implicitly grant running-application context.");
            Assert.IsFalse(local.WindowsServices, "Localhost must not implicitly grant Windows-service context.");
            Assert.IsFalse(local.EventViewer, "Localhost must not implicitly grant Event Viewer context.");
            Assert.IsFalse(local.FileAccess, "Localhost must not implicitly grant read-only file context.");
	            Assert.IsFalse(local.BanUntilEnabled);
            Assert.AreEqual("", local.BannedUntilUtc);
	        Assert.ThrowsException<ArgumentException>(() => proxy.RestrictChatClient("ip:203.0.113.10", "mute", TimeSpan.FromMinutes(10)), "The removed mute action must be rejected.");
	        ChatClientPermissionSnapshot banned = proxy.RestrictChatClient("ip:203.0.113.10", "ban", TimeSpan.FromMinutes(10));
	        Assert.IsTrue(banned.IsBanned, "Ban must remain independent after mute removal.");
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
