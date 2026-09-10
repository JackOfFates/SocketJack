export type TelemetryData = { latency: number[]; throughput: number[]; errorRate: number };

export function generatePseudoData(count: number, variant = 0): TelemetryData {
  const latency: number[] = [];
  const throughput: number[] = [];
  for (let i = 0; i < count; i++) {
    const wave = Math.sin(i * 0.45 + variant * 0.7);
    const pulse = Math.cos(i * 0.18 + variant) * 0.5;
    latency.push(Math.round(38 + variant * 1.3 + wave * 9 + pulse * 5));
    throughput.push(Math.round(980 + variant * 28 + (1 - wave) * 170 + pulse * 80));
  }
  return { latency, throughput, errorRate: Number((0.018 + variant * 0.003).toFixed(3)) };
}
