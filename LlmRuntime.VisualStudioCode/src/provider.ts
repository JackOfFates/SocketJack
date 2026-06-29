import * as vscode from 'vscode';
import { WorkstationModel } from './types';
import { WorkstationClient } from './workstationClient';

type ProviderModel = vscode.LanguageModelChatInformation & { id: string };

export class JackLlmChatModelProvider implements vscode.LanguageModelChatProvider, vscode.Disposable {
  private readonly changeEmitter = new vscode.EventEmitter<void>();
  private cachedModels: WorkstationModel[] = [];

  readonly onDidChangeLanguageModelChatInformation = this.changeEmitter.event;

  constructor(private readonly client: WorkstationClient) {}

  async provideLanguageModelChatInformation(
    options: { silent: boolean },
    token: vscode.CancellationToken
  ): Promise<vscode.LanguageModelChatInformation[]> {
    try {
      const models = await this.client.discoverModels(token);
      if (models.length === 0) throw new Error('Workstation returned no chat-capable models.');
      this.cachedModels = models;
      return models.map(toProviderModel);
    } catch (error) {
      if (!options.silent) {
        void vscode.window.showWarningMessage(
          `JackLLM model discovery failed: ${error instanceof Error ? error.message : String(error)}`,
          'Open Settings'
        ).then(choice => choice && vscode.commands.executeCommand('workbench.action.openSettings', 'jackllm.endpoint'));
      }
      return this.cachedModels.map(toProviderModel);
    }
  }

  async provideLanguageModelChatResponse(
    model: ProviderModel,
    messages: readonly vscode.LanguageModelChatRequestMessage[],
    options: vscode.ProvideLanguageModelChatResponseOptions,
    progress: vscode.Progress<vscode.LanguageModelResponsePart>,
    token: vscode.CancellationToken
  ): Promise<void> {
    const known = this.cachedModels.find(item => item.id === model.id);
    const request: Record<string, unknown> = {
      model: model.id,
      messages: messages.flatMap(convertMessage),
      max_tokens: known?.outputTokens ?? vscode.workspace.getConfiguration('jackllm').get<number>('defaultOutputTokens', 8192)
    };

    const tools = convertTools(options.tools);
    if (tools.length > 0) {
      request.tools = tools;
      request.tool_choice = options.toolMode === vscode.LanguageModelChatToolMode.Required ? 'required' : 'auto';
    }

    await this.client.streamChat(
      request,
      text => progress.report(new vscode.LanguageModelTextPart(text)),
      (id, name, input) => progress.report(new vscode.LanguageModelToolCallPart(id, name, input)),
      token
    );
  }

  async provideTokenCount(
    _model: ProviderModel,
    text: string | vscode.LanguageModelChatRequestMessage,
    _token: vscode.CancellationToken
  ): Promise<number> {
    const value = typeof text === 'string' ? text : text.content.map(part => partToText(part)).join('');
    return Math.max(1, Math.ceil(value.length / 4));
  }

  refresh(): void {
    this.cachedModels = [];
    this.changeEmitter.fire();
  }

  dispose(): void {
    this.changeEmitter.dispose();
  }
}

function toProviderModel(model: WorkstationModel): vscode.LanguageModelChatInformation {
  const detail = [model.loaded ? 'loaded' : model.status, `${model.contextTokens.toLocaleString()} context`].filter(Boolean).join(' · ');
  return {
    id: model.id,
    name: model.name,
    family: inferFamily(model.id),
    version: 'local',
    tooltip: `JackLLM Workstation model ${model.id}`,
    detail,
    maxInputTokens: Math.max(1, model.contextTokens - model.outputTokens),
    maxOutputTokens: model.outputTokens,
    capabilities: {
      toolCalling: model.supportsTools,
      imageInput: model.supportsImages
    }
  };
}

function inferFamily(id: string): string {
  const normalized = id.toLowerCase();
  for (const family of ['qwen', 'llama', 'mistral', 'gemma', 'phi', 'deepseek', 'command-r']) {
    if (normalized.includes(family)) return family;
  }
  return 'jackllm';
}

function convertMessage(message: vscode.LanguageModelChatRequestMessage): Record<string, unknown>[] {
  const role = message.role === vscode.LanguageModelChatMessageRole.User ? 'user' : 'assistant';
  const text: string[] = [];
  const richContent: Array<Record<string, unknown>> = [];
  const toolCalls: Array<Record<string, unknown>> = [];
  const toolResults: Array<Record<string, unknown>> = [];

  for (const part of message.content) {
    if (part instanceof vscode.LanguageModelTextPart) {
      text.push(part.value);
      richContent.push({ type: 'text', text: part.value });
    } else if (part instanceof vscode.LanguageModelDataPart) {
      if (part.mimeType.startsWith('image/')) {
        richContent.push({
          type: 'image_url',
          image_url: { url: `data:${part.mimeType};base64,${Buffer.from(part.data).toString('base64')}` }
        });
      } else {
        const decoded = new TextDecoder().decode(part.data);
        text.push(decoded);
        richContent.push({ type: 'text', text: decoded });
      }
    } else if (part instanceof vscode.LanguageModelToolCallPart) {
      toolCalls.push({
        id: part.callId,
        type: 'function',
        function: { name: part.name, arguments: JSON.stringify(part.input) }
      });
    } else if (part instanceof vscode.LanguageModelToolResultPart) {
      toolResults.push({
        role: 'tool',
        tool_call_id: part.callId,
        content: part.content.map(item => partToText(item)).join('')
      });
    }
  }

  const result: Record<string, unknown>[] = [];
  if (text.length > 0 || toolCalls.length > 0 || toolResults.length === 0) {
    const hasImage = richContent.some(part => part.type === 'image_url');
    const converted: Record<string, unknown> = { role, content: hasImage ? richContent : text.join('') || null };
    if (message.name) converted.name = message.name;
    if (toolCalls.length > 0) converted.tool_calls = toolCalls;
    result.push(converted);
  }
  result.push(...toolResults);
  return result;
}

function convertTools(tools: readonly vscode.LanguageModelChatTool[] | undefined): Array<Record<string, unknown>> {
  return (tools ?? []).map(tool => ({
    type: 'function',
    function: {
      name: tool.name,
      description: tool.description,
      parameters: tool.inputSchema ?? { type: 'object', properties: {} }
    }
  }));
}

function partToText(part: unknown): string {
  if (part instanceof vscode.LanguageModelTextPart) return part.value;
  if (part instanceof vscode.LanguageModelDataPart) {
    if (part.mimeType.startsWith('text/') || part.mimeType.includes('json')) return new TextDecoder().decode(part.data);
    return `[${part.mimeType} data: ${part.data.byteLength} bytes]`;
  }
  if (part && typeof part === 'object' && 'value' in part) {
    const value = (part as { value: unknown }).value;
    if (typeof value === 'string') return value;
    if (value instanceof Uint8Array) return Buffer.from(value).toString('base64');
  }
  try { return JSON.stringify(part); } catch { return String(part); }
}
