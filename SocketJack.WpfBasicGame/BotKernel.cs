using ComputeSharp;

namespace SocketJack.WpfBasicGame;

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BotKernel : IComputeShader {
    public readonly ReadWriteBuffer<BotSimState> States;
    public readonly ReadOnlyBuffer<BotSimConfig> Configs;

    public BotKernel(ReadWriteBuffer<BotSimState> states, ReadOnlyBuffer<BotSimConfig> configs) {
        States = states;
        Configs = configs;
    }

    public void Execute() {
        int i = ThreadIds.X;
        BotSimState s = States[i];

        BotSimConfig cfg = Configs[i];

        float dx = cfg.AimX - s.X;
        float dy = cfg.AimY - s.Y;

        uint h = (uint)(cfg.Seed ^ (i * 1103515245));
        h ^= h << 13;
        h ^= h >> 17;
        h ^= h << 5;
        float r1 = ((h & 1023u) / 1023f) - 0.5f;
        h = (h * 1664525u) + 1013904223u;
        float r2 = ((h & 1023u) / 1023f) - 0.5f;

        float jx = r1 * cfg.Jitter;
        float jy = r2 * cfg.Jitter;

        float tx = dx + jx;
        float ty = dy + jy;

        const float dtMs = 16f;
        const float accelPerMs = 0.02f;
        const float dragPerMs = 0.12f;
        float accel = accelPerMs * dtMs;
        float drag = Hlsl.Exp(-dragPerMs * dtMs);
        float maxSpeedPerMs = cfg.MaxSpeed / dtMs;

        s.Vx += tx * accel;
        s.Vy += ty * accel;
        s.Vx *= drag;
        s.Vy *= drag;

        float vlenSq = (s.Vx * s.Vx) + (s.Vy * s.Vy);
        if (vlenSq > (maxSpeedPerMs * maxSpeedPerMs)) {
            float invV = Hlsl.Rsqrt(vlenSq);
            s.Vx *= invV * maxSpeedPerMs;
            s.Vy *= invV * maxSpeedPerMs;
        }

        s.X += s.Vx * dtMs;
        s.Y += s.Vy * dtMs;

        States[i] = s;
    }
}
