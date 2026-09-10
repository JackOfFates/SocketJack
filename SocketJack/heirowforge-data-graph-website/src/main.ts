import { generatePseudoData } from './data';
import { drawBars, drawLine } from './graph';

const latencyCanvas = document.querySelector<HTMLCanvasElement>('#latencyChart')!;
const throughputCanvas = document.querySelector<HTMLCanvasElement>('#throughputChart')!;
const dataset = document.querySelector<HTMLSelectElement>('#dataset')!;
const regenerate = document.querySelector<HTMLButtonElement>('#regenerate')!;
const status = document.querySelector<HTMLOutputElement>('#status')!;
let generation = 0;

function render(reason = 'Dataset ready') {
  const variant = Number(dataset.value) + generation;
  const data = generatePseudoData(36, variant);
  drawLine(latencyCanvas, data.latency); drawBars(throughputCanvas, data.throughput);
  const average = Math.round(data.latency.reduce((a, b) => a + b, 0) / data.latency.length);
  document.querySelector('#avgLatency')!.textContent = `${average} ms`;
  document.querySelector('#peakThroughput')!.textContent = `${Math.max(...data.throughput).toLocaleString()} req/s`;
  document.querySelector('#errorRate')!.textContent = `${(data.errorRate * 100).toFixed(1)}%`;
  status.value = reason;
}

dataset.addEventListener('change', () => { generation = 0; render(`${dataset.options[dataset.selectedIndex].text} loaded`); });
regenerate.addEventListener('click', () => { generation = (generation + 1) % 10; render(`Regenerated sample ${generation + 1}`); });
window.addEventListener('resize', () => render(status.value));
render();
