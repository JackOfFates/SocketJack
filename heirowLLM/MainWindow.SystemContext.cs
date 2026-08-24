using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using heirowLLM;

namespace heirowLLM;

public partial class MainWindow
{
    private static Task<SystemContextResult> QueryWindowsSystemContextAsync(SystemContextQuery query, CancellationToken cancellationToken) =>
        Task.Run(() => QueryWindowsSystemContext(query, cancellationToken), cancellationToken);

    private static SystemContextResult QueryWindowsSystemContext(SystemContextQuery query, CancellationToken cancellationToken)
    {
        query ??= new SystemContextQuery();
        try
        {
            return query.Kind switch
            {
                "list_running_applications" => QueryRunningApplications(query, cancellationToken),
                "list_windows_services" => QueryWindowsServices(query, cancellationToken),
                "query_event_viewer" => QueryEventViewer(query, cancellationToken),
                _ => FailedSystemContext(query.Kind, "Unknown Windows context query kind.")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FailedSystemContext(query.Kind, ex.Message);
        }
    }

    private static SystemContextResult QueryRunningApplications(SystemContextQuery query, CancellationToken cancellationToken)
    {
        string filter = (query.Query ?? "").Trim();
        int take = Math.Clamp(query.Take <= 0 ? 40 : query.Take, 1, 100);
        var rows = new List<(string Name, int Pid, string Title, bool Responding, long WorkingSet)>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string name = process.ProcessName ?? "";
                    string title = process.MainWindowTitle ?? "";
                    bool visible = process.MainWindowHandle != IntPtr.Zero || !string.IsNullOrWhiteSpace(title);
                    if (!query.IncludeBackground && !visible) continue;
                    if (!string.IsNullOrWhiteSpace(filter) &&
                        name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool responding;
                    try { responding = !visible || process.Responding; } catch { responding = false; }
                    long workingSet;
                    try { workingSet = Math.Max(0, process.WorkingSet64); } catch { workingSet = 0; }
                    rows.Add((name, process.Id, LimitWindowsContextText(title, 300), responding, workingSet));
                }
                catch { }
            }
        }

        var grouped = rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Dictionary<string, object>
            {
                ["name"] = group.Key,
                ["instanceCount"] = group.Count(),
                ["pids"] = group.Select(item => item.Pid).OrderBy(id => id).Take(24).ToArray(),
                ["windowTitles"] = group.Select(item => item.Title).Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray(),
                ["responding"] = group.All(item => item.Responding),
                ["workingSetMiB"] = Math.Round(group.Sum(item => item.WorkingSet) / 1048576d, 1)
            })
            .OrderByDescending(item => Convert.ToInt32(item["instanceCount"]))
            .ThenBy(item => item["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool truncated = grouped.Count > take;
        return SuccessfulSystemContext(query.Kind, grouped.Take(take), truncated);
    }

    private static SystemContextResult QueryWindowsServices(SystemContextQuery query, CancellationToken cancellationToken)
    {
        string filter = (query.Query ?? "").Trim();
        string statusFilter = NormalizeServiceStatus(query.Status);
        int take = Math.Clamp(query.Take <= 0 ? 60 : query.Take, 1, 100);
        var items = new List<Dictionary<string, object>>();
        foreach (ServiceController service in ServiceController.GetServices())
        {
            using (service)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string name = service.ServiceName ?? "";
                    string displayName = service.DisplayName ?? "";
                    string status = NormalizeServiceStatus(service.Status.ToString());
                    if (!string.IsNullOrWhiteSpace(filter) &&
                        name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!string.IsNullOrWhiteSpace(statusFilter) && !string.Equals(status, statusFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    string startType;
                    try { startType = service.StartType.ToString(); } catch { startType = "Unknown"; }
                    items.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["displayName"] = displayName,
                        ["status"] = status,
                        ["startType"] = startType
                    });
                }
                catch { }
            }
        }
        items = items.OrderBy(item => item["displayName"]?.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
        bool truncated = items.Count > take;
        return SuccessfulSystemContext(query.Kind, items.Take(take), truncated);
    }

    private static SystemContextResult QueryEventViewer(SystemContextQuery query, CancellationToken cancellationToken)
    {
        string logName = string.IsNullOrWhiteSpace(query.LogName) ? "both" : query.LogName.Trim();
        if (!logName.Equals("both", StringComparison.OrdinalIgnoreCase) &&
            !logName.Equals("Application", StringComparison.OrdinalIgnoreCase) &&
            !logName.Equals("System", StringComparison.OrdinalIgnoreCase))
            return FailedSystemContext(query.Kind, "Event Viewer access is limited to Application, System, or both.");

        string[] logs = logName.Equals("both", StringComparison.OrdinalIgnoreCase) ? new[] { "Application", "System" } : new[] { logName };
        int take = Math.Clamp(query.Take <= 0 ? 30 : query.Take, 1, 100);
        int sinceMinutes = Math.Clamp(query.SinceMinutes <= 0 ? 120 : query.SinceMinutes, 1, 10080);
        long windowMs = (long)TimeSpan.FromMinutes(sinceMinutes).TotalMilliseconds;
        string xpath = "*[System[TimeCreated[timediff(@SystemTime) <= " + windowMs + "]]]";
        HashSet<string> levels = new(query.Levels ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        string providerFilter = (query.Provider ?? "").Trim();
        string messageFilter = (query.Query ?? "").Trim();
        var items = new List<Dictionary<string, object>>();
        var warnings = new List<string>();

        foreach (string log in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var eventQuery = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true, TolerateQueryErrors = true };
                using var reader = new EventLogReader(eventQuery);
                int scanned = 0;
                while (scanned < 2000 && items.Count < take * 4)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using EventRecord? record = reader.ReadEvent();
                    if (record == null) break;
                    scanned++;
                    string level = EventLevelName(record.Level);
                    string provider = record.ProviderName ?? "";
                    if (levels.Count > 0 && !levels.Contains(level)) continue;
                    if (!string.IsNullOrWhiteSpace(providerFilter) && provider.IndexOf(providerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (query.EventId.HasValue && record.Id != query.EventId.Value) continue;
                    string message;
                    try { message = record.FormatDescription() ?? ""; }
                    catch { message = "Event message text was unavailable."; }
                    message = LimitWindowsContextText(message, 2000);
                    if (!string.IsNullOrWhiteSpace(messageFilter) && message.IndexOf(messageFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    items.Add(new Dictionary<string, object>
                    {
                        ["logName"] = log,
                        ["timeCreatedUtc"] = record.TimeCreated?.ToUniversalTime().ToString("O") ?? "",
                        ["provider"] = provider,
                        ["eventId"] = record.Id,
                        ["level"] = level,
                        ["machineName"] = record.MachineName ?? "",
                        ["message"] = message
                    });
                }
            }
            catch (Exception ex)
            {
                warnings.Add(log + ": " + LimitWindowsContextText(ex.Message, 240));
            }
        }

        items = items.OrderByDescending(item => item["timeCreatedUtc"]?.ToString(), StringComparer.Ordinal).ToList();
        bool truncated = items.Count > take;
        SystemContextResult result = SuccessfulSystemContext(query.Kind, items.Take(take), truncated);
        result.Warning = string.Join(" | ", warnings);
        if (items.Count == 0 && warnings.Count == logs.Length)
        {
            result.Ok = false;
            result.Error = "Event Viewer logs could not be read.";
        }
        return result;
    }

    private static SystemContextResult SuccessfulSystemContext(string kind, IEnumerable<Dictionary<string, object>> items, bool truncated) =>
        new()
        {
            Ok = true,
            Kind = kind,
            CapturedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Items = items.ToList(),
            Truncated = truncated
        };

    private static SystemContextResult FailedSystemContext(string kind, string error) => new()
    {
        Ok = false,
        Kind = kind ?? "",
        CapturedUtc = DateTimeOffset.UtcNow.ToString("O"),
        Error = LimitWindowsContextText(error, 600)
    };

    private static string EventLevelName(byte? level) => level switch
    {
        1 => "critical",
        2 => "error",
        3 => "warning",
        4 => "information",
        5 => "verbose",
        _ => "unknown"
    };

    private static string NormalizeServiceStatus(string? value) => (value ?? "").Trim().Replace("_", "").Replace(" ", "").ToLowerInvariant() switch
    {
        "startpending" => "start_pending",
        "stoppending" => "stop_pending",
        "continuepending" => "continue_pending",
        "pausepending" => "pause_pending",
        string status => status
    };

    private static string LimitWindowsContextText(string? value, int maxLength)
    {
        string text = (value ?? "").Replace('\0', ' ').Trim();
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
    }
}
