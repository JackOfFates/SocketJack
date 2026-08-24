using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class WorkstationPytorchRepairAndGpuPlacementTests
{
    [TestMethod]
    public void RepairPytorch_ShowsBusyProgressAndPreventsDuplicateStarts()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));

        StringAssert.Contains(xaml, "Content=\"{Binding DisplayLabel}\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding IsEnabled}\"");
        StringAssert.Contains(xaml, "Visibility=\"{Binding BusyVisibility}\"");
        StringAssert.Contains(xaml, "IsIndeterminate=\"True\"");
        StringAssert.Contains(code, "if (_pytorchRepairInProgress || _imagePythonInstallInProgress)");
        StringAssert.Contains(code, "RepairPytorchButton.Content = \"Repairing PyTorch…\"");
        StringAssert.Contains(code, "if (RuntimeCompatibilityUsesLegacyCudaGpu(before))");
        StringAssert.Contains(code, "EnsureCudaLegacyPythonRuntimeAsync(CancellationToken.None, progress)");
        StringAssert.Contains(code, "new WarningActionItem(\"Install Python\"");
        StringAssert.Contains(code, "BeginInstallImagePythonFromWarningAsync(progress)");
        StringAssert.Contains(code, "EnsureCudaLegacyPythonExecutableAsync(CancellationToken.None, progress)");
        StringAssert.Contains(code, "EnsureBundledPythonExecutableAsync(CancellationToken.None, progress)");
        StringAssert.Contains(code, "PythonExecutable = JackOnnxPythonDiffusersImageRunner.DefaultPreferredImageGenerationPythonExecutable");
        StringAssert.Contains(code, "Uri.EscapeDataString(pythonExecutable)");
        Assert.IsFalse(code.Contains("before.Diagnostics.RecommendedPytorch == null", StringComparison.Ordinal));
        StringAssert.Contains(code, "text.Contains(\"cuda-enabled pytorch is ready\")");
        StringAssert.Contains(code, "ImagePipelineStatusPanel.Visibility = Visibility.Visible");
        StringAssert.Contains(code, "ImagePipelineProgressContainer.Visibility = inProgress ? Visibility.Visible : Visibility.Collapsed");
        StringAssert.Contains(code, "StartImagePipelineStatusAnimation()");
    }

    [TestMethod]
    public void ImageBootstrap_UsesTheSamePreferredRuntimeAsRepair()
    {
        string root = FindRepositoryRoot();
        string workstation = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));
        int bootstrapStart = workstation.IndexOf("private async Task BootstrapImageRuntimeAsync", StringComparison.Ordinal);
        int bootstrapEnd = workstation.IndexOf("private async Task<bool> EnsureLinuxCudaPytorchSelfInstallAsync", bootstrapStart, StringComparison.Ordinal);
        Assert.IsTrue(bootstrapStart >= 0 && bootstrapEnd > bootstrapStart);
        string bootstrap = workstation.Substring(bootstrapStart, bootstrapEnd - bootstrapStart);

        StringAssert.Contains(bootstrap, "EnsurePreferredImageGenerationPythonRuntimeAsync(cancellationToken, status)");
        Assert.IsFalse(bootstrap.Contains("EnsureBundledPythonAsync", StringComparison.Ordinal));
        Assert.IsFalse(bootstrap.Contains("EnsureQwenDiffusersSupportAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FeaturedModelBundles_SwitchInstalledButtonsToConfirmedUninstallActions()
    {
        string root = FindRepositoryRoot();
        string browser = File.ReadAllText(Path.Combine(root, "LlmRuntime.Wpf", "HuggingFaceModelDownloaderControl.xaml.cs"));

        StringAssert.Contains(browser, "HeirowSongModelBundleCatalog.IsInstalled(CompleteModelsDirectory)");
        StringAssert.Contains(browser, "PictureBankAiBundleCatalog.IsInstalled(CompleteModelsDirectory)");
        StringAssert.Contains(browser, "? \"Uninstall heirowSong\"");
        StringAssert.Contains(browser, "? \"Uninstall PictureBank AI\"");
        StringAssert.Contains(browser, "MessageBoxButton.YesNo");
        StringAssert.Contains(browser, "ValidateFeaturedBundleRemovalTarget");
        StringAssert.Contains(browser, "HeirowSongModelBundleCatalog.BundleDirectory(CompleteModelsDirectory)");
        StringAssert.Contains(browser, "PictureBankAiBundleCatalog.BundleDirectory(CompleteModelsDirectory)");
    }

    [TestMethod]
    public void FullCudaLayerOffload_DoesNotAttributeGlobalRamPressureToTheModel()
    {
        string root = FindRepositoryRoot();
        string manager = File.ReadAllText(Path.Combine(root, "LlmRuntime.Wpf", "ModelsManagerControl.xaml.cs"));
        string workstation = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));

        StringAssert.Contains(manager, "public bool IsModelConfiguredForFullGpuLayerOffload(string modelId)");
        StringAssert.Contains(manager, "backend.Contains(\"cuda\"");
        StringAssert.Contains(manager, "config.GpuLayerCount < 0");
        StringAssert.Contains(manager, "!config.AllowBackendFallback");
        StringAssert.Contains(workstation, "bool fullGpuLayerOffload = ModelsManagerControl?.IsModelConfiguredForFullGpuLayerOffload(model) == true");
        StringAssert.Contains(workstation, "if (!fullGpuLayerOffload)");

        int pressureStart = workstation.IndexOf("private bool HasRuntimePressure()", StringComparison.Ordinal);
        int pressureEnd = workstation.IndexOf("private ModelBenchmarkItem? FindModelBenchmark", pressureStart, StringComparison.Ordinal);
        Assert.IsTrue(pressureStart >= 0 && pressureEnd > pressureStart);
        string pressure = workstation.Substring(pressureStart, pressureEnd - pressureStart);
        Assert.IsFalse(pressure.Contains("_ramUsagePercent", StringComparison.Ordinal), "Unattributed global RAM pressure must not invent a model-specific warning.");
    }

    [TestMethod]
    public void ChickenChaserComposer_HasWorkingAttachmentAndSettingsControls()
    {
        string root = FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(root, "heirowLLM", "ChickenChaserWindow.cs"));
        string endpoint = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "ChickenChaser.cs"));
        string markdownMessage = File.ReadAllText(Path.Combine(root, "heirowLLM", "ChickenChaserMarkdownMessage.cs"));
        string agentBuilder = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "heirowLLM.AgentBuilder.cs"));
        string proxy = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "heirowLLM.cs"));
        string web = File.ReadAllText(Path.Combine(root, "SocketJack", "html", "heirowLLMWebChat.html"));

        StringAssert.Contains(window, "ToolTip = \"Attach or import a file or image\"");
        StringAssert.Contains(window, "HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom");
        StringAssert.Contains(window, "CreateHeaderIconButton(\"settings\", \"Chicken Chaser settings\"");
        StringAssert.Contains(window, "settingsButton.Click += (_, e) => { e.Handled = true; ShowSettings(); }");
        StringAssert.Contains(window, "CreateHeaderIconButton(\"open\"");
        StringAssert.Contains(window, "CreateHeaderIconButton(\"close\"");
        StringAssert.Contains(window, "Content = CreateVectorIcon(icon)");
        StringAssert.Contains(window, "Data = Geometry.Parse(data)");
        StringAssert.Contains(window, "button.MouseEnter += (_, _) => button.Background");
        StringAssert.Contains(window, "ChickenChaserWindowsBackdrop.EnableAcrylic(this)");
        StringAssert.Contains(window, "DwmWindowAttributeSystemBackdropType");
        StringAssert.Contains(window, "DwmSystemBackdropNone = 1");
        Assert.IsFalse(window.Contains("DwmSystemBackdropTransientWindow", StringComparison.Ordinal));
        StringAssert.Contains(window, "EnableAcrylicBlurBehind");
        StringAssert.Contains(window, "CreateTranslucentTextButton");
        StringAssert.Contains(window, "Color.FromArgb(204, 255, 255, 255)");
        StringAssert.Contains(window, "_prompt.Padding = new Thickness(8, 8, 8, 38)");
        StringAssert.Contains(window, "TryCreateAttachmentPreview(attachment.DataUrl");
        StringAssert.Contains(window, "MaxHeight = 180, MaxWidth = 370");
        StringAssert.Contains(window, "_attachments.Clear();");
        StringAssert.Contains(window, "RefreshAttachmentBar();");
        StringAssert.Contains(window, "AddMessage(display, true);");
        StringAssert.Contains(window, "var helpLabel = new TextBlock { Text = \"Agent\"");
        StringAssert.Contains(window, "_settings.Mode = \"help\"");
        Assert.IsFalse(window.Contains("Assistant behavior", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("_settings.Mode = SelectedChoice(mode", StringComparison.Ordinal));
        StringAssert.Contains(window, "ComboBox taskMode = AddChoice(stack, \"Task mode\"");
        StringAssert.Contains(window, "bool autoRouting = _settings.Model.Equals(\"auto\"");
        StringAssert.Contains(window, "bool supported = autoRouting");
        StringAssert.Contains(window, "_attachButton.Visibility = Visibility.Visible");
        StringAssert.Contains(window, "_ = RefreshModelCapabilitiesAsync()");
        StringAssert.Contains(window, "ChickenChaserModelHasMarker(model, \"vision\"");
        StringAssert.Contains(window, "ChickenChaserModelHasMarker(model, \"tool-use\"");
        Assert.IsFalse(window.Contains("attach.IsEnabled = false", StringComparison.Ordinal));
        StringAssert.Contains(window, "attachments = attachments.Select");
        StringAssert.Contains(endpoint, "ReadChickenChaserAttachments(root)");
        StringAssert.Contains(endpoint, "BuildChickenChaserUserContent(userPrompt, attachments)");
        StringAssert.Contains(endpoint, "[\"type\"] = \"image_url\"");
        StringAssert.Contains(agentBuilder, "JsonNode userContent = null");
        StringAssert.Contains(agentBuilder, "userContent?.DeepClone()");
        StringAssert.Contains(web, "Toggle Chat or Agent mode");
        StringAssert.Contains(web, "chickenChaser.settings.mode==='help'?'Agent':'Chat'");
        StringAssert.Contains(web, "-webkit-backdrop-filter:blur(22px) saturate(140%)");
        StringAssert.Contains(endpoint, "server.MapStream(\"POST\", \"/api/chickenchaser/chat/stream\"");
        StringAssert.Contains(endpoint, "type = \"thinking\"");
        StringAssert.Contains(endpoint, "ChunkChickenChaserReply(reply)");
        StringAssert.Contains(endpoint, "ChickenChaserStreamJson = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false }");
        StringAssert.Contains(endpoint, "type = \"thinking\", text = \"Thinking...\" }, ChickenChaserStreamJson");
        StringAssert.Contains(endpoint, "type = \"delta\", text = chunk }, ChickenChaserStreamJson");
        StringAssert.Contains(endpoint, "type = \"done\", result }, ChickenChaserStreamJson");
        StringAssert.Contains(window, "HttpCompletionOption.ResponseHeadersRead");
        StringAssert.Contains(window, "_pendingMessages.Enqueue");
        StringAssert.Contains(window, "ToolTip = \"Show thought process\"");
        StringAssert.Contains(window, "thinking.Label.Text = \"Conclusion\"");
        StringAssert.Contains(window, "thinking.Checkmark.Text = \"✓\"");
        Assert.IsFalse(window.Contains("thinking.Label.Text = \"Thought process\"", StringComparison.Ordinal));
        StringAssert.Contains(window, "ChickenChaserMarkdownMessage response = AddMessage");
        StringAssert.Contains(window, "response.AppendMarkdown(delta)");
        StringAssert.Contains(window, "DataObject.AddPastingHandler(_prompt, HandlePromptPaste)");
        StringAssert.Contains(window, "TryPasteClipboardAttachments()) e.Handled = true");
        StringAssert.Contains(window, "Clipboard.GetDataObject()");
        StringAssert.Contains(window, "PasteClipboardAttachments(data, hasFiles, hasImage)");
        StringAssert.Contains(window, "data.GetDataPresent(DataFormats.FileDrop, true)");
        StringAssert.Contains(window, "data.GetDataPresent(DataFormats.Bitmap, true)");
        StringAssert.Contains(window, "Attach or import a file or image");
        StringAssert.Contains(window, "_attachButton.Visibility = Visibility.Visible");
        StringAssert.Contains(window, "AddAttachmentPaths(paths)");
        StringAssert.Contains(window, "new PngBitmapEncoder()");
        StringAssert.Contains(window, "bool autoRouting = _settings.Model.Equals(\"auto\"");
        StringAssert.Contains(window, "_settings.TaskMode.Equals(\"agent\"");
        StringAssert.Contains(window, "Select Auto or a vision-capable model");
        StringAssert.Contains(window, "api/web-auth/login");
        StringAssert.Contains(window, "api/web-auth/session");
        StringAssert.Contains(window, "api/web-auth/logout");
        StringAssert.Contains(window, "class ChickenChaserLoginWindow");
        StringAssert.Contains(window, "Sign in before chatting");
        StringAssert.Contains(window, "Remember me for 30 days");
        StringAssert.Contains(window, "ProtectedData.Protect");
        StringAssert.Contains(window, "ProtectedData.Unprotect");
        StringAssert.Contains(window, "request.Headers.Authorization = new AuthenticationHeaderValue(\"Bearer\"");
        StringAssert.Contains(window, "CreateTranslucentTextButton(\"Log out\"");
        Assert.IsFalse(window.Contains("X-Chicken-Chaser-Key", StringComparison.Ordinal));
        StringAssert.Contains(endpoint, "BuildChickenChaserAccountLibraryHint(ownerKey)");
        StringAssert.Contains(endpoint, "[Web Chat projects and sessions]");
        StringAssert.Contains(endpoint, "promptAsSystem: true");
        StringAssert.Contains(endpoint, "MemoryRecallToolName = \"recall_memories\"");
        StringAssert.Contains(endpoint, "ExecuteMemoryRecallTool(request.Body, ownerKey)");
        StringAssert.Contains(endpoint, "enableMemoryRecall: false");
        StringAssert.Contains(endpoint, "writer.WriteBoolean(\"heirowllm_forge_clean_conclusion\", true)");
        StringAssert.Contains(endpoint, "CleanChickenChaserConclusion(parsed[\"reply\"]?.ToString()");
        StringAssert.Contains(endpoint, "(?:root|message|response|answer|output)");
        StringAssert.Contains(proxy, "!IsHeirowForgeCleanConclusionRequest(currentRequestJson)");
        StringAssert.Contains(proxy, "[heirowForge clean conclusion]");
        StringAssert.Contains(proxy, "state the answer itself instead of describing which memory");
        StringAssert.Contains(proxy, "BuildProxyToolFinalAnswerRequest(continuationRequest)");
        StringAssert.Contains(endpoint, "ResolveChickenChaserTextModel(settings.Model");
        StringAssert.Contains(endpoint, "!model.supportsAudioGeneration");
        StringAssert.Contains(endpoint, "SelectRelevantChatMemories(GetChatMemories(ownerKey), query, topic, take)");
        Assert.IsFalse(endpoint.Contains("TryBuildGroundedChickenChaserIdentityReply", StringComparison.Ordinal));
        Assert.IsFalse(endpoint.Contains("Your saved heirowLLM memory identifies you as", StringComparison.Ordinal));
        StringAssert.Contains(agentBuilder, "new JsonObject { [\"role\"] = \"system\", [\"content\"] = prompt }");
        StringAssert.Contains(markdownMessage, "IsReadOnlyCaretVisible = true");
        StringAssert.Contains(markdownMessage, "Command = ApplicationCommands.Copy");
        StringAssert.Contains(markdownMessage, "ChickenChaserMarkdownRenderer.CreateDocument");
        StringAssert.Contains(markdownMessage, "new Bold(new Run");
        StringAssert.Contains(markdownMessage, "CreateTable(lines, ref index)");
        StringAssert.Contains(markdownMessage, "string.Join('\\n', paragraphLines)");
        StringAssert.Contains(markdownMessage, "target.Add(new LineBreak())");
        StringAssert.Contains(web, "function addChickenThinking()");
        StringAssert.Contains(web, "field-sizing:content");
        StringAssert.Contains(web, "new ResizeObserver(scheduleChickenViewportFit)");
        StringAssert.Contains(web, "thinking.label.textContent='Conclusion'");
        StringAssert.Contains(web, "thinking.throbber.textContent='✓'");
        Assert.IsFalse(web.Contains("thinking.label.textContent='Thought process'", StringComparison.Ordinal));
        StringAssert.Contains(window, "private const double CompactAvatarWidth = 32");
        StringAssert.Contains(window, "private void ToggleChat()");
        StringAssert.Contains(window, "private void ToggleCompactAvatar()");
        StringAssert.Contains(window, "private void PositionAvatarForChat(ChickenChaserChatWindow chat)");
        StringAssert.Contains(window, "private void RestoreIdleAvatarPosition()");
        StringAssert.Contains(window, "double freeLeft = chat.Left - workArea.Left");
        StringAssert.Contains(window, "workArea.Bottom - Height + AvatarFootInset");
        StringAssert.Contains(web, "function captureChickenIdlePosition()");
        StringAssert.Contains(web, "function positionChickenOpenLayout()");
        StringAssert.Contains(web, "id=\"chickenAttach\"");
        StringAssert.Contains(web, "async function addChickenAttachments(files)");
        StringAssert.Contains(web, "attachments:item.attachments");
        StringAssert.Contains(web, "event.clipboardData?.files");
        StringAssert.Contains(web, "chicken-side-left");
        StringAssert.Contains(web, "chicken-side-right");
        StringAssert.Contains(web, "chickenChaser.idleCaptured=false");
        StringAssert.Contains(window, "Color.FromArgb(84, 7, 11, 18)");
        StringAssert.Contains(web, "class=\"chicken-chaser-glow\"");
        StringAssert.Contains(web, "radial-gradient(ellipse at 50% 48%");
        StringAssert.Contains(web, "class=\"chicken-chaser-restore\"");
        StringAssert.Contains(web, "opacity:.5;cursor:pointer");
        StringAssert.Contains(web, "chickenEls.restore.onclick=()=>setChickenHidden(false,true)");
        StringAssert.Contains(web, "addEventListener('dblclick'");
    }

    [TestMethod]
    public void ModelInventoryRefresh_LeavesFilesystemDiscoveryOffTheUiThread()
    {
        string root = FindRepositoryRoot();
        string manager = File.ReadAllText(Path.Combine(root, "LlmRuntime.Wpf", "ModelsManagerControl.xaml.cs"));

        StringAssert.Contains(manager, "_modelInventoryRefreshActive");
        StringAssert.Contains(manager, "Task.Run<IReadOnlyList<(LlmModelInfo, bool)>>");
        StringAssert.Contains(manager, "Task.Run(() => BuildModelStorageSummary");
        StringAssert.Contains(manager, "await Task.WhenAll(localInventoryTask, runtimeInventoryTask, hardwareTask, storageSummaryTask)");
        Assert.IsFalse(manager.Contains("UpdateModelStorageSummary();", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkstationTheme_UsesDarkNavyAcrylicWithBoundedOpacity()
    {
        string root = FindRepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "heirowLLM", "App.xaml"));
        string main = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml"));
        string workstation = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));

        StringAssert.Contains(app, "x:Key=\"WindowBackgroundBrush\" Color=\"#54070B12\"");
        StringAssert.Contains(app, "x:Key=\"PanelBrush\" Color=\"#54101A2A\"");
        StringAssert.Contains(app, "x:Key=\"TextBrush\" Color=\"#CCF1F7FF\"");
        StringAssert.Contains(app, "x:Key=\"PanelBorderBrush\" Color=\"#CC4E5E74\"");
        StringAssert.Contains(app, "x:Key=\"ControlBrush\" Color=\"#54152333\"");
        StringAssert.Contains(app, "x:Key=\"ControlBorderBrush\" Color=\"#CC4E6A8B\"");
        StringAssert.Contains(main, "<GradientStop Color=\"#54070B12\" Offset=\"0\"/>");
        StringAssert.Contains(workstation, "AccentState = AccentState.AccentEnableAcrylicBlurBehind");
        StringAssert.Contains(workstation, "GradientColor = unchecked((int)0x54120B07)");
    }

    [TestMethod]
    public void WorkstationOptions_ExposeDefaultOnDwmBlurGlassCompatibility()
    {
        string root = FindRepositoryRoot();
        string workstation = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));
        string options = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.WorkstationOptions.cs"));
        string web = File.ReadAllText(Path.Combine(root, "SocketJack", "html", "heirowLLMWebChat.html"));

        StringAssert.Contains(workstation, "public bool DwmBlurGlassEnabled { get; set; } = true;");
        StringAssert.Contains(workstation, "if (_settings?.DwmBlurGlassEnabled == false)");
        StringAssert.Contains(workstation, "TryDisableAeroBlur(handle)");
        StringAssert.Contains(options, "DWMBlurGlass effects");
        StringAssert.Contains(options, "Category = isDreamModel ? \"Dream and Checks\" : isDwmBlurGlass ? \"Appearance\"");
        StringAssert.Contains(options, "https://github.com/Maplespe/DWMBlurGlass");
        StringAssert.Contains(web, "learnMore.textContent = 'Official project'");
        StringAssert.Contains(web, "learnMore.rel = 'noopener noreferrer'");
    }

    [TestMethod]
    public void WorkstationLogs_UseHeirowTemporaryStorageAndExposeHighFidelityDiagnostics()
    {
        string root = FindRepositoryRoot();
        string networkOptions = File.ReadAllText(Path.Combine(root, "SocketJack", "Net", "NetworkOptions.cs"));
        string httpServer = File.ReadAllText(Path.Combine(root, "SocketJack", "Net", "HttpServer.cs"));
        string userData = File.ReadAllText(Path.Combine(root, "heirowLLM", "HeirowLlmUserData.cs"));
        string workstation = File.ReadAllText(Path.Combine(root, "heirowLLM", "MainWindow.xaml.cs"));
        string endpoint = File.ReadAllText(Path.Combine(root, "SocketJack.LlmCore", "Proxy", "heirowLLM.cs"));
        string web = File.ReadAllText(Path.Combine(root, "SocketJack", "html", "heirowLLMWebChat.html"));

        StringAssert.Contains(networkOptions, "Path.Combine(Path.GetTempPath(), \"SocketJack\", \"Logs\")");
        Assert.IsFalse(networkOptions.Contains("C:\\HeirowLlm\\Logs", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(httpServer, "Path.Combine(Path.GetTempPath(), \"SocketJack\", \"Logs\")");
        StringAssert.Contains(endpoint, "Path.Combine(Path.GetTempPath(), \"SocketJack\", \"heirowLLM\", \"Logs\")");
        StringAssert.Contains(endpoint, "networkOptions.HttpAccessLogDirectory = TemporaryLogsDirectory");
        StringAssert.Contains(userData, "MigrateLegacyLogs()");
        StringAssert.Contains(userData, "@\"C:\\LmVsProxy\\Logs\"");
        StringAssert.Contains(workstation, "PromptForTemporaryLogCleanupIfNeeded");
        StringAssert.Contains(workstation, "1024L * 1024L * 1024L");
        StringAssert.Contains(endpoint, "ReadDiagnosticLogEntries(500)");
        StringAssert.Contains(endpoint, "raw = RedactSensitiveLogText(line)");
        StringAssert.Contains(endpoint, "\"/api/diagnostics/logs/clear\"");
        StringAssert.Contains(web, "id=\"diagnosticsLogsTab\"");
        StringAssert.Contains(web, "function renderDiagnosticLogs(logs, storage)");
        StringAssert.Contains(web, "Raw redacted JSON");
        StringAssert.Contains(web, "Clear temporary logs");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SocketJack.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("SocketJack.sln was not found above the test output directory.");
    }
}
