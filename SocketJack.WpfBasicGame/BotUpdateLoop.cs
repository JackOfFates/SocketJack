using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SocketJack.WpfBasicGame;

internal interface ITickableBot {
    bool IsActive { get; }
    void Tick(long nowTimestamp);
}

internal sealed class BotUpdateLoop : IDisposable {
    private readonly List<NpcBotClient> _bots = new();
    private readonly object _gate = new();

    private readonly IBotComputeEngine _engine;
    private CancellationTokenSource? _cts;
    private Task? _task;

    private BotSimState[] _states = Array.Empty<BotSimState>();
    private BotSimConfig[] _configs = Array.Empty<BotSimConfig>();

    public BotUpdateLoop(bool preferGpu) {
        if (preferGpu) {
            var gpu = new GpuBotComputeEngine();
            _engine = gpu.IsAvailable ? gpu : new CpuBotComputeEngine();
            if (!gpu.IsAvailable)
                gpu.Dispose();
        } else {
            _engine = new CpuBotComputeEngine();
        }
    }

    public void Add(NpcBotClient bot) {
        lock (_gate)
            _bots.Add(bot);
    }

    public void Remove(NpcBotClient bot) {
        lock (_gate)
            _bots.Remove(bot);
    }

    public void Start() {
        if (_task is { IsCompleted: false })
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _task = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                TickAll();
        }, ct);
    }

    private void TickAll() {
        var nowTs = Stopwatch.GetTimestamp();

        List<NpcBotClient> snapshot;
        lock (_gate)
            snapshot = _bots.Count == 0 ? new List<NpcBotClient>() : new List<NpcBotClient>(_bots);

        if (snapshot.Count == 0)
            return;

        var activeCount = 0;
        for (var i = 0; i < snapshot.Count; i++) {
            if (snapshot[i].IsActive)
                activeCount++;
        }

        if (activeCount == 0)
            return;

        if (_states.Length < snapshot.Count)
            _states = new BotSimState[snapshot.Count];

        if (_configs.Length < snapshot.Count)
            _configs = new BotSimConfig[snapshot.Count];

        var seed = unchecked((int)nowTs);

        if (!_engine.IsAvailable) {
            for (var i = 0; i < snapshot.Count; i++) {
                var b = snapshot[i];
                if (!b.IsActive)
                    continue;
                b.Tick(nowTs);
            }
            return;
        }

        for (var i = 0; i < snapshot.Count; i++) {
            var b = snapshot[i];
            if (!b.IsActive)
                continue;

            _states[i] = b.ExportSimState();
            _configs[i] = b.GetSimConfig(seed + i);
        }

        _engine.UpdateBots(_states, _configs);

        for (var i = 0; i < snapshot.Count; i++) {
            var b = snapshot[i];
            if (!b.IsActive)
                continue;

            b.ImportSimState(_states[i], nowTs);
        }
    }

    public void Stop() {
        _cts?.Cancel();
    }

    public void Dispose() {
        Stop();
        _cts?.Dispose();
        _engine.Dispose();
    }
}
