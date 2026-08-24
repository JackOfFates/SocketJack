using Microsoft.VisualStudio.TestTools.UnitTesting;
using heirowLLM;
using SocketJack;
using SocketJack.Net;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class ChatWorkspaceTests
{
    [TestMethod]
    public void WebChatAssistantAnswersAndMarkdownTablesUseAvailableWidth()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");

        StringAssert.Contains(html, ".message.assistant .answer {");
        StringAssert.Contains(html, "max-width: 100% !important;");
        StringAssert.Contains(html, ".answer .markdown-table-scroll {");
        StringAssert.Contains(html, "width: max-content;");
        StringAssert.Contains(html, "min-width: 100%;");
        int fluentMessageRule = html.LastIndexOf(".message {", StringComparison.Ordinal);
        int fluentMessageRuleEnd = html.IndexOf('}', fluentMessageRule);
        string rule = html.Substring(fluentMessageRule, fluentMessageRuleEnd - fluentMessageRule);
        StringAssert.Contains(rule, "max-width: 100% !important;");
        Assert.IsFalse(rule.Contains("820px", StringComparison.Ordinal),
            "The desktop message rule must not cap assistant answers at 820px.");
    }

    [TestMethod]
    public void WebChatRecoversStreamedFencesAndPreviewsWebCodeInASandbox()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");

        StringAssert.Contains(html, "function splitFencedBlocks(markdown)");
        StringAssert.Contains(html, "const gluedHeading = info.match");
        StringAssert.Contains(html, "const embeddedFence = body.match");
        StringAssert.Contains(html, "function toggleWebCodePreview(block, button)");
        StringAssert.Contains(html, "function buildWebCodePreviewDocument(activeBlock)");
        StringAssert.Contains(html, "frame.sandbox = 'allow-scripts allow-forms allow-modals allow-popups allow-downloads'");
        StringAssert.Contains(html, "button.disabled = previewable && !closed");
        StringAssert.Contains(html, "browserVerification: agentMode && isJackhammerEnabled()");
        StringAssert.Contains(html, "snapshot.screenshotKind = snapshot.screenshotUrl ? 'live-page' : 'observation-card'");
    }

    [TestMethod]
    public void WebChatAdvertisesContextConsentAndApprovalControls()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");
        StringAssert.Contains(html, "id=\"permRunningApplications\"");
        StringAssert.Contains(html, "id=\"permWindowsServices\"");
        StringAssert.Contains(html, "id=\"permEventViewer\"");
        StringAssert.Contains(html, "id=\"permFileAccess\"");
        StringAssert.Contains(html, "settings.contextConsentUi = modelSupportsToolsById(normalizedModel)");
        StringAssert.Contains(html, "id=\"contextApprovalToasts\"");
        StringAssert.Contains(html, "Allow once");
        StringAssert.Contains(html, "Always allow");
        StringAssert.Contains(html, "/api/context-approvals");
    }

    [TestMethod]
    public void WebChatExposesSessionWorkspaceAndRegexBuilder()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");

        int permissionPanel = html.IndexOf("id=\"permissionsMenu\"", StringComparison.Ordinal);
        int projectExplorer = html.IndexOf("id=\"solutionExplorer\"", StringComparison.Ordinal);
        int workspaceBrowser = html.IndexOf("id=\"projectWorkspaceBrowser\"", StringComparison.Ordinal);
        Assert.IsTrue(permissionPanel >= 0 && projectExplorer > permissionPanel && workspaceBrowser > projectExplorer,
            "Workspace management belongs in Solution Explorer, not the administrator permissions flyout.");
        StringAssert.Contains(html, "id=\"workspaceSetPrimary\"");
        StringAssert.Contains(html, "id=\"workspaceAddAttached\"");
        StringAssert.Contains(html, "id=\"workspaceAddGlobal\"");
        StringAssert.Contains(html, "id=\"workspaceAddFiles\"");
        StringAssert.Contains(html, "id=\"workspaceAddFolder\"");
        StringAssert.Contains(html, "className = 'workspace-access-control'");
        StringAssert.Contains(html, "['read-only', 'Read only']");
        StringAssert.Contains(html, "['read-write', 'Read & write']");
        StringAssert.Contains(html, "function chooseProjectWorkspaceUpload(includeFolder)");
        StringAssert.Contains(html, "id=\"workspaceRegexMode\"");
        StringAssert.Contains(html, "id=\"workspaceRegexPattern\"");
        StringAssert.Contains(html, "id=\"workspaceRegexTestPath\"");
        StringAssert.Contains(html, "/api/chat-workspaces");
        StringAssert.Contains(html, "function generateWorkspaceRegex()");
        StringAssert.Contains(html, "candidate.classList.toggle('selected', candidate === item)");
        StringAssert.Contains(html, "const displayName = parts[parts.length - 1] || 'Workspace';");
        Assert.IsFalse(html.Contains("Copy existing sandbox session files into the new primary workspace?", StringComparison.Ordinal));
        StringAssert.Contains(html, "id=\"projectVersionsPanel\"");
	    StringAssert.Contains(html, "/api/project-version-control");
        StringAssert.Contains(html, "preview-upload-overlay");
        StringAssert.Contains(html, "postSessionFileWithProgress");
        StringAssert.Contains(html, "deleteSolutionEntry");
        StringAssert.Contains(html, "id=\"deleteSelectedSolutionEntries\"");
        StringAssert.Contains(html, ">delete selected files</button>");
        StringAssert.Contains(html, "deleteSelectedSolutionFiles");
        StringAssert.Contains(html, "Delete folder and contents from Project Files");
        Assert.IsFalse(html.Contains("showConfirm(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebChatLoadsAuthenticatedOwnerPermissionsBeforeRenderingAccess()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");

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
    public void WebChatHeaderControlsWrapPersistAndExposeLayoutOptions()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");

        StringAssert.Contains(html, "id=\"menuPermissions\"");
        StringAssert.Contains(html, "data-header-layout-preset=\"default\"");
        StringAssert.Contains(html, "data-header-layout-preset=\"compact\"");
        StringAssert.Contains(html, "data-header-layout-preset=\"wide\"");
        StringAssert.Contains(html, "id=\"menuResetHeaderLayout\"");
        StringAssert.Contains(html, "const headerLayoutStorageKey = 'heirowllm-header-layout-v1';");
        StringAssert.Contains(html, "function initializeHeaderControlLayout()");
        StringAssert.Contains(html, ".header-layout-surface {");
        StringAssert.Contains(html, "#runtimeControls.header-layout-ready.compact-runtime-controls > #headerLayoutSurface { display:flex !important; }");
        StringAssert.Contains(html, "flex-wrap:wrap;");
        StringAssert.Contains(html, ".identity-summary .logout-button { margin-left:auto !important; }");
        StringAssert.Contains(html, "#runtimeControls.header-layout-ready #accessPanelToggle { display:none !important; }");
        StringAssert.Contains(html, "block.setAttribute('aria-description', 'Drag the panel background to move')");
        Assert.IsFalse(html.Contains("block.title = label +", StringComparison.Ordinal));
        StringAssert.Contains(html, "chip.removeAttribute('title')");
        StringAssert.Contains(html, "hardwareInstantTooltip.textContent = chip.dataset.tooltip");
        StringAssert.Contains(html, "id=\"usagePanel\" class=\"usage-panel identity-usage-panel\"");
        StringAssert.Contains(html, "id=\"workspaceViewPanel\" class=\"workspace-view-panel\"");
        StringAssert.Contains(html, "createBlock('usage', 'Workstation views', [workspaceViewPanel])");
        StringAssert.Contains(html, ".header-layout-block .identity-usage-panel .usage-track {");
        StringAssert.Contains(html, "height:2px;");
        StringAssert.Contains(html, "border-radius:0;");
        StringAssert.Contains(html, "background:transparent !important;");
        StringAssert.Contains(html, ".identity-usage-panel.usage-expanded #usageTokenText::before");
        StringAssert.Contains(html, "usageTokenText.removeAttribute('title')");
        StringAssert.Contains(html, "usagePanel.removeAttribute('title')");
        StringAssert.Contains(html, "...Object.values(companionPermissionInputs)");
        int identityUsageIndex = html.IndexOf("id=\"usagePanel\" class=\"usage-panel identity-usage-panel\"", StringComparison.Ordinal);
        int headerControlsIndex = html.IndexOf("<div class=\"header-controls top-panel\">", StringComparison.Ordinal);
        int workspaceViewIndex = html.IndexOf("id=\"workspaceViewPanel\" class=\"workspace-view-panel\"", StringComparison.Ordinal);
        Assert.IsTrue(identityUsageIndex >= 0 && identityUsageIndex < headerControlsIndex,
            "Usage must be inside the signed-in account panel.");
        Assert.IsTrue(workspaceViewIndex > headerControlsIndex,
            "The workstation view tabs must remain in their own movable header block.");
        Assert.IsFalse(html.Contains("id=\"menuAdministrativeTools\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebChatExposesFirstClassWorkspaceFeatureTabs()
    {
        string html = HtmlPageResources.GetHtml("heirowLLMWebChat.html");

        StringAssert.Contains(html, "id=\"workspaceBuilderTab\"");
        StringAssert.Contains(html, "id=\"workspaceSqlTab\"");
        StringAssert.Contains(html, "id=\"workspaceDirectorTab\"");
        StringAssert.Contains(html, "id=\"workspaceSongTab\"");
        StringAssert.Contains(html, "id=\"featureWorkspaceFrame\"");
        StringAssert.Contains(html, "id=\"permAgentBuilder\"");
        StringAssert.Contains(html, "id=\"permSqlAdmin\"");
        StringAssert.Contains(html, "permissionState.agentBuilder");
        StringAssert.Contains(html, "permissionState.sqlAdmin");
        StringAssert.Contains(html, "director: { title: 'heirowDirector', path: '/heirowDirector' }");
        StringAssert.Contains(html, "song: { title: 'heirowSong', path: '/heirowSong' }");
        StringAssert.Contains(html, "featureWorkspaceFrame.src = definition.path;");
        Assert.IsFalse(html.Contains("id=\"jackDirectorLauncher\"", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("id=\"heirowSongLauncher\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProjectAssessmentCannotFinishBeforeFilesystemInspection()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 1234, 27434, 27436,
            Path.Combine(Path.GetTempPath(), "heirowllm-grounding-test-" + Guid.NewGuid().ToString("N")));
        string request = JsonSerializer.Serialize(new
        {
            messages = new object[]
            {
                new { role = "system", content = "[HeirowLlm Agent Filesystem Context]\nApproved roots:\n- SocketJack [primary] [read-write] | C:\\work\\SocketJack" },
                new { role = "user", content = "How can we improve the UI and functionality?" }
            },
            tools = new object[]
            {
                new { type = "function", function = new { name = "vs_list_files" } },
                new { type = "function", function = new { name = "vs_read_file" } }
            }
        });
        MethodInfo method = typeof(HeirowLlm).GetMethod(
            "PromptRequiresFilesystemGrounding",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { request })!);
    }

    [TestMethod]
    public void JackhammerChatReceivesCurrentProjectIdentityAndFilesystemRoot()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-jackhammer-context-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(dataRoot, "SocketJack");
        Directory.CreateDirectory(projectRoot);
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", 1234, 27434, 27436, dataRoot);
            proxy.SaveChatWorkspaceRootDiagnostics(
                "local:test", "session-one", "", "primary", "SocketJack", projectRoot, "read-write");
            using JsonDocument request = JsonDocument.Parse("""
                {"sessionId":"session-one","jackhammer":{"enabled":true}}
                """);
            Type permissionType = typeof(HeirowLlm).Assembly.GetType("heirowLLM.ChatPermissionState", throwOnError: true)!;
            object permissions = Activator.CreateInstance(permissionType)!;
            permissionType.GetProperty("vsCopilotTools")!.SetValue(permissions, true);
            MethodInfo method = typeof(HeirowLlm).GetMethod(
                "BuildAgentFilesystemContextSystemHint",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            string hint = (string)method.Invoke(proxy, new[] { (object)request.RootElement, permissions, "local:test", "session-one" })!;

            StringAssert.Contains(hint, "Mode: All approved roots.");
            StringAssert.Contains(hint, "Current project: SocketJack.");
            StringAssert.Contains(hint, "Current project root: " + projectRoot + ".");
            StringAssert.Contains(hint, "Do not say that you cannot see or access the project");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task AutomaticProjectVersionPersistsVerificationEvidenceWithoutDuplicatingReferencedFiles()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-version-verification-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(dataRoot, "ReferencedProject");
        Directory.CreateDirectory(projectRoot);
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", NextFreePort(), NextFreePort(), NextFreePort(), dataRoot)
            {
                PublicAccessEnabled = false,
                RequireWorkstationUserAuthentication = false
            };
            Assert.IsTrue(proxy.ChatServer.Listen());
            using var client = new System.Net.Http.HttpClient { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.PostAsync("/api/chat-session", new System.Net.Http.StringContent(
                JsonSerializer.Serialize(new { id = "session-one", title = "Verified project", messages = Array.Empty<object>() }), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            proxy.SaveChatWorkspaceRootDiagnostics("unauthenticated", "session-one", "", "primary", "ReferencedProject", projectRoot, "read-write");
            MethodInfo begin = typeof(HeirowLlm).GetMethod("BeginAutomaticVersionControlRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo record = typeof(HeirowLlm).GetMethod("RecordActiveProjectVerification", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo complete = typeof(HeirowLlm).GetMethod("CompleteAutomaticVersionControlRun", BindingFlags.Instance | BindingFlags.NonPublic)!;

            begin.Invoke(proxy, new object[] { "prompt-one", "unauthenticated", "session-one" });
            string result = (string)record.Invoke(proxy, new object[] { "unauthenticated", "session-one", "passed", "Release build and browser smoke check passed.", new[] { "dotnet build -c Release", "http://127.0.0.1:5000/", "page.png" } })!;
            StringAssert.Contains(result, "\"verificationStatus\":\"passed\"");
            string versionId = (string)complete.Invoke(proxy, new object[] { "prompt-one", "completed" })!;

            ProjectVersionControlVersionSnapshot version = proxy.GetProjectVersionControlDiagnostics("unauthenticated", "session-one").Versions.Single(item => item.Id == versionId);
            Assert.AreEqual("passed", version.VerificationStatus);
            StringAssert.Contains(version.VerificationSummary, "browser smoke check passed");
            Assert.AreEqual(3, version.VerificationEvidence.Count);
            Assert.AreEqual(0, version.StorageBytes, "Referenced project files must not be duplicated into version storage.");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static int NextFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void SessionWorkspacePersistsPrimaryAttachedGlobalAndIgnoreRules()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-workspace-test-" + Guid.NewGuid().ToString("N"));
        string primaryPath = Path.Combine(dataRoot, "SocketJack");
        string attachedPath = Path.Combine(dataRoot, "OtherApp");
        string globalPath = Path.Combine(dataRoot, "Shared");
        Directory.CreateDirectory(primaryPath);
        Directory.CreateDirectory(attachedPath);
        Directory.CreateDirectory(globalPath);

        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", 1234, 27434, 27436, dataRoot);
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
            MethodInfo promptContextMethod = typeof(HeirowLlm).GetMethod(
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

    [TestMethod]
    public void SelectingAnAttachedFolderAsPrimaryPromotesTheExistingReference()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-workspace-promote-" + Guid.NewGuid().ToString("N"));
        string projectPath = Path.Combine(dataRoot, "SJWeb");
        Directory.CreateDirectory(projectPath);

        try
        {
            string promotedRootId;
            using (var proxy = new HeirowLlm("127.0.0.1", 1234, 27434, 27436, dataRoot))
            {
                ChatWorkspaceRootSnapshot attached = proxy.SaveChatWorkspaceRootDiagnostics(
                    "local:test", "session-one", "", "attached", "SJWeb", projectPath, "read-write");

                ChatWorkspaceRootSnapshot primary = proxy.SaveChatWorkspaceRootDiagnostics(
                    "local:test", "session-one", "", "primary", "SJWeb", projectPath, "read-write");
                IReadOnlyList<ChatWorkspaceRootSnapshot> roots = proxy.GetChatWorkspaceRootsDiagnostics(
                    "local:test", "session-one");

                Assert.AreEqual(attached.Id, primary.Id);
                Assert.AreEqual("primary", primary.Role);
                Assert.AreEqual(1, roots.Count(item => !item.IsSandbox));
                Assert.IsFalse(roots.Any(item => item.IsSandbox));
                promotedRootId = primary.Id;
            }

            using var reloaded = new HeirowLlm("127.0.0.1", 1234, 27434, 27436, dataRoot);
            IReadOnlyList<ChatWorkspaceRootSnapshot> persistedRoots = reloaded.GetChatWorkspaceRootsDiagnostics(
                "local:test", "session-one");
            Assert.IsTrue(persistedRoots.Any(item => item.Id == promotedRootId && item.Role == "primary"));
            Assert.IsFalse(persistedRoots.Any(item => item.IsSandbox));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void ReferencedProjectInsideAnotherSessionUsesNormalFileIo()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-cross-session-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", 1234, 27434, 27436, dataRoot);
            MethodInfo sessionDirectoryMethod = typeof(HeirowLlm).GetMethod(
                "GetChatSessionFilesDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string oldSessionRoot = (string)sessionDirectoryMethod.Invoke(proxy, new object[] { "old-session" })!;
            string projectRoot = Path.Combine(oldSessionRoot, "SJWeb");
            Directory.CreateDirectory(projectRoot);
            string programPath = Path.Combine(projectRoot, "Program.cs");
            File.WriteAllText(programPath, "var status = \"before\";\n");
            proxy.SaveChatWorkspaceRootDiagnostics(
                "local:test", "new-session", "", "primary", "SJWeb", projectRoot, "read-write");

            MethodInfo readMethod = typeof(HeirowLlm).GetMethod(
                "ExecuteVsReadFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string readResult = (string)readMethod.Invoke(proxy, new object[]
            {
                JsonSerializer.Serialize(new { path = "\\Program.cs" }), "local:test", "new-session"
            })!;
            StringAssert.Contains(readResult, "var status = \"before\"");
            Assert.IsFalse(readResult.Contains("sandbox file does not exist", StringComparison.OrdinalIgnoreCase));

            MethodInfo replaceMethod = typeof(HeirowLlm).GetMethod(
                "ExecuteVsReplaceInFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string replaceResult = (string)replaceMethod.Invoke(proxy, new object[]
            {
                JsonSerializer.Serialize(new { path = "\\Program.cs", oldString = "before", newString = "after" }),
                "local:test",
                "new-session"
            })!;
            StringAssert.Contains(replaceResult, "applied 1 replacement");
            StringAssert.Contains(File.ReadAllText(programPath), "after");
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
