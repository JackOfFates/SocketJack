using ComputeSharp;

namespace SocketJack.WpfBasicGame;

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct BotKernel : IComputeShader {
    public readonly ReadWriteBuffer<BotSimState> States;
    public readonly ReadWriteBuffer<BotSimConfig> Configs;

    public BotKernel(ReadWriteBuffer<BotSimState> states, ReadWriteBuffer<BotSimConfig> configs) {
        States = states;
        Configs = configs;
    }

    public void Execute() {
        int i = ThreadIds.X;
        BotSimState s = States[i];
        BotSimConfig cfg = Configs[i];

        // PRNG chain (xorshift + LCG)
        uint h = (uint)(cfg.Seed ^ (i * 1103515245));
        h ^= h << 13;
        h ^= h >> 17;
        h ^= h << 5;
        float r1 = ((h & 1023u) / 1023f) - 0.5f;
        h = (h * 1664525u) + 1013904223u;
        float r2 = ((h & 1023u) / 1023f) - 0.5f;

        // Wander: GPU-driven timer + target regeneration
        s.WanderTimer -= 16f;
        if (s.WanderTimer <= 0f) {
            h = (h * 1664525u) + 1013904223u;
            s.WanderX = 30f + (h % 491u);
            h = (h * 1664525u) + 1013904223u;
            s.WanderY = 30f + (h % 301u);
            h = (h * 1664525u) + 1013904223u;
            s.WanderTimer = 350f + (h % 551u);
        }

        // Choose aim: target center when available, else wander point
        float aimX = cfg.HasTarget == 1 ? cfg.TargetCenterX : s.WanderX;
        float aimY = cfg.HasTarget == 1 ? cfg.TargetCenterY : s.WanderY;

        float dx = aimX - s.X;
        float dy = aimY - s.Y;

        float tx = dx + (r1 * cfg.Jitter);
        float ty = dy + (r2 * cfg.Jitter);

        // Precomputed constants: accel = 0.02 * 16, drag = exp(-0.12 * 16)
        const float accel = 0.32f;
        const float drag = 0.146607f;

        s.Vx = (s.Vx + (tx * accel)) * drag;
        s.Vy = (s.Vy + (ty * accel)) * drag;

        float maxSpd = cfg.MaxSpeedPerMs;
        float vlenSq = (s.Vx * s.Vx) + (s.Vy * s.Vy);
        if (vlenSq > (maxSpd * maxSpd)) {
            float scale = maxSpd * Hlsl.Rsqrt(vlenSq);
            s.Vx *= scale;
            s.Vy *= scale;
        }

        s.X += s.Vx * 16f;
        s.Y += s.Vy * 16f;

        // Hit detection: GPU checks if cursor is within padded target bounds
        float cm = cfg.ClickMargin;
        s.HitTarget = (cfg.HasTarget == 1 &&
                       s.X >= cfg.TargetLeft + cm && s.X <= cfg.TargetLeft + cfg.TargetSize - cm &&
                       s.Y >= cfg.TargetTop + cm && s.Y <= cfg.TargetTop + cfg.TargetSize - cm) ? 1 : 0;

        States[i] = s;
    }
}
