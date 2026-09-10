function setup(canvas: HTMLCanvasElement) {
  const ratio = window.devicePixelRatio || 1;
  const width = Math.max(280, Math.floor(canvas.clientWidth));
  const height = 300;
  canvas.width = Math.floor(width * ratio); canvas.height = Math.floor(height * ratio);
  const ctx = canvas.getContext('2d')!; ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  return { ctx, width, height };
}

function grid(ctx: CanvasRenderingContext2D, width: number, height: number) {
  ctx.strokeStyle = '#303950'; ctx.lineWidth = 1;
  for (let y = 44; y < height - 28; y += 48) { ctx.beginPath(); ctx.moveTo(48, y); ctx.lineTo(width - 18, y); ctx.stroke(); }
}

export function drawLine(canvas: HTMLCanvasElement, values: number[]): void {
  const { ctx, width, height } = setup(canvas); grid(ctx, width, height);
  const min = Math.min(...values) - 4, max = Math.max(...values) + 4, range = max - min || 1;
  const point = (value: number, i: number) => ({ x: 48 + i * ((width - 70) / (values.length - 1)), y: height - 30 - ((value - min) / range) * (height - 65) });
  const gradient = ctx.createLinearGradient(0, 20, 0, height); gradient.addColorStop(0, 'rgba(94,156,255,.5)'); gradient.addColorStop(1, 'rgba(94,156,255,0)');
  ctx.beginPath(); values.forEach((v, i) => { const p = point(v, i); i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y); }); ctx.lineTo(width - 22, height - 30); ctx.lineTo(48, height - 30); ctx.closePath(); ctx.fillStyle = gradient; ctx.fill();
  ctx.beginPath(); values.forEach((v, i) => { const p = point(v, i); i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y); }); ctx.strokeStyle = '#65a3ff'; ctx.lineWidth = 3; ctx.stroke();
  ctx.fillStyle = '#9aa8c7'; ctx.font = '12px Segoe UI'; ctx.fillText(`${max - 4} ms`, 8, 38); ctx.fillText(`${min + 4} ms`, 8, height - 28);
}

export function drawBars(canvas: HTMLCanvasElement, values: number[]): void {
  const { ctx, width, height } = setup(canvas); grid(ctx, width, height);
  const bucketSize = Math.floor(values.length / 8);
  const buckets = Array.from({ length: 8 }, (_, i) => values.slice(i * bucketSize, (i + 1) * bucketSize).reduce((a, b) => a + b, 0) / bucketSize);
  const max = Math.max(...buckets) * 1.08; const usable = width - 70; const gap = 8; const barWidth = usable / buckets.length - gap;
  buckets.forEach((value, i) => { const barHeight = (value / max) * (height - 58); const x = 48 + i * (barWidth + gap); const y = height - 30 - barHeight; const g = ctx.createLinearGradient(0, y, 0, height - 30); g.addColorStop(0, '#7ee2ae'); g.addColorStop(1, '#348d70'); ctx.fillStyle = g; ctx.fillRect(x, y, barWidth, barHeight); });
  ctx.fillStyle = '#9aa8c7'; ctx.font = '12px Segoe UI'; ctx.fillText('Aggregated interval buckets', 48, height - 9);
}
