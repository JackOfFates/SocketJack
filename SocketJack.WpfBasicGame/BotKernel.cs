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

        float distSq = (dx * dx) + (dy * dy);
        float invLen = distSq > 1e-6f ? Hlsl.Rsqrt(distSq) : 0f;
        float nx = dx * invLen;
        float ny = dy * invLen;

        uint h = (uint)(cfg.Seed ^ (i * 1103515245));
        h ^= h << 13;
        h ^= h >> 17;
        h ^= h << 5;
        float r1 = ((h & 1023u) / 1023f) - 0.5f;
        h = (h * 1664525u) + 1013904223u;
        float r2 = ((h & 1023u) / 1023f) - 0.5f;

        float jx = r1 * cfg.Jitter;
        float jy = r2 * cfg.Jitter;

        float vx = (nx * cfg.MaxSpeed) + jx;
        float vy = (ny * cfg.MaxSpeed) + jy;

        float vlenSq = (vx * vx) + (vy * vy);
        if (vlenSq > (cfg.MaxSpeed * cfg.MaxSpeed)) {
            float invV = Hlsl.Rsqrt(vlenSq);
            vx *= invV * cfg.MaxSpeed;
            vy *= invV * cfg.MaxSpeed;
        }

        s.Vx = vx;
        s.Vy = vy;
        s.X += vx;
        s.Y += vy;

        States[i] = s;
    }
}
