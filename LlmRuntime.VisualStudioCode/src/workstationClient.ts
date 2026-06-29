import * as vscode from 'vscode';
import { OpenAiResponse, OpenAiToolCall, WorkstationModel } from './types';

type JsonObject = Record<string, unknown>;

interface ModelMetadata {
  id?: string;
  key?: string;
  displayName?: string;
  display_name?: string;
  supportsTools?: boolean;
  supportsImages?: boolean;
  supportsVision?: boolean;
  isLoaded?: boolean;
  isAvailable?: boolean;
  enabled?: boolean;
  isEnabled?: boolean;
  disabled?: boolean;
  status?: string;
  contextLength?: number;
  maxContextLength?: number;
  max_context_length?: number;
  maxOutputTokens?: number;
  capabilities?: {
    tool_calling?: boolean;
    tools?: boolean;
    vision?: boolean;
    chat_completion?: boolean;
  };
  loaded_instances?: unknown[];
}

export class WorkstationClient {
  constructor(
    private readonly secrets: vscode.SecretStorage,
    private readonly output: vscode.OutputChannel
  ) {}

  get endpoint(): string {
    const configured = vscode.workspace.getConfiguration('jackllm').get<string>('endpoint', 'http://127.0.0.1:11436');
    return configured.trim().replace(/\/+$/, '');
  }

  async discoverModels(token?: vscode.CancellationToken): Promise<WorkstationModel[]> {
    const controller = this.createAbortController(token, 15_000);
    try {
      const [apiModels, runtimeModels, openAiModels] = await Promise.all([
        this.tryGetJson('/api/models', controller.signal),
        this.tryGetJson('/api/model-runtime/models', controller.signal),
        this.tryGetJson('/v1/models', controller.signal)
      ]);

      const merged = new Map<string, ModelMetadata>();
      for (const model of extractModels(openAiModels)) mergeModel(merged, model);
      for (const model of extractModels(runtimeModels)) mergeModel(merged, model);
      for (const model of extractModels(apiModels)) mergeModel(merged, model);

      const config = vscode.workspace.getConfiguration('jackllm');
      const defaultContext = config.get<number>('defaultContextTokens', 32768);
      const defaultOutput = config.get<number>('defaultOutputTokens', 8192);
      return [...merged.values()]
        .filter(model => model.id || model.key)
        .filter(model => model.capabilities?.chat_completion !== false)
        .filter(model => model.disabled !== true && model.enabled !== false && model.isEnabled !== false)
        .map(model => {
          const id = String(model.id ?? model.key);
          const contextTokens = positiveNumber(
            model.maxContextLength,
            model.contextLength,
            model.max_context_length,
            defaultContext
          );
          const outputTokens = Math.min(positiveNumber(model.maxOutputTokens, defaultOutput), Math.max(256, contextTokens - 1));
          return {
            id,
            name: model.displayName || model.display_name || id,
            contextTokens,
            outputTokens,
            supportsTools: model.supportsTools ?? model.capabilities?.tool_calling ?? model.capabilities?.tools ?? false,
            supportsImages: model.supportsImages ?? model.supportsVision ?? model.capabilities?.vision ?? false,
            loaded: model.isLoaded ?? Boolean(model.loaded_instances?.length),
            available: model.isAvailable ?? true,
            enabled: model.enabled ?? model.isEnabled ?? true,
            status: model.status || (model.isLoaded ? 'loaded' : 'available')
          };
        })
        .filter(model => model.available)
        .sort((a, b) => Number(b.loaded) - Number(a.loaded) || a.name.localeCompare(b.name));
    } finally {
      controller.dispose();
    }
  }

  async health(): Promise<JsonObject> {
    const controller = this.createAbortController(undefined, 10_000);
    try {
      const result = await this.getJson('/api/health', controller.signal);
      return asObject(result);
    } finally {
      controller.dispose();
    }
  }

  async streamChat(
    request: JsonObject,
    onText: (text: string) => void,
    onToolCall: (id: string, name: string, input: object) => void,
    token: vscode.CancellationToken
  ): Promise<void> {
    const timeoutSeconds = vscode.workspace.getConfiguration('jackllm').get<number>('requestTimeoutSeconds', 600);
    const controller = this.createAbortController(token, timeoutSeconds * 1000);
    const url = `${this.endpoint}/v1/chat/completions`;
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: await this.headers(),
        body: JSON.stringify({ ...request, stream: true }),
        signal: controller.signal
      });
      if (!response.ok) {
        const body = await response.text();
        throw new Error(formatHttpError(response.status, response.statusText, body));
      }

      const contentType = response.headers.get('content-type') ?? '';
      if (contentType.includes('text/event-stream') && response.body) {
        await consumeSse(response.body, onText, onToolCall, token);
      } else {
        consumeJson(await response.json() as OpenAiResponse, onText, onToolCall);
      }
    } catch (error) {
      if (token.isCancellationRequested) return;
      const message = error instanceof Error ? error.message : String(error);
      this.output.appendLine(`[chat] ${message}`);
      throw new Error(`JackLLM Workstation request failed at ${url}: ${message}`);
    } finally {
      controller.dispose();
    }
  }

  async setApiKey(value: string | undefined): Promise<void> {
    if (value) await this.secrets.store('jackllm.apiKey', value);
    else await this.secrets.delete('jackllm.apiKey');
  }

  async hasApiKey(): Promise<boolean> {
    return Boolean(await this.secrets.get('jackllm.apiKey'));
  }

  private async tryGetJson(path: string, signal: AbortSignal): Promise<unknown> {
    try {
      return await this.getJson(path, signal);
    } catch (error) {
      this.output.appendLine(`[discovery] ${path}: ${error instanceof Error ? error.message : String(error)}`);
      return undefined;
    }
  }

  private async getJson(path: string, signal: AbortSignal): Promise<unknown> {
    const response = await fetch(`${this.endpoint}${path}`, { headers: await this.headers(), signal });
    if (!response.ok) throw new Error(formatHttpError(response.status, response.statusText, await response.text()));
    return response.json();
  }

  private async headers(): Promise<Record<string, string>> {
    const headers: Record<string, string> = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' };
    const apiKey = await this.secrets.get('jackllm.apiKey');
    if (apiKey) headers.Authorization = `Bearer ${apiKey}`;
    return headers;
  }

  private createAbortController(token: vscode.CancellationToken | undefined, timeoutMs: number): AbortController & { dispose(): void } {
    const controller = new AbortController() as AbortController & { dispose(): void };
    const subscription = token?.onCancellationRequested(() => controller.abort());
    const timeout = setTimeout(() => controller.abort(new Error('Request timed out.')), timeoutMs);
    controller.dispose = () => {
      clearTimeout(timeout);
      subscription?.dispose();
    };
    return controller;
  }
}

function extractModels(payload: unknown): ModelMetadata[] {
  if (!payload || typeof payload !== 'object') return [];
  const object = payload as { models?: unknown; data?: unknown };
  const models = Array.isArray(object.models) ? object.models : Array.isArray(object.data) ? object.data : [];
  return models.filter(item => item && typeof item === 'object') as ModelMetadata[];
}

function mergeModel(target: Map<string, ModelMetadata>, incoming: ModelMetadata): void {
  const id = incoming.id ?? incoming.key;
  if (!id) return;
  const previous = target.get(id) ?? {};
  target.set(id, {
    ...previous,
    ...incoming,
    capabilities: { ...previous.capabilities, ...incoming.capabilities },
    id
  });
}

function positiveNumber(...values: Array<number | undefined>): number {
  return values.find(value => typeof value === 'number' && Number.isFinite(value) && value > 0) ?? 1;
}

function asObject(value: unknown): JsonObject {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {};
}

function formatHttpError(status: number, statusText: string, body: string): string {
  const clean = body.trim().replace(/\s+/g, ' ').slice(0, 500);
  return `HTTP ${status} ${statusText}${clean ? `: ${clean}` : ''}`;
}

async function consumeSse(
  stream: ReadableStream<Uint8Array>,
  onText: (text: string) => void,
  onToolCall: (id: string, name: string, input: object) => void,
  token: vscode.CancellationToken
): Promise<void> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let reasoningBuffer = '';
  let sawAssistantContent = false;
  const calls = new Map<number, { id: string; name: string; arguments: string }>();
  try {
    while (!token.isCancellationRequested) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n');
      let boundary: number;
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const event = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        for (const line of event.split('\n')) {
          if (!line.startsWith('data:')) continue;
          const data = line.slice(5).trimStart();
          if (!data || data === '[DONE]') continue;
          let parsed: OpenAiResponse;
          try { parsed = JSON.parse(data) as OpenAiResponse; } catch { continue; }
          if (parsed.error?.message) throw new Error(parsed.error.message);
          for (const choice of parsed.choices ?? []) {
            const delta = choice.delta ?? choice.message;
            if (!delta) continue;
            if (delta.content) {
              sawAssistantContent = true;
              reasoningBuffer = '';
              onText(delta.content);
            } else if (!sawAssistantContent) {
              reasoningBuffer += delta.reasoning_content ?? delta.reasoning ?? '';
            }
            accumulateToolCalls(calls, delta.tool_calls);
          }
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
  if (!sawAssistantContent && reasoningBuffer) onText(reasoningBuffer);
  emitToolCalls(calls, onToolCall);
}

function consumeJson(response: OpenAiResponse, onText: (text: string) => void, onToolCall: (id: string, name: string, input: object) => void): void {
  if (response.error?.message) throw new Error(response.error.message);
  const calls = new Map<number, { id: string; name: string; arguments: string }>();
  for (const choice of response.choices ?? []) {
    const message = choice.message ?? choice.delta;
    const text = message?.content ?? message?.reasoning_content ?? message?.reasoning;
    if (text) onText(text);
    accumulateToolCalls(calls, message?.tool_calls);
  }
  emitToolCalls(calls, onToolCall);
}

function accumulateToolCalls(target: Map<number, { id: string; name: string; arguments: string }>, chunks?: OpenAiToolCall[]): void {
  for (const chunk of chunks ?? []) {
    const index = chunk.index ?? target.size;
    const current = target.get(index) ?? { id: '', name: '', arguments: '' };
    if (chunk.id) current.id += chunk.id;
    if (chunk.function?.name) current.name += chunk.function.name;
    if (chunk.function?.arguments) current.arguments += chunk.function.arguments;
    target.set(index, current);
  }
}

function emitToolCalls(target: Map<number, { id: string; name: string; arguments: string }>, emit: (id: string, name: string, input: object) => void): void {
  for (const [index, call] of [...target.entries()].sort(([a], [b]) => a - b)) {
    if (!call.name) continue;
    let input: object = {};
    if (call.arguments.trim()) {
      try { input = JSON.parse(call.arguments) as object; }
      catch { input = { rawArguments: call.arguments }; }
    }
    emit(call.id || `jackllm-tool-${index}`, call.name, input);
  }
}
