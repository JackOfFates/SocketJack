using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Console.ForegroundColor = ConsoleColor.Green;
Console.OutputEncoding = Encoding.UTF8;

PublisherOptions options = PublisherOptions.Parse(args);
Terminal.StartSessionLog();
Terminal.Banner();
Terminal.Line("boot", "SocketJack Update Publisher online");
Terminal.Line("target", options.ServerUrl);
Terminal.Line("log", Terminal.LogPath);

using var session = new PublisherSession(options);
while (true) {
    try {
        await session.AuthenticateAsync(forceRefresh: false);
        break;
    } catch (Exception ex) {
        Terminal.Error("auth", ex.Message);
        string action = Terminal.Prompt("auth", "Retry login or quit? [Y/q]: ").Trim().ToLowerInvariant();
        if (action == "q" || action == "quit") {
            Terminal.Line("stop", "publisher stopped before login");
            Console.ResetColor();
            return 0;
        }
        options.Password = "";
    }
}

List<PublishJob> jobs = options.ResolveJobs();
foreach (PublishJob job in jobs) {
    while (true) {
        try {
            await PublishAsync(session, job, options.ChunkSize, options.ProtectNewerServerFiles, options.AllowLargeWorkstationPayload);
            break;
        } catch (Exception ex) {
            Terminal.Error("fail", ex.Message);
            string action = Terminal.Prompt("next", "Retry, re-login, skip this channel, or quit? [r/l/s/q]: ").Trim().ToLowerInvariant();
            if (action == "l" || action == "login") {
                await session.AuthenticateAsync(forceRefresh: true);
                continue;
            }
            if (action == "s" || action == "skip") {
                Terminal.Line("skip", job.Channel);
                break;
            }
            if (action == "q" || action == "quit") {
                Terminal.Line("stop", "publisher stopped by user");
                Console.ResetColor();
                return 0;
            }
            Terminal.Line("retry", job.Channel);
        }
    }
}

Terminal.Line("done", "all requested channels handled");
Console.ResetColor();
return 0;

static async Task PublishAsync(PublisherSession session, PublishJob job, int chunkSize, bool protectNewerServerFiles, bool allowLargeWorkstationPayload) {
    Terminal.Line("scan", job.Channel + " <- " + job.SourceDirectory);
    if (!string.IsNullOrWhiteSpace(job.PublicPath) || !string.IsNullOrWhiteSpace(job.ServerPath))
        Terminal.Line("serve", job.Channel + " url=" + (string.IsNullOrWhiteSpace(job.PublicPath) ? "-" : job.PublicPath) + " serverPath=" + (string.IsNullOrWhiteSpace(job.ServerPath) ? "-" : job.ServerPath));
    if (!Directory.Exists(job.SourceDirectory))
        throw new DirectoryNotFoundException("Source folder not found: " + job.SourceDirectory);

    List<UpdateFile> files = LoadPublishFiles(job);

    ServerManifest serverManifest = await ServerManifest.LoadAsync(session, job);
    bool serverPathMismatch = await DetectServerPathMismatchAsync(session, job, serverManifest.Root);
    if (serverPathMismatch)
        serverManifest = new ServerManifest();
    Terminal.Line("meta", serverManifest.Available
        ? serverManifest.Count.ToString(CultureInfo.InvariantCulture) + " server hashes loaded from metadata"
        : "server hash metadata missing; changed-file plan will upload every local file");

    List<UpdateFile> filesToUpload = new();
    HashSet<string> localPaths = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    int sameHashSkipped = 0;
    int olderSkipped = 0;
    int olderApproved = 0;
    int clockSkewApproved = 0;
    int changedNewer = 0;
    int newFiles = 0;
    OlderFileDecision olderFilePolicy = OlderFileDecision.Ask;

    foreach (UpdateFile file in files) {
        if (!serverManifest.TryGet(file.RelativePath, out ServerManifestFile? serverFile) || serverFile == null) {
            filesToUpload.Add(file);
            newFiles++;
            continue;
        }

        if (!string.IsNullOrWhiteSpace(serverFile.Sha256) && string.Equals(serverFile.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) {
            sameHashSkipped++;
            continue;
        }

        if (serverFile.TryGetLastWriteUtc(out DateTimeOffset serverLastWriteUtc) &&
            ServerTimestampIsNewerThanPublisher(serverLastWriteUtc, file.LastWriteUtc)) {
            if (!protectNewerServerFiles || ShouldAutoReplaceBecauseServerTimestampIsUnreliable(serverManifest, serverLastWriteUtc)) {
                filesToUpload.Add(file);
                clockSkewApproved++;
                continue;
            }

            OlderFileDecision decision = olderFilePolicy == OlderFileDecision.Ask
                ? PromptReplaceOlderFile(file, FilePublishResult.Skip("publisher-not-newer", serverLastWriteUtc.ToString("O", CultureInfo.InvariantCulture), file.LastWriteUtc.ToString("O", CultureInfo.InvariantCulture)))
                : olderFilePolicy;

            if (decision == OlderFileDecision.ReplaceAll || decision == OlderFileDecision.SkipAll)
                olderFilePolicy = decision;

            if (decision == OlderFileDecision.Replace || decision == OlderFileDecision.ReplaceAll) {
                filesToUpload.Add(file);
                olderApproved++;
            } else {
                olderSkipped++;
            }
            continue;
        }

        filesToUpload.Add(file);
        changedNewer++;
    }

    int deletedServerFiles = serverManifest.Available
        ? serverManifest.Files.Keys.Count(path => !localPaths.Contains(path))
        : 0;
    long totalBytes = filesToUpload.Sum(file => file.Length);
    Terminal.Line("plan", filesToUpload.Count.ToString(CultureInfo.InvariantCulture) + " files to upload, " + Terminal.Bytes(totalBytes) +
                          "; same-hash " + sameHashSkipped.ToString(CultureInfo.InvariantCulture) +
                          ", newer " + changedNewer.ToString(CultureInfo.InvariantCulture) +
                          ", new " + newFiles.ToString(CultureInfo.InvariantCulture) +
                          ", time-fix " + clockSkewApproved.ToString(CultureInfo.InvariantCulture) +
                          ", older-ok " + olderApproved.ToString(CultureInfo.InvariantCulture) +
                          ", older-skip " + olderSkipped.ToString(CultureInfo.InvariantCulture) +
                          ", stale-server " + deletedServerFiles.ToString(CultureInfo.InvariantCulture));

    bool shouldPruneWorkstationPayload = IsJackLlmWorkstationChannel(job.Channel);
    const long maxWorkstationUploadBytes = 512L * 1024L * 1024L;
    if (shouldPruneWorkstationPayload && totalBytes > maxWorkstationUploadBytes && !allowLargeWorkstationPayload) {
        throw new InvalidOperationException(job.Channel + " would upload " + Terminal.Bytes(totalBytes) +
            ". Refusing the large JackLLM Workstation payload; run the MasterList channel only, trim the JackLLM artifact, or pass --allow-large-workstation-payload intentionally.");
    }

    if (filesToUpload.Count == 0 && deletedServerFiles == 0 && !shouldPruneWorkstationPayload) {
        Terminal.Line("done", job.Channel + " already matches server metadata");
        return;
    }
    if (filesToUpload.Count == 0 && deletedServerFiles == 0 && shouldPruneWorkstationPayload)
        Terminal.Line("prune", job.Channel + " metadata matches; completing a zero-byte prune to remove non-payload files");

    var beginBody = new Dictionary<string, object> {
        ["channel"] = job.Channel,
        ["client"] = "SocketJack.Update.Publisher",
        ["updateDirectory"] = job.ServerPath,
        ["files"] = files.Select(file => file.RelativePath).ToArray()
    };
    AddJobChannelMetadata(beginBody, job);
    using JsonDocument begin = await session.PostJsonAsync("api/update/publish/begin", beginBody);
    ValidateServerPath(job, PublisherJson.Text(begin.RootElement, "updateDirectory"), "begin", requireServerPath: serverPathMismatch);
    string uploadId = PublisherJson.Text(begin.RootElement, "uploadId");
    if (string.IsNullOrWhiteSpace(uploadId)) {
        Terminal.Json("begin", begin.RootElement);
        throw new InvalidOperationException("Update server did not return an upload id.");
    }
    bool useBinaryArchiveChunks = ServerSupportsArchiveBinaryChunks(begin.RootElement);

    long handledBytes = 0;
    long uploadedBytes = 0;
    long serverSkippedBytes = 0;
    int uploadedFiles = 0;
    int serverSkippedFiles = 0;
    string archivePath = "";
    bool completeStarted = false;
    bool archiveUploadStarted = false;
    try {
        if (filesToUpload.Count > 0) {
            archivePath = CreatePublishArchive(job.Channel, filesToUpload);
            var archiveInfo = new FileInfo(archivePath);
            string archiveSha256 = ComputeSha256Hex(archivePath);
            Terminal.Line("zip", filesToUpload.Count.ToString(CultureInfo.InvariantCulture) + " files packed, " + Terminal.Bytes(totalBytes) + " -> " + Terminal.Bytes(archiveInfo.Length));
            archiveUploadStarted = true;
            await UploadArchiveAsync(session, uploadId, archivePath, archiveSha256, filesToUpload.Count, totalBytes, chunkSize, useBinaryArchiveChunks, (uploadedChunkBytes, archiveBytesDone, archiveBytesTotal) => {
                handledBytes += uploadedChunkBytes;
                Terminal.Bar(Path.GetFileName(archivePath), handledBytes, Math.Max(1, archiveInfo.Length), archiveBytesDone, archiveBytesTotal, 24, "zip-upload");
            });
            uploadedFiles = filesToUpload.Count;
            uploadedBytes = totalBytes;
        }

        completeStarted = true;
        using JsonDocument complete = await CompletePublishAsync(session, job, uploadId, uploadedFiles, uploadedBytes, serverSkippedFiles, serverSkippedBytes);
        ValidateServerPath(job, FirstNonEmpty(PublisherJson.Text(complete.RootElement, "updateDirectory"), ManifestRoot(complete.RootElement)), "complete");
        Terminal.Json("complete", complete.RootElement);
        Terminal.Line("push", job.Channel + " uploaded " + uploadedFiles.ToString(CultureInfo.InvariantCulture) + " files, metadata-skipped " + sameHashSkipped.ToString(CultureInfo.InvariantCulture) + ", server-skipped " + serverSkippedFiles.ToString(CultureInfo.InvariantCulture));
    } catch (PublishUploadAbortedException) {
        throw;
    } catch (Exception ex) {
        if (!completeStarted) {
            if (archiveUploadStarted) {
                Terminal.Line("hold", "upload failed before complete; remote staging was kept for same-upload retry diagnostics: " + uploadId);
            } else {
                await AbortPublishAsync(session, uploadId);
            }
        } else {
            if (await TryVerifyCompletedPublishAsync(session, job, files, uploadId, ex)) {
                Terminal.Line("push", job.Channel + " verified on server after lost complete response; uploaded " + uploadedFiles.ToString(CultureInfo.InvariantCulture) + " files, metadata-skipped " + sameHashSkipped.ToString(CultureInfo.InvariantCulture) + ", server-skipped " + serverSkippedFiles.ToString(CultureInfo.InvariantCulture));
                return;
            }
            Terminal.Line("hold", "complete failed after upload; remote staging was kept for complete retry diagnostics: " + uploadId);
        }
        throw;
    } finally {
        if (!string.IsNullOrWhiteSpace(archivePath)) {
            TryDeleteFile(archivePath);
            Terminal.Line("clean", "local archive removed");
        }
    }
}

static async Task<bool> TryVerifyCompletedPublishAsync(PublisherSession session, PublishJob job, IReadOnlyList<UpdateFile> expectedFiles, string uploadId, Exception completeException) {
    Terminal.Error("complete", "response was not confirmed: " + completeException.Message);
    Terminal.Line("verify", "checking refreshed server metadata before treating " + uploadId + " as failed");

    const int maxAttempts = 10;
    string lastReason = "server metadata was not available";
    for (int attempt = 1; attempt <= maxAttempts; attempt++) {
        if (attempt > 1) {
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(20, 2 * attempt));
            Terminal.Line("verify", "waiting " + delay.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) + "s before metadata check " + attempt.ToString(CultureInfo.InvariantCulture) + "/" + maxAttempts.ToString(CultureInfo.InvariantCulture));
            await Task.Delay(delay);
        }

        ServerManifest manifest = await ServerManifest.LoadAsync(session, job);
        if (!manifest.Available) {
            lastReason = "server metadata was not available";
            continue;
        }

        try {
            ValidateServerPath(job, manifest.Root, "complete verification");
        } catch (Exception pathEx) {
            lastReason = pathEx.Message;
            break;
        }

        if (ManifestMatchesExpectedFiles(manifest, expectedFiles, out lastReason)) {
            Terminal.Line("verify", "server metadata matches local payload (" + manifest.Count.ToString(CultureInfo.InvariantCulture) + " files)");
            return true;
        }
    }

    Terminal.Line("verify", "server metadata did not confirm completion: " + lastReason);
    return false;
}

static bool ManifestMatchesExpectedFiles(ServerManifest manifest, IReadOnlyList<UpdateFile> expectedFiles, out string reason) {
    var expectedByPath = expectedFiles.ToDictionary(file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    foreach (UpdateFile expected in expectedByPath.Values) {
        if (!manifest.TryGet(expected.RelativePath, out ServerManifestFile? serverFile) || serverFile == null) {
            reason = "missing " + expected.RelativePath;
            return false;
        }

        if (serverFile.Length != expected.Length) {
            reason = expected.RelativePath + " length mismatch; expected " + expected.Length.ToString(CultureInfo.InvariantCulture) + " but server has " + serverFile.Length.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        if (string.IsNullOrWhiteSpace(serverFile.Sha256) || !string.Equals(serverFile.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)) {
            reason = expected.RelativePath + " hash mismatch";
            return false;
        }
    }

    foreach (string serverPath in manifest.Files.Keys) {
        if (!expectedByPath.ContainsKey(serverPath)) {
            reason = "stale server file still present: " + serverPath;
            return false;
        }
    }

    reason = "";
    return true;
}

static List<UpdateFile> LoadPublishFiles(PublishJob job) {
    var files = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
    foreach (string path in Directory.EnumerateFiles(job.SourceDirectory, "*", SearchOption.AllDirectories)) {
        UpdateFile? file = UpdateFile.TryCreate(job.SourceDirectory, path, job.Channel);
        if (file != null)
            files[file.RelativePath] = file;
    }

    foreach (ExtraPublishFile extra in job.ExtraFiles) {
        string fullPath = Path.GetFullPath(extra.FullPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Extra publish file was not found: " + fullPath, fullPath);

        string relativePath = string.IsNullOrWhiteSpace(extra.RelativePath)
            ? Path.GetFileName(fullPath)
            : extra.RelativePath.Trim().Replace('\\', '/').Trim('/');
        UpdateFile? file = UpdateFile.TryCreateExtra(fullPath, relativePath, job.Channel);
        if (file == null)
            throw new InvalidOperationException("Extra publish file is not allowed in the update payload: " + relativePath);

        files[file.RelativePath] = file;
        Terminal.Line("extra", file.RelativePath + " <- " + fullPath);
    }

    if (job.IncludePaths.Count > 0) {
        foreach (string includePath in job.IncludePaths) {
            string normalized = NormalizePublishRelativePath(includePath);
            if (!string.IsNullOrWhiteSpace(normalized) && !files.ContainsKey(normalized) && !files.Keys.Any(path => IsIncludedPublishPath(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalized })))
                Terminal.Line("skip", "include path missing from source: " + normalized);
        }

        files = files
            .Where(item => IsIncludedPublishPath(item.Key, job.IncludePaths))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    return files.Values
        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static bool IsIncludedPublishPath(string relativePath, HashSet<string> includePaths) {
    relativePath = NormalizePublishRelativePath(relativePath);
    foreach (string includePath in includePaths) {
        string normalizedInclude = NormalizePublishRelativePath(includePath);
        if (relativePath.Equals(normalizedInclude, StringComparison.OrdinalIgnoreCase))
            return true;
        if (relativePath.StartsWith(normalizedInclude.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

static string NormalizePublishRelativePath(string path) {
    return (path ?? "").Trim().Replace('\\', '/').Trim('/');
}

static bool IsJackLlmWorkstationChannel(string channel) {
    string id = (channel ?? "").Trim().ToLowerInvariant();
    return id.Equals("jackllm", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("jackllm-companion", StringComparison.OrdinalIgnoreCase);
}

static void AddJobChannelMetadata(Dictionary<string, object> body, PublishJob job) {
    if (!string.IsNullOrWhiteSpace(job.DisplayName))
        body["displayName"] = job.DisplayName;
    if (!string.IsNullOrWhiteSpace(job.PublicPath))
        body["publicPath"] = job.PublicPath;
    if (!string.IsNullOrWhiteSpace(job.ManagedProcessName))
        body["managedProcessName"] = job.ManagedProcessName;
    if (!string.IsNullOrWhiteSpace(job.ManagedExecutablePath))
        body["managedExecutablePath"] = job.ManagedExecutablePath;
    if (job.AutoStartAfterUpdate.HasValue)
        body["autoStartAfterUpdate"] = job.AutoStartAfterUpdate.Value;
}

static async Task<JsonDocument> CompletePublishAsync(PublisherSession session, PublishJob job, string uploadId, int fileCount, long byteCount, int skippedFileCount, long skippedByteCount) {
    var approvedLockingProcessIds = new HashSet<int>();
    while (true) {
        try {
            var body = new Dictionary<string, object> {
                ["uploadId"] = uploadId,
                ["updateDirectory"] = job.ServerPath,
                ["fileCount"] = fileCount,
                ["byteCount"] = byteCount,
                ["skippedFileCount"] = skippedFileCount,
                ["skippedByteCount"] = skippedByteCount
            };
            AddJobChannelMetadata(body, job);
            if (approvedLockingProcessIds.Count > 0)
                body["killLockingProcessIds"] = approvedLockingProcessIds.OrderBy(id => id).ToArray();

            return await session.PostJsonAsync("api/update/publish/complete", body);
        } catch (PublisherHttpException ex) {
            Terminal.Error("complete", ex.Message);
            if (TryPromptForLockingProcessKill(ex.ResponseBody, approvedLockingProcessIds, out List<int> newlyApprovedProcessIds)) {
                foreach (int processId in newlyApprovedProcessIds)
                    approvedLockingProcessIds.Add(processId);
                Terminal.Line("locks", "approved server stop for pid(s): " + string.Join(", ", newlyApprovedProcessIds));
                continue;
            }

            string action = Terminal.Prompt("complete", "Retry complete, re-login, abort upload, or quit? [r/l/a/q]: ").Trim().ToLowerInvariant();
            if (action == "l" || action == "login") {
                await session.AuthenticateAsync(forceRefresh: true);
                continue;
            }
            if (action == "a" || action == "abort") {
                await AbortPublishAsync(session, uploadId);
                throw new PublishUploadAbortedException(uploadId);
            }
            if (action == "q" || action == "quit")
                throw;

            Terminal.Line("retry", "complete " + uploadId);
        }
    }
}

static void ValidateServerPath(PublishJob job, string serverPath, string phase, bool requireServerPath = false) {
    if (string.IsNullOrWhiteSpace(job.ServerPath))
        return;

    if (string.IsNullOrWhiteSpace(serverPath)) {
        if (requireServerPath)
            throw new InvalidOperationException("Update server did not confirm the requested update directory for " + job.Channel + " during " + phase + ".");
        return;
    }

    if (SamePath(job.ServerPath, serverPath))
        return;

    throw new InvalidOperationException(
        "Server update directory mismatch during " + phase + " for " + job.Channel + ": expected " +
        job.ServerPath + " but server reported " + serverPath + ".");
}

static async Task<bool> DetectServerPathMismatchAsync(PublisherSession session, PublishJob job, string manifestRoot) {
    if (string.IsNullOrWhiteSpace(job.ServerPath))
        return false;

    if (!string.IsNullOrWhiteSpace(manifestRoot)) {
        if (!SamePath(job.ServerPath, manifestRoot)) {
            Terminal.Line("path", "server metadata root=" + manifestRoot + "; requesting " + job.ServerPath);
            return true;
        }
        return false;
    }

    JsonDocument? document = await session.GetJsonOrNullAsync("api/update/channels");
    if (document == null)
        return false;

    using (document) {
        if (!TryGetJsonProperty(document.RootElement, "channels", out JsonElement channels) || channels.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement channel in channels.EnumerateArray()) {
            string channelId = FirstJsonText(channel, "id", "channel", "channelId");
            if (!string.Equals(channelId, job.Channel, StringComparison.OrdinalIgnoreCase))
                continue;

            string updateDirectory = FirstJsonText(channel, "updateDirectory", "serverPath", "targetDirectory");
            if (!string.IsNullOrWhiteSpace(updateDirectory) && !SamePath(job.ServerPath, updateDirectory)) {
                Terminal.Line("path", "server channel root=" + updateDirectory + "; requesting " + job.ServerPath);
                return true;
            }
            return false;
        }
    }

    return false;
}

static string ManifestRoot(JsonElement root) {
    if (TryGetJsonProperty(root, "manifest", out JsonElement manifest) && manifest.ValueKind == JsonValueKind.Object)
        return PublisherJson.Text(manifest, "root");
    return "";
}

static bool SamePath(string left, string right) {
    if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        return false;

    try {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    } catch {
        return string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}

static string FirstNonEmpty(params string[] values) {
    foreach (string value in values) {
        if (!string.IsNullOrWhiteSpace(value))
            return value;
    }
    return "";
}

static bool TryPromptForLockingProcessKill(string responseBody, HashSet<int> alreadyApprovedProcessIds, out List<int> approvedProcessIds) {
    approvedProcessIds = new List<int>();
    if (string.IsNullOrWhiteSpace(responseBody))
        return false;

    List<LockingProcessPromptInfo> processes;
    try {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        processes = ReadLockingProcessPromptInfos(document.RootElement)
            .Where(process => process.Id > 0 && process.CanStop && !alreadyApprovedProcessIds.Contains(process.Id))
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.Id)
            .ToList();
    } catch {
        return false;
    }

    if (processes.Count == 0)
        return false;

    Terminal.Line("locks", "server reports " + processes.Count.ToString(CultureInfo.InvariantCulture) + " process(es) using update files");
    foreach (LockingProcessPromptInfo process in processes.Take(8))
        Terminal.Line("", process.Summary);
    if (processes.Count > 8)
        Terminal.Line("", "... " + (processes.Count - 8).ToString(CultureInfo.InvariantCulture) + " more process(es) omitted");

    string action = Terminal.Prompt("locks", "Kill these server process(es) and restart queued apps after update? [y/N]: ").Trim().ToLowerInvariant();
    if (action != "y" && action != "yes")
        return false;

    approvedProcessIds = processes.Select(process => process.Id).ToList();
    return approvedProcessIds.Count > 0;
}

static List<LockingProcessPromptInfo> ReadLockingProcessPromptInfos(JsonElement root) {
    var processes = new List<LockingProcessPromptInfo>();
    AddLockingProcessPromptInfos(processes, root, "lockingProcessDetails");
    AddLockingProcessPromptInfos(processes, root, "probableLockingProcessDetails");
    if (processes.Count == 0) {
        AddLockingProcessPromptInfos(processes, root, "lockingProcesses");
        AddLockingProcessPromptInfos(processes, root, "probableLockingProcesses");
    }
    return processes;
}

static void AddLockingProcessPromptInfos(List<LockingProcessPromptInfo> processes, JsonElement root, string propertyName) {
    if (!TryGetJsonProperty(root, propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        return;

    foreach (JsonElement item in array.EnumerateArray()) {
        LockingProcessPromptInfo? process = item.ValueKind == JsonValueKind.Object
            ? ReadLockingProcessPromptInfo(item)
            : ReadLockingProcessPromptInfoFromText(JsonScalarText(item));
        if (process != null)
            processes.Add(process);
    }
}

static LockingProcessPromptInfo? ReadLockingProcessPromptInfo(JsonElement element) {
    int id = (int)JsonLong(element, "id", 0);
    string name = FirstJsonText(element, "name", "processName");
    string executablePath = FirstJsonText(element, "executablePath", "exe", "path");
    string display = FirstJsonText(element, "display", "label");
    bool canStop = JsonBool(element, "canStop", true);
    bool restartAfterUpdate = JsonBool(element, "restartAfterUpdate", false);
    if (id <= 0 && !string.IsNullOrWhiteSpace(display))
        id = ExtractProcessId(display);
    if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(display))
        name = ExtractProcessName(display);
    if (id <= 0)
        return null;
    return new LockingProcessPromptInfo {
        Id = id,
        Name = string.IsNullOrWhiteSpace(name) ? "process" : name,
        ExecutablePath = executablePath,
        CanStop = canStop,
        RestartAfterUpdate = restartAfterUpdate
    };
}

static LockingProcessPromptInfo? ReadLockingProcessPromptInfoFromText(string text) {
    int id = ExtractProcessId(text);
    if (id <= 0)
        return null;
    return new LockingProcessPromptInfo {
        Id = id,
        Name = ExtractProcessName(text),
        ExecutablePath = ExtractProcessExecutablePath(text),
        CanStop = true
    };
}

static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value) {
    if (element.ValueKind == JsonValueKind.Object) {
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                value = property.Value;
                return true;
            }
        }
    }

    value = default;
    return false;
}

static string FirstJsonText(JsonElement element, params string[] names) {
    foreach (string name in names) {
        string value = JsonText(element, name);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
    }
    return "";
}

static string JsonText(JsonElement element, string name) {
    return TryGetJsonProperty(element, name, out JsonElement value) ? JsonScalarText(value) : "";
}

static long JsonLong(JsonElement element, string name, long fallback) {
    string value = JsonText(element, name);
    return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;
}

static bool JsonBool(JsonElement element, string name, bool fallback) {
    string value = JsonText(element, name);
    return string.IsNullOrWhiteSpace(value) ? fallback : bool.TryParse(value, out bool parsed) ? parsed : fallback;
}

static string JsonScalarText(JsonElement value) {
    return value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? value.GetRawText()
            : value.ToString();
}

static int ExtractProcessId(string text) {
    if (string.IsNullOrWhiteSpace(text))
        return 0;

    int hash = text.IndexOf('#', StringComparison.Ordinal);
    if (hash < 0 || hash + 1 >= text.Length)
        return 0;

    int end = hash + 1;
    while (end < text.Length && char.IsDigit(text[end]))
        end++;
    string idText = text.Substring(hash + 1, end - hash - 1);
    return int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0;
}

static string ExtractProcessName(string text) {
    if (string.IsNullOrWhiteSpace(text))
        return "process";

    int hash = text.IndexOf('#', StringComparison.Ordinal);
    if (hash > 0)
        return text.Substring(0, hash).Trim();
    return "process";
}

static string ExtractProcessExecutablePath(string text) {
    if (string.IsNullOrWhiteSpace(text))
        return "";

    const string marker = " exe=";
    int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
        return "";

    start += marker.Length;
    int end = text.IndexOf(" baseDir=", start, StringComparison.OrdinalIgnoreCase);
    if (end < 0)
        end = text.Length;
    return text.Substring(start, end - start).Trim();
}

static async Task AbortPublishAsync(PublisherSession session, string uploadId) {
    try {
        await session.PostJsonAsync("api/update/publish/abort", new { uploadId });
        Terminal.Line("abort", uploadId);
    } catch (Exception abortEx) {
        Terminal.Error("abort", abortEx.Message);
    }
}

static string CreatePublishArchive(string channel, IReadOnlyList<UpdateFile> files) {
    string archivePath = Path.Combine(Path.GetTempPath(), "SocketJackUpdate-" + channel + "-" + Guid.NewGuid().ToString("N") + ".zip");
    using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)) {
        foreach (UpdateFile file in files) {
            ZipArchiveEntry entry = archive.CreateEntry(file.RelativePath.Replace('\\', '/'), CompressionLevel.Fastest);
            entry.LastWriteTime = file.LastWriteUtc;
            using Stream entryStream = entry.Open();
            using FileStream sourceStream = File.OpenRead(file.FullPath);
            sourceStream.CopyTo(entryStream);
        }
    }
    return archivePath;
}

static async Task UploadArchiveAsync(PublisherSession session, string uploadId, string archivePath, string sha256, int fileCount, long byteCount, int chunkSize, bool useBinaryChunks, Action<long, long, long> onUploadedChunk) {
    if (!useBinaryChunks) {
        Terminal.Line("compat", "server did not advertise archive-binary chunks; using JSON chunks");
        await UploadArchiveJsonChunksAsync(session, uploadId, archivePath, sha256, fileCount, byteCount, Math.Min(chunkSize, 4 * 1024 * 1024), onUploadedChunk);
        return;
    }

    try {
        await UploadArchiveBinaryChunksAsync(session, uploadId, archivePath, sha256, fileCount, byteCount, chunkSize, onUploadedChunk);
    } catch (PublisherHttpException ex) when (LooksLikeLegacyArchiveBinaryEndpoint(ex)) {
        Terminal.Line("compat", "server archive-binary endpoint is legacy; falling back to JSON chunks");
        await UploadArchiveJsonChunksAsync(session, uploadId, archivePath, sha256, fileCount, byteCount, Math.Min(chunkSize, 4 * 1024 * 1024), onUploadedChunk);
    }
}

static bool ServerSupportsArchiveBinaryChunks(JsonElement beginResponse) {
    if (JsonBool(beginResponse, "archiveBinaryChunks", false) ||
        JsonBool(beginResponse, "archiveBinaryChunked", false))
        return true;

    return TryGetJsonProperty(beginResponse, "capabilities", out JsonElement capabilities) &&
           (JsonBool(capabilities, "archiveBinaryChunks", false) ||
            JsonBool(capabilities, "archiveBinaryChunked", false));
}

static async Task UploadArchiveBinaryChunksAsync(PublisherSession session, string uploadId, string archivePath, string sha256, int fileCount, long byteCount, int chunkSize, Action<long, long, long> onUploadedChunk) {
    var info = new FileInfo(archivePath);
    chunkSize = Math.Clamp(chunkSize, 256 * 1024, 32 * 1024 * 1024);
    Terminal.Line("buffer", "archive upload binary chunk size " + Terminal.Bytes(chunkSize));

    long offset = 0;
    byte[] buffer = new byte[chunkSize];
    using FileStream stream = File.OpenRead(archivePath);
    while (offset < info.Length) {
        int requested = (int)Math.Min(buffer.Length, info.Length - offset);
        int read = await stream.ReadAsync(buffer.AsMemory(0, requested));
        if (read <= 0)
            throw new EndOfStreamException("Archive upload ended before all bytes were read.");

        long chunkOffset = offset;
        offset += read;
        bool finalChunk = offset >= info.Length;
        await SendArchiveChunkWithRetryAsync(
            session,
            uploadId,
            chunkOffset,
            read,
            info.Length,
            () => session.PostBinaryArchiveChunkAsync(uploadId, chunkOffset, info.Length, finalChunk, finalChunk ? sha256 : "", finalChunk ? fileCount : 0, finalChunk ? byteCount : 0, buffer, read));
        onUploadedChunk(read, offset, info.Length);
    }
}

static async Task UploadArchiveJsonChunksAsync(PublisherSession session, string uploadId, string archivePath, string sha256, int fileCount, long byteCount, int chunkSize, Action<long, long, long> onUploadedChunk) {
    var info = new FileInfo(archivePath);
    chunkSize = Math.Clamp(chunkSize, 64 * 1024, 4 * 1024 * 1024);
    Terminal.Line("buffer", "archive upload JSON chunk size " + Terminal.Bytes(chunkSize));

    long offset = 0;
    byte[] buffer = new byte[chunkSize];
    using FileStream stream = File.OpenRead(archivePath);
    while (offset < info.Length) {
        int requested = (int)Math.Min(buffer.Length, info.Length - offset);
        int read = await stream.ReadAsync(buffer.AsMemory(0, requested));
        if (read <= 0)
            throw new EndOfStreamException("Archive upload ended before all bytes were read.");

        long chunkOffset = offset;
        offset += read;
        bool finalChunk = offset >= info.Length;
        await SendArchiveChunkWithRetryAsync(
            session,
            uploadId,
            chunkOffset,
            read,
            info.Length,
            () => session.PostJsonAsync("api/update/publish/archive", new {
                uploadId,
                offset = chunkOffset,
                totalLength = info.Length,
                finalChunk,
                sha256 = finalChunk ? sha256 : "",
                fileCount = finalChunk ? fileCount : 0,
                byteCount = finalChunk ? byteCount : 0,
                contentBase64 = Convert.ToBase64String(buffer, 0, read)
            }));
        onUploadedChunk(read, offset, info.Length);
    }
}

static async Task SendArchiveChunkWithRetryAsync(PublisherSession session, string uploadId, long chunkOffset, int chunkBytes, long totalLength, Func<Task<JsonDocument>> sendAsync) {
    const int automaticRetries = 5;
    int attempt = 0;
    while (true) {
        try {
            using JsonDocument response = await sendAsync();
            return;
        } catch (PublisherHttpException ex) when (LooksLikeAlreadyAcceptedArchiveChunk(ex, chunkOffset, chunkBytes)) {
            Terminal.Line("retry", "server already has chunk ending at " + Terminal.Bytes(chunkOffset + chunkBytes) + "; continuing");
            return;
        } catch (Exception ex) when (IsRetryableUploadException(ex)) {
            attempt++;
            if (attempt <= automaticRetries) {
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                Terminal.Error("retry", "upload chunk " + Terminal.Bytes(chunkOffset) + "-" + Terminal.Bytes(chunkOffset + chunkBytes) + " failed: " + ex.Message);
                Terminal.Line("retry", "waiting " + delay.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) + "s before retry " + attempt.ToString(CultureInfo.InvariantCulture) + "/" + automaticRetries.ToString(CultureInfo.InvariantCulture));
                await Task.Delay(delay);
                continue;
            }

            string action = Terminal.Prompt("upload", "Retry this chunk, re-login, abort upload, or quit? [r/l/a/q]: ").Trim().ToLowerInvariant();
            if (action == "l" || action == "login") {
                await session.AuthenticateAsync(forceRefresh: true);
                attempt = 0;
                continue;
            }
            if (action == "a" || action == "abort") {
                await AbortPublishAsync(session, uploadId);
                throw new PublishUploadAbortedException(uploadId);
            }
            if (action == "q" || action == "quit")
                throw new IOException("Upload stopped at " + Terminal.Bytes(chunkOffset) + " of " + Terminal.Bytes(totalLength) + ".", ex);

            attempt = 0;
        }
    }
}

static bool IsRetryableUploadException(Exception ex) {
    if (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        return true;
    if (ex is PublisherHttpException http) {
        int code = (int)http.StatusCode;
        return http.StatusCode == HttpStatusCode.RequestTimeout ||
               http.StatusCode == (HttpStatusCode)429 ||
               code == 500 ||
               http.StatusCode == HttpStatusCode.BadGateway ||
               http.StatusCode == HttpStatusCode.ServiceUnavailable ||
               http.StatusCode == HttpStatusCode.GatewayTimeout;
    }
    return false;
}

static bool LooksLikeLegacyArchiveBinaryEndpoint(PublisherHttpException ex) {
    return ex.StatusCode == HttpStatusCode.NotFound ||
           (ex.StatusCode == HttpStatusCode.Conflict &&
            ex.ResponseBody.IndexOf("Uploaded archive length mismatch", StringComparison.OrdinalIgnoreCase) >= 0);
}

static bool LooksLikeAlreadyAcceptedArchiveChunk(PublisherHttpException ex, long chunkOffset, int chunkBytes) {
    if (ex.StatusCode != HttpStatusCode.Conflict || string.IsNullOrWhiteSpace(ex.ResponseBody))
        return false;

    try {
        using JsonDocument document = JsonDocument.Parse(ex.ResponseBody);
        long expectedOffset = PublisherJson.Long(document.RootElement, "expectedOffset", -1);
        long receivedOffset = PublisherJson.Long(document.RootElement, "receivedOffset", -1);
        return receivedOffset == chunkOffset && expectedOffset >= chunkOffset + chunkBytes;
    } catch {
        return false;
    }
}

static string ComputeSha256Hex(string path) {
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static void TryDeleteFile(string path) {
    try {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    } catch {
    }
}

static bool ServerTimestampIsNewerThanPublisher(DateTimeOffset serverLastWriteUtc, DateTimeOffset publisherLastWriteUtc) {
    return serverLastWriteUtc.ToUniversalTime() - publisherLastWriteUtc.ToUniversalTime() > TimeSpan.FromSeconds(2);
}

static bool ShouldAutoReplaceBecauseServerTimestampIsUnreliable(ServerManifest manifest, DateTimeOffset serverLastWriteUtc) {
    if (!manifest.TryGetGeneratedUtc(out DateTimeOffset generatedUtc))
        return false;

    DateTimeOffset serverUtc = serverLastWriteUtc.ToUniversalTime();
    DateTimeOffset generated = generatedUtc.ToUniversalTime();

    if (serverUtc > generated.Add(TimeSpan.FromSeconds(2)))
        return true;

    bool metadataClockDoesNotMatchPublisher = (generated - manifest.LoadedUtc.ToUniversalTime()).Duration() > TimeSpan.FromMinutes(2);
    bool serverStampLooksLikePublishTime = (serverUtc - generated).Duration() <= TimeSpan.FromMinutes(15);
    return metadataClockDoesNotMatchPublisher && serverStampLooksLikePublishTime;
}

static OlderFileDecision PromptReplaceOlderFile(UpdateFile file, FilePublishResult result) {
    Terminal.Line("older", file.RelativePath);
    Terminal.Line("older", "publisher UTC   " + FormatUtcAndLocal(file.LastWriteUtc));
    Terminal.Line("older", "server UTC      " + FormatUtcAndLocalText(result.ServerLastWriteUtc));
    Terminal.Line("older", "server copy may be newer because an older updater wrote upload-time timestamps");
    char action = Terminal.PromptKey("older", "Replace newer server file? [O]K/[S]KIP/[R]EPLACE ALL/S[K]IP ALL: ", "osrk");
    return action switch {
        'o' => OlderFileDecision.Replace,
        'r' => OlderFileDecision.ReplaceAll,
        'k' => OlderFileDecision.SkipAll,
        _ => OlderFileDecision.Skip
    };
}

static string FormatUtcAndLocal(DateTimeOffset value) {
    DateTimeOffset utc = value.ToUniversalTime();
    return utc.ToString("O", CultureInfo.InvariantCulture) + " | local " + utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}

static string FormatUtcAndLocalText(string value) {
    return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
        ? FormatUtcAndLocal(parsed)
        : value;
}

sealed class PublishUploadAbortedException : Exception {
    public PublishUploadAbortedException(string uploadId)
        : base("Upload " + uploadId + " was aborted.") {
        UploadId = uploadId;
    }

    public string UploadId { get; }
}

sealed class PublisherHttpException : InvalidOperationException {
    public PublisherHttpException(string url, HttpStatusCode statusCode, string reasonPhrase, string responseBody)
        : base(url + " -> " + (int)statusCode + " " + reasonPhrase) {
        Url = url;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        ResponseBody = responseBody;
    }

    public string Url { get; }
    public HttpStatusCode StatusCode { get; }
    public string ReasonPhrase { get; }
    public string ResponseBody { get; }
}

sealed class PublisherSession : IDisposable {
    private readonly PublisherOptions _options;
    private readonly HttpClient _http;
    private bool _authorityChecked;

    public PublisherSession(PublisherOptions options) {
        _options = options;
        var handler = new SocketsHttpHandler {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 2,
            AllowAutoRedirect = false
        };
        _http = new HttpClient(handler, disposeHandler: true) {
            BaseAddress = new Uri(EnsureTrailingSlash(options.ServerUrl)),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task AuthenticateAsync(bool forceRefresh) {
        await EnsureSecureAuthorityHealthyAsync();
        string token = await TokenCache.GetTokenAsync(_http, _options, forceRefresh);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task EnsureSecureAuthorityHealthyAsync() {
        if (_authorityChecked)
            return;

        using HttpResponseMessage response = await _http.GetAsync("healthz");
        string text = await response.Content.ReadAsStringAsync();
        string requestUrl = response.RequestMessage?.RequestUri?.ToString() ?? "healthz";
        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException("SecureAuthority health check failed at " + requestUrl + ": " +
                                                (int)response.StatusCode + " " + response.ReasonPhrase + " " +
                                                PublisherJson.Shorten(text, 220));
        }

        if (string.IsNullOrWhiteSpace(text)) {
            throw new InvalidOperationException("SecureAuthority health check returned an empty body at " + requestUrl +
                                                ". Verify SocketJack-MagicMasterList proxies /SecureAuthority/ to http://127.0.0.1:8500/.");
        }

        _authorityChecked = true;
        Terminal.Line("check", "SecureAuthority health OK");
    }

    public async Task<JsonDocument> PostJsonAsync(string url, object body) {
        for (int attempt = 0; attempt < 2; attempt++) {
            using HttpResponseMessage response = await _http.PostAsync(url, PublisherJson.CreateContent(body));
            string text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return PublisherJson.ParseResponse(url, response, text);

            Terminal.Line("http", url + " -> " + (int)response.StatusCode + " " + response.ReasonPhrase);
            if (!string.IsNullOrWhiteSpace(text))
                Terminal.JsonText("body", text);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) {
                Terminal.Line("auth", "token rejected; clearing cache and logging in again");
                await AuthenticateAsync(forceRefresh: true);
                continue;
            }

            throw new PublisherHttpException(url, response.StatusCode, response.ReasonPhrase ?? "", text);
        }

        throw new InvalidOperationException(url + " failed after authentication retry.");
    }

    public async Task<JsonDocument> PostBinaryFileAsync(string url, string filePath, long contentLength) {
        for (int attempt = 0; attempt < 2; attempt++) {
            using FileStream stream = File.OpenRead(filePath);
            using var content = new StreamContent(stream, 1024 * 1024);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = contentLength;

            using HttpResponseMessage response = await _http.PostAsync(url, content);
            string text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return PublisherJson.ParseResponse(url, response, text);

            Terminal.Line("http", url + " -> " + (int)response.StatusCode + " " + response.ReasonPhrase);
            if (!string.IsNullOrWhiteSpace(text))
                Terminal.JsonText("body", text);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) {
                Terminal.Line("auth", "token rejected; clearing cache and logging in again");
                await AuthenticateAsync(forceRefresh: true);
                continue;
            }

            throw new PublisherHttpException(url, response.StatusCode, response.ReasonPhrase ?? "", text);
        }

        throw new InvalidOperationException(url + " failed after authentication retry.");
    }

    public async Task<JsonDocument> PostBinaryArchiveChunkAsync(string uploadId, long offset, long totalLength, bool finalChunk, string sha256, int fileCount, long byteCount, byte[] buffer, int count) {
        string url = BuildArchiveBinaryUrl(uploadId, offset, totalLength, finalChunk, sha256, fileCount, byteCount);
        for (int attempt = 0; attempt < 2; attempt++) {
            using var content = new ByteArrayContent(buffer, 0, count);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = count;

            using HttpResponseMessage response = await _http.PostAsync(url, content);
            string text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return PublisherJson.ParseResponse(url, response, text);

            Terminal.Line("http", url + " -> " + (int)response.StatusCode + " " + response.ReasonPhrase);
            if (!string.IsNullOrWhiteSpace(text))
                Terminal.JsonText("body", text);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) {
                Terminal.Line("auth", "token rejected; clearing cache and logging in again");
                await AuthenticateAsync(forceRefresh: true);
                continue;
            }

            throw new PublisherHttpException(url, response.StatusCode, response.ReasonPhrase ?? "", text);
        }

        throw new InvalidOperationException(url + " failed after authentication retry.");
    }

    public async Task<JsonDocument?> GetJsonOrNullAsync(string url) {
        for (int attempt = 0; attempt < 2; attempt++) {
            using HttpResponseMessage response = await _http.GetAsync(url);
            string text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return PublisherJson.ParseResponse(url, response, text);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            Terminal.Line("http", url + " -> " + (int)response.StatusCode + " " + response.ReasonPhrase);
            if (!string.IsNullOrWhiteSpace(text))
                Terminal.JsonText("body", text);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) {
                Terminal.Line("auth", "token rejected; clearing cache and logging in again");
                await AuthenticateAsync(forceRefresh: true);
                continue;
            }

            throw new PublisherHttpException(url, response.StatusCode, response.ReasonPhrase ?? "", text);
        }

        throw new InvalidOperationException(url + " failed after authentication retry.");
    }

    public void Dispose() {
        _http.Dispose();
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private static string BuildArchiveBinaryUrl(string uploadId, long offset, long totalLength, bool finalChunk, string sha256, int fileCount, long byteCount) {
        var pairs = new List<KeyValuePair<string, string>> {
            new("uploadId", uploadId),
            new("offset", offset.ToString(CultureInfo.InvariantCulture)),
            new("totalLength", totalLength.ToString(CultureInfo.InvariantCulture)),
            new("finalChunk", finalChunk ? "true" : "false")
        };
        if (finalChunk) {
            pairs.Add(new KeyValuePair<string, string>("sha256", sha256 ?? ""));
            pairs.Add(new KeyValuePair<string, string>("fileCount", fileCount.ToString(CultureInfo.InvariantCulture)));
            pairs.Add(new KeyValuePair<string, string>("byteCount", byteCount.ToString(CultureInfo.InvariantCulture)));
        }
        return "api/update/publish/archive-binary?" + string.Join("&", pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }
}

sealed class PublisherOptions {
    private const int DefaultArchiveChunkSize = 8 * 1024 * 1024;
    public string ServerUrl { get; set; } = "https://socketjack.com/SecureAuthority/";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public int ChunkSize { get; set; } = DefaultArchiveChunkSize;
    public bool ProtectNewerServerFiles { get; set; }
    public bool AllowLargeWorkstationPayload { get; set; }
    public List<PublishJob> Jobs { get; } = new();

    public static PublisherOptions Parse(string[] args) {
        var options = new PublisherOptions();
        options.ServerUrl = Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_SERVER_URL") ?? options.ServerUrl;
        options.UserName = Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_USERNAME") ?? options.UserName;
        options.Password = Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PASSWORD") ?? options.Password;
        bool all = false;
        for (int i = 0; i < args.Length; i++) {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (arg.ToLowerInvariant()) {
                case "--server":
                case "--server-url":
                case "--url":
                    options.ServerUrl = Next();
                    break;
                case "--username":
                case "--user":
                    options.UserName = Next();
                    break;
                case "--password":
                    options.Password = Next();
                    break;
                case "--channel":
                    options.Jobs.Add(new PublishJob { Channel = Next() });
                    break;
                case "--source":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].SourceDirectory = Path.GetFullPath(Next());
                    break;
                case "--server-path":
                case "--target-directory":
                case "--update-directory":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].ServerPath = Next();
                    break;
                case "--public-path":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].PublicPath = Next();
                    break;
                case "--display-name":
                case "--name":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].DisplayName = Next();
                    break;
                case "--managed-process-name":
                case "--process-name":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].ManagedProcessName = Next();
                    break;
                case "--managed-exe":
                case "--managed-executable":
                case "--executable-path":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].ManagedExecutablePath = Next();
                    break;
                case "--auto-start":
                case "--auto-run":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].AutoStartAfterUpdate = true;
                    break;
                case "--no-auto-start":
                case "--no-auto-run":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].AutoStartAfterUpdate = false;
                    break;
                case "--extra-file":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].ExtraFiles.Add(new ExtraPublishFile { FullPath = Next() });
                    break;
                case "--no-default-extras":
                    if (options.Jobs.Count == 0)
                        options.Jobs.Add(new PublishJob());
                    options.Jobs[^1].SuppressDefaultExtras = true;
                    break;
                case "--chunk-size":
                    if (TryParseByteCount(Next(), out int chunkSize) && chunkSize > 64 * 1024)
                        options.ChunkSize = chunkSize;
                    break;
                case "--protect-newer-server-files":
                case "--prompt-older":
                case "--no-time-fix":
                    options.ProtectNewerServerFiles = true;
                    break;
                case "--allow-large-workstation-payload":
                case "--allow-large-jackllm-payload":
                    options.AllowLargeWorkstationPayload = true;
                    break;
                case "--all":
                    all = true;
                    break;
            }
        }

        if (all || options.Jobs.Count == 0)
            options.Jobs.AddRange(DefaultJobs());
        options.ServerUrl = NormalizeServerUrl(options.ServerUrl);
        return options;
    }

    public static string NormalizeServerUrl(string value) {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            value = "https://socketjack.com/SecureAuthority/";

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value.TrimStart('/');

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return EnsureTrailingSlash(value);

        string scheme = uri.Scheme;
        if (IsPublicSocketJackAuthority(uri))
            scheme = Uri.UriSchemeHttps;

        string path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
            ? "/SecureAuthority/"
            : uri.AbsolutePath;
        if (!path.StartsWith("/SecureAuthority", StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals("socketjack.com", StringComparison.OrdinalIgnoreCase))
            path = "/SecureAuthority" + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);

        var builder = new UriBuilder(uri) {
            Scheme = scheme,
            Path = EnsureTrailingSlash(path),
            Query = ""
        };
        if (IsDefaultPortForScheme(builder.Scheme, builder.Port))
            builder.Port = -1;
        return builder.Uri.ToString();
    }

    private static bool IsPublicSocketJackAuthority(Uri uri) {
        return uri.Host.Equals("socketjack.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("www.socketjack.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultPortForScheme(string scheme, int port) {
        return port <= 0 ||
               (scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && port == 80) ||
               (scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && port == 443);
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    public List<PublishJob> ResolveJobs() {
        string root = LocateRepoRoot();
        foreach (PublishJob job in Jobs) {
            job.Channel = NormalizeChannel(job.Channel);
            PublishJob? defaultJob = DefaultJobs().FirstOrDefault(candidate => candidate.Channel == job.Channel);
            if (string.IsNullOrWhiteSpace(job.SourceDirectory))
                job.SourceDirectory = defaultJob?.SourceDirectory ?? "";
            if (string.IsNullOrWhiteSpace(job.PublicPath))
                job.PublicPath = defaultJob?.PublicPath ?? "";
            if (string.IsNullOrWhiteSpace(job.ServerPath))
                job.ServerPath = defaultJob?.ServerPath ?? "";
            if (string.IsNullOrWhiteSpace(job.DisplayName))
                job.DisplayName = defaultJob?.DisplayName ?? "";
            if (string.IsNullOrWhiteSpace(job.ManagedProcessName))
                job.ManagedProcessName = defaultJob?.ManagedProcessName ?? "";
            if (string.IsNullOrWhiteSpace(job.ManagedExecutablePath))
                job.ManagedExecutablePath = defaultJob?.ManagedExecutablePath ?? "";
            if (!job.AutoStartAfterUpdate.HasValue && defaultJob?.AutoStartAfterUpdate.HasValue == true)
                job.AutoStartAfterUpdate = defaultJob.AutoStartAfterUpdate;
            if (job.IncludePaths.Count == 0 && defaultJob != null)
                job.IncludePaths.UnionWith(defaultJob.IncludePaths);
            job.SourceDirectory = Path.GetFullPath(job.SourceDirectory);
            ResolveExtraFiles(job, root, defaultJob != null && !job.SuppressDefaultExtras);
        }
        return Jobs.Where(job => !string.IsNullOrWhiteSpace(job.Channel) && !string.IsNullOrWhiteSpace(job.SourceDirectory)).ToList();
    }

    private static List<PublishJob> DefaultJobs() {
        string root = LocateRepoRoot();
        string configuration = InferConfiguration(AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = "Debug";
        return new List<PublishJob> {
            new() {
                Channel = "jackllm",
                DisplayName = "JackLLM",
                SourceDirectory = Path.Combine(root, "artifacts", "JackLLM", "publish"),
                PublicPath = "/Update",
                ServerPath = @"C:\JackLLM\Update",
                ManagedProcessName = "JackLLM",
                ManagedExecutablePath = "JackLLM.exe",
                AutoStartAfterUpdate = false,
                ExtraFiles = {
                    CreateMsiInstallerExtraFile(root, configuration),
                    CreateBootstrapInstallerExtraFile(root, configuration),
                    CreateLinuxWorkstationDebExtraFile(root)
                }
            },
            new() {
                Channel = "socketjack-magic-master-list",
                DisplayName = "SocketJack-MagicMasterList",
                SourceDirectory = Path.Combine(root, "SocketJack-MagicMasterList", "bin", configuration, "net8.0-windows7.0"),
                ServerPath = @"C:\Users\jackoffates\Desktop\Server2",
                ManagedProcessName = "SocketJack-MagicMasterList",
                ManagedExecutablePath = "SocketJack-MagicMasterList.exe",
                AutoStartAfterUpdate = true
            },
            new() {
                Channel = "jackllm-companion",
                DisplayName = "JackLLM Companion",
                SourceDirectory = Path.Combine(root, "JackLLMCompanion", "bin", configuration, "net8.0-windows7.0"),
                ServerPath = @"C:\JackLLM\Update\Companion",
                ManagedProcessName = "JackLLMCompanion",
                ManagedExecutablePath = "JackLLMCompanion.exe",
                AutoStartAfterUpdate = false
            },
            new() {
                Channel = "onlineusers-server",
                DisplayName = "OnlineUsers Server",
                SourceDirectory = @"C:\Users\Vin\source\repos\wShare\OnlineUsers\bin\Debug\net10.0-windows7.0",
                ServerPath = @"C:\Users\jackoffates\Desktop\wShare Server",
                ManagedProcessName = "OnlineUsers",
                ManagedExecutablePath = "OnlineUsers.exe",
                AutoStartAfterUpdate = true,
                IncludePaths = {
                    "OnlineUsers.deps.json",
                    "OnlineUsers.dll",
                    "OnlineUsers.exe",
                    "OnlineUsers.pdb",
                    "OnlineUsers.runtimeconfig.json",
                    "dataserver.json",
                    "runtimeconfig.json",
                    "SharedClasses.dll.config",
                    "Video Compiler.dll.config",
                    "Resources"
                }
            }
        };
    }

    private static void ResolveExtraFiles(PublishJob job, string root, bool includeDefaultExtras) {
        var resolved = new List<ExtraPublishFile>();
        foreach (ExtraPublishFile extra in job.ExtraFiles) {
            string rawPath = (extra.FullPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            string fullPath = IsBareInstallerFileName(rawPath)
                ? GetInstallerPath(root, job, rawPath)
                : Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(root, rawPath));

            resolved.Add(new ExtraPublishFile {
                FullPath = fullPath,
                RelativePath = extra.RelativePath
            });
        }

        if (includeDefaultExtras) {
            foreach (ExtraPublishFile defaultExtra in DefaultExtraFiles(root, job)) {
                string outputName = GetExtraOutputName(defaultExtra);
                bool alreadyPresent = resolved.Any(extra =>
                    string.Equals(GetExtraOutputName(extra), outputName, StringComparison.OrdinalIgnoreCase));
                if (!alreadyPresent)
                    resolved.Add(defaultExtra);
            }
        }

        job.ExtraFiles.Clear();
        job.ExtraFiles.AddRange(resolved);
    }

    private static IEnumerable<ExtraPublishFile> DefaultExtraFiles(string root, PublishJob job) {
        if (string.Equals(job.Channel, "jackllm", StringComparison.OrdinalIgnoreCase)) {
            string configuration = GetJobConfiguration(job);
            yield return CreateMsiInstallerExtraFile(root, configuration);
            yield return CreateBootstrapInstallerExtraFile(root, configuration);
            yield return CreateLinuxWorkstationDebExtraFile(root);
        }
    }

    private static ExtraPublishFile CreateMsiInstallerExtraFile(string root, string configuration) {
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = "Debug";
        return new ExtraPublishFile {
            FullPath = FindMsiInstallerPath(root, configuration)
        };
    }

    private static ExtraPublishFile CreateBootstrapInstallerExtraFile(string root, string configuration) {
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = "Debug";
        return new ExtraPublishFile {
            FullPath = FindBootstrapInstallerPath(root, configuration)
        };
    }

    private static ExtraPublishFile CreateLinuxWorkstationDebExtraFile(string root) {
        return new ExtraPublishFile {
            FullPath = FindLinuxWorkstationDebPath(root)
        };
    }

    private static string FindMsiInstallerPath(string root, string configuration) {
        string[] candidates = {
            Path.Combine(root, "JackLLMInstaller", "bin", "x64", configuration, "JackLLM-Setup.msi"),
            Path.Combine(root, "JackLLMInstaller", "bin", configuration, "JackLLM-Setup.msi")
        };

        return candidates
            .Where(File.Exists)
            .Select(candidate => new FileInfo(candidate))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault() ?? candidates[0];
    }

    private static string FindBootstrapInstallerPath(string root, string configuration) {
        string[] candidates = {
            Path.Combine(root, "artifacts", "JackLLMBridgeInstaller", "publish", "JackLLM-Setup.exe"),
            Path.Combine(root, "JackLLMBridgeInstaller", "bin", configuration, "net8.0-windows7.0", "win-x64", "publish", "JackLLM-Setup.exe"),
            Path.Combine(root, "JackLLMBridgeInstaller", "bin", configuration, "net8.0-windows7.0", "win-x64", "JackLLM-Setup.exe")
        };

        foreach (string candidate in candidates) {
            if (File.Exists(candidate))
                return candidate;
        }

        return candidates[0];
    }

    private static string FindLinuxWorkstationDebPath(string root) {
        const string fileName = "LlmWorkstation_Linux64.deb";
        string[] candidates = {
            Path.Combine(root, "artifacts", "linux-installer", fileName),
            Path.Combine(@"C:\JackLLM\Update", fileName)
        };

        foreach (string candidate in candidates) {
            if (File.Exists(candidate))
                return candidate;
        }

        return candidates[0];
    }

    private static string GetInstallerPath(string root, PublishJob job, string fileName) {
        string configuration = GetJobConfiguration(job);
        return fileName.Equals("JackLLM-Setup.exe", StringComparison.OrdinalIgnoreCase)
            ? CreateBootstrapInstallerExtraFile(root, configuration).FullPath
            : CreateMsiInstallerExtraFile(root, configuration).FullPath;
    }

    private static string GetJobConfiguration(PublishJob job) {
        string configuration = InferConfiguration(job.SourceDirectory);
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = InferConfiguration(AppContext.BaseDirectory);
        return string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration;
    }

    private static bool IsBareInstallerFileName(string path) {
        return !path.Contains('\\') &&
            !path.Contains('/') &&
            (string.Equals(path, "JackLLM-Setup.msi", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(path, "JackLLM-Setup.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetExtraOutputName(ExtraPublishFile extra) {
        return string.IsNullOrWhiteSpace(extra.RelativePath)
            ? Path.GetFileName(extra.FullPath)
            : Path.GetFileName(extra.RelativePath);
    }

    private static string LocateRepoRoot() {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null) {
            if (File.Exists(Path.Combine(directory.FullName, "SocketJack.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string InferConfiguration(string path) {
        foreach (string segment in path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)) {
            if (string.Equals(segment, "Debug", StringComparison.OrdinalIgnoreCase))
                return "Debug";
            if (string.Equals(segment, "Release", StringComparison.OrdinalIgnoreCase))
                return "Release";
        }
        return "";
    }

    private static string NormalizeChannel(string value) {
        value = (value ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? "jackllm" : value;
    }

    private static bool TryParseByteCount(string value, out int bytes) {
        bytes = 0;
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        long multiplier = 1;
        char suffix = char.ToLowerInvariant(value[^1]);
        if (suffix is 'k' or 'm' or 'g') {
            value = value.Substring(0, value.Length - 1).Trim();
            multiplier = suffix switch {
                'k' => 1024L,
                'm' => 1024L * 1024L,
                'g' => 1024L * 1024L * 1024L,
                _ => 1L
            };
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || parsed <= 0)
            return false;

        double result = parsed * multiplier;
        if (result > int.MaxValue)
            result = int.MaxValue;
        bytes = (int)Math.Round(result);
        return bytes > 0;
    }
}

sealed class PublishJob {
    public string Channel { get; set; } = "jackllm";
    public string DisplayName { get; set; } = "";
    public string SourceDirectory { get; set; } = "";
    public string PublicPath { get; set; } = "";
    public string ServerPath { get; set; } = "";
    public string ManagedProcessName { get; set; } = "";
    public string ManagedExecutablePath { get; set; } = "";
    public bool? AutoStartAfterUpdate { get; set; }
    public bool SuppressDefaultExtras { get; set; }
    public HashSet<string> IncludePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ExtraPublishFile> ExtraFiles { get; } = new();
}

sealed class ExtraPublishFile {
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
}

sealed class ServerManifest {
    public bool Available { get; private init; }
    public string Root { get; private init; } = "";
    public string GeneratedUtc { get; private init; } = "";
    public DateTimeOffset LoadedUtc { get; private init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, ServerManifestFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Count => Files.Count;

    public static async Task<ServerManifest> LoadAsync(PublisherSession session, PublishJob job) {
        try {
            JsonDocument? document = await session.GetJsonOrNullAsync(BuildManifestPath(job));
            if (document == null)
                return new ServerManifest();
            using (document)
                return FromJson(document.RootElement);
        } catch (Exception ex) {
            Terminal.Error("meta", "could not load server metadata: " + ex.Message);
            return new ServerManifest();
        }
    }

    private static string BuildManifestPath(PublishJob job) {
        string path;
        if (!string.IsNullOrWhiteSpace(job.PublicPath))
            path = job.PublicPath.Trim().Trim('/') + "/meta";
        else
            path = "update/" + Uri.EscapeDataString(job.Channel) + "/meta";
        return path + (path.Contains("?", StringComparison.Ordinal) ? "&" : "?") + "fresh=true";
    }

    public bool TryGet(string relativePath, out ServerManifestFile? file) {
        return Files.TryGetValue((relativePath ?? "").Replace('\\', '/'), out file);
    }

    public bool TryGetGeneratedUtc(out DateTimeOffset value) {
        return DateTimeOffset.TryParse(GeneratedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static ServerManifest FromJson(JsonElement root) {
        var manifest = new ServerManifest {
            Available = PublisherJson.Bool(root, "available"),
            Root = PublisherJson.Text(root, "root"),
            GeneratedUtc = PublisherJson.Text(root, "generatedUtc"),
            LoadedUtc = DateTimeOffset.UtcNow
        };
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            return manifest;

        foreach (JsonElement item in files.EnumerateArray()) {
            string path = PublisherJson.Text(item, "path").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path))
                continue;
            manifest.Files[path] = new ServerManifestFile {
                Path = path,
                Sha256 = FirstNonEmpty(PublisherJson.Text(item, "sha256"), PublisherJson.Text(item, "hash")).Trim().ToLowerInvariant(),
                Length = PublisherJson.Long(item, "length", 0),
                LastWriteUtc = PublisherJson.Text(item, "lastWriteUtc")
            };
        }
        return manifest;
    }

    private static string FirstNonEmpty(params string[] values) {
        foreach (string value in values) {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }
}

sealed class ServerManifestFile {
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Length { get; init; }
    public string LastWriteUtc { get; init; } = "";

    public bool TryGetLastWriteUtc(out DateTimeOffset value) {
        return DateTimeOffset.TryParse(LastWriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}

enum OlderFileDecision {
    Ask,
    Replace,
    Skip,
    ReplaceAll,
    SkipAll
}

sealed class FilePublishResult {
    public bool Uploaded { get; private init; }
    public string Reason { get; private init; } = "";
    public string ServerLastWriteUtc { get; private init; } = "";
    public string PublisherLastWriteUtc { get; private init; } = "";

    public static FilePublishResult UploadedChunk() => new() { Uploaded = true };
    public static FilePublishResult UploadComplete() => new() { Uploaded = true };
    public static FilePublishResult Skip(string reason, string serverLastWriteUtc, string publisherLastWriteUtc) => new() {
        Uploaded = false,
        Reason = string.IsNullOrWhiteSpace(reason) ? "skipped" : reason,
        ServerLastWriteUtc = serverLastWriteUtc,
        PublisherLastWriteUtc = publisherLastWriteUtc
    };
}

sealed class LockingProcessPromptInfo {
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public bool CanStop { get; init; } = true;
    public bool RestartAfterUpdate { get; init; }

    public string Summary {
        get {
            string restart = RestartAfterUpdate ? "restart after update" : "restart handled separately or unavailable";
            string path = string.IsNullOrWhiteSpace(ExecutablePath) ? "" : "  " + ExecutablePath;
            return Name + "#" + Id.ToString(CultureInfo.InvariantCulture) + "  " + restart + path;
        }
    }
}

sealed class UpdateFile {
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long Length { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset LastWriteUtc { get; init; }

    public static UpdateFile? TryCreate(string root, string path, string channel) {
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        if (!IsAllowed(relative, channel))
            return null;
        var info = new FileInfo(path);
        return new UpdateFile {
            FullPath = path,
            RelativePath = relative,
            Length = info.Length,
            Sha256 = Hash(path),
            LastWriteUtc = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)).ToUniversalTime()
        };
    }

    public static UpdateFile? TryCreateExtra(string fullPath, string relativePath, string channel) {
        relativePath = relativePath.Trim().Replace('\\', '/').Trim('/');
        if (!IsAllowed(relativePath, channel))
            return null;

        var info = new FileInfo(fullPath);
        return new UpdateFile {
            FullPath = fullPath,
            RelativePath = relativePath,
            Length = info.Length,
            Sha256 = Hash(fullPath),
            LastWriteUtc = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)).ToUniversalTime()
        };
    }

    private static bool IsAllowed(string relativePath, string channel) {
        if (IsJackLlmWorkstationChannel(channel) && IsBlockedJackLlmPayloadPath(relativePath))
            return false;

        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                segment.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("database", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("databases", StringComparison.OrdinalIgnoreCase)))
            return false;
        string fileName = Path.GetFileName(relativePath);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !IsAllowedJsonUpdateFile(fileName))
            return false;
        return true;
    }

    private static bool IsAllowedJsonUpdateFile(string fileName) {
        return fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dataserver.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJackLlmWorkstationChannel(string channel) {
        string id = (channel ?? "").Trim().ToLowerInvariant();
        return id.Equals("jackllm", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("jackllm-companion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedJackLlmPayloadPath(string relativePath) {
        relativePath = (relativePath ?? "").Replace('\\', '/').Trim('/');
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsJackLlmRuntimeDataSegment))
            return true;

        string fileName = Path.GetFileName(relativePath);
        if (IsJackLlmRuntimeDataFile(fileName))
            return true;

        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJackLlmRuntimeDataSegment(string segment) {
        return segment.Equals("agents", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("caches", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("config", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("configs", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("database", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("databases", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("jackllmchat", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("log", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("models", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("completemodels", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sessionfiles", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sockjackdml", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("uploads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("userdata", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("user-data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("workspaces", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJackLlmRuntimeDataFile(string fileName) {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        string extension = Path.GetExtension(fileName);
        return fileName.Equals(".socketjack-update.meta", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("auth.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dynamicUpdates.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("JackLLM.settings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("lastUpdates.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("updater-config.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("updater-status.json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".iobj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ipdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lib", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".map", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".old", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".orig", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".suo", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".user", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wixpdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string path) {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

static class TokenCache {
    public static async Task<string> GetTokenAsync(HttpClient http, PublisherOptions options, bool forceRefresh) {
        string cachePath = CachePath(options.ServerUrl);
        if (forceRefresh)
            Clear(options.ServerUrl);

        if (!forceRefresh && File.Exists(cachePath)) {
            TokenRecord? cached = JsonSerializer.Deserialize<TokenRecord>(await File.ReadAllTextAsync(cachePath), PublisherJson.Options);
            if (cached != null && !string.IsNullOrWhiteSpace(cached.AccessToken) && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5)) {
                Terminal.Line("auth", "cached bearer token accepted");
                return cached.AccessToken;
            }
        }

        while (true) {
            string username = options.UserName;
            string password = options.Password;

            if (string.IsNullOrWhiteSpace(username)) {
                username = Terminal.Prompt("login", "username: ");
                options.UserName = username;
            }
            if (string.IsNullOrWhiteSpace(password)) {
                Terminal.Write("login", "password: ");
                password = ReadPassword();
                Terminal.Raw("");
                options.Password = password;
            }

            string text;
            try {
                using HttpResponseMessage response = await http.PostAsync("api/socketjack/oauth/token", PublisherJson.CreateContent(new {
                    grant_type = "password",
                    username,
                    password,
                    remember = true
                }));
                text = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode) {
                    using JsonDocument document = PublisherJson.ParseResponse("api/socketjack/oauth/token", response, text);
                    string token = PublisherJson.Text(document.RootElement, "access_token");
                    if (string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("api/socketjack/oauth/token did not return an access_token.");
                    int expiresIn = int.TryParse(PublisherJson.Text(document.RootElement, "expires_in"), out int seconds) ? seconds : 86400;
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new TokenRecord {
                        AccessToken = token,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn))
                    }, PublisherJson.Options));
                    Terminal.Line("auth", "new bearer token stored");
                    return token;
                }
            } catch (Exception ex) {
                text = DescribeAuthConnectionFailure(options.ServerUrl, ex);
            }

            Terminal.Error("auth", "login failed: " + text);
            options.Password = "";
            string action = Terminal.Prompt("login", "Try login again? [Y/n]: ").Trim().ToLowerInvariant();
            if (action == "n" || action == "no")
                throw new InvalidOperationException("Login was not completed.");
            forceRefresh = true;
        }
    }

    public static void Clear(string serverUrl) {
        string cachePath = CachePath(serverUrl);
        try {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        } catch {
        }
    }

    private static string CachePath(string serverUrl) {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SocketJackUpdate", "publisher-" + key + ".json");
    }

    private static string DescribeAuthConnectionFailure(string serverUrl, Exception exception) {
        string message = exception.Message;
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? selectedUri) &&
            selectedUri.Host.Equals("socketjack.com", StringComparison.OrdinalIgnoreCase) &&
            selectedUri.AbsolutePath.StartsWith("/SecureAuthority", StringComparison.OrdinalIgnoreCase)) {
            message += " Public publishing through socketjack.com must use https://socketjack.com/SecureAuthority/. " +
                       "Private publishing on the server host can use http://127.0.0.1:8500/SecureAuthority/.";
        }

        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            HasSocketException(exception)) {
            int port = uri.IsDefaultPort ? 443 : uri.Port;
            message += " The publisher is trying HTTPS on " + uri.Host + ":" + port.ToString(CultureInfo.InvariantCulture) +
                       ". Confirm SocketJack-MagicMasterList owns socketjack.com:443 and proxies /SecureAuthority/ to http://127.0.0.1:8500/.";
        }
        return message;
    }

    private static bool HasSocketException(Exception exception) {
        for (Exception? current = exception; current != null; current = current.InnerException) {
            if (current is SocketException)
                return true;
        }
        return false;
    }

    private static string ReadPassword() {
        var builder = new StringBuilder();
        while (true) {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace) {
                if (builder.Length > 0)
                    builder.Length--;
                continue;
            }
            builder.Append(key.KeyChar);
        }
        return builder.ToString();
    }
}

sealed class TokenRecord {
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
}

static class PublisherJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ByteArrayContent CreateContent(object value) {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = payload.LongLength;
        return content;
    }

    public static JsonDocument ParseResponse(string url, HttpResponseMessage response, string text) {
        string requestUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        if (string.IsNullOrWhiteSpace(text)) {
            throw new InvalidOperationException(url + " returned " + (int)response.StatusCode + " " + response.ReasonPhrase +
                                                " with an empty body. Verify the update server route is mapped at " + requestUrl + ".");
        }

        try {
            return JsonDocument.Parse(text);
        } catch (JsonException ex) {
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown content type";
            throw new InvalidOperationException(url + " returned " + (int)response.StatusCode + " " + response.ReasonPhrase +
                                                " with non-JSON " + contentType + ": " + Shorten(text, 220), ex);
        }
    }

    public static string Text(JsonElement element, string name) {
        if (element.ValueKind != JsonValueKind.Object)
            return "";
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
        }
        return "";
    }

    public static bool Bool(JsonElement element, string name) {
        string value = Text(element, name);
        return bool.TryParse(value, out bool parsed) && parsed;
    }

    public static long Long(JsonElement element, string name, long fallback) {
        string value = Text(element, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;
    }

    public static string Shorten(string text, int maxLength) {
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
    }
}

static class Terminal {
    private static readonly object Sync = new();
    private static string _logPath = "";
    private static bool _liveLineActive;
    private static int _liveLineLength;
    private static string _liveLineText = "";

    public static string LogPath => _logPath;

    public static void StartSessionLog() {
        if (!string.IsNullOrWhiteSpace(_logPath))
            return;
        string stamp = DateTime.Now.ToString("MM-dd-yyyy--HHmm-ss-ff", CultureInfo.InvariantCulture);
        _logPath = Path.Combine(Environment.CurrentDirectory, "log_sess" + stamp + ".txt");
        File.AppendAllText(_logPath, "SocketJack Update Publisher session " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine);
    }

    public static void Banner() {
        if (!Console.IsOutputRedirected) {
            try {
                Console.Clear();
            } catch {
            }
        }
        Raw("  ____             _        _     _            _      _   _           _       _       ");
        Raw(" / ___|  ___   ___| | _____| |_  | | __ _  ___| | __ | | | |_ __   __| | __ _| |_ ___ ");
        Raw(" \\___ \\ / _ \\ / __| |/ / _ \\ __| | |/ _` |/ __| |/ / | | | | '_ \\ / _` |/ _` | __/ _ \\");
        Raw("  ___) | (_) | (__|   <  __/ |_  | | (_| | (__|   <  | |_| | |_) | (_| | (_| | ||  __/");
        Raw(" |____/ \\___/ \\___|_|\\_\\___|\\__| |_|\\__,_|\\___|_|\\_\\  \\___/| .__/ \\__,_|\\__,_|\\__\\___|");
        Raw("                                                            |_|                         ");
        Raw("");
    }

    public static void Line(string tag, string message) {
        Raw("[" + tag.PadRight(6) + "] " + message);
    }

    public static void Error(string tag, string message) {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Line(tag, message);
        Console.ForegroundColor = previous;
    }

    public static void Write(string tag, string message) {
        ClearLiveLine();
        string text = "[" + tag.PadRight(6) + "] " + message;
        Console.Write(text);
        AppendLog(text);
    }

    public static string Prompt(string tag, string message) {
        Write(tag, message);
        string value = Console.ReadLine() ?? "";
        AppendLog(value);
        return value;
    }

    public static char PromptKey(string tag, string message, string allowedKeys) {
        Write(tag, message);
        allowedKeys = (allowedKeys ?? "").ToLowerInvariant();
        while (true) {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            char value = char.ToLowerInvariant(key.KeyChar);
            if (allowedKeys.IndexOf(value) < 0)
                continue;

            Console.Write(char.ToUpperInvariant(value));
            Console.WriteLine();
            AppendLog(value.ToString());
            return value;
        }
    }

    public static void Json(string tag, JsonElement element) {
        AppendLog("[" + tag.PadRight(6) + "] " + element.GetRawText());
        WriteSummary(tag, SummarizeJson(element));
    }

    public static void JsonText(string tag, string text) {
        AppendLog("[" + tag.PadRight(6) + "] " + (text ?? ""));
        try {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            WriteSummary(tag, SummarizeJson(document.RootElement));
        } catch {
            WriteSummary(tag, SummarizeText(text ?? ""));
        }
    }

    public static void Raw(string message) {
        bool redrawLiveLine = false;
        string liveLineText = "";
        lock (Sync) {
            redrawLiveLine = _liveLineActive && !Console.IsOutputRedirected;
            liveLineText = _liveLineText;
            if (redrawLiveLine)
                DrawBottomLine("");

            Console.WriteLine(message);

            if (redrawLiveLine)
                DrawBottomLine(liveLineText);
        }
        AppendLog(message);
    }

    private static void WriteSummary(string tag, IReadOnlyList<string> lines) {
        int count = 0;
        foreach (string line in lines.Take(16)) {
            Line(count == 0 ? tag : "", line);
            count++;
        }
    }

    private static IReadOnlyList<string> SummarizeText(string text) {
        text = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new[] { "(empty response)" };

        var lines = text
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Shorten(line.Trim(), 132))
            .Take(15)
            .ToList();
        if (text.Count(c => c == '\n') + 1 > lines.Count)
            lines.Add("... full response saved in session log");
        return lines.Count == 0 ? new[] { Shorten(text, 132) } : lines;
    }

    private static IReadOnlyList<string> SummarizeJson(JsonElement element) {
        var lines = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
            return new[] { Shorten(element.GetRawText(), 132) };

        string ok = JsonText(element, "ok");
        string error = FirstJsonText(element, "error", "message");
        string detail = JsonText(element, "detail");
        string exception = JsonText(element, "exception");
        string replaceStep = JsonText(element, "replaceStep");
        string uploadId = FirstJsonText(element, "uploadId", "id");
        string channel = FirstJsonText(element, "channel", "channelId");

        var headline = new List<string>();
        if (!string.IsNullOrWhiteSpace(ok)) headline.Add("ok=" + ok.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(channel)) headline.Add("channel=" + channel);
        if (!string.IsNullOrWhiteSpace(uploadId)) headline.Add("upload=" + uploadId);
        if (!string.IsNullOrWhiteSpace(error)) headline.Add("error=" + error);
        if (headline.Count == 0)
            headline.Add("json response");
        lines.Add(Shorten(string.Join("  ", headline), 132));

        if (!string.IsNullOrWhiteSpace(exception) || !string.IsNullOrWhiteSpace(replaceStep))
            lines.Add(Shorten("exception=" + EmptyDash(exception) + "  step=" + EmptyDash(replaceStep), 132));
        if (!string.IsNullOrWhiteSpace(detail))
            lines.Add(Shorten("detail=" + detail, 132));

        AddNumberLine(lines, element, "files", "fileCount", "byteCount");
        AddNumberLine(lines, element, "skipped", "skippedFileCount", "skippedByteCount");
        AddPathLine(lines, element, "updateDirectory", "update");
        AddPathLine(lines, element, "stagingRoot", "staging");
        AddPathLine(lines, element, "serverLog", "server log");
        AddArraySummary(lines, element, "stoppedProcesses", "stopped");
        AddArraySummary(lines, element, "startedProcesses", "started");
        AddArraySummary(lines, element, "lockingProcesses", "locking processes");
        AddArraySummary(lines, element, "lockingProcessDetails", "locking process details");
        AddArraySummary(lines, element, "probableLockingProcesses", "probable locks");
        AddArraySummary(lines, element, "probableLockingProcessDetails", "probable lock details");
        AddArraySummary(lines, element, "lockedFiles", "locked files");

        if (TryGetProperty(element, "manifest", out JsonElement manifest) && manifest.ValueKind == JsonValueKind.Object) {
            string available = JsonText(manifest, "available");
            string count = JsonText(manifest, "count");
            string root = JsonText(manifest, "root");
            string baseUrl = JsonText(manifest, "baseUrl");
            lines.Add(Shorten("manifest available=" + EmptyDash(available) + "  count=" + EmptyDash(count), 132));
            if (!string.IsNullOrWhiteSpace(root))
                lines.Add(Shorten("manifest root=" + root, 132));
            if (!string.IsNullOrWhiteSpace(baseUrl))
                lines.Add(Shorten("manifest base=" + baseUrl, 132));
            if (TryGetProperty(manifest, "files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                lines.Add(SummarizeJsonArray(files, "manifest files"));
        }

        foreach (JsonProperty property in element.EnumerateObject()) {
            if (lines.Count >= 15)
                break;
            if (IsKnownSummaryProperty(property.Name))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array)
                continue;
            lines.Add(Shorten(property.Name + "=" + JsonScalarText(property.Value), 132));
        }

        if (CountJsonLines(element) > 16 || lines.Count >= 16) {
            const string omitted = "... full JSON saved in session log";
            if (lines.Count >= 16)
                lines[15] = omitted;
            else
                lines.Add(omitted);
        }

        return lines.Take(16).ToList();
    }

    private static void AddNumberLine(List<string> lines, JsonElement element, string label, string countName, string bytesName) {
        string count = JsonText(element, countName);
        string bytes = JsonText(element, bytesName);
        if (string.IsNullOrWhiteSpace(count) && string.IsNullOrWhiteSpace(bytes))
            return;

        string byteText = long.TryParse(bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? Bytes(parsed)
            : EmptyDash(bytes);
        lines.Add(label + "=" + EmptyDash(count) + "  bytes=" + byteText);
    }

    private static void AddPathLine(List<string> lines, JsonElement element, string name, string label) {
        string value = JsonText(element, name);
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add(Shorten(label + "=" + value, 132));
    }

    private static void AddArraySummary(List<string> lines, JsonElement element, string name, string label) {
        if (!TryGetProperty(element, name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return;
        lines.Add(SummarizeJsonArray(array, label));
    }

    private static string SummarizeJsonArray(JsonElement array, string label) {
        int count = array.GetArrayLength();
        var samples = new List<string>();
        foreach (JsonElement item in array.EnumerateArray().Take(3)) {
            if (item.ValueKind == JsonValueKind.Object) {
                string path = FirstJsonText(item, "path", "name", "id");
                samples.Add(string.IsNullOrWhiteSpace(path) ? Shorten(item.GetRawText(), 48) : path);
            } else {
                samples.Add(JsonScalarText(item));
            }
        }

        string suffix = count > samples.Count ? " +" + (count - samples.Count).ToString(CultureInfo.InvariantCulture) + " more" : "";
        return Shorten(label + "=" + count.ToString(CultureInfo.InvariantCulture) + (samples.Count == 0 ? "" : "  " + string.Join(", ", samples) + suffix), 132);
    }

    private static string FirstJsonText(JsonElement element, params string[] names) {
        foreach (string name in names) {
            string value = JsonText(element, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }

    private static string JsonText(JsonElement element, string name) {
        if (!TryGetProperty(element, name, out JsonElement value))
            return "";
        return JsonScalarText(value);
    }

    private static string JsonScalarText(JsonElement value) {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? value.GetRawText()
                : value.ToString();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value) {
        if (element.ValueKind == JsonValueKind.Object) {
            foreach (JsonProperty property in element.EnumerateObject()) {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool IsKnownSummaryProperty(string name) {
        string normalized = name.ToLowerInvariant();
        return normalized is "ok" or "error" or "message" or "detail" or "exception" or "replacestep" or
            "uploadid" or "id" or "channel" or "channelid" or "filecount" or "bytecount" or
            "skippedfilecount" or "skippedbytecount" or "updatedirectory" or "stagingroot" or
            "serverlog" or "stoppedprocesses" or "startedprocesses" or "requiresprocesskill" or
            "lockingprocesses" or "lockingprocessdetails" or "probablelockingprocesses" or
            "probablelockingprocessdetails" or "lockedfiles" or "manifest";
    }

    private static int CountJsonLines(JsonElement element) {
        string raw = element.GetRawText();
        return Math.Max(1, raw.Length / 120);
    }

    private static string EmptyDash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Shorten(string value, int maxLength) {
        value ??= "";
        if (value.Length <= maxLength)
            return value;
        return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
    }

    public static void Status(string tag, string message) {
        WriteLive("[" + tag.PadRight(6) + "] " + message);
    }

    public static void Bar(string fileName, long value, long max, long fileValue, long fileMax, int width, string state) {
        double pct = Math.Clamp(value / (double)max, 0, 1);
        int fill = (int)Math.Round(pct * width);
        string text = "[upload] [" + new string('#', fill) + new string('.', Math.Max(0, width - fill)) + "] " +
                      (pct * 100).ToString("N1", CultureInfo.InvariantCulture).PadLeft(5) + "% " +
                      "total " + Mb(value) + "/" + Mb(max) + " MB " +
                      "| file " + Mb(fileValue) + "/" + Mb(Math.Max(1, fileMax)) + " MB " +
                      "| " + (string.IsNullOrWhiteSpace(state) ? "working" : state) + " " +
                      ShortenPath(fileName, 36);
        WriteLive(text);
        if (value >= max || value == 0 || value % (32L * 1024L * 1024L) < 1024L * 1024L)
            AppendLog(text);
    }

    public static string Bytes(long bytes) {
        if (bytes < 1024) return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("N1", CultureInfo.InvariantCulture) + " KB";
        if (bytes < 1024 * 1024 * 1024L) return (bytes / (1024.0 * 1024)).ToString("N1", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("N2", CultureInfo.InvariantCulture) + " GB";
    }

    private static string Mb(long bytes) {
        return (bytes / (1024.0 * 1024.0)).ToString("N2", CultureInfo.InvariantCulture);
    }

    private static string ShortenPath(string path, int maxLength) {
        path = (path ?? "").Replace('\\', '/');
        if (path.Length <= maxLength)
            return path;

        string fileName = Path.GetFileName(path);
        if (fileName.Length + 3 <= maxLength)
            return "..." + fileName.Substring(Math.Max(0, fileName.Length - (maxLength - 3)));

        return "..." + path.Substring(path.Length - Math.Max(1, maxLength - 3));
    }

    private static void AppendLog(string message) {
        if (string.IsNullOrWhiteSpace(_logPath))
            return;
        lock (Sync) {
            File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }
    }

    private static void WriteLive(string text) {
        lock (Sync) {
            if (Console.IsOutputRedirected) {
                int clearWidth = Math.Max(_liveLineLength, text.Length);
                Console.Write("\r" + text);
                if (clearWidth > text.Length)
                    Console.Write(new string(' ', clearWidth - text.Length));
                Console.Write("\r" + text);
                _liveLineActive = true;
                _liveLineLength = text.Length;
                _liveLineText = text;
                return;
            }

            text = FitToConsoleWidth(text);
            DrawBottomLine(text);
            _liveLineActive = true;
            _liveLineLength = text.Length;
            _liveLineText = text;
        }
    }

    private static void ClearLiveLine() {
        lock (Sync) {
            if (!_liveLineActive)
                return;
            if (Console.IsOutputRedirected) {
                Console.Write("\r" + new string(' ', _liveLineLength) + "\r");
            } else {
                DrawBottomLine("");
            }
            _liveLineActive = false;
            _liveLineLength = 0;
            _liveLineText = "";
        }
    }

    private static void DrawBottomLine(string text) {
        int width = SafeWindowWidth();
        int height = SafeWindowHeight();
        if (width <= 0 || height <= 0) {
            Console.Write("\r" + text);
            return;
        }

        int left = Console.CursorLeft;
        int top = Console.CursorTop;
        int bottom = Math.Max(0, height - 1);
        Console.SetCursorPosition(0, bottom);
        Console.Write((text ?? "").PadRight(Math.Max(0, width - 1)));
        Console.SetCursorPosition(Math.Min(left, Math.Max(0, width - 1)), Math.Min(top, Math.Max(0, bottom - 1)));
    }

    private static string FitToConsoleWidth(string text) {
        int width = SafeWindowWidth();
        if (width > 0 && text.Length >= width)
            return text.Substring(0, Math.Max(1, width - 1));
        return text;
    }

    private static int SafeWindowWidth() {
        try {
            return Console.WindowWidth;
        } catch {
            return 0;
        }
    }

    private static int SafeWindowHeight() {
        try {
            return Console.WindowHeight;
        } catch {
            return 0;
        }
    }
}
