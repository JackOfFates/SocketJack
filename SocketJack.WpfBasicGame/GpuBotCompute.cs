using System;
using ComputeSharp;

namespace SocketJack.WpfBasicGame;

internal interface IBotComputeEngine : IDisposable {
    bool IsAvailable { get; }
    void UpdateBots(BotSimState[] states, BotSimConfig[] configs);
}

internal readonly struct BotSimConfig {
    public readonly float AimX;
    public readonly float AimY;
    public readonly float MaxSpeed;
    public readonly float Jitter;
    public readonly int Seed;

    public BotSimConfig(float aimX, float aimY, float maxSpeed, float jitter, int seed) {
        AimX = aimX;
        AimY = aimY;
        MaxSpeed = maxSpeed;
        Jitter = jitter;
        Seed = seed;
    }
}

internal struct BotSimState {
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
}

internal sealed class CpuBotComputeEngine : IBotComputeEngine {
    public bool IsAvailable => false;

    public void UpdateBots(BotSimState[] states, BotSimConfig[] configs) {
        // Placeholder: CPU path should remain in NpcBotClient for now.
        // This engine exists to keep call sites simple when GPU isn't available.
    }

    public void Dispose() {
    }
}

internal sealed class GpuBotComputeEngine : IBotComputeEngine {
    private readonly GraphicsDevice? _device;
    private bool _disabled;

    public bool IsAvailable => _device != null && !_disabled;

    public GpuBotComputeEngine() {
        try {
            _device = GraphicsDevice.GetDefault();
        } catch {
            _device = null;
        }
    }

    public void UpdateBots(BotSimState[] states, BotSimConfig[] configs) {
        if (_device == null || _disabled)
            return;

        if (states.Length == 0)
            return;

        if (configs.Length < states.Length)
            return;

        try {
            using var stateBuffer = _device.AllocateReadWriteBuffer(states);
            using var cfgBuffer = _device.AllocateReadOnlyBuffer(configs);
            _device.For(states.Length, new BotKernel(stateBuffer, cfgBuffer));
            stateBuffer.CopyTo(states);
        } catch {
            // If the GPU path faults (driver/device/shader issues), disable it for the process.
            _disabled = true;
        }
    }

    public void Dispose() {
    }
}
