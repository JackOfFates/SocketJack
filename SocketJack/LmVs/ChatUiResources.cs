namespace LmVs
{
    internal static class ChatUiResources
    {
        public const string FtpConfigurationPageHtml = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>FTP Configuration</title>
<style>
:root { color-scheme: dark; --line: rgba(255,255,255,0.18); --text: #f8fbff; --muted: rgba(255,255,255,0.68); --accent: #40c9a2; --accent2: #7dd3fc; --danger: #fb7185; }
* { box-sizing: border-box; }
html, body { margin: 0; min-height: 100%; background: transparent; color: var(--text); font-family: 'Segoe UI', 'Cascadia Code', sans-serif; }
body { padding: 10px; }
.panel { border: 1px solid var(--line); border-radius: 14px; background: rgba(0,0,0,0.30); backdrop-filter: blur(18px) saturate(1.18); box-shadow: inset 0 1px 0 rgba(255,255,255,0.10); padding: 10px; }
.head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
h1 { margin: 0; font: 900 0.94rem 'Cascadia Code', monospace; letter-spacing: 0; }
.status-tools { display: flex; align-items: center; gap: 6px; min-width: 0; }
.status { border: 1px solid rgba(125,211,252,0.26); border-radius: 999px; padding: 5px 8px; color: #d8f3ff; background: rgba(125,211,252,0.08); font: 0.68rem 'Cascadia Code', monospace; white-space: nowrap; }
.grid { display: grid; gap: 8px; }
label { display: grid; gap: 4px; color: rgba(255,255,255,0.78); font: 0.7rem 'Cascadia Code', monospace; }
input, select { width: 100%; min-height: 34px; border: 1px solid rgba(255,255,255,0.18); border-radius: 9px; color: #fff; background: rgba(2,6,23,0.54); padding: 7px 8px; font: 0.78rem 'Cascadia Code', monospace; }
select option { color: #0f172a; }
.two { display: grid; grid-template-columns: 84px minmax(0,1fr); gap: 8px; }
.check { display: flex; align-items: center; gap: 8px; }
.check input { width: 16px; min-height: 16px; accent-color: var(--accent); }
.accounts { display: grid; gap: 8px; }
.account-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; color: rgba(255,255,255,0.78); font: 0.7rem 'Cascadia Code', monospace; }
.account-list { display: grid; gap: 8px; max-height: 190px; overflow: auto; }
.account-row { display: grid; grid-template-columns: minmax(0,1fr) minmax(0,1fr) minmax(0,1.25fr) 86px 34px; gap: 6px; align-items: end; padding: 8px; border: 1px solid rgba(255,255,255,0.12); border-radius: 10px; background: rgba(255,255,255,0.05); }
.account-row label { font-size: 0.62rem; }
.account-row .check { justify-content: center; padding-bottom: 7px; }
.actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 2px; }
button { min-height: 34px; border: 1px solid rgba(255,255,255,0.22); border-radius: 9px; color: #fff; background: rgba(255,255,255,0.08); padding: 7px 10px; font: 800 0.74rem 'Cascadia Code', monospace; }
button.icon { width: 34px; min-width: 34px; padding: 0; display: inline-grid; place-items: center; font: 900 1.02rem 'Segoe UI Symbol', 'Cascadia Code', monospace; line-height: 1; }
button.primary { color: #061015; background: linear-gradient(135deg, var(--accent), var(--accent2)); border-color: rgba(255,255,255,0.24); }
button.danger { color: #fff; background: rgba(251,113,133,0.20); border-color: rgba(251,113,133,0.42); }
button:disabled, input:disabled, select:disabled { opacity: 0.56; cursor: not-allowed; }
.note { margin-top: 8px; color: var(--muted); font: 0.68rem/1.35 'Cascadia Code', monospace; white-space: pre-wrap; }
.error { color: #fecdd3; }
</style>
</head>
<body>
<section class='panel'>
    <div class='head'>
        <h1>FTP Configuration</h1>
        <div class='status-tools'>
            <div id='status' class='status'>Loading</div>
            <button id='copyUrl' class='icon' type='button' title='Copy FTP login URL' aria-label='Copy FTP login URL' disabled>&#x29C9;</button>
        </div>
    </div>
    <div class='grid'>
        <label>Shared folder
            <select id='rootPath'></select>
        </label>
        <div class='two'>
            <label>Port
                <input id='port' type='number' min='1' max='65535' step='1'>
            </label>
            <label>User
                <input id='userName' type='text' spellcheck='false'>
            </label>
        </div>
        <label>Password
            <input id='password' type='text' spellcheck='false'>
        </label>
        <label class='check'><input id='allowWrite' type='checkbox'>Allow write/delete</label>
        <div class='accounts'>
            <div class='account-head'>
                <span>Additional user accounts</span>
                <button id='addAccount' class='icon' type='button' title='Add FTP user account' aria-label='Add FTP user account'>+</button>
            </div>
            <div id='accountRows' class='account-list'></div>
        </div>
        <label class='check'><input id='autoStart' type='checkbox'>Autostart FTP with saved details</label>
        <div class='actions'>
            <button id='save' type='button'>Save</button>
            <button id='start' class='primary' type='button'>Start FTP</button>
            <button id='stop' class='danger' type='button'>Stop</button>
            <button id='reload' type='button'>Reload</button>
        </div>
    </div>
    <div id='note' class='note'></div>
</section>
<script>
const rootPath = document.getElementById('rootPath');
const port = document.getElementById('port');
const userName = document.getElementById('userName');
const password = document.getElementById('password');
const allowWrite = document.getElementById('allowWrite');
const addAccount = document.getElementById('addAccount');
const accountRows = document.getElementById('accountRows');
const autoStart = document.getElementById('autoStart');
const save = document.getElementById('save');
const start = document.getElementById('start');
const stop = document.getElementById('stop');
const reload = document.getElementById('reload');
const copyUrl = document.getElementById('copyUrl');
const statusEl = document.getElementById('status');
const note = document.getElementById('note');
let canEdit = false;
let permissionEnabled = false;
let directories = [];
let extraAccounts = [];

async function readJson(response) {
    const text = await response.text();
    return text && text.trim() ? JSON.parse(text) : {};
}

function setNote(text, isError) {
    note.textContent = text || '';
    note.classList.toggle('error', !!isError);
}

function formPayload(action) {
    const users = [{
        rootPath: rootPath.value,
        userName: userName.value,
        password: password.value,
        allowWrite: !!allowWrite.checked
    }].concat(readExtraAccounts());
    return {
        action,
        rootPath: rootPath.value,
        port: parseInt(port.value, 10) || 2121,
        userName: userName.value,
        password: password.value,
        allowWrite: !!allowWrite.checked,
        autoStart: !!autoStart.checked,
        users
    };
}

function buildFtpLoginUrl() {
    const selectedPort = parseInt(port.value, 10) || 2121;
    const encodedUser = encodeURIComponent(userName.value || 'lmvsproxy');
    const encodedPassword = encodeURIComponent(password.value || '');
    return 'ftp://' + encodedUser + ':' + encodedPassword + '@localhost:' + selectedPort + '/';
}

function copyTextToClipboard(text) {
    if (navigator.clipboard && window.isSecureContext)
        return navigator.clipboard.writeText(text);

    return new Promise((resolve, reject) => {
        const area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.left = '-9999px';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        try {
            if (document.execCommand('copy')) resolve();
            else reject(new Error('Copy command failed.'));
        } catch (error) {
            reject(error);
        } finally {
            area.remove();
        }
    });
}

async function copyFtpLoginUrl() {
    const url = buildFtpLoginUrl();
    await copyTextToClipboard(url);
    setNote('Copied FTP login URL:\n' + url, false);
}

function applyDisabled() {
    const disabled = !canEdit || !permissionEnabled;
    [rootPath, port, userName, password, allowWrite, autoStart, save, start, addAccount].forEach(el => {
        if (el) el.disabled = disabled;
    });
    Array.from(document.querySelectorAll('.account-row input, .account-row select, .account-row button')).forEach(el => {
        el.disabled = disabled;
    });
    stop.disabled = !canEdit;
    if (copyUrl)
        copyUrl.disabled = !permissionEnabled || !userName.value || !password.value || !(parseInt(port.value, 10) > 0);
}

function directoryOptions(selectedPath) {
    if (!directories.length)
        return ""<option value=''>"" + escapeHtml('No accessible directories') + ""</option>"";
    return directories.map(entry => {
        const value = entry.path || '';
        const selected = value === selectedPath ? ' selected' : '';
        return ""<option value='"" + escapeHtml(value) + ""'"" + selected + "">"" + escapeHtml(value) + ""</option>"";
    }).join('');
}

function escapeHtml(value) {
    return String(value || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/""/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function readExtraAccounts() {
    return Array.from(document.querySelectorAll('.account-row')).map(row => ({
        userName: row.querySelector('[data-account-field=userName]')?.value || '',
        password: row.querySelector('[data-account-field=password]')?.value || '',
        rootPath: row.querySelector('[data-account-field=rootPath]')?.value || '',
        allowWrite: !!row.querySelector('[data-account-field=allowWrite]')?.checked
    })).filter(user => user.userName.trim() || user.password.trim() || user.rootPath.trim());
}

function renderExtraAccounts(users) {
    if (!accountRows) return;
    accountRows.textContent = '';
    extraAccounts = Array.isArray(users) ? users.slice(1) : [];
    extraAccounts.forEach(user => addAccountRow(user));
    applyDisabled();
}

function addAccountRow(user) {
    if (!accountRows) return;
    const row = document.createElement('div');
    row.className = 'account-row';
    row.innerHTML =
        ""<label>User<input data-account-field='userName' type='text' spellcheck='false' value='"" + escapeHtml(user && user.userName || '') + ""'></label>"" +
        ""<label>Password<input data-account-field='password' type='text' spellcheck='false' value='"" + escapeHtml(user && user.password || '') + ""'></label>"" +
        ""<label>Folder<select data-account-field='rootPath'>"" + directoryOptions(user && user.rootPath || '') + ""</select></label>"" +
        ""<label class='check'><input data-account-field='allowWrite' type='checkbox'"" + (!user || user.allowWrite !== false ? ' checked' : '') + "">Write</label>"" +
        ""<button class='icon danger' type='button' title='Remove FTP user account' aria-label='Remove FTP user account'>&times;</button>"";
    row.querySelector('button').addEventListener('click', () => row.remove());
    accountRows.appendChild(row);
}

function render(data) {
    canEdit = !!data.canEdit;
    permissionEnabled = !!data.permissionEnabled;
    const config = data.config || {};
    const status = data.status || {};
    directories = Array.isArray(data.directories) ? data.directories.filter(d => d && d.exists && d.path) : [];

    rootPath.textContent = '';
    if (!directories.length) {
        const option = document.createElement('option');
        option.value = '';
        option.textContent = 'No accessible directories';
        rootPath.appendChild(option);
    } else {
        directories.forEach(entry => {
            const option = document.createElement('option');
            option.value = entry.path;
            option.textContent = entry.path;
            rootPath.appendChild(option);
        });
    }

    rootPath.value = config.rootPath || (directories[0] && directories[0].path) || '';
    port.value = config.port || 2121;
    userName.value = config.userName || 'lmvsproxy';
    password.value = config.password || '';
    allowWrite.checked = config.allowWrite !== false;
    autoStart.checked = !!config.autoStart;
    renderExtraAccounts(Array.isArray(config.users) && config.users.length ? config.users : [config]);

    if (status.running) {
        statusEl.textContent = 'Running ftp://localhost:' + status.port;
        setNote('Serving: ' + (status.rootPath || rootPath.value) + '\nUser: ' + (status.userName || userName.value), false);
    } else if (!permissionEnabled) {
        statusEl.textContent = 'Disabled';
        setNote('Enable Ftp Server from the Permissions dropdown first.', false);
    } else if (!directories.length) {
        statusEl.textContent = 'No folder';
        setNote('Add an accessible directory in the Filesystem permissions list before starting FTP.', false);
    } else {
        statusEl.textContent = 'Stopped';
        setNote(canEdit ? 'Select a folder from the accessible directories list, then start FTP. Auto start runs the saved config when LmVsProxy starts.' : 'Read-only. Change from localhost or 127.0.0.1.', false);
    }

    applyDisabled();
}

async function load() {
    const response = await fetch('/api/ftp-config');
    if (!response.ok) throw new Error('HTTP ' + response.status);
    const data = await readJson(response);
    if (!data.ok) throw new Error(data.error || 'FTP config load failed.');
    render(data);
}

async function mutate(action) {
    setNote('Saving...', false);
    const response = await fetch('/api/ftp-config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(formPayload(action))
    });
    if (!response.ok) throw new Error('HTTP ' + response.status);
    const data = await readJson(response);
    if (!data.ok) throw new Error(data.error || 'FTP config update failed.');
    render(data);
}

save.addEventListener('click', () => mutate('save').catch(error => setNote(error.message || String(error), true)));
start.addEventListener('click', () => mutate('start').catch(error => setNote(error.message || String(error), true)));
stop.addEventListener('click', () => mutate('stop').catch(error => setNote(error.message || String(error), true)));
reload.addEventListener('click', () => load().catch(error => setNote(error.message || String(error), true)));
copyUrl.addEventListener('click', () => copyFtpLoginUrl().catch(error => setNote(error.message || String(error), true)));
[port, userName, password].forEach(el => el.addEventListener('input', applyDisabled));
if (addAccount) addAccount.addEventListener('click', () => addAccountRow({ userName: '', password: '', rootPath: (directories[0] && directories[0].path) || '', allowWrite: true }));
load().catch(error => setNote(error.message || String(error), true));
</script>
</body>
</html>";
    }
}
