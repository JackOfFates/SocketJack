using System;
using ComputeSharp;

namespace SocketJack.WpfBasicGame;

internal interface IBotComputeEngine : IDisposable {
    bool IsAvailable { get; }
    void UpdateBots(BotSimState[] states, BotSimConfig[] configs, int count);
}

internal readonly struct BotSimConfig {
    public readonly float MaxSpeedPerMs;
    public readonly float Jitter;
    public readonly int Seed;
    public readonly float TargetCenterX;
    public readonly float TargetCenterY;
    public readonly float TargetLeft;
    public readonly float TargetTop;
    public readonly float TargetSize;
    public readonly float ClickMargin;
    public readonly int HasTarget;

    public BotSimConfig(float maxSpeed, float jitter, int seed,
                        float targetCenterX, float targetCenterY,
                        float targetLeft, float targetTop,
                        float targetSize, float clickMargin, int hasTarget) {
        MaxSpeedPerMs = maxSpeed / 16f;
        Jitter = jitter;
        Seed = seed;
        TargetCenterX = targetCenterX;
        TargetCenterY = targetCenterY;
        TargetLeft = targetLeft;
        TargetTop = targetTop;
        TargetSize = targetSize;
        ClickMargin = clickMargin;
        HasTarget = hasTarget;
    }
}

internal struct BotSimState {
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
    public float WanderX;
    public float WanderY;
    public float WanderTimer;
    public int HitTarget;
}

internal sealed class CpuBotComputeEngine : IBotComputeEngine {
    public bool IsAvailable => false;

    public void UpdateBots(BotSimState[] states, BotSimConfig[] configs, int count) {
    }

    public void Dispose() {
    }
}

internal sealed class GpuBotComputeEngine : IBotComputeEngine {
    private readonly GraphicsDevice? _device;
    private bool _disabled;

    private ReadWriteBuffer<BotSimState>? _stateBuffer;
    private ReadWriteBuffer<BotSimConfig>? _cfgBuffer;
    private int _bufferCapacity;

    public bool IsAvailable => _device != null && !_disabled;

    public GpuBotComputeEngine() {
        try {
            _device = GraphicsDevice.GetDefault();
        } catch {
            _device = null;
        }
    }

    public void UpdateBots(BotSimState[] states, BotSimConfig[] configs, int count) {
        if (_device == null || _disabled || count <= 0)
            return;

        try {
            EnsureBufferCapacity(states.Length);

            _stateBuffer!.CopyFrom(states);
            _cfgBuffer!.CopyFrom(configs);

            _device.For(count, new BotKernel(_stateBuffer!, _cfgBuffer!));

            _stateBuffer!.CopyTo(states);
        } catch {
            _disabled = true;
        }
    }

    private void EnsureBufferCapacity(int length) {
        if (length == _bufferCapacity && _stateBuffer != null && _cfgBuffer != null)
            return;

        _stateBuffer?.Dispose();
        _cfgBuffer?.Dispose();

        _bufferCapacity = length;
        _stateBuffer = _device!.AllocateReadWriteBuffer<BotSimState>(length);
        _cfgBuffer = _device!.AllocateReadWriteBuffer<BotSimConfig>(length);
    }

    public void Dispose() {
        _stateBuffer?.Dispose();
        _cfgBuffer?.Dispose();
    }
}
