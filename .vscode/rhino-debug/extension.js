const { spawn } = require('node:child_process');
const { existsSync } = require('node:fs');
const vscode = require('vscode');

function nodeBin() {
  for (const candidate of ['/usr/local/bin/node', '/opt/homebrew/bin/node']) {
    if (existsSync(candidate)) return candidate;
  }
  return 'node';
}

function run(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd });
    let text = '';
    child.stdout.on('data', (chunk) => { text += chunk; });
    child.stderr.on('data', (chunk) => { text += chunk; });
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve(text.trim());
      else reject(new Error(text.trim() || `${command} exited with code ${code}`));
    });
  });
}

async function activate(context) {
  context.subscriptions.push(vscode.commands.registerCommand('rhinoClient.launch', async () => {
    const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!folder) throw new Error('Open the RhinoClient folder before debugging.');
    const node = nodeBin();
    await run(node, ['scripts/build.mjs'], folder);
    const pid = Number(await run(node, ['scripts/launch.mjs', '--print-pid'], folder));
    if (!pid) throw new Error('Rhino did not start.');
    return String(pid);
  }));
}

exports.activate = activate;
