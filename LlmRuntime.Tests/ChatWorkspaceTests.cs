using Microsoft.VisualStudio.TestTools.UnitTesting;
using LmVs;
using SocketJack;
using SocketJack.Net;
using System.Reflection;
using System.Text.Json;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class ChatWorkspaceTests
{
    [TestMethod]
    public void WebChatExposesSessionWorkspaceAndRegexBuilder()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "id=\"workspaceSetPrimary\"");
        StringAssert.Contains(html, "id=\"workspaceAddAttached\"");
        StringAssert.Contains(html, "id=\"workspaceAddGlobal\"");
        StringAssert.Contains(html, "id=\"workspaceRegexMode\"");
        StringAssert.Contains(html, "id=\"workspaceRegexPattern\"");
        StringAssert.Contains(html, "id=\"workspaceRegexTestPath\"");
        StringAssert.Contains(html, "/api/chat-workspaces");
        StringAssert.Contains(html, "function generateWorkspaceRegex()");
        StringAssert.Contains(html, "id=\"projectVersionsPanel\"");
        StringAssert.Contains(html, "/api/project-file-versions");
        StringAssert.Contains(html, "preview-upload-overlay");
        StringAssert.Contains(html, "postSessionFileWithProgress");
        StringAssert.Contains(html, "deleteSolutionEntry");
        StringAssert.Contains(html, "Delete folder and contents from Project Files");
        Assert.IsFalse(html.Contains("showConfirm(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebChatLoadsAuthenticatedOwnerPermissionsBeforeRenderingAccess()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "id=\"accessPanelToggle\" class=\"top-panel-summary access-panel-toggle\"");
        StringAssert.Contains(html, "aria-controls=\"permissionsMenu\" hidden");
        StringAssert.Contains(html, "id=\"permissionsMenu\" class=\"permissions-menu header-permissions\" hidden");
        StringAssert.Contains(html, "let selectedPermissionOwnerKey = '';");
        StringAssert.Contains(html, "ownerKey || selectedPermissionOwnerKey || currentSessionOwnerKey || 'global'");

        int bootstrapStart = html.IndexOf("async function bootstrapChatApp()", StringComparison.Ordinal);
        int ownerLoad = html.IndexOf("await withTimeout('session owner', loadSessionOwner(), 8000);", bootstrapStart, StringComparison.Ordinal);
        int permissionLoad = html.IndexOf("await withTimeout('permissions', loadPermissions(15000), 8000)", bootstrapStart, StringComparison.Ordinal);
        Assert.IsTrue(ownerLoad >= bootstrapStart && permissionLoad > ownerLoad,
            "The authenticated owner must load before its effective permission snapshot.");
    }

    [TestMethod]
    public void SessionWorkspacePersistsPrimaryAttachedGlobalAndIgnoreRules()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-workspace-test-" + Guid.NewGuid().ToString("N"));
        string primaryPath = Path.Combine(dataRoot, "SocketJack");
        string attachedPath = Path.Combine(dataRoot, "OtherApp");
        string globalPath = Path.Combine(dataRoot, "Shared");
        Directory.CreateDirectory(primaryPath);
        Directory.CreateDirectory(attachedPath);
        Directory.CreateDirectory(globalPath);

        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 1234, 27434, 27436, dataRoot);
            ChatWorkspaceRootSnapshot primary = proxy.SaveChatWorkspaceRootDiagnostics(
                "local:test", "session-one", "", "primary", "SocketJack", primaryPath, "read-write");
            ChatWorkspaceRootSnapshot attached = proxy.SaveChatWorkspaceRootDiagnostics(
                "local:test", "session-one", "", "attached", "OtherApp", attachedPath, "read-write", primary.Id);
            ChatWorkspaceRootSnapshot global = proxy.SaveChatWorkspaceRootDiagnostics(
                "local:test", "", "", "global", "Shared", globalPath, "read-write");
            ChatWorkspaceIgnoreRuleSnapshot rule = proxy.SaveChatWorkspaceIgnoreRuleDiagnostics(
                "local:test", "session-one", "", attached.Id, "Build outputs",
                @"(^|/)(bin|obj)(/|$)", "path", false, true, "visual", "{}");

            IReadOnlyList<ChatWorkspaceRootSnapshot> roots =
                proxy.GetChatWorkspaceRootsDiagnostics("local:test", "session-one");
            IReadOnlyList<ChatWorkspaceIgnoreRuleSnapshot> rules =
                proxy.GetChatWorkspaceIgnoreRulesDiagnostics("local:test", "session-one");

            Assert.AreEqual("read-only", global.AccessMode);
            Assert.IsTrue(roots.Any(item => item.Id == primary.Id && item.Role == "primary"));
            Assert.IsTrue(roots.Any(item => item.Id == attached.Id && item.ParentId == primary.Id));
            Assert.IsTrue(roots.Any(item => item.Id == global.Id && item.IsInherited && item.AccessMode == "read-only"));
            Assert.IsTrue(rules.Any(item => item.Id == rule.Id && item.RootId == attached.Id));
            Assert.IsTrue(proxy.TestChatWorkspaceIgnoreRegex(
                rule.Pattern, rule.Target, rule.CaseSensitive, "src/bin/Debug/app.dll", out string error), error);
            Assert.IsFalse(proxy.TestChatWorkspaceIgnoreRegex(
                rule.Pattern, rule.Target, rule.CaseSensitive, "src/App.cs", out error), error);

            string uploadPath = Path.Combine(primaryPath, "note.txt");
            File.WriteAllText(uploadPath, "workspace upload visible");
            using JsonDocument request = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                files = new[] { new { name = "note.txt", type = "text/plain", path = uploadPath } }
            }));
            MethodInfo promptContextMethod = typeof(LmVsProxy).GetMethod(
                "BuildChatUploadedFilesSystemHint",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            string promptContext = (string)promptContextMethod.Invoke(
                proxy,
                new object[] { request.RootElement, false, "local:test", "session-one" })!;
            StringAssert.Contains(promptContext, "workspace upload visible");
            StringAssert.Contains(promptContext, "SocketJack/note.txt");
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
