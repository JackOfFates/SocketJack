import * as path from 'node:path';
import * as vscode from 'vscode';

interface IgnoreRules {
  exact: string[];
  globs: string[];
  regex: string[];
}

export class SessionExplorer implements vscode.TreeDataProvider<SessionFileItem>, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<SessionFileItem | undefined>();
  private roots: SessionFileItem[] = [];
  private rules: IgnoreRules = { exact: [], globs: [], regex: [] };
  private view?: vscode.TreeView<SessionFileItem>;

  readonly onDidChangeTreeData = this.changed.event;

  constructor(private readonly context: vscode.ExtensionContext) {
    this.rules = this.context.workspaceState.get<IgnoreRules>('jackllm.sessionIgnoreRules', this.rules);
  }

  register(): vscode.Disposable[] {
    this.view = vscode.window.createTreeView('jackllm.sessionExplorer', {
      treeDataProvider: this,
      canSelectMany: true,
      showCollapseAll: true
    });
    const registrations = [
      this.view,
      vscode.commands.registerCommand('jackllm.session.refresh', () => this.refresh()),
      vscode.commands.registerCommand('jackllm.session.ignoreSelected', () => this.ignoreSelected()),
      vscode.commands.registerCommand('jackllm.session.unignoreSelected', () => this.unignoreSelected()),
      vscode.commands.registerCommand('jackllm.session.ignoreFileTypes', () => this.ignoreFileTypes()),
      vscode.commands.registerCommand('jackllm.session.ignoreRegex', () => this.ignoreRegex()),
      vscode.workspace.onDidCreateFiles(() => this.refresh()),
      vscode.workspace.onDidDeleteFiles(() => this.refresh()),
      vscode.workspace.onDidRenameFiles(() => this.refresh())
    ];
    void this.refresh();
    return registrations;
  }

  getTreeItem(element: SessionFileItem): vscode.TreeItem { return element; }
  getChildren(element?: SessionFileItem): SessionFileItem[] { return element ? element.children : this.roots; }

  async refresh(): Promise<void> {
    const folders = vscode.workspace.workspaceFolders ?? [];
    const rootMap = new Map<string, SessionFileItem>();
    for (const folder of folders) {
      const root = new SessionFileItem(folder.name, '', true, folder.uri, 0);
      rootMap.set(folder.uri.fsPath, root);
      const files = await vscode.workspace.findFiles(
        new vscode.RelativePattern(folder, '**/*'),
        '**/{.git,node_modules,bin,obj,dist}/**',
        10000
      );
      for (const uri of files) this.addFile(root, folder, uri);
      sortTree(root);
    }
    this.roots = [...rootMap.values()];
    this.changed.fire(undefined);
  }

  private addFile(root: SessionFileItem, folder: vscode.WorkspaceFolder, uri: vscode.Uri): void {
    const relative = normalize(path.relative(folder.uri.fsPath, uri.fsPath));
    const parts = relative.split('/').filter(Boolean);
    let parent = root;
    let current = '';
    for (let index = 0; index < parts.length; index++) {
      const segment = parts[index];
      if (!segment) continue;
      current = current ? `${current}/${segment}` : segment;
      const isDirectory = index < parts.length - 1;
      let item = parent.children.find(child => child.label === segment && child.isDirectory === isDirectory);
      if (!item) {
        item = new SessionFileItem(segment, current, isDirectory, isDirectory ? vscode.Uri.joinPath(folder.uri, ...parts.slice(0, index + 1)) : uri, index + 1);
        parent.children.push(item);
      }
      parent = item;
    }
    if (parent !== root) this.decorate(parent);
  }

  private decorate(item: SessionFileItem): void {
    const rule = this.matchingRule(item.relativePath);
    item.ignored = Boolean(rule);
    item.description = rule ? `Ignored (${rule})` : undefined;
    item.iconPath = new vscode.ThemeIcon(rule ? 'eye-closed' : item.isDirectory ? 'folder' : 'file');
    item.contextValue = rule ? 'jackllmSessionIgnoredItem' : 'jackllmSessionItem';
  }

  private selectedFiles(): SessionFileItem[] {
    return (this.view?.selection ?? []).filter(item => item.relativePath && !item.isDirectory);
  }

  private async ignoreSelected(): Promise<void> {
    const selected = this.selectedFiles();
    if (!selected.length) return void vscode.window.showInformationMessage('Select one or more files in JackLLM Session Explorer first.');
    this.rules.exact = unique([...this.rules.exact, ...selected.map(item => item.relativePath)]);
    await this.saveRules(`${selected.length} file${selected.length === 1 ? '' : 's'} ignored.`);
  }

  private async unignoreSelected(): Promise<void> {
    const selected = this.selectedFiles();
    if (!selected.length) return void vscode.window.showInformationMessage('Select one or more files in JackLLM Session Explorer first.');
    const paths = new Set(selected.map(item => item.relativePath.toLowerCase()));
    this.rules.exact = this.rules.exact.filter(value => !paths.has(value.toLowerCase()));
    await this.saveRules(`Exact ignore rules removed for ${selected.length} file${selected.length === 1 ? '' : 's'}.`);
  }

  private async ignoreFileTypes(): Promise<void> {
    const selectedExtensions = unique(this.selectedFiles().map(item => path.extname(item.relativePath).toLowerCase()).filter(Boolean));
    const picked = await vscode.window.showQuickPick(
      selectedExtensions.length ? selectedExtensions.map(ext => ({ label: `*${ext}`, ext })) : commonExtensions.map(ext => ({ label: `*${ext}`, ext })),
      { title: 'Ignore file types in this JackLLM session', canPickMany: true, placeHolder: 'Select one or more *.ext rules' }
    );
    if (!picked?.length) return;
    this.rules.globs = unique([...this.rules.globs, ...picked.map(item => `*${item.ext}`)]);
    await this.saveRules(`Ignored ${picked.map(item => `*${item.ext}`).join(', ')}.`);
  }

  private async ignoreRegex(): Promise<void> {
    const expression = await vscode.window.showInputBox({
      title: 'Ignore files matching regex',
      prompt: 'The regex is matched against each workspace-relative file path.',
      placeHolder: '(^|/)generated/|\\.min\\.js$',
      validateInput: value => { try { new RegExp(value, 'i'); return undefined; } catch (error) { return error instanceof Error ? error.message : String(error); } }
    });
    if (!expression) return;
    this.rules.regex = unique([...this.rules.regex, expression]);
    await this.saveRules(`Added regex ignore rule: ${expression}`);
  }

  private matchingRule(relativePath: string): string | undefined {
    if (this.rules.exact.some(value => value.toLowerCase() === relativePath.toLowerCase())) return 'exact';
    for (const glob of this.rules.globs) if (globMatches(glob, relativePath)) return glob;
    for (const expression of this.rules.regex) {
      try { if (new RegExp(expression, 'i').test(relativePath)) return `regex:${expression}`; } catch { /* preserve invalid hand-edited state */ }
    }
    return undefined;
  }

  private async saveRules(message: string): Promise<void> {
    await this.context.workspaceState.update('jackllm.sessionIgnoreRules', this.rules);
    await this.refresh();
    void vscode.window.showInformationMessage(message);
  }

  dispose(): void {
    this.changed.dispose();
  }
}

class SessionFileItem extends vscode.TreeItem {
  readonly children: SessionFileItem[] = [];
  ignored = false;

  constructor(
    public readonly label: string,
    public readonly relativePath: string,
    public readonly isDirectory: boolean,
    public readonly resourceUri: vscode.Uri,
    depth: number
  ) {
    super(label, isDirectory ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
    this.tooltip = relativePath || resourceUri.fsPath;
    this.iconPath = new vscode.ThemeIcon(isDirectory ? 'folder' : 'file');
    this.contextValue = 'jackllmSessionItem';
    this.command = isDirectory ? undefined : { command: 'vscode.open', title: 'Open File', arguments: [resourceUri] };
    this.id = `${resourceUri.toString()}#${depth}`;
  }
}

const commonExtensions = ['.cs', '.ts', '.js', '.json', '.md', '.log', '.tmp', '.map'];
const normalize = (value: string): string => value.replace(/\\/g, '/');
const unique = (values: string[]): string[] => [...new Set(values.map(value => value.trim()).filter(Boolean))];

function globMatches(glob: string, value: string): boolean {
  const source = '^' + glob.split('').map(char => char === '*' ? '.*' : char === '?' ? '.' : char.replace(/[\\^$+?.()|{}[\]]/g, '\\$&')).join('') + '$';
  return new RegExp(source, 'i').test(value);
}

function sortTree(item: SessionFileItem): void {
  item.children.sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.label.localeCompare(b.label));
  for (const child of item.children) sortTree(child);
}
