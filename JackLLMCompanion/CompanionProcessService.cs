using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace JackLLMCompanion;

public sealed class CompanionProcessService
{
    private const int MaxTake = 1000;
    private const int DefaultTake = 200;
    private const int MaxBrowserEntries = 500;
    private const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;
    private readonly object _sampleLock = new();
    private readonly Dictionary<int, ProcessCpuSample> _cpuSamples = new();

    public CompanionProcessSnapshot GetProcessSnapshot(CompanionProcessQuery? query = null)
    {
        query ??= new CompanionProcessQuery();
        DateTimeOffset capturedUtc = DateTimeOffset.UtcNow;
        List<CompanionWindowInfo> windows = EnumerateWindows(capturedUtc);
        Dictionary<int, List<CompanionWindowInfo>> windowsByPid = windows
            .GroupBy(window => window.Pid)
            .ToDictionary(group => group.Key, group => group.ToList());
        ulong totalRamBytes = GetTotalPhysicalMemoryBytes();
        double totalRamGb = RoundGb(totalRamBytes / BytesPerGb);
        Dictionary<int, double> gpuByPid = TryReadGpuUsageByPid(out bool gpuAvailable, out string gpuUnavailableReason);

        var rows = new List<CompanionProcessInfo>();
        var seenPids = new HashSet<int>();
        foreach (Process process in SafeGetProcesses())
        {
            try
            {
                int pid = SafeRead(() => process.Id, 0);
                if (pid <= 0)
                    continue;
                seenPids.Add(pid);

                CompanionProcessInfo info = ReadProcessInfo(process, pid, windowsByPid, totalRamBytes, totalRamGb, gpuByPid, gpuAvailable, gpuUnavailableReason, capturedUtc);
                rows.Add(info);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        CleanupCpuSamples(seenPids, capturedUtc);
        List<CompanionProcessInfo> filtered = ApplyProcessQuery(rows, query).ToList();
        return new CompanionProcessSnapshot
        {
            CapturedUtc = capturedUtc,
            TotalRamGb = totalRamGb,
            TotalCount = rows.Count,
            FilteredCount = filtered.Count,
            GpuAvailable = gpuAvailable,
            GpuUnavailableReason = gpuAvailable ? "" : gpuUnavailableReason,
            Processes = filtered.Take(NormalizeTake(query.Take)).ToList()
        };
    }

    public CompanionWindowSnapshot GetWindowSnapshot(CompanionProcessQuery? query = null)
    {
        query ??= new CompanionProcessQuery();
        DateTimeOffset capturedUtc = DateTimeOffset.UtcNow;
        List<CompanionWindowInfo> windows = EnumerateWindows(capturedUtc);
        foreach (CompanionWindowInfo window in windows)
            window.ProcessName = TryGetProcessName(window.Pid);

        IEnumerable<CompanionWindowInfo> filtered = windows;
        string search = (query.Query ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(window =>
                Contains(window.Title, search) ||
                Contains(window.ProcessName, search) ||
                Contains(window.ClassName, search) ||
                window.Pid.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        filtered = (query.Sort ?? "").Trim().ToLowerInvariant() switch
        {
            "pid" => filtered.OrderBy(window => window.Pid),
            "process" or "name" => filtered.OrderBy(window => window.ProcessName).ThenBy(window => window.Title),
            "class" => filtered.OrderBy(window => window.ClassName).ThenBy(window => window.Title),
            _ => filtered.OrderBy(window => window.Title)
        };

        List<CompanionWindowInfo> filteredList = filtered.ToList();
        return new CompanionWindowSnapshot
        {
            CapturedUtc = capturedUtc,
            TotalCount = windows.Count,
            FilteredCount = filteredList.Count,
            Windows = filteredList.Take(NormalizeTake(query.Take)).ToList()
        };
    }

    public string BuildCompactProcessSummary(CompanionProcessQuery? query = null)
    {
        CompanionProcessSnapshot snapshot = GetProcessSnapshot(query);
        var sb = new StringBuilder();
        sb.AppendLine("Captured UTC: " + snapshot.CapturedUtc.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("Rows: " + snapshot.Processes.Count.ToString(CultureInfo.InvariantCulture) + " of " + snapshot.FilteredCount.ToString(CultureInfo.InvariantCulture) + " filtered; total RAM " + snapshot.TotalRamGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB.");
        sb.AppendLine("GPU: " + (snapshot.GpuAvailable ? "available" : "unavailable: " + snapshot.GpuUnavailableReason));
        foreach (CompanionProcessInfo process in snapshot.Processes.Take(NormalizeTake(query?.Take ?? 30)))
        {
            sb.AppendLine(
                process.Pid.ToString(CultureInfo.InvariantCulture) + " | " +
                process.Name + " | CPU " + process.CpuPercentDisplay + " | GPU " + process.GpuPercentDisplay + " | RAM " +
                process.RamGbDisplay + " (" + process.RamPercentDisplay + ") | Admin " + process.AdminState + " | " +
                FirstNonEmpty(process.WindowSummary, "no window") + " | " +
                FirstNonEmpty(process.ExecutablePath, process.UnavailableReason));
        }
        return sb.ToString();
    }

    public string BuildCompactWindowSummary(CompanionProcessQuery? query = null)
    {
        CompanionWindowSnapshot snapshot = GetWindowSnapshot(query);
        var sb = new StringBuilder();
        sb.AppendLine("Captured UTC: " + snapshot.CapturedUtc.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("Windows: " + snapshot.Windows.Count.ToString(CultureInfo.InvariantCulture) + " of " + snapshot.FilteredCount.ToString(CultureInfo.InvariantCulture));
        foreach (CompanionWindowInfo window in snapshot.Windows.Take(NormalizeTake(query?.Take ?? 30)))
        {
            sb.AppendLine(
                window.Pid.ToString(CultureInfo.InvariantCulture) + " | " +
                FirstNonEmpty(window.ProcessName, "unknown") + " | " +
                window.Title + " | " + window.ClassName + " | " + window.HandleHex);
        }
        return sb.ToString();
    }

    public CompanionProcessMutationResult KillProcess(int pid, bool entireTree = true)
    {
        if (pid <= 4)
            return CompanionProcessMutationResult.Fail("Refusing to kill a system process PID.");
        if (pid == Environment.ProcessId)
            return CompanionProcessMutationResult.Fail("Refusing to kill the Companion process itself.");

        try
        {
            using Process process = Process.GetProcessById(pid);
            string name = SafeRead(() => process.ProcessName, "process");
            if (process.HasExited)
                return new CompanionProcessMutationResult { Ok = true, Pid = pid, Name = name, Message = name + " has already exited." };

            process.Kill(entireTree);
            return new CompanionProcessMutationResult
            {
                Ok = true,
                Pid = pid,
                Name = name,
                Message = "Kill signal sent to " + name + " (" + pid.ToString(CultureInfo.InvariantCulture) + ")."
            };
        }
        catch (Exception ex)
        {
            return CompanionProcessMutationResult.Fail("Kill failed: " + ex.Message, pid);
        }
    }

    public CompanionProcessMutationResult StartProcess(CompanionProcessStartRequest request)
    {
        request ??= new CompanionProcessStartRequest();
        string path = (request.Path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path))
            return CompanionProcessMutationResult.Fail("Start failed: path is required.");

        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path) && !Directory.Exists(path))
                return CompanionProcessMutationResult.Fail("Start failed: file or folder was not found.");

            string workingDirectory = (request.WorkingDirectory ?? "").Trim();
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                workingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = request.Arguments ?? "",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            int pid = 0;
            string name = Path.GetFileNameWithoutExtension(path);
            if (process != null)
            {
                pid = SafeRead(() => process.Id, 0);
                name = SafeRead(() => process.ProcessName, name);
                process.Dispose();
            }

            return new CompanionProcessMutationResult
            {
                Ok = true,
                Pid = pid,
                Name = name,
                Path = path,
                Message = "Started " + Path.GetFileName(path) + (pid > 0 ? " as PID " + pid.ToString(CultureInfo.InvariantCulture) + "." : ".")
            };
        }
        catch (Exception ex)
        {
            return CompanionProcessMutationResult.Fail("Start failed: " + ex.Message);
        }
    }

    public CompanionProcessBrowserSnapshot BrowseFileSystem(string path, bool executableOnly = false)
    {
        string resolved = ResolveBrowserPath(path);
        var snapshot = new CompanionProcessBrowserSnapshot
        {
            Path = resolved,
            ParentPath = GetBrowserParent(resolved),
            ExecutableOnly = executableOnly
        };

        if (string.IsNullOrWhiteSpace(resolved))
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady).OrderBy(drive => drive.Name))
            {
                snapshot.Entries.Add(new CompanionProcessBrowserEntry
                {
                    Name = drive.Name,
                    Path = drive.RootDirectory.FullName,
                    Kind = "drive",
                    IsDirectory = true
                });
            }
            snapshot.Count = snapshot.Entries.Count;
            return snapshot;
        }

        if (!Directory.Exists(resolved))
        {
            snapshot.Error = "Folder was not found.";
            return snapshot;
        }

        try
        {
            foreach (DirectoryInfo directory in new DirectoryInfo(resolved).EnumerateDirectories().OrderBy(info => info.Name).Take(MaxBrowserEntries))
            {
                if ((directory.Attributes & FileAttributes.Hidden) != 0)
                    continue;
                snapshot.Entries.Add(new CompanionProcessBrowserEntry
                {
                    Name = directory.Name,
                    Path = directory.FullName,
                    Kind = "folder",
                    IsDirectory = true,
                    LastWriteUtc = directory.LastWriteTimeUtc
                });
            }

            IEnumerable<FileInfo> files = new DirectoryInfo(resolved).EnumerateFiles()
                .Where(file => !executableOnly || IsStartableFile(file.FullName))
                .OrderBy(file => file.Name)
                .Take(Math.Max(0, MaxBrowserEntries - snapshot.Entries.Count));
            foreach (FileInfo file in files)
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0)
                    continue;
                snapshot.Entries.Add(new CompanionProcessBrowserEntry
                {
                    Name = file.Name,
                    Path = file.FullName,
                    Kind = IsStartableFile(file.FullName) ? "startable" : "file",
                    IsDirectory = false,
                    SizeBytes = file.Length,
                    LastWriteUtc = file.LastWriteTimeUtc
                });
            }
            snapshot.Count = snapshot.Entries.Count;
        }
        catch (Exception ex)
        {
            snapshot.Error = ex.Message;
        }

        return snapshot;
    }

    private CompanionProcessInfo ReadProcessInfo(
        Process process,
        int pid,
        Dictionary<int, List<CompanionWindowInfo>> windowsByPid,
        ulong totalRamBytes,
        double totalRamGb,
        Dictionary<int, double> gpuByPid,
        bool gpuAvailable,
        string gpuUnavailableReason,
        DateTimeOffset capturedUtc)
    {
        var unavailable = new List<string>();
        string name = SafeRead(() => process.ProcessName, "");
        string mainWindowTitle = SafeRead(() => process.MainWindowTitle, "");
        string path = TryGetProcessImagePath(pid, process, out string pathError);
        if (!string.IsNullOrWhiteSpace(pathError))
            unavailable.Add(pathError);

        long workingSetBytes = SafeRead(() => process.WorkingSet64, -1L);
        if (workingSetBytes < 0)
            unavailable.Add("RAM access denied");

        TimeSpan? cpuTime = SafeReadNullable(() => process.TotalProcessorTime);
        double? cpuPercent = TryCalculateCpuPercent(pid, cpuTime, capturedUtc);
        double ramGb = workingSetBytes > 0 ? RoundGb(workingSetBytes / BytesPerGb) : 0;
        double? ramPercent = totalRamBytes > 0 && workingSetBytes >= 0
            ? Math.Round(workingSetBytes / (double)totalRamBytes * 100.0, 1)
            : null;

        List<string> titles = new();
        if (windowsByPid.TryGetValue(pid, out List<CompanionWindowInfo>? processWindows))
            titles.AddRange(processWindows.Select(window => window.Title));
        if (!string.IsNullOrWhiteSpace(mainWindowTitle))
            titles.Add(mainWindowTitle);
        titles = titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        bool? elevated = TryGetProcessElevation(pid, out string elevationReason);
        if (!string.IsNullOrWhiteSpace(elevationReason))
            unavailable.Add(elevationReason);

        double? gpuPercent = null;
        if (gpuAvailable)
            gpuPercent = Math.Round(gpuByPid.TryGetValue(pid, out double gpu) ? gpu : 0, 1);

        return new CompanionProcessInfo
        {
            Pid = pid,
            Name = name,
            MainWindowTitle = mainWindowTitle,
            WindowTitles = titles,
            WindowCount = processWindows?.Count ?? titles.Count,
            WindowSummary = BuildWindowSummary(titles),
            ExecutablePath = path,
            DirectoryPath = string.IsNullOrWhiteSpace(path) ? "" : Path.GetDirectoryName(path) ?? "",
            HasExecutablePath = !string.IsNullOrWhiteSpace(path) && File.Exists(path),
            CpuPercent = cpuPercent,
            GpuPercent = gpuPercent,
            GpuAvailable = gpuAvailable,
            GpuUnavailableReason = gpuAvailable ? "" : gpuUnavailableReason,
            RamPercent = ramPercent,
            RamGb = ramGb,
            RamTotalGb = totalRamGb,
            IsAdmin = elevated,
            AdminState = elevated.HasValue ? (elevated.Value ? "admin" : "notAdmin") : "unknown",
            AccessDenied = unavailable.Count > 0,
            UnavailableReason = string.Join("; ", unavailable.Distinct(StringComparer.OrdinalIgnoreCase)),
            CapturedUtc = capturedUtc
        };
    }

    private static IEnumerable<CompanionProcessInfo> ApplyProcessQuery(IEnumerable<CompanionProcessInfo> rows, CompanionProcessQuery query)
    {
        IEnumerable<CompanionProcessInfo> result = rows;
        if (query.WindowedOnly)
            result = result.Where(process => process.WindowCount > 0);

        string search = (query.Query ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            result = result.Where(process =>
                Contains(process.Name, search) ||
                Contains(process.ExecutablePath, search) ||
                Contains(process.WindowSummary, search) ||
                process.WindowTitles.Any(title => Contains(title, search)) ||
                process.Pid.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!query.IncludeSystem)
            result = result.Where(process => process.WindowCount > 0 || !string.IsNullOrWhiteSpace(process.ExecutablePath));

        return (query.Sort ?? "").Trim().ToLowerInvariant() switch
        {
            "cpu" => result.OrderByDescending(process => process.CpuPercent ?? -1).ThenBy(process => process.Name),
            "gpu" => result.OrderByDescending(process => process.GpuPercent ?? -1).ThenBy(process => process.Name),
            "ram" or "memory" => result.OrderByDescending(process => process.RamGb).ThenBy(process => process.Name),
            "pid" => result.OrderBy(process => process.Pid),
            "path" => result.OrderBy(process => process.ExecutablePath).ThenBy(process => process.Name),
            "window" => result.OrderByDescending(process => process.WindowCount).ThenBy(process => process.Name),
            _ => result.OrderBy(process => process.Name).ThenBy(process => process.Pid)
        };
    }

    private double? TryCalculateCpuPercent(int pid, TimeSpan? totalProcessorTime, DateTimeOffset capturedUtc)
    {
        if (!totalProcessorTime.HasValue)
            return null;

        lock (_sampleLock)
        {
            if (!_cpuSamples.TryGetValue(pid, out ProcessCpuSample? previous) || previous == null)
            {
                _cpuSamples[pid] = new ProcessCpuSample(totalProcessorTime.Value, capturedUtc);
                return null;
            }

            _cpuSamples[pid] = new ProcessCpuSample(totalProcessorTime.Value, capturedUtc);
            double elapsedMs = Math.Max(1, (capturedUtc - previous.CapturedUtc).TotalMilliseconds);
            double cpuMs = Math.Max(0, (totalProcessorTime.Value - previous.TotalProcessorTime).TotalMilliseconds);
            if (elapsedMs < 100)
                return null;

            double percent = cpuMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100.0;
            return Math.Round(Math.Max(0, Math.Min(100, percent)), 1);
        }
    }

    private void CleanupCpuSamples(HashSet<int> seenPids, DateTimeOffset capturedUtc)
    {
        lock (_sampleLock)
        {
            foreach (int pid in _cpuSamples.Keys.ToList())
            {
                if (!seenPids.Contains(pid) || capturedUtc - _cpuSamples[pid].CapturedUtc > TimeSpan.FromMinutes(2))
                    _cpuSamples.Remove(pid);
            }
        }
    }

    private static List<CompanionWindowInfo> EnumerateWindows(DateTimeOffset capturedUtc)
    {
        var windows = new List<CompanionWindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
                    return true;

                int length = GetWindowTextLength(hWnd);
                if (length <= 0)
                    return true;

                var title = new StringBuilder(length + 1);
                GetWindowText(hWnd, title, title.Capacity);
                string windowTitle = title.ToString().Trim();
                if (string.IsNullOrWhiteSpace(windowTitle))
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0)
                    return true;

                var className = new StringBuilder(256);
                GetClassName(hWnd, className, className.Capacity);
                windows.Add(new CompanionWindowInfo
                {
                    Handle = hWnd.ToInt64(),
                    HandleHex = "0x" + hWnd.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                    Pid = unchecked((int)pid),
                    Title = windowTitle,
                    ClassName = className.ToString(),
                    CapturedUtc = capturedUtc
                });
            }
            catch
            {
            }
            return true;
        }, IntPtr.Zero);

        return windows
            .GroupBy(window => window.Handle)
            .Select(group => group.First())
            .OrderBy(window => window.Title)
            .ToList();
    }

    private static Dictionary<int, double> TryReadGpuUsageByPid(out bool available, out string unavailableReason)
    {
        available = false;
        unavailableReason = "";
        var totals = new Dictionary<int, double>();
        try
        {
            Type? categoryType = Type.GetType("System.Diagnostics.PerformanceCounterCategory, System.Diagnostics.PerformanceCounter");
            Type? counterType = Type.GetType("System.Diagnostics.PerformanceCounter, System.Diagnostics.PerformanceCounter");
            if (categoryType == null || counterType == null)
            {
                unavailableReason = "GPU engine performance counters are not available in this build.";
                return totals;
            }

            object? category = Activator.CreateInstance(categoryType, "GPU Engine");
            MethodInfo? getInstanceNames = categoryType.GetMethod("GetInstanceNames", Type.EmptyTypes);
            string[] instances = getInstanceNames?.Invoke(category, null) as string[] ?? Array.Empty<string>();
            if (instances.Length == 0)
            {
                unavailableReason = "GPU Engine counters returned no process instances.";
                return totals;
            }

            ConstructorInfo? counterCtor = counterType.GetConstructor(new[] { typeof(string), typeof(string), typeof(string), typeof(bool) });
            MethodInfo? nextValue = counterType.GetMethod("NextValue", Type.EmptyTypes);
            if (counterCtor == null || nextValue == null)
            {
                unavailableReason = "GPU Engine counter APIs were not found.";
                return totals;
            }

            available = true;
            foreach (string instance in instances)
            {
                if (!TryParseGpuPid(instance, out int pid))
                    continue;

                object? counter = null;
                try
                {
                    counter = counterCtor.Invoke(new object[] { "GPU Engine", "Utilization Percentage", instance, true });
                    object? value = nextValue.Invoke(counter, null);
                    double percent = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (percent <= 0)
                        continue;
                    totals[pid] = Math.Round((totals.TryGetValue(pid, out double current) ? current : 0) + percent, 1);
                }
                catch
                {
                }
                finally
                {
                    if (counter is IDisposable disposable)
                        disposable.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            available = false;
            unavailableReason = "GPU Engine counter read failed: " + ex.Message;
            totals.Clear();
        }

        if (available)
            unavailableReason = "";
        return totals;
    }

    private static bool TryParseGpuPid(string instanceName, out int pid)
    {
        pid = 0;
        string marker = "pid_";
        int index = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;
        index += marker.Length;
        int end = index;
        while (end < instanceName.Length && char.IsDigit(instanceName[end]))
            end++;
        return end > index && int.TryParse(instanceName[index..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
    }

    private static string TryGetProcessImagePath(int pid, Process process, out string error)
    {
        error = "";
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle != IntPtr.Zero)
            {
                var builder = new StringBuilder(32768);
                int size = builder.Capacity;
                if (QueryFullProcessImageName(handle, 0, builder, ref size) && size > 0)
                    return builder.ToString();
            }
        }
        catch
        {
        }
        finally
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }

        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch (Exception ex)
        {
            error = "path unavailable: " + ex.GetType().Name;
            return "";
        }
    }

    private static bool? TryGetProcessElevation(int pid, out string reason)
    {
        reason = "";
        IntPtr processHandle = IntPtr.Zero;
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                reason = "admin state unknown: process access denied";
                return null;
            }

            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle) || tokenHandle == IntPtr.Zero)
            {
                reason = "admin state unknown: token access denied";
                return null;
            }

            var elevation = new TOKEN_ELEVATION();
            int size = Marshal.SizeOf<TOKEN_ELEVATION>();
            if (!GetTokenInformation(tokenHandle, TokenElevation, out elevation, size, out _))
            {
                reason = "admin state unknown: token elevation unavailable";
                return null;
            }

            return elevation.TokenIsElevated != 0;
        }
        catch (Exception ex)
        {
            reason = "admin state unknown: " + ex.GetType().Name;
            return null;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
                CloseHandle(tokenHandle);
            if (processHandle != IntPtr.Zero)
                CloseHandle(processHandle);
        }
    }

    private static Process[] SafeGetProcesses()
    {
        try { return Process.GetProcesses(); }
        catch { return Array.Empty<Process>(); }
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static ulong GetTotalPhysicalMemoryBytes()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static TimeSpan? SafeReadNullable(Func<TimeSpan> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private static bool Contains(string value, string query)
    {
        return !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildWindowSummary(IReadOnlyList<string> titles)
    {
        if (titles.Count == 0)
            return "";
        string first = titles[0].Length > 90 ? titles[0][..90] + "..." : titles[0];
        return titles.Count == 1 ? first : first + " +" + (titles.Count - 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private static int NormalizeTake(int take)
    {
        return Math.Max(1, Math.Min(MaxTake, take <= 0 ? DefaultTake : take));
    }

    private static double RoundGb(double value)
    {
        return Math.Round(Math.Max(0, value), 2);
    }

    private static string ResolveBrowserPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables((path ?? "").Trim());
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? "";
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string GetBrowserParent(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            DirectoryInfo? parent = Directory.GetParent(path);
            return parent?.FullName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsStartableFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".exe" or ".bat" or ".cmd" or ".ps1" or ".lnk" or ".msi" or ".com";
    }

    private sealed record ProcessCpuSample(TimeSpan TotalProcessorTime, DateTimeOffset CapturedUtc);

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

public sealed class CompanionProcessQuery
{
    public string Query { get; set; } = "";
    public bool WindowedOnly { get; set; }
    public bool IncludeSystem { get; set; } = true;
    public int Take { get; set; } = 200;
    public string Sort { get; set; } = "name";
}

public sealed class CompanionProcessStartRequest
{
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
}

public sealed class CompanionProcessMutationResult
{
    public bool Ok { get; set; }
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";

    public static CompanionProcessMutationResult Fail(string message, int pid = 0)
    {
        return new CompanionProcessMutationResult { Ok = false, Pid = pid, Message = message };
    }
}

public sealed class CompanionProcessBrowserSnapshot
{
    public string Path { get; set; } = "";
    public string ParentPath { get; set; } = "";
    public bool ExecutableOnly { get; set; }
    public int Count { get; set; }
    public string Error { get; set; } = "";
    public List<CompanionProcessBrowserEntry> Entries { get; set; } = new();
}

public sealed class CompanionProcessBrowserEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

public sealed class CompanionProcessSnapshot
{
    public DateTimeOffset CapturedUtc { get; set; }
    public int TotalCount { get; set; }
    public int FilteredCount { get; set; }
    public double TotalRamGb { get; set; }
    public bool GpuAvailable { get; set; }
    public string GpuUnavailableReason { get; set; } = "";
    public List<CompanionProcessInfo> Processes { get; set; } = new();
}

public sealed class CompanionProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string MainWindowTitle { get; set; } = "";
    public List<string> WindowTitles { get; set; } = new();
    public int WindowCount { get; set; }
    public string WindowSummary { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public bool HasExecutablePath { get; set; }
    public double? CpuPercent { get; set; }
    public double? GpuPercent { get; set; }
    public bool GpuAvailable { get; set; }
    public string GpuUnavailableReason { get; set; } = "";
    public double? RamPercent { get; set; }
    public double RamGb { get; set; }
    public double RamTotalGb { get; set; }
    public bool? IsAdmin { get; set; }
    public string AdminState { get; set; } = "unknown";
    public bool AccessDenied { get; set; }
    public string UnavailableReason { get; set; } = "";
    public DateTimeOffset CapturedUtc { get; set; }

    public string CpuPercentDisplay => CpuPercent.HasValue ? CpuPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "sampling";
    public string GpuPercentDisplay => GpuAvailable ? (GpuPercent ?? 0).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "unavailable";
    public string RamPercentDisplay => RamPercent.HasValue ? RamPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "unknown";
    public string RamGbDisplay => RamGb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    public string RamTotalGbDisplay => RamTotalGb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
}

public sealed class CompanionWindowSnapshot
{
    public DateTimeOffset CapturedUtc { get; set; }
    public int TotalCount { get; set; }
    public int FilteredCount { get; set; }
    public List<CompanionWindowInfo> Windows { get; set; } = new();
}

public sealed class CompanionWindowInfo
{
    public long Handle { get; set; }
    public string HandleHex { get; set; } = "";
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public DateTimeOffset CapturedUtc { get; set; }
}
