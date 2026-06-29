import * as vscode from 'vscode';
import { JackLlmChatModelProvider } from './provider';
import { WorkstationClient } from './workstationClient';
import { SessionExplorer } from './sessionExplorer';

export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel('JackLLM Workstation', { log: true });
  const client = new WorkstationClient(context.secrets, output);
  const provider = new JackLlmChatModelProvider(client);
  const sessionExplorer = new SessionExplorer(context);

  context.subscriptions.push(
    output,
    provider,
    sessionExplorer,
    ...sessionExplorer.register(),
    vscode.lm.registerLanguageModelChatProvider('jackllm', provider),
    vscode.commands.registerCommand('jackllm.refreshModels', () => {
      provider.refresh();
      void vscode.window.showInformationMessage('JackLLM model list refreshed.');
    }),
    vscode.commands.registerCommand('jackllm.setApiKey', async () => {
      const existing = await client.hasApiKey();
      const value = await vscode.window.showInputBox({
        title: 'JackLLM Workstation API key',
        prompt: existing ? 'Enter a replacement key, or leave blank to remove the stored key.' : 'Enter the bearer token used by this Workstation endpoint.',
        password: true,
        ignoreFocusOut: true,
        placeHolder: existing ? 'A key is currently stored in VS Code SecretStorage' : 'Optional for the default local Workstation'
      });
      if (value === undefined) return;
      await client.setApiKey(value.trim() || undefined);
      provider.refresh();
      void vscode.window.showInformationMessage(value.trim() ? 'JackLLM API key stored securely.' : 'JackLLM API key removed.');
    }),
    vscode.commands.registerCommand('jackllm.openWorkstation', async () => {
      await vscode.env.openExternal(vscode.Uri.parse(client.endpoint));
    }),
    vscode.commands.registerCommand('jackllm.showStatus', async () => showStatus(client)),
    vscode.commands.registerCommand('jackllm.manage', async () => manageProvider(client, provider, output)),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('jackllm')) provider.refresh();
    })
  );

  output.info(`Provider activated for ${client.endpoint}`);
}

async function manageProvider(client: WorkstationClient, provider: JackLlmChatModelProvider, output: vscode.OutputChannel): Promise<void> {
  const choice = await vscode.window.showQuickPick([
    { label: '$(pulse) Show connection status', action: 'status' },
    { label: '$(refresh) Refresh models', action: 'refresh' },
    { label: '$(settings-gear) Configure Workstation endpoint', action: 'endpoint', description: client.endpoint },
    { label: '$(key) Set API key', action: 'key' },
    { label: '$(globe) Open Workstation Web UI', action: 'open' },
    { label: '$(output) Show JackLLM logs', action: 'logs' }
  ], { title: 'Manage JackLLM Workstation', placeHolder: 'Choose an action' });
  if (!choice) return;
  switch (choice.action) {
    case 'status': await showStatus(client); break;
    case 'refresh': provider.refresh(); break;
    case 'endpoint': await vscode.commands.executeCommand('workbench.action.openSettings', 'jackllm.endpoint'); break;
    case 'key': await vscode.commands.executeCommand('jackllm.setApiKey'); break;
    case 'open': await vscode.commands.executeCommand('jackllm.openWorkstation'); break;
    case 'logs': output.show(true); break;
  }
}

async function showStatus(client: WorkstationClient): Promise<void> {
  await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Checking JackLLM Workstation…' }, async () => {
    try {
      const [health, models] = await Promise.all([client.health(), client.discoverModels()]);
      const state = health.ok === true ? 'online' : 'responding';
      void vscode.window.showInformationMessage(`JackLLM Workstation is ${state} at ${client.endpoint} with ${models.length} chat model${models.length === 1 ? '' : 's'}.`);
    } catch (error) {
      void vscode.window.showErrorMessage(`JackLLM Workstation is unavailable at ${client.endpoint}: ${error instanceof Error ? error.message : String(error)}`, 'Open Settings')
        .then(choice => choice && vscode.commands.executeCommand('workbench.action.openSettings', 'jackllm.endpoint'));
    }
  });
}

export function deactivate(): void {}
