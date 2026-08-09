using System.Text.Json;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class SystemContextPermissionTests
{
    [TestMethod]
    public async Task UncheckedContextRequestsApprovalForEveryCall()
    {
        string dataRoot = TempRoot();
        try
        {
            using var proxy = CreateProxy(dataRoot);
            int providerCalls = 0;
            proxy.SystemContextProvider = (query, _) =>
            {
                providerCalls++;
                return Task.FromResult(new SystemContextResult
                {
                    Ok = true,
                    Kind = query.Kind,
                    Items = { new Dictionary<string, object> { ["name"] = "Example" } }
                });
            };

            for (int call = 0; call < 2; call++)
            {
                Task<string> execution = proxy.ExecuteSystemContextToolDiagnosticsAsync(
                    "list_running_applications", "{\"query\":\"Example\"}", "webauth:context-user", "session-one");
                SystemContextPermissionRequestSnapshot pending = await WaitForPendingAsync(proxy, "webauth:context-user", "session-one");
                Assert.AreEqual("runningApplications", pending.Capability);
                Assert.IsTrue(proxy.DecideSystemContextPermissionDiagnostics(pending.Id, approved: true));
                using JsonDocument result = JsonDocument.Parse(await execution.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(result.RootElement.GetProperty("Ok").GetBoolean());
            }

            Assert.AreEqual(2, providerCalls);
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
    }

    [TestMethod]
    public async Task StandingPermissionRunsImmediatelyAndInvalidEventChannelIsRejected()
    {
        string dataRoot = TempRoot();
        try
        {
            using var proxy = CreateProxy(dataRoot);
            ChatClientPermissionSnapshot permissions = proxy.GetChatClientPermissionsDiagnostics("webauth:standing-context");
            permissions.EventViewer = true;
            proxy.SaveChatClientPermissionsDiagnostics(permissions);
            int providerCalls = 0;
            proxy.SystemContextProvider = (query, _) =>
            {
                providerCalls++;
                return Task.FromResult(new SystemContextResult { Ok = true, Kind = query.Kind });
            };

            string output = await proxy.ExecuteSystemContextToolDiagnosticsAsync(
                "query_event_viewer", "{\"logName\":\"Security\"}", "webauth:standing-context", "session-two");
            StringAssert.Contains(output, "Application, System, or both");
            Assert.AreEqual(0, providerCalls);
            Assert.AreEqual(0, proxy.GetPendingSystemContextPermissionRequestsDiagnostics("webauth:standing-context", "session-two").Count);
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
    }

    [TestMethod]
    public void CapabilityContextNamesEnabledDisabledAndRequestableCapabilities()
    {
        string dataRoot = TempRoot();
        try
        {
            using var proxy = CreateProxy(dataRoot);
            ChatClientPermissionSnapshot permissions = proxy.GetChatClientPermissionsDiagnostics("webauth:manifest");
            permissions.WindowsServices = true;
            proxy.SaveChatClientPermissionsDiagnostics(permissions);
            string context = proxy.GetJackCapabilityContextDiagnostics("webauth:manifest", consentUi: true);
            StringAssert.StartsWith(context, "[JACK capability context]");
            StringAssert.Contains(context, "Enabled:");
            StringAssert.Contains(context, "windowsServices");
            StringAssert.Contains(context, "Disabled:");
            StringAssert.Contains(context, "fileAccess, companionObservation, runningApplications, windowsServices, eventViewer");
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
    }

    [TestMethod]
    public void ContextSchemasRequireConsentUiAndAreOmittedFromPublicShares()
    {
        string dataRoot = TempRoot();
        try
        {
            using var proxy = CreateProxy(dataRoot);
            const string baseRequest = "{\"model\":\"tool-model\",\"messages\":[{\"role\":\"user\",\"content\":\"Check the PC\"}]}";
            string raw = proxy.AttachSystemContextToolsDiagnostics(baseRequest, "webauth:schema-user");
            Assert.IsFalse(raw.Contains("list_running_applications", StringComparison.Ordinal));

            string interactive = proxy.AttachSystemContextToolsDiagnostics(
                "{\"model\":\"tool-model\",\"contextConsentUi\":true,\"messages\":[{\"role\":\"user\",\"content\":\"Check the PC\"}]}",
                "webauth:schema-user");
            StringAssert.Contains(interactive, "list_running_applications");
            StringAssert.Contains(interactive, "list_windows_services");
            StringAssert.Contains(interactive, "query_event_viewer");
            StringAssert.Contains(interactive, "inspect_files");
            StringAssert.Contains(interactive, "[JACK capability context]");

            string publicShare = proxy.AttachSystemContextToolsDiagnostics(
                "{\"model\":\"tool-model\",\"contextConsentUi\":true,\"shareKey\":\"public-key\",\"messages\":[{\"role\":\"user\",\"content\":\"Check the PC\"}]}",
                "webauth:schema-user");
            Assert.IsFalse(publicShare.Contains("list_running_applications", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
    }

    private static async Task<SystemContextPermissionRequestSnapshot> WaitForPendingAsync(LmVsProxy proxy, string ownerKey, string sessionId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            SystemContextPermissionRequestSnapshot? request = proxy.GetPendingSystemContextPermissionRequestsDiagnostics(ownerKey, sessionId).FirstOrDefault();
            if (request is not null) return request;
            await Task.Delay(20);
        }
        Assert.Fail("The context approval request did not become pending.");
        throw new InvalidOperationException();
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "jackllm-system-context-" + Guid.NewGuid().ToString("N"));
    private static LmVsProxy CreateProxy(string dataRoot) => new("127.0.0.1", 11434, 11435, 0, dataRoot);
}
