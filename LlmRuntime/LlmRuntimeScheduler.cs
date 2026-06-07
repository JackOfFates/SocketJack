using System.Collections.Concurrent;

namespace LlmRuntime;

internal sealed class LlmRuntimeScheduler
{
    private readonly ConcurrentDictionary<string, int> _activeJobs = new(StringComparer.OrdinalIgnoreCase);

    public LlmScheduledJob BeginJob(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return new LlmScheduledJob(this, "");

        _activeJobs.AddOrUpdate(instanceId, 1, (_, value) => value + 1);
        return new LlmScheduledJob(this, instanceId);
    }

    public int GetActiveJobs(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) && _activeJobs.TryGetValue(instanceId, out int count)
            ? Math.Max(0, count)
            : 0;

    public int GetQueuedJobs(string instanceId, int concurrencyLimit)
    {
        int active = GetActiveJobs(instanceId);
        int limit = Math.Max(1, concurrencyLimit);
        return Math.Max(0, active - limit);
    }

    public IReadOnlyList<LlmSchedulerInstanceStatus> BuildStatus(IEnumerable<LlmModelInfo> models)
    {
        var instances = new List<LlmSchedulerInstanceStatus>();
        foreach (var model in models)
        {
            foreach (var instance in model.LoadedInstances)
            {
                instances.Add(new LlmSchedulerInstanceStatus
                {
                    InstanceId = instance.Id,
                    ModelKey = string.IsNullOrWhiteSpace(instance.ModelKey) ? model.Key : instance.ModelKey,
                    DeviceId = instance.DeviceId,
                    Backend = instance.Backend,
                    ParallelismMode = instance.Config.ParallelismMode.ToString(),
                    ParallelismPlacement = instance.Config.ParallelismPlacement.ToString(),
                    ParallelTensor = instance.Config.ParallelTensor,
                    TensorParallelSize = instance.Config.TensorParallelSize,
                    TargetDeviceIds = instance.Config.TargetDeviceIds,
                    NetworkNodeIds = instance.Config.NetworkNodeIds,
                    MaxGpuLoadPercent = instance.Config.MaxGpuLoadPercent,
                    MaxVramUsagePercent = instance.Config.MaxVramUsagePercent,
                    Modalities = instance.Modalities,
                    ActiveJobs = GetActiveJobs(instance.Id),
                    QueuedJobs = GetQueuedJobs(instance.Id, instance.ConcurrencyLimit),
                    ConcurrencyLimit = Math.Max(1, instance.ConcurrencyLimit),
                    Health = instance.Health,
                    LoadedAtUtc = instance.LoadedAtUtc
                });
            }
        }

        return instances
            .OrderBy(instance => instance.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void EndJob(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        _activeJobs.AddOrUpdate(instanceId, 0, (_, value) => Math.Max(0, value - 1));
    }

    internal readonly struct LlmScheduledJob : IDisposable
    {
        private readonly LlmRuntimeScheduler? _scheduler;
        private readonly string _instanceId;

        public LlmScheduledJob(LlmRuntimeScheduler scheduler, string instanceId)
        {
            _scheduler = scheduler;
            _instanceId = instanceId ?? "";
        }

        public void Dispose() => _scheduler?.EndJob(_instanceId);
    }
}

public sealed class LlmSchedulerInstanceStatus
{
    public string InstanceId { get; init; } = "";

    public string ModelKey { get; init; } = "";

    public string DeviceId { get; init; } = "";

    public string Backend { get; init; } = "";

    public string ParallelismMode { get; init; } = "";

    public string ParallelismPlacement { get; init; } = "";

    public bool ParallelTensor { get; init; }

    public int TensorParallelSize { get; init; } = 1;

    public IReadOnlyList<string> TargetDeviceIds { get; init; } = [];

    public IReadOnlyList<string> NetworkNodeIds { get; init; } = [];

    public int MaxGpuLoadPercent { get; init; } = 100;

    public int MaxVramUsagePercent { get; init; } = 100;

    public IReadOnlyList<string> Modalities { get; init; } = [];

    public int QueuedJobs { get; init; }

    public int ActiveJobs { get; init; }

    public int ConcurrencyLimit { get; init; } = 1;

    public string Health { get; init; } = "healthy";

    public DateTimeOffset LoadedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
